using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal static partial class IdleCompactionTests
{
    private static async Task RunPreservationChecksAsync(Action<bool, string> check)
    {
        foreach (var baseline in new[] { false, true })
        {
            using var f = new Fixture();
            await f.SeedAsync(large: false);
            for (var i = 0; i < 40; i++) f.Append("outside-page", "Retained page source " + i, i);
            if (baseline) f.SetBaseline(new("paging-baseline", 1, 5,
                [new("earlier-covered-source", 1)], "Earlier summary.", "paging-baseline", true), 1);
            var next = await f.SubmitAsync("page-next", "Latest accepted user source.");
            var original = f.Host.Store.ReadConversationGoal(ConversationWorkflowModule.DefinitionId, "idle-chat").Original;
            var all = new List<ContextContribution>();
            long? cursor = null;
            var pages = 0;
            do
            {
                var result = await ReadPageAsync(f, next, cursor, 8);
                check(result.State == WorkflowEffectState.Succeeded, "first and older history pages succeed with " + (baseline ? "baseline" : "no baseline"));
                var page = result.Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
                check(JsonSerializer.SerializeToUtf8Bytes(page, CerebralJson.Options).Length <= WorkerWorkflowEffects.MaximumConversationEffectBytes,
                    "each exact-goal history page stays within its result bound");
                if (pages++ == 0)
                    check(page.Context.Contributions.Any(s => s.ExactReadHandle == original.EventId && s.Requirement == ContextRequirement.Required &&
                        s.Complete && s.BeforeCurrentInput &&
                        s.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                            .Any(part => part.Text == Fixture.OriginalGoal) &&
                        Text(new(page.Context.Revision, 0, [s])).Contains("Historical initial conversation criteria/context, from exact accepted source event " + original.EventId, StringComparison.Ordinal) &&
                        Text(new(page.Context.Revision, 0, [s])).Contains("Later owner updates and the current accepted request take precedence.", StringComparison.Ordinal)),
                        "ordinary first page retains complete original outside recent window");
                else check(page.Context.Contributions.All(s => s.ExactReadHandle != original.EventId) && page.Context.Checkpoint is null,
                    "older page repeats neither exact original nor first-page checkpoint");
                all.AddRange(page.Context.Contributions);
                cursor = page.HasOlder ? page.BeforeSequence : null;
                if (pages > 16) throw new Exception("Fixture history cursor did not advance.");
            } while (cursor is not null);
            check(pages > 1 && all.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == all.Count &&
                all.Count(s => s.ExactReadHandle == original.EventId) == 1,
                "real first-page to older-page reads retain one original and unique contribution IDs");

            var first = (await ReadPageAsync(f, next)).Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
            var generated = await GenerateWithHistoryAsync(f, next, first.Context, "Latest steering feedback.");
            var messages = f.Models.LastRequest!.Messages;
            var texts = messages.SelectMany(m => m.Content).OfType<TextContentPart>().Select(p => p.Text).ToArray();
            check(generated.State == WorkflowEffectState.Succeeded && texts.Count(t => t == Fixture.OriginalGoal) == 1 &&
                messages.Where(m => m.Role == "system").SelectMany(m => m.Content).OfType<TextContentPart>()
                    .All(p => !p.Text.Contains(Fixture.OriginalGoal, StringComparison.Ordinal)) &&
                Array.IndexOf(texts, Fixture.OriginalGoal) < Array.IndexOf(texts, next.Submission.UserText) &&
                texts[^1] == "Latest steering feedback.",
                "original remains user reference before current input and latest steering, never system instructions");
        }

        using (var f = new Fixture())
        {
            var original = await CompleteWithoutGenerationAsync(f, "original", new string('g', 30_000));
            f.SetBaseline(new("moderate-goal-baseline", 1, 0, [new("earlier-covered-source", 1)],
                new string('b', 5000), "moderate-goal-baseline", true), 1);
            for (var i = 0; i < 30; i++) f.Append("bounded-page", new string('r', 2200), i);
            var next = await f.SubmitAsync("moderate-original");
            var page = await ReadPageAsync(f, next, limit: 24);
            check(page.State == WorkflowEffectState.Succeeded, "moderate exact original trims reducible page rather than failing history");
            var history = page.Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
            check(history.HasOlder && history.BeforeSequence is not null && Text(history.Context).Contains(original.Submission.UserText, StringComparison.Ordinal),
                "trimming leaves a cursor and full required original");
            var later = await ReadPageAsync(f, next, history.BeforeSequence, 24);
            check(later.State == WorkflowEffectState.Succeeded && later.Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!
                    .Context.Contributions.All(s => !s.Id.StartsWith("idle-goal-", StringComparison.Ordinal)),
                "trimmed reducible sources remain pageable without repeated required anchor");
        }

        using (var f = new Fixture())
        {
            await f.SeedAsync(large: false);
            var goal = f.Host.Store.ReadConversationGoal(ConversationWorkflowModule.DefinitionId, "idle-chat").Original;
            using (var connection = Open(f.Store))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE conversation_events SET payload=json_set(payload,'$.excludedFromContext',json('true')) WHERE event_id=$id";
                command.Parameters.AddWithValue("$id", goal.EventId); command.ExecuteNonQuery();
            }
            var next = await f.SubmitAsync("excluded-goal");
            var history = await f.HistoryAsync(next);
            check(history.Context.Contributions.All(s => s.ExactReadHandle != goal.EventId) &&
                !Text(history.Context).Contains(Fixture.OriginalGoal, StringComparison.Ordinal),
                "explicit original-source exclusion is preserved without replacement or inference");
        }

        using (var f = new Fixture())
        {
            var original = await f.CompleteAsync("original", Fixture.OriginalGoal, "First exact final.");
            f.Append("current-steering", "Retain current-turn steering.", 0);
            var window = f.Bridge.ReadIdleWindow(original, f.Host.Journal.GetRevision("idle-chat"), null, default)!;
            var goal = f.Host.Store.ReadConversationGoal(ConversationWorkflowModule.DefinitionId, "idle-chat").Original;
            check(window.Contributions.Count(s => s.ExactReadHandle == goal.EventId) == 1 &&
                window.Contributions.All(s => s.Requirement == ContextRequirement.Required) &&
                Text(window).Contains("Retain current-turn steering.", StringComparison.Ordinal),
                "first-turn idle window keeps one exact goal and later current-turn sources");
            await f.PrepareAsync();
            check(f.Models.Summaries == 0, "first completed turn without older reducible history dispatches no summarizer");
        }

        using (var f = new Fixture())
        {
            await f.SeedAsync(large: true);
            f.Host = f.NewHost();
            var older = f.Host.Store.Get(f.Original.Id);
            f.Host.Publish(older); f.Host.Publish(f.Completed); f.Host.Publish(older);
            await f.PrepareAsync();
            var next = await f.SubmitAsync("latest-pending");
            check(f.Models.Summaries > 0 && f.Host.Store.GetConversationCheckpoint("idle-chat", next.Submission.ConversationSequence)?.SourceRevision ==
                f.Completed.Submission.ConversationSequence, "latest pending completion replaces old hint and delayed older publication cannot downgrade it");
        }

        using (var f = new Fixture())
        {
            await CompleteWithoutGenerationAsync(f, "original", new string('o', 60_000));
            for (var i = 0; i < 14; i++) f.Append("older", new string('x', 2200), i);
            await f.CompleteAsync("completed", Fixture.LastUser, Fixture.LastAnswer);
            var subscription = f.Events.Subscribe();
            await f.PrepareAsync();
            check(f.Models.Summaries == 0 && Reason(subscription.Reader) == "bounded-history-unavailable",
                "oversized exact original skips optional maintenance before inference with fixed reason");
            f.Events.Unsubscribe(subscription.Id);
            var next = await f.SubmitAsync("oversized-original");
            check(next.Status == WorkflowStatus.Queued && f.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null,
                "oversized original creates no adoptable checkpoint and normal submit remains admitted");
            var history = await ReadPageAsync(f, next);
            check(history.State == WorkflowEffectState.Failed, "unreadable required original fails explicitly without clipping its body");
        }

        using (var f = new Fixture())
        {
            f.Profile.ContextTokens = 32768; f.Profile.ResponseTokens = 8192;
            f.Profile.PluginSettings[IdleCompactionPreference.Key] = CerebralJson.Element(new { schemaVersion = 1, enabled = true, triggerPercent = 50 });
            f.Store.Put("agent", f.Profile.Id, f.Profile);
            f.Store.Put("model", f.Profile.ModelId, new ModelRecord { Id = f.Profile.ModelId, Path = Path.Combine(f.Store.DirectoryPath, "fixture.gguf"),
                ContextTokens = 32768, MaximumContextTokens = 32768, AutomaticContextSizing = false });
            await CompleteWithoutGenerationAsync(f, "original", new string('g', 46_000));
            for (var i = 0; i < 30; i++) f.Append("older-large", new string('x', 2200), i);
            await f.CompleteAsync("completed", Fixture.LastUser, Fixture.LastAnswer);
            f.Models.Summary = new string('s', 3_000);
            var subscription = f.Events.Subscribe();
            await f.PrepareAsync();
            var overflowReason = Reason(subscription.Reader);
            check(f.Models.Summaries > 0 && overflowReason == "history-page-overflow",
                $"new valid reduced checkpoint is checked with required original against future 48 KiB history envelope (summaries={f.Models.Summaries}, reason={overflowReason})");
            f.Events.Unsubscribe(subscription.Id);
            await f.SubmitAsync("summary-overflow");
            check(f.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null && f.Models.Active == 0,
                "oversized combined history page never becomes ready or adopted and provider slot is released");
        }

        foreach (var change in new[] { "completed-run-changed", "latest-run-changed", "preference-disabled", "accepted-settings-changed", "generation-envelope-unavailable" })
        {
            using var f = new Fixture(); await f.SeedAsync(large: true); f.Host = f.NewHost();
            if (change == "generation-envelope-unavailable")
                await CompleteWithoutGenerationAsync(f, "without-generation", "Latest accepted turn.");
            else f.Host.Publish(change == "completed-run-changed" ? f.Completed with { Revision = f.Completed.Revision + 1 } :
                change == "latest-run-changed" ? f.Host.Store.Get(f.Original.Id) : f.Completed);
            var cancellation = f.Host.TryClaimIdleCompaction(default)!;
            if (change == "preference-disabled") f.Profile.PluginSettings[IdleCompactionPreference.Key] = CerebralJson.Element(new { schemaVersion = 1, enabled = false, triggerPercent = 70 });
            if (change == "accepted-settings-changed") f.Profile.Instructions += "Updated fixture guidance.";
            if (change is "preference-disabled" or "accepted-settings-changed") f.Store.Put("agent", f.Profile.Id, f.Profile);
            var subscription = f.Events.Subscribe();
            await f.Host.RunIdleCompactionAsync(cancellation);
            check(Reason(subscription.Reader) == change && f.Models.Summaries == 0, "early skip reports fixed private reason " + change);
            f.Events.Unsubscribe(subscription.Id);
        }

        using (var f = await PluginReviewContinuationTests.Fixture.CreateAsync(acceptedProfile: p => p.ReasoningMode = "off"))
        {
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var stage = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            var checkpoint = new ContextCheckpoint("plugin-baseline", 1, 0, [new("prior-plugin-source", 1)], "Prior plugin summary.", "plugin-baseline", true);
            using (var connection = Open(f.Store))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO conversation_context_checkpoints(conversation_id,source_revision,run_id,effect_id,checkpoint,adopted_at) VALUES($conversation,1,'prior','plugin-baseline',$checkpoint,$at)";
                command.Parameters.AddWithValue("$conversation", f.ConversationId);
                command.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
            }
            var models = new MeasuredProvider();
            var bridge = new WorkerWorkflowEffects(f.Store, f.Configuration, models, f.Tools);
            var result = await bridge.Commands.Single(c => c.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand).ExecuteAsync(
                new("plugin-history", stage.Submission, WorkflowContract.EmptyObject) { RunId = stage.Id }, default);
            check(result.State == WorkflowEffectState.Succeeded, "automatic plugin stage can read retained checkpoint history");
            var history = result.Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
            check(history.Context.Checkpoint is not null && history.Context.Contributions.All(s => !s.Id.StartsWith("idle-goal-", StringComparison.Ordinal)),
                "plugin continuation with baseline uses its dedicated original binding without ordinary goal duplication");
            var generated = await bridge.Commands.Single(c => c.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && c.Descriptor.Version == 1).ExecuteAsync(
                new("plugin-generation", stage.Submission, CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [], Context: history.Context))) { RunId = stage.Id }, default);
            check(generated.State == WorkflowEffectState.Succeeded && models.LastRequest!.Messages.SelectMany(m => m.Content).OfType<TextContentPart>()
                .Count(p => p.Text == PluginReviewContinuationTests.Fixture.UserText) == 1,
                "plugin original appears exactly once through dedicated continuation binding");
        }
    }

    private static SqliteConnection Open(StateStore store)
    {
        var connection = new SqliteConnection("Data Source=" + Path.Combine(store.DirectoryPath, "engine.db"));
        connection.Open(); return connection;
    }

    private static async Task<WorkflowRun> CompleteWithoutGenerationAsync(Fixture f, string id, string text)
    {
        var run = await f.SubmitAsync(id, text);
        run = f.Host.Store.TryClaim(run.Id)!;
        return f.Host.Publish(f.Host.Store.ApplyStep(run.Id, run.Revision, new(WorkflowContract.EmptyObject,
            new CompleteWorkflow(CerebralJson.Element(new { text = "Fixture final without generation." })))));
    }

    private static Task<WorkflowCommandResult> ReadPageAsync(Fixture f, WorkflowRun run, long? before = null, int? limit = null) =>
        f.Bridge.Commands.Single(c => c.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand).ExecuteAsync(
            new("page", run.Submission, CerebralJson.Element(new WorkerWorkflowEffects.HistoryArguments(before, limit))) { RunId = run.Id }, default);

    private static Task<WorkflowCommandResult> GenerateWithHistoryAsync(Fixture f, WorkflowRun run, ContextWindow window, string steering) =>
        f.Bridge.Commands.Single(c => c.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && c.Descriptor.Version == 1).ExecuteAsync(
            new("history-generation", run.Submission, CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments(
                [new("user", [new TextContentPart(steering)])], Context: window))) { RunId = run.Id }, default);

    private static string Reason(System.Threading.Channels.ChannelReader<ServiceEvent> reader)
    {
        while (reader.TryRead(out var item))
            if (item.Type == "context.idle-compaction") return item.Data.GetProperty("reason").GetString()!;
        throw new Exception("No bounded maintenance diagnostic was published.");
    }
}
