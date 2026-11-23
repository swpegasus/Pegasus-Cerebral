using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Pure harness fixtures only. No model, provider, native SDK, pipe or filesystem call.</summary>
public static class LocalGenerationConversationChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Local generation Conversation: " + name); count++; }
        var choice = new WorkflowLocalGenerationChoice(1, "local.native.text", 262_144, 131_073);
        var edition = ConversationAgentHarness.CreateLocalGenerationEdition();
        Check(edition.Descriptor.Version == "1.0.5-local-generation.1" &&
            edition.Descriptor.LocalGenerationConsumer == new WorkflowLocalGenerationConsumer(1, "cerebral.host:generate", 2, "local.native.text") &&
            edition.Descriptor.ToolReceiptConsumer is { SchemaVersion: 1, Supported: true },
            "explicit edition declares the separate generic generation contract and retains receipt consumption");
        using var exported = new LocalGenerationConversationServicePlugin();
        using var ordinary = new ConversationServicePlugin();
        Check(exported.Harnesses.Any(h => h.Descriptor.Version == edition.Descriptor.Version) &&
            ordinary.Harnesses.All(h => h.Descriptor.Version != edition.Descriptor.Version), "new export leaves default definition selection untouched");

        var f = new Fixture(edition, choice);
        Check(f.Is("generate") && f.Command.CommandVersion == 2 &&
            f.Command.Arguments.GetProperty("responseLimit").GetInt32() == 131_073 &&
            !f.Command.Arguments.TryGetProperty("responseSharePercent", out _) &&
            !f.Command.Arguments.TryGetProperty("policy", out _), "normal generation repeats only the frozen visible choice and selects v2");
        Check(f.Step.State.GetProperty("localGenerationV2").GetProperty("choice").GetProperty("contextTokens").GetInt32() == 262_144,
            "run marker retains the detached accepted choice without executable policy authority");
        Check(JsonElement.DeepEquals(f.Step.State.GetProperty("pendingArguments"), f.Command.Arguments),
            "normal v2 state saves the exact final pending command envelope");
        var successful = new Fixture(edition, choice);
        successful.Model(new { action = "final", text = "Accepted normal-v2 fixture answer" });
        Check(successful.Step.Next is CompleteWorkflow { Success: true },
            "a fresh normal v2 receipt restores and adopts its valid final action");
        var absent = new Fixture(edition, null);
        Check(absent.Is("generate") && absent.Command.CommandVersion == 1 &&
            !absent.Step.State.TryGetProperty("localGenerationV2", out _), "new definition without accepted choice keeps v1/null behavior");
        foreach (var old in new[] { new ConversationAgentHarness(), ConversationAgentHarness.CreateToolContextProjectionEdition(),
            ConversationAgentHarness.CreateToolCatalogPageEdition() })
        {
            var legacy = new Fixture(old, null);
            Check(legacy.Is("generate") && legacy.Command.CommandVersion == 1 && old.Descriptor.LocalGenerationConsumer is null &&
                !legacy.Step.State.TryGetProperty("localGenerationV2", out _), "old edition and saved-state envelope stay v1 without generation declaration");
            legacy.Model(new { action = "final", text = "Fixture answer" }, versionOverride: 2);
            Check(legacy.Step.Next is CompleteWorkflow { Success: false }, "old model receipt cannot be upgraded to v2");
        }
        var unsupported = new Fixture(new ConversationAgentHarness(), choice);
        Check(unsupported.Step.Next is CompleteWorkflow { Success: false }, "a choice cannot make an undeclared old harness a consumer");
        var badShare = new Fixture(edition, choice, new { responseSharePercent = 25 });
        Check(badShare.Step.Next is CompleteWorkflow { Success: false }, "explicit share cannot silently override the accepted token choice");
        var balanceAction = new Fixture(edition, choice);
        balanceAction.Model(new { action = "tool", name = "conversation.response-balance", arguments = new { responseSharePercent = 50 } });
        Check(balanceAction.Is("generate") && balanceAction.Command.CommandVersion == 2 &&
            balanceAction.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 &&
            balanceAction.Step.State.GetProperty("descriptions").GetInt32() == 0,
            "opted normal phase refuses a conflicting percentage preference before catalog or tool dispatch. " +
            JsonSerializer.Serialize(new { next = balanceAction.Step.Next.GetType().Name,
                command = balanceAction.Step.Next is InvokeWorkflowCommand invoked ? invoked.CommandId + ":" + invoked.CommandVersion : null,
                error = balanceAction.Step.Next is CompleteWorkflow complete ? complete.Error : null,
                descriptions = balanceAction.Step.State.TryGetProperty("descriptions", out var descriptions) ? descriptions.GetInt32() : -1 }, CerebralJson.Options));
        var pendingBalance = new Fixture(edition, choice);
        pendingBalance.HostFailure("A pending response-balance preference conflicts with the explicit local generation choice.");
        Check(pendingBalance.Step.Next is CompleteWorkflow { Success: false } stopped &&
            stopped.Error!.Contains("pending response-balance", StringComparison.Ordinal) &&
            pendingBalance.Step.State.GetProperty("localGenerationV2").GetProperty("choice").GetProperty("visibleOutputTokens").GetInt32() == 131_073,
            "actual v2 preference refusal is retained without retry, v1 fallback or pretend consumption");
        var length = new Fixture(edition, choice);
        length.Model("incomplete fixture action", finish: ModelGenerationFinish.Length);
        Check(length.Is("generate") && length.Command.CommandVersion == 2 &&
            length.LastContributionText.Contains("accepted visible-token choice", StringComparison.Ordinal) &&
            !length.LastContributionText.Contains("call conversation.response-balance", StringComparison.Ordinal),
            "v2 length recovery cannot coach an unsupported percentage-override loop");

        var wrongVersion = new Fixture(edition, choice);
        wrongVersion.Model(new { action = "final", text = "must not be adopted" }, versionOverride: 1);
        Check(wrongVersion.Step.Next is CompleteWorkflow { Success: false }, "opted normal generation refuses a v1 receipt");
        var wrongArguments = new Fixture(edition, choice);
        wrongArguments.Model(new { action = "tool", name = "fixture.tool", arguments = new { } },
            argumentsOverride: CerebralJson.Element(new { responseLimit = 131_074 }));
        Check(wrongArguments.Step.Next is CompleteWorkflow { Success: false } &&
            wrongArguments.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "v2 receipt must echo the exact pending arguments before any proposed semantic action");
        foreach (var mutate in new Action<JsonObject>[] {
            state => state.Remove("localGenerationV2"),
            state => state["localGenerationV2"]!["runId"] = "other-run",
            state => state["localGenerationV2"]!["choice"]!["visibleOutputTokens"] = 131_074,
            state => state["localGenerationV2"]!["unknown"] = true,
            state => state["pendingArguments"]!["responseLimit"] = 131_074,
            state => state["pendingArguments"]!["policy"] = new JsonObject() })
        {
            var restore = new Fixture(edition, choice); restore.Resume(mutate);
            Check(restore.Step.Next is CompleteWorkflow { Success: false }, "missing/foreign/changed/extended marker cannot migrate an accepted effect");
        }
        var changedChoice = new Fixture(edition, choice);
        changedChoice.ChangeAcceptedChoice(choice with { VisibleOutputTokens = 131_074 });
        Check(changedChoice.Step.Next is CompleteWorkflow { Success: false }, "accepted submission choice cannot drift across state restoration");

        f.Model(new { action = "describe", names = new[] { "fixture.tool" } });
        Check(f.Is("catalog") && f.Command.CommandVersion == 2, "generation edition keeps independent catalog-v2 behavior");
        f.Catalog();
        f.Model(new { action = "tool", name = "fixture.tool", arguments = new { } });
        Check(f.Is("tool") && f.Command.CommandVersion == 1, "semantic execution remains its existing v1 contract");
        f.Tool(success: false);
        Check(f.Is("generate") && f.Command.CommandVersion == 2 &&
            f.Step.State.GetProperty("toolReceipts")[0].GetProperty("state").GetString() == "Failed",
            "next normal generation returns to v2 and preserves actual failed tool receipt");

        var raw = new Fixture(edition, choice);
        raw.Model(new { action = "describe", names = new[] { "fixture.tool" } }); raw.Catalog();
        raw.Model(new { action = "tool", name = "fixture.tool", arguments = new { } });
        raw.Tool(success: true, generation: new ToolGenerationRequest("Generate a fixture payload", "fixture.sink",
            WorkflowContract.EmptyObject, MaxTokens: 64, CompleteAssignment: false));
        Check(raw.Is("generate") && raw.Command.CommandVersion == 1 &&
            raw.Step.State.GetProperty("phase").GetString() == "generated-model" &&
            raw.Command.Arguments.GetProperty("responseLimit").GetInt32() == 64,
            "actual provider generation plan uses raw v1 and its own bounded payload budget");
        raw.Model("fixture payload", versionOverride: 2);
        Check(raw.Step.Next is CompleteWorkflow { Success: false }, "raw generated-tool receipt cannot borrow normal-model v2 authority");

        var compact = new Fixture(edition, choice, forceCompaction: true,
            history: [new ContextContribution("older", "fixture.owner", "history", 1, ContextRequirement.Reducible,
                [new ModelInputMessage("user", [new TextContentPart("older fixture source")])], BeforeCurrentInput: true)]);
        Check(compact.Is("compact") && compact.Command.CommandVersion == 1 &&
            !compact.Command.Arguments.TryGetProperty("responseLimit", out _),
            "compaction uses v1 and does not inherit the expanded normal-model response choice");

        var steering = new Fixture(edition, choice);
        steering.Steer();
        steering.Model(new { action = "tool", name = "fixture.tool", arguments = new { } });
        Check(steering.Is("generate") && steering.Command.CommandVersion == 2 &&
            steering.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 &&
            steering.Step.State.GetProperty("steeringCount").GetInt32() == 1,
            "new steering adopts the v2 receipt as superseded and never dispatches its stale action");

        object TaskBudget(string runId = "local-conversation-fixture") => new
        {
            schemaVersion = 1, runId, profileId = "fixture-profile", profileRevision = 1,
            origin = "worker", maxContextTokens = 46_080, maxResponseTokens = 32_768,
            currentContextTokens = 46_080, defaultContextTokens = 46_080, defaultResponseTokens = 11_520,
            contextSelectable = true, responseSelectable = true, allowSelection = true
        };
        Fixture BudgetFixture(bool compact = false, string runId = "local-conversation-fixture") =>
            new(new ConversationAgentHarness(), null, new { requestedResponseTokens = 24_576 },
                forceCompaction: compact, taskBudget: TaskBudget(runId),
                history: [new ContextContribution("budget-history", "fixture.owner", "history", 1,
                    ContextRequirement.Reducible, [new ModelInputMessage("user", [new TextContentPart("Older budget fixture source")])],
                    BeforeCurrentInput: true)]);
        var selected = BudgetFixture();
        Check(selected.Is("generate") && selected.Command.CommandVersion == 4 &&
            selected.Command.Arguments.GetProperty("contextLimit").GetInt32() == 46_080 &&
            selected.Command.Arguments.GetProperty("responseLimit").GetInt32() == 24_576,
            "normal task generation carries selected owner limits rather than frozen default output");
        var taskCompact = BudgetFixture(compact: true);
        Check(taskCompact.Is("compact") && taskCompact.Command.CommandVersion == 2 &&
            taskCompact.Command.Arguments.TryGetProperty("contextLimit", out var selectedContext) && selectedContext.GetInt32() == 46_080 &&
            taskCompact.Command.Arguments.TryGetProperty("responseLimit", out var selectedOutput) && selectedOutput.GetInt32() == 24_576 &&
            !taskCompact.Command.Arguments.TryGetProperty("responseSharePercent", out _),
            "typed compaction carries the same next-generation fit budget under compact v2; summary output is a separate Worker policy");
        taskCompact.HostFailure("fixture-summary-refused");
        Check(taskCompact.Step.Next is CompleteWorkflow { Success: false } acceptedFailure &&
            acceptedFailure.Error == "fixture-summary-refused",
            "an exact v2 compaction failure is retained without replay or generation fallback");

        var downgraded = BudgetFixture(compact: true);
        downgraded.HostFailure("must not be adopted", versionOverride: 1);
        Check(downgraded.Step.Next is CompleteWorkflow { Success: false } badVersion &&
            badVersion.Error!.Contains("matching versioned", StringComparison.Ordinal),
            "typed compact arguments cannot be adopted as a legacy v1 receipt");
        var altered = BudgetFixture(compact: true);
        var alteredArguments = JsonNode.Parse(altered.Command.Arguments.GetRawText())!.AsObject();
        alteredArguments["responseLimit"] = 24_577;
        altered.HostFailure("must not be adopted", argumentsOverride: JsonSerializer.SerializeToElement(alteredArguments));
        Check(altered.Step.Next is CompleteWorkflow { Success: false } badBudget &&
            badBudget.Error!.Contains("matching versioned", StringComparison.Ordinal),
            "v2 receipt budgets must exactly match the saved selected task budget");

        var legacyPending = BudgetFixture(compact: true);
        var legacyArguments = JsonNode.Parse(legacyPending.Command.Arguments.GetRawText())!.AsObject();
        legacyArguments.Remove("contextLimit"); legacyArguments.Remove("responseLimit");
        legacyPending.HostFailure("legacy-summary-refused", versionOverride: 1,
            argumentsOverride: JsonSerializer.SerializeToElement(legacyArguments));
        Check(legacyPending.Step.Next is CompleteWorkflow { Success: false } legacyFailure &&
            legacyFailure.Error == "legacy-summary-refused" &&
            legacyPending.Step.State.GetProperty("selectedResponseTokens").GetInt32() == 24_576,
            "retained closed legacy v1 receipt remains readable without acquiring v2 fit authority or replay");
        var wrongWindow = BudgetFixture(compact: true);
        var wrongWindowArguments = JsonNode.Parse(wrongWindow.Command.Arguments.GetRawText())!.AsObject();
        var capturedRevision = wrongWindow.Command.Arguments.GetProperty("context").GetProperty("revision").GetInt64();
        wrongWindowArguments["context"]!["revision"] = checked(capturedRevision + 1);
        wrongWindow.HostFailure("must not be adopted", argumentsOverride: JsonSerializer.SerializeToElement(wrongWindowArguments));
        Check(wrongWindow.Step.Next is CompleteWorkflow { Success: false } badWindow &&
            badWindow.Error!.Contains("matching versioned", StringComparison.Ordinal),
            "typed compaction cannot adopt another saved context window");
        var wrongOwner = BudgetFixture(compact: true, runId: "other-run");
        Check(wrongOwner.Step.Next is CompleteWorkflow { Success: false },
            "a compaction task budget cannot be admitted from another run");
        return count;
    }

    private sealed class Fixture
    {
        private readonly ConversationAgentHarness harness;
        private WorkflowSubmission submission;
        private int sequence;
        private readonly List<WorkflowSteeringEntry> steering = [];
        public WorkflowStep Step { get; private set; }
        public InvokeWorkflowCommand Command => (InvokeWorkflowCommand)Step.Next;
        public string LastContributionText => ((TextContentPart)Command.Arguments.GetProperty("context")
            .Deserialize<ContextWindow>(CerebralJson.Options)!.Contributions.Last().Messages.Single().Content.Single()).Text;
        public bool Is(string suffix) => Step.Next is InvokeWorkflowCommand c && c.CommandId == "cerebral.host:" + suffix;
        public Fixture(ConversationAgentHarness harness, WorkflowLocalGenerationChoice? choice, object? input = null,
            bool forceCompaction = false, ContextContribution[]? history = null, object? taskBudget = null)
        {
            this.harness = harness;
            submission = new("local-conversation-fixture", "fixture-chat", "fixture-profile", harness.Descriptor.Id,
                harness.Descriptor.Version, "Fixture request", null, CerebralJson.Element(input ?? new { }), WorkflowContract.EmptyObject)
                { LocalGeneration = choice, ConversationCompactionRequestId = forceCompaction ? "fixture-compaction" : null };
            Step = harness.Advance(new(Run()));
            if (Step.Next is CompleteWorkflow) return;
            var data = new Dictionary<string, object> { ["context"] = new ContextWindow(100, 0, history ?? []),
                ["hasOlder"] = false, ["beforeSequence"] = null!, ["snapshotSequence"] = 100 };
            if (taskBudget is not null) data["taskBudget"] = taskBudget;
            if (harness.Descriptor.ToolReceiptConsumer is not null)
                data["receiptProjectionAdmissions"] = new { schemaVersion = 1, items = Array.Empty<object>() };
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(data));
            if (Is("catalog")) Catalog();
        }
        private WorkflowRun Run() => new() { Id = submission.RequestId, Submission = submission,
            Status = WorkflowStatus.Running, Steering = steering.ToArray() };
        public void Steer() => steering.Add(new("steer-1", 1, "Updated fixture request", null, DateTimeOffset.UnixEpoch));
        public void Resume(Action<JsonObject> mutate)
        {
            var state = JsonNode.Parse(Step.State.GetRawText())!.AsObject(); mutate(state);
            Step = harness.Advance(new(Run() with { State = JsonSerializer.SerializeToElement(state) }));
        }
        public void ChangeAcceptedChoice(WorkflowLocalGenerationChoice choice)
        { submission = submission with { LocalGeneration = choice }; Step = harness.Advance(new(Run() with { State = Step.State })); }
        public void Catalog()
        {
            var exact = Command.Arguments.TryGetProperty("names", out var names);
            var selected = exact ? names.EnumerateArray().Select(n => n.GetString()!).ToArray() : ["fixture.tool"];
            var rows = selected.Select(name => {
                var row = new JsonObject { ["name"] = name, ["description"] = "Fixture", ["capability"] = "fixture" };
                if (exact) row["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };
                return (JsonNode)row;
            }).ToArray();
            var data = new JsonObject { ["tools"] = new JsonArray(rows), ["truncated"] = false, ["helpers"] = new JsonArray() };
            if (Command.CommandVersion == 2)
            {
                data["schemaVersion"] = 2; data["mode"] = exact ? "names" : "page";
                data["request"] = JsonNode.Parse(Command.Arguments.GetRawText()); data["remaining"] = 0; data["nextAfterName"] = null;
            }
            Result(WorkflowEffectState.Succeeded, JsonSerializer.SerializeToElement(data));
        }
        public void Model(object action, int? versionOverride = null, JsonElement? argumentsOverride = null,
            ModelGenerationFinish finish = ModelGenerationFinish.Stop)
        {
            var text = action is string raw ? raw : CerebralJson.Element(action).GetRawText();
            var responseBudget = Command.Arguments.TryGetProperty("responseLimit", out var limit)
                ? limit.GetInt32() : 64;
            var metadata = new PreparedModelMetadata("fixture-instance", "fixture-adapter", "1", "fixture-model", 100,
                262_144, responseBudget, "deterministic-fixture");
            ContextProjection? projection = null;
            if (Command.Arguments.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                var window = context.Deserialize<ContextWindow>(CerebralJson.Options)!;
                projection = new ContextProjection(ContextProjectionStatus.Ready, window.Revision, window.ExclusionRevision,
                    window.Contributions.Select(c => new ContextSourceReference(c.Id, c.SourceRevision)).ToArray(), [], metadata);
            }
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new { generation = new ModelGenerationResult(finish, text),
                prepared = metadata, context = projection, outputOmitted = false }), versionOverride, argumentsOverride);
        }
        public void Tool(bool success, ToolGenerationRequest? generation = null) =>
            Result(success ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed, CerebralJson.Element(new
            {
                name = Command.Arguments.GetProperty("name").GetString(), content = "Fixture tool outcome", truncated = false,
                evidence = new ToolExecutionEvidence(success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed, "fixture"),
                followUpRequired = generation is not null, generation
            }));
        public void HostFailure(string error, int? versionOverride = null, JsonElement? argumentsOverride = null) =>
            Result(WorkflowEffectState.Failed, WorkflowContract.EmptyObject, versionOverride, argumentsOverride, error);
        private void Result(WorkflowEffectState outcome, JsonElement data, int? versionOverride = null,
            JsonElement? argumentsOverride = null, string? error = null)
        {
            var command = Command;
            var effect = new WorkflowEffect("fixture-effect-" + ++sequence, command.CommandId, versionOverride ?? command.CommandVersion,
                argumentsOverride ?? command.Arguments, WorkflowReplayPolicy.Never, outcome, data, error);
            Step = harness.Advance(new(Run() with { State = Step.State, Effect = effect,
                Signal = new(WorkflowSignalKind.EffectResult, data) }));
        }
    }
}
