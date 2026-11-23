using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Context;

namespace Cerebral.Contracts.Workflows;

public static class WorkflowContract
{
    public const string Id = "pegasus.cerebral.workflow";
    public const int Version = 1;
    public const int MaximumStateBytes = 128 * 1024;
    public const int MaximumSteps = 512;
    public const int MaximumPluginTestMounts = 72;
    public static JsonElement EmptyObject => JsonSerializer.SerializeToElement(new { });
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowStatus>))]
public enum WorkflowStatus { Queued, Running, Waiting, Cancelling, Completed, Failed, Cancelled }
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowEffectState>))]
public enum WorkflowEffectState { Prepared, Dispatched, Succeeded, Failed, Cancelled, Unknown, RejectedBeforeDispatch }
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowSignalKind>))]
public enum WorkflowSignalKind { Start, Answers, EffectResult, DependencyResults, PreparedRejected }
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowReplayPolicy>))]
public enum WorkflowReplayPolicy { Never, Idempotent }

/// <summary>Value-only owner choice for the first local native text generation lane. The
/// Worker validates the actual loaded model, accepted definition and runtime policy separately.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowLocalGenerationChoice(
    int SchemaVersion, string Route, int ContextTokens, int VisibleOutputTokens)
{
    public const int CurrentSchemaVersion = 1;
    public const string NativeTextRoute = "local.native.text";

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Route != NativeTextRoute ||
            ContextTokens < 1 || VisibleOutputTokens < 1)
            throw new ArgumentException("The local generation choice is invalid or unsupported.");
    }
}

/// <summary>An exact definition-level opt-in. Absence grants no local generation path;
/// this declaration is distinct from receipt-context consumption.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowLocalGenerationConsumer(
    int SchemaVersion, string CommandId, int CommandVersion, string Route)
{
    public const int CurrentSchemaVersion = 1;
    public const string NativeGenerateCommandId = "cerebral.host:generate";
    public const int NativeGenerateCommandVersion = 2;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion ||
            CommandId != NativeGenerateCommandId || CommandVersion != NativeGenerateCommandVersion ||
            Route != WorkflowLocalGenerationChoice.NativeTextRoute)
            throw new ArgumentException("The workflow local-generation consumer is invalid or unsupported.");
    }
}

