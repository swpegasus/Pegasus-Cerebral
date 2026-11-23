using System.Text.Json.Serialization;

namespace Cerebral.Contracts.ModelOperations;

/// <summary>Declarations and values do not grant execution, placement, resource or filesystem authority.</summary>
public sealed record ModelOperationCapability(string Id, int ContractVersion,
    IReadOnlyList<string> InputMediaTypes, IReadOnlyList<string> OutputMediaProfiles);

/// <summary>The host qualifies an exact profile separately; this declaration is not a fit or readiness result.</summary>
public sealed record ModelOperationProfile(string Id, string Mode, string Platform, string Device,
    string Sha256, IReadOnlyList<string> Operations);

public sealed record ModelOperationRuntimeDescriptor(string RuntimeId, string RuntimeVersion,
    IReadOnlyList<ModelOperationCapability> Capabilities, IReadOnlyList<ModelOperationProfile> Profiles);

/// <summary>Frozen by the host at admission. InstanceId must identify this generation, never a reusable model slot.</summary>
public sealed record ModelOperationRuntimeBinding(string WorkerInstallationId, string RuntimeId,
    string RuntimeVersion, string RuntimeSha256, string ModelId, string PackageId, string PackageVersion,
    string PackageSha256, string ProfileId, string ProfileSha256, string InstanceId);

/// <summary>Immutable, opaque store coordinate. Paths and access grants stay in the host's private store.</summary>
public sealed record ModelOperationArtifactReference(string WorkerInstallationId, string JobId, string EffectId,
    string ArtifactId, long Revision, string MediaType, string MediaProfile, string Role, long Bytes, string Sha256);

public sealed record ModelOperationInput(string Role, ModelOperationArtifactReference Artifact);
public sealed record ModelOperationOutput(string Role, ModelOperationArtifactReference Artifact);

public enum ModelOperationPipelineType
{
    [JsonStringEnumMemberName("512")] Resolution512,
    [JsonStringEnumMemberName("1024")] Resolution1024,
    [JsonStringEnumMemberName("1024_cascade")] Cascade1024,
    [JsonStringEnumMemberName("1536_cascade")] Cascade1536
}

public sealed record ModelOperationGuidanceInterval(double Start, double End);
public sealed record ModelOperationSamplerOptions(int? Steps = null, double? GuidanceStrength = null,
    double? GuidanceRescale = null, ModelOperationGuidanceInterval? GuidanceInterval = null,
    double? TimeRescale = null);

/// <summary>Null means the selected provider profile's declared default, not a guessed upstream value.
/// Shape bounds alone do not qualify a nondefault remesh/projection configuration.</summary>
public sealed record ModelOperationExportOptions(int? DecimationTarget = null, bool? Remesh = null,
    double? RemeshBand = null, double? RemeshProjection = null);

public sealed record ModelOperationOptions
{
    public ModelOperationPipelineType? PipelineType { get; init; }
    /// <summary>Mesh-retexture resolution is independent of the image-to-3D pipeline selector.</summary>
    public int? Resolution { get; init; }
    public int? NumSamples { get; init; }
    public bool? PreprocessImage { get; init; }
    public int? MaxNumTokens { get; init; }
    public int? Seed { get; init; }
    public int? TextureResolution { get; init; }
    public ModelOperationSamplerOptions? SparseStructure { get; init; }
    public ModelOperationSamplerOptions? Shape { get; init; }
    public ModelOperationSamplerOptions? Texture { get; init; }
    public ModelOperationExportOptions? Export { get; init; }
}

/// <summary>Client intent only. The Worker assigns durable job/effect and instance bindings per item.</summary>
public sealed record ModelOperationSubmissionItem(string ItemId, IReadOnlyList<ModelOperationInput> Inputs,
    ModelOperationOptions Options);
public sealed record ModelOperationSubmissionRequest(int Version, string RequestId, string ModelId,
    string Operation, IReadOnlyList<ModelOperationSubmissionItem> Items);

/// <summary>The runtime accepts only the host-frozen request, after admission and profile qualification.</summary>
public sealed record ModelOperationRequest(int Version, string RequestId, string JobId, string EffectId,
    string Operation, ModelOperationRuntimeBinding Binding, IReadOnlyList<ModelOperationInput> Inputs,
    ModelOperationOptions Options);

public enum ModelOperationState { Queued, Loading, Running, Exporting, Succeeded, Failed, Cancelled, Unknown }
public sealed record ModelOperationProgress(int Version, string JobId, string EffectId,
    ModelOperationState State, string Stage, double? Fraction = null, string? Message = null);
public sealed record ModelOperationError(string Code, string Message);
public sealed record ModelOperationResult(int Version, string RequestId, string JobId, string EffectId,
    ModelOperationRuntimeBinding Binding, ModelOperationState State, bool Partial,
    IReadOnlyList<ModelOperationOutput> Outputs, ModelOperationError? Error = null);

public sealed record ModelOperationExportRequest(int Version, string RequestId, string JobId, string EffectId,
    ModelOperationRuntimeBinding Binding, ModelOperationExportOptions Options);
public sealed record ModelOperationCancelRequest(int Version, string RequestId, string JobId, string EffectId,
    ModelOperationRuntimeBinding Binding);
/// <summary>Requested is not confirmation. Capacity remains owned until the host observes confirmed cleanup.</summary>
public sealed record ModelOperationCancellation(int Version, string JobId, string EffectId,
    bool Requested, bool Confirmed, bool PhysicalReleaseConfirmed);
public sealed record ModelOperationRuntimeState(ModelOperationRuntimeBinding Binding, string State,
    bool PhysicalReleaseConfirmed, ModelOperationError? Error = null);

/// <summary>Optional nontext facet under trusted service-provider activation. No alternate auth, scheduler,
/// resource policy, idle timer, public listener or tool dispatcher is conferred by this interface.</summary>
public interface IModelOperationRuntime : IAsyncDisposable
{
    ModelOperationRuntimeDescriptor Descriptor { get; }
    Task<ModelOperationRuntimeState> LoadAsync(ModelOperationRuntimeBinding binding, CancellationToken cancellationToken);
    Task<ModelOperationResult> RunAsync(ModelOperationRequest request,
        IProgress<ModelOperationProgress>? progress, CancellationToken cancellationToken);
    Task<ModelOperationResult> ExportAsync(ModelOperationExportRequest request,
        IProgress<ModelOperationProgress>? progress, CancellationToken cancellationToken);
    Task<ModelOperationCancellation> CancelAsync(ModelOperationCancelRequest request, CancellationToken cancellationToken);
    Task<ModelOperationRuntimeState> UnloadAsync(ModelOperationRuntimeBinding binding, CancellationToken cancellationToken);
}
