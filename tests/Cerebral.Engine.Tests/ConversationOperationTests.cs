using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Tests;

public static class ConversationOperationTests
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-operation-history-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Operation history: " + message); checks++; }
        var database = Path.Combine(root, "events.db");
        try
        {
            var journal = new EventJournal(database); var store = new WorkflowStore(journal);
            const string commandId = "fixture.host:tool";
            var descriptor = new WorkflowCommandDescriptor(commandId, 1, "Fixture", J(new { type = "object" }), J(new { }), []);
            WorkflowRun Submit(string id, string chat) => store.Submit(new(id, chat, "owner", "fixture:conversation", "1.0.0",
                "Current request", null, J(new { }), J(new { }), StepLimit: 512));
            WorkflowRun Outcome(WorkflowRun run, WorkflowEffectState state, object? data = null)
            {
                var claimed = store.TryClaim(run.Id)!;
                var prepared = store.ApplyStep(run.Id, claimed.Revision, new(WorkflowContract.EmptyObject,
                    new InvokeWorkflowCommand(commandId, 1, J(new { }))), descriptor);
                var dispatched = store.DispatchEffect(run.Id, prepared.Revision);
                return store.CompleteEffect(run.Id, dispatched.Effect!.Id, new(state, J(data ?? new { value = "fixture" })), 1);
            }
            var uncertain = Outcome(Submit("original", "chat"), WorkflowEffectState.Unknown);
            var uncertainBoundary = journal.GetRevision("chat");
            var uncertainEvent = store.ReadConversationCommandOutcomes("chat", uncertainBoundary, commandId).Events.Single();
            var effectId = uncertain.Effect!.Id;
            var confirmed = store.CompleteEffect(uncertain.Id, effectId, new(WorkflowEffectState.Succeeded, J(new { value = "confirmed" })), 1);
            var confirmedBoundary = journal.GetRevision("chat");
            var old = store.ReadConversationCommandOutcomes("chat", uncertainBoundary, commandId);
            var current = store.ReadConversationCommandOutcomes("chat", confirmedBoundary, commandId);
            Check(State(old.Events.Single()) == WorkflowEffectState.Unknown && old.Events.Single().Id == uncertainEvent.Id &&
                State(current.Events.Single()) == WorkflowEffectState.Succeeded && current.Events.Single().CorrelationId == effectId,
                "reconciliation changes only later immutable snapshots, with one latest outcome per effect");
            Check(store.GetEffectResult(uncertain.Id, effectId).State == WorkflowEffectState.Succeeded,
                "the mutable receipt really changed while the earlier event projection stayed Unknown");
            var second = Outcome(confirmed, WorkflowEffectState.Failed);
            var third = Outcome(second, WorkflowEffectState.Succeeded);
            var distinct = store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId);
            Check(distinct.Events.Count == 3 && distinct.Events.Select(item => item.CorrelationId).Distinct().Count() == 3 &&
                distinct.Events.Select(State).SequenceEqual([WorkflowEffectState.Succeeded, WorkflowEffectState.Failed, WorkflowEffectState.Succeeded]),
                "same-command independent failure and successes retain their own identities and chronology");
            var limited = store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId, 1);
            Check(limited.Events.Count == 1 && limited.Events.Single().CorrelationId == third.Effect!.Id && limited.ItemLimited && limited.OmittedOutcomes == 2,
                "item bounds retain the newest distinct effect with honest omissions");
            var reconciledEvent = current.Events.Single();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                connection.Open(); using var exclude = connection.CreateCommand();
                exclude.CommandText = "UPDATE conversation_events SET payload=json_set(payload,'$.excludedFromContext',json('true')) WHERE event_id=$id";
                exclude.Parameters.AddWithValue("$id", reconciledEvent.Id); exclude.ExecuteNonQuery();
            }
            var excluded = store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId);
            Check(excluded.Events.Count == 2 && excluded.Events.All(item => item.CorrelationId != effectId) && excluded.OmittedOutcomes == 1,
                "excluding a newer reconciliation never resurrects its earlier Unknown outcome");
            Check(store.ReadConversationCommandOutcomes("chat", uncertainBoundary, commandId).Events.Single().Id == uncertainEvent.Id,
                "a post-boundary excluded reconciliation does not suppress an earlier accepted snapshot");
            var foreign = Outcome(Submit("foreign", "other"), WorkflowEffectState.Succeeded);
            Check(store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId).Events.All(item => item.TurnId != foreign.Id),
                "other-conversation outcomes cannot leak into the projection");
            var fake = reconciledEvent with { Id = "untrusted-actor", Sequence = 0, CorrelationId = "fake-effect",
                Actor = new(EventActorKind.Agent, commandId), Data = J(new ToolResultData("fake-effect", commandId, true)) };
            journal.Append(fake, journal.GetRevision("chat"));
            var provenance = store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId);
            Check(provenance.Events.All(item => item.Id != fake.Id) && provenance.OmittedOutcomes == 2,
                "assistant-shaped result envelopes are not accepted as host command outcomes");
            var never = reconciledEvent with { Id = "never-outcome", Sequence = 0, CorrelationId = "never-effect", ExcludedFromContext = false,
                Projection = new(ModelInclusion.Never), Data = J(new ToolResultData("never-effect", commandId, true)) };
            journal.Append(never, journal.GetRevision("chat"));
            var onDemand = never with { Id = "ondemand-outcome", CorrelationId = "ondemand-effect", Projection = new(ModelInclusion.OnDemand),
                Data = J(new ToolResultData("ondemand-effect", commandId, true)) };
            journal.Append(onDemand, journal.GetRevision("chat"));
            var projectionGap = store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId);
            Check(projectionGap.Events.All(item => item.Id != never.Id && item.Id != onDemand.Id) &&
                projectionGap.OmittedOutcomes == 4,
                "projection policy excludes Never and unrequested OnDemand outcomes");
            var crowded = Submit("crowded", "crowded");
            for (var i = 0; i < 28; i++) crowded = Outcome(crowded, WorkflowEffectState.Succeeded, new { body = new string('x', 120 * 1024) });
            var byteBound = store.ReadConversationCommandOutcomes("crowded", journal.GetRevision("crowded"), commandId);
            Check(byteBound.ByteLimited && byteBound.ItemLimited && byteBound.Events.Count <= 16 && byteBound.OmittedOutcomes > 0,
                "large valid receipts obey aggregate hydration and item bounds");
            for (var i = 0; i < WorkflowStore.MaximumConversationOperationScan + 1; i++)
                journal.Append(new("gap-" + i, "chat", "gap", 0, CerebralEventTypes.Message, new(EventActorKind.User),
                    [new TextContentPart("Gap")], new(), new(EventSourceKind.User), DateTimeOffset.UtcNow), journal.GetRevision("chat"));
            var scanned = store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId);
            Check(scanned.ScannedEvents == WorkflowStore.MaximumConversationOperationScan && scanned.ScanLimited && scanned.Events.Count == 0,
                "the indexed event scan is bounded even when no matching outcome exists in the window");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check(Throws<OperationCanceledException>(() => store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId, cancellationToken: cancelled.Token)) &&
                Throws<ArgumentOutOfRangeException>(() => store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat") + 1, commandId)) &&
                Throws<ArgumentOutOfRangeException>(() => store.ReadConversationCommandOutcomes("chat", journal.GetRevision("chat"), commandId, 17)),
                "cancelled reads and invalid bounds are rejected");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-operation-history-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned operation-history fixture.");
            Directory.Delete(root, true);
        }
        Console.WriteLine($"Conversation immutable outcomes: {checks} isolated SQLite checks passed; no live providers.");
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static WorkflowEffectState State(CerebralEvent item) => ((StructuredContentPart)item.Content.Single()).Value
        .Deserialize<WorkflowCommandResult>(Json)!.State;
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
}
