using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Contracts.Collaboration;

public static class CollaborationContract
{
    public const string Id = "pegasus.cerebral.collaboration";
    public const int Version = 1;
    public const int MaximumInstructionUtf8Bytes = 128 * 1024;
    public const int MaximumResultUtf8Bytes = 256 * 1024;
    public const int MaximumReferences = 32;
    public const int MaximumDependencies = 32;
    public const int MaximumEventPage = 200;
    public const int MaximumViewChildren = 64;
}

[JsonConverter(typeof(JsonStringEnumConverter<CollaborationTaskStatus>))]
public enum CollaborationTaskStatus
{
    Queued,
    Running,
    Waiting,
    Cancelling,
    ReconcileRequired,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<CollaborationResourceAccess>))]
public enum CollaborationResourceAccess
{
    Read,
    Write
}

/// <summary>Opaque typed resource identity. Paths, credentials, prompt bodies, plugin settings, and Cortex namespaces are outside this contract.</summary>
public sealed record CollaborationResourceReference(
    string ProjectId,
    string Kind,
    string Id,
    long Revision,
    CollaborationResourceAccess Access = CollaborationResourceAccess.Read);

/// <summary>Caller input. The service resolves and freezes project and profile identities from the selected conversations.</summary>
public sealed record CollaborationTaskRequest(
    string RequestId,
    string SourceConversationId,
    string TargetConversationId,
    string Instruction,
    IReadOnlyList<CollaborationResourceReference> InputReferences,
    IReadOnlyList<string> DependencyTaskIds,
    string? ParentTaskId = null,
    int StepLimit = 64,
    int? ResponseSharePercent = null,
    string? ProjectGrantId = null);

public sealed record CollaborationTask
{
    public required string Id { get; init; }
    public required CollaborationTaskRequest Request { get; init; }
    public required string AttemptId { get; init; }
    public int AttemptNumber { get; init; } = 1;
    public required string SourceProfileId { get; init; }
    public string? SourceRunId { get; init; }
    public required string TargetProfileId { get; init; }
    public string? SourceProjectId { get; init; }
    public string? TargetProjectId { get; init; }
    public string? ProjectGrantId { get; init; }
    public long? ProjectGrantRevision { get; init; }
    public string? TargetRunId { get; init; }
    public long? TargetRunRevision { get; init; }
    public CollaborationTaskStatus Status { get; init; } = CollaborationTaskStatus.Queued;
    public long Revision { get; init; } = 1;
    public WorkflowQuestionSet? Questions { get; init; }
    public JsonElement? Result { get; init; }
    public string? WaitingReason { get; init; }
    public string? Error { get; init; }
    public bool CancelRequested { get; init; }
    public string? AdoptionRequestId { get; init; }
    public long? AdoptedSourceRevision { get; init; }
    public DateTimeOffset? AdoptedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool IsTerminal => Status is CollaborationTaskStatus.Completed or CollaborationTaskStatus.Failed or CollaborationTaskStatus.Cancelled;
}

public sealed record CollaborationTaskEvent(
    string TaskId,
    long Sequence,
    string Type,
    CollaborationTaskStatus Status,
    DateTimeOffset At,
    JsonElement Data);

public sealed record CollaborationTaskEventPage(
    IReadOnlyList<CollaborationTaskEvent> Events,
    long SnapshotSequence,
    long? NextAfterSequence);

public sealed record CollaborationTaskListPage(
    IReadOnlyList<CollaborationTask> Tasks,
    long SnapshotRow,
    long? NextBeforeRow);

/// <summary>Owner-only navigation coordinates. Run content and resource authority remain in their existing exact-read APIs.</summary>
public sealed record CollaborationViewRun(string RunId, string ConversationId, string ProfileId,
    string? ProjectId, string? ParentRunId, WorkflowStatus Status, long Revision);

public sealed record CollaborationViewTask(string TaskId, long Revision, CollaborationTaskStatus Status,
    string TargetConversationId, string TargetProfileId, string? TargetProjectId,
    string? TargetRunId, long? TargetRunRevision, DateTimeOffset UpdatedAt)
{
    public string? SourceRunId { get; init; }
}

public sealed record CollaborationViewTaskPage(IReadOnlyList<CollaborationViewTask> Tasks,
    long SnapshotRow, long? NextBeforeRow);

/// <summary>A bounded view of one accepted task. Complete means every retained lineage edge was verified;
/// synchronized means the task projection has caught up with its coordinator run.</summary>
public sealed record CollaborationViewGraph(int Version, string TaskId, long TaskRevision,
    CollaborationTaskStatus TaskStatus, string SourceConversationId, string SourceProfileId,
    string? SourceProjectId, string TargetConversationId, string TargetProfileId,
    string? TargetProjectId, CollaborationViewRun? Coordinator,
    IReadOnlyList<CollaborationViewRun> Workers, int ChildCount, int? NextChildOffset,
    bool Complete, bool Synchronized, string? IncompleteReason)
{
    public string? SourceRunId { get; init; }
}

public sealed record CollaborationResultPage(
    string TaskId,
    string AttemptId,
    long TargetRunRevision,
    int Offset,
    string Content,
    int? NextOffset,
    bool Complete,
    string Sha256);

public sealed record CollaborationProjectGrantRequest(
    string Id,
    string SourceProjectId,
    string TargetProjectId,
    IReadOnlyList<string> TargetProfileIds,
    IReadOnlyList<string> Capabilities);

public sealed record CollaborationProjectGrant(
    string Id,
    string SourceProjectId,
    string TargetProjectId,
    IReadOnlyList<string> TargetProfileIds,
    IReadOnlyList<string> Capabilities,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt = null);

public static class CollaborationCapabilities
{
    public const string ExecuteTask = "task.execute";
    public const string ReadResource = "resource.read";
    public const string WriteResource = "resource.write";
}

public sealed record CollaborationAnswerRequest(
    string RequestId,
    string TaskId,
    long ExpectedTaskRevision,
    string AttemptId,
    long ExpectedTargetRunRevision,
    string QuestionSetId,
    long ExpectedQuestionRevision,
    IReadOnlyDictionary<string, string> Answers);

/// <summary>Host-bound question relay carried in the source conversation's durable harness state.</summary>
public sealed record CollaborationQuestionBinding(string TaskId, long TaskRevision,
    string AttemptId, string TargetRunId, long TargetRunRevision,
    string TargetQuestionSetId, long TargetQuestionRevision,
    string? SourceQuestionSetId = null, long? SourceQuestionRevision = null);

public sealed record CollaborationSteerRequest(
    string RequestId,
    string TaskId,
    long ExpectedTaskRevision,
    string Text,
    string? AssignmentText = null);

public sealed record CollaborationCancelRequest(string TaskId, long ExpectedTaskRevision);

public sealed record CollaborationReconcileRequest(string TaskId, long ExpectedTaskRevision);

public sealed record CollaborationAdoptRequest(
    string RequestId,
    string TaskId,
    long ExpectedTaskRevision,
    string AttemptId,
    long ExpectedTargetRunRevision);