public sealed record WorkflowDefinitionDescriptor(string Id, string Version, string Description,
    JsonElement InputSchema, IReadOnlyList<string> RequiredCapabilities)
{
    public bool SupportsSteering { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowToolReceiptContextConsumer? ToolReceiptConsumer { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowLocalGenerationConsumer? LocalGenerationConsumer { get; init; }
}

/// <summary>Created by the host after authorization; settings contain no service credentials.</summary>
public sealed record WorkflowSubmission(string RequestId, string ConversationId, string ProfileId,
    string DefinitionId, string DefinitionVersion, string UserText, string? AssignmentText,
    JsonElement Input, JsonElement SettingsSnapshot, int StepLimit = 64, EventActor? Caller = null)
{
    /// <summary>Host-frozen project binding. Input project labels never grant project access.</summary>
    public string? ProjectId { get; init; }
    /// <summary>Host-owned delegation lineage. Transport input cannot assign a parent or borrow its context.</summary>
    public string? ParentRunId { get; init; }
    /// <summary>Host-frozen owner policy only; arbitrary plugin settings and credentials are excluded.</summary>
    public WorkflowAuditPolicy? AuditPolicy { get; init; }
    /// <summary>Host-validated immutable choice. No workflow input or command argument can add it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowLocalGenerationChoice? LocalGeneration { get; init; }
    /// <summary>Host-admitted isolated advisory work; never inferred from definition Input.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Cerebral.Contracts.Models.WorkflowIsolatedTextAdmission? IsolatedText { get; init; }
    /// <summary>Engine-owned prior-history boundary captured atomically before this turn's first event.</summary>
    public long ConversationSequence { get; init; }
    /// <summary>Supplemental accepted content; UserText remains the exact visible request. References
    /// are admitted by the host for this profile/conversation and never contain attachment bytes.</summary>
    public IReadOnlyList<ContentPart> AcceptedContent { get; init; } = [];
    public int ActiveTimeLimitSeconds { get; init; } = 900;
    public int PolicyStepLimit { get; init; } = WorkflowContract.MaximumSteps;
    [JsonIgnore] public int EffectiveStepLimit => Math.Min(StepLimit, PolicyStepLimit);
    public IReadOnlyDictionary<string, int> FailureLimits { get; init; } = new Dictionary<string, int>();
    /// <summary>Completion requirements inserted by the admitting host service. External workflow
    /// input cannot populate this provenance, and ordinary requirements remain in Input.</summary>
    public IReadOnlyList<string> HostAddedRequiredSuccessfulTools { get; init; } = [];
    /// <summary>Exact owner-approved plugin mounts inserted by the admitting host service for
    /// automatic native QA. These are provenance, not model input or control authority.</summary>
    public IReadOnlyList<string> HostPluginTestMountIds { get; init; } = [];
    /// <summary>
    /// Host-owned one-shot request captured from the conversation control plane. It is not accepted
    /// from workflow input and must be consumed atomically with this run's admission.
    /// </summary>
    public string? ConversationCompactionRequestId { get; init; }
}

/// <summary>Structural validation and ownership isolation only. This does not authorize reference access.</summary>
public static class WorkflowAcceptedContent
{
    public const int MaximumParts = 16;
    public const int MaximumUtf8Bytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static void Validate(IReadOnlyList<ContentPart> parts)
    {
        if (parts is null || parts.Count > MaximumParts)
            throw new ArgumentException("Accepted content requires at most 16 supplemental parts.", nameof(parts));
        for (var index = 0; index < parts.Count; index++)
        {
            if (parts[index] is null) throw new ArgumentException("Accepted content cannot contain null parts.", nameof(parts));
            parts[index].Validate(index);
        }
        if (JsonSerializer.SerializeToUtf8Bytes(parts, Json).Length > MaximumUtf8Bytes)
            throw new ArgumentException("Accepted content exceeds 128 KiB of serialized metadata.", nameof(parts));
    }

    public static IReadOnlyList<ContentPart> Freeze(IReadOnlyList<ContentPart> parts)
    {
        Validate(parts);
        var result = JsonSerializer.Deserialize<ContentPart[]>(JsonSerializer.SerializeToUtf8Bytes(parts, Json), Json)!;
        Validate(result);
        return result;
    }

    public static bool RequiresReferenceAdmission(IReadOnlyList<ContentPart> parts) =>
        parts.Any(part => part is FileContentPart or ImageContentPart or AudioContentPart or ResourceContentPart);
}

public sealed record WorkflowQuestion(string Id, string Label, bool Required = true,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>Choose a subset of Choices in one answer. Omitted for existing single-choice forms.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Multiple { get; init; }

    /// <summary>Optional Markdown explanation, including comparisons or tables, shown above the input.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}

public sealed record WorkflowQuestionSet(string Id, long Revision, IReadOnlyList<WorkflowQuestion> Fields,
    IReadOnlyDictionary<string, string> Answers);

/// <summary>Host-owned identity for one local child's question shown on its waiting parent.</summary>
public sealed record LocalChildQuestionRelay(string ParentRunId, string ChildRunId,
    long ChildRunRevision, string ChildQuestionSetId, long ChildQuestionRevision,
    string ParentQuestionSetId, long ParentQuestionRevision, string FieldsSha256,
    string ChildAnswersSha256,
    IReadOnlyList<string> WaitingForRunIds, EventActor ChildCaller);

public sealed record WorkflowAnswerRequest(string RequestId, string RunId, string QuestionSetId,
    long ExpectedQuestionRevision, IReadOnlyDictionary<string, string> Answers);

public sealed record WorkflowEffect(string Id, string CommandId, int CommandVersion, JsonElement Arguments,
    WorkflowReplayPolicy ReplayPolicy, WorkflowEffectState State, JsonElement? Result = null,
    string? Error = null, string? ProviderReceiptId = null)
{
    public int ReservedExecutionMilliseconds { get; init; }
    public string? FailureBudgetKey { get; init; }
}

/// <summary>Immutable persisted snapshot. The store freezes nested collections/JSON on admission.</summary>
public sealed record WorkflowRun
{
    public required string Id { get; init; }
    public required WorkflowSubmission Submission { get; init; }
    public long Revision { get; init; }
    public WorkflowStatus Status { get; init; } = WorkflowStatus.Queued;
    public JsonElement State { get; init; } = WorkflowContract.EmptyObject;
    public WorkflowSignal Signal { get; init; } = new(WorkflowSignalKind.Start, WorkflowContract.EmptyObject);
    public IReadOnlyList<WorkflowSteeringEntry> Steering { get; init; } = [];
    public WorkflowQuestionSet? Questions { get; init; }
    /// <summary>Only the engine may set this exact local-child answer binding.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LocalChildQuestionRelay? LocalChildQuestionRelay { get; init; }
    /// <summary>Durable child barrier; a waiting parent owns no scheduler or model slot.</summary>
    public IReadOnlyList<string> WaitingForRunIds { get; init; } = [];
    public WorkflowEffect? Effect { get; init; }
    public JsonElement? Result { get; init; }
    public string? WaitingReason { get; init; }
    public string? Error { get; init; }
    public bool CancelRequested { get; init; }
    public int Steps { get; init; }
    /// <summary>Measured completed work plus conservative reservations for uncertain/interrupted effects.</summary>
    public long ActiveMilliseconds { get; init; }
    /// <summary>Read-only receipt aggregate. Never persisted as workflow control state.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowActivityTotals? ActivityTotals { get; init; }
    public IReadOnlyDictionary<string, int> FailedEffects { get; init; } = new Dictionary<string, int>();
    /// <summary>Bounded owner interventions. These never change the accepted submission or its failure counts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WorkflowPreparedInterventionRecord>? PreparedInterventions { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool IsTerminal => Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled;
}

public sealed record WorkflowSignal(WorkflowSignalKind Kind, JsonElement Data);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AskWorkflowQuestions), "questions")]
[JsonDerivedType(typeof(InvokeWorkflowCommand), "command")]
[JsonDerivedType(typeof(CompleteWorkflow), "complete")]
[JsonDerivedType(typeof(WaitForWorkflowRuns), "children")]
public abstract record WorkflowAction;
public sealed record AskWorkflowQuestions(IReadOnlyList<WorkflowQuestion> Fields) : WorkflowAction;
public sealed record InvokeWorkflowCommand(string CommandId, int CommandVersion, JsonElement Arguments) : WorkflowAction;
public sealed record CompleteWorkflow(JsonElement Result, bool Success = true, string? Error = null) : WorkflowAction;
public sealed record WaitForWorkflowRuns(IReadOnlyList<string> RunIds) : WorkflowAction;
public sealed record WorkflowStep(JsonElement State, WorkflowAction Next);

