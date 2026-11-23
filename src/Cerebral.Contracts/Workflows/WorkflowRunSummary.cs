namespace Cerebral.Contracts.Workflows;

/// <summary>List projection without private assignment, settings, model context or effect payloads.</summary>
public sealed record WorkflowRunSummary(string Id, string ConversationId, string ProfileId,
    string Title, string DefinitionId, WorkflowStatus Status, long Revision, DateTimeOffset UpdatedAt);

/// <summary>Newest accepted runs first. The snapshot bounds insertions; individual statuses remain live.</summary>
public sealed record WorkflowRecentPage(IReadOnlyList<WorkflowRunSummary> Runs, long SnapshotRow, long? NextBeforeRow);

/// <summary>Bounded active display projection in insertion order; statuses remain live between pages.</summary>
public sealed record WorkflowActiveSummaryPage(IReadOnlyList<WorkflowRunSummary> Runs, long? NextAfterRow);
