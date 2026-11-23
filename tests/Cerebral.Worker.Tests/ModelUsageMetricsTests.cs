using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Resources;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic adapter and workflow fixtures; no model, network, UI, or hardware is exercised.</summary>
public static class ModelUsageMetricsTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-model-usage-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }

        var store = new StateStore(Path.Combine(root, "data"));
        var configuration = new Configuration(store);
        await using var runtime = new ModelRuntime(store, configuration.Get, new EventHub());
        try
        {
            var initial = runtime.UsageTotals();
            Check(initial.ConfirmedInputTokens == 0 && initial.ConfirmedOutputTokens == 0 &&
                  initial.ConfirmedInputComplete == true && initial.ConfirmedOutputComplete == true,
                "a new Worker reports a complete real zero before any generation is attempted");
            var legacy = CerebralJson.Read<ModelUsageTotals>(CerebralJson.Element(new
                { confirmedInputTokens = 9L, confirmedOutputTokens = 4L }));
            Check(legacy.ConfirmedInputTokens == 9 && legacy.ConfirmedOutputTokens == 4 &&
                  legacy.ConfirmedInputComplete is null && legacy.ConfirmedOutputComplete is null,
                "the additive completeness flags preserve the historical two-number wire shape");
            Check(ReferenceEquals(runtime.TrackExternalUsage(runtime), runtime),
                "the local runtime is never decorated and cannot be double-counted");

            var exact = new FixtureAdapter(new(ModelGenerationFinish.Stop, "done", GeneratedTokens: 17, EvaluatedTokens: 23));
            var tracked = runtime.TrackExternalUsage(exact);
            Check(ReferenceEquals(runtime.TrackExternalUsage(tracked), tracked),
                "an already tracked external adapter cannot be decorated twice");
            await using (var prepared = await tracked.PrepareAsync(Request(), default))
            {
                var result = await prepared.GenerateAsync(null, default);
                Check(result.GeneratedTokens == 17 && result.EvaluatedTokens == 23 && exact.Generations == 1,
                    "the decorator returns the exact external result after one provider generation");
                Check(await RejectAsync<InvalidOperationException>(() => prepared.GenerateAsync(null, default)),
                    "the host one-use gate rejects replay before a second provider call");
                Check(exact.Generations == 1, "a rejected replay cannot increment the provider or usage meter");
            }
            Check(exact.Disposals == 1, "decorating an external generation preserves exact disposal ownership");
            var exactTotals = runtime.UsageTotals();
            Check(exactTotals.ConfirmedInputTokens == 23 && exactTotals.ConfirmedOutputTokens == 17 &&
                  exactTotals.ConfirmedInputComplete == true && exactTotals.ConfirmedOutputComplete == true,
                "one external terminal result contributes its evaluated and generated usage exactly once");

            var partial = runtime.TrackExternalUsage(new FixtureAdapter(
                new(ModelGenerationFinish.Unknown, "", GeneratedTokens: 5, EvaluatedTokens: null,
                    Error: "provider returned bounded terminal uncertainty")));
            await using (var prepared = await partial.PrepareAsync(Request(), default))
                _ = await prepared.GenerateAsync(null, default);
            var partialTotals = runtime.UsageTotals();
            Check(partialTotals.ConfirmedInputTokens == 23 && partialTotals.ConfirmedOutputTokens == 22 &&
                  partialTotals.ConfirmedInputComplete == false && partialTotals.ConfirmedOutputComplete == true,
                "an uncertain result retains an explicit reported side and marks only missing usage incomplete");

            var throwing = new FixtureAdapter(new(ModelGenerationFinish.Stop, "unused")) { GenerationError = new IOException("fixture terminal transport loss") };
            var throwingTracked = runtime.TrackExternalUsage(throwing);
            await using (var prepared = await throwingTracked.PrepareAsync(Request(), default))
            {
                Exception? observed = null;
                try { _ = await prepared.GenerateAsync(null, default); }
                catch (IOException error) { observed = error; }
                Check(ReferenceEquals(observed, throwing.GenerationError),
                    "a generation exception is rethrown unchanged after entering the adapter");
            }
            var thrownTotals = runtime.UsageTotals();
            Check(thrownTotals.ConfirmedInputTokens == 23 && thrownTotals.ConfirmedOutputTokens == 22 &&
                  thrownTotals.ConfirmedInputComplete == false && thrownTotals.ConfirmedOutputComplete == false,
                "a lost terminal provider result makes both accumulated totals lower bounds without inventing counts");

            var preparationFailure = new FixtureAdapter(new(ModelGenerationFinish.Stop, "unused"))
                { PreparationError = new IOException("fixture preparation failed before dispatch") };
            var beforePreparationFailure = runtime.UsageTotals();
            Check(await RejectAsync<IOException>(() => runtime.TrackExternalUsage(preparationFailure).PrepareAsync(Request(), default)),
                "a preparation-only failure is returned without entering generation");
            Check(runtime.UsageTotals() == beforePreparationFailure,
                "a preparation-only failure does not change counts or completeness");

            await using var zeroRuntime = new ModelRuntime(store, configuration.Get, new EventHub());
            var zero = zeroRuntime.TrackExternalUsage(new FixtureAdapter(
                new(ModelGenerationFinish.Stop, "", GeneratedTokens: 0, EvaluatedTokens: 0)));
            await using (var prepared = await zero.PrepareAsync(Request(), default))
                _ = await prepared.GenerateAsync(null, default);
            var zeroTotals = zeroRuntime.UsageTotals();
            Check(zeroTotals.ConfirmedInputTokens == 0 && zeroTotals.ConfirmedOutputTokens == 0 &&
                  zeroTotals.ConfirmedInputComplete == true && zeroTotals.ConfirmedOutputComplete == true,
                "explicit provider zeroes remain complete zeroes rather than unavailable usage");

            await using var cancelledRuntime = new ModelRuntime(store, configuration.Get, new EventHub());
            var cancelled = cancelledRuntime.TrackExternalUsage(new FixtureAdapter(
                new(ModelGenerationFinish.Cancelled, "", GeneratedTokens: null, EvaluatedTokens: 2,
                    CancellationConfirmed: true)));
            await using (var prepared = await cancelled.PrepareAsync(Request(), default))
                _ = await prepared.GenerateAsync(null, default);
            var cancelledTotals = cancelledRuntime.UsageTotals();
            Check(cancelledTotals.ConfirmedInputTokens == 2 && cancelledTotals.ConfirmedOutputTokens == 0 &&
                  cancelledTotals.ConfirmedInputComplete == true && cancelledTotals.ConfirmedOutputComplete == false,
                "a returned cancelled result counts only explicit provider usage and never invents its missing side");

            var multimodalInner = new MultimodalFixtureAdapter(
                new(ModelGenerationFinish.Stop, "image", GeneratedTokens: 3, EvaluatedTokens: 7));
            var multimodal = zeroRuntime.TrackExternalUsage(multimodalInner);
            Check(multimodal is IMultimodalModelRuntimeAdapter,
                "the external decorator preserves verified multimodal capability");
            var image = new RemotePreparedImageInput(0, 0, "image/png", 1, 1, [1, 2, 3]);
            await using (var prepared = await ((IMultimodalModelRuntimeAdapter)multimodal)
                .PrepareMultimodalAsync(Request(), [image], default))
                _ = await prepared.GenerateAsync(null, default);
            var multimodalTotals = zeroRuntime.UsageTotals();
            Check(multimodalInner.MultimodalPreparations == 1 && multimodalInner.TextPreparations == 0 &&
                  multimodalTotals.ConfirmedInputTokens == 7 && multimodalTotals.ConfirmedOutputTokens == 3,
                "multimodal preparation delegates unchanged and contributes one exact terminal usage result");

            var routedProfile = new AgentProfile
            {
                Id = "routed-remote-profile", Name = "Routed remote fixture", Role = "compactor", WorkerId = "local",
                ModelWorkerId = "gb10", ModelId = "routed-remote-model", ContextTokens = 2048, ResponseTokens = 128,
                FollowsModelBudgets = false
            };
            var routedBinding = new WorkerExecutionSnapshot.ModelBinding(routedProfile.ModelId, "/fixture/routed.gguf", null,
                [], 2048, 1, -1, ModelWorkerId: routedProfile.ModelWorkerId,
                ModelWorkerInstallationId: "routed-fixture-installation");
            await using var configuredModels = new ConfiguredWorkflowModelProvider(zeroRuntime);
            var routedAdapters = new WorkerModelAdapters(zeroRuntime, new RemoteWorkerManagement(configuration), null!, null!, configuredModels);
            var routed = routedAdapters.ForCompactor(new(routedProfile, "compact", routedBinding));
            Check(routed is IMultimodalModelRuntimeAdapter && ReferenceEquals(zeroRuntime.TrackExternalUsage(routed), routed),
                "the accepted paired-worker adapter route applies the same meter once and preserves multimodal capability");

            await using var bridgeRuntime = new ModelRuntime(store, configuration.Get, new EventHub());
            var remote = new FixtureAdapter(new(ModelGenerationFinish.Stop, "workflow", GeneratedTokens: 4, EvaluatedTokens: 11));
            var profile = new AgentProfile
            {
                Id = "remote-usage-profile", Name = "Remote usage fixture", Role = "lead", WorkerId = "local",
                ModelWorkerId = "gb10", ModelId = "remote-usage-model", ContextTokens = 2048, ResponseTokens = 128,
                FollowsModelBudgets = false
            };
            store.Put("agent", profile.Id, profile);
            var binding = new WorkerExecutionSnapshot.ModelBinding(profile.ModelId, "/fixture/remote.gguf", null, [],
                2048, 1, -1, ModelWorkerId: profile.ModelWorkerId, ModelWorkerInstallationId: "fixture-installation");
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile, "", "", binding,
                [], [], [], [], false, WorkerExecutionSnapshot.RoutingFingerprint(configuration.Get()), [], []);
            var submission = new WorkflowSubmission("usage-run", "usage-conversation", profile.Id,
                "fixture.workflow", "1", "Measure the verified paired result.", null, WorkflowContract.EmptyObject,
                CerebralJson.Element(snapshot));
            var effects = new WorkerWorkflowEffects(store, configuration, bridgeRuntime, new NoTools(), _ => remote);
            var generate = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && command.Descriptor.Version == 1);
            var completed = await generate.ExecuteAsync(new("usage-effect", submission,
                CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments(
                    [new("user", [new TextContentPart("Count this exact input.")])]))), default);
            var bridgeTotals = bridgeRuntime.UsageTotals();
            Check(completed.State == WorkflowEffectState.Succeeded && remote.Generations == 1 &&
                  bridgeTotals.ConfirmedInputTokens == 11 && bridgeTotals.ConfirmedOutputTokens == 4 &&
                  bridgeTotals.ConfirmedInputComplete == true && bridgeTotals.ConfirmedOutputComplete == true,
                "the durable workflow remote-adapter seam shares the Worker meter without local fallback or double count");

            Console.WriteLine($"Model usage metrics: {checks} deterministic adapter/workflow checks passed; no model, network, UI, or hardware execution.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
            Directory.Delete(root, true);
        }
    }

    private static ModelGenerationRequest Request() => new("remote-usage-model",
        [new("user", [new TextContentPart("fixture usage")])], 2048, 128, Temperature: 0);

    private static async Task<bool> RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return true; }
        return false;
    }

    private class FixtureAdapter : IModelRuntimeAdapter
    {
        protected ModelGenerationResult Result { get; }
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.usage", "1", ["text"]);
        public Exception? PreparationError;
        public Exception? GenerationError;
        public int TextPreparations;
        public int Generations;
        public int Disposals;

        public FixtureAdapter(ModelGenerationResult result) => Result = result;

        public virtual Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PreparationError is not null) return Task.FromException<IPreparedModelGeneration>(PreparationError);
            TextPreparations++;
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, Result, request));
        }

        protected sealed class Prepared(FixtureAdapter owner, ModelGenerationResult result, ModelGenerationRequest request)
            : IPreparedModelGeneration
        {
            private int used;
            private int disposed;
            public PreparedModelMetadata Metadata { get; } = new("fixture-instance", owner.Descriptor.AdapterId,
                owner.Descriptor.Version, request.ModelId, 13, request.ContextLimit, request.ResponseLimit, "fixture exact usage");

            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.CompareExchange(ref used, 1, 0) != 0)
                    throw new InvalidOperationException("Fixture generation is one-use.");
                owner.Generations++;
                return owner.GenerationError is null
                    ? Task.FromResult(result)
                    : Task.FromException<ModelGenerationResult>(owner.GenerationError);
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Disposals++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class MultimodalFixtureAdapter(ModelGenerationResult result) : FixtureAdapter(result), IMultimodalModelRuntimeAdapter
    {
        public int MultimodalPreparations;

        public Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
            IReadOnlyList<RemotePreparedImageInput> images, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (images.Count != 1 || images[0].Data.Length != 3)
                throw new InvalidOperationException("Fixture did not receive the exact image sidecar.");
            MultimodalPreparations++;
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, Result, request));
        }
    }

    private sealed class NoTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) =>
            throw new InvalidOperationException("The usage fixture does not dispatch tools.");
    }
}
