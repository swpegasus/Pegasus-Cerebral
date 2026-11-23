using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker;

// A structural check on the same handler that prepares registered native text. Success requires
// exact preparation; it says nothing about template acceptance, memory fit, runnable/free slots
// or a KV lease. Invalid requests keep the normal typed validation exceptions.
internal interface IRegisteredNativeRequestShapeFacet
{
    ModelRuntimeSupportBinding RequestShapeBinding { get; }
    void ValidateRequestShape(ModelGenerationRequest request);
}

// Only the registered handler with the exact captured loaded instance implements this private
// executor. A public policy value or an arbitrary adapter facet cannot invoke it.
internal interface IRegisteredNativePolicyExecutor : IModelExecutionPolicyFacet
{
    Task<IPreparedModelGeneration> PrepareSelectedAsync(ModelGenerationRequest request,
        ModelExecutionPolicy selected, CancellationToken token);
}

internal static class RegisteredNativeTextRequestShape
{
    internal static void Require(ModelGenerationRequest request, string modelId)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.ModelId != modelId)
            throw new ArgumentException("Registered native inference requires its captured model and text content.");
        // The actual native projection uses this same allocation-light pure guard.
        // A late system message fails before entering slot/accounting work.
        LlamaPreparedGeneration.ValidateTextShape(request);
    }

    internal static void Require(ModelGenerationRequest request, string modelId,
        ModelExecutionPolicy selected)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(selected);
        if (request.ModelId != modelId || selected.Binding.ModelId != modelId)
            throw new ArgumentException("Selected native text inference requires its captured model.");
        LlamaPreparedGeneration.ValidateTextShape(request);
    }

    // Copy before settings/store callbacks in the optional read. No JSON translation or provider
    // operation is needed; text strings are immutable, and the schema owns its JsonElement copy.
    internal static ModelGenerationRequest Freeze(ModelGenerationRequest request, string modelId)
    {
        Require(request, modelId);
        var messages = request.Messages.Select(message => new ModelInputMessage(message.Role,
            Array.AsReadOnly(message.Content.Select(part => part is TextContentPart text
                ? (ContentPart)new TextContentPart(text.Text, text.Format)
                : throw new ArgumentException("Registered native inference requires text content.")).ToArray()))).ToArray();
        var frozen = request with { Messages = Array.AsReadOnly(messages), ResponseSchema = request.ResponseSchema?.Clone() };
        Require(frozen, modelId);
        return frozen;
    }

    internal static ModelGenerationRequest Freeze(ModelGenerationRequest request, string modelId,
        ModelExecutionPolicy selected)
    {
        Require(request, modelId, selected);
        var messages = request.Messages.Select(message => new ModelInputMessage(message.Role,
            Array.AsReadOnly(message.Content.Select(part => part is TextContentPart text
                ? (ContentPart)new TextContentPart(text.Text, text.Format)
                : throw new ArgumentException("Selected native inference requires text content.")).ToArray()))).ToArray();
        var frozen = request with { Messages = Array.AsReadOnly(messages), ResponseSchema = request.ResponseSchema?.Clone() };
        Require(frozen, modelId, selected);
        return frozen;
    }
}

// These are host-only admission facts sampled under the existing runtime lifecycle gate.
// The deterministic checks exercise rejection, not a simulated provider/physical release.
internal static class NativeRegistrationGuard
{
    internal static void Require(bool sameRuntime, bool sameInstance, bool alive,
        bool admissionOpen, bool resourcePaused, bool fenceOpen)
    {
        if (!sameRuntime) throw new InvalidOperationException("registered_native_runtime_changed");
        if (!sameInstance) throw new InvalidOperationException("registered_native_instance_changed");
        if (!alive) throw new InvalidOperationException("registered_native_instance_unavailable");
        if (!admissionOpen) throw new InvalidOperationException("model_admission_closed");
        if (resourcePaused) throw new InvalidOperationException("registered_native_resource_paused");
        if (!fenceOpen) throw new InvalidOperationException("registered_native_registration_closed");
    }
}

// This lifetime owns a borrowed registration view, never the ModelRuntime or native process.
// Closing prevents new callbacks/admission. Existing callbacks retain their own native leases.
internal sealed class NativeRegistrationFence : IAsyncDisposable
{
    private readonly object gate = new();
    private bool open = true;
    private int callbacks;
    private long observationRevision = 1;
    private TaskCompletionSource? idle;
    internal (bool Open, int Callbacks) Snapshot() { lock (gate) return (open, callbacks); }
    internal (bool Open, int Callbacks, long Revision) SnapshotVersioned()
    { lock (gate) return (open, callbacks, observationRevision); }
    internal IDisposable Enter()
    {
        lock (gate)
        {
            if (!open) throw new InvalidOperationException("registered_native_registration_closed");
            if (callbacks == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            checked { callbacks++; }
            observationRevision++;
            return new Callback(this);
        }
    }
    internal T WhileOpen<T>(Func<T> action)
    {
        lock (gate)
        {
            if (!open) throw new InvalidOperationException("registered_native_registration_closed");
            return action();
        }
    }
    internal void CloseAdmission() { lock (gate) { if (open) { open = false; observationRevision++; } } }
    internal Task WhenIdle() { lock (gate) return idle?.Task ?? Task.CompletedTask; }
    internal void CloseForRetirement()
    {
        lock (gate)
        {
            if (open) { open = false; observationRevision++; }
            if (callbacks != 0) throw new InvalidOperationException("registered_native_callbacks_active");
        }
    }
    public ValueTask DisposeAsync() { CloseForRetirement(); return ValueTask.CompletedTask; }
    private sealed class Callback(NativeRegistrationFence owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate)
            {
                if (--owner.callbacks == 0) owner.idle?.TrySetResult();
                owner.observationRevision++;
            }
        }
    }
}

