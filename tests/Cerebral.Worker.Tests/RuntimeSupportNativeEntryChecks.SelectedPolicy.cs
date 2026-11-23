using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker.Tests;

// Same-handler selected registry checks use the native-entry fixture's already admitted
// support binding. They do not prepare a server request or invoke the controlled apphost.
public static partial class RuntimeSupportNativeEntryChecks
{
    private static async Task<int> SelectedPolicyRegistryAsync(ModelRuntimeSupportDescriptor support,
        ModelGenerationRequest request)
    {
        var checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Selected native registry: " + name); checks++; }
        var selectedBinding = support.Binding with
            { BackendId = "fixture.selected-native", InstanceId = "fixture.selected-instance" };
        var selected = new SelectedShapeFixture(support with { Binding = selectedBinding });
        var registry = new AdapterRegistry();
        var adapterBinding = new AdapterBinding(selectedBinding.BackendId, selectedBinding.ModelId,
            selectedBinding.InstanceId!, selectedBinding.AdapterId, selectedBinding.AdapterVersion);
        selected.Bind(registry, adapterBinding);
        registry.RegisterWithRuntimeSupport(selectedBinding.BackendId, selectedBinding.ModelId,
            selectedBinding.InstanceId!, selected, new FixtureOwner(), [selected.Descriptor],
            selectedBinding, []);
        var policy = selected.ExecutionPolicy;
        Check(registry.TryValidateRequestShape(adapterBinding, request, policy) &&
            registry.Observe(adapterBinding).RegistryReservations == 0 && selected.SelectedCalls == 0,
            "exact selected policy validates shape without reserving capacity");
        var wrong = policy with { Binding = policy.Binding with { InstanceId = "fixture.wrong-instance" } };
        Check(Refused<RegistryException>(() => registry.TryValidateRequestShape(adapterBinding, request, wrong)) &&
            registry.Observe(adapterBinding).RegistryReservations == 0 && selected.SelectedCalls == 0,
            "different selected instance refuses before provider or registry owner");
        var lease = await registry.PrepareSelectedAsync(adapterBinding, new(["text"], Reasoning: false),
            request, policy, default);
        Check(lease is IModelExecutionPolicyFacet { ExecutionPolicy: var transferred } && transferred == policy &&
            selected.SelectedCalls == 1 && selected.ReservationsDuringSelection.SequenceEqual([1]) &&
            lease.Metadata.DesiredResponseBudget == request.ResponseLimit &&
            registry.Observe(adapterBinding).RegistryReservations == 1,
            "selected native callback, prepared metadata and registry lease retain exact policy");
        var generated = await lease.GenerateAsync(null, default);
        Check(generated.Finish == ModelGenerationFinish.Stop && selected.Generations == 1 &&
            await RefusedAsync<RegistryException>(() => lease.GenerateAsync(null, default)),
            "selected registry lease allows one generation and refuses replay");
        await lease.DisposeAsync();
        Check(registry.Observe(adapterBinding).RegistryReservations == 0 && selected.Disposals == 1,
            "selected registry owner releases after the prepared fixture settles");
        selected.FailDispose = true;
        var uncertain = await registry.PrepareSelectedAsync(adapterBinding, new(["text"], Reasoning: false),
            request, policy, default);
        Check(await RefusedAsync<RegistryException>(() => uncertain.DisposeAsync().AsTask()) &&
            registry.Observe(adapterBinding).UnconfirmedCleanupCount == 1 &&
            selected.Disposals == 2 && selected.Generations == 1,
            "selected cleanup failure retains unconfirmed owner without extra inference");
        var before = selected.SelectedCalls;
        Check(await RefusedAsync<RegistryException>(() => registry.PrepareSelectedAsync(adapterBinding,
                new(["text"], Reasoning: false), request, policy, default)) &&
            selected.SelectedCalls == before,
            "uncertain selected cleanup prevents another provider preparation");
        return checks;
    }

    private static bool Refused<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }
    private static async Task<bool> RefusedAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); return false; } catch (T) { return true; } }

    private sealed class SelectedShapeFixture(ModelRuntimeSupportDescriptor support)
        : IModelRuntimeAdapter, IModelRuntimeSupportFacet, IRegisteredNativeRequestShapeFacet,
            IRegisteredNativePolicyExecutor
    {
        private AdapterRegistry? registry;
        private AdapterBinding? adapterBinding;
        internal int SelectedCalls, Generations, Disposals;
        internal bool FailDispose;
        internal List<int> ReservationsDuringSelection { get; } = [];
        public ModelAdapterDescriptor Descriptor { get; } = new(support.Binding.AdapterId,
            support.Binding.AdapterVersion, ["text"]);
        public ModelRuntimeSupportDescriptor RuntimeSupport => support;
        public ModelRuntimeSupportBinding RequestShapeBinding => support.Binding;
        public ModelExecutionPolicy ExecutionPolicy { get; } = new(1, "fixture.selected-native", 1,
            support.Binding, "fixture-native-runtime-generation", "text", false, 512, 2048,
            1, 2048 - 1 - AgentTokenLimits.ContextSafetyTokens, "fixture-native-software/1");
        internal void Bind(AdapterRegistry owner, AdapterBinding binding)
        { registry = owner; adapterBinding = binding; }
        public void ValidateRequestShape(ModelGenerationRequest request) =>
            throw new InvalidOperationException("The selected branch uses the typed native shape guard.");
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("The selected branch must not enter v1 provider preparation.");
        public Task<IPreparedModelGeneration> PrepareSelectedAsync(ModelGenerationRequest request,
            ModelExecutionPolicy policy, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); request.Validate(ExecutionPolicy);
            if (policy != ExecutionPolicy) throw new InvalidOperationException("Selected fixture policy changed.");
            SelectedCalls++;
            var binding = adapterBinding ?? throw new InvalidOperationException("Fixture binding missing.");
            ReservationsDuringSelection.Add((registry ?? throw new InvalidOperationException("Fixture registry missing."))
                .Observe(binding).RegistryReservations);
            var metadata = new PreparedModelMetadata(binding.InstanceId, binding.AdapterId,
                binding.AdapterVersion, request.ModelId, 1, request.ContextLimit,
                request.ResponseLimit, "selected deterministic fixture count")
                { DesiredResponseBudget = request.ResponseLimit,
                    SafetyReserveTokens = AgentTokenLimits.ContextSafetyTokens };
            return Task.FromResult<IPreparedModelGeneration>(new SelectedPrepared(this, metadata, policy));
        }

        private sealed class SelectedPrepared(SelectedShapeFixture owner, PreparedModelMetadata metadata,
            ModelExecutionPolicy policy) : IPreparedModelGeneration, IModelExecutionPolicyFacet
        {
            private int generated, disposed;
            public ModelExecutionPolicy ExecutionPolicy => policy;
            public PreparedModelMetadata Metadata => metadata;
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref generated, 1) != 0)
                    throw new InvalidOperationException("Selected fixture generation replayed.");
                owner.Generations++;
                return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop,
                    "selected fixture result", 2, 1));
            }
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    throw new InvalidOperationException("Selected fixture disposed twice.");
                owner.Disposals++;
                return owner.FailDispose ? ValueTask.FromException(new IOException("Fixture selected cleanup failed."))
                    : ValueTask.CompletedTask;
            }
        }
    }
}
