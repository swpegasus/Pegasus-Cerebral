using System.Reflection;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

internal static class WorkflowActivityTotalsChecks
{
    // Caller supplies a fresh task-owned scratch directory. This fixture never opens
    // installed state, executes an effect, starts a model or connects to a service.
    public static int Run(string scratchDirectory)
    {
        if (!Path.IsPathFullyQualified(scratchDirectory) || Directory.Exists(scratchDirectory))
            throw new ArgumentException("A new absolute scratch directory is required.");
        Directory.CreateDirectory(scratchDirectory);
        var database = Path.Combine(scratchDirectory, "activity.sqlite");
        var journal = new EventJournal(database);
        var store = new WorkflowStore(journal);
        var empty = WorkflowContract.EmptyObject;
        var descriptor = new WorkflowCommandDescriptor("fixture.op", 1, "No execution", empty, empty, []);
        int checks = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        WorkflowRun Submit(string id, string chat) => store.Submit(new(id, chat, "fixture-profile", "fixture.activity", "1.0.0", "Synthetic", null, empty, empty));
        WorkflowRun Prepare(string id)
        {
            var run = store.Get(id);
            if (run.Status == WorkflowStatus.Queued) run = store.TryClaim(id)!;
            return store.ApplyStep(id, run.Revision, new(empty, new InvokeWorkflowCommand(descriptor.Id, 1, empty)), descriptor);
        }
        WorkflowActivityTotals Totals(string id) => store.GetWithActivityTotals(id).ActivityTotals!;
        WorkflowRun Finish(WorkflowRun prepared, WorkflowEffectState state)
        {
            var dispatched = store.DispatchEffect(prepared.Id, prepared.Revision, 1000);
            return store.CompleteEffect(dispatched.Id, dispatched.Effect!.Id, new(state), 0);
        }
        var first = Submit("activity-run-1", "activity-chat-1");
        var zero = Totals(first.Id);
        Check(zero.CoverageComplete && zero.Calls == 0 && zero.IsValidFor(first.Submission.ConversationId, first.Id, first.Revision), "Empty run identity/counts.");
        var prepared = Prepare(first.Id);
        Check(Totals(first.Id) is { Calls: 1, Unconfirmed: 1, Completed: 0 }, "Prepared call counted once as unconfirmed.");
        var complete = Finish(prepared, WorkflowEffectState.Succeeded);
        Check(Totals(first.Id) is { Calls: 1, Completed: 1, Unconfirmed: 0 }, "Receipt replaces prepared outcome without adding a call.");
        Finish(Prepare(first.Id), WorkflowEffectState.Failed);
        Check(Totals(first.Id) is { Calls: 2, Completed: 1, Failed: 1 }, "Exclusive terminal counts.");
        var uncertain = Finish(Prepare(first.Id), WorkflowEffectState.Unknown);
        Check(Totals(first.Id) is { Calls: 3, Completed: 1, Failed: 1, Unconfirmed: 1 }, "Unknown is not success/failure.");
        var reconciled = store.CompleteReconciledEffect(first.Id, uncertain.Revision, uncertain.Effect!.Id, new(WorkflowEffectState.Succeeded), 0);
        Check(Totals(first.Id) is { Calls: 3, Completed: 2, Failed: 1, Unconfirmed: 0 }, "Reconciliation updates one outcome.");
        Prepare(first.Id); store.Cancel(first.Id);
        var cancelled = Totals(first.Id);
        Check(cancelled is { Calls: 4, Completed: 2, Failed: 1, Cancelled: 1 }, "Undispatched cancellation remains a distinct call.");
        Check(Totals(first.Id) == cancelled && store.Get(first.Id).ActivityTotals is null, "Repeated refresh stable and totals not persisted as run control state.");
        Check(!cancelled.IsValidFor("foreign", first.Id, cancelled.RunRevision) &&
              !cancelled.IsValidFor(cancelled.ConversationId, "foreign", cancelled.RunRevision) &&
              !cancelled.IsValidFor(cancelled.ConversationId, first.Id, cancelled.RunRevision + 1) &&
              !cancelled.IsValidFor(cancelled.ConversationId, first.Id, cancelled.RunRevision, cancelled.AsOfSequence + 1), "Foreign/stale aggregates rejected.");
        var second = Submit("activity-run-2", "activity-chat-2");
        Check(Totals(second.Id) is { Calls: 0, Completed: 0 }, "Foreign run receipts excluded.");
        var rejected = Prepare(second.Id);
        rejected = store.Hold(second.Id, rejected.Revision, "Synthetic owner review");
        var request = new WorkflowPreparedRejectRequest("reject-one", second.Id, rejected.Revision, rejected.Effect!.Id,
            WorkflowPreparedEffectIdentity.Sha256(rejected.Effect), "Synthetic rejection", "Use another synthetic effect.");
        var rejectMethod = typeof(WorkflowStore).GetMethod("RejectPrepared", BindingFlags.Instance | BindingFlags.NonPublic)!;
        rejected = (WorkflowRun)rejectMethod.Invoke(store, [request, new Action<WorkflowRun>(_ => { })])!;
        Check(Totals(second.Id) is { Calls: 1, Blocked: 1, Failed: 0, Unconfirmed: 0 }, "Owner rejection is blocked, not an execution failure.");
        Prepare(second.Id);
        Check(Totals(second.Id) is { Calls: 2, Blocked: 1, Unconfirmed: 1 }, "Rejection remains counted after later planning.");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO workflow_effect_results(effect_id,run_id,result) VALUES($effect,$run,$result);";
            command.Parameters.AddWithValue("$effect", "malformed-fixture"); command.Parameters.AddWithValue("$run", second.Id);
            command.Parameters.AddWithValue("$result", "not-json"); command.ExecuteNonQuery();
        }
        Check(!Totals(second.Id).CoverageComplete && Totals(second.Id).DiagnosticCode == "invalid-receipt-metadata", "Malformed optional receipt metadata cannot fabricate authoritative totals.");
        Check(!(cancelled with { Completed = -1 }).IsValidFor(cancelled.ConversationId, first.Id, cancelled.RunRevision) &&
              !(cancelled with { Calls = 512, Completed = 512, Failed = 1 }).IsValidFor(cancelled.ConversationId, first.Id, cancelled.RunRevision), "Invalid sums/ranges rejected.");
        return checks;
    }
}
