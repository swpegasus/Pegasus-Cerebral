namespace Cerebral.Contracts.Workflows;

/// <summary>Bounded desktop navigation data; full messages and harness state stay in the service.</summary>
public sealed record ConversationSummary(string Id, string Title, string ProfileId, string? ProjectId,
    string LatestRunId, WorkflowStatus Status, DateTimeOffset UpdatedAt)
{
    public long TitleRevision { get; init; }
}

public sealed record ConversationRecentPage(IReadOnlyList<ConversationSummary> Conversations,
    long SnapshotRow, long? NextBeforeRow);
