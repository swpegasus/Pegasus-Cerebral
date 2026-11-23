using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

internal static class WorkflowIdleExpiryTests
{
    public static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("cerebral-workflow-expiry-");
        var database = Path.Combine(directory.FullName, "engine.db");
        var journal = new EventJournal(database);
        var store = new WorkflowStore(journal);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-7);
        var empty = JsonSerializer.SerializeToElement(new { });
        var command = new WorkflowCommandDescriptor("test:work", 1, "Test work", empty, empty, []);
        const string interrupted =
            "Worker execution was interrupted before the next effect. The accepted work can be resumed.";
        WorkflowRun Running(string id) => store.TryClaim(store.Submit(
            new WorkflowSubmission(id, id, "lead", "test:work", "1.0.0", "Original request", null, empty, empty)).Id)!;
        WorkflowRun Prepared(string id)
        {
            var run = Running(id);
            return store.ApplyStep(id, run.Revision, new(empty, new InvokeWorkflowCommand("test:work", 1, empty)), command);
        }
        void Age(WorkflowRun run, DateTimeOffset timestamp)
        {
            using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE workflow_runs SET payload=json_set(payload,'$.updatedAt',$time) WHERE id=$id;";
            update.Parameters.AddWithValue("$time", timestamp.ToString("O"));
            update.Parameters.AddWithValue("$id", run.Id);
            Check(update.ExecuteNonQuery() == 1, "fixture run age was set");
        }

        try
        {
            var prepared = Prepared("idle");
            var waiting = store.Hold(prepared.Id, prepared.Revision, interrupted);
            var pending = Prepared("uncertain");
            var dispatched = store.DispatchEffect(pending.Id, pending.Revision);
            var uncertain = store.Hold(dispatched.Id, dispatched.Revision, interrupted);
            var asking = Running("asking");
            var question = store.ApplyStep(asking.Id, asking.Revision,
                new(empty, new AskWorkflowQuestions([new("answer", "What next?")])));
            var audit = Running("audit");
            var auditWait = store.Hold(audit.Id, audit.Revision, "Required audit held this checkpoint.");
            var recent = Running("recent");
            var recentWait = store.Hold(recent.Id, recent.Revision, interrupted);
            foreach (var run in new[] { waiting, uncertain, question, auditWait }) Age(run, cutoff.AddMinutes(-1));
            Age(recentWait, cutoff.AddMinutes(1));

            var reopened = new WorkflowStore(new EventJournal(database));
            var expired = reopened.ExpireIdleInterrupted(cutoff).Single();
            Check(expired.Id == waiting.Id && expired.Revision == waiting.Revision + 1 &&
                expired.IsTerminal && expired.Status == WorkflowStatus.Failed &&
                expired.Submission.UserText == "Original request" &&
                expired.Effect is { State: WorkflowEffectState.Cancelled } && expired.Effect.Id == waiting.Effect!.Id &&
                expired.Error?.Contains("expired", StringComparison.Ordinal) == true,
                "stale interrupted work expires once and preserves original history and prepared effect identity");
            Check(reopened.ExpireIdleInterrupted(cutoff).Count == 0 &&
                reopened.Get(uncertain.Id) is { Status: WorkflowStatus.Waiting, Effect.State: WorkflowEffectState.Unknown } retained &&
                retained.Effect!.Id == uncertain.Effect!.Id && retained.Revision == uncertain.Revision &&
                reopened.Get(question.Id).Questions is not null && reopened.Get(auditWait.Id).Revision == auditWait.Revision &&
                reopened.Get(recentWait.Id).Revision == recentWait.Revision,
                "pending answers, uncertain effects, deliberate holds and recent work survive the sweep");

            var oldRunning = Prepared("old-running");
            var newRunning = Running("new-running");
            var oldDispatched = Prepared("old-dispatched");
            oldDispatched = store.DispatchEffect(oldDispatched.Id, oldDispatched.Revision);
            Age(oldRunning, cutoff.AddMinutes(-1));
            Age(oldDispatched, cutoff.AddMinutes(-1));
            Check(reopened.RecoverInterrupted(cutoff) == 3 &&
                reopened.Get(oldRunning.Id) is { Status: WorkflowStatus.Failed, Effect.State: WorkflowEffectState.Cancelled } &&
                reopened.Get(newRunning.Id).Status == WorkflowStatus.Queued &&
                reopened.Get(oldDispatched.Id) is { Status: WorkflowStatus.Waiting, Effect.State: WorkflowEffectState.Unknown },
                "startup expires overdue safe work, requeues recent work and keeps dispatched work for reconciliation");

            var disabled = Running("disabled");
            Age(disabled, cutoff.AddDays(-1));
            Check(reopened.RecoverInterrupted() == 1 && reopened.Get(disabled.Id).Status == WorkflowStatus.Queued,
                "recovery without an expiry cutoff preserves the previous requeue behavior");
            Console.WriteLine("Workflow idle expiry SQLite checks passed.");
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(recursive: true); }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
    }
}
