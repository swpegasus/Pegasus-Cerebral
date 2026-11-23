using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

// Actual configured selector/coordinator, snapshots, cleanup and Effects with inert providers.
// Temporary SQLite is the only product persistence; no model, process, pipe, tool, network or UI.
internal static partial class ConfiguredModelAdaptersChecks
{
    internal sealed record Counts(int SelectionChecks, int SnapshotChecks, int FailureChecks,
        int PreservedRouteChecks, int EffectsChecks, int AdaptiveReasoningChecks)
    {
        public int Total => SelectionChecks + SnapshotChecks + FailureChecks + PreservedRouteChecks + EffectsChecks + AdaptiveReasoningChecks;
    }

    internal static async Task<Counts> RunAsync(string root)
    {
        if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != root)
            throw new ArgumentException("Configured adapter checks require an exact absolute private parent.", nameof(root));
        var owned = Path.Combine(root, "configured-model-adapters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        var counts = new int[6];
        void Check(int group, bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Configured model adapters: " + label);
            counts[group]++;
        }
        try
        {
            await using var fixture = new Fixture(owned);
            await SelectionAsync(fixture, Check);
            await SelectedPolicyAsync(fixture, Check);
            await SnapshotAsync(fixture, Check);
            await FailuresAsync(fixture, Check);
            await PreservedRoutesAsync(fixture, Check);
            await EffectsAsync(fixture, Check);
            await AdaptiveReasoningAsync(fixture, Check);
            return new(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(owned);
            if (Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(root))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task SelectionAsync(Fixture f, Action<int, bool, string> check)
    {
        var state = new ProviderState();
        var barrier = new TaskCompletionSource<IWorkflowReadinessLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Acquire = (_, _) => barrier.Task;
        await using var provider = state.Provider();
        var adapters = f.Adapters(provider);
        var owner = await f.Submission("owner-on", "lead", "on");
        var helper = await f.Submission("helper-off", "helper", "off");
        var ownerAdapter = adapters.ForSubmission(owner);
        var helperAdapter = adapters.ForSubmission(helper);
        check(0, ownerAdapter.Descriptor.SupportsReasoning && !helperAdapter.Descriptor.SupportsReasoning &&
            state.Acquisitions.Count == 0, "ordinary configured owner/helper selection is pure and mode-specific");
        var ownerTask = ownerAdapter.PrepareAsync(Request(owner), default);
        var helperTask = helperAdapter.PrepareAsync(Request(helper), default);
        try
        {
            check(0, state.Acquisitions.Count == 1, "same full model binding shares one readiness initialization");
            var changed = provider.ForAccepted(WorkerExecutionSnapshot.Read(owner.SettingsSnapshot).Profile,
                f.Binding with { ChatTemplate = "different accepted definition" });
            var changedFailure = await FailureAsync(() => changed.PrepareAsync(Request(owner), default));
            check(0, changedFailure is InvalidOperationException && state.Acquisitions.Count == 1,
                "same model ID with a different full binding cannot join or replace busy readiness");
            var service = new Service(f.Binding, state);
            var lease = new Readiness(service);
            barrier.SetResult(lease);
            await using var first = await ownerTask;
            await using var second = await helperTask;
            check(0, !ReferenceEquals(first, second) && service.Prepared.Count == 2 &&
                first.Metadata.ReasoningReserveTokens == 512 && second.Metadata.ReasoningReserveTokens == 0 &&
                first.Metadata.AdapterId == LlamaPreparedReasoningGeneration.Adapter.AdapterId &&
                second.Metadata.AdapterId == LlamaPreparedGeneration.Adapter.AdapterId,
                "shared readiness produces distinct actual preparations with exact off/on facet metadata");
            check(0, lease.Disposals == 1 && service.Disposals == 0,
                "temporary readiness pin retires once without disposing the Engine-owned service");
            var results = await Task.WhenAll(first.GenerateAsync(null, default), second.GenerateAsync(null, default));
            check(0, results.All(result => result.Finish == ModelGenerationFinish.Stop) && state.Generated == 2,
                "each configured prepared owner performs its own one-use generation");
            check(0, await RejectAsync<InvalidOperationException>(() => first.GenerateAsync(null, default)),
                "configured preparation cannot replay generation");
        }
        finally
        {
            barrier.TrySetCanceled();
            foreach (var task in new[] { ownerTask, helperTask })
                try { await (await task).DisposeAsync(); } catch { /* Preserve the original assertion/failure while draining owned tasks. */ }
        }
    }

    private static async Task SnapshotAsync(Fixture f, Action<int, bool, string> check)
    {
        var state = new ProviderState();
        await using var provider = state.Provider();
        var adapters = f.Adapters(provider);
        foreach (var role in new[] { "lead", "helper", "compactor" })
        {
            var submission = await f.Submission("snapshot-" + role, role, "on", automaticCompaction: true);
            var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
            var compactor = snapshot.Compactor ?? throw new InvalidOperationException("Expected actual self-compactor snapshot.");
            check(1, snapshot.Profile.ReasoningMode == "on" && snapshot.Profile.ReasoningTokens == 512 &&
                snapshot.Profile.ContextTokens == 8192 && snapshot.Profile.ResponseTokens == 512 &&
                compactor.Profile.ReasoningMode == "on" && compactor.Profile.ReasoningTokens == 512 &&
                JsonElement.DeepEquals(CerebralJson.Element(compactor.Model), CerebralJson.Element(snapshot.Model)) && compactor.Profile.AllowedTools.Count == 0,
                "actual accepted " + role + " and self-compactor snapshots retain reasoning and budgets");
            await using var prepared = await adapters.ForCompactor(compactor).PrepareAsync(
                new(compactor.Model.Id, [new("user", [new TextContentPart("Self compaction fixture")])], 8192, 512)
                { ReasoningTokens = 512 }, default);
            check(1, prepared.Metadata.ReasoningReserveTokens == 512 &&
                prepared.Metadata.AdapterId == LlamaPreparedReasoningGeneration.Adapter.AdapterId,
                "actual " + role + " self-compactor routes through its accepted on facet");
        }
        var mutableStops = new List<string> { "STOP", "", "STOP" };
        var accepted = f.Binding with { StopSequences = mutableStops };
        var profile = f.Profile("frozen", "helper", "on");
        var selected = provider.ForAccepted(profile, accepted);
        var barrier = new TaskCompletionSource<IWorkflowReadinessLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Acquire = (_, _) => barrier.Task;
        var messages = new List<ModelInputMessage> { new("user", [new TextContentPart("ORIGINAL")]) };
        var task = selected.PrepareAsync(new(f.Model.Id, messages, 8192, 512) { ReasoningTokens = 512 }, default);
        mutableStops.Add("MUTATED"); profile.ReasoningMode = "off"; messages[0] = new("user", [new TextContentPart("MUTATED")]);
        var service = new Service(accepted with { StopSequences = ["STOP", "", "STOP"] }, state);
        barrier.SetResult(new Readiness(service));
        await using var frozen = await task;
        check(1, state.Acquisitions[^1].StopSequences.SequenceEqual(new[] { "STOP", "", "STOP" }) &&
            service.Prepared.Single().Request.Messages[0].Content.Single() is TextContentPart { Text: "ORIGINAL" } &&
            frozen.Metadata.ReasoningReserveTokens == 512,
            "accepted full binding, mode and request are frozen before readiness awaits");

        state.Acquire = null;
        var other = CopyModel(f.Model, "configured-second");
        f.Store.Put("model", other.Id, other);
        var otherBinding = WorkerExecutionSnapshot.Bind(other);
        var secondProfile = f.Profile("second-model", "helper", "off"); secondProfile.ModelId = other.Id;
        await using var second = await provider.ForAccepted(secondProfile, otherBinding).PrepareAsync(
            new(other.Id, [new("user", [new TextContentPart("second binding")])], 8192, 512), default);
        check(1, state.Acquisitions[^1].Id == other.Id && second.Metadata.ModelId == other.Id &&
            second.Metadata.InstanceId != frozen.Metadata.InstanceId,
            "distinct configured models retain separate readiness and prepared identities");
    }

    private static async Task FailuresAsync(Fixture f, Action<int, bool, string> check)
    {
        var stale = new InvalidOperationException("fixture exact binding changed");
        var state = new ProviderState
        {
            Validate = (expected, _, _) => expected.Path == f.Binding.Path ? Task.CompletedTask : Task.FromException(stale)
        };
        await using (var provider = state.Provider())
        {
            var profile = f.Profile("stale", "helper", "on");
            var selected = provider.ForAccepted(profile, f.Binding with { Path = Path.Combine(f.Root, "changed.gguf") });
            var failure = await FailureAsync(() => selected.PrepareAsync(Request(profile), default));
            check(2, ReferenceEquals(failure, stale) && state.Prepared == 0 && state.Leases.Single().Disposals == 1,
                "stale complete binding fails before provider preparation and releases its actual pin");
            await using var valid = await provider.ForAccepted(profile, f.Binding).PrepareAsync(Request(profile), default);
            check(2, valid.Metadata.ModelId == f.Model.Id && state.Prepared == 1,
                "proven ordinary stale-binding failure does not poison a later valid binding");
            check(2, Reject<RemoteModelProtocolException>(() => provider.ForAccepted(profile,
                f.Binding with { ModelWorkerId = "other-worker", ModelWorkerInstallationId = "other-installation" })),
                "local provider refuses a remote or mismatched accepted origin before acquisition");
            check(2, await RejectAsync<NotSupportedException>(() => provider.ForAccepted(profile, f.Binding).PrepareAsync(
                Request(profile) with { ReasoningTokens = 0 }, default)),
                "captured on mode cannot silently dispatch an off request");
        }

        var unknown = new IOException("fixture pin disposal unconfirmed");
        var held = new ProviderState { LeaseDisposalFailure = new WorkflowReadinessCleanupException(null, unknown) };
        var retained = held.Provider();
        try
        {
            var profile = f.Profile("unknown", "helper", "on");
            var old = retained.ForAccepted(profile, f.Binding);
            var failure = await FailureAsync(() => old.PrepareAsync(Request(profile), default));
            check(2, failure is WorkflowReadinessCleanupException && held.Prepared == 1 && held.Disposed == 1 &&
                held.Leases.Single().Disposals == 1, "failed transfer cleans preparation before retaining unknown readiness");
            var again = await FailureAsync(() => old.PrepareAsync(Request(profile), default));
            check(2, ReferenceEquals(failure, again) && held.Acquisitions.Count == 1,
                "old accepted proxy retains the exact owned readiness failure without retry");
            var fresh = await FailureAsync(() => retained.ForAccepted(profile, f.Binding).PrepareAsync(Request(profile), default));
            check(2, fresh is InvalidOperationException && held.Acquisitions.Count == 1,
                "fresh accepted proxy cannot bypass unresolved cleanup by reacquiring the same model");
        }
        finally
        {
            var disposed = await FailureAsync(() => retained.DisposeAsync().AsTask());
            check(2, disposed is AggregateException { InnerExceptions.Count: 1 } aggregate &&
                ReferenceEquals(aggregate.InnerExceptions[0], held.LeaseDisposalFailure) &&
                held.Leases.Single().Disposals == 1 && held.Acquisitions.Count == 1,
                "provider disposal retains unknown cleanup and never repeats the failed pin disposal");
        }
    }

    private static async Task PreservedRoutesAsync(Fixture f, Action<int, bool, string> check)
    {
        var state = new ProviderState();
        await using var provider = state.Provider();
        var adapters = f.Adapters(provider);
        var remoteProfile = f.Profile("remote", "helper", "on"); remoteProfile.ModelWorkerId = "remote-fixture";
        var remoteBinding = f.Binding with { ModelWorkerId = remoteProfile.ModelWorkerId, ModelWorkerInstallationId = "remote-installation" };
        var remote = Find<RemoteModelRuntimeAdapter>(adapters.ForCompactor(new(remoteProfile, "opaque compact guidance", remoteBinding)));
        check(3, remote is not null && Field<string>(remote, "workerId") == remoteProfile.ModelWorkerId &&
            Field<string>(remote, "workerInstallationId") == remoteBinding.ModelWorkerInstallationId &&
            JsonSerializer.Serialize(Field<RemotePreparedModelBinding>(remote, "binding"), CerebralJson.Options) ==
                JsonSerializer.Serialize(WorkerExecutionSnapshot.RemoteBinding(remoteBinding), CerebralJson.Options) &&
            state.Acquisitions.Count == 0, "admitted remote worker, installation and full model binding survive existing metering");
        check(3, Reject<RemoteModelProtocolException>(() => adapters.ForCompactor(
            new(remoteProfile, "", remoteBinding with { ModelWorkerInstallationId = null }))),
            "remote route still refuses a missing admitted installation");

        var required = await f.Submission("required-image", "helper", "off", requiredImage: true);
        check(3, ReferenceEquals(Find<LlamaResourceModelAdapter>(adapters.ForSubmission(required))?.DelegatedRuntime, f.Runtime) && state.Acquisitions.Count == 0,
            "required-image profile retains the existing native image route");
        var projector = await f.Submission("projector-image", "helper", "off",
            model: CopyModel(f.Model, "configured-projector", Path.Combine(f.Root, "fixture.mmproj")));
        check(3, ReferenceEquals(Find<LlamaResourceModelAdapter>(adapters.ForSubmission(projector))?.DelegatedRuntime, f.Runtime) && state.Acquisitions.Count == 0,
            "accepted projector binding excludes registered text selection");
        var actualImage = await f.Submission("actual-image", "helper", "off");
        actualImage = actualImage with { AcceptedContent = [Image()] };
        check(3, ReferenceEquals(Find<LlamaResourceModelAdapter>(adapters.ForSubmission(actualImage))?.DelegatedRuntime, f.Runtime) && state.Acquisitions.Count == 0,
            "currently accepted image content retains the native image route before readiness");

        var selected = provider.ForAccepted(f.Profile("history", "helper", "off"), f.Binding);
        var legacy = new LegacyImages();
        var route = new ConfiguredLocalWorkflowRoutingAdapter(selected, legacy);
        var imageRequest = new ModelGenerationRequest(f.Model.Id,
            [new("user", [new TextContentPart("previous turn"), Image()]), new("user", [new TextContentPart("current text")])], 8192, 512);
        await using var image = await route.PrepareAsync(imageRequest, default);
        check(3, legacy.TextPreparations == 1 && state.Acquisitions.Count == 0 &&
            ReferenceEquals(legacy.LastRequest, imageRequest), "actual prior-history image dispatch uses only the existing legacy image adapter");
        await using var sidecar = await route.PrepareMultimodalAsync(Request(f.Profile("sidecar", "helper", "off")), [], default);
        check(3, legacy.SidecarPreparations == 1 && state.Acquisitions.Count == 0,
            "actual multimodal sidecar forwarding never acquires registered text ownership");
        await using var text = await route.PrepareAsync(Request(f.Profile("text", "helper", "off")), default);
        check(3, state.Acquisitions.Count == 1 && legacy.TextPreparations == 1 && text.Metadata.AdapterId == LlamaPreparedGeneration.Adapter.AdapterId,
            "ordinary text dispatch uses the configured facet without legacy fallback");
    }

    private static async Task EffectsAsync(Fixture f, Action<int, bool, string> check)
    {
        var state = new ProviderState();
        var provider = state.Provider();
        var adapters = f.Adapters(provider);
        var submission = await f.Submission("effects", "helper", "on");
        var effects = new WorkerWorkflowEffects(f.Store, f.Configuration, f.Runtime, f.Tools,
            adapters.ForSubmission, adapters.AcquireRequiredAsync, adapters.ForCompactor, diagnosticEvents: f.Events);
        var command = effects.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand &&
            item.Descriptor.Version == 1);
        var subscription = f.Events.Subscribe();
        try
        {
            async Task<(WorkflowCommandResult Result, WorkflowGenerationPreviewEvent[] Events)> Execute(string id, CancellationToken token)
            {
                var context = new WorkflowCommandContext(id, submission,
                    CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([]))) { RunId = id + "-run" };
                var result = await command.ExecuteAsync(context, token);
                var events = new List<WorkflowGenerationPreviewEvent>();
                while (subscription.Reader.TryRead(out var item))
                    if (item.Type == WorkflowGenerationPreview.EventType)
                        events.Add(item.Data.Deserialize<WorkflowGenerationPreviewEvent>(CerebralJson.Options)!);
                return (result, events.ToArray());
            }
            var success = await Execute("configured-success", default);
            check(4, success.Result.State == WorkflowEffectState.Succeeded && state.Bindings.Count == 1 &&
                state.Bindings[0].EffectId == "configured-success" && state.Bindings[0].RunId == "configured-success-run" &&
                state.Generated == 1 && state.Disposed == 1,
                "normal Effects preserves bound draft through configured Host, Cleanup, resource, priority and budget wrappers");
            check(4, success.Events is [{ Phase: "draft", Text: "Configured public draft" }, { Phase: "settled" }] &&
                state.CanonicalCallbacks == 1, "bound raw public draft has one canonical callback and no duplicate preview fallback");

            state.PreparationDisposalFailure = new IOException("fixture preparation cleanup failed");
            var cleanup = await Execute("configured-cleanup", default);
            var data = cleanup.Result.Data!.Value.Deserialize<WorkerWorkflowEffects.GenerationData>(CerebralJson.Options)!;
            check(4, cleanup.Result.State == WorkflowEffectState.Unknown && data.Generation.GeneratedTokens == 6 &&
                data.Generation.EvaluatedTokens == 20 && state.Disposed == 2,
                "normal cleanup uncertainty retains real generation data and disposes actual preparation once");
            check(4, cleanup.Events.LastOrDefault() is { Phase: "unavailable", Finish: null } &&
                cleanup.Events.All(item => item.Phase != "settled"),
                "cleanup failure retracts the draft after cleanup and never emits a settled claim");
        }
        finally
        {
            f.Events.Unsubscribe(subscription.Id);
            var failure = await FailureAsync(() => provider.DisposeAsync().AsTask());
            check(4, failure is AggregateException { InnerExceptions.Count: 1 } aggregate &&
                aggregate.InnerExceptions[0] is WorkflowReadinessCleanupException retained &&
                ReferenceEquals(retained.CleanupFailure, state.PreparationDisposalFailure) &&
                state.Disposed == 2 && state.Acquisitions.Count == 2,
                "normal provider drain preserves the actual failed preparation cleanup");
        }

        var before = new ProviderState();
        await using var beforeProvider = before.Provider();
        var beforeAdapters = f.Adapters(beforeProvider);
        var beforeEffects = new WorkerWorkflowEffects(f.Store, f.Configuration, f.Runtime, f.Tools,
            beforeAdapters.ForSubmission, beforeAdapters.AcquireRequiredAsync, beforeAdapters.ForCompactor);
        var beforeCommand = beforeEffects.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand &&
            item.Descriptor.Version == 1);
        using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
        var pre = await beforeCommand.ExecuteAsync(new("before", submission,
            CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([]))) { RunId = "before-run" }, preCancelled.Token);
        check(4, pre.State == WorkflowEffectState.Cancelled && pre.Data is null && before.Acquisitions.Count == 0,
            "cancelled before dispatch remains Cancelled without preparation or inference");
        using var afterCancellation = new CancellationTokenSource();
        before.CancelDuringGeneration = afterCancellation;
        var post = await beforeCommand.ExecuteAsync(new("after", submission,
            CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([]))) { RunId = "after-run" }, afterCancellation.Token);
        check(4, post.State == WorkflowEffectState.Unknown && before.Generated == 1 && before.Disposed == 1,
            "cancellation thrown after actual invocation retains Unknown provenance rather than inventing confirmed cancellation");
    }

    private static ModelRecord CopyModel(ModelRecord model, string id, string? projector = null)
    {
        var copy = CerebralJson.Read<ModelRecord>(CerebralJson.Element(model));
        copy.Id = id; copy.VisionProjectorPath = projector;
        return copy;
    }

    private static ModelGenerationRequest Request(WorkflowSubmission submission) =>
        Request(WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Profile);
    private static ModelGenerationRequest Request(AgentProfile profile) =>
        new(profile.ModelId, [new("user", [new TextContentPart("Configured fixture input")])], 8192, 512)
        { ReasoningTokens = profile.ReasoningMode == "on" ? 512 : 0 };
    private static ImageContentPart Image() => new(new("fixture-image", 1, "image/png", "fixture.png", 1));
    private static bool Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return true; } return false; }
    private static async Task<bool> RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return true; } return false; }
    private static async Task<Exception?> FailureAsync(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    private static T Field<T>(object value, string name) => (T)(value.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value) ?? throw new InvalidOperationException("Missing fixture inspection field " + name));
    private static T? Find<T>(IModelRuntimeAdapter value) where T : class
    {
        var pending = new Queue<IModelRuntimeAdapter>();
        var seen = new HashSet<IModelRuntimeAdapter>(ReferenceEqualityComparer.Instance);
        pending.Enqueue(value);
        while (pending.Count != 0 && seen.Count < 16)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current)) continue;
            if (current is T found) return found;
            for (var type = current.GetType(); type is not null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (field.GetValue(current) is IModelRuntimeAdapter child && !seen.Contains(child))
                        pending.Enqueue(child);
        }
        return null;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; }
        internal StateStore Store { get; }
        internal Configuration Configuration { get; }
        internal ModelRuntime Runtime { get; }
        internal EventHub Events { get; } = new();
        internal NoTools Tools { get; } = new();
        internal ModelRecord Model { get; }
        internal WorkerExecutionSnapshot.ModelBinding Binding => WorkerExecutionSnapshot.Bind(Model);
        internal Fixture(string root)
        {
            Root = root; Store = new(Path.Combine(root, "data")); Configuration = new(Store);
            var settings = Configuration.Get();
            settings.ModelDirectory = Path.Combine(root, "models"); settings.ProjectsDirectory = Path.Combine(root, "projects");
            settings.SharedDirectory = Path.Combine(root, "shared"); Configuration.Save(settings);
            Model = new() { Id = "configured-model", Path = Path.Combine(root, "unloaded-fixture.gguf"),
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192, AutomaticContextSizing = false };
            Store.Put("model", Model.Id, Model);
            Runtime = new(Store, Configuration.Get, Events);
        }
        internal AgentProfile Profile(string id, string role, string mode) => new()
        {
            Id = id, Name = id, Role = role, Revision = 4, ModelId = Model.Id, WorkerId = "local", ModelWorkerId = "local",
            ContextTokens = 8192, ResponseTokens = 512, ReasoningTokens = 512, ReasoningMode = mode,
            FollowsModelBudgets = false, AutomaticCompaction = false, MaxSteps = 64, MaxSeconds = 600,
            AllowedRoots = [Root], AllowedTools = [], Capabilities = []
        };
        internal async Task<WorkflowSubmission> Submission(string id, string role, string mode,
            bool automaticCompaction = false, bool requiredImage = false, ModelRecord? model = null)
        {
            var profile = Profile(id, role, mode); profile.AutomaticCompaction = automaticCompaction;
            if (requiredImage) profile.RequiredModelCapabilities = [ModelCapabilityIds.ImageInput];
            if (model is not null) { Store.Put("model", model.Id, model); profile.ModelId = model.Id; }
            Store.Put("agent", profile.Id, profile);
            var snapshot = await WorkerExecutionSnapshot.CaptureAsync(Store, profile, Configuration, Tools, [], default);
            return new(id + "-request", id + "-chat", profile.Id, "fixture.configured", "1.0.0",
                "PUBLIC FIXTURE INPUT", null, WorkflowContract.EmptyObject, snapshot);
        }
        internal WorkerModelAdapters Adapters(ConfiguredWorkflowModelProvider provider) =>
            new(Runtime, new RemoteWorkerManagement(Configuration), null!, null!, provider);
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class ProviderState
    {
        internal List<RemotePreparedModelBinding> Acquisitions { get; } = [];
        internal List<Readiness> Leases { get; } = [];
        internal List<GenerationDraftBinding> Bindings { get; } = [];
        internal Func<RemotePreparedModelBinding, CancellationToken, Task<IWorkflowReadinessLease>>? Acquire;
        internal Func<RemotePreparedModelBinding, AdapterBinding, CancellationToken, Task>? Validate;
        internal Exception? LeaseDisposalFailure, PreparationDisposalFailure;
        internal bool WrongSelectedPreparedPolicy;
        internal CancellationTokenSource? CancelDuringGeneration;
        internal int Prepared, Generated, Disposed, CanonicalCallbacks;
        internal ConfiguredWorkflowModelProvider Provider() => new(async (binding, token) =>
        {
            Acquisitions.Add(binding);
            if (Acquire is not null) return await Acquire(binding, token);
            var lease = new Readiness(new Service(binding, this)) { Failure = LeaseDisposalFailure };
            Leases.Add(lease); return lease;
        }, (binding, selected, token) => Validate?.Invoke(binding, selected, token) ?? Task.CompletedTask);
    }
    private sealed class Readiness(IRegisteredNativeGenerationService service) : IWorkflowReadinessLease
    {
        internal int Disposals;
        internal Exception? Failure;
        public IRegisteredNativeGenerationService Registration => service;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref Disposals) != 1) throw new InvalidOperationException("Readiness disposal repeated.");
            return Failure is null ? ValueTask.CompletedTask : ValueTask.FromException(Failure);
        }
    }
    private sealed class Service : IRegisteredNativeGenerationService, IRegisteredNativeWorkflowSource
    {
        private readonly ProviderState state;
        public AdapterBinding Binding { get; }
        internal ConcurrentQueue<Prepared> Prepared { get; } = new();
        internal int Disposals;
        internal Service(WorkerExecutionSnapshot.ModelBinding binding, ProviderState state)
            : this(WorkerExecutionSnapshot.RemoteBinding(binding), state) { }
        internal Service(RemotePreparedModelBinding binding, ProviderState state)
        {
            this.state = state;
            Binding = new("worker.local.llama.cpp", binding.Id, "fixture-instance-" + binding.Id,
                LlamaPreparedGeneration.Adapter.AdapterId, LlamaPreparedGeneration.Adapter.Version);
        }
        public IRegisteredNativeWorkflowAdapter CreateWorkflowAdapter() => CreateWorkflowAdapter(false);
        public IRegisteredNativeWorkflowAdapter CreateWorkflowAdapter(bool reasoning) => new Adapter(this, state, reasoning);
        public IRegisteredNativeWorkflowAdapter CreatePolicyWorkflowAdapter() =>
            new PolicyAdapter(this, state);
        public RegisteredNativeServiceObservation Observe() => throw new InvalidOperationException("No direct registration diagnostics in configured checks.");
        public Task<string> GenerateAsync(string id, IReadOnlyList<ModelMessage> messages, int maxTokens, double temperature,
            bool json, Action<string>? onToken, CancellationToken token, JsonElement? responseSchema = null,
            string reasoningMode = "off", int reasoningTokens = 1024, RegisteredNativeGenerationCapture? capture = null,
            InferenceInvocation? invocation = null) => throw new InvalidOperationException("No direct generation in configured checks.");
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        private sealed class Adapter : IRegisteredNativeWorkflowAdapter
        {
            private readonly Service service;
            private readonly ProviderState state;
            public ModelAdapterDescriptor Descriptor { get; }
            public AdapterBinding Binding { get; }
            public int CapturedContextLimit => 8192;
            internal Adapter(Service service, ProviderState state, bool reasoning)
            {
                this.service = service; this.state = state;
                var actual = reasoning ? LlamaPreparedReasoningGeneration.Adapter : LlamaPreparedGeneration.Adapter;
                Descriptor = actual with { SupportsReasoning = reasoning };
                Binding = service.Binding with { AdapterId = Descriptor.AdapterId, AdapterVersion = Descriptor.Version };
            }
            public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Interlocked.Increment(ref state.Prepared);
                var prepared = new Prepared(state, Binding, request); service.Prepared.Enqueue(prepared);
                return Task.FromResult<IPreparedModelGeneration>(prepared);
            }
        }
    }
    private sealed class Prepared : IPreparedModelGeneration, IBoundGenerationDraft
    {
        private readonly ProviderState state;
        private readonly GenerationDraftChannel draft = new();
        private int generated, disposed;
        internal bool GeneratedOnce => Volatile.Read(ref generated) != 0;
        internal bool DisposedOnce => Volatile.Read(ref disposed) != 0;
        internal ModelGenerationRequest Request { get; }
        public PreparedModelMetadata Metadata { get; }
        internal Prepared(ProviderState state, AdapterBinding binding, ModelGenerationRequest request)
        {
            this.state = state; Request = request;
            Metadata = new(binding.InstanceId, binding.AdapterId, binding.AdapterVersion, request.ModelId, 20,
                request.ContextLimit, request.ResponseLimit, "deterministic fixture count") { ReasoningReserveTokens = request.ReasoningTokens };
        }
        public bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer)
        { state.Bindings.Add(binding); return draft.TryBind(binding, observer); }
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            if (Interlocked.Exchange(ref generated, 1) != 0) throw new InvalidOperationException("Fixture preparation replay.");
            Interlocked.Increment(ref state.Generated); draft.Start();
            const string json = "{\"action\":\"final\",\"text\":\"Configured public draft\"}";
            draft.Append(json);
            if (state.CancelDuringGeneration is { } cancellation)
            {
                cancellation.Cancel(); draft.Complete(false);
                return Task.FromException<ModelGenerationResult>(new OperationCanceledException(token));
            }
            onText?.Invoke(json); Interlocked.Increment(ref state.CanonicalCallbacks); draft.Complete(true);
            return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, json, 6, 20));
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) throw new InvalidOperationException("Fixture preparation disposal repeated.");
            Interlocked.Increment(ref state.Disposed); draft.Complete(false);
            return state.PreparationDisposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(state.PreparationDisposalFailure);
        }
    }
    private sealed class LegacyImages : IModelRuntimeAdapter, IMultimodalModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => LlamaPreparedGeneration.Adapter;
        internal int TextPreparations, SidecarPreparations;
        internal ModelGenerationRequest? LastRequest;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { TextPreparations++; LastRequest = request; return Task.FromResult<IPreparedModelGeneration>(new Prepared(new(), new("legacy", request.ModelId, "legacy", "legacy", "1"), request)); }
        public Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request, IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
        { SidecarPreparations++; return Task.FromResult<IPreparedModelGeneration>(new Prepared(new(), new("legacy", request.ModelId, "legacy", "legacy", "1"), request)); }
    }
    private sealed class NoTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) =>
            throw new InvalidOperationException("Configured adapter checks never dispatch a tool.");
    }
}
