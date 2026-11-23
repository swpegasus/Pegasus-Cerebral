using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunQaSchemaBootstrapOnlyAsync()
    {
        RunQaSchemaBootstrapChecks();
        RunInitialQaObservationChecks();
        return Task.CompletedTask;
    }

    private static int RunQaSchemaBootstrapChecks()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("QA schema bootstrap: " + name);
            checks++;
        }
        const string presentation = "99999999999999999999999999999999", mount = "note.main";
        const string diagnostic = "Actual accepted/current descriptor mismatch; control schema unavailable.";
        var schema = CerebralJson.Element(new { type = "object" });
        Fixture Start(bool hints = false)
        {
            var required = new[] { "interface.status", "plugin.controls" };
            var fixture = new Fixture(warm: false, input: new { requiredSuccessfulTools = required },
                requestId: "plugin-tests-" + presentation, caller: new(EventActorKind.Service, "cerebral.plugin-tests"),
                hostAddedRequiredTools: required, hostPluginTestMountIds: [mount]);
            fixture.History([Past("prior-schema-evidence", 1, "Earlier retained evidence.")]);
            fixture.Catalog(forcedNames: hints ? [.. required, "fixture.optional"] : required,
                prerequisites: hints ? new Dictionary<string, string[]> { ["plugin.controls"] = ["fixture.optional"] } : null);
            return fixture;
        }
        bool Marked(Fixture fixture) => fixture.Step.State.TryGetProperty("initialQaSchemaLookupAttempted", out var value) && value.GetBoolean();
        bool EmptyCoverage(Fixture fixture) => fixture.Step.State.GetProperty("nativeControlCoverage").GetArrayLength() == 0 &&
            fixture.Step.State.GetProperty("initialQaObservations").GetArrayLength() == 0 &&
            fixture.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0;
        void RestoreModel(Fixture fixture, int descriptions, bool keepSchema, bool marker)
        {
            var state = JsonNode.Parse(fixture.Step.State.GetRawText())!.AsObject();
            state["phase"] = "model"; state["pendingName"] = null; state["pendingArguments"] = null;
            state["initialQaObservations"] = new JsonArray(); state["descriptions"] = descriptions;
            state["initialQaSchemaLookupAttempted"] = marker;
            if (!keepSchema) state["selected"] = new JsonArray();
            var context = new ContextWindow(state["contextRevision"]!.GetValue<long>(), state["exclusionRevision"]!.GetValue<long>(),
                state["contributions"]!.Deserialize<ContextContribution[]>(CerebralJson.Options)!);
            fixture.RestoreStep(new(CerebralJson.Element(state), new InvokeWorkflowCommand("cerebral.host:generate", 1,
                CerebralJson.Element(new { messages = Array.Empty<ModelInputMessage>(), context }))));
        }
        var fresh = Start(hints: true);
        Check(fresh.Is("catalog") && Marked(fresh) && fresh.Step.State.GetProperty("descriptions").GetInt32() == 1 &&
            fresh.Command.Arguments.GetProperty("names").EnumerateArray().Select(item => item.GetString())
                .SequenceEqual(new[] { "interface.status", "plugin.controls" }),
            "the first mandatory lookup records one intent and optional hints do not add a bootstrap retry dependency");
        fresh.CatalogFailure(diagnostic);
        Check(fresh.Step.Next is CompleteWorkflow { Success: false } && Marked(fresh) &&
            fresh.Step.State.GetProperty("descriptions").GetInt32() == 1 && fresh.Step.State.GetProperty("repairs").GetInt32() == 0 && EmptyCoverage(fresh),
            "one failed first lookup stops with no second catalog, model correction or native coverage");
        Check(fresh.Step.State.GetProperty("contributions").EnumerateArray().Any(item =>
            item.GetProperty("category").GetString() == "catalog-result" && item.GetRawText().Contains("Actual accepted/current descriptor mismatch", StringComparison.Ordinal)) &&
            fresh.Summary.Contains("catalog evidence is retained", StringComparison.Ordinal),
            "the actual failed catalog diagnostic and exact source remain retained at the truthful stop");
        var legacy = Start();
        var pending = JsonNode.Parse(legacy.Step.State.GetRawText())!.AsObject();
        pending.Remove("initialQaSchemaLookupAttempted");
        legacy.RestoreStep(legacy.Step with { State = CerebralJson.Element(pending) });
        legacy.CatalogFailure(diagnostic);
        Check(legacy.Step.Next is CompleteWorkflow { Success: false } && Marked(legacy) && EmptyCoverage(legacy) &&
            legacy.Step.State.GetProperty("descriptions").GetInt32() == 1,
            "a restored pre-upgrade exact lookup consumes its real terminal attempt without re-querying");
        var missing = Start();
        missing.Catalog(exact: true, forcedNames: ["interface.status"], inputSchema: schema);
        Check(missing.Step.Next is CompleteWorkflow { Success: false } && Marked(missing) && EmptyCoverage(missing),
            "a succeeded but incomplete exact catalog cannot fabricate the missing control schema or observation");
        var unknown = Start(); unknown.Uncertain();
        Check(unknown.Step.Next is CompleteWorkflow { Success: false } && Marked(unknown) && EmptyCoverage(unknown) &&
            unknown.Summary.Contains("Outcome unknown", StringComparison.Ordinal),
            "an unknown catalog stops without retry, preparation substitution or native dispatch");
        var quota = Start(); quota.Catalog(exact: true, inputSchema: schema);
        RestoreModel(quota, 16, keepSchema: false, marker: false);
        quota.Model(new { action = "final", text = "Done." });
        Check(quota.Step.Next is CompleteWorkflow { Success: false } && quota.Step.State.GetProperty("descriptions").GetInt32() == 16 &&
            quota.Step.State.GetProperty("repairs").GetInt32() == 0 && EmptyCoverage(quota) && quota.Summary.Contains("sixteen-selection limit", StringComparison.Ordinal),
            "quota sixteen with no control schema stops directly instead of entering recursive action corrections");
        var quotaCatalog = Start();
        var quotaCatalogState = JsonNode.Parse(quotaCatalog.Step.State.GetRawText())!.AsObject();
        quotaCatalogState["phase"] = "catalog"; quotaCatalogState["descriptions"] = 16;
        quotaCatalogState["initialQaSchemaLookupAttempted"] = false;
        quotaCatalogState["selected"] = new JsonArray(); quotaCatalogState["pendingArguments"] = new JsonObject();
        quotaCatalog.RestoreStep(quotaCatalog.Step with { State = CerebralJson.Element(quotaCatalogState) });
        quotaCatalog.Catalog(forcedNames: ["interface.status", "plugin.controls"]);
        Check(quotaCatalog.Step.Next is CompleteWorkflow { Success: false } &&
            quotaCatalog.Step.State.GetProperty("descriptions").GetInt32() == 16 &&
            quotaCatalog.Step.State.GetProperty("repairs").GetInt32() == 0 && EmptyCoverage(quotaCatalog),
            "a legacy broad-catalog result at quota sixteen stops before a bootstrap selection or model correction");
        var usable = Start(); usable.Catalog(exact: true, inputSchema: schema);
        RestoreModel(usable, 16, keepSchema: true, marker: true);
        usable.Model(new { action = "final", text = "Old model decision." });
        Check(usable.Is("tool") && usable.Command.Arguments.GetProperty("name").GetString() == "plugin.controls" &&
            usable.Command.Arguments.GetProperty("arguments").GetProperty("operation").GetString() == "inspect" &&
            usable.Step.State.GetProperty("descriptions").GetInt32() == 16,
            "a selected usable schema permits the exact native observation at quota sixteen without another lookup");
        var evicted = Start(); evicted.Catalog(exact: true, inputSchema: schema);
        RestoreModel(evicted, 1, keepSchema: false, marker: true);
        evicted.RejectContext(ContextProjectionStatus.NeedsCompaction);
        Check(evicted.Is("compact") && Marked(evicted), "compaction retains the consumed schema intent before any follow-up");
        evicted.CompactSuccess();
        Check(evicted.Step.Next is CompleteWorkflow { Success: false } && Marked(evicted) && EmptyCoverage(evicted) &&
            evicted.Step.State.GetProperty("descriptions").GetInt32() == 1,
            "a compacted state with an absent schema cannot restart its consumed host preparation attempt");
        var ordinary = new Fixture();
        var forged = JsonNode.Parse(ordinary.Step.State.GetRawText())!.AsObject(); forged["initialQaSchemaLookupAttempted"] = true;
        ordinary.Resume(CerebralJson.Element(forged));
        Check(ordinary.Step.Next is CompleteWorkflow { Success: false },
            "a schema marker outside the accepted installed-QA coordinates fails closed");
        Console.WriteLine("QA schema bootstrap: " + checks + " deterministic checks passed; no native/provider/model execution.");
        return checks;
    }
}
