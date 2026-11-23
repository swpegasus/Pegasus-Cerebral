namespace Cerebral.Core;

/// <summary>Owner-visible model/resource diagnostics; excludes tool configuration, logs and credentials.</summary>
public sealed record WorkerModelRuntimeStatus(string Id, int ProcessId, long? WorkingSetMb,
    int? ResolvedGpuLayers, int? TotalGpuLayers, int Busy, int AvailableSlots, DateTimeOffset LastUsed);
public sealed record WorkerModelDiagnostics(string WorkerInstallationId, ResourceInfo Resources,
    IReadOnlyList<WorkerModelRuntimeStatus> Models, DateTimeOffset SampledAt, int ReservedMemoryMb = 0);
public sealed record WorkerModelLoadResult(ModelRecord Model, bool Started);
