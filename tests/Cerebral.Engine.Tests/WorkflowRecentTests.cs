using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class WorkflowRecentTests
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-recent-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; }
        try
        {
            var store = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            void Add(string id) => store.Submit(new(id, id, "lead", "test.review:review", "1.0.0",
                new string('x', 170) + "\nvisible", "PRIVATE-ASSIGNMENT", WorkflowContract.EmptyObject,
                JsonSerializer.SerializeToElement(new { secret = "PRIVATE-SETTINGS" })));
            Check(store.ListRecentPage().Runs.Count == 0, "empty recent page");
            Add("first"); Add("second"); Add("third");
            store.Cancel("second");
            var first = store.ListRecentPage(2);
            Check(first.Runs.Select(x => x.Id).SequenceEqual(["third", "second"]) && first.NextBeforeRow is not null, "recent rows include terminal work in reverse admission order");
            Check(first.Runs[1].Status == WorkflowStatus.Cancelled, "terminal status is retained");
            Check(first.Runs.All(x => x.Title.Length == 160), "titles are bounded in SQL");
            var serialized = JsonSerializer.Serialize(first);
            Check(!serialized.Contains("PRIVATE-") && !serialized.Contains("settingsSnapshot"), "list projections omit assignment and settings");
            Add("fourth");
            var second = store.ListRecentPage(2, first.NextBeforeRow, first.SnapshotRow);
            Check(second.Runs.Select(x => x.Id).SequenceEqual(["first"]) && second.NextBeforeRow is null, "new insertions cannot perturb the saved page snapshot");
            Check(store.ListRecentPage(1).Runs[0].Id == "fourth", "fresh refresh sees new work");
            var reopened = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            Check(reopened.ListRecentPage().Runs.Count == 4, "history is available after reopen");
            foreach (var action in new Action[] { () => store.ListRecentPage(0), () => store.ListRecentPage(201), () => store.ListRecentPage(beforeRow: 0), () => store.ListRecentPage(snapshotRow: long.MaxValue) })
            {
                try { action(); throw new Exception("FAIL: invalid recent bound was accepted"); }
                catch (ArgumentException) { checks++; }
            }
            Console.WriteLine($"Workflow recent summaries: {checks} SQLite projection and paging assertions passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("cerebral-recent-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid test cleanup root.");
            Directory.Delete(root, true);
        }
    }
}
