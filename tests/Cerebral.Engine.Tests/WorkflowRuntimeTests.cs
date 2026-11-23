using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

/// <summary>Deterministic command fixtures exercise the engine, not a live provider or model.</summary>
public static class WorkflowRuntimeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement Schema(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool result, string message) { if (!result) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        static bool Rejected<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-workflow-runtime-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            var handler = new MeasureHandler();
            var definition = new ReviewDefinition();
            var module = new FixtureModule(definition, handler);
            var registry = new WorkflowRegistry([module]);
            var runtime = new WorkflowRuntime(store, registry, (_, required) => required.All(x => x == "diagnostics"));
            Check(registry.Definitions.Single().Id == "fixture.review:review" && registry.Commands.Single().Id == "fixture.review:measure",
                "registry discovers namespaced versioned workflow and command descriptors");
            Check(Rejected<ArgumentException>(() => new WorkflowRegistry([module, module])), "registration rejects duplicate packages atomically");
            Check(Rejected<ArgumentException>(() => new WorkflowRegistry([module with { Id = "other.module" }])), "registration refuses another package's contribution IDs");
            Check(Rejected<ArgumentException>(() => WorkflowSchema.ValidateSchema(Schema("{\"type\":\"object\",\"anyOf\":[]}"))) &&
                Rejected<ArgumentException>(() => WorkflowSchema.ValidateSchema(Schema("{\"maxLength\":\"100\"}"))),
                "schema subset rejects unsupported keywords and malformed numeric constraints explicitly");
            Check(Rejected<ArgumentException>(() => WorkflowSchema.Validate(Schema("{\"text\":\"one\",\"text\":\"two\"}"), handler.Descriptor.InputSchema)),
                "schema validation rejects ambiguous duplicate properties");
            var original = handler.Descriptor;
            handler.Descriptor = original with { Id = "fixture.review:changed", RequiredCapabilities = ["administration"] };
            Check(registry.GetCommand("fixture.review:measure", 1).Descriptor.Id == original.Id &&
                registry.GetCommand("fixture.review:measure", 1).Descriptor.RequiredCapabilities.SequenceEqual(["diagnostics"]) &&
                !registry.TryGetCommand("fixture.review:changed", 1, out _),
                "registry freezes admitted command descriptors against later plugin mutation");
            handler.Descriptor = original;

            var submitted = store.Submit(Submission("review"));
            var waiting = await runtime.RunAsync(submitted.Id);
            Check(waiting.Status == WorkflowStatus.Waiting && waiting.Questions?.Fields.Count == 2 && handler.Executions == 0,
                "workflow presents two durable fields without dispatching a command");
            var partial = store.Answer(new("answer-one", waiting.Id, waiting.Questions!.Id, waiting.Questions.Revision,
                new Dictionary<string, string> { ["label"] = "Demo" }));
            Check(partial.Status == WorkflowStatus.Waiting && handler.Executions == 0, "partial answer keeps required remaining field pending");
            var answered = store.Answer(new("answer-two", partial.Id, partial.Questions!.Id, partial.Questions.Revision,
                new Dictionary<string, string> { ["style"] = "concise" }));
            var complete = await runtime.RunAsync(answered.Id);
            Check(complete.Status == WorkflowStatus.Completed && complete.Result?.GetProperty("words").GetInt32() == 3 && handler.Executions == 1,
                "answered workflow executes its deterministic command and persists the actual result");
            Check((await runtime.RunAsync(complete.Id)).Revision == complete.Revision && handler.Executions == 1,
                "requesting an already completed run never replays its command");

            handler.Outcome = new(WorkflowEffectState.Failed, Error: "Intentional fixture failure.");
            var failing = Prepare(store, definition, registry, "failed-command");
            store.RecoverInterrupted();
            var failed = await runtime.RunAsync(failing.Id);
            Check(failed.Status == WorkflowStatus.Failed && failed.Error == "Intentional fixture failure.",
                "confirmed failed command reaches the workflow's explicit failed completion");
            handler.Outcome = null;

