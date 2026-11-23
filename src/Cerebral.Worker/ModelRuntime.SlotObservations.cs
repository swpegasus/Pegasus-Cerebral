namespace Cerebral.Worker;

internal sealed record ManagedModelSlotObservation(string ModelId, string InstanceId, string InstanceState,
    int? AllocatedSlots, int? ManagedAvailableSlots, int? ManagedQuarantinedSlots);
internal sealed record ManagedSlotObservationPage(string Schema, DateTimeOffset SampledAt,
    string ProcessGenerationId, long RuntimeEpoch, string State, bool Truncated,
    bool? ModelAdmissionOpen, IReadOnlyList<ManagedModelSlotObservation> Models);

public sealed partial class ModelRuntime
{
    /// <summary>Managed allocation only: no provider I/O, loading, configuration normalization or store writes.</summary>
    internal ManagedSlotObservationPage ObserveManagedSlots(int maximumModels = 64)
    {
        if (maximumModels is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumModels));
        try { if (!lifecycle.Wait(0)) return Page("unavailable", false, null, []); }
        catch (ObjectDisposedException) { return Page("unavailable", false, null, []); }
        try
        {
            lock (ownerSnapshotGate)
            {
                if (ownerRuntimeDisposed) return Page("unavailable", false, null, []);
                var selected = instances.OrderBy(x => x.Key, StringComparer.Ordinal).Take(maximumModels + 1).ToArray();
                var truncated = selected.Length > maximumModels;
                var rows = new List<ManagedModelSlotObservation>();
                foreach (var (id, instance) in selected.Take(maximumModels))
                {
                    try
                    {
                        var present = !instance.Stopped && !instance.Process.HasExited;
                        rows.Add(present
                            ? new(id, instance.Id, "present", instance.Slots.Capacity,
                                instance.Slots.AvailableCount, instance.Slots.Quarantined.Count)
                            : new(id, instance.Id, "absent", null, null, null));
                    }
                    catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
                    { rows.Add(new(id, instance.Id, "unobserved", null, null, null)); }
                }
                // Allocator counters are individually sampled; do not derive an atomic admission fence.
                return Page(truncated || rows.Any(x => x.InstanceState == "unobserved") ? "partial" : "observed",
                    truncated, !resourcePauseRequested, rows.AsReadOnly());
            }
        }
        finally { lifecycle.Release(); }

        ManagedSlotObservationPage Page(string state, bool truncated, bool? admission,
            IReadOnlyList<ManagedModelSlotObservation> models) =>
            new("cerebral.managed-model-slots/1", DateTimeOffset.UtcNow, ownerProcessGenerationId,
                Interlocked.Read(ref ownerRuntimeEpoch), state, truncated, admission, models);
    }
}
