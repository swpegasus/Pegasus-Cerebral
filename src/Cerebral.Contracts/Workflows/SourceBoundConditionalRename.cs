namespace Cerebral.Contracts.Workflows;

// Private signature candidate. The marker is a validated WorkerExecutionSnapshot
// member, never a WorkflowSubmission transport property or workflow Input field.

public static class SourceBoundConditionalRenameContract
{
    public const string CommandId = "cerebral.host:conditional-conversation-rename";
    public const int CommandVersion = 1;
}

/// <summary>Only the existing owner-metadata rename request is proposed by the
/// package. The handler derives authority from the saved host snapshot marker.</summary>
public sealed record SourceBoundConditionalRenameArguments(ConversationRenameRequest Rename);

public enum SourceBoundConditionalRenameDisposition { Applied, AlreadyApplied, Skipped }

public sealed record SourceBoundConditionalRenameResult(
    SourceBoundConditionalRenameDisposition Disposition,
    ConversationRenameResult? Receipt = null, string? SkipCode = null);

/// <summary>Engine-only data for an exact SQLite guard and existing rename CAS.
/// It contains no Core type, package callback, model setting, or owner command.</summary>
public sealed record SourceBoundConversationRenameGuard(
    ConversationRenameRequest Rename,
    string WorkRequestId, string WorkConversationId,
    string SourceConversationId, string SourceRequestId,
    string SourceEventId, long SourceEventSequence, string SourceInputSha256,
    string SourceDefinitionId, string SourceDefinitionVersion,
    string? SourceProjectId, string CapturedSourceTitle,
    long CapturedTitleRevision);

// The saved rename receipt must distinguish this guarded origin from the
// existing owner rename origin, even when both use the same request ID and
// request fields. The existing receipt table can retain a tagged request body.