            var limited = store.Submit(Submission("step-limit") with { StepLimit = 1 });
            limited = await runtime.RunAsync(limited.Id);
            store.Answer(new("limit-answer", limited.Id, limited.Questions!.Id, limited.Questions.Revision,
                new Dictionary<string, string> { ["label"] = "Budget", ["style"] = "concise" }));
            var limitResult = await runtime.RunAsync(limited.Id);
            Check(limitResult.Status == WorkflowStatus.Failed && limitResult.Error!.Contains("budget", StringComparison.Ordinal),
                "exhausted step budget reports failure without leaving a permanently running task");

            var prepared = Prepare(store, definition, registry, "prepared-recovery");
            var beforeRecovery = handler.Executions;
            Check(store.RecoverInterrupted() >= 1, "recovery finds a previously committed prepared intent");
            var recovered = await runtime.RunAsync(prepared.Id);
            Check(recovered.Status == WorkflowStatus.Completed && handler.Executions == beforeRecovery + 1 && handler.LastEffectId == prepared.Effect!.Id,
                "recovered prepared effect keeps its identity and executes only once");

            var uncertain = Prepare(store, definition, registry, "unknown-recovery");
            store.DispatchEffect(uncertain.Id, uncertain.Revision);
            store.RecoverInterrupted();
            var beforeUnknown = handler.Executions;
            var stillUnknown = await runtime.RunAsync(uncertain.Id);
            Check(stillUnknown.Status == WorkflowStatus.Waiting && stillUnknown.Effect?.State == WorkflowEffectState.Unknown && handler.Executions == beforeUnknown,
                "interrupted dispatched effect never replays even when replay policy says idempotent");
            handler.ReconciledOutcome = new(WorkflowEffectState.Succeeded, Data(new { words = 3 }));
            var reconciled = await runtime.ReconcileAsync(uncertain.Id);
            Check(reconciled.Status == WorkflowStatus.Queued && handler.Executions == beforeUnknown && handler.Reconciliations == 1,
                "reconciliation only inspects the existing effect and queues the next transition");
            Check((await runtime.RunAsync(uncertain.Id)).Status == WorkflowStatus.Completed && handler.Executions == beforeUnknown,
                "scheduler continuation consumes the reconciled result without repeating the command");

            var cancelledUnknown = Prepare(store, definition, registry, "cancelled-unknown");
            store.DispatchEffect(cancelledUnknown.Id, cancelledUnknown.Revision);
            store.RecoverInterrupted();
            var cancellationPending = store.Cancel(cancelledUnknown.Id);
            Check(cancellationPending.Status == WorkflowStatus.Waiting && cancellationPending.CancelRequested &&
                cancellationPending.Effect?.State == WorkflowEffectState.Unknown,
                "cancelling an interrupted effect retains uncertainty until its handler inspects the prior operation");
            handler.ReconciledOutcome = new(WorkflowEffectState.Failed, Error: "The prior non-mutating operation was not completed.");
            var cancellationSettled = await runtime.ReconcileAsync(cancelledUnknown.Id);
            Check(cancellationSettled.Status == WorkflowStatus.Cancelled && cancellationSettled.CancelRequested &&
                cancellationSettled.Effect?.State == WorkflowEffectState.Failed,
                "a known failed reconciliation settles an already-cancelled run instead of leaving permanent active work");
            handler.ReconciledOutcome = null;

            var denied = Prepare(store, definition, registry, "denied");
            var deniedRuntime = new WorkflowRuntime(store, registry);
            // Prepare represents a prior process's running state; recover it before a new claim.
            store.RecoverInterrupted();
            var deniedResult = await deniedRuntime.RunAsync(denied.Id);
            Check(deniedResult.Status == WorkflowStatus.Waiting && deniedResult.Effect?.State == WorkflowEffectState.Prepared,
                "missing host grant checker holds a required-capability command before dispatch");
            var beforeResume = handler.Executions;
            var resumed = await runtime.ResumeAsync(denied.Id, deniedResult.Revision);
            Check(resumed.Status == WorkflowStatus.Queued && handler.Executions == beforeResume,
                "resume queues work instead of bypassing scheduler concurrency");
            Check((await runtime.RunAsync(denied.Id)).Status == WorkflowStatus.Completed, "restored grants allow explicit resumed execution");

            var identityDenied = store.Submit(Submission("identity-denied"));
            var identityResult = await new WorkflowRuntime(store, registry, (_, _) => false).RunAsync(identityDenied.Id);
            Check(identityResult.Status == WorkflowStatus.Waiting && identityResult.Steps == 0,
                "host identity admission runs even when a workflow declares no required capabilities");

