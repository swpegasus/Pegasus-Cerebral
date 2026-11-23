using System.Text.Json;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

public sealed partial class StateStore
{
    private void InitializePromptCacheDiagnostics(bool prune) => WithConnection(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS prompt_cache_diagnostics(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recorded_at INTEGER NOT NULL,
                model_id TEXT NOT NULL,
                worker_id TEXT NOT NULL,
                adapter_id TEXT NOT NULL,
                body TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS prompt_cache_diagnostics_age ON prompt_cache_diagnostics(recorded_at,id);
            CREATE INDEX IF NOT EXISTS prompt_cache_diagnostics_lane ON prompt_cache_diagnostics(model_id,worker_id,adapter_id,id DESC);
            """;
        command.ExecuteNonQuery();
        return prune ? TrimPromptCacheDiagnostics(connection, DateTimeOffset.UtcNow) : 0;
    });

    internal bool RecordPromptCacheDiagnostic(PromptCacheDiagnosticEntry entry, int threshold) => Locked(() =>
        WithConnection(connection =>
        {
            ValidatePromptCacheDiagnostic(entry);
            if (threshold is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(threshold));
            TrimPromptCacheDiagnostics(connection, entry.At);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT body FROM prompt_cache_diagnostics
                WHERE model_id=$model AND worker_id=$worker AND adapter_id=$adapter
                ORDER BY id DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("$model", entry.ModelId);
            command.Parameters.AddWithValue("$worker", entry.WorkerId);
            command.Parameters.AddWithValue("$adapter", entry.AdapterId);
            if (command.ExecuteScalar() is string previous &&
                JsonSerializer.Deserialize<PromptCacheDiagnosticEntry>(previous, CerebralJson.Options) is { } baseline &&
                !MeaningfulCacheChange(baseline, entry, threshold)) return false;
            command.CommandText = """
                INSERT INTO prompt_cache_diagnostics(recorded_at,model_id,worker_id,adapter_id,body)
                VALUES($at,$model,$worker,$adapter,$body)
                """;
            command.Parameters.AddWithValue("$at", entry.At.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(entry, CerebralJson.Options));
            command.ExecuteNonQuery();
            TrimPromptCacheDiagnostics(connection, entry.At);
            return true;
        }));

    internal IReadOnlyList<PromptCacheDiagnosticEntry> ReadPromptCacheDiagnostics(int limit = 100,
        DateTimeOffset? at = null) => Locked(() => WithConnection(connection =>
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        TrimPromptCacheDiagnostics(connection, at ?? DateTimeOffset.UtcNow);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM prompt_cache_diagnostics ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<PromptCacheDiagnosticEntry>();
        while (reader.Read())
            if (JsonSerializer.Deserialize<PromptCacheDiagnosticEntry>(reader.GetString(0), CerebralJson.Options) is { } entry)
                result.Add(entry);
        return result;
    }));

    internal long TrimPromptCacheDiagnostics(DateTimeOffset? at = null) => Locked(() =>
        WithConnection(connection => TrimPromptCacheDiagnostics(connection, at ?? DateTimeOffset.UtcNow)));

    private long TrimPromptCacheDiagnostics(SqliteConnection connection, DateTimeOffset at)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Fixed retention applies even when collection is disabled. Clock rollback may discard
        // future diagnostic observations, but can never mutate durable chat/workflow evidence.
        command.CommandText = """
            DELETE FROM prompt_cache_diagnostics WHERE recorded_at <= $cutoff OR recorded_at > $now;
            """;
        command.Parameters.AddWithValue("$cutoff", at.AddHours(-PromptCacheDiagnostics.RetentionHours).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", at.ToUnixTimeMilliseconds());
        long removed = command.ExecuteNonQuery();
        command.CommandText = """
            DELETE FROM prompt_cache_diagnostics WHERE id IN (
                SELECT id FROM prompt_cache_diagnostics ORDER BY id DESC LIMIT -1 OFFSET $maximum
            )
            """;
        command.Parameters.AddWithValue("$maximum", PromptCacheDiagnostics.MaximumEntries);
        return removed + command.ExecuteNonQuery();
    }

    private static bool MeaningfulCacheChange(PromptCacheDiagnosticEntry previous, PromptCacheDiagnosticEntry next, int threshold)
    {
        if (previous.State != next.State || previous.Diagnostic != next.Diagnostic ||
            previous.CachedTokens.HasValue != next.CachedTokens.HasValue ||
            previous.ProcessedPromptTokens.HasValue != next.ProcessedPromptTokens.HasValue ||
            previous.PromptProcessingMilliseconds.HasValue != next.PromptProcessingMilliseconds.HasValue) return true;
        if (previous.ReuseRatio is { } before && next.ReuseRatio is { } after &&
            Math.Abs(after - before) * 100 + 1e-9 >= threshold) return true;
        // Changes accumulate against the last retained observation, not the latest suppressed sample.
        // Absolute floors avoid a history entry for token noise or sub-millisecond timing jitter.
        return RelativeChange(previous.PreparedInputTokens, next.PreparedInputTokens, threshold, 128) ||
            RelativeChange(previous.ProcessedPromptTokens, next.ProcessedPromptTokens, threshold, 128) ||
            RelativeChange(previous.PromptProcessingMilliseconds, next.PromptProcessingMilliseconds, threshold, 25);
    }

    private static bool RelativeChange(double? previous, double? next, int threshold, double floor) =>
        previous is { } before && next is { } after &&
        Math.Abs(after - before) + 1e-9 >= Math.Max(floor, Math.Abs(before) * threshold / 100d);

    private static void ValidatePromptCacheDiagnostic(PromptCacheDiagnosticEntry entry)
    {
        foreach (var identity in new[] { entry.ModelId, entry.WorkerId, entry.AdapterId })
            if (string.IsNullOrWhiteSpace(identity) || identity.Length > 256 || identity.Any(char.IsControl))
                throw new ArgumentException("Prompt-cache diagnostics require bounded model, worker and adapter identities.");
        if (entry.PreparedInputTokens < 0 || entry.CachedTokens is < 0 || entry.ProcessedPromptTokens is < 0 ||
            entry.CachedTokens > entry.PreparedInputTokens || entry.ProcessedPromptTokens > entry.PreparedInputTokens ||
            entry.CachedTokens is { } cached && entry.ProcessedPromptTokens is { } processed &&
                (long)cached + processed != entry.PreparedInputTokens ||
            entry.PromptProcessingMilliseconds is { } ms && (!double.IsFinite(ms) || ms < 0) ||
            entry.State is not ("reported" or "unavailable") ||
            entry.Diagnostic is not (null or "not-reported" or "invalid-provider-telemetry") ||
            entry.State == "reported" && (entry.Diagnostic is not null ||
                entry.CachedTokens is null && entry.ProcessedPromptTokens is null && entry.PromptProcessingMilliseconds is null) ||
            entry.State == "unavailable" && entry.Diagnostic is null ||
            entry.State == "unavailable" && (entry.CachedTokens is not null || entry.ProcessedPromptTokens is not null || entry.PromptProcessingMilliseconds is not null))
            throw new ArgumentException("Prompt-cache diagnostic numeric fields or availability are invalid.");
    }
}
