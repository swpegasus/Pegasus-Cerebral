using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Real in-process scheduler/SQLite/engine with deterministic handlers; no model, network, UI or owner data.</summary>
internal static class IntentionalWorkHoldTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        await OwnerContractAsync(Check);
        await ModernGracefulAsync(Check);
        await ModernStopAndRestartAsync(Check);
        await LegacyStopAsync(Check);
        await LegacyGracefulAsync(Check);
        await PauseWriteFailureAsync(Check);
        await BoundedCaptureAsync(Check);
        Console.WriteLine($"Intentional work hold checks passed: {checks} (deterministic handlers; actual in-process engine/scheduler/SQLite; no model/network/live owner mutation).");
    }

    private static async Task OwnerContractAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        var initial = fixture.Scheduler.GetIntentionalWorkHoldStatus();
        check(initial.State == "none" && initial.Generation == 0 && initial.Revision == 0 &&
            !initial.StartupPromptRequired, "ordinary boot has no intentional work startup prompt");
        var paused = await fixture.Command("engine.pause", new { expectedEpoch = initial.EngineEpoch });
        check(paused.Success && !fixture.Scheduler.GetIntentionalWorkHoldStatus().StartupPromptRequired,
            "ordinary engine pause does not become intentionally held work");
        check((await fixture.Command("engine.resume", new { expectedEpoch = initial.EngineEpoch })).Success,
            "ordinary engine resume remains compatible");
        var waiting = fixture.Workflow("old-waiting");
        var claimed = fixture.Host.Store.TryClaim(waiting.Id)!;
        fixture.Host.Store.Hold(waiting.Id, claimed.Revision, "Old QA waiting artifact.");
        var unknown = fixture.Workflow("old-unknown");
        fixture.Dispatch(unknown.Id);
        fixture.Host.Store.RecoverInterrupted();
        var ready = fixture.Workflow("owner-ready");
        var requestId = Guid.NewGuid().ToString("N");
        var args = new { requestId, mode = "finish-current", expectedEpoch = initial.EngineEpoch,
            expectedGeneration = 0L, expectedRevision = 0L };
        check(!(await fixture.Command("engine.work-hold", args, "lead")).Success, "administrative-capable agent cannot create owner hold");
        check(!(await fixture.Command("engine.work-hold", new { requestId, mode = "invalid",
            args.expectedEpoch, args.expectedGeneration, args.expectedRevision })).Success, "malformed mode rejected before creating hold");
        check(!(await fixture.Command("engine.work-hold", new { requestId, mode = "finish-current",
            expectedEpoch = initial.EngineEpoch + 1, expectedGeneration = 0L, expectedRevision = 0L })).Success,
            "stale engine epoch rejected before creating hold");
        var hold = await fixture.Hold(args);
        check(hold.State == "held" && hold.StartupPromptRequired && hold.EnginePaused &&
            hold.Work.Count == 1 && hold.Work[0].Id == ready.Id,
            "explicit marker captures only ready work, excludes inactive historical Waiting/Unknown");
        var repeated = await fixture.Hold(args);
        check(repeated.HoldId == hold.HoldId && repeated.Revision == hold.Revision &&
            repeated.Generation == hold.Generation, "identical request retry does not recapture work or increment hold");
        check(!(await fixture.Command("engine.work-hold", new { requestId, mode = "stop-now",
            args.expectedEpoch, args.expectedGeneration, args.expectedRevision })).Success, "requestId cannot be reused for another mode");
        check(!(await fixture.Command("engine.resume", new { expectedEpoch = hold.EngineEpoch })).Success,
            "direct engine resume cannot bypass an active intentional hold");
        check(!(await fixture.Command("engine.work-hold.resume", new { holdId = hold.HoldId,
            expectedEpoch = hold.EngineEpoch, expectedGeneration = hold.Generation, expectedRevision = hold.Revision - 1 })).Success,
            "hold revision compare rejects stale resolution");
        var invalid = await fixture.Dispatcher.ExecuteAsync(new("engine.work-hold.status",
            JsonDocument.Parse("{\"unexpected\":1}").RootElement.Clone()));
        check(!invalid.Success, "status rejects undeclared fields");
        var duplicate = await fixture.Dispatcher.ExecuteAsync(new("engine.work-hold",
            JsonDocument.Parse($"{{\"requestId\":\"{requestId}\",\"requestId\":\"{requestId}\",\"mode\":\"finish-current\",\"expectedEpoch\":{initial.EngineEpoch},\"expectedGeneration\":0,\"expectedRevision\":0}}").RootElement.Clone()));
        check(!duplicate.Success, "duplicate owner arguments rejected");
        var descriptor = await fixture.Command("sdk.describe", new { id = "engine.work-hold" });
        check(descriptor.Success && descriptor.Data.GetProperty("ownerOnly").GetBoolean(),
            "additive operation is discoverable as owner only");
        var cancelled = await fixture.Resolve(hold, cancel: true);
        check(cancelled.State == "cancelled" && cancelled.EnginePaused && !cancelled.StartupPromptRequired &&
            fixture.Host.Store.Get(ready.Id).CancelRequested &&
            fixture.Host.Store.Get(unknown.Id).Effect?.State == WorkflowEffectState.Unknown &&
            !fixture.Host.Store.Get(unknown.Id).CancelRequested,
            "explicit cancel affects only captured work, preserves historical Unknown and engine pause");
        check((await fixture.Command("engine.resume", new { expectedEpoch = cancelled.EngineEpoch })).Success,
            "normal resume available after intentional marker resolved");
    }

    private static async Task ModernGracefulAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Action = async (_, token) => { await fixture.Handler.Release.Task.WaitAsync(token); return new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject); };
        var first = fixture.Workflow("modern-graceful");
        var next = fixture.Workflow("modern-graceful-next");
        await fixture.Start();
        try { await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) {
            var failed = fixture.Host.Store.Get(first.Id);
            throw new InvalidOperationException($"Fixture never reached command: status={failed.Status}, error={failed.Error}, waiting={failed.WaitingReason}, engine={fixture.Controller.Status().Error}");
        }
        var before = fixture.Host.Store.Get(first.Id);
        var hold = await fixture.CreateHold("finish-current");
        check(hold.State == "holding" && hold.Work.Count == 2 && hold.Work.Single(x => x.Id == first.Id).IsExecuting,
            "graceful hold reports exact executing and queued work");
        await Task.Delay(350);
        check(fixture.Handler.Calls == 1 && !fixture.Handler.Cancelled &&
            fixture.Host.Store.Get(first.Id).Effect?.Id == before.Effect?.Id,
            "graceful hold does not interrupt the current operation or claim the next queue entry");
        check(!(await fixture.ResolveCommand(hold, false)).Success, "resume rejected while current captured execution is draining");
        fixture.Handler.Release.TrySetResult();
        await Until(() => fixture.Scheduler.GetIntentionalWorkHoldStatus().State == "held");
        var retained = fixture.Host.Store.Get(first.Id);
        check(retained.Status == WorkflowStatus.Queued && retained.Effect is { State: WorkflowEffectState.Succeeded } &&
            !retained.CancelRequested, "completed operation stays confirmed and accepted work yields for engine pause");
        var resolved = await fixture.Resolve(fixture.Scheduler.GetIntentionalWorkHoldStatus(), false);
        await Until(() => fixture.Host.Store.Get(first.Id).Status == WorkflowStatus.Completed &&
            fixture.Host.Store.Get(next.Id).Status == WorkflowStatus.Completed);
        check(resolved.State == "resumed" && !resolved.EnginePaused && fixture.Handler.Calls == 2 &&
            !resolved.StartupPromptRequired, "resume continues prepared state without replaying the first completed operation");
    }

    private static async Task ModernStopAndRestartAsync(Action<bool, string> check)
    {
        var root = Fixture.NewRoot(); string heldId; string effectId; long generation; long revision;
        await using (var fixture = await Fixture.OpenAsync(root, true, false))
        {
            fixture.Handler.Action = async (_, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
                catch (OperationCanceledException)
                {
                    var status = fixture.Scheduler.GetIntentionalWorkHoldStatus();
                    fixture.Handler.BarrierBeforeCancellation = status.EnginePaused && status.HoldId is not null;
                    throw;
                }
            };
            var run = fixture.Workflow("modern-stop");
            fixture.Workflow("modern-stop-next");
            await fixture.Start();
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            effectId = fixture.Host.Store.Get(run.Id).Effect!.Id;
            var held = await fixture.CreateHold("stop-now");
            await Until(() => fixture.Scheduler.GetIntentionalWorkHoldStatus().State == "held");
            var retained = fixture.Host.Store.Get(run.Id);
            check(fixture.Handler.BarrierBeforeCancellation && !retained.CancelRequested &&
                retained.Status == WorkflowStatus.Waiting && retained.Effect is { State: WorkflowEffectState.Unknown } &&
                retained.Effect.Id == effectId, "hard stop persists both barriers before CTS interruption and preserves uncertain effect identity");
            check(fixture.Handler.Calls == 1 && fixture.Host.Store.Get("modern-stop-next").Status == WorkflowStatus.Queued,
                "hard stop cannot claim later ready work");
            heldId = held.HoldId!; generation = held.Generation; revision = held.Revision;
        }
        await using (var reopened = await Fixture.OpenAsync(root, false, true))
        {
            var restored = reopened.Scheduler.GetIntentionalWorkHoldStatus();
            check(restored.HoldId == heldId && restored.Generation == generation && restored.Revision == revision &&
                restored.EnginePaused && restored.StartupPromptRequired &&
                restored.Work.Single(x => x.Id == "modern-stop").RequiresReview,
                "reopening exact SQLite/engine restores only the intentional marker and reviewable Unknown");
            await reopened.Start();
            await Task.Delay(350);
            check(reopened.Handler.Calls == 0, "startup barrier prevents any held work execution");
            var unknownBefore = CerebralJson.Element(reopened.Host.Store.Get("modern-stop")).GetRawText();
            var resolved = await reopened.Resolve(restored, false);
            await Until(() => reopened.Host.Store.Get("modern-stop-next").Status == WorkflowStatus.Completed);
            check(resolved.State == "resumed" && reopened.Handler.Calls == 1 &&
                CerebralJson.Element(reopened.Host.Store.Get("modern-stop")).GetRawText() == unknownBefore &&
                reopened.Host.Store.Get("modern-stop").Effect?.Id == effectId,
                "resume processes independent queued work but never mutates or replays Unknown");
            var replay = await reopened.Command("jobs.workflow.resume",
                new { id = "modern-stop", expectedRevision = reopened.Host.Store.Get("modern-stop").Revision });
            check(!replay.Success, "existing resume guard still rejects uncertain effect");
        }
    }

    private static async Task LegacyStopAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Executor.Action = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException)
            {
                var status = fixture.Scheduler.GetIntentionalWorkHoldStatus();
                fixture.Executor.BarrierBeforeCancellation = status.EnginePaused && status.HoldId is not null;
                throw;
            }
        };
        var first = fixture.Scheduler.Submit(new() { RequestId = "legacy-stop", ProfileId = "lead", Prompt = "fixture legacy work" });
        var next = fixture.Scheduler.Submit(new() { RequestId = "legacy-next", ProfileId = "lead", Prompt = "fixture next work" });
        await fixture.Start(); await fixture.Executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.CreateHold("stop-now");
        await Until(() => fixture.Scheduler.GetIntentionalWorkHoldStatus().State == "held");
        var retained = fixture.Scheduler.Get(first.Id);
        check(fixture.Executor.BarrierBeforeCancellation && retained.Status == "interrupted" &&
            !retained.CancelRequested && fixture.Scheduler.GetIntentionalWorkHoldStatus().Work.Single(x => x.Id == first.Id).RequiresReview,
            "hard legacy stop is interrupted/review, never user cancellation or successful completion");
        fixture.Executor.Action = (_, _) => Task.CompletedTask;
        var resolved = await fixture.Resolve(fixture.Scheduler.GetIntentionalWorkHoldStatus(), false);
        await Until(() => fixture.Scheduler.Get(next.Id).Status == "completed");
        check(resolved.State == "resumed" && fixture.Executor.Calls == 2 &&
            fixture.Scheduler.Get(first.Id).Status == "interrupted" && !fixture.Scheduler.Get(first.Id).CancelRequested,
            "legacy interrupted work is not replayed when later queued work resumes");
    }

    private static async Task LegacyGracefulAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Executor.Action = async (_, token) => await fixture.Executor.Release.Task.WaitAsync(token);
        var first = fixture.Scheduler.Submit(new() { RequestId = "legacy-graceful", ProfileId = "lead", Prompt = "whole assignment" });
        var next = fixture.Scheduler.Submit(new() { RequestId = "legacy-graceful-next", ProfileId = "lead", Prompt = "queued assignment" });
        await fixture.Start(); await fixture.Executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var held = await fixture.CreateHold("finish-current");
        await Task.Delay(350);
        check(fixture.Executor.Calls == 1 && !fixture.Executor.Cancelled &&
            held.Detail.Contains("current assignment", StringComparison.Ordinal),
            "legacy graceful limit is truthful and does not pretend to stop between model responses");
        fixture.Executor.Release.TrySetResult();
        await Until(() => fixture.Scheduler.GetIntentionalWorkHoldStatus().State == "held");
        check(fixture.Scheduler.Get(first.Id).Status == "completed" && fixture.Scheduler.Get(next.Id).Status == "queued",
            "legacy current assignment finishes but next queued assignment remains held");
        await fixture.Resolve(fixture.Scheduler.GetIntentionalWorkHoldStatus(), false);
        await Until(() => fixture.Scheduler.Get(next.Id).Status == "completed");
        check(fixture.Executor.Calls == 2, "graceful legacy resume claims only remaining queued assignment");
    }

    private static async Task PauseWriteFailureAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Workflow("pause-write-failure");
        var selection = Path.Combine(fixture.Root, "EngineState", "selection.ini");
        var saved = Path.Combine(fixture.Root, "EngineState", "selection.fixture-backup");
        File.Move(selection, saved);
        Directory.CreateDirectory(selection); // Exact task-owned fixture: engine atomic replacement must fail.
        try
        {
            var held = await fixture.CreateHold("stop-now");
            check(held.State == "faulted" && held.Fault is not null && !held.EnginePaused &&
                held.StartupPromptRequired, "pause write failure retains truthful owner barrier and prompt marker");
            await fixture.Start(); await Task.Delay(350);
            check(fixture.Handler.Calls == 0, "marker blocks admission even when durable engine pause could not commit");
        }
        finally
        {
            Directory.Delete(selection); // Nonrecursive, known empty fixture directory.
            File.Move(saved, selection);
        }
        var resolved = await fixture.Resolve(fixture.Scheduler.GetIntentionalWorkHoldStatus(), false);
        await Until(() => fixture.Host.Store.Get("pause-write-failure").Status == WorkflowStatus.Completed);
        check(resolved.State == "resumed" && resolved.Fault is null && fixture.Handler.Calls == 1,
            "fresh exact owner resolution recovers failed pause write without replay or hidden release");
    }

    private static async Task BoundedCaptureAsync(Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 201; i++) fixture.Workflow("bound-" + i);
        var before = fixture.Scheduler.GetIntentionalWorkHoldStatus();
        var overflow = await fixture.Command("engine.work-hold", new { requestId = Guid.NewGuid().ToString("N"),
            mode = "stop-now", expectedEpoch = before.EngineEpoch, expectedGeneration = 0L, expectedRevision = 0L });
        check(!overflow.Success && fixture.Scheduler.GetIntentionalWorkHoldStatus().State == "none" &&
            !fixture.Controller.IsPaused, "capture overflow rejects complete hold instead of silently dropping work");
        fixture.State.Put("owner-work-hold", "intentional", new { version = 99, state = "resumed" });
        check(!(await fixture.Command("engine.work-hold.status", new { })).Success,
            "malformed marker is not silently advertised as ordinary unpaused state");
        await fixture.Start(); await Task.Delay(350);
        check(fixture.Handler.Calls == 0, "malformed hold marker fails closed in scheduler admission");
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(20, deadline.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required bool Delete { get; init; }
        public required StateStore State { get; init; }
        public required Configuration Configuration { get; init; }
        public required ModelRuntime Runtime { get; init; }
        public required ToolDispatcher Tools { get; init; }
        public required EngineController Controller { get; init; }
        public required WorkerWorkflows Host { get; init; }
        public required JobScheduler Scheduler { get; init; }
        public required CommandDispatcher Dispatcher { get; init; }
        public required Handler Handler { get; init; }
        public required Executor Executor { get; init; }
        public static string NewRoot() => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-intentional-hold-" + Guid.NewGuid().ToString("N")));
        public static Task<Fixture> CreateAsync() => OpenAsync(NewRoot(), true, true);
        public static async Task<Fixture> OpenAsync(string root, bool initialize, bool delete)
        {
            Directory.CreateDirectory(root);
            var state = new StateStore(root); var config = new Configuration(state); var events = new EventHub();
            if (initialize)
            {
                var settings = config.Get(); settings.MaxConcurrentJobs = 1; settings.QueueCapacity = 512; config.Save(settings);
                File.WriteAllBytes(Path.Combine(root, "not-used.gguf"), "GGUF"u8.ToArray());
                state.Put("model", "fixture", new ModelRecord { Id = "fixture", Name = "Fixture", Path = Path.Combine(root, "not-used.gguf") });
                state.Put("agent", "lead", new AgentProfile { Id = "lead", Name = "Lead", Role = "lead", ModelId = "fixture",
                    WorkerId = "local", ModelWorkerId = "local", MaxSteps = 10, MaxSeconds = 60,
                    Capabilities = ["diagnostics", "administration"] });
                EngineHostTests.Pack(Path.Combine(root, "Engines"), "A", true);
            }
            var controller = new EngineController(Path.Combine(root, "Engines"), Path.Combine(root, "EngineState"));
            await controller.InitializeAsync();
            var runtime = new ModelRuntime(state, config.Get, events);
            var tools = new ToolDispatcher(config.Get);
            var handler = new Handler();
            var host = new WorkerWorkflows(state, config, events, [new Module(handler)],
                [new("hold-fixture", "1.0.0", true, [])], engine: controller);
            var executor = new Executor();
            var scheduler = new JobScheduler(state, config, executor, runtime, events, NullLogger<JobScheduler>.Instance, host);
            var dispatcher = new CommandDispatcher(state, config, runtime, scheduler, tools, events, workflows: host);
            return new() { Root = root, Delete = delete, State = state, Configuration = config, Runtime = runtime, Tools = tools,
                Controller = controller, Host = host, Scheduler = scheduler, Dispatcher = dispatcher, Handler = handler, Executor = executor };
        }
        public WorkflowRun Workflow(string id) => Host.Submit(new(id, id + "-chat", "lead", "hold-fixture:work", "1.0.0",
            "Fixture work", null, WorkflowContract.EmptyObject), null);
        public WorkflowRun Dispatch(string id)
        {
            var run = Host.Store.TryClaim(id)!;
            run = Host.Store.ApplyStep(id, run.Revision, new(WorkflowContract.EmptyObject,
                new InvokeWorkflowCommand(Handler.Descriptor.Id, 1, WorkflowContract.EmptyObject)), Handler.Descriptor);
            return Host.Store.DispatchEffect(id, run.Revision);
        }
        public Task Start() => Scheduler.StartAsync(default);
        public Task<CommandResult> Command(string op, object args, string? actor = null) =>
            Dispatcher.ExecuteAsync(new(op, CerebralJson.Element(args), actor));
        public async Task<IntentionalWorkHoldStatus> Hold(object args)
        {
            var result = await Command("engine.work-hold", args);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return CerebralJson.Read<IntentionalWorkHoldStatus>(result.Data);
        }
        public Task<IntentionalWorkHoldStatus> CreateHold(string mode)
        {
            var status = Scheduler.GetIntentionalWorkHoldStatus();
            return Hold(new { requestId = Guid.NewGuid().ToString("N"), mode, expectedEpoch = status.EngineEpoch,
                expectedGeneration = status.Generation, expectedRevision = status.Revision });
        }
        public Task<CommandResult> ResolveCommand(IntentionalWorkHoldStatus status, bool cancel) =>
            Command(cancel ? "engine.work-hold.cancel" : "engine.work-hold.resume",
                new { holdId = status.HoldId, expectedEpoch = status.EngineEpoch, expectedGeneration = status.Generation,
                    expectedRevision = status.Revision });
        public async Task<IntentionalWorkHoldStatus> Resolve(IntentionalWorkHoldStatus status, bool cancel)
        {
            var result = await ResolveCommand(status, cancel);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return CerebralJson.Read<IntentionalWorkHoldStatus>(result.Data);
        }
        public async ValueTask DisposeAsync()
        {
            Handler.Release.TrySetResult(); Executor.Release.TrySetResult();
            await Scheduler.StopAsync(default); Scheduler.Dispose();
            await Tools.DisposeAsync(); await Runtime.DisposeAsync(); await Controller.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(Root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(Root).StartsWith("cerebral-intentional-hold-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe intentional hold fixture cleanup path.");
            if (Delete) Directory.Delete(Root, true);
        }
    }

    private sealed class Module(Handler handler) : IWorkflowModule
    {
        public string Id => "hold-fixture";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands { get; } = [handler];
    }
    private sealed class Definition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("hold-fixture:work", "1.0.0", "Deterministic work fixture",
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }), []);
        public WorkflowStep Advance(WorkflowRun run) => run.Effect?.State == WorkflowEffectState.Succeeded
            ? new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject))
            : new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand("hold-fixture:execute", 1, WorkflowContract.EmptyObject));
    }
    private sealed class Handler : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new("hold-fixture:execute", 1, "Deterministic command",
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }),
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }), []);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<WorkflowCommandContext, CancellationToken, Task<WorkflowCommandResult>> Action = (_, _) =>
            Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
        public int Calls; public bool Cancelled; public bool BarrierBeforeCancellation;
        public async Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            try { return await Action(context, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
    private sealed class Executor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<AgentJob, CancellationToken, Task> Action = (_, _) => Task.CompletedTask;
        public int Calls; public bool Cancelled; public bool BarrierBeforeCancellation;
        public async Task RunAsync(AgentJob job, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            try { await Action(job, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
}