            var unclaimed = store.Submit(Submission("shutdown-before-claim"));
            using (var stopped = new CancellationTokenSource())
            {
                stopped.Cancel();
                var stoppedResult = await runtime.RunAsync(unclaimed.Id, stopped.Token);
                Check(stoppedResult.Status == WorkflowStatus.Queued && !stoppedResult.CancelRequested,
                    "host shutdown before claim retains queued accepted work without fabricating owner cancellation");
            }
            var beforeDispatch = Prepare(store, definition, registry, "shutdown-before-dispatch");
            store.RecoverInterrupted();
            using (var stopped = new CancellationTokenSource())
            {
                var stoppedRuntime = new WorkflowRuntime(store, registry, (_, _) => { stopped.Cancel(); return true; });
                var stoppedResult = await stoppedRuntime.RunAsync(beforeDispatch.Id, stopped.Token);
                Check(stoppedResult.Status == WorkflowStatus.Waiting && stoppedResult.Effect?.State == WorkflowEffectState.Prepared && !stoppedResult.CancelRequested,
                    "host interruption after claim holds an undispatched intent for explicit resume");
            }

            handler.Throw = true;
            var thrown = Prepare(store, definition, registry, "thrown");
            store.RecoverInterrupted();
            var thrownResult = await runtime.RunAsync(thrown.Id);
            Check(thrownResult.Status == WorkflowStatus.Waiting && thrownResult.Effect?.State == WorkflowEffectState.Unknown,
                "exception after dispatch records an uncertain outcome instead of claiming no effects occurred");
            handler.Throw = false;

            handler.Outcome = new(WorkflowEffectState.Succeeded, Data(new { invented = true }));
            var invalidOutput = Prepare(store, definition, registry, "invalid-output");
            store.RecoverInterrupted();
            var invalidResult = await runtime.RunAsync(invalidOutput.Id);
            Check(invalidResult.Status == WorkflowStatus.Waiting && invalidResult.Effect?.State == WorkflowEffectState.Unknown,
                "invalid command result cannot be mistaken for a verified success or a safe retry");
            handler.Outcome = null;

            var checkpointFirst = Prepare(store, definition, registry, "checkpoint-admission");
            var checkpointDispatched = store.DispatchEffect(checkpointFirst.Id, checkpointFirst.Revision);
            var retainedCheckpoint = new Cerebral.Contracts.Context.ContextCheckpoint(checkpointDispatched.Effect!.Id,
                1, 0, [new("earlier-input", 1)], "Previously retained summary.", checkpointDispatched.Effect.Id, true);
            store.CompleteEffect(checkpointDispatched.Id, checkpointDispatched.Effect.Id,
                new(WorkflowEffectState.Succeeded, Data(new { words = 3 })) { ConversationCheckpoint = retainedCheckpoint });
            var conflictingCheckpointRun = Prepare(store, definition, registry, checkpointDispatched.Id);
            var rejectedCheckpoint = retainedCheckpoint with { Id = conflictingCheckpointRun.Effect!.Id,
                CompactionEffectId = conflictingCheckpointRun.Effect.Id,
                CoveredSources = [new(retainedCheckpoint.Id, retainedCheckpoint.ContextRevision)], Summary = "Different content at unchanged context." };
            handler.Outcome = new(WorkflowEffectState.Succeeded, Data(new { words = 3 }))
                { ConversationCheckpoint = rejectedCheckpoint };
            store.RecoverInterrupted();
            var checkpointRejected = await runtime.RunAsync(conflictingCheckpointRun.Id);
            var rejectedReceipt = store.GetEffectResult(checkpointRejected.Id, rejectedCheckpoint.Id);
            Check(checkpointRejected.Status == WorkflowStatus.Failed && checkpointRejected.Effect is null &&
                checkpointRejected.Result?.GetProperty("words").GetInt32() == 3 &&
                checkpointRejected.Error?.Contains("could not adopt its conversation checkpoint", StringComparison.Ordinal) == true &&
                store.GetConversationCheckpoint(checkpointRejected.Submission.ConversationId,
                    checkpointRejected.Submission.ConversationSequence)?.Checkpoint.Id == retainedCheckpoint.Id &&
                rejectedReceipt.State == WorkflowEffectState.Failed && rejectedReceipt.Data?.GetProperty("words").GetInt32() == 3 &&
                rejectedReceipt.ConversationCheckpoint is null && rejectedReceipt.Error == checkpointRejected.Error,
                "checkpoint admission conflict retains a confirmed failed host outcome and returned data instead of fabricating provider uncertainty");
            var executionsAfterCheckpointRejection = handler.Executions;
            Check((await runtime.RunAsync(checkpointRejected.Id)).Status == WorkflowStatus.Failed &&
                handler.Executions == executionsAfterCheckpointRejection,
                "a rejected checkpoint effect remains terminal and is never replayed");
            handler.Outcome = null;

