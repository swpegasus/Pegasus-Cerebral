using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker.Tests;

// Extends the existing configured-readiness fixture with the actual v2 scope. The fake
// registration supplies an immutable loaded policy but never opens a model or provider.
internal static partial class ConfiguredModelAdaptersChecks
{
    private static async Task SelectedPolicyAsync(Fixture f, Action<int, bool, string> check)
    {
        var state = new ProviderState();
        await using (var provider = state.Provider())
        {
            var ordinary = await f.Submission("selected-policy", "helper", "off");
            var consumer = new WorkflowLocalGenerationConsumer(1, WorkerWorkflowEffects.GenerateCommand,
                2, "local.native.text");
            var opted = ordinary with
            {
                SettingsSnapshot = WorkerExecutionSnapshot.Bounded(
                    WorkerExecutionSnapshot.Read(ordinary.SettingsSnapshot) with { LocalGenerationConsumer = consumer }),
                LocalGeneration = new WorkflowLocalGenerationChoice(1, "local.native.text", 8192, 512)
            };
            var candidate = f.Adapters(provider).ForSubmission(opted);
            check(0, candidate is ILocalModelPolicySelection && state.Acquisitions.Count == 0,
                "opted normal route defers readiness until selected policy acquisition");
            var scope = await ((ILocalModelPolicySelection)candidate).SelectAsync(default);
            var policy = scope.Policy;
            LocalModelExecutionPolicy.Require(scope.Adapter, policy);
            var adapter = LocalModelExecutionPolicy.Bind(
                WorkerPreparedBudgets.ForPreparation(scope.Adapter), policy);
            var request = new ModelGenerationRequest(f.Model.Id,
                [new("user", [new TextContentPart("Exact selected fixture request")])], 8192, 512);
            var heldProbe = await adapter.PrepareAsync(request, default);
            var rejectedSecond = await FailureAsync(() => adapter.PrepareAsync(request, default));
            var rejectedThird = await FailureAsync(() => adapter.PrepareAsync(request, default));
            check(2, rejectedSecond is InvalidOperationException && rejectedThird is InvalidOperationException &&
                state.Prepared == 1 && state.Disposed == 0 && state.Acquisitions.Count == 1,
                "rejected concurrent probes cannot clear the first active preparation guard");
            LocalModelExecutionPolicy.Require(heldProbe, policy);
            LocalModelExecutionPolicy.ValidateMetadata(policy, request, heldProbe.Metadata, false);
            await heldProbe.DisposeAsync();
            await using (var probe = await adapter.PrepareAsync(request, default))
            {
                LocalModelExecutionPolicy.Require(probe, policy);
                LocalModelExecutionPolicy.ValidateMetadata(policy, request, probe.Metadata, false);
                check(0, probe.Metadata.DesiredResponseBudget == request.ResponseLimit &&
                    probe.Metadata.SafetyReserveTokens == AgentTokenLimits.ContextSafetyTokens &&
                    probe is IRegisteredNativeWorkflowPreparation,
                    "selected policy, measured metadata and native observation cross decorated budget wrappers");
            }
            var final = await adapter.PrepareAsync(request, default);
            check(0, state.Acquisitions.Count == 1 && state.Leases.Single().Disposals == 0 &&
                state.Prepared == 3 && state.Disposed == 2 && state.Generated == 0,
                "two probes and one final preparation share the same retained readiness borrow");
            var drift = new InvalidOperationException("fixture exact binding changed");
            state.Validate = (_, _, _) => Task.FromException(drift);
            var refused = await FailureAsync(() => scope.RequireCurrentAsync(default));
            check(2, ReferenceEquals(refused, drift) && state.Generated == 0 &&
                state.Leases.Single().Disposals == 0,
                "current binding drift refuses before inference while final native owner remains held");
            state.Validate = null;
            await scope.RequireCurrentAsync(default);
            await scope.DisposeAsync();
            check(0, state.Leases.Single().Disposals == 1 && state.Disposed == 2,
                "readiness releases once after current recheck while final preparation retains ownership");
            var generated = await final.GenerateAsync(null, default);
            check(0, generated.Finish == ModelGenerationFinish.Stop && state.Generated == 1 &&
                await RejectAsync<InvalidOperationException>(() => final.GenerateAsync(null, default)),
                "selected prepared generation uses the original one-use owner");
            await final.DisposeAsync();
            check(0, state.Disposed == 3 && state.Leases.Single().Disposals == 1,
                "final preparation and readiness settle exactly once");
        }

        var unknown = new IOException("fixture selected readiness cleanup unconfirmed");
        var held = new ProviderState
        { LeaseDisposalFailure = new WorkflowReadinessCleanupException(null, unknown) };
        var retained = held.Provider();
        try
        {
            var profile = f.Profile("selected-cleanup", "helper", "off");
            var candidate = (ILocalModelPolicySelection)retained.ForAccepted(profile, f.Binding);
            var scope = await candidate.SelectAsync(default);
            var cleanup = await FailureAsync(() => scope.DisposeAsync().AsTask());
            check(2, cleanup is WorkflowReadinessCleanupException &&
                held.Leases.Single().Disposals == 1 && held.Generated == 0,
                "selected scope retains uncertain readiness cleanup without generation");
            var blocked = await FailureAsync(() =>
                ((ILocalModelPolicySelection)retained.ForAccepted(profile, f.Binding)).SelectAsync(default));
            check(2, blocked is InvalidOperationException or WorkflowReadinessCleanupException &&
                held.Acquisitions.Count == 1 && held.Prepared == 0,
                "unconfirmed selected cleanup cannot acquire another readiness owner");
        }
        finally
        {
            var failure = await FailureAsync(() => retained.DisposeAsync().AsTask());
            check(2, failure is AggregateException && held.Leases.Single().Disposals == 1,
                "provider drain preserves the selected readiness cleanup failure");
        }

        var primary = new InvalidOperationException("selected_local_policy_preparation_changed");
        var disposal = new IOException("fixture selected preparation cleanup failed");
        var doubleFault = new ProviderState { WrongSelectedPreparedPolicy = true,
            PreparationDisposalFailure = disposal };
        var doubleFaultProvider = doubleFault.Provider();
        try
        {
            var profile = f.Profile("selected-double-fault", "helper", "off");
            var scope = await ((ILocalModelPolicySelection)doubleFaultProvider.ForAccepted(profile, f.Binding))
                .SelectAsync(default);
            var request = new ModelGenerationRequest(f.Model.Id,
                [new("user", [new TextContentPart("Mismatch with failed cleanup")])], 8192, 512);
            var fault = await FailureAsync(() => scope.Adapter.PrepareAsync(request, default));
            check(2, fault is WorkflowReadinessCleanupException composite &&
                composite.PrimaryFailure is InvalidOperationException mismatch &&
                mismatch.Message == primary.Message && ReferenceEquals(composite.CleanupFailure, disposal) &&
                doubleFault.Prepared == 1 && doubleFault.Disposed == 1 && doubleFault.Generated == 0,
                "selected preparation retains policy-mismatch primary and exact cleanup failure");
            var again = await FailureAsync(() => scope.Adapter.PrepareAsync(request, default));
            check(2, again is InvalidOperationException && doubleFault.Prepared == 1,
                "failed selected cleanup closes the scope without another provider preparation");
            var release = await FailureAsync(() => scope.DisposeAsync().AsTask());
            check(2, release is WorkflowReadinessCleanupException releaseFailure1 &&
                releaseFailure1.PrimaryFailure is InvalidOperationException &&
                ReferenceEquals(releaseFailure1.CleanupFailure, disposal) && doubleFault.Leases.Single().Disposals == 1,
                "selected scope release retains the original primary and unconfirmed preparation cleanup");
            var blocked = await FailureAsync(() =>
                ((ILocalModelPolicySelection)doubleFaultProvider.ForAccepted(profile, f.Binding)).SelectAsync(default));
            check(2, blocked is InvalidOperationException && doubleFault.Acquisitions.Count == 1,
                "unconfirmed selected preparation cleanup blocks a fresh readiness owner");
        }
        finally
        {
            var drain = await FailureAsync(() => doubleFaultProvider.DisposeAsync().AsTask());
            check(2, drain is AggregateException && doubleFault.Leases.Single().Disposals == 1,
                "provider drain retains the selected preparation cleanup blocker");
        }

        var wrapperState = new ProviderState { WrongSelectedPreparedPolicy = true,
            PreparationDisposalFailure = new IOException("fixture policy view cleanup failed") };
        var wrapperService = new Service(f.Binding, wrapperState);
        var wrapperAdapter = new PolicyAdapter(wrapperService, wrapperState);
        var bound = LocalModelExecutionPolicy.Bind(wrapperAdapter, wrapperAdapter.ExecutionPolicy);
        var wrapperRequest = new ModelGenerationRequest(f.Model.Id,
            [new("user", [new TextContentPart("Policy-view cleanup fixture")])], 8192, 512);
        var wrapperFailure = await FailureAsync(() => bound.PrepareAsync(wrapperRequest, default));
        check(2, wrapperFailure is WorkflowReadinessCleanupException viewFailure &&
            viewFailure.PrimaryFailure is InvalidOperationException &&
            ReferenceEquals(viewFailure.CleanupFailure, wrapperState.PreparationDisposalFailure) &&
            wrapperState.Prepared == 1 && wrapperState.Disposed == 1 && wrapperState.Generated == 0,
            "policy view preserves a primary mismatch alongside failed prepared cleanup");

        var earlyPrimary = new InvalidOperationException("fixture provider rejected selected preparation");
        var earlyCleanup = new IOException("fixture provider cleanup uncertain before return");
        var earlySignal = new WorkflowReadinessCleanupException(earlyPrimary, earlyCleanup);
        var beforeReturn = new ProviderState { PreparationDisposalFailure = earlySignal };
        var beforeReturnProvider = beforeReturn.Provider();
        try
        {
            var profile = f.Profile("selected-before-return", "helper", "off");
            var scope = await ((ILocalModelPolicySelection)beforeReturnProvider.ForAccepted(profile, f.Binding))
                .SelectAsync(default);
            var request = new ModelGenerationRequest(f.Model.Id,
                [new("user", [new TextContentPart("Typed pre-return cleanup fixture")])], 8192, 512);
            var signaled = await FailureAsync(() => scope.Adapter.PrepareAsync(request, default));
            check(2, ReferenceEquals(signaled, earlySignal) && beforeReturn.Prepared == 0 &&
                beforeReturn.Generated == 0,
                "typed cleanup before prepared return keeps the original provider signal");
            var retry = await FailureAsync(() => scope.Adapter.PrepareAsync(request, default));
            check(2, retry is InvalidOperationException && beforeReturn.Prepared == 0,
                "pre-return uncertainty closes the selected scope to further preparation");
            var release = await FailureAsync(() => scope.DisposeAsync().AsTask());
            check(2, release is WorkflowReadinessCleanupException releaseFailure2 &&
                ReferenceEquals(releaseFailure2.PrimaryFailure, earlySignal) &&
                ReferenceEquals(releaseFailure2.CleanupFailure, earlyCleanup) &&
                beforeReturn.Leases.Single().Disposals == 1,
                "readiness release retains both pre-return primary and cleanup uncertainty");
            var blocked = await FailureAsync(() =>
                ((ILocalModelPolicySelection)beforeReturnProvider.ForAccepted(profile, f.Binding))
                    .SelectAsync(default));
            check(2, blocked is InvalidOperationException && beforeReturn.Acquisitions.Count == 1,
                "typed pre-return cleanup blocks another readiness owner");
        }
        finally
        {
            var drain = await FailureAsync(() => beforeReturnProvider.DisposeAsync().AsTask());
            check(2, drain is AggregateException && beforeReturn.Leases.Single().Disposals == 1,
                "provider drain retains the pre-return cleanup blocker");
        }
    }

