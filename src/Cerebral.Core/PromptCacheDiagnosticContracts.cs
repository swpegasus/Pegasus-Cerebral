namespace Cerebral.Core;

/// <summary>Disposable numeric diagnostics only. These rows never contain a prompt, output, or credential.</summary>
public sealed record PromptCacheDiagnosticEntry(DateTimeOffset At, string ModelId, string WorkerId,
    string AdapterId, int PreparedInputTokens, int? CachedTokens, int? ProcessedPromptTokens,
    double? PromptProcessingMilliseconds, string State, string? Diagnostic)
{
    public double? ReuseRatio => CachedTokens is { } cached && PreparedInputTokens > 0
        ? (double)cached / PreparedInputTokens : null;
}

public sealed record PromptCacheDiagnosticsSnapshot(bool Enabled, int RetentionHours, int MaximumEntries,
    int ReuseChangePercentagePoints, IReadOnlyList<PromptCacheDiagnosticEntry> Entries);
