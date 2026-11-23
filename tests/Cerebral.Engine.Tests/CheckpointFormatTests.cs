using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class CheckpointFormatTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-checkpoint-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var count = 0;
        try
        {
            CreateFixtures(root, (value, message) =>
            {
                if (!value) throw new Exception("FAIL: " + message);
                count++; Console.WriteLine("PASS: " + message);
            });
            Console.WriteLine("Checkpoint format: " + count + " real SQLite checks passed.");
        }
        finally { Directory.Delete(root, true); }
    }

    // The independent old-binary probe reuses these actual production-store fixtures.
    public static IReadOnlyDictionary<string, string> CreateFixtures(string root, Action<bool, string> check)
    {
        var normal = Path.Combine(root, "normal.db");
        var journal = new EventJournal(normal);
        var store = new WorkflowStore(journal);
        check(Format(normal) == 6 && new EventJournal(normal).GetRevision("format-chat") == 0 && Format(normal) == 6,
            "opening or reopening without checkpoint adoption preserves the legacy journal format");
        var (run, checkpoint) = Dispatch(store, journal, "normal");
        check(Format(normal) == 6, "ordinary admission and dispatch do not upgrade the checkpoint format");
        var before = journal.GetRevision(run.Submission.ConversationId);
        check(Rejected<ArgumentException>(() => store.CompleteEffect(run.Id, run.Effect!.Id,
            Result(checkpoint with { BeforeCurrentInput = false }))) && Format(normal) == 6 &&
            journal.GetRevision(run.Submission.ConversationId) == before &&
            store.Get(run.Id).Revision == run.Revision,
            "invalid checkpoint adoption preserves format, run and journal");
        var done = store.CompleteEffect(run.Id, run.Effect!.Id, Result(checkpoint));
        var adopted = new WorkflowStore(new EventJournal(normal)).GetConversationCheckpoint(run.Submission.ConversationId, long.MaxValue)!;
        check(Format(normal) == 7 && adopted.Checkpoint.SourceLocators.SequenceEqual(checkpoint.SourceLocators),
            "successful production adoption marks the source-map format and retains exact locators across reopen");
        var exact = journal.ReadExact(run.Submission.ConversationId, checkpoint.Id, journal.GetRevision(run.Submission.ConversationId));
        check(exact.Type == "context.compaction" &&
            store.GetEffectResult(run.Id, checkpoint.Id).ConversationCheckpoint!.SourceLocators.SequenceEqual(checkpoint.SourceLocators),
            "the marked format includes the original effect receipt and exact-read checkpoint event");
        var committed = journal.GetRevision(run.Submission.ConversationId);
        check(store.CompleteEffect(run.Id, checkpoint.Id, Result(checkpoint)).Revision == done.Revision &&
            Format(normal) == 7 && journal.GetRevision(run.Submission.ConversationId) == committed,
            "duplicate checkpoint completion neither replays nor regresses the format");

        var collision = Path.Combine(root, "collision.db");
        var collisionJournal = new EventJournal(collision);
        var collisionStore = new WorkflowStore(collisionJournal);
        var (collisionRun, colliding) = Dispatch(collisionStore, collisionJournal, "collision");
        collisionJournal.Append(new(colliding.Id, collisionRun.Submission.ConversationId, collisionRun.Id, 0,
            "fixture.collision", new(EventActorKind.Service, "fixture"), [],
            new(ModelInclusion.OnDemand, HumanVisibility.Activity), new(EventSourceKind.Service, "fixture"),
            DateTimeOffset.UtcNow), collisionJournal.GetRevision(collisionRun.Submission.ConversationId));
        var collisionHead = collisionJournal.GetRevision(collisionRun.Submission.ConversationId);
        check(Rejected<InvalidOperationException>(() => collisionStore.CompleteEffect(collisionRun.Id, colliding.Id, Result(colliding))) &&
            Format(collision) == 6 && collisionStore.Get(collisionRun.Id).Revision == collisionRun.Revision &&
            collisionJournal.GetRevision(collisionRun.Submission.ConversationId) == collisionHead,
            "an event collision after the format mark rolls back the PRAGMA with run and journal");
        check(collisionStore.GetConversationCheckpoint(collisionRun.Submission.ConversationId, long.MaxValue) is null &&
            Rejected<KeyNotFoundException>(() => collisionStore.GetEffectResult(collisionRun.Id, colliding.Id)),
            "rolled-back format adoption leaves neither a baseline nor an effect result");
        collisionStore.CompleteEffect(collisionRun.Id, colliding.Id, new(WorkflowEffectState.Failed, Error: "Fixture collision."));
        check(Format(collision) == 6, "a failed effect without an adopted checkpoint does not mark a new format");

        var legacy = Path.Combine(root, "legacy.db");
        var legacyJournal = new EventJournal(legacy);
        var legacyCheckpoint = new ContextCheckpoint("legacy", 1, 0, [new("legacy-source", 1)],
            "Exact legacy summary.", "legacy", true);
        var legacyJson = JsonSerializer.SerializeToNode(legacyCheckpoint, JsonOptions)!.AsObject();
        legacyJson.Remove("sourceLocators");
        using (var connection = Open(legacy))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO conversation_context_checkpoints
                    (conversation_id,source_revision,run_id,effect_id,checkpoint,adopted_at)
                VALUES('legacy-chat',1,'legacy-run','legacy',$checkpoint,$at);
                """;
            command.Parameters.AddWithValue("$checkpoint", legacyJson.ToJsonString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var restored = new WorkflowStore(new EventJournal(legacy)).GetConversationCheckpoint("legacy-chat", long.MaxValue)!;
        check(Format(legacy) == 6 && restored.Checkpoint.Summary == legacyCheckpoint.Summary && restored.Checkpoint.SourceLocators.Count == 0,
            "new host reads an existing legacy checkpoint without rewriting it or upgrading its format");
        check(EventJournal.SchemaVersion >= 7, "the host advertises support for the persisted source-map format");
        return new Dictionary<string, string> { ["normal"] = normal, ["collision"] = collision, ["legacy"] = legacy };
    }

    private static (WorkflowRun Run, ContextCheckpoint Checkpoint) Dispatch(WorkflowStore store, EventJournal journal, string id)
    {
        var chat = id + "-chat";
        var source = new CerebralEvent(id + "-source", chat, id + "-previous", 0, CerebralEventTypes.Message,
            new(EventActorKind.User), [new TextContentPart("Fixture source.")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), DateTimeOffset.UtcNow);
        var appended = journal.Append(source, 0);
        var submission = new WorkflowSubmission(id + "-run", chat, "lead", "test.format:run", "1.0.0",
            "Fixture task.", "", WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);
        var run = store.Submit(submission);
        run = store.TryClaim(run.Id)!;
        var descriptor = new WorkflowCommandDescriptor("cerebral.host:compact", 1, "Compact", WorkflowContract.EmptyObject,
            WorkflowContract.EmptyObject, []);
        run = store.ApplyStep(run.Id, run.Revision, new(WorkflowContract.EmptyObject,
            new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, WorkflowContract.EmptyObject)), descriptor);
        run = store.DispatchEffect(run.Id, run.Revision);
        var checkpoint = new ContextCheckpoint(run.Effect!.Id, run.Submission.ConversationSequence, 0,
            [new(source.Id, appended.Event.Sequence)], "Fixture summary.", run.Effect.Id, true)
        {
            SourceLocators = [new(source.Id, appended.Event.Sequence, "cerebral.host", "conversation", source.Id, true)]
        };
        return (run, checkpoint);
    }

    private static WorkflowCommandResult Result(ContextCheckpoint checkpoint) => new(WorkflowEffectState.Succeeded,
        WorkflowContract.EmptyObject) { ConversationCheckpoint = checkpoint };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static bool Rejected<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
    public static long Format(string path)
    {
        using var connection = Open(path); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;"; return Convert.ToInt64(command.ExecuteScalar());
    }
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open(); return connection;
    }
}
