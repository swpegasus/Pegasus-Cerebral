using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic host-effect fixtures; no provider, model, host grant or network execution.</summary>
public static class ConversationPersonalityTests
{
    private const string Provider = "pegasus.personality:context.resolve", Style = "pegasus.personality:style";
    private const string Kind = "personality.behavior", Body = "STYLE_ONLY_PERSONALITY_FIXTURE";
    public static int Run()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Personality fixture: " + message);
            checks++;
        }
        var legacy = new Fixture(); legacy.History(null, include: false);
        var absent = new Fixture(); absent.History(null);
        Check(legacy.Is("cerebral.host:catalog") && JsonElement.DeepEquals(legacy.Step.State, absent.Step.State),
            "missing and null metadata preserve legacy initial transition and state");
        Check(!legacy.Step.State.TryGetProperty("personality", out _), "legacy state has no additive personality field");
        Check(!new ConversationAgentHarness().Descriptor.InputSchema.GetProperty("properties").TryGetProperty("personality", out _),
            "public Descriptor input has no personality channel");
        var publicInput = new Fixture(input: new { personality = Ready() });
        Check(publicInput.Step.Next is CompleteWorkflow { Success: false }, "public personality input refuses before an effect");

        foreach (var status in new[] { "legacy", "notApplicable", "disabled", "unavailable", "migrationPending" })
        {
            var omitted = new Fixture(); omitted.History(new { status, request = (object?)null });
            Check(omitted.Is("cerebral.host:catalog") &&
                !omitted.Step.State.GetProperty("personality").GetProperty("attempted").GetBoolean(),
                "non-ready disposition never requests source: " + status);
        }
        foreach (var status in new[] { "Ready", "unknown", "migration-ready" })
        {
            var invalid = new Fixture(); invalid.History(new { status, request = (object?)null });
            Check(invalid.Step.Next is CompleteWorkflow { Success: false }, "unknown/case-changed status refuses");
        }
        var missingReady = new Fixture(); missingReady.History(new { status = "ready", request = (object?)null });
        Check(missingReady.Step.Next is CompleteWorkflow { Success: false }, "ready requires exact request");
        var disabledRequest = new Fixture(); disabledRequest.History(new { status = "disabled", request = Request() });
        Check(disabledRequest.Step.Next is CompleteWorkflow { Success: false }, "non-ready request refuses");

        foreach (var request in new[]
        {
            Request() with { ProviderId = "foreign:context.resolve" },
            Request() with { ProviderVersion = 2 },
            Request() with { MaximumUtf8Bytes = 4096 },
            Request() with { References = [Request().References[0] with { SourceId = "bad source" }] },
            Request() with { References = [] },
            Request() with { References = [Request().References[0] with { Requirement = ContextRequirement.Required }] },
            Request() with { References = [Request().References[0] with { BeforeCurrentInput = false }] },
            Request() with { References = [Request().References[0] with { ExpectedRevision = null }] }
        })
        {
            var invalid = new Fixture(); invalid.History(new { status = "ready", request });
            Check(invalid.Step.Next is CompleteWorkflow { Success: false }, "invalid provider selection refuses");
        }
        var duplicate = new Fixture();
        using (var raw = JsonDocument.Parse("{\"status\":\"disabled\",\"status\":\"ready\",\"request\":null}"))
            duplicate.History(raw.RootElement.Clone());
        Check(duplicate.Step.Next is CompleteWorkflow { Success: false }, "duplicate metadata fields refuse");

        var ready = new Fixture(); ready.History(Ready());
        Check(ready.Is(Provider) && ready.Command.CommandVersion == 1, "one exact provider effect precedes catalog/model");
        var pending = ready.Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!;
        Check(pending.ContextRevision == 101 && pending.ExclusionRevision == 0 &&
            pending.References[0].SourceId == Request().References[0].SourceId,
            "rebase actual window revisions without changing immutable source");
        Check(ready.Step.State.GetProperty("personality").GetProperty("attempted").GetBoolean(),
            "attempted is durable before dispatch");
        ready.ProviderSuccess();
        var receipt = ready.Step.State.GetProperty("personality");
        Check(receipt.GetProperty("outcome").GetString() == "received" &&
            receipt.GetProperty("effectId").GetString() == receipt.GetProperty("providerReceiptId").GetString(),
            "confirmed exact effect/provider receipt retained");
        ready.Catalog();
        Check(ready.Is("cerebral.host:generate") && ready.Context.Contributions.Single(x => x.Id == Style).Requirement == ContextRequirement.Optional,
            "optional style reaches current lead context");
        Check(ready.Context.Contributions.Single(x => x.Id == Style).BeforeCurrentInput, "style precedes current input");
        Check(!ready.SystemText.Contains(Body, StringComparison.Ordinal) && !ready.MetadataText.Contains(Body, StringComparison.Ordinal),
            "style body absent from stable system policy and outcome metadata");
        Check(ready.MetadataText.Contains("received", StringComparison.Ordinal), "fixed outcome metadata is model-visible");
        Check(!ContextValidation.GetCoveredManifest(ready.Context).Any(x => x.Id == Style) &&
            !ContextValidation.GetCoveredLocators(ready.Context).Any(x => x.Id == Style),
            "optional style has no compactor summary coverage");
        ready.ModelFinal();
        Check(ready.Step.Next is CompleteWorkflow { Success: true }, "ordinary final outcome preserved");

        var unavailable = new Fixture(); unavailable.History(Ready());
        unavailable.Result(WorkflowEffectState.Failed, CerebralJson.Element(new { failureKind = "source-unavailable" }));
        var omission = unavailable.Step.State.GetProperty("personality");
        Check(unavailable.Is("cerebral.host:catalog") && omission.GetProperty("outcome").GetString() == "sourceUnavailable" &&
            omission.GetProperty("effectId").GetString() is not null && !omission.TryGetProperty("providerReceiptId", out _),
            "exact source-unavailable is failed known omission with no fabricated provider receipt");
        unavailable.Catalog();
        Check(unavailable.Is("cerebral.host:generate") && !unavailable.Context.Contributions.Any(x => x.Id == Style),
            "source omission proceeds without replacement or another provider effect");
        foreach (var failureKind in new[] { "scope-mismatch", "invalid-request", "requested-byte-bound" })
        {
            var failed = new Fixture(); failed.History(Ready());
            failed.Result(WorkflowEffectState.Failed, CerebralJson.Element(new { failureKind }));
            Check(failed.Step.Next is CompleteWorkflow { Success: false }, "arbitrary failed source cannot become absence");
        }
        foreach (var state in new[] { WorkflowEffectState.Unknown, WorkflowEffectState.Dispatched, WorkflowEffectState.Cancelled })
        {
            var uncertain = new Fixture(); uncertain.History(Ready());
            uncertain.Result(state, WorkflowContract.EmptyObject);
            Check(uncertain.Step.Next is CompleteWorkflow { Success: false } &&
                uncertain.Step.State.GetProperty("personality").GetProperty("attempted").GetBoolean(),
                "uncertain/cancelled effect stops and never replays");
        }
        var cancelled = new Fixture(); cancelled.History(Ready()); cancelled.Cancel();
        Check(cancelled.Step.Next is CompleteWorkflow { Success: false }, "cancellation requests no new provider/model");

        foreach (var mutate in new Func<ContextProviderResult, ContextProviderResult>[]
        {
            data => data with { ProviderId = "foreign:context.resolve" },
            data => data with { ProviderVersion = 2 },
            data => data with { Window = data.Window with { Revision = data.Window.Revision + 1 } },
            data => data with { Window = data.Window with { ExclusionRevision = 1 } },
            data => data with { Window = data.Window with { Contributions = [] } },
            data => ChangeSource(data, source => source with { Owner = "foreign:context.resolve" }),
            data => ChangeSource(data, source => source with { SourceRevision = 2 }),
            data => ChangeSource(data, source => source with { Requirement = ContextRequirement.Reducible }),
            data => ChangeSource(data, source => source with { Complete = false }),
            data => ChangeSource(data, source => source with { ExactReadHandle = null }),
            data => ChangeSource(data, source => source with { Messages = [new("system", [new TextContentPart(Body)])] })
        })
        {
            var invalid = new Fixture(); invalid.History(Ready()); invalid.ProviderSuccess(mutate);
            Check(invalid.Step.Next is CompleteWorkflow { Success: false }, "malformed normalized result refuses");
        }
        var wrongReceipt = new Fixture(); wrongReceipt.History(Ready()); wrongReceipt.ProviderSuccess(receipt: "foreign-receipt");
        Check(wrongReceipt.Step.Next is CompleteWorkflow { Success: false }, "foreign provider receipt refuses");
        var wrongArguments = new Fixture(); wrongArguments.History(Ready());
        wrongArguments.ProviderSuccess(arguments: CerebralJson.Element(Request()));
        Check(wrongArguments.Step.Next is CompleteWorkflow { Success: false }, "stale/pending arguments refuse");

        var restoredHealthy = new Fixture(); restoredHealthy.History(Ready()); restoredHealthy.ProviderSuccess();
        restoredHealthy.RestoreState(restoredHealthy.Step.State); restoredHealthy.Catalog();
        Check(restoredHealthy.Is("cerebral.host:generate"), "matching catalog effect restores a healthy received state");

        var restore = new Fixture(); restore.History(Ready()); restore.ProviderSuccess();
        var badState = JsonNode.Parse(restore.Step.State.GetRawText())!.AsObject();
        badState["personality"]!["providerReceiptId"] = "changed";
        restore.RestoreState(CerebralJson.Element(badState)); restore.Catalog();
        Check(restore.RestorationRefused, "saved receipt corruption refuses before matching catalog effect");
        var duplicateState = new Fixture(); duplicateState.History(Ready());
        using (var raw = JsonDocument.Parse(duplicateState.Step.State.GetRawText().Replace("\"attempted\":true", "\"attempted\":true,\"attempted\":false", StringComparison.Ordinal)))
            duplicateState.RestoreState(raw.RootElement.Clone());
        duplicateState.ProviderSuccess();
        Check(duplicateState.RestorationRefused, "saved duplicate one-use flag refuses before matching provider effect");

        var missingAttempt = new Fixture(); missingAttempt.History(Ready());
        var incompleteState = JsonNode.Parse(missingAttempt.Step.State.GetRawText())!.AsObject();
        incompleteState["personality"]!.AsObject().Remove("attempted");
        missingAttempt.RestoreState(CerebralJson.Element(incompleteState)); missingAttempt.ProviderSuccess();
        Check(missingAttempt.RestorationRefused, "present metadata requires explicit attempted Boolean");

        var unattempted = new Fixture(); unattempted.History(Ready());
        var inconsistentState = JsonNode.Parse(unattempted.Step.State.GetRawText())!.AsObject();
        inconsistentState["personality"]!["attempted"] = false;
        unattempted.RestoreState(CerebralJson.Element(inconsistentState)); unattempted.ProviderSuccess();
        Check(unattempted.RestorationRefused, "personality phase requires durable attempted true");

        foreach (var received in new[] { false, true })
        foreach (var remove in new[] { false, true })
        {
            var stripped = new Fixture(); stripped.History(Ready());
            if (received) stripped.ProviderSuccess();
            var strippedState = JsonNode.Parse(stripped.Step.State.GetRawText())!.AsObject();
            if (remove) strippedState.Remove("personality"); else strippedState["personality"] = null;
            stripped.RestoreState(CerebralJson.Element(strippedState));
            if (received) stripped.Catalog(); else stripped.ProviderSuccess();
            Check(stripped.RestorationRefused,
                "missing/null metadata refuses pending phase or reserved style before a matching effect");
        }

        var page = new Fixture(); page.History(Ready()); page.ProviderSuccess(); page.Catalog();
        page.Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
        {
            generation = new ModelGenerationResult(ModelGenerationFinish.Stop, "{\"action\":\"history\",\"beforeSequence\":50}"),
            context = page.Projection()
        }));
        // Its initial page advertises no older cursor, so arbitrary later history was not authorized.
        Check(page.Step.Next is not InvokeWorkflowCommand { CommandId: "cerebral.host:history" },
            "style cannot create older-history authority");

        var compaction = new Fixture(history: [Prior()]);
        compaction.History(Ready()); compaction.ProviderSuccess(); compaction.Catalog();
        compaction.Result(WorkflowEffectState.Failed, CerebralJson.Element(new { context = compaction.Projection(ContextProjectionStatus.NeedsCompaction) }));
        Check(compaction.Is("cerebral.host:compact") && !ContextValidation.GetCoveredManifest(compaction.Context).Any(x => x.Id == Style),
            "compaction excludes optional style from covered sources");
        compaction.Compact();
        Check(compaction.Is("cerebral.host:generate") && compaction.Context.Contributions.Count(x => x.Id == Style) == 1,
            "compaction retains exact style without second provider request");

        var forced = new Fixture(forceCompaction: true, history: [Prior()]);
        forced.History(Ready());
        Check(forced.Is("cerebral.host:compact") && !forced.Context.Contributions.Any(x => x.Id == Style),
            "explicit initial compaction preserves ordering before style retrieval");
        forced.Compact();
        Check(forced.Is(Provider), "one exact provider request follows explicit initial compaction");
        var historySteering = new Fixture(); historySteering.Steer(); historySteering.History(Ready());
        var historyRequest = historySteering.Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!;
        Check(historySteering.Is(Provider) &&
            historyRequest.ContextRevision == historySteering.Step.State.GetProperty("contextRevision").GetInt64() &&
            historyRequest.ExclusionRevision == historySteering.Step.State.GetProperty("exclusionRevision").GetInt64() &&
            JsonElement.DeepEquals(historySteering.Command.Arguments, historySteering.Step.State.GetProperty("pendingArguments")),
            "steering on first history rebases the new provider command and durable pending arguments");
        historySteering.ProviderSuccess(); historySteering.Catalog();
        Check(historySteering.Is("cerebral.host:generate") &&
            historySteering.Context.Contributions.Count(x => x.Id == Style) == 1,
            "first-history steering preserves one request and accepts its exact receipt");

        var compactionSteering = new Fixture(forceCompaction: true, history: [Prior()]);
        compactionSteering.History(Ready()); compactionSteering.Steer(); compactionSteering.Compact();
        var compactRequest = compactionSteering.Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!;
        Check(compactionSteering.Is(Provider) &&
            compactRequest.ContextRevision == compactionSteering.Step.State.GetProperty("contextRevision").GetInt64() &&
            JsonElement.DeepEquals(compactionSteering.Command.Arguments, compactionSteering.Step.State.GetProperty("pendingArguments")),
            "steering on initial compaction rebases only the newly created provider request");
        compactionSteering.ProviderSuccess(); compactionSteering.Catalog();
        Check(compactionSteering.Is("cerebral.host:generate") &&
            compactionSteering.Context.Contributions.Count(x => x.Id == Style) == 1,
            "post-compaction steering accepts the exact provider outcome without reissue");

        var steering = new Fixture(); steering.History(Ready()); steering.Steer();
        steering.ProviderSuccess(); steering.Catalog();
        Check(steering.Is("cerebral.host:generate") && steering.Context.Contributions.Count(x => x.Id == Style) == 1 &&
            steering.Step.State.GetProperty("steeringCount").GetInt32() == 1,
            "pending steering adopts source receipt once and retains latest direction");
        Console.WriteLine("PASS ConversationPersonalityTests: " + checks);
        return checks;
    }

    private static ContextProviderResult ChangeSource(ContextProviderResult data, Func<ContextContribution, ContextContribution> change) =>
        data with { Window = data.Window with { Contributions = [change(data.Window.Contributions[0])] } };
    private static ContextProviderRequest Request() => new(Provider, 1,
        [new(Style, Kind, "personality/v1/synthetic/1/" + new string('a', 64), 1, ContextRequirement.Optional, 0, true)], 2048, 100, 0);
    private static object Ready() => new { status = "ready", request = Request() };
    private static ContextContribution Prior() => new("prior", "cerebral.host", "history", 10, ContextRequirement.Reducible,
        [new("user", [new TextContentPart("Earlier working context.")])], "event-prior", BeforeCurrentInput: true);
    private static PreparedModelMetadata Metadata(int tokens) =>
        new("fixture-instance", "fixture-adapter", "1.0.0", "fixture-model", tokens, 2048, 256, "deterministic-fixture")
        { SafetyReserveTokens = 128 };

    private sealed class Fixture
    {
        private readonly ConversationAgentHarness harness = new();
        private readonly WorkflowSubmission submission;
        private readonly ContextContribution[] history;
        private readonly List<WorkflowSteeringEntry> steering = [];
        private int sequence;
        public WorkflowStep Step { get; private set; }
        public InvokeWorkflowCommand Command => (InvokeWorkflowCommand)Step.Next;
        public ContextWindow Context => Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
        public string SystemText => ((TextContentPart)Command.Arguments.GetProperty("messages")[0].Deserialize<ModelInputMessage>(CerebralJson.Options)!.Content.Single()).Text;
        public string MetadataText => ((TextContentPart)Command.Arguments.GetProperty("messages")[1].Deserialize<ModelInputMessage>(CerebralJson.Options)!.Content.Single()).Text;
        public bool Is(string name) => Step.Next is InvokeWorkflowCommand command && command.CommandId == name;
        public bool RestorationRefused => Step.Next is CompleteWorkflow { Success: false, Error: { } error } &&
            error.StartsWith("The saved conversation state could not be restored:", StringComparison.Ordinal);
        public void RestoreState(JsonElement state) => Step = Step with { State = state };
        public Fixture(bool forceCompaction = false, ContextContribution[]? history = null, object? input = null)
        {
            this.history = history ?? [];
            submission = new("personality-fixture", "conversation", "lead", ConversationAgentHarness.Id, ConversationAgentHarness.Version,
                "Answer the accepted request.", null, CerebralJson.Element(input ?? new { }), WorkflowContract.EmptyObject)
                { Caller = new(EventActorKind.User, "owner"), ConversationCompactionRequestId = forceCompaction ? "compact" : null };
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running }));
        }
        public void History(object? personality, bool include = true)
        {
            var page = new Dictionary<string, object?>
            {
                ["context"] = new ContextWindow(100, 0, history), ["hasOlder"] = false,
                ["beforeSequence"] = null, ["snapshotSequence"] = 100L
            };
            if (include) page["personality"] = personality;
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(page));
        }
        public void ProviderSuccess(Func<ContextProviderResult, ContextProviderResult>? mutate = null,
            string? receipt = null, JsonElement? arguments = null)
        {
            var request = Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!;
            var source = new ContextContribution(Style, Provider, Kind, 1, ContextRequirement.Optional,
                [new("user", [new TextContentPart(Body)])], "cerebral-context:v1.fixture", BeforeCurrentInput: true);
            var data = new ContextProviderResult(Provider, 1, new(request.ContextRevision, request.ExclusionRevision, [source]));
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(mutate?.Invoke(data) ?? data),
                receipt ?? "effect-" + (sequence + 1), arguments);
        }
        public void Catalog() => Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new { tools = Array.Empty<object>(), truncated = false, helpers = Array.Empty<object>() }));
        public ContextProjection Projection(ContextProjectionStatus status = ContextProjectionStatus.Ready) =>
            new(status, Context.Revision, Context.ExclusionRevision,
                Context.Contributions.Select(x => new ContextSourceReference(x.Id, x.SourceRevision)).ToArray(), [], Metadata(128));
        public void ModelFinal() => Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
        {
            generation = new ModelGenerationResult(ModelGenerationFinish.Stop, "{\"action\":\"final\",\"text\":\"done\"}"),
            context = Projection()
        }));
        public void Compact()
        {
            var window = Context;
            var checkpoint = new ContextCheckpoint("checkpoint", window.Revision, window.ExclusionRevision,
                ContextValidation.GetCoveredManifest(window), "Confirmed bounded summary.", "effect-" + (sequence + 1),
                ContextValidation.CheckpointBeforeCurrentInput(window)) { SourceLocators = ContextValidation.GetCoveredLocators(window) };
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new { checkpoint, before = Metadata(1800), after = Metadata(256) }));
        }
        public void Steer() => steering.Add(new("steering-1", 1, "Use the latest direction.", null, DateTimeOffset.UtcNow));
        public void Cancel() => Step = harness.Advance(new(new WorkflowRun
        { Id = submission.RequestId, Submission = submission, State = Step.State, Status = WorkflowStatus.Cancelling, CancelRequested = true }));
        public void Result(WorkflowEffectState state, JsonElement data, string? receipt = null, JsonElement? arguments = null)
        {
            var command = Command;
            var effect = new WorkflowEffect("effect-" + ++sequence, command.CommandId, command.CommandVersion,
                arguments ?? command.Arguments, WorkflowReplayPolicy.Idempotent, state, data, ProviderReceiptId: receipt);
            Step = harness.Advance(new(new WorkflowRun
            {
                Id = submission.RequestId, Submission = submission, State = Step.State, Status = WorkflowStatus.Running,
                Steering = steering.ToArray(), Signal = new(WorkflowSignalKind.EffectResult, data), Effect = effect
            }));
        }
    }
}