/// <summary>Pure transition; effects are performed only after the Engine commits an intent.</summary>
public interface IWorkflowDefinition
{
    WorkflowDefinitionDescriptor Descriptor { get; }
    WorkflowStep Advance(WorkflowRun snapshot);
}

public sealed record WorkflowCommandDescriptor(string Id, int Version, string Description,
    JsonElement InputSchema, JsonElement OutputSchema, IReadOnlyList<string> RequiredCapabilities,
    WorkflowReplayPolicy ReplayPolicy = WorkflowReplayPolicy.Never, int TimeoutSeconds = 60);

public sealed record WorkflowCommandContext(string EffectId, WorkflowSubmission Submission, JsonElement Arguments)
{
    /// <summary>Injected by the runtime, never accepted from model or transport arguments.</summary>
    public string? RunId { get; init; }
    /// <summary>Host supplies prior evidence for read-only reconciliation; never supplied by model arguments.</summary>
    public WorkflowEffect? PreviousEffect { get; init; }
}
public sealed record WorkflowCommandResult(WorkflowEffectState State, JsonElement? Data = null,
    string? Error = null, string? ProviderReceiptId = null)
{
    /// <summary>Trusted host-produced checkpoint committed atomically with a successful effect receipt.</summary>
    public Cerebral.Contracts.Context.ContextCheckpoint? ConversationCheckpoint { get; init; }
}

/// <summary>Trusted host adapter. Reconcile must inspect existing work, never start a new effect.</summary>
public interface IWorkflowCommandHandler
{
    WorkflowCommandDescriptor Descriptor { get; }
    /// <summary>Pure host policy classification. Non-null keys require a saved failure limit.</summary>
    string? GetFailureBudgetKey(WorkflowCommandContext context) => null;
    Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken);
    Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        => Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Unknown, Error: "Effect requires reconciliation."));
}

/// <summary>A package supplies definitions and handlers; the host validates the whole candidate before publication.</summary>
public interface IWorkflowModule
{
    string Id { get; }
    IReadOnlyList<IWorkflowDefinition> Workflows { get; }
    IReadOnlyList<IWorkflowCommandHandler> Commands { get; }
}

public sealed class WorkflowConflictException(string message) : InvalidOperationException(message);
