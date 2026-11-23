using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class StateStoreTests
{
    public static void Run()
    {
        static void check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            Console.WriteLine("PASS: " + description);
        }
        var directory = Path.Combine(Path.GetTempPath(), "cerebral-state-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new StateStore(directory);
            store.Put("value", "existing", "original");
            var session = new TaskSession { Id = "session", ProfileId = "profile", Title = "Saved task" };
            store.Put("session", session.Id, session);
            var threw = false;
            try
            {
                store.Locked<bool>(() =>
                {
                    store.Put("job", "new-job", new AgentJob { Id = "new-job", SessionId = session.Id });
                    store.Delete("value", "existing");
                    store.Append(session.Id, "user", "This message must roll back with the job.");
                    throw new InvalidOperationException("fixture interruption");
                });
            }
            catch (InvalidOperationException error) when (error.Message == "fixture interruption") { threw = true; }
            var reopened = new StateStore(directory);
            check(threw && reopened.Get<AgentJob>("job", "new-job") is null && reopened.Get<string>("value", "existing") == "original"
                && reopened.Get<TaskSession>("session", session.Id) is { Revision: 0, Messages.Count: 0 },
                "SQLite transaction rolls back job, delete, and nested session append together");

            store.Locked(() =>
            {
                store.Put("value", "existing", "updated");
                store.Locked(() => { store.Put("value", "second", "nested"); return true; });
                check(store.Get<string>("value", "existing") == "updated" && store.List<string>("value").Count == 2,
                    "transaction reads see its own writes across nested operations");
                store.Append(session.Id, "user", "committed message");
                return true;
            });
            reopened = new StateStore(directory);
            check(reopened.Get<string>("value", "existing") == "updated" && reopened.Get<string>("value", "second") == "nested"
                && reopened.Get<TaskSession>("session", session.Id) is { Revision: 1, Messages.Count: 1 },
                "successful nested transaction persists all related records");

            var oldRevision = store.Get<TaskSession>("session", session.Id)!.Revision;
            store.Append(session.Id, "assistant", "new evidence arrives during compaction");
            var staleAccepted = store.AdoptCheckpoint(session.Id, oldRevision, 1, "stale summary");
            var afterConflict = reopened.Get<TaskSession>("session", session.Id)!;
            check(!staleAccepted && afterConflict.Checkpoint is null && afterConflict.CheckpointThroughSequence == 0 && afterConflict.Messages.Count == 2,
                "stale compaction checkpoint preserves original history and current revision");
            check(store.AdoptCheckpoint(session.Id, afterConflict.Revision, 1, "current summary")
                && reopened.Get<TaskSession>("session", session.Id) is { Checkpoint: "current summary", CheckpointThroughSequence: 1, Messages.Count: 2 },
                "current checkpoint commits while retaining underlying messages");

            var initial = store.Get<TaskSession>("session", session.Id)!;
            Parallel.For(0, 20, i => store.Append(session.Id, "user", "parallel message " + i));
            var final = reopened.Get<TaskSession>("session", session.Id)!;
            check(final.Messages.Count == initial.Messages.Count + 20 && final.Revision == initial.Revision + 20
                && final.Messages.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(1, final.Messages.Count)),
                "concurrent session appends retain every message with ordered unique sequences");

            var latestPage = reopened.GetSession(session.Id, messageLimit: 5)!;
            var olderPage = reopened.GetSession(session.Id, latestPage.OldestLoadedSequence, messageLimit: 5)!;
            check(latestPage.MessageCount == final.Messages.Count && latestPage.Messages.Count == 5 && latestPage.HasEarlierMessages &&
                latestPage.Messages.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(final.Messages.Count - 4, 5)) &&
                olderPage.Messages.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(final.Messages.Count - 9, 5)),
                "conversation pages read bounded ordered rows without loading the full retained history");
            check(reopened.List<TaskSession>("session") is [{ Messages: { Count: 0 }, MessageCount: 0 }],
                "conversation lists read metadata without materializing message bodies");

            var bounded = new TaskSession { Id = "bounded", ProfileId = "profile", Title = "Bounded work" };
            store.Put("session", bounded.Id, bounded);
            for (var index = 1; index <= 20; index++)
                store.Append(bounded.Id, index % 2 == 1 ? "user" : "assistant", "saved " + index,
                    origin: index % 2 == 1 ? "user" : "model");
            var boundedBeforeCheckpoint = store.Get<TaskSession>("session", bounded.Id)!;
            check(store.AdoptCheckpoint(bounded.Id, boundedBeforeCheckpoint.Revision, 20, "bounded checkpoint"),
                "bounded work fixture adopts a checkpoint");
            store.Append(bounded.Id, "user", "current request");
            store.Append(bounded.Id, "assistant", "current response", origin: "model");
            var metadata = reopened.GetSessionMetadata(bounded.Id)!;
            var working = reopened.GetSessionForWork(bounded.Id, 21)!;
            var compactable = reopened.GetSessionForCompaction(bounded.Id)!;
            check(metadata.Messages.Count == 0 && metadata.Revision == 23,
                "conversation metadata reads do not materialize message rows");
            check(working.Messages.Select(x => x.Sequence).SequenceEqual(new[] { 7, 9, 11, 13, 15, 17, 19, 21, 22 }) &&
                  working.Messages.All(x => x.Sequence > 20 || WorkingContext.IsUserRequest(x)),
                "checkpointed turn reads retain current work and recent user intent without old assistant rows");
            check(compactable.Messages.Select(x => x.Sequence).SequenceEqual(new[] { 21, 22 }),
                "compaction reads only active rows newer than the adopted checkpoint");

            using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "cerebral.db") }.ToString());
            database.Open();
            using var normalized = database.CreateCommand();
            normalized.CommandText = "SELECT (SELECT COUNT(*) FROM sessions),(SELECT COUNT(*) FROM session_messages),(SELECT COUNT(*) FROM records WHERE kind='session')";
            using var counts = normalized.ExecuteReader(); counts.Read();
            check(counts.GetInt64(0) == 2 && counts.GetInt64(1) == final.Messages.Count + 22 && counts.GetInt64(2) == 0,
                "conversation metadata and messages use normalized SQLite tables rather than one session JSON blob");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // The unique fixture directory is created above, never supplied by a caller.
            Directory.Delete(directory, recursive: true);
        }
    }
}