            var blockedHandler = new ControlledHandler();
            var blockedRegistry = new WorkflowRegistry([new FixtureModule(definition, blockedHandler)]);
            var blockedRuntime = new WorkflowRuntime(store, blockedRegistry, (_, _) => true);
            var blocked = Prepare(store, definition, blockedRegistry, "noncooperative-timeout");
            store.RecoverInterrupted();
            var execution = blockedRuntime.RunAsync(blocked.Id);
            await blockedHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await blockedHandler.TimedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!execution.IsCompleted && store.Get(blocked.Id).Effect?.State == WorkflowEffectState.Dispatched,
                "timeout does not release execution while a noncooperative handler is still active");
            store.Cancel(blocked.Id);
            blockedHandler.Release.SetResult(new(WorkflowEffectState.Succeeded, Data(new { words = 3 })));
            var cancelled = await execution;
            Check(cancelled.Status == WorkflowStatus.Cancelled && cancelled.Effect?.State == WorkflowEffectState.Succeeded,
                "late confirmed result preserves the real effect outcome while cancellation prevents further workflow steps");

            var shutdownHandler = new ControlledHandler();
            var shutdownRegistry = new WorkflowRegistry([new FixtureModule(definition, shutdownHandler)]);
            var shutdown = Prepare(store, definition, shutdownRegistry, "shutdown-dispatched");
            store.RecoverInterrupted();
            using var shutdownSource = new CancellationTokenSource();
            var interruptedExecution = new WorkflowRuntime(store, shutdownRegistry, (_, _) => true).RunAsync(shutdown.Id, shutdownSource.Token);
            await shutdownHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdownSource.Cancel();
            shutdownHandler.Release.SetException(new OperationCanceledException(shutdownSource.Token));
            var interrupted = await interruptedExecution;
            Check(interrupted.Status == WorkflowStatus.Waiting && interrupted.Effect?.State == WorkflowEffectState.Unknown && !interrupted.CancelRequested,
                "host interruption after dispatch retains uncertainty without recording an owner cancellation");

            var receiptHandler = new ControlledHandler();
            var receiptRegistry = new WorkflowRegistry([new FixtureModule(definition, receiptHandler)]);
            var receiptRun = Prepare(store, definition, receiptRegistry, "busy-receipt-commit");
            store.RecoverInterrupted();
            var receiptExecution = new WorkflowRuntime(store, receiptRegistry, (_, _) => true).RunAsync(receiptRun.Id);
            await receiptHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "engine.db"), Mode = SqliteOpenMode.ReadWrite,
                Pooling = false, DefaultTimeout = 5
            }.ToString()))
            {
                writer.Open();
                using var writerLock = writer.BeginTransaction(deferred: false);
                receiptHandler.Release.SetResult(new(WorkflowEffectState.Succeeded, Data(new { words = 3 })));
                // Outlast one real journal busy timeout so this checks the retry boundary, not
                // merely SQLite's first wait. The provider has already returned its result.
                await Task.Delay(TimeSpan.FromMilliseconds(5600));
                Check(!receiptExecution.IsCompleted && receiptHandler.ExecutionCount == 1,
                    "a busy receipt transaction retains the owned result and never replays the provider");
                writerLock.Commit();
            }
            var receiptCompleted = await receiptExecution.WaitAsync(TimeSpan.FromSeconds(8));
            var committedReceipt = store.GetEffectResult(receiptRun.Id, receiptRun.Effect!.Id);
            Check(receiptCompleted.Status == WorkflowStatus.Completed &&
                committedReceipt.State == WorkflowEffectState.Succeeded &&
                committedReceipt.Data?.GetProperty("words").GetInt32() == 3 &&
                receiptHandler.ExecutionCount == 1,
                "confirmed output survives a real SQLite lock timeout and commits once after the lock clears");
            Check((await runtime.RunAsync(receiptRun.Id)).Revision == receiptCompleted.Revision &&
                receiptHandler.ExecutionCount == 1,
                "reopening a busy-recovered run does not execute its effect again");

            Console.WriteLine($"Workflow runtime: {count} deterministic fixture / local SQLite assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static WorkflowSubmission Submission(string id) => new(id, "conversation-" + id, "test-profile",
        "fixture.review:review", "1.0.0", "Review my sample.", "Private workflow assignment.", Data(new { text = "one two three" }), Data(new { }));

    private static WorkflowRun Prepare(WorkflowStore store, ReviewDefinition definition, WorkflowRegistry registry, string id)
    {
        var run = store.Submit(Submission(id));
        run = store.TryClaim(run.Id)!;
        // A deterministic fixture stages an effect exactly as a preceding process would have done.
        return store.ApplyStep(run.Id, run.Revision, new(Data(new { phase = "measure" }),
            new InvokeWorkflowCommand("fixture.review:measure", 1, run.Submission.Input)), registry.GetCommand("fixture.review:measure", 1).Descriptor);
    }

    private sealed record FixtureModule(ReviewDefinition Definition, IWorkflowCommandHandler Handler) : IWorkflowModule
    {
        public string Id { get; init; } = "fixture.review";
        public IReadOnlyList<IWorkflowDefinition> Workflows => [Definition];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [Handler];
    }

    private sealed class ReviewDefinition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.review:review", "1.0.0", "Deterministic review test.",
            Schema("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}"), []);
        public WorkflowStep Advance(WorkflowRun snapshot) => snapshot.Signal.Kind switch
        {
            WorkflowSignalKind.Start => new(Data(new { phase = "questions" }), new AskWorkflowQuestions([
                new("label", "Label"), new("style", "Style", Choices: ["concise", "detailed"])])),
            WorkflowSignalKind.Answers => new(Data(new { phase = "measure" }), new InvokeWorkflowCommand("fixture.review:measure", 1, snapshot.Submission.Input)),
            WorkflowSignalKind.EffectResult => Finish(snapshot.Signal.Data.Deserialize<WorkflowCommandResult>(Json)!),
            _ => throw new InvalidOperationException("Unexpected test signal.")
        };
        private static WorkflowStep Finish(WorkflowCommandResult result) => new(Data(new { phase = "done" }),
            new CompleteWorkflow(result.Data ?? Data(new { }), result.State == WorkflowEffectState.Succeeded, result.Error));
    }

    private class MeasureHandler : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; set; } = new("fixture.review:measure", 1, "Count words in fixture input.",
            Schema("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}"),
            Schema("{\"type\":\"object\",\"properties\":{\"words\":{\"type\":\"integer\",\"minimum\":0}},\"required\":[\"words\"],\"additionalProperties\":false}"),
            ["diagnostics"], WorkflowReplayPolicy.Idempotent);
        public int Executions { get; private set; }
        public int Reconciliations { get; private set; }
        public string? LastEffectId { get; private set; }
        public WorkflowCommandResult? Outcome { get; set; }
        public WorkflowCommandResult? ReconciledOutcome { get; set; }
        public bool Throw { get; set; }
        public virtual Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        {
            Executions++; LastEffectId = context.EffectId;
            if (Throw) throw new InvalidOperationException("Intentional fixture exception after dispatch.");
            return Task.FromResult(Outcome ?? new(WorkflowEffectState.Succeeded,
                Data(new { words = context.Arguments.GetProperty("text").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length })));
        }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        {
            Reconciliations++; return Task.FromResult(ReconciledOutcome ?? new(WorkflowEffectState.Unknown));
        }
    }

    private sealed class ControlledHandler : MeasureHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TimedOut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorkflowCommandResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExecutionCount { get; private set; }
        public ControlledHandler() { Descriptor = Descriptor with { TimeoutSeconds = 1 }; }
        public override async Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            using var registration = cancellationToken.Register(() => TimedOut.TrySetResult());
            Started.TrySetResult();
            return await Release.Task;
        }
    }
}
