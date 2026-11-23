using Cerebral.Worker.ModelOperations;

namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    private readonly ModelOperationCapacityLedger modelOperationCapacity = new();
    internal Func<CancellationToken, Task>? ModelOperationIdleSweep { get; set; }

    internal int EffectiveReservedMemoryMb(Cerebral.Core.HostSettings configuration)
        => checked(configuration.ReservedMemoryMb + (int)modelOperationCapacity.StillPromisedMb);

    internal async Task<ModelOperationCapacityLease> ReserveModelOperationAsync(ModelOperationMemoryDemand demand,
        CancellationToken token, bool allowResourceResume = false)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfOwnerRuntimeDisposed(); token.ThrowIfCancellationRequested();
            if (resourcePauseRequested && !allowResourceResume)
                throw new ModelOperationAdmissionException("resource_pause", "Cerebral's resource pause holds model-operation admission.", waiting: true);
            var ownership = Accounting.Begin(ModelOwnershipKind.CapabilityLease, demand.ModelId);
            try
            {
                var hardware = await HardwareProbe.ReadAsync(token).ConfigureAwait(false);
                var reservation = modelOperationCapacity.Reserve(demand, hardware, settings().ReservedMemoryMb);
                ownership.Bind(instanceId: demand.InstanceId, reservationId: reservation.Id);
                return new(this, reservation, ownership);
            }
            catch { ownership.Complete(); throw; }
        }
        finally { lifecycle.Release(); }
    }

    /// <summary>Scope disposal is never proof of provider completion. Resident and active-operation
    /// reservations use distinct leases; unknown ownership survives until explicit confirmed release.</summary>
    internal sealed class ModelOperationCapacityLease(ModelRuntime owner, ModelOperationMemoryReservation reservation,
        ModelExecutionAccounting.ModelOwnership ownership) : IAsyncDisposable
    {
        internal ModelOperationMemoryReservation Reservation { get; private set; } = reservation;
        private bool effects, retired;
        internal void BeginProviderEffect() { effects = true; ownership.Phase(ModelOwnershipPhase.Loading, effectStarted: true); }
        internal async Task MaterializedAsync(CancellationToken token)
        {
            await owner.lifecycle.WaitAsync(token).ConfigureAwait(false);
            try { Reservation = owner.modelOperationCapacity.Materialize(Reservation.Id); ownership.Phase(ModelOwnershipPhase.Ready, effectStarted: true); effects = true; }
            finally { owner.lifecycle.Release(); }
        }
        internal async Task ReleaseConfirmedAsync(CancellationToken token)
        {
            await owner.lifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (retired) return;
                owner.modelOperationCapacity.ReleaseConfirmed(Reservation.Id); ownership.Complete(); retired = true;
            }
            finally { owner.lifecycle.Release(); }
        }
        internal void MarkUnknown(string cause)
        {
            if (retired) return;
            owner.modelOperationCapacity.Unknown(Reservation.Id); ownership.Unknown(cause);
        }
        public async ValueTask DisposeAsync()
        {
            if (retired) return;
            if (effects) { MarkUnknown("operation-lease-ended-without-confirmed-release"); return; }
            await ReleaseConfirmedAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
