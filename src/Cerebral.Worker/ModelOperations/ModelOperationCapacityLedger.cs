using Cerebral.Core;

namespace Cerebral.Worker.ModelOperations;

public sealed record ModelOperationMemoryDemand(string ModelId, string InstanceId, long MinimumDeviceBytes,
    long PromisedBytes, bool Unified);
public sealed record ModelOperationMemoryReservation(string Id, ModelOperationMemoryDemand Demand,
    long StillPromisedMb, bool Materialized, bool Unknown);

/// <summary>Bookkeeping for promised allocations, not a hardware measurement or an execution grant.
/// The owning ModelRuntime additionally serializes snapshot/reserve commits with native fitting.</summary>
public sealed class ModelOperationCapacityLedger
{
    private readonly object gate = new();
    private readonly Dictionary<string, ModelOperationMemoryReservation> entries = new(StringComparer.Ordinal);
    public long StillPromisedMb { get { lock (gate) return entries.Values.Sum(x => x.StillPromisedMb); } }
    public IReadOnlyList<ModelOperationMemoryReservation> Snapshot() { lock (gate) return entries.Values.ToArray(); }

    public ModelOperationMemoryReservation Reserve(ModelOperationMemoryDemand demand, ResourceInfo observed, int configuredReserveMb)
    {
        ModelOperationArtifactStore.Identifier(demand.ModelId); ModelOperationArtifactStore.Identifier(demand.InstanceId);
        if (demand.MinimumDeviceBytes <= 0 || demand.PromisedBytes <= 0 || demand.PromisedBytes > 1L << 40 || configuredReserveMb < 0)
            throw new ArgumentException("Invalid model-operation memory demand.");
        lock (gate)
        {
            var promised = entries.Values.Sum(x => x.StillPromisedMb);
            var needed = checked((demand.PromisedBytes + 1048575L) / 1048576L);
            if (observed.Gpus.Count != 1 || observed.Gpus[0].Unified != demand.Unified)
                throw new ModelOperationAdmissionException("device_profile", "Initial graphics profiles require one exact qualified GPU/memory-pool topology.");
            var device = observed.Gpus[0];
            var total = demand.Unified ? observed.SystemTotalMb : device.TotalMb;
            var available = demand.Unified ? observed.SystemAvailableMb : device.FreeMb;
            if (total is null || available is null || available < 0)
                throw new ModelOperationAdmissionException("capacity_unknown", "Current capacity is unknown; no model allocation is admitted.", waiting: true);
            if (total.Value < checked((demand.MinimumDeviceBytes + 1048575L) / 1048576L))
                throw new ModelOperationAdmissionException("device_memory", "Device capacity is below this runtime profile's qualified minimum.");
            // A materialized resident is already reflected by the fresh available-memory
            // observation. Only promises not yet represented there are deducted again.
            if (available.Value < checked(needed + promised + configuredReserveMb))
                throw new ModelOperationAdmissionException("capacity_busy", "Current free capacity cannot satisfy this operation and the existing host reserve.", waiting: true);
            var value = new ModelOperationMemoryReservation(Guid.NewGuid().ToString("N"), demand, needed, false, false);
            entries.Add(value.Id, value); return value;
        }
    }

    public ModelOperationMemoryReservation Materialize(string id)
    {
        lock (gate)
        {
            var value = Required(id);
            if (value.Unknown) throw new InvalidOperationException("An unknown reservation requires exact-instance reconciliation.");
            value = value with { StillPromisedMb = 0, Materialized = true }; entries[id] = value; return value;
        }
    }
    public void Unknown(string id) { lock (gate) { var value = Required(id); entries[id] = value with { Unknown = true }; } }
    public void ReleaseConfirmed(string id) { lock (gate) { Required(id); entries.Remove(id); } }
    private ModelOperationMemoryReservation Required(string id) => entries.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Model-operation reservation not found.");
}
