using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class WorkflowBudgetTests
{
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static readonly JsonElement ObjectSchema = Data(new { type = "object" });

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-budget-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "engine.db");
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; }
        try
        {
            var store = new WorkflowStore(new EventJournal(path));
            var handler = new FailingHandler(); var definition = new RepeatDefinition();
            var runtime = new WorkflowRuntime(store, new WorkflowRegistry([new Module(definition, handler)]));
            var limited = store.Submit(Submission("failures"));
            limited = await runtime.RunAsync(limited.Id);
            Check(limited.Status == WorkflowStatus.Waiting && handler.Calls == 2 && limited.FailedEffects["budget:launch"] == 2,
                "two failed effects exhaust the saved category before a third dispatch");
            var reopened = new WorkflowStore(new EventJournal(path));
            var saved = reopened.Get(limited.Id);
            Check(saved.FailedEffects["budget:launch"] == 2 && saved.ActiveMilliseconds > 0,
                "failure and active-time counters survive reopening SQLite");
            reopened.Resume(saved.Id, saved.Revision);
            await runtime.RunAsync(saved.Id);
            Check(handler.Calls == 2, "resume cannot reset a host-owned failure limit");

            var missing = store.Submit(Submission("missing") with { FailureLimits = new Dictionary<string, int>() });
            missing = await runtime.RunAsync(missing.Id);
            Check(missing.Status == WorkflowStatus.Waiting && handler.Calls == 2,
                "an unadmitted failure category cannot run");

            var policy = store.Submit(Submission("policy") with { PolicyStepLimit = 1 });
            policy = await runtime.RunAsync(policy.Id);
            Check(policy.Status == WorkflowStatus.Failed && policy.Steps == 1 && handler.Calls == 3,
                "captured profile step cap constrains the requested step limit");

            var timed = store.Submit(Submission("time") with { ActiveTimeLimitSeconds = 1 });
            timed = Prepare(store, definition, handler, timed.Id);
            timed = store.DispatchEffect(timed.Id, timed.Revision, 900, "budget:launch");
            Check(timed.ActiveMilliseconds == 900 && timed.Effect!.ReservedExecutionMilliseconds == 900,
                "execution time is reserved durably before dispatch");
            var receipt = new WorkflowCommandResult(WorkflowEffectState.Succeeded, Data(new { value = 1 }));
            timed = store.CompleteEffect(timed.Id, timed.Effect!.Id, receipt, 100);
            Check(timed.ActiveMilliseconds == 100, "known execution refunds unused reservation using measured duration");
            timed = Prepare(store, definition, handler, timed.Id);
            timed = store.DispatchEffect(timed.Id, timed.Revision, 900, "budget:launch");
            store.RecoverInterrupted(); timed = store.Get(timed.Id);
            Check(timed.ActiveMilliseconds == 1000 && timed.Effect!.State == WorkflowEffectState.Unknown,
                "restart retains an interrupted operation's reservation and uncertain status");
            timed = store.CompleteEffect(timed.Id, timed.Effect!.Id, receipt);
            timed = await runtime.RunAsync(timed.Id);
            Check(timed.Status == WorkflowStatus.Waiting && handler.Calls == 3,
                "reconciliation does not silently replenish an exhausted execution budget");

            var late = Prepare(store, definition, handler, store.Submit(Submission("late")).Id);
            late = store.DispatchEffect(late.Id, late.Revision, 1000, "budget:launch");
            late = store.CompleteEffect(late.Id, late.Effect!.Id, new(WorkflowEffectState.Unknown, Error: "lost receipt"), 10);
            Check(late.ActiveMilliseconds == 1000 && late.FailedEffects.Count == 0,
                "unknown effects retain their reservation and are not fabricated failures");
            var failed = new WorkflowCommandResult(WorkflowEffectState.Failed, Error: "confirmed later");
            late = store.CompleteEffect(late.Id, late.Effect!.Id, failed);
            late = store.CompleteEffect(late.Id, late.Effect!.Id, failed);
            Check(late.FailedEffects["budget:launch"] == 1, "reconciled failure increments exactly once across receipt retries");

            var waiting = store.Submit(Submission("questions"));
            waiting = store.TryClaim(waiting.Id)!;
            waiting = store.ApplyStep(waiting.Id, waiting.Revision, new(Data(new { }), new AskWorkflowQuestions([new("choice", "Choose")])));
            waiting = store.Answer(new("answer", waiting.Id, waiting.Questions!.Id, waiting.Questions.Revision,
                new Dictionary<string, string> { ["choice"] = "yes" }));
            Check(waiting.ActiveMilliseconds == 0, "question waiting and answers consume no execution reservation");

            var receiptHandler = new ReceiptHandler("receipt:execute", receipt);
            var receiptDefinition = new ReceiptDefinition("receipt:once", receiptHandler.Descriptor.Id);
            var receiptRuntime = new WorkflowRuntime(store, new WorkflowRegistry([new FixtureModule("receipt", receiptDefinition, receiptHandler)]));
            var crowded = store.Submit(Submission("crowded") with
            {
                DefinitionId = receiptDefinition.Descriptor.Id,
                UserText = new string('<', 110_000)
            });
            crowded = await receiptRuntime.RunAsync(crowded.Id);
            Check(crowded.Status == WorkflowStatus.Failed && receiptHandler.Calls == 0 &&
                crowded.Effect?.State == WorkflowEffectState.Prepared && crowded.ActiveMilliseconds == 0 &&
                crowded.Error?.Contains("No command was dispatched", StringComparison.Ordinal) == true,
                "near-limit accepted context cannot dispatch when the maximum legal receipt would not fit");

            var largeResult = Data(new { text = new string('x', WorkflowContract.MaximumStateBytes - 11) });
            Check(JsonSerializer.SerializeToUtf8Bytes(largeResult).Length == WorkflowContract.MaximumStateBytes,
                "receipt fixture reaches the maximum permitted serialized data size");
            var spacious = store.Submit(Submission("large-receipt") with { UserText = new string('<', 100_000) });
            spacious = Prepare(store, definition, handler, spacious.Id);
            spacious = store.DispatchEffect(spacious.Id, spacious.Revision, 1000, "budget:launch");
            var maximumReceipt = new WorkflowCommandResult(WorkflowEffectState.Failed, largeResult,
                new string('<', 8192), new string('<', 1024));
            spacious = store.CompleteEffect(spacious.Id, spacious.Effect!.Id, maximumReceipt, 20);
            var retained = reopened.Get(spacious.Id);
            Check(retained.Effect!.Result!.Value.GetProperty("text").GetString()!.Length == WorkflowContract.MaximumStateBytes - 11 &&
                retained.Effect.Error == maximumReceipt.Error && retained.Effect.ProviderReceiptId == maximumReceipt.ProviderReceiptId &&
                retained.Signal.Kind == WorkflowSignalKind.EffectResult && retained.ActiveMilliseconds == 20,
                "admitted dispatch durably retains maximum data and worst-case escaped metadata across reopening");
            Check(store.CompleteEffect(spacious.Id, spacious.Effect!.Id, maximumReceipt).FailedEffects["budget:launch"] == 1,
                "large receipt retries are idempotent and do not double-count failures");

            using (var unescaped = JsonDocument.Parse("{\"text\":\"" + new string('<', 30_000) + "\"}"))
            {
                receiptHandler.Result = new(WorkflowEffectState.Succeeded, unescaped.RootElement.Clone());
                var expanded = store.Submit(Submission("escaped-result") with { DefinitionId = receiptDefinition.Descriptor.Id });
                expanded = await receiptRuntime.RunAsync(expanded.Id);
                Check(expanded.Effect?.State == WorkflowEffectState.Unknown && expanded.Status == WorkflowStatus.Waiting && receiptHandler.Calls == 1,
                    "a plugin result exceeding the persistence encoding bound becomes an explicit unknown outcome, not a stranded dispatch");
            }

            var owner = new string('a', 128);
            var qualifiedDefinition = owner + ":" + new string('w', 127);
            var qualifiedCommand = owner + ":" + new string('c', 127);
            var longHandler = new ReceiptHandler(qualifiedCommand, receipt);
            var longDefinition = new ReceiptDefinition(qualifiedDefinition, qualifiedCommand);
            var longRuntime = new WorkflowRuntime(store, new WorkflowRegistry([new FixtureModule(owner, longDefinition, longHandler)]));
            var longRun = store.Submit(Submission("long-qualified") with { DefinitionId = qualifiedDefinition });
            longRun = await longRuntime.RunAsync(longRun.Id);
            Check(longRun.Status == WorkflowStatus.Completed && longHandler.Calls == 1,
                "registered 256-character qualified definition and command IDs can actually execute");
            var longSimpleRejected = false;
            try { store.Submit(Submission(new string('x', 129))); }
            catch (ArgumentException) { longSimpleRejected = true; }
            Check(longSimpleRejected, "simple request identities retain their separate 128-character bound");
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Workflow budgets: {count} SQLite/deterministic-handler checks passed.");
    }

    private static WorkflowSubmission Submission(string id) => new(id, "conversation-" + id, "agent", "budget:repeat", "1.0.0", "Do work", null,
        Data(new { }), Data(new { })) { FailureLimits = new Dictionary<string, int> { ["budget:launch"] = 2 } };
    private static WorkflowRun Prepare(WorkflowStore store, RepeatDefinition definition, FailingHandler handler, string id)
    {
        var run = store.TryClaim(id)!;
        return store.ApplyStep(id, run.Revision, definition.Advance(run), handler.Descriptor);
    }
    private sealed class RepeatDefinition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("budget:repeat", "1.0.0", "Budget fixture", ObjectSchema, []);
        public WorkflowStep Advance(WorkflowRun snapshot) => new(Data(new { }), new InvokeWorkflowCommand("budget:execute", 1, Data(new { })));
    }
    private sealed class FailingHandler : IWorkflowCommandHandler
    {
        public int Calls { get; private set; }
        public WorkflowCommandDescriptor Descriptor { get; } = new("budget:execute", 1, "Budget fixture", ObjectSchema, ObjectSchema, []);
        public string? GetFailureBudgetKey(WorkflowCommandContext context) => "budget:launch";
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Failed, Error: "fixture failure")); }
    }
    private sealed record Module(RepeatDefinition Definition, FailingHandler Handler) : IWorkflowModule
    {
        public string Id => "budget";
        public IReadOnlyList<IWorkflowDefinition> Workflows => [Definition];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [Handler];
    }
    private sealed class ReceiptDefinition(string id, string commandId) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(id, "1.0.0", "Receipt fixture", ObjectSchema, []);
        public WorkflowStep Advance(WorkflowRun snapshot) => new(Data(new { }), snapshot.Signal.Kind == WorkflowSignalKind.EffectResult
            ? new CompleteWorkflow(Data(new { complete = true }))
            : new InvokeWorkflowCommand(commandId, 1, Data(new { })));
    }
    private sealed class ReceiptHandler(string id, WorkflowCommandResult result) : IWorkflowCommandHandler
    {
        public int Calls { get; private set; }
        public WorkflowCommandResult Result { get; set; } = result;
        public WorkflowCommandDescriptor Descriptor { get; } = new(id, 1, "Receipt fixture", ObjectSchema, ObjectSchema, []);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Result); }
    }
    private sealed record FixtureModule(string Id, IWorkflowDefinition Definition, IWorkflowCommandHandler Handler) : IWorkflowModule
    {
        public IReadOnlyList<IWorkflowDefinition> Workflows => [Definition];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [Handler];
    }
}
