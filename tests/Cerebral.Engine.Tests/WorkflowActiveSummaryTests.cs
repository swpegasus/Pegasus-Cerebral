using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class WorkflowActiveSummaryTests
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-active-summary-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; }
        try
        {
            var path = Path.Combine(root, "engine.db");
            var store = new WorkflowStore(new EventJournal(path));
            WorkflowRun Add(string id, string title = "Visible\r\ntitle") => store.Submit(new(id, id, "lead", "test.review:review", "1.0.0",
                title, "PRIVATE-ASSIGNMENT", JsonSerializer.SerializeToElement(new { text = new string('x', 48 * 1024) }),
                JsonSerializer.SerializeToElement(new { secret = "PRIVATE-SETTINGS" })));
            Check(store.ListActiveSummaries() is { Runs.Count: 0, NextAfterRow: null }, "empty active summary page");
            Add("first"); Add("cancelled"); store.Cancel("cancelled"); Add("second");
            var running = store.TryClaim("second")!;
            var waiting = store.ApplyStep(running.Id, running.Revision,
                new(WorkflowContract.EmptyObject, new AskWorkflowQuestions([new("label", "Choose a label")])));
            Add("third", new string('a', 159) + "\U0001F680remaining");
            foreach (var success in new[] { true, false })
            {
                var id = success ? "completed" : "failed"; Add(id);
                var run = store.TryClaim(id)!;
                store.ApplyStep(id, run.Revision, new(WorkflowContract.EmptyObject,
                    new CompleteWorkflow(JsonSerializer.SerializeToElement(new { hidden = "PRIVATE-RESULT" }), success, success ? null : "fixture failure")));
            }
            var page = store.ListActiveSummaries(2);
            Check(page.Runs.Select(x => x.Id).SequenceEqual(["first", "second"]) && page.NextAfterRow is not null,
                "active summaries page in insertion order and exclude cancelled work");
            Check(page.Runs[1].Status == WorkflowStatus.Waiting && page.Runs[1].Revision == waiting.Revision
                && page.Runs[1].UpdatedAt == waiting.UpdatedAt, "current waiting status and durable revision are projected");
            Check(page.Runs[0].Title == "Visible  title", "titles use visible user text on one line");
            var encoded = JsonSerializer.Serialize(page);
            Check(!encoded.Contains("PRIVATE-") && !encoded.Contains("settingsSnapshot") && !encoded.Contains(new string('x', 1000)),
                "active summaries omit assignment, input, settings, results and model state");
            store.Cancel("first"); Add("fourth");
            var next = store.ListActiveSummaries(2, page.NextAfterRow!.Value);
            Check(next.Runs.Select(x => x.Id).SequenceEqual(["third", "fourth"]) && next.NextAfterRow is null,
                "row cursor survives earlier terminal changes and includes newly admitted work");
            Check(next.Runs[0].Title.Length == 159 && !char.IsSurrogate(next.Runs[0].Title[^1]),
                "bounded titles never split a UTF-16 surrogate pair");
            var reopened = new WorkflowStore(new EventJournal(path));
            Check(reopened.ListActiveSummaries().Runs.Select(x => x.Id).SequenceEqual(["second", "third", "fourth"]),
                "reopened summary excludes all terminal statuses without changing live records");
            Check(store.ListActiveSummaries(200, long.MaxValue) is { Runs.Count: 0, NextAfterRow: null }, "cursor past end is an empty page");
            foreach (var action in new Action[] { () => store.ListActiveSummaries(0), () => store.ListActiveSummaries(201), () => store.ListActiveSummaries(afterRow: -1) })
            {
                try { action(); throw new Exception("FAIL: invalid summary bound was accepted"); }
                catch (ArgumentOutOfRangeException) { checks++; }
            }
            Console.WriteLine($"Workflow active summaries: {checks} SQLite projection and paging assertions passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("cerebral-active-summary-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid test cleanup root.");
            Directory.Delete(root, true);
        }
    }
}
