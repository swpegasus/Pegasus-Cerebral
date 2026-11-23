using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class ConversationRenameTests
{
    public static void Run()
    {
        const string definition = "pegasus.conversation:chat";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-chat-rename-" + Guid.NewGuid().ToString("N")));
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or WorkflowConflictException or KeyNotFoundException or SqliteException)
            { checks++; Console.WriteLine("PASS: " + message); return; }
            throw new Exception("FAIL: " + message);
        }
        try
        {
            var path = Path.Combine(root, "engine.db");
            var journal = new EventJournal(path);
            var store = new WorkflowStore(journal);
            WorkflowSubmission Submit(string runId, string conversationId, string message) =>
                new(runId, conversationId, "lead", definition, "1.0.0", message,
                    "PRIVATE-ASSIGNMENT-CONTEXT", JsonSerializer.SerializeToElement(new { }),
                    JsonSerializer.SerializeToElement(new { privateSetting = "PRIVATE-SETTINGS" }));
            var run = store.Submit(Submit("first-run", "chat", "Original user question"));
            var initial = store.GetConversation(definition, "chat")!;
            Check(initial.Title == "Original user question" && initial.TitleRevision == 0,
                "unrenamed conversation keeps its derived title and revision zero");
            var initialRun = JsonSerializer.Serialize(store.Get(run.Id));
            var initialEvents = JsonSerializer.Serialize(journal.ReadPage("chat"));
            var request = new ConversationRenameRequest("rename-one", definition, "chat", 0, "New owner title");
            var first = store.RenameConversation(request);
            Check(first == new ConversationRenameResult("chat", "New owner title", 1),
                "first owner rename creates a durable revisioned receipt");
            Check(store.GetConversation(definition, "chat") is { Title: "New owner title", TitleRevision: 1 } &&
                store.ListConversations(definition).Conversations.Single() is { Title: "New owner title", TitleRevision: 1 },
                "exact lookup and bounded list project the persisted title override");
            Check(JsonSerializer.Serialize(store.Get(run.Id)) == initialRun &&
                JsonSerializer.Serialize(journal.ReadPage("chat")) == initialEvents,
                "renaming active work changes neither accepted input, private assignment, run state nor event history");
            Check(store.RenameConversation(request) == first &&
                store.GetConversation(definition, "chat")!.TitleRevision == 1,
                "lost-response retry returns the same receipt without another revision");
            Reject(() => store.RenameConversation(request with { Title = "Different payload" }),
                "request ID reuse with different text conflicts");
            Reject(() => store.RenameConversation(request with { ExpectedTitleRevision = 1 }),
                "request ID reuse with a different expected revision conflicts");
            Reject(() => store.RenameConversation(new("stale", definition, "chat", 0, "Stale edit")),
                "a stale independent rename cannot overwrite current title");
            var second = store.RenameConversation(new("rename-two", definition, "chat", 1, "Second owner title"));
            Check(second.TitleRevision == 2 && store.RenameConversation(request) == first &&
                store.GetConversation(definition, "chat")!.Title == "Second owner title",
                "an old successful retry does not revert a later owner rename");
            var sameText = store.RenameConversation(new("rename-three", definition, "chat", 2, "Second owner title"));
            Check(sameText.TitleRevision == 3,
                "an independently accepted same-text rename advances revision for stale-editor detection");

            store.Cancel(run.Id);
            var priorSequence = journal.ReadPage("chat").SnapshotSequence;
            store.Submit(Submit("second-run", "chat", "Follow-up that must not replace the owner title"));
            Check(store.GetConversation(definition, "chat") is { Title: "Second owner title", TitleRevision: 3, LatestRunId: "second-run" },
                "later conversation turns preserve the owner's title and metadata revision");
            Check(store.Get("second-run").Submission.ConversationSequence == priorSequence,
                "rename metadata adds no hidden model-context event or sequence");
            var reopened = new WorkflowStore(new EventJournal(path));
            Check(reopened.GetConversation(definition, "chat") is { Title: "Second owner title", TitleRevision: 3 } &&
                reopened.RenameConversation(request) == first,
                "title override and request-id receipts survive reopening the database");
            Reject(() => reopened.RenameConversation(new("missing", definition, "absent", 0, "Title")),
                "unknown conversations cannot create orphan title records");
            Reject(() => reopened.RenameConversation(new("foreign-definition", "other:chat", "chat", 3, "Title")),
                "rename requires the conversation to exist on the specified workflow surface");
            Check(reopened.GetConversation("other:chat", "chat") is null,
                "a title override does not expose a conversation to a foreign definition listing");

            store.Submit(Submit("unicode-run", "unicode-chat", "Unicode source"));
            var maximum = string.Concat(Enumerable.Repeat("🚀", ConversationRenameContract.MaximumTitleScalars));
            var unicode = store.RenameConversation(new("unicode-rename", definition, "unicode-chat", 0, maximum));
            Check(unicode.Title == maximum && store.GetConversation(definition, "unicode-chat")!.Title == maximum,
                "160 supplementary Unicode characters round-trip without truncating surrogate pairs");
            var readable = "Café — 火星での冒険";
            var readableResult = store.RenameConversation(new("readable-rename", definition, "unicode-chat", 1, readable));
            Check(readableResult.Title == readable && readableResult.TitleRevision == 2,
                "mixed-script titles preserve their exact Unicode text");
            var eventBeforeInvalid = JsonSerializer.Serialize(journal.ReadPage("unicode-chat"));
            foreach (var invalid in new[] { "", " \t ", "first\nsecond", "line\u2028break", "bad\0title", "\ud800",
                new string('a', 161), maximum + "🚀" })
                Reject(() => store.RenameConversation(new("invalid-title", definition, "unicode-chat", 2, invalid)),
                    "blank, multiline, malformed or over-limit title is rejected");
            Reject(() => store.RenameConversation(new("bad-revision", definition, "unicode-chat", -1, "Title")),
                "negative title revisions are rejected");
            Reject(() => store.RenameConversation(new("bad request id", definition, "unicode-chat", 2, "Title")),
                "request identifiers use the existing bounded workflow contract");
            Check(store.GetConversation(definition, "unicode-chat") is { TitleRevision: 2 } &&
                JsonSerializer.Serialize(journal.ReadPage("unicode-chat")) == eventBeforeInvalid,
                "failed title validation leaves title revision and context history unchanged");

            store.Submit(Submit("race-run", "race-chat", "Race source"));
            var competing = Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                var connection = new WorkflowStore(new EventJournal(path));
                try { connection.RenameConversation(new("race-" + index, definition, "race-chat", 0, "Race title " + index)); return true; }
                catch (WorkflowConflictException) { return false; }
            }))).GetAwaiter().GetResult();
            Check(competing.Count(value => value) == 1 && store.GetConversation(definition, "race-chat")!.TitleRevision == 1,
                "separate store instances atomically admit one competing rename for the same revision");

            store.Submit(Submit("rollback-run", "rollback-chat", "Rollback source"));
            var rollbackRequest = new ConversationRenameRequest("rollback-rename", definition, "rollback-chat", 0, "Rollback target");
            ExecuteSql(path, """
                CREATE TRIGGER rename_fixture_abort BEFORE INSERT ON workflow_conversation_renames
                WHEN NEW.request_id='rollback-rename'
                BEGIN SELECT RAISE(ABORT, 'rename receipt fixture rollback'); END;
                """);
            Reject(() => store.RenameConversation(rollbackRequest),
                "a SQLite receipt failure aborts the title transaction");
            Check(store.GetConversation(definition, "rollback-chat") is { Title: "Rollback source", TitleRevision: 0 },
                "failed receipt persistence rolls back the title override atomically");
            ExecuteSql(path, "DROP TRIGGER rename_fixture_abort;");
            Check(store.RenameConversation(rollbackRequest) is { TitleRevision: 1 },
                "an uncommitted rename request remains retryable after storage recovery");
            Check(!JsonSerializer.Serialize(store.ListConversations(definition)).Contains("PRIVATE-", StringComparison.Ordinal),
                "navigation projections continue to exclude private assignment and execution settings");
            Console.WriteLine($"Conversation rename: {checks} SQLite metadata, CAS, retry, restart, Unicode and context-isolation assertions passed.");
        }
        finally
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(root).StartsWith("cerebral-chat-rename-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact rename fixture.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
