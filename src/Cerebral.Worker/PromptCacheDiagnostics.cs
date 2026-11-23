using Cerebral.Contracts.Models;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

/// <summary>Optional observations; never part of workflow evidence or model context.</summary>
internal static class PromptCacheDiagnostics
{
    internal const int RetentionHours = 24;
    internal const int MaximumEntries = 2048;

    internal static bool Capture(StateStore store, Configuration configuration, string workerId,
        PreparedModelMetadata prepared, ModelGenerationResult generation, DateTimeOffset? at = null)
    {
        // Diagnostic storage is optional. A storage failure cannot turn a confirmed model result
        // into an uncertain inference and cause the completed inference to be repeated.
        try
        {
            return store.Locked(() =>
            {
                var settings = configuration.Get();
                var now = at ?? DateTimeOffset.UtcNow;
                if (!settings.PromptCacheDiagnosticsEnabled) { store.TrimPromptCacheDiagnostics(now); return false; }
                var metrics = LlamaProtocol.ValidatePromptCache(generation.PromptCache, prepared.InputTokens);
                var invalid = metrics?.Diagnostic is not null;
                if (invalid) metrics = null;
                var reported = metrics is not null && (metrics.CachedTokens is not null ||
                    metrics.ProcessedPromptTokens is not null || metrics.PromptProcessingMilliseconds is not null);
                var entry = new PromptCacheDiagnosticEntry(now, prepared.ModelId, workerId, prepared.AdapterId,
                    prepared.InputTokens, reported ? metrics!.CachedTokens : null,
                    reported ? metrics!.ProcessedPromptTokens : null,
                    reported ? metrics!.PromptProcessingMilliseconds : null,
                    reported ? "reported" : "unavailable", invalid ? "invalid-provider-telemetry" : reported ? null : "not-reported");
                return store.RecordPromptCacheDiagnostic(entry, settings.PromptCacheDiagnosticsChangePercentagePoints);
            });
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return false; }
    }

    internal static PromptCacheDiagnosticsSnapshot Read(StateStore store, Configuration configuration,
        int limit = 100, DateTimeOffset? at = null) => store.Locked(() =>
    {
        var settings = configuration.Get();
        return new PromptCacheDiagnosticsSnapshot(settings.PromptCacheDiagnosticsEnabled, RetentionHours, MaximumEntries,
            settings.PromptCacheDiagnosticsChangePercentagePoints, store.ReadPromptCacheDiagnostics(limit, at));
    });
}
