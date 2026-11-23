using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker.Tests;

// Source candidate for a later isolated lane. These deterministic checks never start a child,
// load a model, call a provider or claim that the private live read has been integration-tested.
public static class RuntimeSupportNativeInstanceChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Native runtime-support: " + message);
            count++;
        }

        var model = new ModelRecord
        {
            Id = "fixture.model", Path = "fixture.gguf", StopSequences = ["<stop>"],
            ParallelRequests = 2, MaximumContextTokens = 8192, ContextTokens = 4096,
            AutomaticContextSizing = true, GpuLayers = -1
        };
        var capture = OwnerRuntimeLaunchCapture.Capture(model, "fixture-llama-server", "fixture.worker");
        var launch = capture.SuccessfulStartup(null);
        Check(launch.WorkerInstallationId == "fixture.worker" &&
            launch.ConfigurationScopeId == OwnerModelSnapshotCodec.BindingCodec &&
            launch.ConfigurationSha256 == OwnerModelSnapshotCodec.EncodeBinding(launch.Binding!).Sha256,
            "launch keeps the exact Worker scalar and scoped model-binding hash");
        Check(OwnerRuntimeLaunchCapture.Capture(model, "fixture-llama-server")
            .SuccessfulStartup(null).WorkerInstallationId is null,
            "old two-argument capture remains valid and has no invented Worker identity");

        var native = LlamaPreparedGeneration.Adapter;
        var facade = new ModelAdapterDescriptor(native.AdapterId, native.Version,
            Array.AsReadOnly(new[] { "text" }), SupportsReasoning: true);
        var actual = new AdapterBinding("worker.local.llama.cpp", model.Id, "fixture.instance",
            facade.AdapterId, facade.Version);
        var declaration = NativeInstanceRuntimeSupport.TryCreate(launch, actual, facade);
        Check(declaration is not null && declaration.Binding.InstanceId == actual.InstanceId &&
            declaration.Binding.ConfigurationScopeId == OwnerModelSnapshotCodec.BindingCodec &&
            declaration.Capabilities.Count == 0,
            "declaration binds the loaded native instance with no executable capabilities");
        Check(NativeInstanceRuntimeSupport.TryCreate(
            OwnerRuntimeLaunchCapture.Capture(model, "fixture-llama-server").SuccessfulStartup(null),
            actual, facade) is null,
            "unavailable optional Worker identity leaves the ordinary path available");
        Check(NativeInstanceRuntimeSupport.TryCreate(launch,
            actual with { BackendId = "fixture.remote" }, facade) is null &&
            NativeInstanceRuntimeSupport.TryCreate(launch,
                actual with { ModelId = "fixture.other" }, facade) is null,
            "native declaration refuses a foreign backend or model");
        Check(NativeInstanceRuntimeSupport.TryCreate(
            launch with { ConfigurationSha256 = new string('b', 64) }, actual, facade) is null,
            "declaration refuses a digest that does not match captured launch fields");

        var limits = declaration!.Capacity;
        Check(limits.InferenceSlots is { Minimum: 1, Maximum: 16, Step: 1 } &&
            limits.InferenceSlots.Provenance.Basis == RuntimeLimitBasis.WireCompatibility &&
            limits.ContextPerSlotTokens is { Minimum: 512, Maximum: 1_048_576, Step: 1 } &&
            limits.VisibleOutputTokens is { Minimum: 1, Maximum: 131_072, Step: 1 },
            "known ranges name software envelopes, not native maxima");
        Check(limits.ReasoningTokens is { Minimum: 0, Maximum: 32_768, Step: 1 } &&
            limits.CombinedGenerationTokens.Maximum is null &&
            limits.CombinedGenerationTokens.Provenance.Basis == RuntimeLimitBasis.Unreported &&
            limits.AggregateContextTokens.Maximum is null &&
            limits.AggregateContextTokens.Provenance.Basis == RuntimeLimitBasis.Unreported,
            "reasoning is only an outer hull and independent combined/aggregate maxima stay unknown");

        var support = new ModelRuntimeSupportSnapshot(declaration!);
        var facts = new NativeSupportReadFacts(model.Id, actual.InstanceId, "fixture.worker",
            OwnerModelSnapshotCodec.BindingCodec, launch.ConfigurationSha256,
            SameInstance: true, SameRegistration: true, InstanceAlive: true,
            Quarantined: false, RegistrationUsable: true);
        Check(NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual, facts) &&
            !support.ExpandedPathSupported,
            "matching captured launch, persisted scope and exact active instance allow a passive read");
        Check(!NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
            facts with { CurrentWorkerInstallationId = "fixture.other-worker" }),
            "current Worker installation drift closes the view");
        Check(!NativeInstanceRuntimeSupport.IsCurrent(support,
            launch with { WorkerInstallationId = "fixture.other-worker" }, actual, facts),
            "captured Worker installation drift closes the view");
        Check(!NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
            facts with { PersistedConfigurationScopeId = "fixture.other-scope/1" }) &&
            !NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
                facts with { PersistedConfigurationSha256 = new string('b', 64) }),
            "persisted scope or model-binding drift closes the view");
        Check(!NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
            facts with { RequestedModelId = "fixture.other" }),
            "model selection drift closes the view");
        Check(!NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
            facts with { ActualInstanceId = "fixture.other-instance" }) &&
            !NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
                facts with { SameInstance = false }),
            "stale instance and replaced object cannot reuse support");
        Check(!NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
            facts with { SameRegistration = false }) &&
            !NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
                facts with { RegistrationUsable = false }),
            "replaced, closed or retired registration cannot be read");
        Check(!NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
            facts with { InstanceAlive = false }) &&
            !NativeInstanceRuntimeSupport.IsCurrent(support, launch, actual,
                facts with { Quarantined = true }),
            "exited and quarantined instances cannot be read");

        var replacement = actual with { InstanceId = "fixture.reloaded" };
        var replacementDeclaration = NativeInstanceRuntimeSupport.TryCreate(launch, replacement, facade);
        var replacementFacts = facts with { ActualInstanceId = replacement.InstanceId };
        Check(replacementDeclaration is not null &&
            !NativeInstanceRuntimeSupport.IsCurrent(support, launch, replacement, replacementFacts) &&
            NativeInstanceRuntimeSupport.IsCurrent(new ModelRuntimeSupportSnapshot(replacementDeclaration!), launch,
                replacement, replacementFacts),
            "reload needs a newly bound view and never retargets the old one");

        model.ContextTokens = 5120;
        model.AutomaticContextSizing = false;
        Check(OwnerModelSnapshotCodec.EncodeBinding(RemotePreparedInference.Bind(model)).Sha256 ==
            launch.ConfigurationSha256,
            "selected context and automatic sizing are outside this eight-field digest");
        model.Path = "changed.gguf";
        Check(OwnerModelSnapshotCodec.EncodeBinding(RemotePreparedInference.Bind(model)).Sha256 !=
            launch.ConfigurationSha256 && launch.Binding!.Path == "fixture.gguf",
            "captured launch fields stay detached while scoped persisted changes become visible");

        var legacy = new FixtureAdapter(declaration!);
        var registry = new AdapterRegistry();
        registry.Register("fixture.backend", model.Id, "fixture.legacy", legacy,
            new FixtureOwner(), [legacy.Descriptor]);
        Check(legacy.SupportReads == 0 && legacy.PrepareCalls == 0 &&
            registry.RuntimeSupport(new("fixture.backend", model.Id, "fixture.legacy",
                legacy.Descriptor.AdapterId, legacy.Descriptor.Version)) is null,
            "unchanged legacy Register ignores the facet and prepares no provider work");

        // Exercise the actual private lifetime read with a controlled registration. This
        // provider cannot generate and no native process or model is started by the fixture.
        var accounting = new ModelExecutionAccounting(coverageComplete: true);
        var fence = new NativeRegistrationFence();
        var provider = new FixtureAdapter(declaration);
        var liveRegistry = new AdapterRegistry();
        liveRegistry.RegisterWithRuntimeSupport(actual.BackendId, actual.ModelId, actual.InstanceId,
            provider, fence, [provider.Descriptor], declaration.Binding, []);
        var service = new RegisteredNativeGenerationService(liveRegistry, actual, fence, 2048,
            "fixture.runtime", accounting.ProcessGenerationId);
        var ownership = accounting.Begin(ModelOwnershipKind.CapabilityLease, actual.ModelId,
            requiresAdmission: false);
        ownership.Bind(instanceId: actual.InstanceId);
        ownership.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
        var lifetime = new NativeRegistrationLifetime(service, fence, ownership)
        { RuntimeSupport = liveRegistry.RuntimeSupport(actual) };
        var beforeFence = fence.SnapshotVersioned();
        var beforeAccounting = accounting.Snapshot().Entries.Length;
        var before = lifetime.ReadRuntimeSupport();
        var after = lifetime.ReadRuntimeSupport();
        Check(before is not null && ReferenceEquals(before, after) &&
            provider.SupportReads == 1 && provider.PrepareCalls == 0 &&
            service.Observe().ActiveCallbacks == 0 &&
            fence.SnapshotVersioned() == beforeFence &&
            accounting.Snapshot().Entries.Length == beforeAccounting,
            "actual lifetime returns its immutable cached support without provider or callback work");
        lifetime.CloseAdmission();
        Check(lifetime.ReadRuntimeSupport() is null && provider.PrepareCalls == 0,
            "actual lifetime refuses support after its registration fence closes");
        Check(lifetime.BeginRetirement().GetAwaiter().GetResult() is null &&
            lifetime.ReadRuntimeSupport() is null,
            "retired controlled registration cannot revive the passive view");
        return count;
    }

    private sealed class FixtureOwner : IAsyncDisposable
    { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    private sealed class FixtureAdapter(ModelRuntimeSupportDescriptor support)
        : IModelRuntimeAdapter, IModelRuntimeSupportFacet
    {
        public ModelAdapterDescriptor Descriptor { get; } = new(support.Binding.AdapterId,
            support.Binding.AdapterVersion, ["text"]);
        public int SupportReads { get; private set; }
        public int PrepareCalls { get; private set; }
        public ModelRuntimeSupportDescriptor RuntimeSupport
        { get { SupportReads++; return support; } }
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request,
            CancellationToken token)
        {
            PrepareCalls++;
            throw new InvalidOperationException("Structural fixture must not prepare inference.");
        }
    }
}
