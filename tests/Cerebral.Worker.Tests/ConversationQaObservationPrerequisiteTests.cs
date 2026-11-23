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
    // Existing scenarios script their own later observations. Retain an explicit failed
    // deterministic bootstrap receipt, without manufacturing package or behavior coverage.
    private static void CompleteInitialQaObservationFixture(Fixture fixture)
    {
        var count = 0;
        while (fixture.Is("tool") && fixture.Command.Arguments.GetProperty("name").GetString() == "plugin.controls" &&
            fixture.Command.Arguments.GetProperty("arguments").GetProperty("operation").GetString() == "inspect")
        {
            if (++count > WorkflowContract.MaximumPluginTestMounts) throw new InvalidOperationException("Fixture observation loop exceeded the mount bound.");
            fixture.Tool(false, "This fixture has not supplied its native view yet.",
                provider: "cerebral.plugin.controls", requestId: Guid.NewGuid().ToString("N"));
        }
    }

    private static int RunInitialQaObservationChecks()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Initial QA observation: " + label);
            checks++;
        }
        const string presentation = "11111111111111111111111111111111";
        const string field = "33333333333333333333333333333333";
        const string mountA = "note.main", mountB = "note.second";
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralQaObservation", "desktop-plugin.json"));
        var caller = new EventActor(EventActorKind.Service, "cerebral.plugin-tests", "QA");
        var schema = CerebralJson.Element(new { type = "object" });
        Fixture Start(IReadOnlyList<string> mounts, bool repair = false, string? runId = null, EventActor? actor = null)
        {
            var required = repair ? new[] { "interface.status", "process.run", "plugin.verify", "plugin.present" }
                : ["interface.status", "plugin.controls"];
            var fixture = new Fixture(warm: false, input: new { requiredSuccessfulTools = required },
                requestId: runId ?? "plugin-tests-" + presentation, caller: actor ?? caller,
                hostAddedRequiredTools: required, hostPluginTestMountIds: mounts);
            if (fixture.Step.Next is CompleteWorkflow) return fixture;
            fixture.History([Past("retained-history", 10, "Earlier authoring evidence.")]);
            fixture.Catalog(forcedNames: required);
            fixture.Catalog(exact: true, inputSchema: schema);
            return fixture;
        }
        bool IsInspect(Fixture fixture, string mount) => fixture.Is("tool") &&
            fixture.Command.Arguments.GetProperty("name").GetString() == "plugin.controls" &&
            JsonElement.DeepEquals(fixture.Command.Arguments.GetProperty("arguments"),
                CerebralJson.Element(new { presentationId = "reviewed", mountId = mount, operation = "inspect" }));
        void Observe(Fixture fixture, string mount, bool success = true, bool limited = false,
            bool malformed = false, string provider = "cerebral.plugin.controls")
        {
            var receipt = Guid.NewGuid().ToString("N");
            var snapshot = new PluginControlSnapshot(mount, ["Ready"], [new("Save", true, true)], limited)
                { Fields = [new(field, "Observed café value.", true, true, false)] };
            var content = malformed ? "{\"status\":\"failed\",\"error\":\"Actual observation unavailable.\"}" :
                JsonSerializer.Serialize(new
                {
                    receipt, presentationId = presentation, mountId = mount, operation = "inspect",
                    status = success ? "completed" : "failed", nativeStatus = "completed",
                    sourceManifest = source, packageFingerprint = new string('a', 64),
                    observation = new PluginControlObservation(snapshot, snapshot, false), error = success ? null : "Actual inspection failed."
                }, CerebralJson.Options);
            fixture.Tool(success, content, provider: provider, requestId: receipt);
        }
        var two = Start([mountA, mountB]);
        Check(IsInspect(two, mountA), "the exact first approved mount is inspected after catalog and before any model decision");
        Observe(two, mountA);
        Check(IsInspect(two, mountB) && two.Step.State.GetProperty("initialQaObservations").GetArrayLength() == 2,
            "multi-mount preparation moves to the next frozen mount with one retained intent per mount");
        Observe(two, mountB);
        Check(two.Is("generate") && two.Step.State.GetProperty("initialQaObservations").EnumerateArray().All(item =>
            item.GetProperty("effectId").ValueKind == JsonValueKind.String) &&
            two.Step.State.GetProperty("toolReceipts").GetArrayLength() == 2,
            "the first model decision follows both actual retained inspection receipts");
        var observedContext = string.Join("\n", two.Context.Contributions.SelectMany(item => item.Messages)
            .SelectMany(item => item.Content).OfType<TextContentPart>().Select(item => item.Text));
        Check(observedContext.Contains(field, StringComparison.Ordinal) &&
            observedContext.Contains(JsonSerializer.Serialize("Observed café value.", CerebralJson.Options), StringComparison.Ordinal) &&
            observedContext.Contains("Save", StringComparison.Ordinal) &&
            two.Context.Contributions.Any(item => item.Id == "retained-history"),
            "real field/button identities and current values enter the model context alongside retained history");
        Check(two.Step.State.GetProperty("nativeControlCoverage").GetArrayLength() == 2 &&
            two.Step.State.GetProperty("nativeControlCoverage").EnumerateArray().All(item =>
                !item.GetProperty("interactionObserved").GetBoolean()),
            "initial inspection establishes package observations and gives no interaction credit");
        two.Model(new { action = "tool", name = "interface.status", arguments = new { } });
        two.Tool(true, "{}", provider: "cerebral.interface", requestId: Guid.NewGuid().ToString("N"));
        two.Model(new { action = "final", text = "All behavior works." });
        Check(two.Is("generate") && two.LastContributionText.Contains("Inspection alone", StringComparison.Ordinal),
            "an interactive view still requires real behavior interaction after bootstrap inspection");

        foreach (var result in new[] { "failed", "limited", "foreign" })
        {
            var fixture = Start([mountA]);
            Observe(fixture, mountA, success: result != "failed", limited: result == "limited",
                malformed: result == "failed", provider: result == "foreign" ? "foreign.provider" : "cerebral.plugin.controls");
            Check(fixture.Is("generate") && fixture.Step.State.GetProperty("nativeControlCoverage").GetArrayLength() == 0 &&
                fixture.Step.State.GetProperty("initialQaObservations")[0].GetProperty("effectId").ValueKind == JsonValueKind.String,
                result + " observation remains exact receipt evidence without complete coverage or an automatic retry");
            fixture.Model(new { action = "final", text = "Done." });
            Check(fixture.Is("generate") && fixture.Step.State.GetProperty("initialQaObservations").GetArrayLength() == 1,
                result + " observation does not auto-repeat when model completion needs correction");
        }
        var maximum = Enumerable.Range(0, WorkflowContract.MaximumPluginTestMounts).Select(index => "note.mount" + index).ToArray();
        var bounded = Start(maximum);
        foreach (var mount in maximum.Take(4))
        {
            if (!IsInspect(bounded, mount)) throw new InvalidOperationException("Maximum-mount fixture left its bounded inspect sequence at " + mount + ".");
            Observe(bounded, mount);
        }
        var boundedEvidence = JsonNode.Parse(bounded.Command.Arguments.GetProperty("messages")[1].GetProperty("content")[0]
            .GetProperty("text").GetString()!.Split("Accepted metadata and host evidence:\n", StringSplitOptions.None)[1])!;
        Check(bounded.Is("generate") && bounded.Step.State.GetProperty("initialQaObservations").GetArrayLength() == 4 &&
            boundedEvidence["initialQaObservation"]!["notAttemptedMountIds"]!.AsArray().Count == maximum.Length - 4 &&
            boundedEvidence["initialQaObservation"]!["limitation"]!.GetValue<string>().Contains("four-attempt limit", StringComparison.Ordinal),
            "the maximum accepted multi-mount set stops after four real attempts and reports every remaining mount without coverage");
        Check(Start([.. maximum, "note.overflow"]).Step.Next is CompleteWorkflow { Success: false },
            "an over-bound frozen mount list is rejected before any initial observation");
        var uncertain = Start([mountA]);
        uncertain.Uncertain();
        Check(uncertain.Step.Next is CompleteWorkflow { Success: false } &&
            uncertain.Step.State.GetProperty("nativeControlCoverage").GetArrayLength() == 0,
            "an unknown observation preserves the ordinary uncertainty boundary and dispatches no follow-up");

        Check(Start([]).Is("generate"), "an absent frozen mount list grants no deterministic observation or guessed mount");
        var repair = Start([], repair: true);
        Check(repair.Is("tool") && repair.Command.Arguments.GetProperty("name").GetString() == "interface.status" &&
            JsonElement.DeepEquals(repair.Command.Arguments.GetProperty("arguments"), CerebralJson.Element(new { requestId = presentation })),
            "failed-review repair reads the exact review instead of inspecting a nonexistent installed mount");
        Check(Start([mountA], actor: new(EventActorKind.Service, "unrelated.service")).Step.Next is CompleteWorkflow { Success: false },
            "an unrelated service cannot forge the reviewed bootstrap identity");

        var legacy = Start([mountA]);
        var state = JsonNode.Parse(legacy.Step.State.GetRawText())!.AsObject();
        state.Remove("initialQaObservations");
        state["phase"] = "model"; state["pendingName"] = null; state["pendingArguments"] = null;
        var context = new ContextWindow(state["contextRevision"]!.GetValue<long>(), state["exclusionRevision"]!.GetValue<long>(),
            state["contributions"]!.Deserialize<ContextContribution[]>(CerebralJson.Options)!);
        legacy.RestoreStep(new(CerebralJson.Element(state), new InvokeWorkflowCommand("cerebral.host:generate", 1,
            CerebralJson.Element(new { messages = Array.Empty<ModelInputMessage>(), context }))));
        legacy.Model(new { action = "tool", name = "plugin.present", arguments = new { path = source } });
        Check(IsInspect(legacy, mountA) && legacy.Step.State.GetProperty("contributions").EnumerateArray().Any(item =>
            item.GetProperty("category").GetString() == "model-output"),
            "a completed pre-upgrade decision is retained as reference and cannot dispatch its proposed presentation before inspection");
        Observe(legacy, mountA);
        Check(legacy.Is("generate") && legacy.Step.State.GetProperty("toolReceipts").EnumerateArray().All(item =>
            item.GetProperty("name").GetString() == "plugin.controls"),
            "legacy migration regenerates from actual observation instead of automatically executing the old proposal");

        var crowded = Start([mountA, mountB]);
        Observe(crowded, mountA);
        var retainedReceipt = crowded.Step.State.GetProperty("toolReceipts")[0].GetRawText();
        var retainedAttempt = crowded.Step.State.GetProperty("initialQaObservations")[0].GetRawText();
        var crowdedState = JsonNode.Parse(crowded.Step.State.GetRawText())!.AsObject();
        crowdedState["initialQaObservations"] = new JsonArray(crowdedState["initialQaObservations"]![0]!.DeepClone());
        crowdedState["phase"] = "model"; crowdedState["pendingName"] = null; crowdedState["pendingArguments"] = null;
        var pressureSource = JsonSerializer.SerializeToNode(Past("qa-migration-pressure", 11, "Earlier retained evidence."), CerebralJson.Options)!;
        crowdedState["contributions"]!.AsArray().Add(pressureSource);
        var compactJson = new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false };
        var highWater = WorkflowContract.MaximumStateBytes - 3 * 8192;
        // Leave command-envelope room for the actual policy and exact observation metadata;
        // this case tests migration above high-water, not deliberate irreducible overflow.
        var targetBytes = highWater + 2048;
        var beforePadding = JsonSerializer.SerializeToUtf8Bytes(crowdedState, compactJson).Length;
        var pressurePart = pressureSource["messages"]![0]!["content"]![0]!;
        pressurePart["text"] = pressurePart["text"]!.GetValue<string>() + new string('s', targetBytes - beforePadding);
        var paddedBytes = JsonSerializer.SerializeToUtf8Bytes(crowdedState, compactJson).Length;
        Check(paddedBytes == targetBytes && paddedBytes < WorkflowContract.MaximumStateBytes,
            "restored QA migration state is measured above the existing high-water mark and below the absolute bound");
        var crowdedWindow = new ContextWindow(crowdedState["contextRevision"]!.GetValue<long>(), crowdedState["exclusionRevision"]!.GetValue<long>(),
            crowdedState["contributions"]!.Deserialize<ContextContribution[]>(CerebralJson.Options)!);
        crowded.RestoreStep(new(CerebralJson.Element(crowdedState), new InvokeWorkflowCommand("cerebral.host:generate", 1,
            CerebralJson.Element(new { messages = Array.Empty<ModelInputMessage>(), context = crowdedWindow }))));
        crowded.Model(new { action = "tool", name = "plugin.present", arguments = new { path = source } });
        var proposal = crowded.Context.Contributions.Single(item => item.Category == "model-output");
        Check(crowded.Is("compact") && crowded.Step.State.GetProperty("initialQaObservations").GetArrayLength() == 1 &&
            crowded.Step.State.GetProperty("toolReceipts")[0].GetRawText() == retainedReceipt &&
            crowded.Step.State.GetProperty("initialQaObservations")[0].GetRawText() == retainedAttempt &&
            proposal.ExactReadHandle is not null,
            "the admitted old proposal is retained and compacts before any new inspect intent or tool dispatch");
        var proposalId = proposal.Id;
        crowded.CompactSuccess();
        Check(IsInspect(crowded, mountB) && crowded.Step.State.GetProperty("initialQaObservations").GetArrayLength() == 2 &&
            crowded.Step.State.GetProperty("toolReceipts")[0].GetRawText() == retainedReceipt &&
            crowded.Step.State.GetProperty("checkpoint").GetProperty("coveredSources").EnumerateArray().Any(item =>
                item.GetProperty("id").GetString() == proposalId),
            "successful compaction preserves old proposal provenance and dispatches the remaining exact mount once");
        Observe(crowded, mountB);
        Check(crowded.Is("generate") && crowded.Step.State.GetProperty("initialQaObservations").GetArrayLength() == 2 &&
            crowded.Step.State.GetProperty("toolReceipts").GetArrayLength() == 2 &&
            crowded.Step.State.GetProperty("toolReceipts")[0].GetRawText() == retainedReceipt &&
            crowded.Step.State.GetProperty("initialQaObservations")[1].GetProperty("effectId").GetString() ==
                crowded.Step.State.GetProperty("toolReceipts")[1].GetProperty("effectId").GetString(),
            "the compacted legacy migration regenerates after its real observation with prior receipt and attempt provenance unchanged");

        var invalid = Start([mountA]);
        var forged = JsonNode.Parse(invalid.Step.State.GetRawText())!.AsObject();
        forged["initialQaObservations"]![0]!["mountId"] = "foreign.mount";
        invalid.Resume(CerebralJson.Element(forged));
        Check(invalid.Step.Next is CompleteWorkflow { Success: false },
            "a saved bootstrap intent outside the exact frozen mount list fails closed");

        var nullIntent = Start([mountA]);
        var nullState = JsonNode.Parse(nullIntent.Step.State.GetRawText())!.AsObject();
        nullState["initialQaObservations"] = new JsonArray((JsonNode?)null);
        nullIntent.Resume(CerebralJson.Element(nullState));
        Check(nullIntent.Step.Next is CompleteWorkflow { Success: false },
            "a malformed saved observation list fails closed without an unhandled null or native request");
        Console.WriteLine("Initial QA observation: " + checks + " deterministic harness checks passed; no native UI or model execution.");
        return checks;
    }
}
