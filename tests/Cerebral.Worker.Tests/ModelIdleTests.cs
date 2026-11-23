using System.Reflection;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Persistence;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real health-server process, SQLite and separately compiled strategies; virtual elapsed
/// time and deterministic model/feature behavior. Does not load a model or perform inference.</summary>
internal static class ModelIdleTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-model-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new Clock(); var store = new StateStore(Path.Combine(root, "data"));
        var configuration = new Configuration(store); var events = new EventHub();
        await using var runtime = new ModelRuntime(store, configuration.Get, events, clock);
        var engineRoot = Path.Combine(root, "engines");
        EngineHostTests.Pack(engineRoot, "A", true);
        await using var engine = new EngineController(engineRoot, Path.Combine(root, "selection"));
        var previous = Environment.GetEnvironmentVariable(ModelRuntimeCleanupTests.FixtureEnvironment);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        const string id = "idle-fixture";
        bool Loaded() => runtime.IsLoaded(id);
        void SetIdle(int minutes) { var model = store.Get<ModelRecord>("model", id)!; model.IdleMinutes = minutes; store.Put("model", id, model); }
        async Task Scan(Func<string, bool>? activity = null) => await runtime.UnloadIdleAsync(CancellationToken.None, activity, engine);
        async Task Load()
        {
            Environment.SetEnvironmentVariable(ModelRuntimeCleanupTests.FixtureEnvironment, Path.Combine(root, Guid.NewGuid().ToString("N") + ".signal"));
            try { await runtime.LoadAsync(id, CancellationToken.None); }
            finally { Environment.SetEnvironmentVariable(ModelRuntimeCleanupTests.FixtureEnvironment, previous); }
        }
        async Task Replace(string mode)
        {
            var status = engine.Status(); engine.Pause(status.Epoch);
            EngineHostTests.Pack(engineRoot, "B", false, mode);
            status = await engine.ActivateAsync("B", status.Epoch); engine.Resume(status.Epoch);
        }
        async Task Rollback()
        {
            var status = engine.Status(); engine.Pause(status.Epoch);
            status = await engine.RollbackAsync(status.Epoch); engine.Resume(status.Epoch);
        }
        try
        {
            var settings = configuration.Get(); settings.LlamaServerPath = ModelRuntimeCleanupTests.AppHost();
            settings.ReservedMemoryMb = 0; settings.DefaultModelIdleMinutes = 2; configuration.Save(settings);
            var path = Path.Combine(root, "fixture.gguf"); await File.WriteAllBytesAsync(path, "GGUF"u8.ToArray());
            store.Put("model", id, new ModelRecord { Id = id, Name = "Idle fixture", Path = path, SizeBytes = 4,
                ContextTokens = 2048, MaximumContextTokens = 2048, ParallelRequests = 1, GpuLayers = 0,
                IdleMinutes = 1, AutomaticContextSizing = false });
            await engine.InitializeAsync(); await Load();
            Check(engine.SupportsWork("cerebral.model.idle", 1), "default separately loaded engine advertises the optional idle adapter");
            clock.Advance(59); await Scan(); Check(Loaded(), "short inactivity retains the model");
            var job = new AgentJob { Id = "active", Status = "running", Profile = new AgentProfile { ModelId = id, ModelWorkerId = "local" } };
            store.Put("job", job.Id, job); clock.Advance(600); await Scan();
            Check(Loaded(), "a long accepted tool/helper wait outlives the last inference without unloading");
            job.Status = "completed"; store.Put("job", job.Id, job); await Scan();
            Check(Loaded(), "finishing work starts a complete new grace period");
            clock.Advance(59); await Scan(); Check(Loaded(), "the complete grace period survives until its boundary");
            clock.Advance(1); await Scan(); Check(!Loaded(), "the default engine and host release a truly idle owned process at the boundary");

            await Load(); SetIdle(0); clock.Advance(600); await Scan(); Check(Loaded(), "zero idle minutes keeps the model resident");
            SetIdle(-1); await Scan(); Check(!Loaded(), "an inherited positive service delay is honored after genuine inactivity");
            await Load(); clock.Advance(119); await Scan(); Check(Loaded(), "service default delay is two minutes in this fixture");
            // Wall-clock adjustment must not turn 119 seconds into a completed delay.
            clock.JumpWallClock(TimeSpan.FromDays(3)); await Scan(); Check(Loaded(), "wall-clock changes cannot advance monotonic inactivity");
            clock.Advance(1); await Scan(); Check(!Loaded(), "inherited delay releases only at its monotonic boundary");

            await Load(); SetIdle(1);
            var instance = ModelRuntimeCleanupTests.CurrentInstance(runtime, id);
            var busy = instance.GetType().GetField("Busy", BindingFlags.Instance | BindingFlags.Public)!;
            busy.SetValue(instance, 1); clock.Advance(600); await Scan(); Check(Loaded(), "an occupied or quarantined inference slot prevents automatic release");
            busy.SetValue(instance, 0); await Scan(); clock.Advance(59); await Scan(); Check(Loaded(), "slot release also starts a full grace period");

            var journal = new EventJournal(Path.Combine(root, "workflows.db"));
            var workflowHost = new WorkerWorkflows(store, configuration, events, [], [], journal: journal);
            var workflows = workflowHost.Store;
            for (var index = 0; index < 201; index++)
            {
                var modelId = index == 200 ? id : "other-model";
                workflows.Submit(new("work-" + index, "chat-" + index, "profile", "fixture:work", "1", "test", null,
                    WorkflowContract.EmptyObject, CerebralJson.Element(new { modelId, modelWorkerId = "local" })));
            }
            bool Interest(string model) => workflowHost.HasActiveLocalModelWork(model);
            clock.Advance(600); await Scan(Interest);
            Check(Loaded(), "an active binding beyond 200 viewport rows still protects its model");
            workflows.Cancel("work-200"); await Scan(Interest); clock.Advance(59); await Scan(Interest);
            Check(Loaded(), "ending a new workflow starts the delay while unrelated active work does not hold this model");
            var remote = workflows.Submit(new("remote-main", "remote-chat", "profile", "fixture:work", "1", "test", null,
                WorkflowContract.EmptyObject, CerebralJson.Element(new { profile = new { modelId = id, modelWorkerId = "paired-remote" } })));
            Check(!Interest(id), "same-named remote models do not pin a local model");
            var compact = workflows.Submit(new("local-compactor", "compact-chat", "profile", "fixture:work", "1", "test", null,
                WorkflowContract.EmptyObject, CerebralJson.Element(new { profile = new { modelId = "remote", modelWorkerId = "paired-remote" },
                    compactor = new { profile = new { modelId = id, modelWorkerId = "local" } } })));
            Check(Interest(id), "the workflow's frozen local compactor is retained across a remote main-agent tool wait");
            workflows.Cancel(compact.Id); workflows.Cancel(remote.Id);

            await Replace("idle-retain"); clock.Advance(1); await Scan();
            Check(Loaded(), "a replaceable engine may retain an idle model without host or shared-SDK replacement");
            await Rollback(); await Replace("slow-idle");
            var scanning = Scan(); await Until(() => engine.Status().ActiveEvaluations == 1);
            SetIdle(0); await scanning;
            Check(Loaded(), "a concurrent owner policy edit rejects the previously prepared unload decision");
            SetIdle(1); scanning = Scan(); await Until(() => engine.Status().ActiveEvaluations == 1);
            busy.SetValue(instance, 1); await scanning; busy.SetValue(instance, 0);
            Check(Loaded(), "a newly occupied slot invalidates the pending engine unload decision");
            await Scan(); clock.Advance(60);
            await Rollback(); await Replace("bad-idle");
            try { await Scan(); throw new Exception("Invalid idle decision was accepted."); }
            catch (EngineEvaluationException) { Check(Loaded() && engine.IsPaused, "malformed strategy output pauses the engine and retains the actual process"); }
            await Rollback(); await Scan(); Check(!Loaded(), "rollback restores the default release policy against the latest host state");
            await Load(); var retainedInstance = ModelRuntimeCleanupTests.CurrentInstance(runtime, id);
            clock.Advance(600); await runtime.SetIdleMinutesAsync(id, 2, CancellationToken.None); await Scan();
            Check(Loaded() && ReferenceEquals(retainedInstance, ModelRuntimeCleanupTests.CurrentInstance(runtime, id)),
                "editing the live idle delay preserves the exact resident process and starts a fresh grace period");
            clock.Advance(119); await Scan(); Check(Loaded(), "live delay changes use the newly saved value without a runtime restart");
            try { await runtime.SetIdleMinutesAsync(id, 10081, CancellationToken.None); throw new Exception("Invalid idle setting was accepted."); }
            catch (ArgumentOutOfRangeException) { Check(store.Get<ModelRecord>("model", id)!.IdleMinutes == 2 && Loaded(), "invalid live idle settings preserve saved policy and resident process"); }
            Console.WriteLine($"Model idle: {checks} checks passed (real managed health-server children and SQLite; separately compiled engines; virtual elapsed time; no model inference).");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelRuntimeCleanupTests.FixtureEnvironment, previous);
            await runtime.DisposeAsync(); await engine.DisposeAsync(); SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(root, true);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private sealed class Clock : TimeProvider
    {
        private long milliseconds;
        private DateTimeOffset wall = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public override DateTimeOffset GetUtcNow() => wall;
        public void Advance(int seconds) { milliseconds += seconds * 1000L; wall += TimeSpan.FromSeconds(seconds); }
        public void JumpWallClock(TimeSpan amount) => wall += amount;
    }
}
