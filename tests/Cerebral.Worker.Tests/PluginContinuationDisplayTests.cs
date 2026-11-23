using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Fixture = Cerebral.Worker.Tests.PluginReviewContinuationTests.Fixture;

namespace Cerebral.Worker.Tests;

/// <summary>Real bounded dispatcher/client reads over isolated legacy SQLite fixtures; no model, scheduler or plugin execution.</summary>
internal static class PluginContinuationDisplayTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Plugin stage display: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }
        using var f = await Fixture.CreateAsync();
        await using var runtime = new ModelRuntime(f.Store, f.Configuration.Get, f.Events);
        await using var tools = new ToolDispatcher(f.Configuration.Get);
        using var scheduler = new JobScheduler(f.Store, f.Configuration, new NeverExecutor(), runtime, f.Events,
            NullLogger<JobScheduler>.Instance, f.Workflows);
        var commands = new CommandDispatcher(f.Store, f.Configuration, runtime, scheduler, tools, f.Events,
            workflows: f.Workflows);
        var review1 = f.Present(f.Origin.Id);
        var old1 = await AdmitLegacy(f, review1);
        var oldMessage1 = SeedLegacyProjection(f, old1);
        var actualResponse = Append(f, old1.Id, new(EventActorKind.Agent, f.Profile.Id, f.Profile.Name),
            Fixture.UserText);
        var humanRepeat = Append(f, old1.Id, new(EventActorKind.User, "owner", "You"), Fixture.UserText);
        old1 = f.Finish(old1.Id);
        var review2 = f.Present(old1.Id);
        var old2 = await AdmitLegacy(f, review2);
        var oldMessage2 = SeedLegacyProjection(f, old2);
        var unbound = Append(f, "plugin-tests-" + Fixture.Id(), new(EventActorKind.Service, "cerebral.plugin-tests"),
            Fixture.UserText);
        var rawRevision = f.Journal.GetRevision(f.ConversationId);
        var originalEvents = f.Journal.ReadPage(f.ConversationId, 0, 100, rawRevision).Events;
        var legacyIds = new HashSet<string>([oldMessage1.Id, oldMessage2.Id], StringComparer.Ordinal);
        async Task<T> Page<T>(string operation, object arguments)
        {
            var result = await commands.ExecuteAsync(new(operation, CerebralJson.Element(arguments)));
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return result.Data.Deserialize<T>(CerebralJson.Options)!;
        }
        bool ExactProjection(IReadOnlyList<CerebralEvent> original, IReadOnlyList<CerebralEvent> shown) =>
            original.Count == shown.Count && original.Zip(shown).All(pair =>
                JsonElement.DeepEquals(CerebralJson.Element(pair.First),
                    CerebralJson.Element(legacyIds.Contains(pair.First.Id)
                        ? pair.Second with { Projection = pair.Second.Projection with { Human = HumanVisibility.Transcript } }
                        : pair.Second)) &&
                (!legacyIds.Contains(pair.First.Id) || pair.Second.Projection.Human == HumanVisibility.Activity));
        long after = 0;
        var allForward = new List<CerebralEvent>();
        do
        {
            var raw = f.Journal.ReadPage(f.ConversationId, after, 3, rawRevision);
            var shown = await Page<JournalPage>("jobs.workflow.events",
                new { id = old2.Id, afterSequence = after, limit = 3, snapshotSequence = rawRevision });
            Check(shown.SnapshotSequence == raw.SnapshotSequence && shown.NextAfterSequence == raw.NextAfterSequence &&
                shown.RetentionGap == raw.RetentionGap && ExactProjection(raw.Events, shown.Events),
                "forward UI page retains exact identities/content/snapshot/cursor and projects only proven legacy admissions");
            allForward.AddRange(shown.Events);
            if (shown.NextAfterSequence is not { } next) break;
            after = next;
        } while (true);
        long? before = null;
        var allPrevious = new List<CerebralEvent>();
        do
        {
            var raw = f.Journal.ReadPreviousPage(f.ConversationId, before, 3, rawRevision);
            var shown = await Page<JournalPreviousPage>("jobs.workflow.events.previous",
                before is null ? new { id = old2.Id, limit = 3, snapshotSequence = rawRevision } :
                    (object)new { id = old2.Id, beforeSequence = before.Value, limit = 3, snapshotSequence = rawRevision });
            Check(shown.SnapshotSequence == raw.SnapshotSequence && shown.NextBeforeSequence == raw.NextBeforeSequence &&
                shown.RetentionGap == raw.RetentionGap && ExactProjection(raw.Events, shown.Events),
                "previous UI page retains exact identities/content/snapshot/cursor and projects only proven legacy admissions");
            allPrevious.InsertRange(0, shown.Events);
            before = shown.NextBeforeSequence;
        } while (before is not null);
        Check(allForward.Select(item => item.Id).SequenceEqual(originalEvents.Select(item => item.Id)) &&
            allPrevious.Select(item => item.Id).SequenceEqual(originalEvents.Select(item => item.Id)) &&
            new[] { actualResponse, humanRepeat, unbound }.All(original =>
                allForward.Single(item => item.Id == original.Id).Projection.Human == HumanVisibility.Transcript),
            "all bounded pages preserve actual responses, human repeats and unbound service-looking cards visibly unchanged");
        var client = new WorkflowConversationClient(f.ConversationId, commands.ExecuteAsync);
        await client.OpenAsync(old1.Id);
        await client.RefreshConversationAsync(() => true);
        Check(client.Snapshot.RunId == old2.Id && client.Snapshot.Run?.Status == WorkflowStatus.Queued &&
            client.Snapshot.OriginalMessage is { Id: var retainedId, Projection.Human: HumanVisibility.Activity } &&
            retainedId == oldMessage1.Id &&
            client.Snapshot.Events.Single(item => item.Id == oldMessage2.Id).Projection.Human == HumanVisibility.Activity &&
            client.Snapshot.Events.Single(item => item.Id == humanRepeat.Id).Projection.Human == HumanVisibility.Transcript,
            "fresh legacy QA1 selection follows legacy QA2 without rejecting activity admission or erasing a human repeat");
        var projected = PluginContinuationGoal.ForDisplay(f.Store, f.Journal, old2.Submission,
            oldMessage2, rawRevision, default);
        Check(projected.Projection.Human == HumanVisibility.Activity &&
            JsonElement.DeepEquals(CerebralJson.Element(oldMessage2),
                CerebralJson.Element(projected with { Projection = oldMessage2.Projection })) &&
            f.Journal.ReadExact(f.ConversationId, oldMessage2.Id, rawRevision).Projection.Human == HumanVisibility.Transcript &&
            f.Journal.GetRevision(f.ConversationId) == rawRevision,
            "display normalization leaves the stored legacy event and stream revision unchanged");
        foreach (var forged in new[]
        {
            oldMessage2 with { Id = Fixture.Id() },
            oldMessage2 with { Content = new ContentPart[] { new TextContentPart("Forged content.") } },
            oldMessage2 with { Actor = oldMessage2.Actor with { Id = "unrelated-service" } },
            oldMessage2 with { Sequence = oldMessage2.Sequence + 1 },
            oldMessage2 with { ConversationId = "foreign-conversation" }
        })
            Check(ReferenceEquals(PluginContinuationGoal.ForDisplay(f.Store, f.Journal, old2.Submission,
                forged, rawRevision, default), forged),
                "unproven display identity/actor/sequence/content stays visible without normalization");
        var changedReview = f.Read(review2.Id);
        changedReview.PluginTests = changedReview.PluginTests! with { OriginRunId = "missing-origin" };
        f.Store.Put("interface", changedReview.Id, changedReview);
        var unprovenPage = await Page<JournalPage>("jobs.workflow.events", new
            { id = old2.Id, afterSequence = old2.Submission.ConversationSequence, limit = 1, snapshotSequence = rawRevision });
        Check(unprovenPage.Events.Single().Projection.Human == HumanVisibility.Transcript,
            "unproven optional owner-review lineage leaves the canonical admission card visible");
        changedReview.PluginTests = changedReview.PluginTests with { OriginRunId = f.Origin.Id };
        f.Store.Put("interface", changedReview.Id, changedReview);
        var completed2 = f.Finish(old2.Id);
        var review3 = f.Present(completed2.Id);
        await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
        var new3 = f.Workflows.Store.Get(f.Read(review3.Id).PluginTests!.RunId!);
        var newMessage = f.Journal.ReadPage(f.ConversationId, new3.Submission.ConversationSequence, 1).Events.Single();
        var newPage = await Page<JournalPage>("jobs.workflow.events",
            new { id = new3.Id, afterSequence = new3.Submission.ConversationSequence, limit = 1 });
        Check(newMessage.Projection.Human == HumanVisibility.Activity &&
            JsonElement.DeepEquals(CerebralJson.Element(newMessage), CerebralJson.Element(newPage.Events.Single())),
            "new brief activity admission passes through the display page without alteration");
        var bridge = new WorkerWorkflowEffects(f.Store, f.Configuration, new UnusedModels(), f.Tools);
        var history = bridge.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand);
        var exact = await history.ExecuteAsync(new WorkflowCommandContext("display-exact-history", new3.Submission,
            CerebralJson.Element(new { eventId = oldMessage2.Id })) { RunId = new3.Id }, default);
        var source = JsonSerializer.Deserialize<CerebralEvent>(exact.Data!.Value.GetProperty("json").GetString()!, CerebralJson.Options)!;
        Check(exact.State == WorkflowEffectState.Succeeded && source.Projection.Human == HumanVisibility.Transcript &&
            JsonElement.DeepEquals(CerebralJson.Element(source), CerebralJson.Element(oldMessage2)),
            "model exact-history reads retain the original legacy event projection/content after UI normalization");
        using (var repairFixture = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            var failedReview = repairFixture.Present(repairFixture.Origin.Id, success: false, repairAllowed: true,
                failureStage: InterfaceFailureStage.NativeLoad);
            var repair = await AdmitLegacy(repairFixture, failedReview);
            var legacyRepair = SeedLegacyProjection(repairFixture, repair);
            var displayedRepair = PluginContinuationGoal.ForDisplay(repairFixture.Store, repairFixture.Journal,
                repair.Submission, legacyRepair, repairFixture.Journal.GetRevision(repairFixture.ConversationId), default);
            Check(displayedRepair.Projection.Human == HumanVisibility.Activity &&
                repairFixture.Journal.ReadExact(repairFixture.ConversationId, legacyRepair.Id,
                    repairFixture.Journal.GetRevision(repairFixture.ConversationId)).Projection.Human == HumanVisibility.Transcript,
                "a proven failed-review legacy repair card projects as activity while its canonical evidence stays intact");
        }
        Console.WriteLine("Plugin stage display: " + checks + " SQLite/dispatcher/client checks passed; no model or plugin execution.");
        return checks;
    }

    private static async Task<WorkflowRun> AdmitLegacy(Fixture fixture, InterfaceRequest presentation)
    {
        var tests = presentation.PluginTests!;
        var assignment = Fixture.Assignment + "\n\n" + (tests.Mode == InterfacePluginTestModes.RepairBeforeInstall
            ? WorkerWorkflows.BuildPluginRepairAssignment(presentation, tests)
            : WorkerWorkflows.BuildPluginTestAssignment(presentation,
            presentation.Plugin!, tests, false, new("unavailable", null, [], [], true, "Fixture metadata only.")));
        var run = fixture.Workflows.Store.Submit(fixture.Origin.Submission with
        {
            RequestId = tests.RequestId, ConversationSequence = 0, AssignmentText = assignment,
            Input = tests.Mode == InterfacePluginTestModes.RepairBeforeInstall
                ? WorkerWorkflows.PreparePluginRepairInput(fixture.Origin.Submission.Input)
                : WorkerWorkflows.PreparePluginTestInput(fixture.Origin.Submission.Input),
            Caller = new(EventActorKind.Service, "cerebral.plugin-tests", "Cerebral plugin tests")
        });
        await fixture.Presentations.ProcessPluginTestsAsync(fixture.Workflows, default);
        return run;
    }

    private static CerebralEvent SeedLegacyProjection(Fixture fixture, WorkflowRun run)
    {
        var item = fixture.Journal.ReadPage(fixture.ConversationId, run.Submission.ConversationSequence, 1).Events.Single();
        var legacy = item with { Projection = item.Projection with { Human = HumanVisibility.Transcript } };
        // Seed an old persisted projection only in the task-owned, isolated fixture database.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(fixture.Store.DirectoryPath, "engine.db") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversation_events SET payload=$payload WHERE event_id=$id;";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(legacy, CerebralJson.Options));
        command.Parameters.AddWithValue("$id", legacy.Id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Fixture legacy admission is missing.");
        return legacy;
    }

    private static CerebralEvent Append(Fixture fixture, string runId, EventActor actor, string text) =>
        fixture.Journal.Append(new(Fixture.Id(), fixture.ConversationId, runId, 0, CerebralEventTypes.Message,
            actor, [new TextContentPart(text)], new(ModelInclusion.Required, HumanVisibility.Transcript),
            new(EventSourceKind.Service), DateTimeOffset.UtcNow), fixture.Journal.GetRevision(fixture.ConversationId)).Event;

    private sealed class NeverExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("The display fixture cannot execute an agent.");
    }

    private sealed class UnusedModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("unused-display-fixture", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("The display fixture cannot prepare a model.");
    }
}
