using System.Runtime.ExceptionServices;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker;

// Optional Worker-only composition/classification facets. They contain an already captured
// binding, never a deferred guess at a native instance, and confer no additional authority.
internal interface IRegisteredNativeWorkflowSource
{
    IRegisteredNativeWorkflowAdapter CreateWorkflowAdapter();
    IRegisteredNativeWorkflowAdapter CreateWorkflowAdapter(bool reasoning);
    // Existing registered sources remain v1-only unless their exact implementation opts in.
    IRegisteredNativeWorkflowAdapter CreatePolicyWorkflowAdapter() =>
        throw new InvalidOperationException("registered_native_policy_unavailable");
}
internal interface IRegisteredNativeWorkflowAdapter : IModelRuntimeAdapter
{
    AdapterBinding Binding { get; }
    int CapturedContextLimit { get; }
}
internal interface IRegisteredNativeWorkflowPreparation : IPreparedModelGeneration, IBoundGenerationTelemetry
{
    RegisteredWorkflowPreparationObservation Observe();
}
internal sealed record RegisteredWorkflowPreparationObservation(AdapterBinding Binding,
    PreparedModelMetadata Metadata, bool GenerationInvoked, bool LifetimeSettled,
    bool CleanupUnconfirmed, bool TelemetryAvailable, LeaseObservation? RegistryCleanup,
    string? PublisherFailureType);

// Typed private signal for Core's workflow cleanup classification. Invocation is conservative
// wrapper state, not proof of provider admission. Registry observations retain uncertainty.
internal sealed class RegisteredWorkflowCleanupException : IOException
{
    internal string Code => "registered_workflow_cleanup_unconfirmed";
    internal bool GenerationInvoked { get; }
    internal LeaseObservation? RegistryCleanup { get; }
    internal InstanceObservation Registry { get; }
    internal RegisteredWorkflowCleanupException(bool generationInvoked, LeaseObservation? cleanup,
        InstanceObservation registry, Exception inner)
        : base("Registered workflow preparation cleanup is unconfirmed.", inner)
    { GenerationInvoked = generationInvoked; RegistryCleanup = cleanup; Registry = registry; }
}

// Fresh async-flow scope per Prepare. Only a safe optional telemetry delegate and immutable
// metadata are retained; no raw preparation, caller identity, or adapter-global last lease.
internal sealed class RegisteredWorkflowTelemetryScope : IDisposable
{
    private static readonly AsyncLocal<RegisteredWorkflowTelemetryScope?> current = new();
    private readonly RegisteredWorkflowTelemetryScope? previous;
    private readonly object gate = new();
    private PreparedModelMetadata? metadata;
    private Action<GenerationTelemetryBinding, Action<GenerationTelemetrySample>>? binder;
    private bool detached;
    private int disposed;
    private RegisteredWorkflowTelemetryScope() { previous = current.Value; current.Value = this; }
    internal static RegisteredWorkflowTelemetryScope Enter() => new();
    internal static void Capture(IPreparedModelGeneration prepared)
    {
        var scope = current.Value;
        if (scope is null) return; // Direct diagnostics retain their original behavior.
        try
        {
            if (prepared is not IBoundGenerationTelemetry telemetry) return;
            var measured = prepared.Metadata;
            lock (scope.gate)
            {
                if (scope.detached || scope.binder is not null) return;
                scope.metadata = measured;
                scope.binder = telemetry.BindTelemetry;
            }
        }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error))
        { /* Optional observation cannot alter provider admission or its returned preparation. */ }
    }
    internal RegisteredWorkflowTelemetryForwarder? Detach(PreparedModelMetadata validated)
    {
        lock (gate)
        {
            detached = true;
            var forward = metadata == validated && binder is not null
                ? new RegisteredWorkflowTelemetryForwarder(binder) : null;
            binder = null; metadata = null;
            return forward;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        current.Value = previous;
        lock (gate) { detached = true; binder = null; metadata = null; }
    }
}
internal sealed class RegisteredWorkflowTelemetryForwarder(
    Action<GenerationTelemetryBinding, Action<GenerationTelemetrySample>> bind)
{
    private int available = 1;
    internal bool Available => Volatile.Read(ref available) != 0;
    internal void Bind(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer)
    {
        if (!Available) return;
        try { bind(binding, observer); }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error))
        {
            Interlocked.Exchange(ref available, 0);
            // Failure is visible as unavailable; it cannot alter inference/ownership.
        }
    }
}

