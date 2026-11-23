using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class StateStore
{
    public void InsertActivity(ActivityEntry entry) => Locked(() =>
    {
        entry.Sequence = checked(Get<long>("activity-sequence", "next") + 1);
        Put("activity-sequence", "next", entry.Sequence);
        Put("activity", entry.Id, entry);
        return true;
    });

    /// <summary>Enumerates only conversation identities that own activity rows, in stable bounded pages.</summary>
    public IReadOnlyList<string> ReadActivitySessionIds(string? afterSessionId = null, int limit = 64,
        CancellationToken cancellationToken = default) => WithConnection(connection =>
    {
        if (afterSessionId is { Length: > 128 } || afterSessionId?.Any(char.IsControl) == true)
            throw new ArgumentException("Invalid activity session cursor.", nameof(afterSessionId));
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            WITH activity_sessions AS (
                SELECT DISTINCT json_extract(body,'$.sessionId') AS session_id
                FROM records
                WHERE kind='activity' AND typeof(json_extract(body,'$.sessionId'))='text'
                  AND length(json_extract(body,'$.sessionId')) BETWEEN 1 AND 128
            )
            SELECT session_id
            FROM activity_sessions
            WHERE $after IS NULL OR session_id > $after COLLATE BINARY
            ORDER BY session_id COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", afterSessionId is null ? DBNull.Value : afterSessionId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<string>(limit);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(reader.GetString(0));
        }
        return result.AsReadOnly();
    });

    public ActivityPage ReadActivity(string sessionId, int turnSequence, long afterSequence, int limit) => WithConnection(connection =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT body FROM records WHERE kind='activity'
            AND json_extract(body,'$.sessionId')=$session AND json_extract(body,'$.turnSequence')=$turn
            AND json_extract(body,'$.sequence')>$after
            ORDER BY json_extract(body,'$.sequence') LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", sessionId); command.Parameters.AddWithValue("$turn", turnSequence);
        command.Parameters.AddWithValue("$after", afterSequence); command.Parameters.AddWithValue("$limit", limit + 1);
        var entries = new List<ActivityEntry>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) entries.Add(JsonSerializer.Deserialize<ActivityEntry>(reader.GetString(0), CerebralJson.Options)!);
        var more = entries.Count > limit; if (more) entries.RemoveAt(entries.Count - 1);
        command.CommandText = """
            SELECT COUNT(*) FROM records WHERE kind='activity'
            AND json_extract(body,'$.sessionId')=$session AND json_extract(body,'$.turnSequence')=$turn;
            """;
        return new ActivityPage { Entries = entries, NextAfterSequence = more ? entries[^1].Sequence : null,
            TotalCount = Convert.ToInt32(command.ExecuteScalar()), Retention = ReadActivityRetention(connection, sessionId) };
    });

    public List<ActivityTurnSummary> ReadActivityTurns(string sessionId) => WithConnection(connection =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT summary.turn,summary.total,json_remove(record.body,'$.arguments','$.result') FROM
            (SELECT json_extract(body,'$.turnSequence') AS turn,COUNT(*) AS total,
                    MAX(json_extract(body,'$.sequence')) AS latest
             FROM records WHERE kind='activity' AND json_extract(body,'$.sessionId')=$session
             GROUP BY json_extract(body,'$.turnSequence') ORDER BY turn DESC LIMIT 200) AS summary
            JOIN records AS record ON record.kind='activity'
                AND json_extract(record.body,'$.sessionId')=$session
                AND json_extract(record.body,'$.turnSequence')=summary.turn
                AND json_extract(record.body,'$.sequence')=summary.latest
            ORDER BY summary.turn DESC;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader(); var result = new List<ActivityTurnSummary>();
        while (reader.Read()) result.Add(new() { TurnSequence = reader.GetInt32(0), Count = reader.GetInt32(1),
            Latest = JsonSerializer.Deserialize<ActivityEntry>(reader.GetString(2), CerebralJson.Options)! });
        return result;
    });

    public int RecoverActivity() => WithConnection(connection =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            UPDATE records SET body=json_set(body,'$.state','uncertain','$.updatedAt',$now,
              '$.result','Worker restarted before a tool completion was recorded. Inspect actual effects before retrying.')
            WHERE kind='activity' AND json_extract(body,'$.state')='running';
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery();
    });

    public int TrimActivity(string sessionId, int maximumEntries, int maximumDays, bool trimByAge,
        CancellationToken cancellationToken = default, DateTimeOffset? now = null) => Locked(() => WithConnection(connection =>
    {
        if (string.IsNullOrWhiteSpace(sessionId) || maximumEntries is < 100 or > 50000 || maximumDays is < 1 or > 3650)
            throw new ArgumentException("Invalid activity retention settings.");
        cancellationToken.ThrowIfCancellationRequested();
        var at = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        using (var candidates = connection.CreateCommand())
        {
            candidates.Transaction = transaction;
            candidates.CommandText = """
                DROP TABLE IF EXISTS temp.activity_prune_candidates;
                CREATE TEMP TABLE activity_prune_candidates(
                    id TEXT PRIMARY KEY,
                    sequence INTEGER NOT NULL);
                WITH terminal AS (
                    SELECT id,json_extract(body,'$.sequence') AS sequence,
                           json_extract(body,'$.updatedAt') AS updated_at,
                           ROW_NUMBER() OVER (ORDER BY json_extract(body,'$.sequence') DESC) AS recent_rank
                    FROM records
                    WHERE kind='activity' AND json_extract(body,'$.sessionId')=$session
                      AND json_extract(body,'$.state')<>'running'
                )
                INSERT INTO activity_prune_candidates(id,sequence)
                SELECT id,sequence FROM terminal
                WHERE recent_rank>$maximum OR ($trim_age=1 AND julianday(updated_at)<julianday($cutoff))
                ORDER BY sequence LIMIT 50000;
                """;
            candidates.Parameters.AddWithValue("$session", sessionId);
            candidates.Parameters.AddWithValue("$maximum", maximumEntries);
            candidates.Parameters.AddWithValue("$trim_age", trimByAge ? 1 : 0);
            candidates.Parameters.AddWithValue("$cutoff", at.AddDays(-maximumDays).ToString("O"));
            candidates.ExecuteNonQuery();
        }

        long removed;
        long? first;
        long? last;
        using (var totals = connection.CreateCommand())
        {
            totals.Transaction = transaction;
            totals.CommandText = "SELECT COUNT(*),MIN(sequence),MAX(sequence) FROM activity_prune_candidates;";
            using var reader = totals.ExecuteReader(); reader.Read();
            removed = reader.GetInt64(0);
            first = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            last = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (removed > 0)
        {
            using (var remove = connection.CreateCommand())
            {
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM records WHERE kind='activity' AND id IN (SELECT id FROM activity_prune_candidates);";
                if (remove.ExecuteNonQuery() != removed) throw new InvalidOperationException("Activity cleanup changed unexpectedly.");
            }
            var retention = ReadActivityRetention(connection, sessionId) ?? new() { SessionId = sessionId };
            retention.PrunedEntries = checked(retention.PrunedEntries + removed);
            retention.FirstPrunedSequence = retention.FirstPrunedSequence == 0
                ? first!.Value : Math.Min(retention.FirstPrunedSequence, first!.Value);
            retention.LastPrunedSequence = Math.Max(retention.LastPrunedSequence, last!.Value);
            retention.LastPrunedAt = at;
            retention.MaximumEntries = maximumEntries;
            retention.MaximumDays = maximumDays;
            Put("activity-retention", sessionId, retention);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return checked((int)removed);
    }));

    private ActivityRetentionInfo? ReadActivityRetention(Microsoft.Data.Sqlite.SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT body FROM records WHERE kind='activity-retention' AND id=$session;";
        command.Parameters.AddWithValue("$session", sessionId);
        return command.ExecuteScalar() is string body
            ? JsonSerializer.Deserialize<ActivityRetentionInfo>(body, CerebralJson.Options)
            : null;
    }
}
