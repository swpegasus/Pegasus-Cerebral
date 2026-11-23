using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Pure host/harness exchanges. No model, database, microphone or installed state.</summary>
internal static class ConversationReasoningAdjustmentChecks
{
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };

    public static void Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Conversation reasoning: " + name);
            checks.Add(name);
        }

        var legacy = new Fixture(null);
        Check(legacy.Command.CommandVersion == 1 && !HasReasoningAction(legacy.Command.Arguments) &&
            !legacy.Step.State.TryGetProperty("reasoningAdjustment", out _), "legacy remains version one without policy or action");
        legacy.Model(new { action = "reasoning", tokens = 2048 });
        Check(legacy.Command.CommandVersion == 1 && !legacy.Command.Arguments.TryGetProperty("reasoningTokens", out _),
            "unapproved model action cannot opt a legacy task in");

        var required = new Fixture(Policy(128, 4096, 1024));
        Check(required.Command.CommandVersion == 3 && HasReasoningAction(required.Command.Arguments) &&
            !required.Command.Arguments.TryGetProperty("reasoningTokens", out _), "opted task starts with host default and version three");
        required.Model(new { action = "reasoning", tokens = 2048 });
        Check(required.Command.CommandVersion == 3 && required.Command.Arguments.GetProperty("reasoningTokens").GetInt32() == 2048,
            "valid model action reaches next host generation as a bounded request");
        required.Model(new { action = "describe", names = new[] { "fixture.read" } });
        required.Catalog();
        Check(required.Command.Arguments.GetProperty("reasoningTokens").GetInt32() == 2048,
            "requested budget survives an intervening discovery effect");

        var alwaysOn = new Fixture(Policy(128, 4096, 1024));
        alwaysOn.Model(new { action = "reasoning", tokens = 0 });
        Check(!alwaysOn.Command.Arguments.TryGetProperty("reasoningTokens", out _), "always-on cannot request zero");
        var excess = new Fixture(Policy(128, 4096, 1024));
        excess.Model(new { action = "reasoning", tokens = 8192 });
        Check(!excess.Command.Arguments.TryGetProperty("reasoningTokens", out _), "model cannot raise owner ceiling");

        var adaptive = new Fixture(Policy(0, 2048, 0));
        adaptive.Model(new { action = "reasoning", tokens = 512 });
        adaptive.Model(new { action = "reasoning", tokens = 0 });
        Check(adaptive.Command.Arguments.GetProperty("reasoningTokens").GetInt32() == 0,
            "adaptive owner policy permits returning to zero");

        var same = new Fixture(Policy(128, 4096, 1024));
        same.Model(new { action = "reasoning", tokens = 1024 });
        Check(!same.Step.State.TryGetProperty("requestedReasoningTokens", out _), "same-budget loop is corrected without staging another value");

        foreach (var bad in new[]
        {
            Policy(128, 4096, 1024, runId: "other-run"),
            Policy(128, 4096, 1024, profileId: "other-profile"),
            Policy(128, 4096, 0), Policy(0, 127, 0), Policy(0, 4096, 1)
        })
        {
            var invalid = new Fixture(bad);
            Check(invalid.Step.Next is CompleteWorkflow { Success: false }, "invalid policy refused before generation");
        }

        var wrongType = JsonNode.Parse(Policy(128, 4096, 1024).GetRawText())!;
        wrongType["maximumTokens"] = "4096";
        Check(new Fixture(Element(wrongType)).Step.Next is CompleteWorkflow { Success: false }, "numeric string policy is refused");

        var mismatched = new Fixture(Policy(128, 4096, 1024));
        mismatched.Model(new { action = "final", text = "fixture complete" }, version: 1);
        Check(mismatched.Step.Next is CompleteWorkflow { Success: false }, "old effect cannot satisfy version-three pending generation");

        var alias = new Fixture(Policy(128, 4096, 1024));
        var state = JsonNode.Parse(alias.Step.State.GetRawText())!.AsObject();
        state["ReasoningAdjustment"] = state["reasoningAdjustment"]!.DeepClone();
        state.Remove("reasoningAdjustment");
        alias.Restore(Element(state));
        Check(alias.Step.Next is CompleteWorkflow { Success: false }, "case alias in saved policy is refused");

        var complete = new Fixture(Policy(128, 4096, 1024));
        complete.Model(new { action = "reasoning", tokens = 2048 });
        complete.Model(new { action = "final", text = "Source confirmed with actual evidence." });
        Check(complete.Step.Next is CompleteWorkflow { Success: true }, "normal completion remains reachable after adjustment");
        Console.WriteLine($"Conversation reasoning: {checks.Count} pure exchange checks passed.");
    }

    private static bool HasReasoningAction(JsonElement arguments) => arguments.GetProperty("responseSchema")
        .GetProperty("oneOf").EnumerateArray().Any(variant =>
            variant.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "reasoning");

    private static JsonElement Policy(int minimum, int maximum, int value,
        string runId = "reasoning-fixture-run", string profileId = "reasoning-fixture-profile") => Element(new
        {
            schemaVersion = 1, runId, profileId, profileRevision = 3,
            minimumTokens = minimum, maximumTokens = maximum, defaultTokens = value
        });

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    private sealed class Fixture
    {
        private readonly ConversationAgentHarness harness = new();
        private readonly WorkflowSubmission submission = new("reasoning-fixture-run", "reasoning-fixture-chat",
            "reasoning-fixture-profile", ConversationAgentHarness.Id, ConversationAgentHarness.Version,
            "Read the relevant source before answering.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);
        private int sequence;
        public WorkflowStep Step { get; private set; }
        public InvokeWorkflowCommand Command => (InvokeWorkflowCommand)Step.Next;

        public Fixture(JsonElement? policy)
        {
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission,
                Status = WorkflowStatus.Running }));
            var data = JsonSerializer.SerializeToNode(new { context = new ContextWindow(0, 0, []),
                hasOlder = false, beforeSequence = (long?)null, snapshotSequence = 0 }, Json)!.AsObject();
            if (policy is { } accepted) data["reasoningAdjustment"] = JsonNode.Parse(accepted.GetRawText());
            Result(Element(data));
            if (Step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:catalog" }) Catalog();
        }

        public void Catalog() => Result(Element(new
        {
            tools = new[] { new { name = "fixture.read", description = "Read a fixture.", capability = "fixture.read",
                inputSchema = new { type = "object", properties = new { }, additionalProperties = false } } },
            truncated = false
        }));

        public void Model(object action, int? version = null)
        {
            var window = Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(Json)!;
            var metadata = new PreparedModelMetadata("fixture-instance", "fixture-adapter", "1.0.0", "fixture-model",
                128, 8192, 512, "pure-fixture") { SafetyReserveTokens = 128 };
            Result(Element(new
            {
                generation = new ModelGenerationResult(ModelGenerationFinish.Stop, JsonSerializer.Serialize(action, Json)),
                prepared = metadata, outputOmitted = false,
                context = new ContextProjection(ContextProjectionStatus.Ready, window.Revision, window.ExclusionRevision,
                    window.Contributions.Select(source => new ContextSourceReference(source.Id, source.SourceRevision)).ToArray(), [], metadata)
            }), version);
        }

        public void Restore(JsonElement state)
        {
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission,
                Status = WorkflowStatus.Running, State = state }));
        }

        private void Result(JsonElement data, int? version = null)
        {
            var command = Command;
            var effect = new WorkflowEffect("reasoning-fixture-effect-" + ++sequence, command.CommandId,
                version ?? command.CommandVersion, command.Arguments, WorkflowReplayPolicy.Never,
                WorkflowEffectState.Succeeded, data);
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission,
                Status = WorkflowStatus.Running, State = Step.State,
                Signal = new(WorkflowSignalKind.EffectResult, data), Effect = effect }));
        }
    }
}