    private sealed class PolicyAdapter(Service service, ProviderState state)
        : IRegisteredNativeWorkflowAdapter, IModelExecutionPolicyFacet
    {
        public AdapterBinding Binding => service.Binding;
        public ModelAdapterDescriptor Descriptor => LlamaPreparedGeneration.Adapter with { SupportsReasoning = false };
        public int CapturedContextLimit => 8192;
        public ModelExecutionPolicy ExecutionPolicy { get; } = new(1, "fixture.native.text", 1,
            new("fixture-worker-installation", service.Binding.BackendId,
                service.Binding.AdapterId, service.Binding.AdapterVersion, service.Binding.ModelId,
                "fixture.binding/1", new string('a', 64), service.Binding.InstanceId),
            "fixture-runtime-generation", "text", false, 512, 8192, 1,
            8192 - 1 - AgentTokenLimits.ContextSafetyTokens, "fixture-native-software/1");

        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); request.Validate(ExecutionPolicy);
            if (state.PreparationDisposalFailure is WorkflowReadinessCleanupException early)
                throw early; // The provider failed cleanup before returning a prepared owner.
            Interlocked.Increment(ref state.Prepared);
            var inner = new Prepared(state, Binding, request);
            service.Prepared.Enqueue(inner);
            return Task.FromResult<IPreparedModelGeneration>(new PolicyPrepared(inner, Binding,
                state.WrongSelectedPreparedPolicy ? ExecutionPolicy with { PolicyRevision = 2 } : ExecutionPolicy,
                request));
        }
    }

    private sealed class PolicyPrepared(Prepared inner, AdapterBinding binding,
        ModelExecutionPolicy policy, ModelGenerationRequest request)
        : IRegisteredNativeWorkflowPreparation, IModelExecutionPolicyFacet, IBoundGenerationDraft
    {
        public ModelExecutionPolicy ExecutionPolicy => policy;
        public PreparedModelMetadata Metadata { get; } = new(binding.InstanceId, binding.AdapterId,
            binding.AdapterVersion, request.ModelId, 20, request.ContextLimit,
            request.ResponseLimit, "selected deterministic fixture count")
            { DesiredResponseBudget = request.ResponseLimit,
                SafetyReserveTokens = AgentTokenLimits.ContextSafetyTokens };
        public RegisteredWorkflowPreparationObservation Observe() => new(binding, Metadata,
            inner.GeneratedOnce, inner.DisposedOnce, false, false, null, null);
        public void BindTelemetry(GenerationTelemetryBinding binding,
            Action<GenerationTelemetrySample> observer) { }
        public bool TryBindDraft(GenerationDraftBinding binding,
            Action<GenerationDraftSample> observer) => inner.TryBindDraft(binding, observer);
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token) =>
            inner.GenerateAsync(onText, token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
