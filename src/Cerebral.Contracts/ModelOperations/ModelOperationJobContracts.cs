using System.Text.Json;

namespace Cerebral.Contracts.ModelOperations;

public sealed record ModelOperationItemStatus(string ItemId, string JobId, string EffectId,
    ModelOperationState State, string? Stage = null, ModelOperationResult? Result = null,
    ModelOperationError? Error = null);
public sealed record ModelOperationBatchStatus(string Id, string RequestId, string RequestSha256,
    string ModelId, string Operation, string? OwnerProfileId, string State, bool CancelRequested,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ModelOperationItemStatus> Items,
    string? WaitingReason = null);
public sealed record ModelOperationProfileReadiness(ModelOperationProfile Profile, bool Qualified,
    string? QualificationId = null, string? UnavailableReason = null);
/// <summary>Availability is a host observation, never an execution or memory-fit grant.</summary>
public sealed record ModelOperationCatalogEntry(string ModelId, string Name, string WorkerInstallationId,
    string State, string? UnavailableReason, ModelOperationRuntimeDescriptor? Runtime,
    IReadOnlyList<ModelOperationProfileReadiness> Profiles);
public sealed record ModelOperationCatalog(int Version, IReadOnlyList<ModelOperationCatalogEntry> Models);
public sealed record ModelOperationDescription(int Version, ModelOperationCatalogEntry Model,
    JsonElement SettingsSchema);
