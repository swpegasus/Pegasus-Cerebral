using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.ReviewPlugin;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Cerebral.Worker.Tests;

public static class SchedulerWorkflowTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-workflow-slots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var handler = new ProbeHandler();
        try
        {
            var store = new StateStore(root); var config = new Configuration(store); var events = new EventHub();
            var settings = config.Get(); settings.MaxConcurrentJobs = 1; config.Save(settings);
            store.Put("agent", "reviewer", new AgentProfile { Id = "reviewer", Name = "Review", Capabilities = ["diagnostics"] });
            using var review = new ExampleReviewPlugin();
            review.Initialize(new ReviewPluginTestHost(Path.Combine(root, "review-data"), AppContext.BaseDirectory));
            var workflows = new WorkerWorkflows(store, config, events, [review, new ProbeModule(handler)],
                [new(review.Id, "1.0.0", true, []), new("fixture.reconcile", "1.0.0", true, [])]);
            await using var models = new ModelRuntime(store, config.Get, events);
            using var scheduler = new JobScheduler(store, config, new UnusedExecutor(), models, events, NullLogger<JobScheduler>.Instance, workflows);
            var run = workflows.Submit(new("uncertain", "uncertain-conversation", "reviewer", "fixture.reconcile:flow", "1.0.0", "Fixture", null, WorkflowContract.EmptyObject), null);
            run = workflows.Store.TryClaim(run.Id)!;
            run = workflows.Store.ApplyStep(run.Id, run.Revision, new(WorkflowContract.EmptyObject,
                new InvokeWorkflowCommand(handler.Descriptor.Id, 1, WorkflowContract.EmptyObject)), handler.Descriptor);
            run = workflows.Store.DispatchEffect(run.Id, run.Revision);
            workflows.Store.CompleteEffect(run.Id, run.Effect!.Id, new(WorkflowEffectState.Unknown, Error: "Fixture interrupted effect."));
            await scheduler.StartAsync(default);
            var inspection = scheduler.ReconcileWorkflowAsync(run.Id, default);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var duplicateRejected = false;
            try { await scheduler.ReconcileWorkflowAsync(run.Id, default); }
            catch (WorkflowConflictException) { duplicateRejected = true; }
            Check(duplicateRejected && handler.Calls == 1, "simultaneous reconciliation cannot inspect the same effect twice");
            workflows.Submit(new("queued-review", "review-conversation", "reviewer", ExampleReviewPlugin.DefinitionId,
                "1.0.0", "Review", null, CerebralJson.Element(new { text = "one two" })), null);
            await Task.Delay(600);
            Check(workflows.Store.Get("queued-review").Status == WorkflowStatus.Queued,
                "reconciliation occupies the same slot as ordinary workflow execution");
            scheduler.CancelWorkflow(run.Id);
            await handler.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!inspection.IsCompleted, "noncooperative reconciliation retains its slot after cancellation signal");
            handler.Release.TrySetResult();
            var reconciled = await inspection.WaitAsync(TimeSpan.FromSeconds(3));
            Check(reconciled.Status == WorkflowStatus.Cancelled && reconciled.Effect?.State == WorkflowEffectState.Succeeded,
                "cancelled inspection retains confirmed effect evidence and prevents further steps");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (workflows.Store.Get("queued-review").Questions is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(30);
            Check(workflows.Store.Get("queued-review").Questions is not null, "finished inspection releases slot for queued workflow");
            scheduler.CancelWorkflow("queued-review");
            await scheduler.StopAsync(default);
        }
        finally { handler.Release.TrySetResult(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine("Workflow scheduler: 5 deterministic concurrency assertions passed.");
        await RunnerExitAsync(cancelRequested: false, rejectSettlement: false);
        await RunnerExitAsync(cancelRequested: true, rejectSettlement: false);
        await RunnerExitAsync(cancelRequested: false, rejectSettlement: true);
        await SettlementRetryAndTerminalAsync();
    }

    private static async Task RunnerExitAsync(bool cancelRequested, bool rejectSettlement)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-runner-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var handler = new ExitHandler(); var logger = new SettlementLogger { PauseOnSettlement = true };
        try
        {
            var store = new StateStore(root); var config = new Configuration(store); var events = new EventHub();
            var settings = config.Get(); settings.MaxConcurrentJobs = 1; config.Save(settings);
            store.Put("agent", "fixture", new AgentProfile { Id = "fixture", Name = "Fixture", Capabilities = ["diagnostics"] });
            var workflows = new WorkerWorkflows(store, config, events, [new ExitModule(handler)],
                [new("fixture.runner-exit", "1.0.0", true, [])]);
            await using var models = new ModelRuntime(store, config.Get, events);
            using var scheduler = new JobScheduler(store, config, new UnusedExecutor(), models, events, logger, workflows);
            var subscription = events.Subscribe();
            var run = workflows.Submit(new("exiting", "exit-conversation", "fixture", "fixture.runner-exit:flow",
                "1.0.0", "Fixture", null, WorkflowContract.EmptyObject), null);
            await scheduler.StartAsync(default);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            run = workflows.Store.Get(run.Id); var effectId = run.Effect!.Id; var state = run.State.GetRawText();
            using var connection = OpenJournal(root);
            using (var command = connection.CreateCommand())
            {
                // Reject the result transaction after the handler returns. Recovery writes neither
                // another result nor another dispatch, so its conservative state update remains valid.
                command.CommandText = """
                    CREATE TRIGGER fixture_reject_result BEFORE INSERT ON workflow_effect_results
                    BEGIN SELECT RAISE(ABORT, 'fixture result commit failure'); END;
                    """;
                command.ExecuteNonQuery();
                if (rejectSettlement)
                {
                    command.CommandText = """
                        CREATE TRIGGER fixture_reject_settlement BEFORE UPDATE ON workflow_runs
                        WHEN instr(json_extract(NEW.payload, '$.error'), 'Workflow runner exited') > 0
                        BEGIN SELECT RAISE(ABORT, 'fixture recovery storage failure'); END;
                        """;
                    command.ExecuteNonQuery();
                }
            }
            if (cancelRequested) scheduler.CancelWorkflow(run.Id);
            handler.Release.TrySetResult();
            await logger.SettlementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(scheduler.QueueMetrics().OwnerRunning == 1,
                "exited handler retains its scheduler slot until runner-exit settlement finishes");
            logger.AllowSettlement.TrySetResult();
            if (rejectSettlement)
            {
                await logger.PersistenceFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await UntilAsync(() => scheduler.QueueMetrics().OwnerRunning == 0);
                var failed = workflows.Store.Get(run.Id);
                Check(failed.Effect?.Id == effectId && failed.Effect.State == WorkflowEffectState.Dispatched,
                    "failed recovery persistence does not fabricate a saved uncertain outcome");
                var diagnostic = false;
                while (subscription.Reader.TryRead(out var item))
                    diagnostic |= item.Type == "error" && item.Data is { } data &&
                        data.TryGetProperty("recoveryPersisted", out var persisted) && !persisted.GetBoolean();
                Check(diagnostic, "recovery storage failure emits an explicit bounded error event");
            }
            else
            {
                await UntilAsync(() => workflows.Store.Get(run.Id).Status == WorkflowStatus.Waiting &&
                    scheduler.QueueMetrics().OwnerRunning == 0);
                var settled = workflows.Store.Get(run.Id);
                Check(settled.Effect?.Id == effectId && settled.Effect.State == WorkflowEffectState.Unknown,
                    "result commit failure retains the exact inactive operation as unknown and reconcilable");
                Check(settled.CancelRequested == cancelRequested && settled.State.GetRawText() == state,
                    "runner-exit settlement preserves cancellation and native control failure anchors");
                Check(settled.Error?.Contains("SQLite error 19", StringComparison.Ordinal) == true &&
                    settled.WaitingReason?.Contains("reconcile", StringComparison.OrdinalIgnoreCase) == true,
                    "runner-exit recovery saves a bounded actionable failure instead of apparent active work");
                await Task.Delay(350);
            }
            Check(handler.Calls == 1, "runner-exit recovery never repeats the command handler");
            events.Unsubscribe(subscription.Id);
            await scheduler.StopAsync(default);
        }
        finally
        {
            handler.Release.TrySetResult(); logger.AllowSettlement.TrySetResult();
            SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    private static async Task SettlementRetryAndTerminalAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-runner-exit-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(root); var config = new Configuration(store); var events = new EventHub();
            store.Put("agent", "fixture", new AgentProfile { Id = "fixture", Name = "Fixture", Capabilities = ["diagnostics"] });
            var handler = new ExitHandler(); var logger = new SettlementLogger();
            var workflows = new WorkerWorkflows(store, config, events, [new ExitModule(handler)],
                [new("fixture.runner-exit", "1.0.0", true, [])]);
            await using var models = new ModelRuntime(store, config.Get, events);
            using var scheduler = new JobScheduler(store, config, new UnusedExecutor(), models, events, logger, workflows);
            var run = workflows.Submit(new("retry", "retry-conversation", "fixture", "fixture.runner-exit:flow",
                "1.0.0", "Fixture", null, WorkflowContract.EmptyObject), null);
            run = workflows.Store.TryClaim(run.Id)!;
            run = workflows.Store.ApplyStep(run.Id, run.Revision, new(ExitDefinition.Anchor,
                new InvokeWorkflowCommand(handler.Descriptor.Id, 1, WorkflowContract.EmptyObject)), handler.Descriptor);
            run = workflows.Store.DispatchEffect(run.Id, run.Revision);
            var effectId = run.Effect!.Id;
            using (var connection = OpenJournal(root))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var settlement = SettleAsync(scheduler, run.Id, new OperationCanceledException());
                await logger.RetryObserved.Task.WaitAsync(TimeSpan.FromSeconds(9));
                transaction.Rollback();
                await settlement.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var recovered = workflows.Store.Get(run.Id);
            Check(recovered.Status == WorkflowStatus.Waiting && recovered.Effect?.Id == effectId &&
                recovered.Effect.State == WorkflowEffectState.Unknown && handler.Calls == 0,
                "SQLite busy recovery retries only the exact state transition after runner cancellation");
            var completed = workflows.Submit(new("terminal", "terminal-conversation", "fixture", "fixture.runner-exit:flow",
                "1.0.0", "Fixture", null, WorkflowContract.EmptyObject), null);
            completed = workflows.Store.TryClaim(completed.Id)!;
            completed = workflows.Store.ApplyStep(completed.Id, completed.Revision,
                new(ExitDefinition.Anchor, new CompleteWorkflow(CerebralJson.Element(new { confirmed = true }))));
            await SettleAsync(scheduler, completed.Id, new InvalidOperationException("Fixture late failure."));
            var after = workflows.Store.Get(completed.Id);
            Check(after.Status == WorkflowStatus.Completed && after.Revision == completed.Revision && after.Error is null,
                "a late runner exception never overwrites an already committed terminal result");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static Task SettleAsync(JobScheduler scheduler, string id, Exception error) => (Task)
        typeof(JobScheduler).GetMethod("SettleWorkflowRunnerExitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [id, error])!;

    private static SqliteConnection OpenJournal(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "engine.db"), Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open(); return connection;
    }

    private static async Task UntilAsync(Func<bool> check)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!check())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Fixture state did not settle.");
            await Task.Delay(20);
        }
    }

    private sealed class ExitModule(ExitHandler handler) : IWorkflowModule
    {
        public string Id => "fixture.runner-exit";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new ExitDefinition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands { get; } = [handler];
    }

    private sealed class ExitDefinition : IWorkflowDefinition
    {
        public static readonly System.Text.Json.JsonElement Anchor = CerebralJson.Element(new
        {
            nativeControlFailures = new[] { new { presentation = "exact", mount = "conversation.start", button = "Decrement" } }
        });
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.runner-exit:flow", "1.0.0", "Runner exit fixture",
            WorkflowContract.EmptyObject, ["diagnostics"]);
        public WorkflowStep Advance(WorkflowRun run) => new(Anchor, run.Effect?.State == WorkflowEffectState.Succeeded
            ? new CompleteWorkflow(WorkflowContract.EmptyObject)
            : new InvokeWorkflowCommand("fixture.runner-exit:execute", 1, WorkflowContract.EmptyObject));
    }

    private sealed class ExitHandler : IWorkflowCommandHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public WorkflowCommandDescriptor Descriptor { get; } = new("fixture.runner-exit:execute", 1, "Runner exit fixture",
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, ["diagnostics"]);
        public async Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken ct)
        {
            Calls++; Entered.TrySetResult(); await Release.Task;
            return new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject);
        }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken ct) =>
            throw new InvalidOperationException("Fixture must not reconcile automatically.");
    }

    private sealed class SettlementLogger : ILogger<JobScheduler>
    {
        public bool PauseOnSettlement { get; init; }
        public TaskCompletionSource SettlementStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSettlement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RetryObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PersistenceFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (text.Contains("runner-exit recovery retry", StringComparison.Ordinal)) RetryObserved.TrySetResult();
            if (text.Contains("recovery state could not be saved", StringComparison.Ordinal)) PersistenceFailed.TrySetResult();
            if (!text.Contains("runner exited before", StringComparison.Ordinal)) return;
            SettlementStarted.TrySetResult();
            if (PauseOnSettlement) AllowSettlement.Task.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    private sealed class ProbeModule(ProbeHandler handler) : IWorkflowModule
    {
        public string Id => "fixture.reconcile";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new ProbeDefinition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands { get; } = [handler];
    }
    private sealed class ProbeDefinition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.reconcile:flow", "1.0.0", "Concurrency fixture", WorkflowContract.EmptyObject, ["diagnostics"]);
        public WorkflowStep Advance(WorkflowRun run) => new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject));
    }
    private sealed class ProbeHandler : IWorkflowCommandHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public WorkflowCommandDescriptor Descriptor { get; } = new("fixture.reconcile:inspect", 1, "Concurrency fixture",
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, ["diagnostics"]);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken ct) => throw new InvalidOperationException("Fixture must not execute an effect.");
        public async Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken ct)
        {
            Calls++; using var registration = ct.Register(() => CancelObserved.TrySetResult());
            Entered.TrySetResult(); await Release.Task;
            return new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject);
        }
    }
    private sealed class UnusedExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Fixture does not use model inference.");
    }
}
