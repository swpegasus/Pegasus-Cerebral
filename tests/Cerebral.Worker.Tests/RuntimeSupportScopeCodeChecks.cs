using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker.Tests;

// Structural fixtures only. No native/backend/API work; execute only in a later released grouped lane.
public static class RuntimeSupportScopeCodeChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("RuntimeSupport scope-code successor: " + message);
            count++;
        }
        static bool Rejected<T>(Action action) where T : Exception
        { try { action(); return false; } catch (T) { return true; } }
        static bool RejectedCode(Action action, string expectedCode)
        {
            try { action(); return false; }
            catch (RegistryException error) { return error.Code == expectedCode; }
        }

        // This is the existing OwnerModelSnapshotCodec.BindingCodec scope, not a claim about
        // Worker settings, executable bytes, fitted context or model residency.
        const string bindingScope = "Cerebral.RemotePreparedModelBinding.canonical/1";
        var binding = new ModelRuntimeSupportBinding("fixture.worker", "fixture.backend", "fixture.adapter",
            "1.0.0", "fixture.model", bindingScope, new string('a', 64), "fixture.instance");
        var actual = new AdapterBinding(binding.BackendId, binding.ModelId, binding.InstanceId!,
            binding.AdapterId, binding.AdapterVersion);
        var inputs = new List<string> { "audio" };
        var outputs = new List<string> { "text" };
        var capability = new ModelRuntimeCapability("fixture.transcribe", "fixture.generation", 1, inputs, outputs);
        var capabilities = new List<ModelRuntimeCapability> { capability };
        var descriptor = new ModelRuntimeSupportDescriptor(1, binding, Capacity(), capabilities);
        var adapter = new FixtureAdapter(descriptor);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.MakeReadOnly();
        var requestCodec = (JsonTypeInfo<ModelGenerationRequest>)options.GetTypeInfo(typeof(ModelGenerationRequest));
        var resultCodec = (JsonTypeInfo<ModelGenerationResult>)options.GetTypeInfo(typeof(ModelGenerationResult));
        requestCodec.MakeReadOnly(); resultCodec.MakeReadOnly();
        RuntimeCapabilityImplementation Witness(FixtureAdapter target, ModelRuntimeCapability value) =>
            new(value, target, requestCodec, resultCodec);
        AdapterRegistry Register(FixtureAdapter target, ModelRuntimeSupportBinding expected,
            IReadOnlyList<RuntimeCapabilityImplementation> implementations)
        {
            var registry = new AdapterRegistry();
            registry.RegisterWithRuntimeSupport(binding.BackendId, binding.ModelId, binding.InstanceId!,
                target, new FixtureOwner(), [target.Descriptor], expected, implementations);
            return registry;
        }

        var oldWire = JsonSerializer.Serialize(adapter.Descriptor, options);
        var legacy = new AdapterRegistry();
        legacy.Register(binding.BackendId, binding.ModelId, binding.InstanceId!, adapter,
            new FixtureOwner(), [adapter.Descriptor]);
        Check(adapter.SupportReads == 0 && legacy.RuntimeSupport(actual) is null,
            "unchanged v1 registration ignores the optional facet");
        Check(oldWire == JsonSerializer.Serialize(adapter.Descriptor, options) &&
            !JsonSerializer.SerializeToElement(adapter.Descriptor, options).TryGetProperty("runtimeSupport", out _),
            "v1 descriptor wire stays exact with no support field");
        Check(typeof(ModelAdapterDescriptor).GetConstructors().Any(constructor => constructor.GetParameters().Length == 4) &&
            typeof(IModelRuntimeAdapter).GetMethods().Select(method => method.Name).Order()
                .SequenceEqual(new[] { "PrepareAsync", "get_Descriptor" }.Order()),
            "base positional descriptor and adapter interface remain unchanged");

        var registry = Register(adapter, binding, [Witness(adapter, capability)]);
        var snapshot = registry.RuntimeSupport(actual)
            ?? throw new InvalidOperationException("Explicit support registration returned no snapshot.");
        Check(adapter.SupportReads == 1, "optional getter is captured once outside registry gates");
        var choice = ModelRuntimeSupportAdmission.CheckKnownCapacity(snapshot, 24, 2_097_152);
        Check(choice.WithinKnownBounds && choice.AggregateContextTokens == 50_331_648,
            "foundation admits 24 slots and more than 1M context as passive bound data without clipping");
        Check(!choice.HasUnknownMaximum && !snapshot.ExpandedPathSupported,
            "known bounds do not enable the unsupported expanded path");
        Check(snapshot.Descriptor.Capacity.InferenceSlots.Provenance.Basis == RuntimeLimitBasis.BackendHard &&
            snapshot.Descriptor.Capacity.ContextPerSlotTokens.Provenance.Basis == RuntimeLimitBasis.ModelNative,
            "backend and model provenance remain separate");
        Check(!ModelRuntimeSupportAdmission.CheckKnownCapacity(snapshot, 33, 2_097_152).WithinKnownBounds,
            "known hard slot limit refuses rather than clamps");
        Check(!ModelRuntimeSupportAdmission.CheckKnownCapacity(snapshot, 24, 2_097_153).WithinKnownBounds,
            "backend alignment step is enforced");
        Check(!ModelRuntimeSupportAdmission.CheckKnownCapacity(snapshot, 32, 4_194_304).WithinKnownBounds,
            "aggregate context constraint is independent of per-dimension bounds");

        inputs[0] = "changed"; outputs.Clear(); capabilities.Clear();
        adapter.Support = descriptor with { SchemaVersion = 99 };
        Check(snapshot.Descriptor.Capabilities.Single().InputKinds.Single() == "audio" &&
            snapshot.Descriptor.Capabilities.Single().OutputKinds.Single() == "text" && snapshot.Descriptor.SchemaVersion == 1,
            "admitted metadata detaches collections and later facet replacement");
        Check(Rejected<NotSupportedException>(() =>
            ((IList<string>)snapshot.Descriptor.Capabilities[0].InputKinds)[0] = "changed"),
            "admitted kind collections are read-only");
        Check(!ReferenceEquals(snapshot.Descriptor.Binding, binding) &&
            !ReferenceEquals(snapshot.Descriptor.Capacity, descriptor.Capacity) &&
            !ReferenceEquals(snapshot.Descriptor.Capacity.InferenceSlots.Provenance,
                descriptor.Capacity.InferenceSlots.Provenance),
            "binding, constraints and provenance values are detached");

        ModelRuntimeCapability FreshCapability() => capability with { InputKinds = new[] { "audio" }, OutputKinds = new[] { "text" } };
        FixtureAdapter Fresh(ModelRuntimeSupportDescriptor? value = null) =>
            new(value ?? descriptor with { Capabilities = new[] { FreshCapability() } });
        void Refuse(FixtureAdapter target, IReadOnlyList<RuntimeCapabilityImplementation> witnesses,
            string message, ModelRuntimeSupportBinding? expected = null) =>
            Check(Rejected<RegistryException>(() => Register(target, expected ?? binding, witnesses)), message);
        void RefuseCode(FixtureAdapter target, IReadOnlyList<RuntimeCapabilityImplementation> witnesses,
            string expectedCode, string message, ModelRuntimeSupportBinding? expected = null) =>
            Check(RejectedCode(() => Register(target, expected ?? binding, witnesses), expectedCode), message);

        var labelOnly = Fresh();
        Refuse(labelOnly, [], "capability labels without implemented typed witnesses refuse");
        var foreign = Fresh();
        Refuse(foreign, [Witness(Fresh(), FreshCapability())], "a different handler object cannot witness support");
        var mismatch = Fresh();
        Refuse(mismatch, [Witness(mismatch, FreshCapability() with { ContractVersion = 2 })],
            "handler/codec contract version mismatch refuses");
        var noCodec = Fresh();
        Refuse(noCodec, [new(FreshCapability(), noCodec, null!, resultCodec)], "missing typed codec refuses");
        var mutableOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var mutableCodec = Fresh();
        Refuse(mutableCodec, [new(FreshCapability(), mutableCodec,
            (JsonTypeInfo<ModelGenerationRequest>)mutableOptions.GetTypeInfo(typeof(ModelGenerationRequest)),
            (JsonTypeInfo<ModelGenerationResult>)mutableOptions.GetTypeInfo(typeof(ModelGenerationResult)))],
            "mutable codec metadata cannot witness an admitted contract");
        var wrongKinds = Fresh();
        Refuse(wrongKinds, [Witness(wrongKinds, FreshCapability() with { InputKinds = new[] { "video" } })],
            "a modality label cannot substitute for matching typed contract kinds");
        var duplicates = Fresh(descriptor with { Capabilities = new[] { FreshCapability(), FreshCapability() } });
        Refuse(duplicates, [Witness(duplicates, FreshCapability())], "duplicate capability identity refuses");
        var noFacet = new PlainAdapter();
        Check(Rejected<RegistryException>(() => ModelRuntimeSupportAdmission.Capture(noFacet,
            noFacet.Descriptor, actual, binding, [])), "explicit support admission requires the actual optional facet");
        var unknownSchema = Fresh(descriptor with { SchemaVersion = 2, Capabilities = new[] { FreshCapability() } });
        Refuse(unknownSchema, [Witness(unknownSchema, FreshCapability())], "unknown support schema refuses");
        var foreignBinding = Fresh();
        Refuse(foreignBinding, [Witness(foreignBinding, FreshCapability())],
            "model/configuration binding mismatch refuses", binding with { ConfigurationSha256 = new string('b', 64) });
        var absentScope = Fresh();
        RefuseCode(absentScope, [Witness(absentScope, FreshCapability())], "runtime_support_configuration_scope",
            "expected binding without a configuration scope refuses", binding with { ConfigurationScopeId = "" });
        var invalidScope = Fresh();
        RefuseCode(invalidScope, [Witness(invalidScope, FreshCapability())], "runtime_support_configuration_scope",
            "unversioned or zero-version expected scope refuses", binding with { ConfigurationScopeId = "fixture.binding/0" });
        var invalidClaim = Fresh(descriptor with
        {
            Binding = binding with { ConfigurationScopeId = "fixture.binding/0" },
            Capabilities = new[] { FreshCapability() }
        });
        RefuseCode(invalidClaim, [Witness(invalidClaim, FreshCapability())], "runtime_support_configuration_scope",
            "declared scope is validated separately from the expected host scope");
        var otherExpectedScope = Fresh();
        RefuseCode(otherExpectedScope, [Witness(otherExpectedScope, FreshCapability())], "runtime_support_binding_mismatch",
            "same digest under a different expected scope refuses",
            binding with { ConfigurationScopeId = "Cerebral.RemotePreparedModelBinding.canonical/2" });
        var otherClaimScope = Fresh(descriptor with
        {
            Binding = binding with { ConfigurationScopeId = "Cerebral.RemotePreparedModelBinding.canonical/2" },
            Capabilities = new[] { FreshCapability() }
        });
        RefuseCode(otherClaimScope, [Witness(otherClaimScope, FreshCapability())], "runtime_support_binding_mismatch",
            "same digest under a different declared scope refuses");
        var unboundInstance = Fresh();
        RefuseCode(unboundInstance, [Witness(unboundInstance, FreshCapability())], "runtime_support_binding_mismatch",
            "unobserved instance identity cannot bind an actual loaded registry instance",
            binding with { InstanceId = null });
        var invalidRange = Fresh(descriptor with
        {
            Capacity = Capacity() with { InferenceSlots = Range(1, 32) with { Step = 0 } },
            Capabilities = new[] { FreshCapability() }
        });
        Refuse(invalidRange, [Witness(invalidRange, FreshCapability())], "zero range step refuses");
        var reversedRange = Fresh(descriptor with
        {
            Capacity = Capacity() with { InferenceSlots = Range(32, 1) },
            Capabilities = new[] { FreshCapability() }
        });
        Refuse(reversedRange, [Witness(reversedRange, FreshCapability())], "reversed bounds refuse");

        var unknown = Fresh(descriptor with
        {
            Capacity = Capacity() with
            {
                InferenceSlots = Range(1, null),
                ContextPerSlotTokens = Range(1, null),
                AggregateContextTokens = Range(0, null)
            },
            Capabilities = new[] { FreshCapability() }
        });
        var unknownSnapshot = Register(unknown, binding, [Witness(unknown, FreshCapability())]).RuntimeSupport(actual)!;
        var unknownChoice = ModelRuntimeSupportAdmission.CheckKnownCapacity(unknownSnapshot, long.MaxValue, 1);
        Check(unknownSnapshot.Descriptor.Capacity.InferenceSlots.Maximum is null &&
            unknownChoice.HasUnknownMaximum && !unknownSnapshot.ExpandedPathSupported,
            "unknown maximum stays unknown and cannot establish fit or expanded support");
        Check(Rejected<OverflowException>(() =>
            ModelRuntimeSupportAdmission.CheckKnownCapacity(unknownSnapshot, long.MaxValue, 2)),
            "aggregate multiplication is checked");

        var tasks = new RuntimeTaskConcurrencyObservation(3, 1, 20);
        var changedTasks = tasks with { MaximumRunnableTasks = 7 };
        Check(changedTasks.MaximumRunnableTasks == 7 &&
            ModelRuntimeSupportAdmission.CheckKnownCapacity(snapshot, 24, 2_097_152).WithinKnownBounds &&
            typeof(ModelRuntimeCapacityConstraints).GetProperties().All(property => property.Name != "MaximumRunnableTasks"),
            "runnable task counts do not derive from or resize model slots");
        var memory = new ModelRuntimeMemorySample(binding, "fixture.gpu", null, 1024, DateTimeOffset.UnixEpoch);
        var estimate = new ModelRuntimeCapacityEstimate(binding, "fixture.generic", 4096, -1024, "incomplete");
        var effective = new ModelRuntimeEffectiveContext(binding, 32768, 5, "startup_log", DateTimeOffset.UnixEpoch);
        var prepared = new ModelRuntimePreparedEvidence(binding,
            new(binding.InstanceId!, binding.AdapterId, binding.AdapterVersion, binding.ModelId, 10, 32768, 1024, "fixture"));
        Check(memory.AvailableBytes is null && estimate.RemainingBytes == -1024 &&
            effective.ContextPerSlotTokens == 32768 && prepared.Prepared.InputTokens == 10 &&
            typeof(ModelRuntimeSupportDescriptor).GetProperties().All(property =>
                property.PropertyType != typeof(ModelRuntimePreparedEvidence) && property.PropertyType != typeof(ModelRuntimeMemorySample)),
            "memory, estimates, effective context and prepared reports remain distinct from adapter declarations");
        Check(Rejected<RegistryException>(() => registry.CreateAdapter(actual, new(new[] { "audio" }))) &&
            adapter.PrepareCalls == 0, "new capability metadata cannot bypass existing v1 execution gates");
        Check(Rejected<ArgumentOutOfRangeException>(() =>
            new ModelGenerationRequest(binding.ModelId, [new("user", [new Cerebral.Contracts.TextContentPart("fixture")])],
                2_097_152, 1024).Validate()),
            "expanded request remains unsupported by unchanged v1 request validation");
        Check(Rejected<ArgumentException>(() =>
            ModelCapacityChoices.ValidateChoice(new ModelRecord { NativeContextTokens = 4_194_304 },
                new("fixed", 24, 2_097_152))),
            "save/preview/Apply limits remain v1 until the whole path is implemented");
        return count;
    }

    private static RuntimeLimitRange Range(long minimum, long? maximum, long step = 1,
        RuntimeLimitBasis basis = RuntimeLimitBasis.BackendHard) =>
        new(minimum, maximum, step, new(basis, "fixture.source", "1"));

    private static ModelRuntimeCapacityConstraints Capacity() =>
        new(Range(1, 32), Range(512, 4_194_304, 512, RuntimeLimitBasis.ModelNative),
            Range(0, 262144), Range(0, 65536), Range(0, 327680), Range(0, 100_000_000));

    private sealed class FixtureOwner : IAsyncDisposable
    { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    private sealed class PlainAdapter : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.adapter", "1.0.0", new[] { "text" });
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Absent-facet fixture must not prepare inference.");
    }

    private sealed class FixtureAdapter(ModelRuntimeSupportDescriptor support) : IModelRuntimeAdapter, IModelRuntimeSupportFacet
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.adapter", "1.0.0", new[] { "text" });
        public ModelRuntimeSupportDescriptor Support { get; set; } = support;
        public int SupportReads { get; private set; }
        public int PrepareCalls { get; private set; }
        public ModelRuntimeSupportDescriptor RuntimeSupport { get { SupportReads++; return Support; } }
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            PrepareCalls++;
            throw new InvalidOperationException("Structural fixture must not prepare inference.");
        }
    }
}
