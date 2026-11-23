using System.Runtime.ExceptionServices;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker;

// Core owns only the temporary readiness lease; Engine owns its cached registration.
internal interface IWorkflowReadinessLease : IAsyncDisposable
{
    IRegisteredNativeGenerationService Registration { get; }
}

internal sealed class WorkflowReadinessCleanupException(Exception? primary, Exception? cleanup)
    : IOException("Workflow readiness cleanup is unconfirmed; inspect its exact owned attempt.", cleanup ?? primary)
{
    internal Exception? PrimaryFailure { get; } = primary;
    internal Exception? CleanupFailure { get; } = cleanup;
}

// One configured model, one exact full-binding cohort at a time. A different accepted
// binding cannot bypass busy or unconfirmed ownership by creating a new host.
internal sealed class ConfiguredWorkflowModelCoordinator : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly string modelId;
    private readonly Func<RemotePreparedModelBinding, CancellationToken, Task<IWorkflowReadinessLease>> workflowReadinessFactory;
    private readonly Func<RemotePreparedModelBinding, AdapterBinding, CancellationToken, Task> workflowBindingValidator;
    private readonly CancellationTokenSource stopping = new();
    private WorkflowReadinessCohort<IWorkflowReadinessLease>? workflowReadiness;
    private RemotePreparedModelBinding? workflowReadinessBinding;
    private Exception? workflowPrimaryFailure, workflowCleanupFailure;
    private int workflowActiveOwners, workflowUnconfirmedOwners;
    private bool closed;
    private TaskCompletionSource idle = Completed();
    private Task? disposal;

    internal ConfiguredWorkflowModelCoordinator(string modelId,
        Func<RemotePreparedModelBinding, CancellationToken, Task<IWorkflowReadinessLease>> acquire,
        Func<RemotePreparedModelBinding, AdapterBinding, CancellationToken, Task> validate)
    { this.modelId = modelId; workflowReadinessFactory = acquire; workflowBindingValidator = validate; }

    internal IModelRuntimeAdapter ForAccepted(RemotePreparedModelBinding binding, bool reasoning)
    {
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(ConfiguredWorkflowModelCoordinator));
            return new DeferredWorkflowAdapter(this, binding, reasoning);
        }
    }

    private async Task<ILocalModelPolicyScope> SelectPolicyScopeAsync(RemotePreparedModelBinding expected,
        DeferredWorkflowAdapter admission, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ownership = ReserveWorkflowReadiness(expected, admission);
        Exception? primary = null, cleanup = null;
        try
        {
            await ownership.Borrow.AcquireAsync(token).ConfigureAwait(false);
            if (ownership.Borrow.Lease.Registration is not IRegisteredNativeWorkflowSource source)
                throw new InvalidOperationException("configured_registered_workflow_source_unavailable");
            var selected = source.CreatePolicyWorkflowAdapter();
            if (selected is not IModelExecutionPolicyFacet facet)
                throw new InvalidOperationException("configured_selected_policy_missing");
            var policy = facet.ExecutionPolicy;
            policy.Validate();
            RequireSelectedWorkflowFacet(expected, selected.Binding, selected.Descriptor, reasoning: false);
            if (policy.Binding.ModelId != expected.Id || policy.Binding.InstanceId != selected.Binding.InstanceId ||
                policy.Binding.AdapterId != selected.Binding.AdapterId ||
                policy.Binding.AdapterVersion != selected.Binding.AdapterVersion)
                throw new InvalidOperationException("configured_selected_policy_binding_mismatch");
            await workflowBindingValidator(expected, selected.Binding, token).ConfigureAwait(false);
            return new SelectedPolicyScope(this, expected, selected, policy, ownership.Borrow);
        }
        catch (Exception error) { primary = error; }
        try { await ownership.Borrow.ReleaseAsync(primary).ConfigureAwait(false); }
        catch (Exception error) { cleanup = error; }
        ReleaseWorkflowOwner(primary, cleanup);
        if (cleanup is not null) throw new WorkflowReadinessCleanupException(primary, cleanup);
        ExceptionDispatchInfo.Capture(primary!).Throw();
        throw new InvalidOperationException("Unreachable policy selection outcome.");
    }

    internal WorkflowReadinessCohortSnapshot? ObserveReadiness()
    { lock (gate) return workflowReadiness?.Observe(); }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult(); return done;
    }

    private static bool WorkflowCleanupUnconfirmed(Exception error)
    {
        var pending = new Stack<(Exception Error, int Depth)>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push((error, 0));
        while (pending.Count != 0 && seen.Count < 32)
        {
            var (current, depth) = pending.Pop();
            if (!seen.Add(current)) continue;
            if (depth > 8) return true;
            if (current is WorkflowReadinessCleanupException or RegisteredWorkflowCleanupException) return true;
            if (current is IRegisteredNativeReadinessFailure)
            {
                try { if (!ConfirmsOwnedWorkflowSettlement(current)) return true; }
                catch { return true; } // A throwing owned getter cannot establish settled cleanup.
            }
            else if (current is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count > 16) return true;
                foreach (var inner in aggregate.InnerExceptions.Take(16)) pending.Push((inner, depth + 1));
            }
        }
        return pending.Count != 0;

    }

    // This is used only outside host/cohort gates. A known readiness branch proves its own
    // sealed handles, never an additional unclassified cleanup sibling. Primary ordinary errors
    // retain their meaning; independent cleanup needs its own exact owned typed proof.
    private static bool ConfirmsOwnedWorkflowSettlement(Exception error)
    {
        var pending = new Stack<(Exception Error, int Depth, bool RequiresOwnedProof)>();
        var primarySeen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var cleanupSeen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push((error, 0, true));
        var visited = 0;
        var ownedProof = false;
        while (pending.Count != 0 && visited < 32)
        {
            var (current, depth, requiresProof) = pending.Pop();
            if (!(requiresProof ? cleanupSeen : primarySeen).Add(current)) continue;
            visited++;
            if (depth > 8) return false;
            if (current is IRegisteredNativeReadinessFailure readiness)
            {
                if (!readiness.Observation.AllowsFreshAcceptedAttempt) return false;
                ownedProof = true;
                if (readiness.CleanupException is { } cleanup)
                    pending.Push((cleanup, depth + 1, true));
                pending.Push((readiness.OriginalException, depth + 1, false));
            }
            else if (current is WorkflowReadinessCleanupException workflow)
            {
                if (workflow.CleanupFailure is not { } cleanup) return false;
                pending.Push((cleanup, depth + 1, true));
                if (workflow.PrimaryFailure is { } primary)
                    pending.Push((primary, depth + 1, false));
            }
            else if (current is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count > 16) return false;
                foreach (var inner in aggregate.InnerExceptions.Take(16).Reverse())
                    pending.Push((inner, depth + 1, requiresProof));
            }
            else if (current is RegisteredWorkflowCleanupException || requiresProof) return false;
        }
        return pending.Count == 0 && ownedProof;
    }

    private (WorkflowReadinessCohort<IWorkflowReadinessLease> Cohort,
        WorkflowReadinessCohort<IWorkflowReadinessLease>.Borrow Borrow) ReserveWorkflowReadiness(
        RemotePreparedModelBinding expected, DeferredWorkflowAdapter admission)
    {
        WorkflowReadinessCohort<IWorkflowReadinessLease> retained;
        WorkflowReadinessCohort<IWorkflowReadinessLease>.SettlementProbe probe;
        lock (admission.AdmissionGate)
        lock (gate)
        {
            admission.RequireUsableUnderGate();
            RequireWorkflowAdmissionUnderGate(expected);
            if (workflowReadiness is null || workflowReadiness.CanReplace)
                return ReserveWorkflowReadinessUnderGate(expected);
            if (!workflowReadiness.Observe().CleanupUnconfirmed)
                return ReserveWorkflowReadinessUnderGate(expected);
            // A new request cannot confirm uncertainty while a previous workflow still owns
            // preparation cleanup.
            if (workflowActiveOwners != 0)
                throw new InvalidOperationException("configured_workflow_cleanup_pending");
            retained = workflowReadiness;
            probe = retained.ObserveSettlement();
        }

        var confirmation = probe.Evaluate(ConfirmsOwnedWorkflowSettlement);
        lock (admission.AdmissionGate)
        lock (gate)
        {
            admission.RequireUsableUnderGate();
            // The typed getter could run concurrently with stop or another new request. Applying
            // its proof is callback-free and checks the exact cohort/version/cause set again.
            RequireWorkflowAdmissionUnderGate(expected);
            if (confirmation is null || !ReferenceEquals(retained, workflowReadiness) ||
                workflowActiveOwners != 0 || !retained.TryConfirmSettlement(confirmation))
                throw new InvalidOperationException("configured_workflow_cleanup_pending");
            return ReserveWorkflowReadinessUnderGate(expected);
        }
    }

    private void RequireWorkflowAdmissionUnderGate(RemotePreparedModelBinding expected)
    {
        if (closed) throw new InvalidOperationException("configured_registered_admission_closed");
        if (modelId != expected.Id)
            throw new InvalidOperationException("configured_registered_model_mismatch");
        if (workflowUnconfirmedOwners != 0)
            throw new InvalidOperationException("configured_workflow_cleanup_pending");
    }

    private (WorkflowReadinessCohort<IWorkflowReadinessLease> Cohort,
        WorkflowReadinessCohort<IWorkflowReadinessLease>.Borrow Borrow) ReserveWorkflowReadinessUnderGate(
        RemotePreparedModelBinding expected)
    {
        if (workflowReadiness is null || workflowReadiness.CanReplace)
        {
            workflowReadiness = new(token => workflowReadinessFactory(expected, token),
                isCleanupUnconfirmed: WorkflowCleanupUnconfirmed);
            workflowReadinessBinding = expected;
        }
        else if (workflowReadinessBinding is null ||
            !SameLoadedWorkflowBinding(workflowReadinessBinding, expected))
            throw new InvalidOperationException("configured_workflow_readiness_binding_busy");

        var borrow = workflowReadiness.ReserveBorrow();
        workflowActiveOwners++;
        if (workflowActiveOwners == 1) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return (workflowReadiness, borrow);
    }
    private void ReleaseWorkflowOwner(Exception? primary = null, Exception? unconfirmedCleanup = null)
    {
        lock (gate)
        {
            if (unconfirmedCleanup is not null)
            {
                workflowPrimaryFailure ??= primary;
                workflowCleanupFailure ??= unconfirmedCleanup;
                checked { workflowUnconfirmedOwners++; }
            }
            workflowActiveOwners--;
            if (workflowActiveOwners == 0) idle.TrySetResult();
        }
    }

    private async Task<IPreparedModelGeneration> PrepareWorkflowAsync(RemotePreparedModelBinding expected,
        ModelGenerationRequest request, bool reasoning, CancellationToken token, DeferredWorkflowAdapter admission)
    {
        token.ThrowIfCancellationRequested();
        var ownership = ReserveWorkflowReadiness(expected, admission);
        CancellationTokenSource? lifetime = null;
        IPreparedModelGeneration? prepared = null;
        Exception? primary = null, cleanup = null;
        var preparedCleanupUnconfirmed = false;
        try
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
            await ownership.Borrow.AcquireAsync(lifetime.Token).ConfigureAwait(false);
            var service = ownership.Borrow.Lease.Registration;
            if (service is not IRegisteredNativeWorkflowSource source)
                throw new InvalidOperationException("configured_registered_workflow_source_unavailable");
            var adapter = source.CreateWorkflowAdapter(reasoning)
                ?? throw new InvalidOperationException("configured_registered_workflow_adapter_missing");
            // The cached service retains its default-off binding. Admission uses this immutable
            // selected facet, including its declared identity, before any provider preparation.
            var selectedBinding = adapter.Binding;
            RequireSelectedWorkflowFacet(expected, selectedBinding, adapter.Descriptor, reasoning);
            // MaximumContextTokens remains an immutable model-binding field. Effective fitted
            // width is separate prepared metadata and does not weaken this exact comparison.
            await workflowBindingValidator(expected, selectedBinding, lifetime.Token).ConfigureAwait(false);
            prepared = await adapter.PrepareAsync(request, lifetime.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("configured_workflow_preparation_missing");
            RequireSelectedWorkflowMetadata(selectedBinding, prepared.Metadata, request.ReasoningTokens);

            // Actual prepared ownership exists before the shared residency pin can retire.
            // The last borrower waits the exact pin cleanup, including a typed failure.
            await ownership.Borrow.ReleaseAsync().ConfigureAwait(false);
            HostWorkflowPreparation owner = prepared is IRegisteredNativeWorkflowPreparation observed
                ? new ObservedHostWorkflowPreparation(this, observed, lifetime)
                : new HostWorkflowPreparation(this, prepared, lifetime);
            prepared = null;
            lifetime = null;
            return owner;
        }
        catch (Exception error) { primary = error; }

        // Every failed transfer settles its actual prepared owner before withdrawing interest.
        // Preserve the first primary failure separately from a later pin/preparation cleanup error.
        if (prepared is not null)
        {
            try { await prepared.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanup = error; preparedCleanupUnconfirmed = true; }
        }
        try
        {
            await ownership.Borrow.ReleaseAsync(preparedCleanupUnconfirmed
                ? new WorkflowReadinessCleanupException(primary, cleanup) : primary).ConfigureAwait(false);
        }
        catch (Exception error) { cleanup ??= error; }
        try { lifetime?.Dispose(); }
        catch (Exception error) { cleanup ??= error; preparedCleanupUnconfirmed = true; }

        var unconfirmed = preparedCleanupUnconfirmed || WorkflowCleanupUnconfirmed(primary!) ||
            cleanup is not null && WorkflowCleanupUnconfirmed(cleanup) ||
            ownership.Cohort.Observe().CleanupUnconfirmed;
        var ownedCleanup = cleanup ?? ownership.Cohort.CleanupException ??
            ownership.Cohort.CancellationException ?? ownership.Cohort.PrimaryException ?? primary!;
        // Cohort-owned readiness uncertainty is retained by that exact cohort. Only an
        // independent preparation/lifetime cleanup failure creates an additional host blocker.
        ReleaseWorkflowOwner(primary, preparedCleanupUnconfirmed ? cleanup ?? primary : null);
        if (unconfirmed) throw new WorkflowReadinessCleanupException(primary, ownedCleanup);
        ExceptionDispatchInfo.Capture(primary!).Throw();
        throw new InvalidOperationException("Unreachable readiness outcome.");
    }

    private static void RequireSelectedWorkflowFacet(RemotePreparedModelBinding expected,
        AdapterBinding binding, ModelAdapterDescriptor descriptor, bool reasoning)
    {
        var selected = reasoning ? LlamaPreparedReasoningGeneration.Adapter : LlamaPreparedGeneration.Adapter;
        if (binding is null || descriptor is null || binding.BackendId != "worker.local.llama.cpp" ||
            binding.ModelId != expected.Id || string.IsNullOrWhiteSpace(binding.InstanceId) ||
            descriptor.AdapterId != selected.AdapterId || descriptor.Version != selected.Version ||
            descriptor.SupportsReasoning != reasoning || descriptor.ContentCapabilities is not { Count: 1 } ||
            descriptor.ContentCapabilities[0] != "text" || binding.AdapterId != descriptor.AdapterId ||
            binding.AdapterVersion != descriptor.Version)
            throw new InvalidOperationException("configured_workflow_selected_facet_mismatch");
    }

    private static void RequireSelectedWorkflowMetadata(AdapterBinding binding, PreparedModelMetadata metadata,
        int reasoningTokens)
    {
        if (metadata is null || metadata.ModelId != binding.ModelId || metadata.InstanceId != binding.InstanceId ||
            metadata.AdapterId != binding.AdapterId || metadata.AdapterVersion != binding.AdapterVersion ||
            metadata.ReasoningReserveTokens != reasoningTokens)
            throw new InvalidOperationException("configured_workflow_selected_metadata_mismatch");
    }

    internal static void RequireLoadedWorkflowBinding(RemotePreparedModelBinding expected,
        OwnerRuntimeModelObservation observed, AdapterBinding captured)
    {
        if (observed.InstanceState != "present" || observed.LaunchBindingState != "captured" ||
            observed.InstanceId is null || observed.InstanceId != captured.InstanceId ||
            observed.ModelId != expected.Id || captured.ModelId != expected.Id ||
            observed.LaunchBinding is not { } launched || !SameLoadedWorkflowBinding(expected, launched))
            throw new InvalidOperationException("configured_workflow_loaded_binding_mismatch");
    }

    internal static bool SameLoadedWorkflowBinding(RemotePreparedModelBinding expected, RemotePreparedModelBinding launched)
    {
        expected.Validate(); launched.Validate();
        // These are local model paths, using the same full-path and Windows case semantics
        // as ModelRuntime's local file selection. No provider I/O or payload attestation.
        static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left),
            Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        static bool SameOptionalPath(string? left, string? right) =>
            left is null || right is null ? left == right : SamePath(left, right);
        return expected.Id == launched.Id && SamePath(expected.Path, launched.Path) &&
            string.Equals(expected.ChatTemplate, launched.ChatTemplate, StringComparison.Ordinal) &&
            expected.StopSequences.SequenceEqual(launched.StopSequences, StringComparer.Ordinal) &&
            expected.MaximumContextTokens == launched.MaximumContextTokens &&
            expected.ParallelRequests == launched.ParallelRequests && expected.GpuLayers == launched.GpuLayers &&
            SameOptionalPath(expected.VisionProjectorPath, launched.VisionProjectorPath);
    }
    private sealed class DeferredWorkflowAdapter : IConfiguredLocalWorkflowAdapter, ILocalModelPolicySelection
    {
        private readonly ConfiguredWorkflowModelCoordinator host;
        private readonly RemotePreparedModelBinding expected;
        private readonly bool reasoning;
        public ModelAdapterDescriptor Descriptor { get; }
        internal DeferredWorkflowAdapter(ConfiguredWorkflowModelCoordinator host, RemotePreparedModelBinding expected,
            bool reasoning)
        {
            this.host = host; this.expected = expected; this.reasoning = reasoning;
            Descriptor = new("cerebral.configured.registered-workflow", "1.0.0",
                Array.AsReadOnly(new[] { "text" }), SupportsReasoning: reasoning);
        }
        internal readonly object AdmissionGate = new();
        private ExceptionDispatchInfo? failedReadiness;

        // Proxy -> host -> cohort is the only nested gate order. Retaining a failure and
        // reserving/replacing a cohort are atomic with respect to this accepted handler.
        internal void RequireUsableUnderGate() => failedReadiness?.Throw();

        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            lock (AdmissionGate) RequireUsableUnderGate();
            ArgumentNullException.ThrowIfNull(request); request.Validate(); token.ThrowIfCancellationRequested();
            if (request.ModelId != expected.Id || (request.ReasoningTokens != 0) != reasoning ||
                request.Messages.SelectMany(message => message.Content).Any(part => part is not TextContentPart))
                throw new NotSupportedException("Configured registered workflow preparation must match its selected text model and reasoning mode.");
            // Snapshot before waiting for capture or a runtime snapshot. Mutable caller lists and
            // JsonDocument ownership must not change the request across readiness acquisition.
            var frozen = CerebralJson.Read<ModelGenerationRequest>(CerebralJson.Element(request));
            return PrepareCoreAsync(frozen, token);
        }

        public Task<ILocalModelPolicyScope> SelectAsync(CancellationToken token)
        {
            if (reasoning) throw new InvalidOperationException("Reasoning cannot select local text policy.");
            lock (AdmissionGate) RequireUsableUnderGate();
            return host.SelectPolicyScopeAsync(expected, this, token);
        }

        private async Task<IPreparedModelGeneration> PrepareCoreAsync(ModelGenerationRequest frozen, CancellationToken token)
        {
            try
            {
                return await host.PrepareWorkflowAsync(expected, frozen, reasoning, token, this).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IRegisteredNativeReadinessFailure or
                WorkflowReadinessCleanupException or RegisteredWorkflowCleanupException)
            {
                // Ordinary context-limit/measurement probes may reuse this proxy. An owned
                // readiness failure requires a distinct newly accepted handler/new proxy.
                lock (AdmissionGate) failedReadiness ??= ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
    }

    private sealed class SelectedPolicyScope : ILocalModelPolicyScope, IConfiguredLocalWorkflowAdapter, IModelExecutionPolicyFacet
    {
        private readonly ConfiguredWorkflowModelCoordinator host;
        private readonly RemotePreparedModelBinding expected;
        private readonly IRegisteredNativeWorkflowAdapter selected;
        private readonly WorkflowReadinessCohort<IWorkflowReadinessLease>.Borrow borrow;
        private readonly SemaphoreSlim serial = new(1, 1);
        private bool closed, preparationActive, cleanupFault;
        private Exception? preparationPrimaryFailure, preparationCleanupFailure;

        internal SelectedPolicyScope(ConfiguredWorkflowModelCoordinator host, RemotePreparedModelBinding expected,
            IRegisteredNativeWorkflowAdapter selected, ModelExecutionPolicy policy,
            WorkflowReadinessCohort<IWorkflowReadinessLease>.Borrow borrow)
        { this.host = host; this.expected = expected; this.selected = selected; this.borrow = borrow; Policy = policy; }

        public ModelExecutionPolicy Policy { get; }
        public ModelExecutionPolicy ExecutionPolicy => Policy;
        public IModelRuntimeAdapter Adapter => this;
        public ModelAdapterDescriptor Descriptor => selected.Descriptor;

        public async Task RequireCurrentAsync(CancellationToken token)
        {
            await serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (closed || cleanupFault) throw new InvalidOperationException("selected_policy_scope_unavailable");
                LocalModelExecutionPolicy.Require(selected, Policy);
                await host.workflowBindingValidator(expected, selected.Binding, token).ConfigureAwait(false);
                LocalModelExecutionPolicy.Require(selected, Policy);
            }
            finally { serial.Release(); }
        }

        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            await serial.WaitAsync(token).ConfigureAwait(false);
            IPreparedModelGeneration? prepared = null;
            CancellationTokenSource? lifetime = null;
            var ownsPreparation = false;
            try
            {
                if (closed || preparationActive || cleanupFault)
                    throw new InvalidOperationException("selected_policy_scope_unavailable");
                preparationActive = true;
                ownsPreparation = true;
                LocalModelExecutionPolicy.Require(selected, Policy);
                request.Validate(Policy);
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, host.stopping.Token);
                await host.workflowBindingValidator(expected, selected.Binding, lifetime.Token).ConfigureAwait(false);
                prepared = await selected.PrepareAsync(request, lifetime.Token).ConfigureAwait(false);
                LocalModelExecutionPolicy.Require(prepared, Policy);
                RequireSelectedWorkflowMetadata(selected.Binding, prepared.Metadata, reasoningTokens: 0);
                if (prepared is not IRegisteredNativeWorkflowPreparation observed)
                    throw new InvalidOperationException("selected_policy_preparation_observation_missing");
                lock (host.gate)
                {
                    if (host.closed) throw new InvalidOperationException("configured_registered_admission_closed");
                    host.workflowActiveOwners++;
                }
                var owner = new ObservedHostWorkflowPreparation(host, observed, lifetime);
                prepared = null; lifetime = null;
                return new SelectedPolicyPreparation(this, owner, Policy);
            }
            catch (Exception primary)
            {
                Exception? cleanup = null;
                if (prepared is not null) try { await prepared.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { cleanup = error; }
                try { lifetime?.Dispose(); }
                catch (Exception error) { cleanup ??= error; }
                if (ownsPreparation) preparationActive = false;
                // The selected adapter may have failed its own cleanup before returning a
                // preparation. Retain that typed uncertainty for the borrowed cohort too.
                if (cleanup is not null || WorkflowCleanupUnconfirmed(primary))
                {
                    cleanupFault = true;
                    preparationPrimaryFailure ??= primary;
                    preparationCleanupFailure ??= cleanup ??
                        (primary as WorkflowReadinessCleanupException)?.CleanupFailure ?? primary;
                    if (cleanup is not null)
                        throw new WorkflowReadinessCleanupException(primary, cleanup);
                }
                throw;
            }
            finally { serial.Release(); }
        }

        internal void PreparedSettled(bool failed)
        {
            if (failed) cleanupFault = true;
            preparationActive = false;
        }

        public async ValueTask DisposeAsync()
        {
            await serial.WaitAsync().ConfigureAwait(false);
            try
            {
                if (closed) return;
                closed = true;
                Exception? cleanup = null;
                var releaseFailure = preparationCleanupFailure is null ? preparationPrimaryFailure :
                    new WorkflowReadinessCleanupException(preparationPrimaryFailure, preparationCleanupFailure);
                try { await borrow.ReleaseAsync(releaseFailure).ConfigureAwait(false); }
                catch (Exception error) { cleanup = error; }
                var unconfirmed = preparationCleanupFailure is null ? cleanup : cleanup is null
                    ? preparationCleanupFailure : new AggregateException(preparationCleanupFailure, cleanup);
                var primary = preparationPrimaryFailure ?? cleanup;
                host.ReleaseWorkflowOwner(primary, unconfirmed);
                if (unconfirmed is not null) throw new WorkflowReadinessCleanupException(primary, unconfirmed);
            }
            finally { serial.Release(); }
        }
    }

    private sealed class SelectedPolicyPreparation(SelectedPolicyScope scope,
        IRegisteredNativeWorkflowPreparation inner, ModelExecutionPolicy policy)
        : IRegisteredNativeWorkflowPreparation, IBoundGenerationDraft, IModelExecutionPolicyFacet
    {
        private int disposed;
        public ModelExecutionPolicy ExecutionPolicy { get; } = policy;
        public PreparedModelMetadata Metadata => inner.Metadata;
        public RegisteredWorkflowPreparationObservation Observe() => inner.Observe();
        public void BindTelemetry(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer) =>
            inner.BindTelemetry(binding, observer);
        public bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer) =>
            (inner as IBoundGenerationDraft)?.TryBindDraft(binding, observer) ?? false;
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token) =>
            inner.GenerateAsync(onText, token);
        public async ValueTask DisposeAsync()
        {
            var first = Interlocked.Exchange(ref disposed, 1) == 0;
            var failed = false;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            catch { failed = true; throw; }
            finally { if (first) scope.PreparedSettled(failed); }
        }
    }

    private class HostWorkflowPreparation(ConfiguredWorkflowModelCoordinator host, IPreparedModelGeneration inner,
        CancellationTokenSource lifetime) : IPreparedModelGeneration, IBoundGenerationTelemetry, IBoundGenerationDraft
    {
        private readonly object ownerGate = new();
        private Task<ModelGenerationResult>? generation;
        private Task? disposal;
        public PreparedModelMetadata Metadata => inner.Metadata;

        public void BindTelemetry(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer)
        {
            lock (ownerGate) { if (disposal is not null) return; }
            (inner as IBoundGenerationTelemetry)?.BindTelemetry(binding, observer);
        }

        public bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer)
        {
            lock (ownerGate) { if (disposal is not null || generation is not null) return false; }
            return (inner as IBoundGenerationDraft)?.TryBindDraft(binding, observer) ?? false;
        }

        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            TaskCompletionSource<ModelGenerationResult> done;
            lock (ownerGate)
            {
                if (disposal is not null) throw new InvalidOperationException("configured_workflow_preparation_disposed");
                if (generation is not null) throw new InvalidOperationException("configured_workflow_preparation_replayed");
                done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                generation = done.Task;
            }
            _ = GenerateCoreAsync(done, onText, token);
            return done.Task;
        }

        private async Task GenerateCoreAsync(TaskCompletionSource<ModelGenerationResult> done,
            Action<string>? onText, CancellationToken token)
        {
            ModelGenerationResult? result = null; Exception? error = null;
            CancellationTokenSource? linked = null;
            try
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                result = await inner.GenerateAsync(onText, linked.Token).ConfigureAwait(false);
            }
            catch (Exception failure) { error = failure; }
            finally { linked?.Dispose(); }
            if (error is OperationCanceledException cancelled) done.TrySetCanceled(cancelled.CancellationToken);
            else if (error is not null) done.TrySetException(error);
            else done.TrySetResult(result!);
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? done = null; Task task; Task<ModelGenerationResult>? started;
            lock (ownerGate)
            {
                if (disposal is null)
                {
                    done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    disposal = done.Task;
                }
                task = disposal; started = generation;
            }
            if (done is not null) _ = DisposeCoreAsync(done, started);
            return new(task);
        }

        private async Task DisposeCoreAsync(TaskCompletionSource done, Task<ModelGenerationResult>? started)
        {
            ExceptionDispatchInfo? cleanup = null;
            try
            {
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanup = ExceptionDispatchInfo.Capture(error); }
                // A failed registry release may precede its own generation wait. Keep the
                // runtime owner until the actual started task settles, even on cleanup failure.
                if (started is not null) try { await started.ConfigureAwait(false); } catch { /* Original caller owns its outcome. */ }
            }
            finally
            {
                try { lifetime.Dispose(); }
                finally { host.ReleaseWorkflowOwner(cleanup?.SourceException, cleanup?.SourceException); }
            }
            if (cleanup is not null) done.TrySetException(cleanup.SourceException); else done.TrySetResult();
        }
    }

    private sealed class ObservedHostWorkflowPreparation : HostWorkflowPreparation, IRegisteredNativeWorkflowPreparation
    {
        private readonly IRegisteredNativeWorkflowPreparation observed;
        internal ObservedHostWorkflowPreparation(ConfiguredWorkflowModelCoordinator host,
            IRegisteredNativeWorkflowPreparation inner, CancellationTokenSource lifetime) : base(host, inner, lifetime)
        { observed = inner; }
        public RegisteredWorkflowPreparationObservation Observe() => observed.Observe();
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? start = null;
        Task task;
        WorkflowReadinessCohort<IWorkflowReadinessLease>? cohort;
        Task owners;
        lock (gate)
        {
            closed = true;
            cohort = workflowReadiness;
            owners = idle.Task;
            if (disposal is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                disposal = start.Task;
            }
            task = disposal;
        }
        if (start is not null) _ = CloseCoreAsync(start, cohort, owners);
        return new(task);
    }

    private async Task CloseCoreAsync(TaskCompletionSource done,
        WorkflowReadinessCohort<IWorkflowReadinessLease>? cohort, Task owners)
    {
        var failures = new List<Exception>();
        try
        {
            // Closing/cancellation may invoke provider callbacks. Neither runs under our gate.
            try { cohort?.CloseAdmission(); } catch (Exception error) { failures.Add(error); }
            try { await stopping.CancelAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (cohort is not null)
                try { await cohort.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await owners.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            lock (gate)
                if (workflowUnconfirmedOwners != 0)
                    failures.Add(new WorkflowReadinessCleanupException(workflowPrimaryFailure, workflowCleanupFailure));
        }
        finally { stopping.Dispose(); }
        if (failures.Count == 0) done.TrySetResult();
        else done.TrySetException(new AggregateException("Configured workflow ownership did not settle.", failures));
    }
}
