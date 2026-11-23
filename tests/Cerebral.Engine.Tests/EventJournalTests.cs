using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class EventJournalTests
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool result, string message) { if (!result) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        static bool Rejected<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        static CerebralEvent Item(string id, string conversation = "chat") => new(id, conversation, "turn", 0,
            CerebralEventTypes.ToolResult, new(EventActorKind.Tool, "books.context"), [new TextContentPart("Hidden chapter instructions: preserve the cliffhanger.")],
            new(ModelInclusion.Required, HumanVisibility.Hidden), new(EventSourceKind.Plugin, PluginId: "pegasus.books", PluginVersion: "2.0"),
            DateTimeOffset.Parse("2026-09-25T12:00:00Z"), JsonSerializer.SerializeToElement(new { chapter = 3 }));
        var root = Path.Combine(Path.GetTempPath(), "cerebral-event-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "events.db");
            var journal = new EventJournal(path);
            var source = Item("first");
            var first = journal.Append(source, 0);
            Check(first.Event.Sequence == 1 && !first.AlreadyCommitted && source.Sequence == 0, "journal assigns sequence without mutating the accepted request");
            var replay = journal.Append(source, 0);
            Check(replay.AlreadyCommitted && replay.Event.Sequence == 1 && journal.GetRevision("chat") == 1, "lost-response retry is idempotent even with a stale revision");
            Check(Rejected<InvalidOperationException>(() => journal.Append(source with { Content = [new TextContentPart("different")] }, 1)), "reusing an event ID with changed content is rejected");
            Check(Rejected<InvalidOperationException>(() => journal.Append(source with { ConversationId = "other" }, 0)), "event identity cannot be reused in another conversation");
            Check(Rejected<JournalRevisionException>(() => journal.Append(Item("stale"), 0)) && journal.GetRevision("chat") == 1, "stale appends do not advance conversation state");
            Check(Rejected<ArgumentException>(() => journal.Append(Item("ephemeral") with { Projection = new(Retention: RetentionPolicy.Ephemeral) }, 1)), "ephemeral progress cannot leak into persistent history");
            var reopened = new EventJournal(path);
            var restored = reopened.ReadPage("chat").Events.Single();
            Check(restored.Id == source.Id && EventProjection.IncludeInModel(restored) && !EventProjection.IsVisibleToHuman(restored), "reopened SQLite preserves hidden model context independently of presentation");
            for (var i = 2; i <= 5; i++) journal.Append(Item("event-" + i), i - 1);
            journal.Append(Item("other-chat", "other"), 0);
            var page1 = journal.ReadPage("chat", limit: 2);
            Check(page1.Events.Count == 2 && page1.NextAfterSequence == 2 && page1.SnapshotSequence == 5, "bounded page reports an explicit continuation and snapshot");
            journal.Append(Item("event-6"), 5);
            var page2 = reopened.ReadPage("chat", page1.NextAfterSequence!.Value, 2, page1.SnapshotSequence);
            var page3 = reopened.ReadPage("chat", page2.NextAfterSequence!.Value, 2, page1.SnapshotSequence);
            Check(page2.Events.Select(x => x.Sequence).SequenceEqual([3L, 4L]) && page3.Events.Single().Sequence == 5 && page3.NextAfterSequence is null,
                "pagination isolates conversation and excludes later arrivals until the next snapshot");
            Check(journal.ReadPage("chat", afterSequence: 5).Events.Single().Id == "event-6", "new snapshot can fetch only newly arrived events");
            var recent = journal.ReadPreviousPage("chat", limit: 2);
            var older = journal.ReadPreviousPage("chat", recent.NextBeforeSequence, 2, recent.SnapshotSequence);
            Check(recent.Events.Select(x => x.Sequence).SequenceEqual([5L, 6L]) && recent.NextBeforeSequence == 5 &&
                older.Events.Select(x => x.Sequence).SequenceEqual([3L, 4L]) && older.NextBeforeSequence == 3,
                "recent history pages backward while returning chronological events");
            var oldest = journal.ReadPreviousPage("chat", older.NextBeforeSequence, 2, recent.SnapshotSequence);
            Check(oldest.Events.Select(x => x.Sequence).SequenceEqual([1L, 2L]) && oldest.NextBeforeSequence is null &&
                journal.ReadPreviousPage("chat", beforeSequence: 1).Events.Count == 0,
                "backward pagination reaches the beginning without repeating or omitting events");
            Check(journal.ReadPage("absent").Events.Count == 0 && journal.ReadPage("absent").SnapshotSequence == 0, "missing conversation has a bounded empty page");
            Check(Rejected<ArgumentOutOfRangeException>(() => journal.ReadPage("chat", limit: 201)) &&
                Rejected<ArgumentOutOfRangeException>(() => journal.ReadPage("chat", snapshotSequence: 7)), "invalid page and future cursor are rejected");
            var races = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                try { new EventJournal(path).Append(Item("racer-" + index), 6); return true; }
                catch (JournalRevisionException) { return false; }
            })));
            Check(races.Count(x => x) == 1 && journal.GetRevision("chat") == 7, "independent connections serialize competing writes without duplicate sequence or lost updates");
            Check(journal.ReadPreviousPage("chat", limit: 2, snapshotSequence: recent.SnapshotSequence).Events.Select(x => x.Sequence).SequenceEqual([5L, 6L]),
                "recent snapshot excludes later arrivals across subsequent queries");
            Check(journal.ReadPreviousPage("absent").Events.Count == 0 &&
                Rejected<ArgumentOutOfRangeException>(() => journal.ReadPreviousPage("chat", beforeSequence: 9)) &&
                Rejected<ArgumentOutOfRangeException>(() => journal.ReadPreviousPage("chat", beforeSequence: 0)),
                "recent page validates cursors and supports an empty conversation");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check(Rejected<OperationCanceledException>(() => journal.Append(Item("cancelled"), 7, cancelled.Token)) && journal.GetRevision("chat") == 7,
                "cancelled append commits no event");
            var mutableParts = new List<ContentPart> { new TextContentPart("accepted") };
            var mutable = Item("frozen", "frozen") with { Content = mutableParts };
            var freeze = journal.Append(mutable, 0); mutableParts.Clear();
            Check(((TextContentPart)freeze.Event.Content.Single()).Text == "accepted" && journal.ReadPage("frozen").Events.Single().Content.Count == 1,
                "commit freezes caller-owned collections rather than retaining mutable aliases");
            // Bound by bytes too: a count-only page could otherwise load hundreds of large tool results.
            for (var i = 0; i < 6; i++) journal.Append(Item("large-" + i, "large") with { Content = [new TextContentPart(new string('x', 900_000))] }, i);
            var large = journal.ReadPage("large");
            Check(large.Events.Count == 3 && large.NextAfterSequence == 3 && journal.ReadPage("large", 3, snapshotSequence: large.SnapshotSequence).Events.Count == 3,
                "page byte limit bounds large-result memory without losing continuation");
            var largeRecent = journal.ReadPreviousPage("large");
            Check(largeRecent.Events.Select(x => x.Sequence).SequenceEqual([4L, 5L, 6L]) && largeRecent.NextBeforeSequence == 4 &&
                journal.ReadPreviousPage("large", largeRecent.NextBeforeSequence, snapshotSequence: largeRecent.SnapshotSequence).Events.Count == 3,
                "recent page also bounds bytes without scanning or returning the older large results");
            const string retentionConversation = "retention";
            var accepted = Item("retention-accepted", retentionConversation) with
            { CreatedAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z") };
            journal.Append(accepted, 0);
            var modelBearingRolling = Item("retention-model-bearing", retentionConversation) with
            {
                CreatedAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
                Projection = new(ModelInclusion.Required, HumanVisibility.Hidden, RetentionPolicy.Rolling)
            };
            journal.Append(modelBearingRolling, 1);
            for (var index = 0; index < 4; index++)
            {
                journal.Append(Item("retention-diagnostic-" + index, retentionConversation) with
                {
                    Type = CerebralEventTypes.WorkflowTransition,
                    CreatedAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z").AddMinutes(index),
                    Projection = new(ModelInclusion.OnDemand, HumanVisibility.Activity, RetentionPolicy.Rolling),
                    Data = JsonSerializer.SerializeToElement(new { reason = "fixture" })
                }, index + 2);
            }
            var absentHasGap = false;
            try { journal.ReadExact(retentionConversation, "never-existed", 6); }
            catch (JournalSourceUnavailableException ex) { absentHasGap = ex.RetentionGap is not null; }
            Check(!absentHasGap, "an exact source missing before pruning is distinguishable from expired rolling detail");
            using (var cancelPrune = new CancellationTokenSource())
            {
                cancelPrune.Cancel();
                Check(Rejected<OperationCanceledException>(() => journal.PruneRolling(retentionConversation,
                    new(1, 4 * 1024 * 1024, 30), DateTimeOffset.Parse("2026-09-26T00:00:00Z"), cancelPrune.Token)) &&
                    journal.ReadPage(retentionConversation).Events.Count == 6,
                    "cancelled rolling cleanup commits neither deletions nor metadata");
            }
            var pruned = journal.PruneRolling(retentionConversation, new(1, 4 * 1024 * 1024, 30),
                DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
            var retained = journal.ReadPage(retentionConversation);
            Check(pruned.RemovedEvents == 4 && pruned.RemovedBytes > 0 && !pruned.QuotaSatisfied && pruned.PinnedRollingEvents == 1 &&
                retained.Events.Select(x => x.Id).SequenceEqual([accepted.Id, modelBearingRolling.Id]) &&
                retained.Events.All(x => EventProjection.IncludeInModel(x)),
                "rolling cleanup preserves accepted or model-bearing context and exposes quota pressure from a pinned rolling source");
            Check(retained.SnapshotSequence == 6 && retained.RetentionGap is { PrunedEvents: 4, FirstPrunedSequence: 3, LastPrunedSequence: 6 } &&
                journal.ReadPreviousPage(retentionConversation).RetentionGap == retained.RetentionGap &&
                journal.GetRetentionStatus(retentionConversation).LastLimits == new JournalRetentionLimits(1, 4 * 1024 * 1024, 30),
                "forward, backward and status reads expose the retained revision and cumulative rolling gap");
            JournalSourceUnavailableException? expired = null;
            try { journal.ReadExact(retentionConversation, "retention-diagnostic-0", 6); }
            catch (JournalSourceUnavailableException ex) { expired = ex; }
            Check(expired?.RetentionGap == retained.RetentionGap &&
                journal.PruneRolling(retentionConversation, new(1, 4 * 1024 * 1024, 30),
                    DateTimeOffset.Parse("2026-09-26T00:00:00Z")).RemovedEvents == 0 &&
                journal.GetRevision(retentionConversation) == 6,
                "expired exact detail reports its retention gap and repeated cleanup is deterministic without rewriting revision");
            using (var unrelated = new SqliteConnection("Data Source=" + Path.Combine(root, "unrelated.db") + ";Pooling=False"))
            {
                unrelated.Open(); using var sql = unrelated.CreateCommand(); sql.CommandText = "CREATE TABLE user_data (value TEXT);"; sql.ExecuteNonQuery();
            }
            Check(Rejected<InvalidOperationException>(() => new EventJournal(Path.Combine(root, "unrelated.db"))), "journal refuses to adopt an unrelated database");
            using (var future = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                future.Open(); using var sql = future.CreateCommand(); sql.CommandText = "PRAGMA user_version=99;"; sql.ExecuteNonQuery();
            }
            Check(Rejected<InvalidOperationException>(() => new EventJournal(path)), "older engine refuses an incompatible future journal schema");
            Console.WriteLine($"Event journal: {count} local SQLite integration assertions passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
