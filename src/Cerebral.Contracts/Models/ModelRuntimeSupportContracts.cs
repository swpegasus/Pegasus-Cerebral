namespace Cerebral.Contracts.Models;

/// <summary>Passive support data only; construction neither proves runtime behavior nor grants execution.</summary>
public static class ModelRuntimeSupportContract
{
    public const int SchemaVersion = 1;
    // Collection/identifier budgets, not limits on model slots or token capacity.
    public const int MaximumCapabilities = 64;
    public const int MaximumKinds = 32;
    public const int MaximumIdentifierCharacters = 128;
}

public enum RuntimeLimitBasis { BackendHard, ModelNative, OperatorPolicy, WireCompatibility, Unreported }

public sealed record RuntimeLimitProvenance(RuntimeLimitBasis Basis, string SourceId, string Revision);

/// <summary>A null maximum is unknown, never infinity or a measured hardware maximum.
/// Independent ranges are necessary outer bounds only: they cannot represent conditional
/// reasoning, intersected provenance or exact input/output/safety fit. Existing request
/// validation and actual preparation remain authoritative.</summary>
public sealed record RuntimeLimitRange(long Minimum, long? Maximum, long Step,
    RuntimeLimitProvenance Provenance);

/// <summary>ConfigurationScopeId names the exact versioned preimage represented by the digest
/// as a qualified identifier followed by / and a positive decimal version.
/// A null InstanceId means unbound or unobserved; it never proves that no instance is loaded
/// or that a model fits. An instance-owned declaration must carry its actual instance ID.</summary>
public sealed record ModelRuntimeSupportBinding(string WorkerInstallationId, string BackendId,
    string AdapterId, string AdapterVersion, string ModelId, string ConfigurationScopeId,
    string ConfigurationSha256, string? InstanceId);

/// <summary>One model instance. Runnable task counts and connection counts are separate dimensions.</summary>
public sealed record ModelRuntimeCapacityConstraints(RuntimeLimitRange InferenceSlots,
    RuntimeLimitRange ContextPerSlotTokens, RuntimeLimitRange VisibleOutputTokens,
    RuntimeLimitRange ReasoningTokens, RuntimeLimitRange CombinedGenerationTokens,
    RuntimeLimitRange AggregateContextTokens);

/// <summary>Labels require a matching admitted typed handler/codec binding; this value has no usable flag.</summary>
public sealed record ModelRuntimeCapability(string Id, string ContractId, int ContractVersion,
    IReadOnlyList<string> InputKinds, IReadOnlyList<string> OutputKinds);

public sealed record ModelRuntimeSupportDescriptor(int SchemaVersion, ModelRuntimeSupportBinding Binding,
    ModelRuntimeCapacityConstraints Capacity, IReadOnlyList<ModelRuntimeCapability> Capabilities);

/// <summary>Optional same-object facet. Existing adapter/descriptor positional and base interfaces are unchanged.</summary>
public interface IModelRuntimeSupportFacet
{
    ModelRuntimeSupportDescriptor RuntimeSupport { get; }
}

// Host observations are deliberately absent from the adapter-declared support descriptor.
// These distinct value types do not verify themselves or create preparation/runtime authority.
public sealed record ModelRuntimeMemorySample(ModelRuntimeSupportBinding Binding, string PoolId,
    long? AvailableBytes, long ReservedBytes, DateTimeOffset SampledAt);

public sealed record ModelRuntimeCapacityEstimate(ModelRuntimeSupportBinding Binding, string Basis,
    long? PlacementBytes, long? RemainingBytes, string Limitations);

public sealed record ModelRuntimeEffectiveContext(ModelRuntimeSupportBinding Binding,
    long ContextPerSlotTokens, long InferenceSlots, string Source, DateTimeOffset ObservedAt);

public sealed record ModelRuntimePreparedEvidence(ModelRuntimeSupportBinding Binding,
    PreparedModelMetadata Prepared);

public sealed record RuntimeTaskConcurrencyObservation(long MaximumRunnableTasks,
    long RunningTasks, long QueuedTasks);
