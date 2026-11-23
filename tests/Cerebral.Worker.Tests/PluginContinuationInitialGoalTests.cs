using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Resources;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Fixture = Cerebral.Worker.Tests.PluginReviewContinuationTests.Fixture;

namespace Cerebral.Worker.Tests;

/// <summary>Real isolated SQLite/admission/history plus deterministic prepared-context fixtures.
/// No provider inference, executable plugin, native controls, or runtime deployment is exercised.</summary>
internal static class PluginContinuationInitialGoalTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Initial plugin goal context: " + name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        const string currentText = "Continue the previous plugin. Updated owner scope: the latest criteria replace conflicting initial criteria; test the reviewed generation only.";
        var initialText = "Initial detailed criteria: retain café and every requested value.\r\n" +
            new string('α', 7000) + "\r\nExact initial criteria beyond the display preview.";
        foreach (var baseline in new[] { false, true })
        {
            using var f = await Fixture.CreateAsync(initialUserText: initialText,
                acceptedProfile: profile => { profile.ReasoningMode = "off"; Fixture.AllowRepairTools(profile); });
            var initial = InitialSource(f);
            var service = Append(f, f.Origin.Id, CerebralEventTypes.Message,
                new(EventActorKind.Service, "cerebral.plugin-tests"), "Service-stage prose is not the initial owner goal.");
            CerebralEvent? cutoff = null;
            for (var i = 0; i < 224; i++)
            {
                var source = Append(f, f.Origin.Id, CerebralEventTypes.ToolResult,
                    new(EventActorKind.Tool, "fixture.read"), "Historical observed result " + i + ": " + new string('r', 2100));
                if (i == 111) cutoff = source;
            }
            if (baseline) SetBaseline(f, cutoff!, initial);
            f.Profile.Instructions = "Latest accepted owner profile instructions.";
            f.Profile.Capabilities.Remove("filesystem.write");
            f.Store.Put("agent", f.Profile.Id, f.Profile);
            var owner = f.Finish((await f.SubmitOriginAsync(currentText)).Id);
            var ordinaryModels = new Models();
            var ordinaryBridge = Bridge(f, ordinaryModels);
            var ordinaryPage = Data(await Read(ordinaryBridge, owner, new { limit = 48 }));
            var ordinaryAnchor = ordinaryPage.Context.Contributions.Single(item => item.ExactReadHandle == initial.Id);
            Check(ordinaryAnchor.Complete && ordinaryAnchor.BeforeCurrentInput &&
                  ordinaryAnchor.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                      .Any(part => part.Text == initialText) &&
                  Text(ordinaryAnchor.Messages).Contains(
                      "Historical initial conversation criteria/context, from exact accepted source event " + initial.Id,
                      StringComparison.Ordinal) &&
                  Text(ordinaryAnchor.Messages).Contains(
                      "Later owner updates and the current accepted request take precedence.", StringComparison.Ordinal),
                "ordinary continuation preserves exact initial text with the same historical provenance and precedence as QA (" + baseline + ")");
            Check((await Generate(ordinaryBridge, owner, ordinaryPage.Context)).State == WorkflowEffectState.Succeeded,
                "ordinary continuation admits the bounded history projection (" + baseline + ")");
            var ordinaryContents = ordinaryModels.Last!.Messages.SelectMany(message => message.Content)
                .OfType<TextContentPart>().Select(part => part.Text).ToArray();
            Check(ordinaryContents.Count(value => value == initialText) == 1 &&
                  ordinaryContents.Count(value => value == currentText) == 1 &&
                  Array.IndexOf(ordinaryContents, initialText) < Array.IndexOf(ordinaryContents, currentText) &&
                  !Text(ordinaryModels.Last.Messages).Contains("Host-owned continuation phase:", StringComparison.Ordinal),
                "ordinary history remains a reference before the latest request without inventing a plugin QA phase (" + baseline + ")");
            var presentation = f.Present(owner.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var stage = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            var models = new Models();
            var bridge = Bridge(f, models);
            Check(f.Journal.ReadPreviousPage(f.ConversationId, null, 200,
                      stage.Submission.ConversationSequence).Events.All(item => item.Id != initial.Id),
                "initial criteria are genuinely outside the last 200 events (" + baseline + ")");
            Check(PluginContinuationGoal.Read(f.Store, f.Journal, stage.Submission)!.Original.RequestId == owner.Id &&
                  JsonElement.DeepEquals(stage.Submission.SettingsSnapshot, owner.Submission.SettingsSnapshot) &&
                  !JsonElement.DeepEquals(stage.Submission.SettingsSnapshot, f.Origin.Submission.SettingsSnapshot) &&
                  JsonElement.DeepEquals(CerebralJson.Element(stage.Submission.AcceptedContent),
                      CerebralJson.Element(owner.Submission.AcceptedContent)),
                "review lineage and current settings/content remain the later owner admission (" + baseline + ")");
            var first = await Read(bridge, stage, new { limit = 48 });
            Check(first.State == WorkflowEffectState.Succeeded, "automatic first history read succeeds (" + baseline + ")");
            var page = Data(first);
            var anchor = page.Context.Contributions.Single(item => item.ExactReadHandle == initial.Id);
            Check(anchor.Requirement == ContextRequirement.Required && anchor.Complete && anchor.BeforeCurrentInput &&
                  anchor.SourceRevision == initial.Sequence &&
                  anchor.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                      .Any(part => part.Text == initialText),
                "full exact initial criteria survive as a required anchor beyond the 4096-character preview (" + baseline + ")");
            Check(Text(anchor.Messages).Contains("Later owner updates and the current accepted request take precedence.", StringComparison.Ordinal) &&
                  page.Context.Contributions.Where(item => item.Requirement == ContextRequirement.Required)
                      .All(item => item.ExactReadHandle != service.Id) &&
                  (page.Context.Checkpoint is not null) == baseline,
                "initial context is framed as historical and independent of service prose/checkpoint (" + baseline + ")");
            // The producer's byte bound measures its compact JsonElement wire representation;
            // the retained element can carry formatting whitespace from the store serializer.
            var pageBytes = JsonSerializer.SerializeToUtf8Bytes(first.Data!.Value).Length;
            Check(pageBytes <= WorkerWorkflowEffects.MaximumConversationEffectBytes &&
                  page.HasOlder && page.BeforeSequence is not null &&
                  page.Context.Contributions.Count(item => item.Requirement == ContextRequirement.Reducible) < 48,
                "required exact criteria remain complete while reducible history is trimmed with a cursor (" + baseline + "); bytes=" +
                    pageBytes + "; hasOlder=" + page.HasOlder +
                    "; cursor=" + page.BeforeSequence + "; reducible=" + page.Context.Contributions.Count(item => item.Requirement == ContextRequirement.Reducible));
            var generated = await Generate(bridge, stage, page.Context);
            var prepared = models.Last!;
            var contents = prepared.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Select(part => part.Text).ToArray();
            Check(generated.State == WorkflowEffectState.Succeeded && contents.Count(value => value == initialText) == 1 &&
                  contents.Count(value => value == currentText) == 1 &&
                  Array.IndexOf(contents, initialText) < Array.IndexOf(contents, currentText) &&
                  contents[^1] == "Latest actual stage feedback." &&
                  prepared.Messages.Where(message => message.Role == "system")
                      .All(message => !Text([message]).Contains(initialText, StringComparison.Ordinal)),
                "prepared generation keeps initial criteria before the authoritative current owner/stage and out of system policy (" + baseline + ")");
            Check(Text(prepared.Messages).Split(Fixture.Assignment, StringSplitOptions.None).Length - 1 == 1 &&
                  Text(prepared.Messages).Contains("Host-owned continuation phase: installed-plugin QA.", StringComparison.Ordinal),
                "historical initial private assignment is not borrowed and the current QA phase remains exact (" + baseline + ")");
            var all = page.Context.Contributions.ToList();
            var cursor = page.BeforeSequence;
            var pages = 1;
            while (cursor is not null)
            {
                var older = await Read(bridge, stage, new { limit = 48, beforeSequence = cursor });
                Check(older.State == WorkflowEffectState.Succeeded, "older bounded automatic history page succeeds (" + baseline + ")");
                var next = Data(older);
                Check(next.Context.Contributions.All(item => item.ExactReadHandle != initial.Id) &&
                      next.Context.Checkpoint is null &&
                      (next.BeforeSequence is null || next.BeforeSequence < cursor),
                    "older page does not duplicate the exact initial goal/checkpoint and advances (" + baseline + ")");
                all.AddRange(next.Context.Contributions);
                cursor = next.BeforeSequence;
                if (++pages > 16) throw new InvalidOperationException("Initial goal fixture history did not advance.");
            }
            Check(all.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == all.Count &&
                  all.Count(item => item.ExactReadHandle == initial.Id) == 1,
                "real pagination retains one initial exact source with unique contribution IDs (" + baseline + ")");
            var future = Append(f, stage.Id, CerebralEventTypes.Message,
                new(EventActorKind.User, "owner"), "A future owner change must not enter this accepted stage.");
            var bounded = Data(await Read(bridge, stage, new { limit = 48 }));
            Check(bounded.Context.Contributions.All(item => item.ExactReadHandle != future.Id) &&
                  bounded.SnapshotSequence == stage.Submission.ConversationSequence,
                "later human events stay outside the immutable accepted conversation boundary (" + baseline + ")");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var stage = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            var initial = InitialSource(f);
            var models = new Models();
            var bridge = Bridge(f, models);
            var page = Data(await Read(bridge, stage, new { limit = 48 }));
            Check(page.Context.Contributions.All(item => !item.Id.StartsWith("idle-goal-", StringComparison.Ordinal)) &&
                  page.Context.Contributions.Single(item => item.ExactReadHandle == initial.Id).Requirement == ContextRequirement.Reducible,
                "initial source equal to review origin retains its existing reference without a duplicate required anchor");
            var generated = await Generate(bridge, stage, page.Context);
            Check(generated.State == WorkflowEffectState.Succeeded &&
                  models.Last!.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                      .Count(part => part.Text == Fixture.UserText) == 1,
                "same-origin prepared generation still contains one exact accepted owner goal");
        }

        using (var f = await Fixture.CreateAsync(initialUserText: "Initial image criterion.",
            initialAcceptedContent: async fixture =>
            {
                var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/RxQAAAAASUVORK5CYII=");
                using var stream = new MemoryStream(png, writable: false);
                var reference = await fixture.Attachments.ImportAsync(
                    new AttachmentAccessContext(fixture.Profile.Id, fixture.ConversationId), Fixture.Id(), "initial.png", stream);
                return [new ImageContentPart(reference, "Exact original reference.")];
            }))
        {
            var owner = f.Finish((await f.SubmitOriginAsync(currentText)).Id);
            var presentation = f.Present(owner.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var stage = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            var models = new Models();
            var bridge = Bridge(f, models);
            var page = Data(await Read(bridge, stage, new { limit = 48 }));
            var original = InitialSource(f);
            var anchor = page.Context.Contributions.Single(item => item.ExactReadHandle == original.Id);
            var exactImage = original.Content.OfType<ImageContentPart>().Single();
            Check(JsonElement.DeepEquals(CerebralJson.Element(anchor.Messages.SelectMany(message => message.Content)
                      .OfType<ImageContentPart>().Single()), CerebralJson.Element(exactImage)) &&
                  stage.Submission.AcceptedContent.All(part => part is not ImageContentPart) &&
                  JsonElement.DeepEquals(CerebralJson.Element(stage.Submission.AcceptedContent),
                      CerebralJson.Element(owner.Submission.AcceptedContent)),
                "historical typed attachment coordinates stay exact without changing current accepted references");
            Check((await Generate(bridge, stage, page.Context)).State == WorkflowEffectState.Succeeded &&
                  models.Last!.Messages.SelectMany(message => message.Content).OfType<ImageContentPart>().Count() == 1,
                "a capable deterministic adapter receives the existing typed historical reference once");
            var textOnly = new Models(image: false);
            var unsupported = await Generate(Bridge(f, textOnly), stage, page.Context);
            Check(unsupported.State == WorkflowEffectState.Failed && textOnly.Generated == 0 && textOnly.Last is null,
                "historical typed content cannot bypass current adapter capability admission");
            Exclude(f, original.Id);
            var excluded = Data(await Read(bridge, stage, new { limit = 48 }));
            Check(excluded.Context.Contributions.All(item => item.ExactReadHandle != original.Id) &&
                  !Text(excluded.Context.Contributions.SelectMany(item => item.Messages))
                      .Contains("Initial image criterion.", StringComparison.Ordinal),
                "explicit initial-source exclusion is honored without replacement or inference");
        }

        using (var f = await Fixture.CreateAsync(initialUserText: new string('o', 60_000)))
        {
            var owner = f.Finish((await f.SubmitOriginAsync(currentText)).Id);
            var presentation = f.Present(owner.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var stage = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            var models = new Models();
            var bridge = Bridge(f, models);
            var oversized = await Read(bridge, stage, new { limit = 48 });
            Check(oversized.State == WorkflowEffectState.Failed && oversized.Error!.Contains("not clipped", StringComparison.Ordinal) &&
                  models.Generated == 0 && models.Last is null,
                "an over-page required initial goal fails visibly without clipping or a deterministic model call");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var owner = f.Finish((await f.SubmitOriginAsync(currentText)).Id);
            var presentation = f.Present(owner.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var stage = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            var models = new Models();
            var bridge = Bridge(f, models);
            var saved = f.Read(presentation.Id);
            saved.PluginTests = saved.PluginTests! with { OriginRunId = "foreign-or-missing-origin" };
            f.Store.Put("interface", saved.Id, saved);
            Check((await Read(bridge, stage, new { limit = 48 })).State == WorkflowEffectState.Failed &&
                  (await Generate(bridge, stage, new(stage.Submission.ConversationSequence, 0, []))).State == WorkflowEffectState.Failed &&
                  models.Generated == 0 && models.Last is null,
                "forged reviewed lineage fails before history adoption or deterministic inference");
            saved.PluginTests = saved.PluginTests with { OriginRunId = owner.Id };
            f.Store.Put("interface", saved.Id, saved);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Check((await Read(bridge, stage, new { limit = 48 }, cancelled.Token)).State == WorkflowEffectState.Cancelled &&
                  models.Generated == 0,
                "initial exact history projection preserves caller cancellation");
        }
        Console.WriteLine("Initial plugin goal context: " + checks + " SQLite/admission/deterministic prepared-context checks passed; no live inference or plugin execution.");
        return checks;
    }

    private static CerebralEvent InitialSource(Fixture f) =>
        f.Journal.ReadPage(f.ConversationId, f.Origin.Submission.ConversationSequence, 1).Events.Single();
    private static CerebralEvent Append(Fixture f, string runId, string type, EventActor actor, string text) =>
        f.Journal.Append(new(Fixture.Id(), f.ConversationId, runId, 0, type, actor, [new TextContentPart(text)],
            new(ModelInclusion.Required, HumanVisibility.Activity), new(EventSourceKind.Service), DateTimeOffset.UtcNow),
            f.Journal.GetRevision(f.ConversationId)).Event;
    private static WorkerWorkflowEffects Bridge(Fixture f, Models models) => new(f.Store, f.Configuration, models, f.Tools);
    private static WorkflowCommandContext Context(WorkflowRun run, object args) =>
        new("initial-goal-fixture-" + Fixture.Id(), run.Submission, CerebralJson.Element(args)) { RunId = run.Id };
    private static Task<WorkflowCommandResult> Read(WorkerWorkflowEffects bridge, WorkflowRun run, object args,
        CancellationToken token = default) =>
        bridge.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand).ExecuteAsync(Context(run, args), token);
    private static WorkerWorkflowEffects.HistoryData Data(WorkflowCommandResult result) =>
        result.Data?.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)
        ?? throw new InvalidOperationException(result.Error ?? "Fixture history was not returned.");
    private static Task<WorkflowCommandResult> Generate(WorkerWorkflowEffects bridge, WorkflowRun run, ContextWindow window) =>
        bridge.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && command.Descriptor.Version == 1)
            .ExecuteAsync(Context(run, new WorkerWorkflowEffects.GenerateArguments(
                [new("system", [new TextContentPart("Fixture harness policy.")]),
                 new("user", [new TextContentPart("Latest actual stage feedback.")])], ToolNames: [], Context: window)), default);
    private static string Text(IEnumerable<ModelInputMessage> messages) =>
        string.Join("\n", messages.SelectMany(message => message.Content).OfType<TextContentPart>().Select(part => part.Text));
    private static SqliteConnection Open(Fixture f)
    {
        var connection = new SqliteConnection("Data Source=" + Path.Combine(f.Store.DirectoryPath, "engine.db"));
        connection.Open();
        return connection;
    }
    private static void SetBaseline(Fixture f, CerebralEvent cutoff, CerebralEvent original)
    {
        // Fixture seeding of an existing durable checkpoint; no summarizer inference is claimed.
        var checkpoint = new ContextCheckpoint("initial-goal-baseline", cutoff.Sequence, 0,
            [new(original.Id, original.Sequence)], "Fallible summary deliberately omits initial criteria.",
            "initial-goal-baseline", true);
        using var connection = Open(f);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversation_context_checkpoints(conversation_id,source_revision,run_id,effect_id,checkpoint,adopted_at) VALUES($conversation,$revision,$run,$id,$checkpoint,$at)";
        command.Parameters.AddWithValue("$conversation", f.ConversationId);
        command.Parameters.AddWithValue("$revision", cutoff.Sequence);
        command.Parameters.AddWithValue("$run", f.Origin.Id);
        command.Parameters.AddWithValue("$id", checkpoint.Id);
        command.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint, CerebralJson.Options));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
    private static void Exclude(Fixture f, string id)
    {
        using var connection = Open(f);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversation_events SET payload=json_set(payload,'$.excludedFromContext',json('true')) WHERE event_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private sealed class Models(bool image = true) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("initial-goal-fixture", "1.0.0", image ? ["text", "image"] : ["text"]);
        public ModelGenerationRequest? Last { get; private set; }
        public int Generated { get; private set; }
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Last = request;
            return Task.FromResult<IPreparedModelGeneration>(new Lease(this, request));
        }
        private sealed class Lease(Models owner, ModelGenerationRequest request) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("initial-goal-fixture", "initial-goal-fixture", "1.0.0",
                request.ModelId, 64, request.ContextLimit, request.ResponseLimit, "deterministic prepared-context fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                owner.Generated++;
                return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "Observed fixture result.", 8, 64));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
