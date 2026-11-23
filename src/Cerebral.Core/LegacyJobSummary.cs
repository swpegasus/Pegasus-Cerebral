namespace Cerebral.Core;

/// <summary>Display-only projection; excludes backend prompts, profiles, results and execution evidence.</summary>
public sealed record LegacyJobSummary(string Id, string SessionId, string ProfileId,
    string Title, string Kind, string Status, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>Live active jobs in insertion order. Terminal jobs can disappear between pages.
/// UpdatedAt is absent when the legacy record does not retain that timestamp.</summary>
public sealed record LegacyJobSummaryPage(IReadOnlyList<LegacyJobSummary> Jobs, long? NextAfterRow);
