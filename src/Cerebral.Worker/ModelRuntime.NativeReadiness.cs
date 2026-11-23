using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    internal async Task<RegisteredNativeReadinessLease> AcquireReadyRegisteredNativeTextAsync(
        RemotePreparedModelBinding accepted, CancellationToken token)
    {
        var modelId = accepted?.Id is { Length: > 0 and <= 128 } id && !id.Contains('\0') ? id : "unavailable";
        using var owned = new NativeReadinessOwnershipScope(Accounting, modelId);
        var gateHeld = false;
        ModelExecutionAccounting.ModelOwnership? pin = null;
        Instance? pinned = null;
        try
        {
            ArgumentNullException.ThrowIfNull(accepted);
            accepted.Validate();
            // Own the entire accepted definition, including ordered duplicate/empty stops,
            // before the first await. Caller mutation cannot change loader admission.
            var frozen = OwnerRuntimeLaunchCapture.CopyBinding(accepted);
            frozen.Validate();
            await lifecycle.WaitAsync(token).ConfigureAwait(false); gateHeld = true;
            token.ThrowIfCancellationRequested(); ThrowIfOwnerRuntimeDisposed();
            await RemotePreparedInference.ValidateModelBinding(store, frozen, token).ConfigureAwait(false);
            var selected = store.Get<ModelRecord>("model", frozen.Id)
                ?? throw new InvalidOperationException("registered_native_model_unavailable");
            // The shared loader consumes THIS selected record, not another later store read.
            // Recheck it with the existing complete Bind contract before any load effects.
            RequireNativeReadinessBinding(frozen, RemotePreparedInference.Bind(selected));
            var fingerprint = CommandDispatcher.TargetModelFingerprint.Capture(selected);
            owned.Stage = RegisteredNativeReadinessStage.Loading;
            ModelLoadResult loaded;
            using (var loading = Accounting.Begin(ModelOwnershipKind.LocalLoad, frozen.Id))
            {
                try
                {
                    loaded = await LoadTrackedUnderLifecycleAsync(frozen.Id, token, loading,
                        selectedModel: selected).ConfigureAwait(false);
                }
                finally { loading.Complete(); }
            }
            owned.Stage = RegisteredNativeReadinessStage.Capture;
            if (!instances.TryGetValue(frozen.Id, out var instance) || instance.Stopped || HasExited(instance))
                throw new InvalidOperationException("registered_native_instance_unavailable");
            // A successful load can legitimately remain resident if later capture validation
            // fails. This marks only this exact authoritative context handle, never a total.
            owned.ConfirmResident(instance.Ownership);
            token.ThrowIfCancellationRequested(); ThrowIfOwnerRuntimeDisposed();
            var verified = store.Get<ModelRecord>("model", frozen.Id)
                ?? throw new InvalidOperationException("registered_native_model_unavailable");
            if (!fingerprint.MatchesConfirmedLoad(loaded.Model, verified))
                throw new InvalidOperationException("registered_native_confirmed_definition_changed");
            RequireNativeReadinessBinding(frozen, RemotePreparedInference.Bind(verified));
            var launch = instance.OwnerLaunch?.Binding
                ?? throw new InvalidOperationException("registered_native_launch_binding_unavailable");
            RequireNativeReadinessBinding(frozen, launch);
            var registration = GetRegisteredNativeTextUnderLifecycle(frozen.Id, instance);
            owned.ConfirmResident(registration.Ownership);
            // Shutdown can mark the owner disposed outside lifecycle. Publish the temporary
            // pin under the same short owner gate used by exact registered preparations.
            lock (ownerSnapshotGate)
            {
                if (ownerRuntimeDisposed) throw new ObjectDisposedException(nameof(ModelRuntime));
                var same = instances.TryGetValue(frozen.Id, out var current) &&
                    ReferenceEquals(current, instance) && current.Id == instance.Id;
                NativeRegistrationGuard.Require(true, same, !instance.Stopped && !HasExited(instance),
                    Accounting.Snapshot().AdmissionOpen, resourcePauseRequested, true);
                registration.RequireUsable(instance.Slots.Quarantined.Count != 0);
                token.ThrowIfCancellationRequested();
                pin = Accounting.Begin(ModelOwnershipKind.CapabilityLease, frozen.Id, requiresAdmission: true,
                    role: ModelOwnershipRole.TemporaryNativeReadiness);
                pin.Bind(instanceId: instance.Id);
                pin.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
                Interlocked.Increment(ref instance.Busy); pinned = instance;
                owned.Captured(ownerProcessGenerationId, instance.Id, pin.Id);
            }
            owned.Stage = RegisteredNativeReadinessStage.Residency;
            var heldInstance = pinned;
            var heldPin = pin;
            var lease = new RegisteredNativeReadinessLease(registration.Service, owned,
                () => ReleaseNativeReadinessPin(heldInstance!, heldPin!));
            owned.Seal();
            pin = null; pinned = null; // the lease is now the sole temporary-pin owner
            return lease;
        }
        catch (Exception error)
        {
            // No cached registration is disposed here. A successful native load or cached
            // facet is owned by Engine and may remain resident after this attempt fails.
            try
            {
                if (pinned is not null && pin is not null) ReleaseNativeReadinessPin(pinned, pin);
                else pin?.Complete();
            }
            catch (Exception cleanup) { owned.CleanupFailure = cleanup; }
            throw owned.Wrap(error);
        }
        finally { if (gateHeld) lifecycle.Release(); }
    }

    private static void RequireNativeReadinessBinding(RemotePreparedModelBinding expected,
        RemotePreparedModelBinding actual)
    {
        // Identical complete comparison to the reused remote binding validator. ContextTokens
        // is operational state; MaximumContextTokens remains a strict definition field.
        if (JsonSerializer.Serialize(expected, CerebralJson.Options) != JsonSerializer.Serialize(actual, CerebralJson.Options))
            throw new InvalidOperationException("registered_native_definition_changed");
    }

    private static void ReleaseNativeReadinessPin(Instance instance, ModelExecutionAccounting.ModelOwnership pin)
    {
        // Touch failure cannot strand Busy. This proves only temporary-pin release, never
        // native slot release, process stop, registry retirement or hardware reclamation.
        try { instance.Idle.Touch(); }
        finally
        {
            Interlocked.Decrement(ref instance.Busy);
            pin.Complete();
        }
    }
}
