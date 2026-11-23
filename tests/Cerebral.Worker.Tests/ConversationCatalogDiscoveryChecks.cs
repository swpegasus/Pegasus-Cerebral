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

/// <summary>Pure policy fixtures only. No Worker, provider, semantic tool, model or filesystem call.</summary>
public static class ConversationCatalogDiscoveryChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Conversation catalog page: " + name); count++; }
        var edition = ConversationAgentHarness.CreateToolCatalogPageEdition();
        Check(edition.Descriptor.Version == "1.0.4-tool-catalog-page.1" && edition.Descriptor.Id == ConversationAgentHarness.Id &&
            edition.Descriptor.ToolReceiptConsumer is { SchemaVersion: 1, Supported: true }, "explicit edition retains receipt/context behavior");
        using var entry = new ToolCatalogPageConversationServicePlugin();
        using var ordinary = new ConversationServicePlugin();
        Check(entry.Harnesses.Any(h => h.Descriptor.Version == edition.Descriptor.Version) &&
            ordinary.Harnesses.All(h => h.Descriptor.Version != edition.Descriptor.Version),
            "new factory export does not change ordinary default export");

        var legacy = new Fixture(new ConversationAgentHarness());
        Check(legacy.Command.CommandVersion == 1 && !legacy.Step.State.TryGetProperty("toolCatalogPage", out _),
            "legacy bootstrap keeps v1 and has no new state bytes");
        legacy.Catalog(["fixture.legacy"], remaining: 0);
        Check(!legacy.HasDiscoveryAction, "legacy generation has no discovery action");
        legacy.Model(new { action = "discover", query = "fixture" });
        Check(legacy.Is("generate"), "legacy action admission rejects discover without a catalog effect");
        var receiptEdition = new Fixture(ConversationAgentHarness.CreateToolContextProjectionEdition());
        Check(receiptEdition.Command.CommandVersion == 1 && !receiptEdition.Step.State.TryGetProperty("toolCatalogPage", out _),
            "existing receipt-aware edition remains v1");
        var missingMarker = Warm(); missingMarker.Resume(state => state.Remove("toolCatalogPage"));
        Check(missingMarker.Step.Next is CompleteWorkflow { Success: false }, "new edition refuses missing marker without migration");
        var wrongRun = Warm(); wrongRun.Resume(state => state["toolCatalogPage"]!["runId"] = "another-run");
        Check(wrongRun.Step.Next is CompleteWorkflow { Success: false }, "new marker belongs to the exact accepted run");
        var markerOnLegacy = new Fixture(new ConversationAgentHarness());
        markerOnLegacy.Resume(state => state["toolCatalogPage"] = new JsonObject { ["schemaVersion"] = 2, ["runId"] = "catalog-page-fixture" });
        Check(markerOnLegacy.Step.Next is CompleteWorkflow { Success: false }, "legacy edition refuses new saved state");

        var f = new Fixture(edition);
        Check(f.Is("catalog") && f.Command.CommandVersion == 2, "new bootstrap selects same-ID catalog v2");
        var first = Enumerable.Range(0, 64).Select(i => $"fixture.visible.{i:D3}").ToArray();
        f.Catalog(first, 7, helpers: [new { id = "helper", revision = 1, name = "Helper", purpose = "Fixture" }]);
        Check(f.Is("generate") && f.Step.State.GetProperty("discovery").GetArrayLength() == 64 &&
            f.Step.State.GetProperty("toolCatalogPage").GetProperty("remaining").GetInt32() == 7 &&
            f.HasDiscoveryAction && f.ModelText.Contains("remainingLookupAttempts", StringComparison.Ordinal),
            "first bounded page exposes honest continuation and the separate lookup budget");
        Check(f.Step.State.GetProperty("descriptions").GetInt32() == 0, "initial metadata bootstrap does not spend a model lookup");
        f.Model(new { action = "discover", afterName = first[^1] });
        Check(f.Is("catalog") && f.Command.CommandVersion == 2 &&
            f.Step.State.GetProperty("descriptions").GetInt32() == 1, "continuation spends the existing shared lookup counter");
        f.Catalog(Enumerable.Range(64, 7).Select(i => $"fixture.visible.{i:D3}").ToArray(), 0, omitHelpers: true);
        Check(f.Is("generate") && f.Step.State.GetProperty("discovery").GetArrayLength() == 7 &&
            f.Step.State.GetProperty("helpers").GetArrayLength() == 1 &&
            f.Step.State.GetProperty("toolCatalogPage").GetProperty("remaining").GetInt32() == 0 &&
            f.Step.State.GetProperty("toolCatalogPage").GetProperty("nextAfterName").ValueKind == JsonValueKind.Null,
            "later page replaces the name view, retains helper metadata and truthfully terminates");
        f.Model(new { action = "describe", names = new[] { "fixture.visible.070" } });
        Check(f.Is("catalog") && f.Command.CommandVersion == 2, "on-demand exact describe also uses v2");
        f.Catalog(["fixture.visible.070"], 0);
        Check(f.Step.State.GetProperty("selected").GetArrayLength() == 1, "exact schema is selected without a semantic effect");
        f.Model(new { action = "tool", name = "fixture.visible.070", arguments = new { } });
        Check(f.Is("tool") && f.Command.CommandVersion == 1, "catalog v2 does not change semantic command version");
        f.Tool(success: false);
        var receipt = f.Step.State.GetProperty("toolReceipts").GetRawText();
        f.Model(new { action = "discover", query = "another" });
        f.Catalog([], 0);
        Check(f.Step.State.GetProperty("toolReceipts").GetRawText() == receipt &&
            receipt.Contains("Failed", StringComparison.Ordinal), "discovery preserves failed receipt evidence");

        var cache = Warm();
        for (var i = 0; i < 9; i++)
        {
            cache.Model(new { action = "describe", names = new[] { "fixture.cache." + i } });
            cache.Catalog(["fixture.cache." + i], 0);
        }
        Check(cache.Step.State.GetProperty("selected").GetArrayLength() == 8 &&
            cache.Step.State.GetProperty("selected")[0].GetProperty("name").GetString() == "fixture.cache.1",
            "exact schemas retain latest-eight cache behavior");
        for (var i = 9; i < 16; i++)
        {
            cache.Model(new { action = "discover", query = "query" + i });
            cache.Catalog([], 0);
        }
        cache.Model(new { action = "discover", query = "one-more" });
        Check(cache.Is("generate") && cache.Step.State.GetProperty("descriptions").GetInt32() == 16 &&
            cache.LastContributionText.Contains("lookup budget", StringComparison.Ordinal) &&
            cache.LastContributionText.Contains("not proven unavailable", StringComparison.Ordinal),
            "page and describe share sixteen attempts without reset");

        foreach (var bad in new[] {
            "{\"action\":\"discover\",\"query\":\"x\",\"query\":\"y\"}",
            "{\"action\":\"discover\",\"names\":[\"fixture.x\"]}",
            "{\"action\":\"discover\",\"query\":null}",
            "{\"action\":\"discover\",\"query\":\" x\"}",
            "{\"action\":\"discover\",\"afterName\":true}",
            JsonSerializer.Serialize(new { action = "discover", query = new string('x', 65) }),
            JsonSerializer.Serialize(new { action = "discover", afterName = new string('x', 257) }) })
        {
            var malformed = Warm(); malformed.ModelRaw(bad);
            Check(malformed.Is("generate") && malformed.Step.State.GetProperty("descriptions").GetInt32() == 0,
                "malformed/unknown/mixed discovery action emits no lookup");
        }
        var repeated = Warm(); repeated.Model(new { action = "discover", query = "find" }); repeated.Catalog([], 0);
        repeated.Model(new { action = "discover", query = "FIND" });
        Check(repeated.Is("generate") && repeated.Step.State.GetProperty("descriptions").GetInt32() == 1,
            "same semantic query/position does not repeat a host read");
        var repairs = Warm(); repairs.ModelRaw("{\"action\":\"unknown\"}");
        var repairState = repairs.Step.State.GetProperty("actionCorrections").GetRawText();
        repairs.Model(new { action = "discover", query = "repair" });
        Check(repairs.Is("catalog") && repairs.Step.State.GetProperty("actionCorrections").GetRawText() == repairState,
            "metadata request does not reset action-correction budget");

        foreach (var change in new Action<JsonObject>[] {
            data => data["schemaVersion"] = 1,
            data => data["mode"] = "names",
            data => data["request"] = new JsonObject { ["query"] = "different" },
            data => data["remaining"] = 0,
            data => data["nextAfterName"] = "fixture.wrong",
            data => data["tools"] = new JsonArray(),
            data => data["tools"] = new JsonArray(data["tools"]![0]!.DeepClone(), data["tools"]![0]!.DeepClone()),
            data => data["helpers"] = new JsonArray(new JsonObject { ["id"] = "helper", ["revision"] = 1, ["name"] = null, ["purpose"] = "Fixture" }),
            data => data["unknown"] = true,
            data => data["tools"]![0]!["inputSchema"] = new JsonObject { ["type"] = "object" } })
        {
            var invalid = Warm(); invalid.Model(new { action = "discover", query = "fixture" });
            invalid.Catalog(["fixture.next"], 2, mutate: change);
            Check(invalid.Step.Next is CompleteWorkflow stopped && !stopped.Success,
                "malformed/stale/nonadvancing response is not adopted or executed");
        }
        var wrongVersion = new Fixture(edition); wrongVersion.Catalog(["fixture.x"], 0, versionOverride: 1);
        Check(wrongVersion.Step.Next is CompleteWorkflow, "new edition refuses v1 catalog result");
        var legacyV2 = new Fixture(new ConversationAgentHarness()); legacyV2.Catalog(["fixture.x"], 0, versionOverride: 2);
        Check(legacyV2.Step.Next is CompleteWorkflow, "old edition refuses v2 catalog result");
        var wrongExact = Warm(); wrongExact.Model(new { action = "describe", names = new[] { "fixture.requested" } });
        wrongExact.Catalog(["fixture.other"], 0);
        Check(wrongExact.Step.Next is CompleteWorkflow { Success: false }, "exact schema results cannot substitute another name");
        var behindCursor = Warm(); behindCursor.Model(new { action = "discover", afterName = "fixture.z" });
        behindCursor.Catalog(["fixture.a"], 0);
        Check(behindCursor.Step.Next is CompleteWorkflow { Success: false }, "terminal page also obeys the exclusive ordinal cursor");
        var unknown = Warm(); unknown.Model(new { action = "discover", query = "unknown" });
        unknown.Result(WorkflowEffectState.Unknown, WorkflowContract.EmptyObject, "Unconfirmed fixture read");
        Check(unknown.Step.Next is CompleteWorkflow && !unknown.Step.State.GetProperty("toolReceipts").EnumerateArray().Any(),
            "uncertain metadata outcome is retained without retry or fabricated receipt");
        var failed = Warm(); failed.Model(new { action = "discover", query = "failed" });
        failed.Result(WorkflowEffectState.Failed, WorkflowContract.EmptyObject, "Actual catalog failure");
        failed.Model(new { action = "discover", query = "failed" });
        Check(failed.Is("generate") && failed.Step.State.GetProperty("descriptions").GetInt32() == 1,
            "failed discovery request is not silently repeated");
        return count;
    }

    private static Fixture Warm()
    { var f = new Fixture(ConversationAgentHarness.CreateToolCatalogPageEdition()); f.Catalog(["fixture.start"], 0); return f; }

    private sealed class Fixture
    {
        private readonly ConversationAgentHarness harness;
        private readonly WorkflowSubmission submission;
        private int sequence;
        public WorkflowStep Step { get; private set; }
        public InvokeWorkflowCommand Command => (InvokeWorkflowCommand)Step.Next;
        public string ModelText => Command.Arguments.GetRawText();
        public bool HasDiscoveryAction => Command.Arguments.GetProperty("responseSchema").GetProperty("oneOf")
            .EnumerateArray().Any(choice => choice.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "discover");
        public string LastContributionText => ((TextContentPart)Command.Arguments.GetProperty("context")
            .Deserialize<ContextWindow>(CerebralJson.Options)!.Contributions.Last().Messages.Single().Content.Single()).Text;
        public bool Is(string suffix) => Step.Next is InvokeWorkflowCommand command && command.CommandId == "cerebral.host:" + suffix;
        public void Resume(Action<JsonObject> mutate)
        {
            var state = JsonNode.Parse(Step.State.GetRawText())!.AsObject(); mutate(state);
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission,
                Status = WorkflowStatus.Running, State = JsonSerializer.SerializeToElement(state) }));
        }
        public Fixture(ConversationAgentHarness harness)
        {
            this.harness = harness;
            submission = new("catalog-page-fixture", "conversation", "fixture", harness.Descriptor.Id, harness.Descriptor.Version,
                "Inspect only granted metadata.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running }));
            var history = new Dictionary<string, object> { ["context"] = new ContextWindow(100, 0, [], null),
                ["hasOlder"] = false, ["beforeSequence"] = null!, ["snapshotSequence"] = 100 };
            if (harness.Descriptor.ToolReceiptConsumer is not null)
                history["receiptProjectionAdmissions"] = new { schemaVersion = 1, items = Array.Empty<object>() };
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(history));
        }
        public void Catalog(string[] names, int remaining, object[]? helpers = null, bool omitHelpers = false,
            Action<JsonObject>? mutate = null, int? versionOverride = null)
        {
            var exact = Command.Arguments.TryGetProperty("names", out _);
            var rows = names.Select(name => {
                var row = new JsonObject { ["name"] = name, ["description"] = "Fixture metadata", ["capability"] = "fixture" };
                if (exact) row["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };
                return (JsonNode)row;
            }).ToArray();
            var data = new JsonObject { ["tools"] = new JsonArray(rows), ["truncated"] = remaining > 0 };
            if (Command.CommandVersion == 2)
            {
                data["schemaVersion"] = 2; data["mode"] = exact ? "names" : "page";
                data["request"] = JsonNode.Parse(Command.Arguments.GetRawText()); data["remaining"] = remaining;
                data["nextAfterName"] = remaining > 0 && names.Length > 0 ? names[^1] : null;
            }
            if (!omitHelpers) data["helpers"] = JsonNode.Parse(CerebralJson.Element(helpers ?? []).GetRawText());
            mutate?.Invoke(data);
            Result(WorkflowEffectState.Succeeded, JsonSerializer.SerializeToElement(data), versionOverride: versionOverride);
        }
        public void Model(object action) => ModelRaw(CerebralJson.Element(action).GetRawText());
        public void ModelRaw(string text)
        {
            var window = Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
            var metadata = new PreparedModelMetadata("fixture", "fixture", "1", "fixture", 100, 4096, 512, "deterministic-fixture");
            var projection = new ContextProjection(ContextProjectionStatus.Ready, window.Revision, window.ExclusionRevision,
                window.Contributions.Select(c => new ContextSourceReference(c.Id, c.SourceRevision)).ToArray(), [], metadata);
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new { generation = new ModelGenerationResult(ModelGenerationFinish.Stop, text),
                prepared = metadata, outputOmitted = false, context = projection }));
        }
        public void Tool(bool success) => Result(success ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed,
            CerebralJson.Element(new { name = Command.Arguments.GetProperty("name").GetString(), content = "Fixture failure", truncated = false,
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Failed, "fixture"), followUpRequired = false }));
        public void Result(WorkflowEffectState state, JsonElement data, string? error = null, int? versionOverride = null)
        {
            var command = Command;
            var effect = new WorkflowEffect("fixture-effect-" + ++sequence, command.CommandId,
                versionOverride ?? command.CommandVersion, command.Arguments, WorkflowReplayPolicy.Never, state, data, error);
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running,
                State = Step.State, Signal = new(WorkflowSignalKind.EffectResult, data), Effect = effect }));
        }
    }
}
