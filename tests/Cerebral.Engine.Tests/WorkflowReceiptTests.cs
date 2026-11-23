using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class WorkflowReceiptTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-receipts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; }
        try
        {
            var database = Path.Combine(root, "engine.db");
            var store = new WorkflowStore(new EventJournal(database));
            var first = Prepare(store, "first");
            Check(Rejected<KeyNotFoundException>(() => store.GetEffectResult(first.Id, first.Effect!.Id)),
                "prepared effects have no fabricated receipt");
            first = store.DispatchEffect(first.Id, first.Revision);
            var expected = new WorkflowCommandResult(WorkflowEffectState.Succeeded,
                JsonSerializer.SerializeToElement(new { stdout = "receipt content", exitCode = 0 }), ProviderReceiptId: "provider-one");
            first = store.CompleteEffect(first.Id, first.Effect!.Id, expected);
            var effectId = first.Effect!.Id;
            var result = store.GetEffectResult(first.Id, effectId, out var revision);
            Check(result.State == WorkflowEffectState.Succeeded && result.ProviderReceiptId == "provider-one" &&
                result.Data!.Value.GetProperty("stdout").GetString() == "receipt content" && revision == first.Revision,
                "exact result and run revision are retrieved from SQLite");
            var second = Prepare(store, "second");
            Check(Rejected<KeyNotFoundException>(() => store.GetEffectResult(second.Id, effectId)) &&
                Rejected<KeyNotFoundException>(() => store.GetEffectResult("missing", effectId)) &&
                Rejected<KeyNotFoundException>(() => store.GetEffectResult(first.Id, "missing")),
                "effect ID alone cannot retrieve a result from another run");
            Check(Rejected<ArgumentException>(() => store.GetEffectResult("bad id", effectId)) &&
                Rejected<ArgumentException>(() => store.GetEffectResult(first.Id, "")), "receipt coordinates are validated");
            var claimed = store.TryClaim(first.Id)!;
            var completed = store.ApplyStep(first.Id, claimed.Revision,
                new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject)));
            var reopened = new WorkflowStore(new EventJournal(database));
            Check(reopened.Get(first.Id).Effect is null && reopened.GetEffectResult(first.Id, effectId).ProviderReceiptId == "provider-one",
                "earlier receipt survives later transitions and a new store connection");
            Check(reopened.GetEffectResult(first.Id, effectId, out revision).State == WorkflowEffectState.Succeeded && revision == completed.Revision,
                "receipt lookup does not rely on the current effect snapshot");

            second = store.DispatchEffect(second.Id, second.Revision);
            second = store.CompleteEffect(second.Id, second.Effect!.Id,
                new(WorkflowEffectState.Unknown, Error: "Connection lost.", ProviderReceiptId: "provider-two"));
            var unknown = store.GetEffectResult(second.Id, second.Effect!.Id, out var unknownRevision);
            Check(unknown.State == WorkflowEffectState.Unknown && unknown.Error == "Connection lost." && unknownRevision == second.Revision,
                "uncertain evidence remains retrievable without claiming success");
            second = store.CompleteEffect(second.Id, second.Effect.Id, expected with { ProviderReceiptId = "provider-two" });
            Check(store.GetEffectResult(second.Id, second.Effect!.Id, out revision).State == WorkflowEffectState.Succeeded && revision > unknownRevision,
                "reconciliation changes the result and revision together");

            using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
            connection.Open();
            void Corrupt(string value)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_effect_results SET result=$value WHERE run_id=$run AND effect_id=$effect;";
                command.Parameters.AddWithValue("$value", value); command.Parameters.AddWithValue("$run", first.Id);
                command.Parameters.AddWithValue("$effect", effectId); command.ExecuteNonQuery();
            }
            Corrupt(new string('x', 300_000));
            Check(Rejected<InvalidDataException>(() => store.GetEffectResult(first.Id, effectId)), "oversized stored receipt is rejected before materialization");
            Corrupt("{\"state\":\"Succeeded\",\"data\":{\"text\":\"" + new string('x', WorkflowContract.MaximumStateBytes) + "\"}}");
            Check(Rejected<InvalidDataException>(() => store.GetEffectResult(first.Id, effectId)), "stored receipt data still obeys the workflow result bound");
            Corrupt("{\"state\":\"Prepared\"}");
            Check(Rejected<InvalidDataException>(() => store.GetEffectResult(first.Id, effectId)), "invalid persisted result state is rejected");
            Corrupt("not JSON");
            Check(Rejected<InvalidDataException>(() => store.GetEffectResult(first.Id, effectId)), "malformed stored receipt is rejected");

            var identity = new RunIdentityModule();
            var runtime = new WorkflowRuntime(store, new WorkflowRegistry([identity]));
            store.Submit(new("identity-run", "identity-chat", "lead", "fixture.identity:workflow", "1.0.0", "Read receipt.", null,
                WorkflowContract.EmptyObject, WorkflowContract.EmptyObject));
            var uncertainRun = await runtime.RunAsync("identity-run");
            Check(uncertainRun.Status == WorkflowStatus.Waiting && identity.ExecuteRunId == "identity-run" && identity.PolicyRunId == "identity-run",
                "runtime injects exact run identity into policy and execution contexts");
            await runtime.ReconcileAsync("identity-run");
            Check(identity.ReconcileRunId == "identity-run" && (await runtime.RunAsync("identity-run")).Status == WorkflowStatus.Completed,
                "runtime preserves the same run identity during read-only reconciliation");
            Console.WriteLine($"Workflow receipts: {checks} local SQLite integration assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static WorkflowRun Prepare(WorkflowStore store, string id)
    {
        store.Submit(new(id, id, "lead", "example.receipt:review", "1.0.0", "Read receipt.", null,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject));
        var run = store.TryClaim(id)!;
        var command = new WorkflowCommandDescriptor("example.receipt:read", 1, "Read fixture",
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, []);
        return store.ApplyStep(id, run.Revision, new(WorkflowContract.EmptyObject,
            new InvokeWorkflowCommand(command.Id, 1, WorkflowContract.EmptyObject)), command);
    }

    private static bool Rejected<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }

    private sealed class RunIdentityModule : IWorkflowModule, IWorkflowDefinition, IWorkflowCommandHandler
    {
        public string Id => "fixture.identity";
        public IReadOnlyList<IWorkflowDefinition> Workflows => [this];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [this];
        public string? PolicyRunId, ExecuteRunId, ReconcileRunId;
        WorkflowDefinitionDescriptor IWorkflowDefinition.Descriptor { get; } = new("fixture.identity:workflow", "1.0.0", "Identity fixture", WorkflowContract.EmptyObject, []);
        WorkflowCommandDescriptor IWorkflowCommandHandler.Descriptor { get; } = new("fixture.identity:read", 1, "Identity fixture", WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, []);
        public WorkflowStep Advance(WorkflowRun run) => run.Signal.Kind == WorkflowSignalKind.Start
            ? new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand("fixture.identity:read", 1, WorkflowContract.EmptyObject))
            : new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject));
        public string? GetFailureBudgetKey(WorkflowCommandContext context) { PolicyRunId = context.RunId; return null; }
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        { ExecuteRunId = context.RunId; return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Unknown, WorkflowContract.EmptyObject)); }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        { ReconcileRunId = context.RunId; return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject)); }
    }
}
