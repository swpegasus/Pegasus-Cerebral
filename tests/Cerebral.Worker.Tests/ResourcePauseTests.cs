using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ResourcePauseTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            assertions++;
            Console.WriteLine("PASS: " + label);
        }

        await AdmissionDrainAndResumeAsync(Check);
        await ModelRuntimeAdmissionGateAsync(Check);
        await DurableRestartAndRemoteModelAdmissionAsync(Check);
        await FailedResumeRetainsDurablePauseAsync(Check);
        await RemotePlacementDoesNotReloadFormerLocalModelAsync(Check);
        await NoPeerFallbackAsync(Check);
        await DeterministicPeerRoutingAsync(Check);
        await IneligiblePeersKeepLocalAsync(Check);
        await ResumePreservesAcceptedRemoteAssignmentAsync(Check);
        Console.WriteLine($"Resource pause: {assertions} deterministic checks passed; no model process or network peer was used.");
    }

    private static async Task ModelRuntimeAdmissionGateAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-resource-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new StateStore(root);
        var configuration = new Configuration(store);
        var events = new EventHub();
        var modelPath = Path.Combine(root, "fixture.gguf");
        await File.WriteAllBytesAsync(modelPath, "GGUF"u8.ToArray());
        store.Put("model", "fixture", new ModelRecord { Id = "fixture", Name = "Fixture", Path = modelPath });
        await using var runtime = new ModelRuntime(store, configuration.Get, events);
        try
        {
            var resident = await runtime.BeginPauseAsync(default);
            var rejected = false;
            try { _ = await runtime.LoadAsync("fixture", default); }
            catch (InvalidOperationException error) when (error.Message.Contains("paused", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
            check(resident.Count == 0 && rejected,
                "the model runtime itself rejects new local model admission after the pause gate closes");
            check(store.Get<ModelRecord>("model", "fixture") is { State: "unloaded", Error: null },
                "a pre-start pause denial retains the model's unloaded state without recording a false runtime failure");
            await runtime.UnloadPausedAsync(default);
            await runtime.ResumePausedAsync([], default);
            var commitRejected = false;
            try
            {
                await runtime.ReopenPausedAdmissionAsync(
                    () => throw new IOException("fixture durable commit failure"), default);
            }
            catch (IOException error) when (error.Message.Contains("durable commit", StringComparison.Ordinal))
            {
                commitRejected = true;
            }
            var stillPaused = false;
            try { _ = await runtime.LoadAsync("fixture", default); }
            catch (InvalidOperationException error) when (error.Message.Contains("paused", StringComparison.OrdinalIgnoreCase)) { stillPaused = true; }
            check(commitRejected && stillPaused,
                "a failed durable active-state commit leaves runtime admission closed for the current and next process generations");
            check(store.Get<ModelRecord>("model", "fixture") is { State: "unloaded", Error: null },
                "repeated pause denial after a failed resume commit still leaves the model unloaded without a failure");
            await runtime.ReopenPausedAdmissionAsync(() => { }, default);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task AdmissionDrainAndResumeAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Action = async (job, token) =>
        {
            var call = Interlocked.Increment(ref fixture.Executor.Calls);
            if (call == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            else secondEntered.TrySetResult();
            job.Status = "completed";
            job.Result = "fixture complete";
        };

        _ = fixture.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "in-flight fixture" });
        await fixture.Scheduler.StartAsync(default);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var draining = await fixture.Scheduler.PauseLocalResourcesAsync(false);
        var queued = fixture.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "must wait while paused" });
        await Task.Delay(250);
        check(draining.State == LocalResourceState.Draining && fixture.Executor.Calls == 1 &&
              fixture.Scheduler.Get(queued.Id).Status == "queued",
            "pause closes local scheduler admission while preserving newly accepted owner work in the durable queue");
        check(!fixture.Models.UnloadCalled,
            "model unload is not attempted while an in-flight assignment still owns its execution boundary");

        releaseFirst.TrySetResult();
        await fixture.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
        var paused = fixture.Scheduler.GetResourcePauseStatus();
        check(paused.State == LocalResourceState.Paused && fixture.Models.UnloadCalled &&
              fixture.Models.Events.SequenceEqual(["begin", "unload"]),
            "the model control unloads only after the scheduler reaches a confirmed safe boundary");
        await Task.Delay(250);
        check(fixture.Executor.Calls == 1 && fixture.Scheduler.Get(queued.Id).Status == "queued",
            "paused local work remains queued without acquiring a GPU execution slot");

        var resumed = await fixture.Scheduler.ResumeLocalResourcesAsync();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(resumed.State == LocalResourceState.Active && fixture.Models.Events.SequenceEqual(["begin", "unload", "resume:fixture", "reopen"]),
            "resume reloads the previously resident model set before reopening local admission");
    }

    private static async Task DurableRestartAndRemoteModelAdmissionAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-resource-restart-" + Guid.NewGuid().ToString("N"));
        await using (var first = await Fixture.OpenAsync(root, initialize: true, deleteOnDispose: false))
        {
            await first.Scheduler.PauseLocalResourcesAsync(true);
            await first.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
            var saved = first.Store.Get<LocalResourcePauseRecord>(ResourcePausePersistence.RecordKind,
                ResourcePausePersistence.RecordId);
            check(saved is { PauseRequested: true, State: LocalResourceState.Paused, RerouteEligibleWork: true } &&
                  saved.PrePauseResidentModelIds.SequenceEqual(["fixture"]),
                "pause intent and the pre-pause resident model set are saved in the bounded typed service record");
        }

        await using (var restored = await Fixture.OpenAsync(root, initialize: false, deleteOnDispose: false))
        {
            var restoredStatus = restored.Scheduler.GetResourcePauseStatus();
            var rejected = false;
            try { _ = await restored.Runtime.LoadAsync("fixture", default); }
            catch (InvalidOperationException error) when (error.Message.Contains("paused", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
            check(restoredStatus is { State: LocalResourceState.Paused, RerouteEligibleWork: true } &&
                  restoredStatus.ModelsToReload.SequenceEqual(["fixture"]) && rejected,
                "a replacement scheduler and model runtime restore closed local GPU admission before any model load");

            var remoteFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var localFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            restored.Executor.Action = (job, _) =>
            {
                job.Status = "completed"; job.Result = "fixture complete";
                if (job.ProfileId == "remote-model") remoteFinished.TrySetResult();
                if (job.ProfileId == "lead") localFinished.TrySetResult();
                return Task.CompletedTask;
            };
            var local = restored.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "local GPU must wait" });
            var remote = restored.Scheduler.Submit(new() { ProfileId = "remote-model", Prompt = "local tools with remote inference may run" });
            await restored.Scheduler.StartAsync(default);
            await remoteFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(150);
            var whilePaused = restored.Scheduler.GetResourcePauseStatus();
            check(restored.Scheduler.Get(remote.Id).Status == "completed" &&
                  restored.Scheduler.Get(local.Id).Status == "queued" &&
                  whilePaused.QueuedLocalAssignments == 1,
                "paused admission classifies the frozen model route, allowing local execution with remote inference while retaining local-GPU work");

            var resumed = await restored.Scheduler.ResumeLocalResourcesAsync();
            await localFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(resumed.State == LocalResourceState.Active &&
                  restored.Store.Get<LocalResourcePauseRecord>(ResourcePausePersistence.RecordKind,
                      ResourcePausePersistence.RecordId) is null &&
                  restored.Models.Events.SequenceEqual(["resume:fixture", "reopen"]),
                "an explicit successful resume clears the durable intent only after restoring the saved resident set");
        }

        await using var final = await Fixture.OpenAsync(root, initialize: false, deleteOnDispose: true);
        check(final.Scheduler.GetResourcePauseStatus().State == LocalResourceState.Active &&
              !ResourcePausePersistence.Read(final.Store).PauseRequested,
            "a later restart remains active after the explicit successful resume cleared the pause intent");
    }

    private static async Task NoPeerFallbackAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync(new FakePeers([]));
        var queued = fixture.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "no peer fixture" });
        await fixture.Scheduler.PauseLocalResourcesAsync(true);
        await fixture.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
        var status = fixture.Scheduler.GetResourcePauseStatus();
        check(status.State == LocalResourceState.Paused && status.RerouteEligibleWork &&
              status.RoutedAssignments == 0 && fixture.Scheduler.Get(queued.Id).WorkerId == "local" &&
              fixture.Scheduler.Get(queued.Id).Status == "queued",
            "no eligible peer leaves the exact assignment queued locally without changing ownership");
    }

    private static async Task FailedResumeRetainsDurablePauseAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Scheduler.PauseLocalResourcesAsync(false);
        await fixture.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
        fixture.Models.ResumeError = new IOException("fixture reload failure");
        var status = await fixture.Scheduler.ResumeLocalResourcesAsync();
        var retained = fixture.Store.Get<LocalResourcePauseRecord>(ResourcePausePersistence.RecordKind,
            ResourcePausePersistence.RecordId);
        check(status.State == LocalResourceState.Faulted && retained is { PauseRequested: true } &&
              !fixture.Models.Events.Contains("reopen", StringComparer.Ordinal),
            "a failed reload keeps both runtime admission and the durable restart intent closed");
    }

    private static async Task RemotePlacementDoesNotReloadFormerLocalModelAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Scheduler.PauseLocalResourcesAsync(false);
        await fixture.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
        var lead = fixture.Store.Get<AgentProfile>("agent", "lead")!;
        lead.ModelWorkerId = "remote-worker";
        fixture.Store.Put("agent", lead.Id, lead);
        var resumed = await fixture.Scheduler.ResumeLocalResourcesAsync();
        check(resumed.State == LocalResourceState.Active &&
              fixture.Models.Events.SequenceEqual(["begin", "unload", "resume:", "reopen"]),
            "resume does not reload a formerly resident local model after its current profile moved to remote inference");
    }

    private static async Task DeterministicPeerRoutingAsync(Action<bool, string> check)
    {
        var peer = new ResourcePausePeerStatus("paired", "Fixture peer", true, 37, 1, 0, ["lead"]);
        await using var fixture = await Fixture.CreateAsync(new FakePeers([peer]));
        var routed = fixture.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "route fixture" });
        var fallback = fixture.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "capacity fallback" });
        await fixture.Scheduler.PauseLocalResourcesAsync(true);
        await fixture.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
        var status = fixture.Scheduler.GetResourcePauseStatus();
        check(fixture.Scheduler.Get(routed.Id).WorkerId == "paired" &&
              fixture.Scheduler.Get(fallback.Id).WorkerId == "local" && status.RoutedAssignments == 1,
            "deterministic paired capacity routes only the supported legacy root assignment that fits the sampled slot");
        check(status.Peers.Single() is { Reachable: true, RoundTripLatencyMilliseconds: 37, AvailableSlots: 1 },
            "resource status exposes the bounded peer latency and capacity sample used for routing");
    }

    private static async Task IneligiblePeersKeepLocalAsync(Action<bool, string> check)
    {
        var cases = new (string Label, ResourcePausePeerStatus Peer)[]
        {
            ("that is offline", new("paired", "Offline peer", false, null, null, null, ["lead"], "Fixture offline")),
            ("without capacity", new("paired", "Busy peer", true, 1, 0, 1, ["lead"])),
            ("without the exact shared receiver profile", new("paired", "Unmatched peer", true, 1, 1, 0, ["other"]))
        };
        foreach (var (label, peer) in cases)
        {
            await using var fixture = await Fixture.CreateAsync(new FakePeers([peer]));
            var queued = fixture.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "must retain local assignment" });
            var originalProfile = CerebralJson.Element(queued.Profile).GetRawText();
            await fixture.Scheduler.PauseLocalResourcesAsync(true);
            await fixture.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
            var retained = fixture.Scheduler.Get(queued.Id);
            check(fixture.Scheduler.GetResourcePauseStatus() is { State: LocalResourceState.Paused, RoutedAssignments: 0 } &&
                  retained.WorkerId == "local" && retained.Status == "queued" &&
                  retained.RemoteSubmission is null && CerebralJson.Element(retained.Profile).GetRawText() == originalProfile,
                $"a peer {label} leaves the accepted local assignment and frozen grants unchanged");
        }
    }

    private static async Task ResumePreservesAcceptedRemoteAssignmentAsync(Action<bool, string> check)
    {
        var peer = new ResourcePausePeerStatus("paired", "Approved fixture peer", true, 1, 1, 0, ["lead"]);
        await using var fixture = await Fixture.CreateAsync(new FakePeers([peer]));
        var queued = fixture.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "preserve remote ownership" });
        await fixture.Scheduler.PauseLocalResourcesAsync(true);
        await fixture.Scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
        var accepted = fixture.Scheduler.Get(queued.Id);
        accepted.Status = "remote-waiting";
        accepted.RemoteJobId = accepted.Id;
        accepted.RemoteSessionId = "remote-session";
        accepted.RemoteConfirmed = true;
        accepted.RemoteSubmission = new()
        {
            RequestId = accepted.Id, SessionId = accepted.RemoteSessionId,
            ProfileId = "lead", Prompt = accepted.Prompt
        };
        fixture.Store.Put("job", accepted.Id, accepted);
        var before = CerebralJson.Element(accepted).GetRawText();
        var resumed = await fixture.Scheduler.ResumeLocalResourcesAsync();
        var after = fixture.Scheduler.Get(accepted.Id);
        check(resumed.State == LocalResourceState.Active &&
              CerebralJson.Element(after).GetRawText() == before,
            "reactivating local admission preserves the confirmed remote assignment, request identity and frozen grants");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, StateStore store, Configuration configuration, ModelRuntime runtime,
            FakeModels models, ControlledExecutor executor, JobScheduler scheduler, bool deleteOnDispose)
        {
            Root = root; Store = store; Configuration = configuration; Runtime = runtime;
            Models = models; Executor = executor; Scheduler = scheduler; DeleteOnDispose = deleteOnDispose;
        }
        public string Root { get; }
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public ModelRuntime Runtime { get; }
        public FakeModels Models { get; }
        public ControlledExecutor Executor { get; }
        public JobScheduler Scheduler { get; }
        private bool DeleteOnDispose { get; }

        public static async Task<Fixture> CreateAsync(IResourcePausePeerProbe? peers = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "cerebral-resource-pause-" + Guid.NewGuid().ToString("N"));
            return await OpenAsync(root, initialize: true, deleteOnDispose: true, peers: peers);
        }

        public static async Task<Fixture> OpenAsync(string root, bool initialize, bool deleteOnDispose,
            IResourcePausePeerProbe? peers = null)
        {
            Directory.CreateDirectory(root);
            var store = new StateStore(root);
            var configuration = new Configuration(store);
            if (initialize)
            {
                var settings = configuration.Get();
                settings.MaxConcurrentJobs = 1;
                configuration.Save(settings);
                var modelPath = Path.Combine(root, "fixture.gguf");
                await File.WriteAllBytesAsync(modelPath, "GGUF"u8.ToArray());
                store.Put("model", "fixture", new ModelRecord { Id = "fixture", Name = "Fixture", Path = modelPath });
                store.Put("agent", "lead", new AgentProfile
                {
                    Id = "lead", Name = "Lead", Role = "lead", ModelId = "fixture", WorkerId = "local", ModelWorkerId = "local",
                    MaxSteps = 4, MaxSeconds = 30, Capabilities = ["diagnostics"]
                });
                store.Put("agent", "remote-model", new AgentProfile
                {
                    Id = "remote-model", Name = "Remote model", Role = "helper", ModelId = "remote-fixture",
                    WorkerId = "local", ModelWorkerId = "remote-worker", MaxSteps = 4, MaxSeconds = 30,
                    Capabilities = ["diagnostics"]
                });
            }
            var events = new EventHub();
            var runtime = new ModelRuntime(store, configuration.Get, events);
            var models = new FakeModels();
            var executor = new ControlledExecutor();
            var scheduler = new JobScheduler(store, configuration, executor, runtime, events,
                NullLogger<JobScheduler>.Instance, resourceModels: models, resourcePeers: peers ?? new FakePeers([]));
            return new(root, store, configuration, runtime, models, executor, scheduler, deleteOnDispose);
        }

        public async ValueTask DisposeAsync()
        {
            await Scheduler.StopAsync(default);
            Scheduler.Dispose();
            await Runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var exact = Path.GetFullPath(Root);
            if (!exact.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
            if (DeleteOnDispose) Directory.Delete(exact, true);
        }
    }

    private sealed class ControlledExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Func<AgentJob, CancellationToken, Task> Action { get; set; } = (_, _) => Task.CompletedTask;
        public int Calls;
        public Task RunAsync(AgentJob job, CancellationToken token) => Action(job, token);
    }

    private sealed class FakeModels : IResourcePauseModelControl
    {
        public List<string> Events { get; } = [];
        public bool UnloadCalled { get; private set; }
        public Exception? ResumeError { get; set; }
        public Task<IReadOnlyList<string>> BeginPauseAsync(CancellationToken cancellationToken)
        {
            Events.Add("begin");
            return Task.FromResult<IReadOnlyList<string>>(["fixture"]);
        }
        public Task UnloadPausedAsync(CancellationToken cancellationToken)
        {
            UnloadCalled = true;
            Events.Add("unload");
            return Task.CompletedTask;
        }
        public Task ResumePausedAsync(IReadOnlyList<string> modelIds, CancellationToken cancellationToken)
        {
            Events.Add("resume:" + string.Join(',', modelIds));
            if (ResumeError is not null) return Task.FromException(ResumeError);
            return Task.CompletedTask;
        }
        public Task ReopenPausedAdmissionAsync(Action markDurablyActive, CancellationToken cancellationToken)
        {
            markDurablyActive();
            Events.Add("reopen");
            return Task.CompletedTask;
        }
    }

    private sealed class FakePeers(IReadOnlyList<ResourcePausePeerStatus> peers) : IResourcePausePeerProbe
    {
        public Task<IReadOnlyList<ResourcePausePeerStatus>> ProbeAsync(IReadOnlyList<WorkerRegistration> workers,
            CancellationToken cancellationToken) => Task.FromResult(peers);
    }
}
