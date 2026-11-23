using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Contracts.Context;

public static class ContextProviderContract
{
    public const int MaximumReferenceKinds = 64;
    public const int MaximumReferences = ContextValidation.MaximumSourceCount;
    public const int MaximumUtf8Bytes = WorkflowContract.MaximumStateBytes;
}

/// <summary>Versioned read-only context contribution advertised by a workflow command.</summary>
public sealed record ContextProviderDescriptor(
    string Id,
    int Version,
    string Description,
    IReadOnlyList<string> SupportedReferenceKinds,
    int MaxReferences,
    int MaxUtf8Bytes);

/// <summary>A workflow-authored reference. SourceId is provider-private lookup identity; Id is the
/// stable contribution identity retained by the host.</summary>
public sealed record ContextProviderReference(
    string Id,
    string Kind,
    string SourceId,
    long? ExpectedRevision = null,
    ContextRequirement Requirement = ContextRequirement.Optional,
    int Priority = 0,
    bool BeforeCurrentInput = false);

/// <summary>Trusted workflow effect arguments. The host validates these against the frozen provider
/// descriptor before dispatch and owns the resulting context/exclusion revisions.</summary>
public sealed record ContextProviderRequest(
    string ProviderId,
    int ProviderVersion,
    IReadOnlyList<ContextProviderReference> References,
    int MaximumUtf8Bytes,
    long ContextRevision = 0,
    long ExclusionRevision = 0);

/// <summary>Provider-authored source content. It deliberately has no exact-read handle; only the host
/// can mint one after it has a committed effect identity and a verified provider receipt.</summary>
public sealed record ContextProviderSource(
    string Id,
    string Kind,
    long SourceRevision,
    ContextRequirement Requirement,
    IReadOnlyList<ModelInputMessage> Messages,
    bool Complete = true,
    int Priority = 0,
    bool BeforeCurrentInput = false);

/// <summary>Raw successful provider output. WorkflowRuntime validates and replaces this value before
/// persistence, so workflow definitions consume ContextProviderResult instead.</summary>
public sealed record ContextProviderResolution(
    string ProviderId,
    int ProviderVersion,
    IReadOnlyList<ContextProviderSource> Sources);

/// <summary>Host-normalized durable result. Window is ready for ContextPlanner's exact adapter
/// measurement; provider-private CLR types never cross the package load boundary.</summary>
public sealed record ContextProviderResult(
    string ProviderId,
    int ProviderVersion,
    ContextWindow Window);

/// <summary>Marks a workflow command as a read-only context provider. It uses the ordinary durable
/// command effect, package binding, timeout, receipt and reconciliation lifecycle.</summary>
public interface IContextProviderCommandHandler : IWorkflowCommandHandler
{
    ContextProviderDescriptor Provider { get; }
}