public sealed partial class ModelRuntime
{
    private sealed record NativeRegistrationTarget(ModelRuntime Owner, string RuntimeGeneration,
        string ModelId, Instance Instance, string InstanceId, string[] StopSequences,
        NativeRegistrationFence Fence);

    // No caller-supplied instance/backend/owner IDs are admitted. The private composition
    // captures an already loaded object; this method never loads, probes HTTP or replaces it.
    internal Task<RegisteredNativeGenerationService> RegisterLoadedNativeTextAsync(
        string modelId, CancellationToken token) => RegisterLoadedNativeTextCoreAsync(modelId, token);

    private async Task<RegisteredNativeGenerationService> RegisterLoadedNativeTextCoreAsync(
        string modelId, CancellationToken token, WorkerExecutionSnapshot.ModelBinding? expected = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested(); ThrowIfOwnerRuntimeDisposed();
            var present = instances.TryGetValue(modelId, out var instance);
            NativeRegistrationGuard.Require(true, present, present && !HasExited(instance!),
                Accounting.Snapshot().AdmissionOpen, resourcePauseRequested, true);
            if (expected is not null)
                RequireNativeReadinessBinding(WorkerExecutionSnapshot.RemoteBinding(expected),
                    instance!.OwnerLaunch?.Binding ?? throw new InvalidOperationException("The loaded model has no verified launch binding."));
            return GetRegisteredNativeTextUnderLifecycle(modelId, instance!).Service;
        }
        finally { lifecycle.Release(); }
    }

    // Exactly one successful facet is published on the actual instance, under lifecycle.
    // Closed/faulted/quarantined facets remain tombstones for that same object.
    private NativeRegistrationLifetime GetRegisteredNativeTextUnderLifecycle(string modelId, Instance instance)
    {
        if (instance.RegisteredText is { } cached)
        {
            cached.RequireUsable(instance.Slots.Quarantined.Count != 0);
            return cached;
        }
        if (instance.Slots.Quarantined.Count != 0)
            throw new InvalidOperationException("registered_native_instance_quarantined");
        var launch = instance.OwnerLaunch ?? throw new InvalidOperationException("registered_native_launch_binding_unavailable");
        var admitted = launch.Binding ?? throw new InvalidOperationException("registered_native_launch_binding_unavailable");
        if (launch.ModelId != modelId || admitted.Id != modelId)
            throw new InvalidOperationException("registered_native_launch_binding_unavailable");
        var context = launch.Context.MeasuredContextTokens ?? instance.CapacityPlan.ContextTokens;
        if (context is < 512 or > LlamaPreparedGeneration.NativeMaximumContextTokens)
            throw new InvalidOperationException("registered_native_context_unavailable");
        var fence = new NativeRegistrationFence();
        var target = new NativeRegistrationTarget(this, ownerProcessGenerationId, modelId, instance,
            instance.Id, admitted.StopSequences.ToArray(), fence);
        var descriptor = new ModelAdapterDescriptor(LlamaPreparedGeneration.Adapter.AdapterId,
            LlamaPreparedGeneration.Adapter.Version, Array.AsReadOnly(new[] { "text" }), SupportsReasoning: false);
        var binding = new AdapterBinding("worker.local.llama.cpp", modelId, instance.Id,
            descriptor.AdapterId, descriptor.Version);
        var facade = descriptor with { SupportsReasoning = true };
        var support = NativeInstanceRuntimeSupport.TryCreate(launch, binding, facade);
        ModelExecutionPolicy? policy = support is null ? null : new(
            ModelExecutionPolicy.CurrentSchemaVersion, "cerebral.native.text", 1,
            support.Binding, ownerProcessGenerationId, ModelExecutionPolicy.NativeTextInputKind,
            ReasoningEnabled: false, 512, context, 1,
            Math.Min(context - LlamaPreparedGeneration.NativeMinimumInputTokens - AgentTokenLimits.ContextSafetyTokens,
                LlamaPreparedGeneration.NativeMaximumVisibleTokens),
            "cerebral.local-native-text-software/1");
        policy?.Validate();
        var registry = new AdapterRegistry();
        var ownership = Accounting.Begin(ModelOwnershipKind.CapabilityLease, modelId, requiresAdmission: false,
            role: ModelOwnershipRole.CachedNativeRegistration);
        try
        {
            ownership.Bind(instanceId: instance.Id);
            ownership.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
            var adapter = new RegisteredNativeAdapter(this, target, facade);
            RegisteredNativeSupportAdapter? supportedAdapter = support is null ? null : new(adapter, support, policy!);
            if (support is null)
                registry.Register(binding.BackendId, binding.ModelId, binding.InstanceId,
                    adapter, fence, [descriptor, LlamaPreparedReasoningGeneration.Adapter]);
            else
                registry.RegisterWithRuntimeSupport(binding.BackendId, binding.ModelId, binding.InstanceId,
                    supportedAdapter!, fence,
                    [descriptor, LlamaPreparedReasoningGeneration.Adapter], support.Binding, []);
            var service = new RegisteredNativeGenerationService(registry, binding, fence, context,
                ownerProcessGenerationId, Accounting.ProcessGenerationId, policy);
            var created = new NativeRegistrationLifetime(service, fence, ownership)
            { RuntimeSupport = support is null ? null : registry.RuntimeSupport(binding),
                RequestShape = supportedAdapter };
            instance.RegisteredText = created;
            return created;
        }
        catch
        {
            // No view was published, so no callback can own this registration.
            fence.CloseAdmission(); ownership.Complete(); throw;
        }
    }

    private async Task<Instance> AcquireRegisteredInstanceAsync(NativeRegistrationTarget target,
        string modelId, CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested(); ThrowIfOwnerRuntimeDisposed();
            // lifecycle -> owner shutdown/membership gate -> short registration gate.
            // Busy is published before any of those gates are released. Force shutdown may
            // still interrupt this admitted call; native stop/quarantine remains authoritative.
            // Registration retirement never enters lifecycle or unloads a model.
            lock (ownerSnapshotGate)
            {
                if (ownerRuntimeDisposed) throw new ObjectDisposedException(nameof(ModelRuntime));
                return target.Fence.WhileOpen(() =>
                {
                    var same = modelId == target.ModelId &&
                        instances.TryGetValue(modelId, out var current) &&
                        ReferenceEquals(current, target.Instance) && current.Id == target.InstanceId;
                    NativeRegistrationGuard.Require(ReferenceEquals(target.Owner, this) &&
                        target.RuntimeGeneration == ownerProcessGenerationId, same, !HasExited(target.Instance),
                        Accounting.Snapshot().AdmissionOpen, resourcePauseRequested, true);
                    Interlocked.Increment(ref target.Instance.Busy);
                    return target.Instance;
                });
            }
        }
        finally { lifecycle.Release(); }
    }

    private sealed class RegisteredNativeAdapter(ModelRuntime owner, NativeRegistrationTarget target,
        ModelAdapterDescriptor descriptor) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => descriptor;
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            RegisteredNativeTextRequestShape.Require(request, target.ModelId);
            var prepared = await owner.PrepareCoreAsync(request, token, target).ConfigureAwait(false);
            // Optional per-preparation observation only. Direct generation has no scope;
            // admission/counting/ownership remain with the existing native/registry paths.
            RegisteredWorkflowTelemetryScope.Capture(prepared);
            return prepared;
        }

        internal async Task<IPreparedModelGeneration> PrepareSelectedAsync(ModelGenerationRequest request,
            ModelExecutionPolicy selected, CancellationToken token)
        {
            RegisteredNativeTextRequestShape.Require(request, target.ModelId, selected);
            if (selected.Binding.InstanceId != target.InstanceId ||
                selected.RuntimeGenerationId != target.RuntimeGeneration)
                throw new InvalidOperationException("selected_native_policy_instance_changed");
            var prepared = await owner.PrepareCoreAsync(request, token, target, selected).ConfigureAwait(false);
            RegisteredWorkflowTelemetryScope.Capture(prepared);
            return prepared;
        }
    }

    // The optional facet is attached only when the exact loaded-instance declaration exists.
    // It adds no new request route; native preparation still belongs to the original adapter.
    private sealed class RegisteredNativeSupportAdapter(RegisteredNativeAdapter inner,
        ModelRuntimeSupportDescriptor support, ModelExecutionPolicy policy)
        : IModelRuntimeAdapter, IModelRuntimeSupportFacet, IRegisteredNativeRequestShapeFacet,
            IRegisteredNativePolicyExecutor
    {
        public ModelAdapterDescriptor Descriptor => inner.Descriptor;
        public ModelRuntimeSupportDescriptor RuntimeSupport => support;
        public ModelRuntimeSupportBinding RequestShapeBinding => support.Binding;
        public ModelExecutionPolicy ExecutionPolicy => policy;
        public void ValidateRequestShape(ModelGenerationRequest request)
        {
            RegisteredNativeTextRequestShape.Require(request, support.Binding.ModelId);
        }
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request,
            CancellationToken token) => inner.PrepareAsync(request, token);
        public Task<IPreparedModelGeneration> PrepareSelectedAsync(ModelGenerationRequest request,
            ModelExecutionPolicy selected, CancellationToken token)
        {
            if (selected != policy || support.Binding != selected.Binding)
                throw new InvalidOperationException("selected_native_policy_changed");
            return inner.PrepareSelectedAsync(request, policy, token);
        }
    }
}
