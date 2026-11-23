using Cerebral.Contracts;

namespace Cerebral.Contracts.Workflows;

/// <summary>
/// A value-only receipt for the first root User message in a conversation.
/// Only the Engine can decide "first" inside the winning durable transaction;
/// earlier Service/system events do not disqualify the first root User message.
/// A duplicate, rejected or later root User submission produces no receipt.
/// </summary>
public sealed record CommittedRootUserInputReceipt(
    string RequestId,
    string ConversationId,
    string ProfileId,
    string DefinitionId,
    string DefinitionVersion,
    string? ProjectId,
    long ConversationSequenceBeforeMessage,
    string EventId,
    string EventTurnId,
    long EventSequence,
    string EventType,
    EventActorKind ActorKind,
    EventSourceKind SourceKind,
    string? ParentRunId,
    string UserText)
{
    /// <summary>
    /// Defense-in-depth at the Worker boundary. This checks the exact event binding,
    /// not whether an earlier root User event exists. Engine must establish that fact
    /// atomically before constructing the receipt.
    /// </summary>
    public bool HasCoherentRootUserEventBinding =>
        ParentRunId is null &&
        ActorKind == EventActorKind.User && SourceKind == EventSourceKind.User &&
        EventType == CerebralEventTypes.Message &&
        EventTurnId == RequestId && EventSequence > 0 &&
        EventSequence == ConversationSequenceBeforeMessage + 1 &&
        !string.IsNullOrEmpty(EventId) && !string.IsNullOrEmpty(RequestId) &&
        !string.IsNullOrEmpty(ConversationId);
}

/// <summary>
/// Result of WorkflowStore.SubmitWithCommittedRootUserInput. Receipt is null for an
/// existing RID, non-User/child submission or later root User. A rejected submission
/// returns no result. Existing Submit overloads keep returning WorkflowRun unchanged.
/// </summary>
public sealed record WorkflowSubmissionCommitResult(
    WorkflowRun Run,
    CommittedRootUserInputReceipt? FirstRootUserInput);
