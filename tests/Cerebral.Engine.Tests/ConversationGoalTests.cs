using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;

namespace Cerebral.Engine.Tests;

public static class ConversationGoalTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cerebral-GoalTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
        static WorkflowSubmission Input(string id, string text, string chat = "goal-chat") => new(id, chat, "lead",
            ConversationWorkflowContract.DefinitionId, "1.0.0", text, null,
            JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }));
        try
        {
            var path = Path.Combine(root, "engine.db");
            var journal = new EventJournal(path); var store = new WorkflowStore(journal);
            var first = store.Submit(Input("goal-first", "Build a note. Keep café and\nsecond line."));
            store.Steer(new("goal-correction", first.Id, "Keep existing notes."));
            var before = journal.GetRevision("goal-chat");
            var goal = store.ReadConversationGoal(ConversationWorkflowContract.DefinitionId, "goal-chat");
            Check(goal.Original.Text == first.Submission.UserText && goal.Original.RunId == first.Id, "Exact original request retains Unicode and lines.");
            Check(goal.LaterInstructions.Single() is { Text: "Keep existing notes.", Steering: true }, "Steering is an attributed correction.");
            Check(goal.Revision == before && journal.GetRevision("goal-chat") == before, "Goal reads do not append or mutate journal events.");
            Check(goal.Original.EventId == journal.ReadPage("goal-chat").Events.First().Id, "Original exact event handle is retained.");
            using (var writer = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
            {
                writer.Open();
                using var reserved = writer.BeginTransaction(deferred: false);
                Check(store.ReadConversationGoal(ConversationWorkflowContract.DefinitionId, "goal-chat").Original == goal.Original,
                    "Goal display remains read-only while another connection owns the writer reservation.");
            }
            store.Cancel(first.Id);
            var automatic = store.Submit(Input("goal-automatic", "Internal stage directive.") with { Caller = new(EventActorKind.Service, "cerebral.plugin-tests") });
            store.Cancel(automatic.Id);
            for (var i = 0; i < 10; i++) { var turn = store.Submit(Input("goal-update-" + i, "Update " + i)); store.Cancel(turn.Id); }
            var latest = new WorkflowStore(new EventJournal(path)).ReadConversationGoal(ConversationWorkflowContract.DefinitionId, "goal-chat");
            Check(latest.Original == goal.Original, "Reopen preserves original request without a shadow store.");
            Check(latest.HasOlderInstructions && latest.LaterInstructions.Count == 8 &&
                latest.LaterInstructions.First().Text == "Update 2" && latest.LaterInstructions.Last().Text == "Update 9",
                "Latest eight updates retain chronological order and truthful older-history flag.");
            Check(!latest.LaterInstructions.Any(entry => entry.Text == "Internal stage directive."), "Service stages cannot masquerade as user corrections.");
            var other = store.Submit(Input("goal-other", "Other conversation.", "other-chat")); store.Cancel(other.Id);
            Check(store.ReadConversationGoal(ConversationWorkflowContract.DefinitionId, "other-chat").Original.Text == "Other conversation.", "Reads stay in exact conversation.");
            var longText = string.Concat(Enumerable.Repeat("😀", 5000));
            var large = store.Submit(Input("goal-large", longText, "large-chat")); store.Cancel(large.Id);
            var excerpt = store.ReadConversationGoal(ConversationWorkflowContract.DefinitionId, "large-chat").Original;
            Check(excerpt.Truncated && excerpt.Text.Length == 8192 && excerpt.Text.EnumerateRunes().Count() == 4096,
                "Large Unicode requests have a complete-scalar bounded excerpt and explicit truncation.");
            Check(store.Get(large.Id).Submission.UserText == longText, "Excerpt does not truncate stored authoritative request.");
            try { store.ReadConversationGoal("wrong.definition", "goal-chat"); throw new Exception("Cross-definition read accepted."); }
            catch (KeyNotFoundException) { checks++; }
            try { store.ReadConversationGoal(ConversationWorkflowContract.DefinitionId, "missing-chat"); throw new Exception("Unknown conversation accepted."); }
            catch (KeyNotFoundException) { checks++; }
            Console.WriteLine($"Conversation goals: {checks} local SQLite/provenance checks passed; no model or network.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
