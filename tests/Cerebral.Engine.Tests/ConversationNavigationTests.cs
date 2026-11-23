using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;

namespace Cerebral.Engine.Tests;

public static class ConversationNavigationTests
{
    public static void Run()
    {
        const string definition = "pegasus.conversation:chat";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-chat-nav-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; }
        try
        {
            var journal = new EventJournal(Path.Combine(root, "engine.db"));
            var store = new WorkflowStore(journal);
            WorkflowSubmission Request(string id, string chat, string title, string? project = null) =>
                new(id, chat, "lead", definition, "1.0.0", title, "PRIVATE-ASSIGNMENT",
                    WorkflowContract.EmptyObject,
                    JsonSerializer.SerializeToElement(new { marker = "PRIVATE-SETTINGS" })) { ProjectId = project };
            WorkflowRun Add(string id, string chat, string title, string? project = null)
            {
                var run = store.Submit(Request(id, chat, title, project));
                store.Cancel(id); return run;
            }
            Check(store.ListConversations(definition).Conversations.Count == 0, "empty list");
            Check(store.GetConversation(definition, "missing") is null, "exact missing ID");
            var first = Add("one", "chat-a", new string('a', 160) + "\nrest", "project-a");
            Check(first.Submission.ConversationSequence == 0, "first admission has no prior history");
            var oldRevision = journal.ReadPage("chat-a").SnapshotSequence;
            Add("two", "chat-b", "B");
            var third = Add("three", "chat-a", "follow up");
            Check(third.Submission.ConversationSequence == oldRevision, "admission atomically captures preceding event boundary");
            var repeated = store.Submit(Request("three", "chat-a", "follow up") with { ConversationSequence = long.MaxValue });
            Check(repeated.Id == third.Id && repeated.Submission.ConversationSequence == oldRevision,
                "retry returns original admission without trusting a supplied boundary");
            Add("four", "chat-c", "C");
            var page = store.ListConversations(definition, 2);
            Check(page.Conversations.Select(x => x.Id).SequenceEqual(["chat-c", "chat-a"]) && page.NextBeforeRow is not null,
                "one compact row per conversation ordered by latest turn");
            var a = page.Conversations[1];
            Check(a.Title == new string('a', 160) && a.LatestRunId == "three" &&
                a.ProjectId == "project-a" && a.ProfileId == "lead" && a.Status == WorkflowStatus.Cancelled,
                "first title with latest state and retained project");
            Check(!JsonSerializer.Serialize(page).Contains("PRIVATE-"), "projection omits model state and assignment");
            Add("five", "chat-b", "new B");
            var older = store.ListConversations(definition, 2, page.NextBeforeRow, page.SnapshotRow);
            Check(older.Conversations.Count == 1 && older.Conversations[0].LatestRunId == "two" && older.NextBeforeRow is null,
                "saved page snapshot is stable while an older conversation gets a new turn");
            Check(store.GetConversation(definition, "chat-b")?.LatestRunId == "five", "exact lookup sees latest turn");
            var imageOnly = Request("image-only", "chat-image", "") with
            {
                AcceptedContent =
                [
                    new ImageContentPart(new("image-fixture", 1, "image/png", "fixture.png", 42,
                        new Dictionary<string, string>()), "Fixture image", 16, 16)
                ]
            };
            store.Cancel(store.Submit(imageOnly).Id);
            Check(store.GetConversation(definition, "chat-image")?.Title == "Attachments",
                "an intentional image-only conversation receives a visible navigation title without inventing prompt text");
            Check(store.GetConversation("other:chat", "chat-a") is null, "definition isolation");
            try { Add("six", "chat-b", "late project assignment", "project-b"); throw new Exception("FAIL: late project binding admitted"); }
            catch (WorkflowConflictException) { checks++; }
            Check(store.GetConversation(definition, "chat-b")?.ProjectId is null &&
                !store.HasConversationForProject(definition, "project-b"),
                "retained unbound work cannot acquire a project through later workflow input");
            Check(store.HasConversationForProject(definition, "project-a") &&
                !store.HasConversationForProject(definition, "project-none"), "project references queried without loading history");
            var reopened = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            Check(reopened.ListConversations(definition).Conversations.Count == 4, "reopen retains navigation summaries");
            foreach (var call in new Action[] {
                () => store.ListConversations(definition, 0),
                () => store.ListConversations(definition, 201),
                () => store.ListConversations(definition, beforeRow: 0),
                () => store.ListConversations(definition, snapshotRow: long.MaxValue) })
            {
                try { call(); throw new Exception("FAIL: invalid page admitted"); }
                catch (ArgumentException) { checks++; }
            }
            // Old protected chats must survive a recent page full of newer ordinary conversations.
            Add("protected-old", "protected-old-chat", "Old pinned choice");
            Add("project-old", "project-old-chat", "Old project choice", "another-project");
            for (var index = 0; index < 45; index++)
                Add("new-" + index, "new-chat-" + index, "General " + index);
            string[] pins = ["protected-old-chat"];
            var ordinary = store.ListGeneralConversations(definition, pins, 40);
            Check(ordinary.Conversations.Count == 40 && ordinary.Conversations.All(item => item.ProjectId is null &&
                item.Id != "protected-old-chat"), "general page capacity is reserved for unpinned unassigned chats");
            var protectedPage = store.ListProtectedConversations(definition, pins, 1, snapshotRow: ordinary.SnapshotRow);
            var protectedRows = protectedPage.Conversations.ToList();
            while (protectedPage.NextBeforeRow is { } protectedCursor)
            {
                protectedPage = store.ListProtectedConversations(definition, pins, 1, protectedCursor, ordinary.SnapshotRow);
                protectedRows.AddRange(protectedPage.Conversations);
            }
            Check(protectedRows.Select(item => item.Id).ToHashSet().SetEquals(
                ["chat-a", "protected-old-chat", "project-old-chat"]),
                "protected paging finds old pins and every project chat outside the newest ordinary page");
            var olderOrdinary = store.ListGeneralConversations(definition, pins, 40, ordinary.NextBeforeRow, ordinary.SnapshotRow);
            Check(ordinary.Conversations.Concat(olderOrdinary.Conversations).Select(item => item.Id).Distinct().Count() == 48 &&
                olderOrdinary.NextBeforeRow is null, "older ordinary chats remain accessible without duplication or protected rows");
            Check(store.GetConversation(definition, "protected-old-chat") is not null &&
                store.GetConversation(definition, "project-old-chat") is not null &&
                journal.ReadPage("protected-old-chat").Events.Count != 0,
                "visibility queries do not delete conversations or their journal");
            Check(store.ListProtectedConversations("other:chat", pins).Conversations.Count == 0 &&
                !JsonSerializer.Serialize(protectedRows).Contains("PRIVATE-"),
                "protected summaries retain definition isolation and omit assignments/model context");
            foreach (var call in new Action[] {
                () => store.ListProtectedConversations(definition, pins, 0),
                () => store.ListProtectedConversations(definition, pins, 201),
                () => store.ListProtectedConversations(definition, ["bad id"]),
                () => store.ListGeneralConversations(definition, pins, beforeRow: 0),
                () => store.ListGeneralConversations(definition, pins, snapshotRow: long.MaxValue) })
            {
                try { call(); throw new Exception("FAIL: invalid protected/general page admitted"); }
                catch (ArgumentException) { checks++; }
            }
            Console.WriteLine($"Conversation navigation: {checks} SQLite summary, paging and admission-boundary assertions passed.");
        }
        finally
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cerebral-chat-nav-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid test cleanup root.");
            Directory.Delete(root, true);
        }
    }
}
