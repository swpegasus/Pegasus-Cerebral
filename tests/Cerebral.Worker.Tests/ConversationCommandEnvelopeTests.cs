using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker.Tests;

/// <summary>Pure deterministic transitions; no model, tool dispatcher, filesystem or live Source execution.</summary>
internal static class ConversationCommandEnvelopeTests
{
    private const int HighWater = WorkflowContract.MaximumStateBytes - 3 * 8192;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };

    public static Task RunAsync()
    {
        var passes = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passes++;
        }
        void Validate(WorkflowStep step)
        {
            WorkflowSchema.Validate(step.State, WorkflowContract.EmptyObject);
            if (step.Next is InvokeWorkflowCommand command)
                WorkflowSchema.Validate(command.Arguments, WorkflowContract.EmptyObject);
            else if (step.Next is CompleteWorkflow complete)
                WorkflowSchema.Validate(complete.Result, WorkflowContract.EmptyObject);
            Check(true, "actual engine data boundaries accept the returned state and command/result separately");
        }

        // Calibrate a dense but admitted catalog rather than assuming state bytes equal command
        // bytes. The reproduced old envelope restores only the optional descriptions in metadata.
        Fixture Dense(int target, string expectedCommand)
        {
            for (var descriptionLength = 500; descriptionLength <= 1800; descriptionLength += 100)
            {
                var fixture = new Fixture(schemaDescriptionBytes: 8192);
                fixture.Pad(target, descriptionLength);
                fixture.SuccessfulRead();
                if (fixture.Step.Next is InvokeWorkflowCommand command &&
                    command.CommandId == "cerebral.host:" + expectedCommand &&
                    fixture.Step.State.GetProperty("discoveryNamesOnly").GetBoolean() &&
                    ExpandedDiscoveryBytes(fixture.Step) > WorkflowContract.MaximumStateBytes)
                    return fixture;
            }
            throw new InvalidOperationException("The calibrated dense catalog did not reproduce optional-description envelope pressure.");
        }

        var generation = Dense(HighWater - 4 * 1024, "generate");
        Validate(generation.Step);
        Check(Bytes(generation.Step.State) < HighWater,
            "generation degrades an oversized command even when saved state remains below high-water");
        Check(ExpandedDiscoveryBytes(generation.Step) > WorkflowContract.MaximumStateBytes,
            "restoring the optional discovery descriptions reproduces the rejected full generation envelope");
        VerifyExactReferences(generation, Check);
        Check(generation.Step.State.GetProperty("lastCompactionRevision").GetInt64() == -1,
            "optional discovery degradation alone does not pretend a compaction completed");

        var compact = Dense(HighWater + 4 * 1024, "compact");
        Validate(compact.Step);
        Check(Bytes(compact.Step.State) > HighWater && Bytes(compact.Step.State) <= WorkflowContract.MaximumStateBytes,
            "the successful-read transition compacts from fitting state above the durable high-water");
        Check(ExpandedDiscoveryBytes(compact.Step) > WorkflowContract.MaximumStateBytes,
            "the full compaction command would exceed 128 KiB without optional discovery degradation");
        Check(compact.Step.State.GetProperty("lastCompactionRevision").GetInt64() == compact.Context.Revision &&
            compact.Step.State.GetProperty("contextRevision").GetInt64() == compact.Context.Revision,
            "compaction pins the exact revised context actually sent after metadata degradation");
        VerifyExactReferences(compact, Check);
        var readSource = compact.Context.Contributions.Last();
        compact.CompactSuccess();
        Validate(compact.Step);
        Check(compact.Is("generate") && compact.Context.Checkpoint is not null &&
            Bytes(compact.Step.State) < HighWater,
            "a valid compact receipt removes reducible bulk and resumes a committable generation");
        Check(JsonElement.DeepEquals(Element(compact.Context.Contributions.Single(source => source.Id == readSource.Id)), Element(readSource)),
            "the exact latest successful-read contribution survives checkpoint adoption");
        VerifyExactReferences(compact, Check);

        // The system policy also occupies the command envelope. Calibrate above high-water
        // against that actual envelope so compact guidance edits cannot turn this fitting case
        // into an oversized command that correctly degrades optional discovery descriptions.
        Fixture? ordinaryHighWater = null;
        for (var extra = 4 * 1024; extra >= 512; extra -= 512)
        {
            var candidate = new Fixture();
            candidate.Pad(HighWater + extra, 0);
            candidate.SuccessfulRead();
            if (candidate.Is("compact") && !candidate.Step.State.GetProperty("discoveryNamesOnly").GetBoolean())
            { ordinaryHighWater = candidate; break; }
        }
        if (ordinaryHighWater is null) throw new InvalidOperationException("No fitting ordinary high-water command was calibrated.");
        Validate(ordinaryHighWater.Step);
        Check(Bytes(ordinaryHighWater.Step.State) > HighWater &&
            Bytes(ordinaryHighWater.Command.Arguments) <= WorkflowContract.MaximumStateBytes &&
            !ordinaryHighWater.Step.State.GetProperty("discoveryNamesOnly").GetBoolean(),
            "existing high-water compaction preserves descriptions when its complete command already fits");

        var sourceCapacity = new Fixture();
        sourceCapacity.FillSources(ContextValidation.MaximumSourceCount - 5);
        sourceCapacity.SuccessfulRead();
        Validate(sourceCapacity.Step);
        Check(sourceCapacity.Is("compact") && Bytes(sourceCapacity.Step.State) < HighWater &&
            sourceCapacity.Context.Contributions.Count > ContextValidation.MaximumSourceCount - 8,
            "the source-capacity compaction guard remains effective independently of byte pressure");

        var requiredOverflow = new Fixture();
        requiredOverflow.Pad(126 * 1024, 0, required: true);
        requiredOverflow.SuccessfulRead(small: true);
        Validate(requiredOverflow.Step);
        Check(requiredOverflow.Step.Next is CompleteWorkflow { Success: false } &&
            Bytes(requiredOverflow.Step.State) <= WorkflowContract.MaximumStateBytes,
            "an irreducible oversized command stops deliberately with fitting preserved state");
        Check(requiredOverflow.Step.State.GetProperty("toolReceipts").EnumerateArray().Last().GetProperty("name").GetString() == "file.read" &&
            requiredOverflow.Step.State.GetProperty("contributions").GetRawText().Contains("KEEP_CURRENT_EVIDENCE", StringComparison.Ordinal),
            "a deliberate envelope stop preserves the actual successful read and exact current evidence");
        Console.WriteLine($"Conversation command envelopes: {passes} deterministic assertions passed.");
        return Task.CompletedTask;
    }

    private static void VerifyExactReferences(Fixture fixture, Action<bool, string> check)
    {
        var arguments = fixture.Command.Arguments;
        var metadata = Metadata(arguments);
        check(Text(arguments.GetProperty("messages")[0]) == fixture.Policy,
            "envelope degradation preserves exact conversation policy");
        check(JsonElement.DeepEquals(metadata.GetProperty("selectedToolSchemas"), fixture.Selected),
            "selected exact tool schemas remain unchanged in model/compactor metadata");
        check(metadata.GetProperty("availableTools").EnumerateArray().All(tool =>
            tool.EnumerateObject().All(property => property.Name == "name")) ||
            !fixture.Step.State.GetProperty("discoveryNamesOnly").GetBoolean(),
            "only optional discovery descriptions disappear from the bounded catalog view");
        check(fixture.Context.Contributions.Any(source => source.Requirement == ContextRequirement.Required &&
            source.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                .Any(part => part.Text.Contains("KEEP_CURRENT_EVIDENCE", StringComparison.Ordinal))),
            "the latest exact successful-read evidence remains required");
    }

    private static int ExpandedDiscoveryBytes(WorkflowStep step)
    {
        var command = (InvokeWorkflowCommand)step.Next;
        var node = JsonNode.Parse(command.Arguments.GetRawText())!.AsObject();
        var part = node["messages"]!.AsArray()[1]!["content"]!.AsArray()[0]!.AsObject();
        var text = part["text"]!.GetValue<string>();
        const string marker = "\nAccepted metadata and host evidence:\n";
        var split = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        if (split < marker.Length) throw new InvalidOperationException("Missing exact host metadata framing.");
        var metadata = JsonNode.Parse(text[split..])!.AsObject();
        metadata["availableTools"] = JsonNode.Parse(step.State.GetProperty("discovery").GetRawText());
        part["text"] = text[..split] + metadata.ToJsonString(Json);
        return Bytes(Element(node));
    }

    private static JsonElement Metadata(JsonElement arguments)
    {
        var text = Text(arguments.GetProperty("messages")[1]);
        const string marker = "\nAccepted metadata and host evidence:\n";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        if (start < marker.Length) throw new InvalidOperationException("Missing exact metadata framing.");
        return JsonDocument.Parse(text[start..]).RootElement.Clone();
    }
    private static string Text(JsonElement message) =>
        string.Concat(message.GetProperty("content").EnumerateArray().Select(part => part.GetProperty("text").GetString()));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    private static int Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json).Length;

    private sealed class Fixture
    {
        private readonly ConversationAgentHarness harness = new();
        private readonly WorkflowSubmission submission;
        private int effectSequence;
        public WorkflowStep Step { get; private set; }
        public InvokeWorkflowCommand Command => (InvokeWorkflowCommand)Step.Next;
        public ContextWindow Context => Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(Json)!;
        public string Policy { get; }
        public JsonElement Selected { get; }
        public bool Is(string name) => Step.Next is InvokeWorkflowCommand command && command.CommandId == "cerebral.host:" + name;
        private static readonly string ReadPath = Path.Combine(Path.GetTempPath(), "cerebral-envelope-fixture.cs");
        private readonly JsonElement readSchema;

        public Fixture(int schemaDescriptionBytes = 0)
        {
            readSchema = Element(new { type = "object", properties = new { path = new
                { type = "string", description = new string('s', schemaDescriptionBytes) } },
                required = new[] { "path" }, additionalProperties = false });
            submission = new("envelope-fixture", "envelope-chat", "fixture", ConversationAgentHarness.Id,
                ConversationAgentHarness.Version, "Preserve the requested fixture feature", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running }));
            var past = Source("past", "earlier exact reference", ContextRequirement.Reducible, true);
            Result(Element(new { context = new ContextWindow(100, 0, new[] { past }), hasOlder = false,
                beforeSequence = (long?)null, snapshotSequence = 100 }));
            Catalog(exact: false);
            Model(new { action = "describe", names = new[] { "file.read" } });
            Catalog(exact: true);
            Policy = Text(Command.Arguments.GetProperty("messages")[0]);
            Selected = Step.State.GetProperty("selected").Clone();
            Model(new { action = "tool", name = "file.read", arguments = new { path = ReadPath } });
            if (!Is("tool")) throw new InvalidOperationException("Fixture did not dispatch its pure read intent.");
        }

        public void Pad(int targetBytes, int descriptionLength, bool required = false)
        {
            var node = JsonNode.Parse(Step.State.GetRawText())!.AsObject();
            if (descriptionLength > 0)
            {
                var phrase = "quote \"example\" C:\\fixture\\reference ";
                var description = string.Concat(Enumerable.Repeat(phrase, descriptionLength / phrase.Length + 1))[..descriptionLength];
                var catalog = new JsonArray();
                catalog.Add(Element(new { name = "file.read", description = "read file", capability = "filesystem.read" }).Deserialize<JsonNode>(Json));
                for (var index = 0; index < 16; index++)
                    catalog.Add(Element(new { name = "fixture.reference." + index, description, capability = "diagnostics" }).Deserialize<JsonNode>(Json));
                if (Bytes(catalog) > 48 * 1024) throw new InvalidOperationException("Fixture discovery exceeds its admitted catalog bound.");
                node["discovery"] = catalog;
            }
            var sources = node["contributions"]!.AsArray();
            if (required) foreach (var source in sources)
                source!["requirement"] = JsonValue.Create("Required");
            var indexSequence = 0;
            while (Bytes(node) < targetBytes)
            {
                var remaining = targetBytes - Bytes(node);
                if (remaining < 512)
                {
                    var part = sources[0]!["messages"]![0]!["content"]![0]!;
                    part["text"] = part["text"]!.GetValue<string>() + new string('p', remaining);
                    break;
                }
                var source = Source("bulk-" + ++indexSequence, "", required ? ContextRequirement.Required : ContextRequirement.Reducible);
                sources.Add(Element(source).Deserialize<JsonNode>(Json));
                remaining = targetBytes - Bytes(node);
                if (remaining < 0) throw new InvalidOperationException("Fixture cannot calibrate its exact state bound.");
                sources[^1]!["messages"]![0]!["content"]![0]!["text"] = new string('p', Math.Min(8192, remaining));
            }
            if (Bytes(node) != targetBytes) throw new InvalidOperationException("Fixture state byte calibration changed.");
            Step = Step with { State = Element(node) };
        }

        public void FillSources(int count)
        {
            var node = JsonNode.Parse(Step.State.GetRawText())!.AsObject();
            var sources = node["contributions"]!.AsArray();
            for (var index = sources.Count; index < count; index++)
                sources.Add(Element(Source("capacity-" + index, "prior bounded evidence", ContextRequirement.Reducible)).Deserialize<JsonNode>(Json));
            Step = Step with { State = Element(node) };
        }

        public void SuccessfulRead(bool small = false) => Result(Element(new
        {
            name = "file.read",
            content = JsonSerializer.Serialize(new { path = ReadPath, sha256 = new string('a', 64), startLine = 1,
                totalLines = 1, content = "KEEP_CURRENT_EVIDENCE" + (small ? "" : new string('e', 1024)) }, Json),
            truncated = false, evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "fixture"),
            followUpRequired = false
        }));

        public void CompactSuccess()
        {
            var window = Context;
            var checkpoint = new ContextCheckpoint("fixture-checkpoint", window.Revision, window.ExclusionRevision,
                ContextValidation.GetCoveredManifest(window), "Bounded earlier fixture work", "effect-" + (effectSequence + 1),
                ContextValidation.CheckpointBeforeCurrentInput(window))
                { SourceLocators = ContextValidation.GetCoveredLocators(window) };
            Result(Element(new { checkpoint, before = Metadata(1800), after = Metadata(256), passes = 1, usage = Array.Empty<object>() }));
        }

        private static ContextContribution Source(string id, string text, ContextRequirement requirement, bool before = false) =>
            new(id, ConversationServicePlugin.ModuleId, "fixture-reference", 1, requirement,
                [new("user", [new TextContentPart(text)])], BeforeCurrentInput: before);

        private void Catalog(bool exact) => Result(Element(new
        {
            tools = new[] { new { name = "file.read", description = "read file", capability = "filesystem.read",
                inputSchema = exact ? readSchema : (JsonElement?)null } }, truncated = false
        }));

        private void Model(object action)
        {
            var window = Context;
            Result(Element(new { generation = new ModelGenerationResult(ModelGenerationFinish.Stop, JsonSerializer.Serialize(action, Json)),
                prepared = Metadata(128), outputOmitted = false,
                context = new ContextProjection(ContextProjectionStatus.Ready, window.Revision, window.ExclusionRevision,
                    window.Contributions.Select(source => new ContextSourceReference(source.Id, source.SourceRevision)).ToArray(), [], Metadata(128)) }));
        }
        private static PreparedModelMetadata Metadata(int tokens) =>
            new("fixture-instance", "fixture-adapter", "1.0.0", "fixture-model", tokens, 2048, 256, "deterministic-fixture") { SafetyReserveTokens = 128 };

        private void Result(JsonElement data)
        {
            var command = Command;
            var effect = new WorkflowEffect("effect-" + ++effectSequence, command.CommandId, command.CommandVersion, command.Arguments,
                WorkflowReplayPolicy.Never, WorkflowEffectState.Succeeded, data);
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission,
                Status = WorkflowStatus.Running, State = Step.State, Signal = new(WorkflowSignalKind.EffectResult, data), Effect = effect }));
        }
    }
}
