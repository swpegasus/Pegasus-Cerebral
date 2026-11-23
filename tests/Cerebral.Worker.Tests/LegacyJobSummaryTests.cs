using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class LegacyJobSummaryTests
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-legacy-summary-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; }
        try
        {
            var store = new StateStore(root);
            var created = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
            AgentJob Add(string id, string status = "queued", string? visible = null, string session = "chat")
            {
                var job = new AgentJob
                {
                    Id = id, SessionId = session, ProfileId = "lead", Status = status, Kind = "agent", CreatedAt = created,
                    Prompt = "PRIVATE-ASSIGNMENT" + new string('x', 48 * 1024), VisiblePrompt = visible,
                    WorkflowSource = "PRIVATE-WORKFLOW", Result = "PRIVATE-RESULT", Error = "PRIVATE-ERROR",
                    Profile = new() { Id = "lead", Instructions = "PRIVATE-PROFILE" }
                };
                store.Put("job", id, job); return job;
            }
            Check(store.ActiveJobSummaries() is { Jobs.Count: 0, NextAfterRow: null }, "empty legacy active summary page");
            store.Put("session", "chat", new TaskSession { Id = "chat", ProfileId = "lead", Title = "Saved conversation" });
            Add("first", visible: "Visible\r\nrequest"); Add("completed", "completed");
            Add("second", "waiting-human"); Add("third", "running", new string('a', 159) + "\U0001F680remaining");
            var first = store.ActiveJobSummaries(2);
            Check(first.Jobs.Select(x => x.Id).SequenceEqual(["first", "second"]) && first.NextAfterRow is not null,
                "legacy active summaries page in insertion order without completed work");
            Check(first.Jobs[0] is { Title: "Visible  request", SessionId: "chat", ProfileId: "lead", Kind: "agent", Status: "queued" }
                && first.Jobs[0].CreatedAt == created && first.Jobs.All(x => x.UpdatedAt is null),
                "explicit visible title and existing fields survive without inventing an update timestamp");
            Check(first.Jobs[1].Title == "Saved conversation", "session title is the fallback instead of private backend prompt");
            var encoded = JsonSerializer.Serialize(first, CerebralJson.Options);
            Check(!encoded.Contains("PRIVATE-") && !encoded.Contains(new string('x', 1000)) && !encoded.Contains("instructions"),
                "queue projection omits backend prompt, workflow payload, profile, result and error");
            Add("first", "failed"); Add("fourth", "remote-unconfirmed", session: "missing");
            var next = store.ActiveJobSummaries(2, first.NextAfterRow!.Value);
            Check(next.Jobs.Select(x => x.Id).SequenceEqual(["third", "fourth"]) && next.NextAfterRow is null,
                "cursor survives an earlier updated job and includes a newly admitted job");
            Check(next.Jobs[0].Title.Length == 159 && !char.IsSurrogate(next.Jobs[0].Title[^1]),
                "legacy title bound does not split surrogate pairs");
            Check(next.Jobs[1].Title == "Background work", "missing visible and session title has a safe fallback");
            Add("helper", "waiting-helper"); Add("remote", "remote-waiting"); Add("cancelled", "cancelled");
            Add("unknown", "unrecognized"); store.Put("other", "not-a-job", new { status = "queued" });
            var all = new StateStore(root).ActiveJobSummaries(200);
            Check(all.Jobs.Select(x => x.Id).SequenceEqual(["second", "third", "fourth", "helper", "remote"]),
                "reopened summary matches recognized live legacy states and excludes other records");
            var savedCount = all.Jobs.Count;
            try
            {
                store.Locked<bool>(() =>
                {
                    Add("rolled-back");
                    Check(store.ActiveJobSummaries().Jobs.Count == savedCount + 1, "summary participates in current state transaction");
                    throw new InvalidOperationException("fixture rollback");
                });
            }
            catch (InvalidOperationException error) when (error.Message == "fixture rollback") { }
            Check(store.ActiveJobSummaries().Jobs.Count == savedCount, "rolled-back job never appears in later queue summaries");
            Check(store.ActiveJobSummaries(200, long.MaxValue) is { Jobs.Count: 0, NextAfterRow: null }, "cursor past end is an empty page");
            foreach (var action in new Action[] { () => store.ActiveJobSummaries(0), () => store.ActiveJobSummaries(201), () => store.ActiveJobSummaries(afterRow: -1) })
            {
                try { action(); throw new Exception("FAIL: invalid legacy summary bound was accepted"); }
                catch (ArgumentOutOfRangeException) { checks++; }
            }
            Console.WriteLine($"Legacy job summaries: {checks} SQLite projection and paging assertions passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("cerebral-legacy-summary-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid test cleanup root.");
            Directory.Delete(root, true);
        }
    }
}
