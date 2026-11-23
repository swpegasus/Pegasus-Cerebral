using Cerebral.Worker.ModelAdapters;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

// Stored only on its actual ModelRuntime.Instance. This view does not increment Busy and
// therefore cannot keep an otherwise idle native model resident.
internal sealed class NativeRegistrationLifetime(RegisteredNativeGenerationService service,
    NativeRegistrationFence fence, ModelExecutionAccounting.ModelOwnership ownership)
{
    internal RegisteredNativeGenerationService Service { get; } = service;
    internal ModelExecutionAccounting.ModelOwnership Ownership { get; } = ownership;
    internal ModelRuntimeSupportSnapshot? RuntimeSupport { get; init; }
    internal IRegisteredNativeRequestShapeFacet? RequestShape { get; init; }
    private readonly object gate = new();
    private Task<Exception?>? retirement;
    internal void RequireUsable(bool quarantined)
    {
        var view = Service.Observe();
        // The creation-time Ready row cannot prove cleanup after an earlier callback failed.
        // Translate THIS exact cache's live registry/quarantine evidence into its own retained
        // ownership before reporting the causal blocker to a later readiness attempt.
        string? uncertainty = quarantined ? "registered-native-retirement-cached-quarantined" :
            view.Registry.Admission == InstanceAdmission.CleanupFaulted || view.Registry.UnconfirmedCleanupCount != 0
                ? "registered-native-retirement-cached-cleanup-faulted" :
            !view.RegistrationOpen && (view.ActiveCallbacks != 0 || view.Registry.RegistryReservations != 0 ||
                view.Registry.Admission is InstanceAdmission.Draining or InstanceAdmission.Retiring)
                ? "registered-native-retirement-cached-owners-pending" : null;
        if (uncertainty is not null) Ownership.Unknown(uncertainty);
        if (Ownership.IsUnknown && Ownership.ReadEntry() is { } retained)
        {
            NativeReadinessOwnershipScope.BlockedBy(Ownership.Owner, retained);
            throw new InvalidOperationException("registered_native_cached_cleanup_unconfirmed");
        }
        if (quarantined) throw new InvalidOperationException("registered_native_instance_quarantined");
        if (!view.RegistrationOpen || view.Registry.Admission != InstanceAdmission.Active)
            throw new InvalidOperationException("registered_native_cached_registration_unavailable");
    }

    // Fixed in-memory reads only. Never RequireUsable: observation must not mark ownership Unknown.
    internal ModelOwnershipRoleRegistration ObserveOwnershipRole()
    {
        var before = fence.SnapshotVersioned();
        var view = Service.Observe();
        var after = fence.SnapshotVersioned();
        var stable = before == after && view.RegistrationOpen == after.Open && view.ActiveCallbacks == after.Callbacks &&
            view.Registry.ModelId == view.Binding.ModelId && view.Registry.InstanceId == view.Binding.InstanceId;
        return new(view.Binding.ModelId, view.Binding.InstanceId, view.RuntimeGenerationId, view.AccountingGenerationId,
            view.RegistrationOpen, view.ActiveCallbacks, view.Registry.Admission.ToString(),
            view.Registry.RegistryReservations, view.Registry.UnconfirmedCleanupCount, after.Revision, stable);
    }

    // Pure in-memory observation. The existing fence and registry remain authoritative;
    // this never enters a callback, reserves a slot or changes ownership state.
    internal ModelRuntimeSupportSnapshot? ReadRuntimeSupport()
    {
        if (RuntimeSupport is not { } support) return null;
        try
        {
            return fence.WhileOpen(() =>
            {
                var view = Service.Observe();
                return view.RegistrationOpen && view.Registry.Admission == InstanceAdmission.Active &&
                    view.Registry.UnconfirmedCleanupCount == 0 &&
                    view.Binding.ModelId == support.Descriptor.Binding.ModelId &&
                    view.Binding.InstanceId == support.Descriptor.Binding.InstanceId &&
                    view.Binding.AdapterId == support.Descriptor.Binding.AdapterId &&
                    view.Binding.AdapterVersion == support.Descriptor.Binding.AdapterVersion
                    ? support : null;
            });
        }
        catch (InvalidOperationException) { return null; }
    }

    // The pure bounded shape check runs outside the registration fence. Bracket it with the
    // existing passive read so a closing/faulted registration cannot return accepted shape.
    internal ModelRuntimeSupportBinding? ReadRequestShape(ModelGenerationRequest request,
        ModelRuntimeSupportSnapshot expected)
    {
        if (RequestShape is not { } shape || !ReferenceEquals(ReadRuntimeSupport(), expected)) return null;
        var binding = shape.RequestShapeBinding;
        if (binding != expected.Descriptor.Binding) return null;
        shape.ValidateRequestShape(request);
        return ReferenceEquals(ReadRuntimeSupport(), expected) &&
            shape.RequestShapeBinding == binding ? binding : null;
    }

    internal void CloseAdmission() => fence.CloseAdmission();
    internal Task<Exception?> BeginRetirement()
    {
        TaskCompletionSource<Exception?> completion;
        lock (gate)
        {
            if (retirement is not null) return retirement;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            retirement = completion.Task;
        }
        CloseAdmission();
        Ownership.Unknown("registered-native-retirement-pending");
        _ = RetireAsync(completion);
        return completion.Task;
    }
    private async Task RetireAsync(TaskCompletionSource<Exception?> completion)
    {
        try
        {
            // No lifecycle gate is acquired here. If a callback is queued on lifecycle,
            // native Stop can release that gate and let it reject or settle normally.
            await fence.WhenIdle().ConfigureAwait(false);
            await Service.DisposeAsync().ConfigureAwait(false);
            var result = Service.Observe().Registry;
            if (result.Admission != InstanceAdmission.RegistryRetired || result.RegistryReservations != 0 ||
                result.UnconfirmedCleanupCount != 0 || !result.InstanceDisposeReturned)
                throw new InvalidOperationException("registered_native_retirement_unconfirmed");
            Ownership.Complete();
            completion.TrySetResult(null);
        }
        catch (Exception error)
        {
            // Callback settlement never erases retained registry reservations/CleanupFaulted.
            Ownership.Unknown("registered-native-retirement-failed");
            completion.TrySetResult(error);
        }
    }
}

public sealed partial class ModelRuntime
{
    private static bool IsRetainedNativeRegistration(ModelOwnershipEntry entry) =>
        entry.Kind == ModelOwnershipKind.CapabilityLease && entry.Phase == ModelOwnershipPhase.Unknown &&
        entry.Uncertainty?.StartsWith("registered-native-retirement-", StringComparison.Ordinal) == true;

    private void RequireNoRetainedNativeRegistration(string modelId)
    {
        var retained = Accounting.Snapshot().Entries.FirstOrDefault(entry => entry.ModelId == modelId && IsRetainedNativeRegistration(entry));
        if (retained is not null)
        {
            NativeReadinessOwnershipScope.BlockedBy(Accounting, retained);
            throw new InvalidOperationException("registered_native_prior_retirement_unconfirmed");
        }
    }
}
