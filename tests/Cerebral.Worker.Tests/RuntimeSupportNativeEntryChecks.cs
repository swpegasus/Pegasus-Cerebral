using System.Reflection;
using System.Collections;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker;
using Cerebral.Worker.ModelAdapters;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

// Controlled real ModelRuntime lifecycle with the existing test apphost serving only /health.
// The four-byte GGUF is never interpreted by a model server; no inference is prepared or run.
public static partial class RuntimeSupportNativeEntryChecks
{
    public static async Task<int> RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "cerebral-runtime-support-entry-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var store = new StateStore(Path.Combine(root, "data"));
        var configuration = new Configuration(store);
        var events = new EventHub();
        string? reportedInstallationOverride = null;
        var runtime = new ModelRuntime(store, () =>
        {
            var current = configuration.Get();
            if (reportedInstallationOverride is not null)
                current.InstallationId = reportedInstallationOverride;
            return current;
        }, events);
        var priorFixtureEnvironment = Environment.GetEnvironmentVariable(ModelRuntimeCleanupTests.FixtureEnvironment);
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Runtime-support entry: " + name);
            checks++;
        }

        static ModelGenerationRequest ShapeRequest(string id) => new(id,
            [new("user", [new TextContentPart("A bounded native text request.")])], 2048, 128);

        const string modelId = "fixture.native-support";
        var signal = Path.Combine(root, "server-exit.signal");
        async Task LoadWithHealthChildAsync()
        {
            Environment.SetEnvironmentVariable(ModelRuntimeCleanupTests.FixtureEnvironment, signal);
            try { await runtime.LoadAsync(modelId, CancellationToken.None); }
            finally { Environment.SetEnvironmentVariable(ModelRuntimeCleanupTests.FixtureEnvironment, priorFixtureEnvironment); }
        }

        try
        {
            var settings = configuration.Get();
            var installationId = settings.InstallationId;
            settings.LlamaServerPath = ModelRuntimeCleanupTests.AppHost();
            settings.ReservedMemoryMb = 0;
            settings.DefaultModelIdleMinutes = 0;
            configuration.Save(settings);

            var modelPath = Path.Combine(root, "fixture.gguf");
            await File.WriteAllBytesAsync(modelPath, "GGUF"u8.ToArray());
            store.Put("model", modelId, new ModelRecord
            {
                Id = modelId, Name = "Native support controlled entry fixture", Path = modelPath,
                SizeBytes = 4, State = "unloaded", ContextTokens = 2048,
                MaximumContextTokens = 2048, ParallelRequests = 1, GpuLayers = 0,
                IdleMinutes = 0, AutomaticContextSizing = false
            });

            await LoadWithHealthChildAsync();
            var firstInstance = ModelRuntimeCleanupTests.CurrentInstance(runtime, modelId);
            var firstInstanceId = InstanceId(firstInstance);
            var shapeRequest = ShapeRequest(modelId);
            var beforeRegistration = runtime.Accounting.Snapshot();
            Check(Registration(firstInstance) is null &&
                await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None) is null &&
                await runtime.ReadLoadedNativeTextRequestShapeAsync(modelId, shapeRequest, CancellationToken.None) is null &&
                Registration(firstInstance) is null &&
                runtime.Accounting.Snapshot().Revision == beforeRegistration.Revision,
                "entry read before registration creates no registration or accounting effect");

            var service = await runtime.RegisterLoadedNativeTextAsync(modelId, CancellationToken.None);
            var firstLifetime = Registration(firstInstance)
                ?? throw new InvalidOperationException("The actual loaded instance has no registration.");
            var beforeRead = runtime.Accounting.Snapshot();
            var beforeService = service.Observe();
            var first = await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None);
            var repeated = await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None);
            var shape = await runtime.ReadLoadedNativeTextRequestShapeAsync(modelId, shapeRequest, CancellationToken.None);
            var afterService = service.Observe();
            Check(first is not null && ReferenceEquals(first, repeated) &&
                ReferenceEquals(service, firstLifetime.Service) &&
                first.Descriptor.Binding.WorkerInstallationId == installationId &&
                first.Descriptor.Binding.InstanceId == firstInstanceId &&
                first.Descriptor.Capabilities.Count == 0 && !first.ExpandedPathSupported &&
                shape == first.Descriptor.Binding && firstLifetime.RequestShape is not null &&
                ReferenceEquals(firstLifetime.RequestShape, RegisteredHandler(service)),
                "actual entry returns the exact loaded registration's passive declaration");
            Check(runtime.Accounting.Snapshot().Revision == beforeRead.Revision &&
                beforeService.ActiveCallbacks == 0 && afterService.ActiveCallbacks == 0 &&
                beforeService.Registry.RegistryReservations == afterService.Registry.RegistryReservations &&
                beforeService.Registry.UnconfirmedCleanupCount == afterService.Registry.UnconfirmedCleanupCount,
                "repeated entry reads do not reserve callbacks, prepare inference or change ownership");

            var registry = RegisteredRegistry(service);
            var plain = registry.CreateAdapter(service.Binding, new(["text"]));
            var reasoning = service.CreateWorkflowAdapter(true);
            var reasoned = shapeRequest with { ReasoningTokens = 128 };
            Check(registry.TryValidateRequestShape(service.Binding, shapeRequest) &&
                registry.TryValidateRequestShape(reasoning.Binding, reasoned) &&
                reasoning.Binding.AdapterId != shape!.AdapterId &&
                reasoning.Descriptor.SupportsReasoning,
                "facade-owned shape validates valid off/on text without conflating the selected reasoning facet");
            var lateSystem = shapeRequest with { Messages =
                [new("user", [new TextContentPart("first")]), new("system", [new TextContentPart("too late")])] };
            lateSystem.Validate(); // General DTO validation does not establish native template order.
            var beforeRefusals = runtime.Accounting.Snapshot();
            var beforeRefusalService = service.Observe();
            var beforeAvailable = runtime.ObserveManagedSlots().Models.Single(x => x.ModelId == modelId).ManagedAvailableSlots;
            var plainWorkflow = service.CreateWorkflowAdapter();
            var workflowFenceBefore = firstLifetime.ObserveOwnershipRole();
            var workflowLateSystemRefused = await RefusedAsync<LlamaProtocolException>(async () =>
                { await plainWorkflow.PrepareAsync(lateSystem, CancellationToken.None); });
            var workflowFenceAfter = firstLifetime.ObserveOwnershipRole();
            Check(Refused<LlamaProtocolException>(() => RegisteredNativeTextRequestShape.Require(lateSystem, modelId)) &&
                Refused<LlamaProtocolException>(() => registry.TryValidateRequestShape(service.Binding, lateSystem)) &&
                await RefusedAsync<LlamaProtocolException>(async () =>
                    { await plain.PrepareAsync(lateSystem, CancellationToken.None); }) &&
                workflowLateSystemRefused && workflowFenceBefore.CaptureStable && workflowFenceAfter.CaptureStable &&
                workflowFenceBefore.FenceRevision == workflowFenceAfter.FenceRevision,
                "late system guidance refuses at both actual registry/workflow entries before native preparation");
            var image = shapeRequest with { Messages = [new("user", [new ImageContentPart(
                new("image", 1, "image/png", "fixture.png", 16))])] };
            Check(Refused<NotSupportedException>(() => RegisteredNativeTextRequestShape.Freeze(image, modelId)) &&
                Refused<NotSupportedException>(() => registry.TryValidateRequestShape(service.Binding, image)) &&
                Refused<ArgumentException>(() => RegisteredNativeTextRequestShape.Require(
                    shapeRequest with { ModelId = "other.model" }, modelId)) &&
                Refused<ArgumentOutOfRangeException>(() => RegisteredNativeTextRequestShape.Require(
                    reasoned with { ReasoningTokens = 64 }, modelId)) &&
                Refused<ArgumentOutOfRangeException>(() => RegisteredNativeTextRequestShape.Require(
                    shapeRequest with { ResponseSharePercent = 51 }, modelId)) &&
                await RefusedAsync<NotSupportedException>(async () =>
                    { await service.CreateWorkflowAdapter().PrepareAsync(reasoned, CancellationToken.None); }),
                "unsupported content, mismatched model and invalid coupled budgets refuse");
            var afterRefusalService = service.Observe();
            Check(runtime.Accounting.Snapshot().Revision == beforeRefusals.Revision &&
                beforeRefusalService.ActiveCallbacks == afterRefusalService.ActiveCallbacks &&
                beforeRefusalService.Registry.RegistryReservations == afterRefusalService.Registry.RegistryReservations &&
                beforeRefusalService.Registry.UnconfirmedCleanupCount == afterRefusalService.Registry.UnconfirmedCleanupCount &&
                runtime.ObserveManagedSlots().Models.Single(x => x.ModelId == modelId).ManagedAvailableSlots == beforeAvailable,
                "all pure shape checks leave callbacks, registry owners, allocator and accounting unchanged");

            reportedInstallationOverride = Guid.NewGuid().ToString("N");
            Check(await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None) is null,
                "current Worker installation drift closes the full entry");
            Check(await runtime.ReadLoadedNativeTextRequestShapeAsync(modelId, shapeRequest, CancellationToken.None) is null,
                "current Worker installation drift closes the typed shape read");
            reportedInstallationOverride = null;
            Check(ReferenceEquals(first,
                await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None)),
                "restored Worker identity reopens only the unchanged loaded registration");

            var persisted = store.Get<ModelRecord>("model", modelId)
                ?? throw new InvalidOperationException("Fixture model record is absent.");
            persisted.Path = Path.Combine(root, "drifted.gguf");
            store.Put("model", modelId, persisted);
            Check(await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None) is null,
                "persisted scoped model-binding drift closes the full entry");
            Check(await runtime.ReadLoadedNativeTextRequestShapeAsync(modelId, shapeRequest, CancellationToken.None) is null,
                "persisted scoped binding drift closes the typed shape read");
            persisted.Path = modelPath;
            store.Put("model", modelId, persisted);
            Check(ReferenceEquals(first,
                await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None)),
                "restored scoped model binding returns the original loaded declaration");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    _ = await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, cancelled.Token);
                    throw new InvalidOperationException("Cancelled support read unexpectedly returned.");
                }
                catch (OperationCanceledException) { checks++; }
            }
            Check(ReferenceEquals(first,
                await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None)),
                "cancellation does not alter the loaded declaration");

            await runtime.UnloadAsync(modelId, CancellationToken.None);
            Check(!runtime.IsLoaded(modelId) && firstLifetime.ReadRuntimeSupport() is null &&
                await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None) is null &&
                await runtime.ReadLoadedNativeTextRequestShapeAsync(modelId, shapeRequest, CancellationToken.None) is null,
                "unload closes the old lifetime and full entry");

            await LoadWithHealthChildAsync();
            var replacement = ModelRuntimeCleanupTests.CurrentInstance(runtime, modelId);
            var replacementId = InstanceId(replacement);
            Check(!ReferenceEquals(firstInstance, replacement) && replacementId != firstInstanceId &&
                firstLifetime.ReadRuntimeSupport() is null &&
                await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None) is null,
                "replacement stays unregistered and never revives old support");
            await runtime.RegisterLoadedNativeTextAsync(modelId, CancellationToken.None);
            var newSupport = await runtime.ReadLoadedNativeRuntimeSupportAsync(modelId, CancellationToken.None);
            Check(newSupport is not null && !ReferenceEquals(first, newSupport) &&
                newSupport.Descriptor.Binding.InstanceId == replacementId &&
                newSupport.Descriptor.Binding.InstanceId != first!.Descriptor.Binding.InstanceId &&
                firstLifetime.ReadRuntimeSupport() is null,
                "reload binds a new declaration to the new instance only");
            Check(await runtime.ReadLoadedNativeTextRequestShapeAsync(modelId, shapeRequest, CancellationToken.None) ==
                newSupport!.Descriptor.Binding && firstLifetime.ReadRequestShape(shapeRequest, first!) is null,
                "fresh shape read binds only the replacement registration");
            var newLifetime = Registration(replacement)!;
            newLifetime.CloseAdmission();
            Check(await runtime.ReadLoadedNativeTextRequestShapeAsync(modelId, shapeRequest, CancellationToken.None) is null &&
                newLifetime.ReadRequestShape(shapeRequest, newSupport!) is null,
                "closed registration fence refuses typed shape without preparing inference");

            var matchingShapeBinding = first!.Descriptor.Binding with
                { BackendId = "fixture.shape-matched", InstanceId = "fixture.shape-matched-instance" };
            var matchingShape = new MatchedShapeFixture(first.Descriptor with { Binding = matchingShapeBinding });
            var matchingShapeRegistry = new AdapterRegistry();
            var matchingShapeAdapterBinding = new AdapterBinding(matchingShapeBinding.BackendId,
                matchingShapeBinding.ModelId, matchingShapeBinding.InstanceId!,
                matchingShapeBinding.AdapterId, matchingShapeBinding.AdapterVersion);
            matchingShape.Bind(matchingShapeRegistry, matchingShapeAdapterBinding);
            matchingShapeRegistry.RegisterWithRuntimeSupport(matchingShapeBinding.BackendId,
                matchingShapeBinding.ModelId, matchingShapeBinding.InstanceId!, matchingShape,
                new FixtureOwner(), [matchingShape.Descriptor], matchingShapeBinding, []);
            var matchingShapeAdapter = matchingShapeRegistry.CreateAdapter(matchingShapeAdapterBinding,
                new(["text"]));
            Check(await RefusedAsync<LlamaProtocolException>(async () =>
                    { await matchingShapeAdapter.PrepareAsync(lateSystem, CancellationToken.None); }) &&
                matchingShape.ValidationCalls == 1 && matchingShape.ReservationsDuringValidation.SequenceEqual([0]) &&
                matchingShape.PrepareCalls == 0 &&
                matchingShapeRegistry.Observe(matchingShapeAdapterBinding).RegistryReservations == 0,
                "same-handler registry facet rejects late system before reserving an owner or calling provider");
            var fixturePrepared = await matchingShapeAdapter.PrepareAsync(shapeRequest, CancellationToken.None);
            Check(matchingShape.ValidationCalls == 2 &&
                matchingShape.ReservationsDuringValidation.SequenceEqual([0, 0]) &&
                matchingShape.PrepareCalls == 1 &&
                matchingShapeRegistry.Observe(matchingShapeAdapterBinding).RegistryReservations == 1,
                "valid shape reaches one deterministic fixture preparation with the registry owner retained");
            await fixturePrepared.DisposeAsync();
            Check(matchingShapeRegistry.Observe(matchingShapeAdapterBinding).RegistryReservations == 0,
                "valid fixture preparation releases its registry owner without inference");

            var legacyFacet = new LegacyShapeFixture(new(installationId, "fixture.backend", "fixture.adapter", "1.0.0",
                modelId, OwnerModelSnapshotCodec.BindingCodec, new string('a', 64), "fixture.legacy"));
            var legacyRegistry = new AdapterRegistry();
            var legacyBinding = new AdapterBinding("fixture.backend", modelId, "fixture.legacy", "fixture.adapter", "1.0.0");
            legacyRegistry.Register(legacyBinding.BackendId, legacyBinding.ModelId, legacyBinding.InstanceId,
                legacyFacet, new FixtureOwner(), [legacyFacet.Descriptor]);
            Check(!legacyRegistry.TryValidateRequestShape(legacyBinding, shapeRequest) &&
                legacyFacet.ShapeCalls == 0 && legacyFacet.PrepareCalls == 0 &&
                legacyRegistry.RuntimeSupport(legacyBinding) is null,
                "legacy Register ignores an optional shape facet and preserves v1 support absence");
            var wrongBinding = first!.Descriptor.Binding with
                { BackendId = "fixture.shape", InstanceId = "fixture.shape-instance" };
            var wrongShape = new SupportedShapeFixture(first!.Descriptor with { Binding = wrongBinding },
                wrongBinding with { ModelId = "fixture.other-model" });
            var wrongRegistry = new AdapterRegistry();
            wrongRegistry.RegisterWithRuntimeSupport(wrongBinding.BackendId, wrongBinding.ModelId,
                wrongBinding.InstanceId!, wrongShape, new FixtureOwner(), [wrongShape.Descriptor], wrongBinding, []);
            Check(Refused<RegistryException>(() => wrongRegistry.TryValidateRequestShape(
                    new(wrongBinding.BackendId, wrongBinding.ModelId, wrongBinding.InstanceId!,
                        wrongBinding.AdapterId, wrongBinding.AdapterVersion), shapeRequest)) &&
                wrongShape.ValidationCalls == 0 && wrongShape.PrepareCalls == 0,
                "same-handler facet with a mismatched claimed binding refuses before validation or provider work");
            checks += await SelectedPolicyRegistryAsync(first.Descriptor, shapeRequest);
            return checks;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelRuntimeCleanupTests.FixtureEnvironment, priorFixtureEnvironment);
            try { await runtime.DisposeAsync(); }
            finally
            {
                SqliteConnection.ClearAllPools();
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsafe fixture cleanup path.");
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string InstanceId(object instance) =>
        (string)(instance.GetType().GetField("Id", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(instance) ?? throw new MissingFieldException("Loaded instance ID is unavailable."));

    private static NativeRegistrationLifetime? Registration(object instance) =>
        (NativeRegistrationLifetime?)instance.GetType()
            .GetField("RegisteredText", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(instance);

    private static AdapterRegistry RegisteredRegistry(RegisteredNativeGenerationService service) =>
        (AdapterRegistry)(typeof(RegisteredNativeGenerationService)
            .GetField("registry", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(service) ?? throw new MissingFieldException("Registration registry is unavailable."));

    private static object RegisteredHandler(RegisteredNativeGenerationService service)
    {
        var registry = RegisteredRegistry(service);
        var entries = (IDictionary)(typeof(AdapterRegistry)
            .GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(registry) ?? throw new MissingFieldException("Registry entries are unavailable."));
        var entry = entries.Values.Cast<object>().Single();
        return entry.GetType().GetField("Adapter", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(entry) ?? throw new MissingFieldException("Registered handler is unavailable.");
    }

    private sealed class FixtureOwner : IAsyncDisposable
    { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    private sealed class MatchedShapeFixture(ModelRuntimeSupportDescriptor support)
        : IModelRuntimeAdapter, IModelRuntimeSupportFacet, IRegisteredNativeRequestShapeFacet
    {
        private AdapterRegistry? registry;
        private AdapterBinding? adapterBinding;
        public ModelAdapterDescriptor Descriptor { get; } = new(support.Binding.AdapterId,
            support.Binding.AdapterVersion, ["text"]);
        public ModelRuntimeSupportDescriptor RuntimeSupport => support;
        public ModelRuntimeSupportBinding RequestShapeBinding => support.Binding;
        public int ValidationCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public List<int> ReservationsDuringValidation { get; } = [];
        public void Bind(AdapterRegistry owner, AdapterBinding binding)
        { registry = owner; adapterBinding = binding; }
        public void ValidateRequestShape(ModelGenerationRequest request)
        {
            ValidationCalls++;
            ReservationsDuringValidation.Add((registry ?? throw new InvalidOperationException("Fixture registry missing."))
                .Observe(adapterBinding ?? throw new InvalidOperationException("Fixture binding missing."))
                .RegistryReservations);
            RegisteredNativeTextRequestShape.Require(request, support.Binding.ModelId);
        }
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            PrepareCalls++;
            var binding = adapterBinding ?? throw new InvalidOperationException("Fixture binding missing.");
            var metadata = new PreparedModelMetadata(binding.InstanceId, binding.AdapterId,
                binding.AdapterVersion, request.ModelId, 1, request.ContextLimit, request.ResponseLimit,
                "deterministic fixture only; no native tokenizer or inference")
                { DesiredResponseBudget = request.ResponseLimit, ReasoningReserveTokens = request.ReasoningTokens };
            return Task.FromResult<IPreparedModelGeneration>(new FixturePrepared(metadata));
        }
    }

    private sealed class LegacyShapeFixture(ModelRuntimeSupportBinding binding)
        : IModelRuntimeAdapter, IRegisteredNativeRequestShapeFacet
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.adapter", "1.0.0", ["text"]);
        public int ShapeCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public ModelRuntimeSupportBinding RequestShapeBinding { get { ShapeCalls++; return binding; } }
        public void ValidateRequestShape(ModelGenerationRequest request)
        { ShapeCalls++; throw new InvalidOperationException("Legacy facet must not run."); }
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { PrepareCalls++; throw new InvalidOperationException("Legacy provider must not run."); }
    }

    private sealed class SupportedShapeFixture(ModelRuntimeSupportDescriptor support,
        ModelRuntimeSupportBinding claimed) : IModelRuntimeAdapter, IModelRuntimeSupportFacet,
            IRegisteredNativeRequestShapeFacet
    {
        public ModelAdapterDescriptor Descriptor { get; } = new(support.Binding.AdapterId,
            support.Binding.AdapterVersion, ["text"]);
        public ModelRuntimeSupportDescriptor RuntimeSupport => support;
        public ModelRuntimeSupportBinding RequestShapeBinding => claimed;
        public int ValidationCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public void ValidateRequestShape(ModelGenerationRequest request)
        { ValidationCalls++; throw new InvalidOperationException("Mismatched shape must not validate."); }
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { PrepareCalls++; throw new InvalidOperationException("Mismatched provider must not run."); }
    }
}
