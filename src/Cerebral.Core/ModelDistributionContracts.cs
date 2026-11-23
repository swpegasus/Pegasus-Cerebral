namespace Cerebral.Core;

/// <summary>Owner-only content evidence for one registered model; paths and settings are not transfer authority.</summary>
public sealed record ModelContentIdentity(long Bytes, string Sha256);

public sealed record ModelContentDescription(int Version, string WorkerInstallationId, string ModelId,
    ModelContentIdentity Weights, ModelContentIdentity? Projector, DateTimeOffset CheckedAt)
{
    public const int CurrentVersion = 1;
}

/// <summary>ExistingVerified proves a matching installed pair. It is distinct from a completed file transfer.</summary>
public sealed record ModelDistributionReceipt(string State, string WorkerId, string WorkerInstallationId,
    string SourceModelId, string TargetModelId, long BytesTransferred, bool TargetSettingsChanged,
    ModelContentIdentity Weights, ModelContentIdentity? Projector, DateTimeOffset CheckedAt);
