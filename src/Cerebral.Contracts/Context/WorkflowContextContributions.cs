using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;

namespace Cerebral.Contracts.Context;

/// <summary>Optional, package-owned context selection; the base service/provider interfaces are unchanged.</summary>
public static class WorkflowContextContributionContract
{
    public const int SchemaVersion = 1;
    public const int MaximumContributions = 4;
    // Compact serialized-value budgets; the outer host snapshot has its own raw/depth bound.
    public const int MaximumPayloadUtf8Bytes = 8192;
    public const int MaximumLegacyFragmentUtf8Bytes = 16384;
    public const int MaximumLegacyFields = 8;
    public const int MaximumLegacyFormats = 8;
    public const int MaximumCollectionUtf8Bytes = 65536;
}

/// <summary>Body-free declaration that a loaded workflow can receive committed tool-receipt context.</summary>
public static class WorkflowToolReceiptContextConsumerContract
{
    public const int SchemaVersion = 1;
}

public sealed record WorkflowToolReceiptContextConsumer(int SchemaVersion, bool Supported);

/// <summary>These are host-authored facts, never claims supplied by a command or model.</summary>
public sealed record WorkflowContextIdentity(string RequestId, string ConversationId, string ProfileId,
    string DefinitionId, string DefinitionVersion, EventActor? Caller, string? ParentRunId);

/// <summary>No prompt, profile instructions, history, model settings, paths or grants cross this boundary.
/// Caller identity contains Kind/Id only. Accepted legacy installation origin can be unknown (null);
/// current host facts always use the observed installation, never a substitute for accepted origin.</summary>
public sealed record WorkflowContextFacts(WorkflowContextIdentity Identity, string ProfileRole,
    int ProfileRevision, string WorkerId, string? InstallationId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowToolReceiptContextConsumer? ToolReceiptConsumer { get; init; }
}

public sealed record WorkflowContextAcceptedFacts(WorkflowContextFacts Accepted, string? ProjectId,
    long ConversationSequence, long ContextRevision, long ExclusionRevision);

/// <summary>Only extension slots declared by the package are handed to its pure compatibility reader.
/// Core-reserved snapshot fields cannot be declared as legacy extension slots.</summary>
public sealed record WorkflowContextLegacyFormat(int SnapshotVersion, IReadOnlyList<string> Fields);

public enum WorkflowContextDisposition { Ready, Omitted, Unavailable, Blocked, Unsupported }
public enum WorkflowContextHistoryProjection { Preserve, ReferenceOnly }

/// <summary>Descriptor and request-template collections are cloned and frozen by the host/runtime;
/// package-owned mutable collections cannot change an admitted selection.</summary>
public sealed record WorkflowContextContributionDescriptor(int SchemaVersion, string Id,
    ContextProviderDescriptor Provider, WorkflowContextHistoryProjection HistoryProjection,
    IReadOnlyList<WorkflowContextLegacyFormat> LegacyFormats);

/// <summary>Payload and its digest are package-private codec data. The host never redefines the inner
/// digest. Ready requires a request and either a paired-null payload/digest (stateless) or both values.
/// Request is a body-free immutable template; only context/exclusion revisions may be rebased.</summary>
public sealed record WorkflowContextSelection(WorkflowContextDisposition Disposition, string? Reason,
    JsonElement? Payload, string? PayloadDigest, ContextProviderRequest? Request);

public sealed record WorkflowContextLegacyFacts(WorkflowContextAcceptedFacts Facts, int SnapshotVersion,
    JsonElement Fragments);

/// <summary>Implemented optionally by a loaded service package. Callbacks are host-only and do not
/// add owner commands, tools, grants, a scheduler or an inference-transport dependency.</summary>
public interface IServiceWorkflowContextModule
{
    IReadOnlyList<IWorkflowContextContribution> ContextContributions { get; }
}

public interface IWorkflowContextContribution
{
    WorkflowContextContributionDescriptor Descriptor { get; }

    /// <summary>Package owns applicability, selection and migration readiness. Capture may read only;
    /// return an explicit unavailable/blocked disposition instead of inventing a replacement source.</summary>
    Task<WorkflowContextSelection> CaptureAsync(WorkflowContextFacts facts, CancellationToken cancellationToken);

    /// <summary>Validate the frozen selection and current package policy before acceptance/execution.
    /// This callback is read-only. A corrupt ready selection refuses; it must not degrade to a new default or optional omission.</summary>
    void Validate(WorkflowContextFacts accepted, WorkflowContextFacts current,
        WorkflowContextSelection selection, CancellationToken cancellationToken);

    /// <summary>Restore from the winning accepted payload only, never from current selection/defaults.
    /// Return the same request template with the supplied context/exclusion revisions.</summary>
    Task<ContextProviderRequest> RestoreAsync(WorkflowContextAcceptedFacts facts,
        WorkflowContextSelection selection, CancellationToken cancellationToken);

    /// <summary>Pure, store-free compatibility conversion. Preserve the original inner digest semantics
    /// and omission/outcome; no capture, dispatch, reconciliation, migration or canonical storage write.</summary>
    WorkflowContextSelection ReadLegacy(WorkflowContextLegacyFacts facts, CancellationToken cancellationToken);
}
