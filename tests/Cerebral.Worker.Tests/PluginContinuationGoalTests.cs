using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Worker;
using Fixture = Cerebral.Worker.Tests.PluginReviewContinuationTests.Fixture;

namespace Cerebral.Worker.Tests;

/// <summary>SQLite lineage and deterministic prepared-context evidence; no live inference or plugin execution.</summary>
internal static class PluginContinuationGoalTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Plugin goal continuity: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }
        async Task<ModelGenerationRequest> Prepare(Fixture fixture, WorkflowRun run)
        {
            var adapter = new Models();
            var effects = new WorkerWorkflowEffects(fixture.Store, fixture.Configuration, adapter, fixture.Tools);
            var generate = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && command.Descriptor.Version == 1);
            var result = await generate.ExecuteAsync(Context(run, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])), default);
            Check(result.State == WorkflowEffectState.Succeeded && adapter.Last is not null,
                "the exact admitted continuation prepares through the host bridge");
            return adapter.Last!;
        }
        static string Policy(ModelGenerationRequest request) =>
            Text(request.Messages.TakeWhile(message => message.Role == "system"));
        using (var f = await Fixture.CreateAsync(acceptedProfile: profile => profile.ReasoningMode = "off"))
        {
            var models = new Models();
            var bridge = new WorkerWorkflowEffects(f.Store, f.Configuration, models, f.Tools);
            var history = bridge.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand);
            var generate = bridge.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && command.Descriptor.Version == 1);
            var originalEvents = f.Journal.ReadPage(f.ConversationId, f.Origin.Submission.ConversationSequence, 2).Events;
            var source = f.Origin;
            CerebralEvent? repeatedHuman = null;
            var actualLogs = new List<CerebralEvent>();
            for (var round = 1; round <= 3; round++)
            {
                if (round == 3)
                    repeatedHuman = Append(f, source.Id, CerebralEventTypes.Message,
                        new(EventActorKind.User, "owner", "You"), Fixture.UserText, HumanVisibility.Transcript);
                var presentation = f.Present(source.Id);
                await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
                var run = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
                var acceptedStage = f.Journal.ReadPage(f.ConversationId, run.Submission.ConversationSequence, 2).Events;
                Check(run.Submission.UserText == PluginContinuationGoal.TestStageText &&
                    !run.Submission.AssignmentText!.Contains(Fixture.UserText, StringComparison.Ordinal) &&
                    !run.Submission.AssignmentText.Contains(Fixture.Assignment, StringComparison.Ordinal) &&
                    acceptedStage[0].Projection.Human == HumanVisibility.Activity &&
                    acceptedStage[0].Actor is { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" },
                    "QA stage " + round + " is brief service activity without a copied owner prompt");
                var read = await history.ExecuteAsync(Context(run, new { limit = 48 }), default);
                Check(read.State == WorkflowEffectState.Succeeded, "QA history " + round + " resolves its durable goal binding: " + read.Error);
                var window = read.Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!.Context;
                foreach (var original in originalEvents)
                {
                    var reference = window.Contributions.Single(item => item.Id == original.Id);
                    Check(reference.ExactReadHandle == original.Id && !reference.Complete &&
                        !Text(reference.Messages).Contains(original.Type == CerebralEventTypes.Message ? Fixture.UserText : Fixture.Assignment,
                            StringComparison.Ordinal),
                        "QA history retains original event " + original.Type + " as an exact reference in round " + round);
                }
                Check(actualLogs.All(log => window.Contributions.Any(item => item.Id == log.Id &&
                    Text(item.Messages).Contains(log.Content.OfType<TextContentPart>().Single().Text, StringComparison.Ordinal))),
                    "prior actual response/tool evidence remains in QA history " + round);
                if (repeatedHuman is not null)
                    Check(window.Contributions.Single(item => item.Id == repeatedHuman.Id).Complete &&
                        Text(window.Contributions.Single(item => item.Id == repeatedHuman.Id).Messages)
                            .Contains(Fixture.UserText, StringComparison.Ordinal),
                        "a legitimate repeated human message is preserved by identity");
                var result = await generate.ExecuteAsync(Context(run, new WorkerWorkflowEffects.GenerateArguments(
                    [new("system", [new TextContentPart("Fixture harness policy.")]),
                     new("user", [new TextContentPart("Latest actual test feedback.")])],
                    ToolNames: [], Context: window)), default);
                var prepared = models.Last ?? throw new InvalidOperationException(result.Error);
                var parts = prepared.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().ToArray();
                var all = Text(prepared.Messages);
                var phase = Policy(prepared);
                Check(Occurrences(phase, "Host-owned continuation phase: installed-plugin QA.") == 1 &&
                    phase.Contains("presentationId=" + presentation.Id, StringComparison.Ordinal) &&
                    phase.Contains("Obtain current native observations before source edits", StringComparison.Ordinal) &&
                    phase.Contains("stop with that review pending", StringComparison.Ordinal) &&
                    !phase.Contains(Fixture.UserText, StringComparison.Ordinal) &&
                    !phase.Contains(Fixture.Assignment, StringComparison.Ordinal) &&
                    !phase.Contains("Service follow-up:", StringComparison.Ordinal),
                    "prepared QA round " + round + " has one leading lineage-derived phase policy before goal/history, without promoting their prose");
                Check(result.State == WorkflowEffectState.Succeeded &&
                    parts.Count(part => part.Text == Fixture.UserText) == 1 &&
                    Occurrences(all, Fixture.UserText) == (repeatedHuman is null ? 1 : 2) &&
                    Occurrences(all, Fixture.Assignment) == 1 &&
                    Occurrences(all, "Supplemental exact café context.\nSecond line.") == 1,
                    "prepared QA round " + round + " has one protected original goal/private assignment/content anchor");
                Check(all.Contains("originalRunId=" + f.Origin.Id, StringComparison.Ordinal) &&
                    all.Contains("eventId=" + originalEvents[0].Id, StringComparison.Ordinal) &&
                    Occurrences(all, "Service follow-up:") == 1 &&
                    all.Contains("Latest actual test feedback.", StringComparison.Ordinal),
                    "prepared QA round " + round + " binds the goal to exact original lineage and only the current stage directive");
                var exact = await history.ExecuteAsync(Context(run, new { eventId = originalEvents[0].Id }), default);
                Check(exact.State == WorkflowEffectState.Succeeded &&
                    exact.Data!.Value.GetProperty("json").GetString()!.Contains("Create a simple checkpoint plugin.", StringComparison.Ordinal) &&
                    f.Journal.ReadExact(f.ConversationId, originalEvents[0].Id, f.Journal.GetRevision(f.ConversationId))
                        .Content.OfType<TextContentPart>().First().Text == Fixture.UserText,
                    "the original accepted prompt remains immutable and exact-readable in round " + round);
                if (round == 3)
                {
                    var before = models.Generated;
                    var altered = run.Submission with { AcceptedContent = [] };
                    var rejected = await generate.ExecuteAsync(Context(run, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: []))
                        with { Submission = altered }, default);
                    Check(rejected.State == WorkflowEffectState.Failed && models.Generated == before,
                        "forged current resource binding fails before deterministic inference");
                    var stored = f.Read(presentation.Id);
                    stored.PluginTests = stored.PluginTests! with { OriginRunId = "foreign-or-missing-original" };
                    f.Store.Put("interface", stored.Id, stored);
                    rejected = await generate.ExecuteAsync(Context(run, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [])), default);
                    Check(rejected.State == WorkflowEffectState.Failed && models.Generated == before,
                        "missing original lineage fails closed without a model call");
                    stored.PluginTests = stored.PluginTests with { OriginRunId = f.Origin.Id };
                    f.Store.Put("interface", stored.Id, stored);
                }
                actualLogs.Add(Append(f, run.Id, CerebralEventTypes.ToolResult,
                    new(EventActorKind.Tool, "plugin.controls"), "Actual observed failure in QA round " + round, HumanVisibility.Activity));
                actualLogs.Add(Append(f, run.Id, CerebralEventTypes.Message,
                    new(EventActorKind.Agent, f.Profile.Id, f.Profile.Name), "Actual QA response " + round, HumanVisibility.Transcript));
                source = f.Finish(run.Id);
            }
            Check(f.Journal.ReadPage(f.ConversationId, 0, 100).Events
                .Where(item => item.Type == CerebralEventTypes.Message && item.Actor.Kind == EventActorKind.User)
                .Select(item => item.Id).SequenceEqual(new[] { originalEvents[0].Id, repeatedHuman!.Id }),
                "all three QA rounds preserve exactly the original owner card and legitimate later owner repeat");
        }
        using (var f = await Fixture.CreateAsync(acceptedProfile: profile => profile.ReasoningMode = "off"))
        {
            var presentation = f.Present(f.Origin.Id);
            var tests = presentation.PluginTests!;
            var legacyAssignment = Fixture.Assignment + "\n\n" +
                WorkerWorkflows.BuildPluginTestAssignment(presentation, presentation.Plugin!, tests, false,
                    new("unavailable", null, [], [], true, "Fixture visual evidence unavailable.")) +
                "\nLegacy stage-only prose: rebuild unchanged source before tests.";
            var legacy = f.Workflows.Store.Submit(f.Origin.Submission with
            {
                RequestId = tests.RequestId, ConversationSequence = 0,
                AssignmentText = legacyAssignment,
                Input = WorkerWorkflows.PreparePluginTestInput(f.Origin.Submission.Input),
                Caller = new(EventActorKind.Service, "cerebral.plugin-tests", "Cerebral plugin tests")
            });
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Workflows.Store.Get(legacy.Id).Submission.UserText == Fixture.UserText &&
                f.Workflows.Store.Get(legacy.Id).Submission.AssignmentText == legacyAssignment,
                "recovery preserves the exact prior admission instead of rewriting a legacy stage");
            var goal = PluginContinuationGoal.Read(f.Store, f.Journal, legacy.Submission)!;
            Check(goal.Original.RequestId == f.Origin.Id &&
                !goal.StageAssignment.Contains(Fixture.Assignment, StringComparison.Ordinal) &&
                goal.StageAssignment.StartsWith("Service follow-up:", StringComparison.Ordinal),
                "legacy combined assignment resolves one exact protected owner assignment and a separate service directive");
            var prepared = await Prepare(f, legacy);
            Check(Policy(prepared).Contains("Host-owned continuation phase: installed-plugin QA.", StringComparison.Ordinal) &&
                !Policy(prepared).Contains("Legacy stage-only prose:", StringComparison.Ordinal) &&
                Text(prepared.Messages).Contains("Legacy stage-only prose:", StringComparison.Ordinal) &&
                Occurrences(Text(prepared.Messages), Fixture.UserText) == 1 &&
                Occurrences(Text(prepared.Messages), Fixture.Assignment) == 1,
                "legacy admission gets the proven QA phase without rewriting the goal or promoting stage assignment text");
        }
        using (var f = await Fixture.CreateAsync(acceptedProfile: profile =>
        {
            profile.ReasoningMode = "off"; Fixture.AllowRepairTools(profile);
        }))
        {
            var presentation = f.Present(f.Origin.Id, success: false, repairAllowed: true,
                failureStage: InterfaceFailureStage.NativeLoad);
            presentation.Error = "Failure prose: build another unrelated plugin.";
            f.Store.Put("interface", presentation.Id, presentation);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var run = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            var goal = PluginContinuationGoal.Read(f.Store, f.Journal, run.Submission)!;
            Check(run.Submission.UserText == PluginContinuationGoal.RepairStageText &&
                !run.Submission.AssignmentText!.Contains(Fixture.Assignment, StringComparison.Ordinal) &&
                goal.Original.RequestId == f.Origin.Id &&
                f.Journal.ReadPage(f.ConversationId, run.Submission.ConversationSequence, 1).Events.Single()
                    .Projection.Human == HumanVisibility.Activity,
                "failed-review repair retains the exact primary goal and enters activity as a brief repair stage");
            var prepared = await Prepare(f, run);
            Check(Policy(prepared).Contains("Host-owned continuation phase: failed-review source repair.", StringComparison.Ordinal) &&
                Policy(prepared).Contains("presentationId=" + presentation.Id, StringComparison.Ordinal) &&
                !Policy(prepared).Contains("Host-owned continuation phase: installed-plugin QA.", StringComparison.Ordinal) &&
                !Policy(prepared).Contains(presentation.Error!, StringComparison.Ordinal) &&
                Text(prepared.Messages).Contains(presentation.Error!, StringComparison.Ordinal) &&
                Occurrences(Text(prepared.Messages), Fixture.UserText) == 1 &&
                Occurrences(Text(prepared.Messages), Fixture.Assignment) == 1,
                "failed-review repair uses its exact closed phase and retains failure prose only as user-level evidence");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var ordinaryService = f.Workflows.Store.Submit(f.Origin.Submission with
            {
                RequestId = "ordinary-service-message", UserText = "An independent service assignment.",
                ConversationSequence = 0, Caller = new(EventActorKind.Service, "cerebral.plugin-tests")
            });
            Check(PluginContinuationGoal.Read(f.Store, f.Journal, ordinaryService.Submission) is null &&
                f.Journal.ReadPage(f.ConversationId, ordinaryService.Submission.ConversationSequence, 1).Events.Single()
                    .Projection.Human == HumanVisibility.Transcript,
                "an unrelated service assignment keeps its independent request and normal transcript projection");
            var prepared = await Prepare(f, ordinaryService);
            Check(!Policy(prepared).Contains("Host-owned continuation phase:", StringComparison.Ordinal),
                "service actor text without an exact reviewed stage lineage cannot obtain a host phase policy");
        }
        checks += await PluginContinuationInitialGoalTests.RunAsync();
        checks += await PluginContinuationDisplayTests.RunAsync();
        Console.WriteLine("Plugin goal continuity: " + checks + " SQLite/deterministic prepared-context checks passed; no live model or plugin execution.");
        return checks;
    }

    private static WorkflowCommandContext Context(WorkflowRun run, object arguments) =>
        new("goal-fixture-" + Guid.NewGuid().ToString("N"), run.Submission, CerebralJson.Element(arguments)) { RunId = run.Id };

    private static CerebralEvent Append(Fixture fixture, string runId, string type, EventActor actor, string text,
        HumanVisibility human) => fixture.Journal.Append(new(Guid.NewGuid().ToString("N"), fixture.ConversationId,
            runId, 0, type, actor, [new TextContentPart(text)], new(ModelInclusion.Required, human),
            new(EventSourceKind.Service), DateTimeOffset.UtcNow), fixture.Journal.GetRevision(fixture.ConversationId)).Event;

    private static string Text(IEnumerable<ModelInputMessage> messages) =>
        string.Join("\n", messages.SelectMany(message => message.Content).OfType<TextContentPart>().Select(part => part.Text));

    private static int Occurrences(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    private sealed class Models : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("goal-continuity-fixture", "1.0.0", ["text"]);
        public ModelGenerationRequest? Last { get; private set; }
        public int Generated { get; private set; }
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Last = request;
            return Task.FromResult<IPreparedModelGeneration>(new Lease(this, request));
        }
        private sealed class Lease(Models owner, ModelGenerationRequest request) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("goal-fixture", "goal-continuity-fixture", "1.0.0",
                request.ModelId, 64, request.ContextLimit, request.ResponseLimit, "deterministic context fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); owner.Generated++;
                return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "Observed fixture result.", 8, 64));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