internal sealed partial class RegisteredNativeGenerationService
{
    public IRegisteredNativeWorkflowAdapter CreateWorkflowAdapter() => CreateWorkflowAdapter(false);

    public IRegisteredNativeWorkflowAdapter CreateWorkflowAdapter(bool reasoning)
    {
        if (!fence.Snapshot().Open) throw new InvalidOperationException("registered_native_registration_closed");
        return new WorkflowAdapter(this, reasoning);
    }

    public IRegisteredNativeWorkflowAdapter CreatePolicyWorkflowAdapter()
    {
        if (!fence.Snapshot().Open || LocalExecutionPolicy is not { } policy)
            throw new InvalidOperationException("registered_native_policy_unavailable");
        return new PolicyWorkflowAdapter(this, policy);
    }

    private static bool IsCleanupUnconfirmed(Exception error) =>
        error is RegisteredWorkflowCleanupException || error is RegistryException
        {
            Code: "lease_cleanup_unconfirmed" or "rejected_preparation_cleanup_unconfirmed" or
                "cleanup_unconfirmed" or "instance_cleanup_unconfirmed"
        };

    private class WorkflowAdapter : IRegisteredNativeWorkflowAdapter
    {
        private readonly RegisteredNativeGenerationService service;
        private readonly bool reasoning;
        private readonly ModelExecutionPolicy? selectedPolicy;
        public AdapterBinding Binding { get; }
        public int CapturedContextLimit => service.contextTokens;
        public ModelAdapterDescriptor Descriptor { get; }
        internal WorkflowAdapter(RegisteredNativeGenerationService service, bool reasoning,
            ModelExecutionPolicy? selectedPolicy = null)
        {
            this.service = service; this.reasoning = reasoning; this.selectedPolicy = selectedPolicy;
            Descriptor = reasoning ? LlamaPreparedReasoningGeneration.Adapter :
                new(service.Binding.AdapterId, service.Binding.AdapterVersion,
                    Array.AsReadOnly(new[] { "text" }), SupportsReasoning: false);
            Binding = service.Binding with { AdapterId = Descriptor.AdapterId, AdapterVersion = Descriptor.Version };
            // Pure registry selection verifies this exact facet exists. No native preparation,
            // additional owner or service-wide binding change occurs during selection.
            _ = service.registry.CreateAdapter(Binding, new(["text"], Reasoning: reasoning));
        }

        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (selectedPolicy is null) request.Validate(); else request.Validate(selectedPolicy);
            token.ThrowIfCancellationRequested();
            if (selectedPolicy is not null &&
                (service.LocalExecutionPolicy != selectedPolicy || selectedPolicy.Binding.InstanceId != Binding.InstanceId))
                throw new InvalidOperationException("registered_native_policy_changed");
            if (request.ModelId != Binding.ModelId)
                throw new InvalidOperationException("registered_workflow_model_mismatch");
            if ((request.ReasoningTokens != 0) != reasoning)
                throw new NotSupportedException("Registered workflow reasoning mode does not match its captured facet.");
            if (request.Messages.SelectMany(message => message.Content).Any(part => part is not TextContentPart))
                throw new NotSupportedException("Registered workflow inference supports text content only.");
            // Snapshot the complete typed request. Only the context ceiling narrows to captured
            // capacity; share/clamping/schema/messages and measured-limit exceptions survive.
            request = CerebralJson.Read<ModelGenerationRequest>(CerebralJson.Element(request));
            request = request with { ContextLimit = Math.Min(request.ContextLimit, CapturedContextLimit) };
            if (selectedPolicy is null) request.Validate(); else request.Validate(selectedPolicy);
            // The optional same-handler facet performs the native pure shape check before
            // callback ownership. Legacy registrations use that same validator directly.
            if (!service.registry.TryValidateRequestShape(Binding, request, selectedPolicy))
            {
                if (selectedPolicy is not null)
                    throw new InvalidOperationException("registered_native_policy_handler_missing");
                RegisteredNativeTextRequestShape.Require(request, Binding.ModelId);
            }
            var callback = service.fence.Enter();
            var owner = new AdapterCommandDispatch(service.registry);
            AdapterCommandDispatch.Preparation? handle = null; var transferred = false;
            using var telemetry = RegisteredWorkflowTelemetryScope.Enter();
            using var draft = RegisteredWorkflowDraftScope.Enter();
            try
            {
                // Preserve genuine ambient workflow Invocation/Execution unchanged.
                handle = selectedPolicy is null
                    ? await owner.PrepareAsync(Binding, new(["text"], Reasoning: reasoning), request, token).ConfigureAwait(false)
                    : await owner.PrepareSelectedAsync(Binding, new(["text"], Reasoning: false),
                        request, selectedPolicy, token).ConfigureAwait(false);
                if (selectedPolicy is not null) LocalModelExecutionPolicy.Require(handle.Lease, selectedPolicy);
                var forwarder = telemetry.Detach(handle.Metadata);
                var draftForwarder = draft.Detach(handle.Metadata);
                WorkflowPreparation result = selectedPolicy is null
                    ? new WorkflowPreparation(service, owner, handle, callback, forwarder, draftForwarder)
                    : new PolicyWorkflowPreparation(service, owner, handle, callback, forwarder,
                        draftForwarder, selectedPolicy);
                transferred = true;
                return result;
            }
            catch (Exception error)
            {
                Exception? releaseError = null;
                if (handle is not null)
                {
                    try { await owner.ReleaseAsync(handle, Binding).ConfigureAwait(false); }
                    catch (Exception cleanup) { releaseError = cleanup; }
                }
                if (releaseError is not null || IsCleanupUnconfirmed(error))
                    throw new RegisteredWorkflowCleanupException(false, handle?.Observe(),
                        service.registry.Observe(Binding), releaseError ?? error);
                throw; // In particular, preserve ModelContextLimitException.Metadata.
            }
            finally { if (!transferred) callback.Dispose(); }
        }
    }

    private sealed class PolicyWorkflowAdapter(RegisteredNativeGenerationService service,
        ModelExecutionPolicy selected)
        : WorkflowAdapter(service, false, selected), IModelExecutionPolicyFacet
    {
        public ModelExecutionPolicy ExecutionPolicy { get; } = selected;
    }

    private class WorkflowPreparation(RegisteredNativeGenerationService service,
        AdapterCommandDispatch owner, AdapterCommandDispatch.Preparation handle, IDisposable callback,
        RegisteredWorkflowTelemetryForwarder? telemetry, RegisteredWorkflowDraftForwarder? draft)
        : IRegisteredNativeWorkflowPreparation, IBoundGenerationDraft
    {
        private readonly object gate = new();
        private Task<ModelGenerationResult>? generation;
        private Task? disposal;
        private bool invoked;
        private RegisteredWorkflowCleanupException? cleanupFailure;
        private RegisteredNativePublisher? publisher;
        public PreparedModelMetadata Metadata { get; } = handle.Metadata;
        private AdapterBinding Binding => handle.Binding;

        public RegisteredWorkflowPreparationObservation Observe()
        {
            bool started, settled, uncertain; string? publicationFailure;
            lock (gate)
            {
                // Both public tasks must settle; a cleanup attempt alone says nothing
                // about native return. Sample registry state outside this wrapper gate.
                started = invoked;
                settled = disposal?.IsCompleted == true && (generation is null || generation.IsCompleted);
                uncertain = cleanupFailure is not null;
                publicationFailure = publisher?.FailureType;
            }
            return new(Binding, Metadata, started, settled, uncertain,
                telemetry?.Available == true, handle.Observe(), publicationFailure);
        }
        public void BindTelemetry(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer)
        {
            lock (gate) { if (disposal is not null) return; }
            telemetry?.Bind(binding, observer);
        }
        public bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer)
        {
            lock (gate)
            {
                if (invoked || disposal is not null) return false;
                return draft?.TryBind(binding, observer) == true;
            }
        }
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            TaskCompletionSource<ModelGenerationResult> done; RegisteredNativePublisher output;
            lock (gate)
            {
                if (disposal is not null) throw new InvalidOperationException("registered_workflow_preparation_disposed");
                if (invoked) throw new InvalidOperationException("registered_workflow_preparation_replayed");
                invoked = true; output = new RegisteredNativePublisher(onText); publisher = output;
                done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                generation = done.Task; // Publish before any provider getter or synchronous callback.
            }
            _ = GenerateCoreAsync(done, output, token);
            return done.Task;
        }
        private async Task GenerateCoreAsync(TaskCompletionSource<ModelGenerationResult> done,
            RegisteredNativePublisher output, CancellationToken token)
        {
            try
            {
                ModelGenerationResult? result = null; ExceptionDispatchInfo? primary = null;
                try { result = await owner.GenerateAsync(handle, Binding, output.Invoke, token).ConfigureAwait(false); }
                catch (Exception error) { primary = ExceptionDispatchInfo.Capture(error); }
                if (output.FailureType is not null)
                {
                    try { await DisposeAsync().ConfigureAwait(false); }
                    catch (RegisteredWorkflowCleanupException)
                    { /* Retained by Observe/Dispose; original publisher remains the primary failure. */ }
                    output.ThrowIfFailed();
                }
                primary?.Throw();
                if (result is null) throw new InvalidOperationException("registered_workflow_terminal_missing");
                done.TrySetResult(result); // Preserve typed Stop/Length/Cancelled/Unknown and metadata.
            }
            catch (OperationCanceledException error) { done.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { done.TrySetException(error); }
        }
        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? start = null; Task task;
            lock (gate)
            {
                if (disposal is null)
                {
                    start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    disposal = start.Task;
                }
                task = disposal;
            }
            if (start is not null) _ = DisposeCoreAsync(start);
            return new(task);
        }
        private async Task DisposeCoreAsync(TaskCompletionSource done)
        {
            Exception? error = null;
            try { await owner.ReleaseAsync(handle, Binding).ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                lock (gate)
                {
                    cleanupFailure = new RegisteredWorkflowCleanupException(invoked, handle.Observe(),
                        service.registry.Observe(Binding), cleanup);
                    error = cleanupFailure;
                }
            }
            finally { draft?.Dispose(); callback.Dispose(); } // Actual native/registry return remains separately observed.
            if (error is not null) done.TrySetException(error); else done.TrySetResult();
        }
    }

    private sealed class PolicyWorkflowPreparation(RegisteredNativeGenerationService service,
        AdapterCommandDispatch owner, AdapterCommandDispatch.Preparation handle, IDisposable callback,
        RegisteredWorkflowTelemetryForwarder? telemetry, RegisteredWorkflowDraftForwarder? draft,
        ModelExecutionPolicy selected)
        : WorkflowPreparation(service, owner, handle, callback, telemetry, draft), IModelExecutionPolicyFacet
    {
        public ModelExecutionPolicy ExecutionPolicy { get; } = selected;
    }
}
