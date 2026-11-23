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
    public static Task RunFailedReviewReadOnlyAsync()
    {
        RunInitialFailedReviewReadChecks();
        return Task.CompletedTask;
    }

    private static int RunInitialFailedReviewReadChecks()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Initial failed-review read: " + label);
            checks++;
        }
        const string presentation = "55555555555555555555555555555555";
        const string runId = "plugin-tests-" + presentation;
        var required = new[] { "interface.status", "process.run", "plugin.verify", "plugin.present" };
        var caller = new EventActor(EventActorKind.Service, "cerebral.plugin-tests", "QA");
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralFailedReview", "Desktop", "desktop-plugin.json"));
        var schema = CerebralJson.Element(new { type = "object" });
        var statusSchema = CerebralJson.Element(new
        {
            type = "object", properties = new { requestId = new { type = "string" } },
            required = new[] { "requestId" }, additionalProperties = false
        });
        Fixture Start(IReadOnlyList<string>? requirements = null, bool automatic = true, IReadOnlyList<string>? mounts = null)
        {
            requirements ??= required;
            var fixture = new Fixture(warm: false, input: new { requiredSuccessfulTools = requirements },
                requestId: automatic ? runId : "ordinary-repair-shaped-request", caller: automatic ? caller : null,
                hostAddedRequiredTools: automatic ? requirements : [], hostPluginTestMountIds: mounts ?? []);
            if (fixture.Step.Next is CompleteWorkflow) return fixture;
            fixture.History([Past("failed-review-history", 10, "Earlier retained authoring evidence.")]);
            fixture.Catalog(forcedNames: requirements.Concat(["file.read"]).Distinct(StringComparer.Ordinal).ToArray());
            fixture.Catalog(exact: true, inputSchema: schema,
                inputSchemas: new Dictionary<string, JsonElement> { ["interface.status"] = statusSchema });
            return fixture;
        }
        bool IsRead(Fixture fixture) => fixture.Is("tool") &&
            fixture.Command.Arguments.GetProperty("name").GetString() == "interface.status" &&
            JsonElement.DeepEquals(fixture.Command.Arguments.GetProperty("arguments"), CerebralJson.Element(new { requestId = presentation }));
        string Payload(string returnedId = presentation, string stage = "NativeLoad", string mode = InterfacePluginTestModes.RepairBeforeInstall) =>
            JsonSerializer.Serialize(new
            {
                requestId = returnedId, status = "failed", kind = "plugin", failureStage = stage,
                sourceManifest = source, packageFingerprint = new string('a', 64), error = "Actual fixture native-load diagnostic.",
                tests = new InterfacePluginTestContinuation(runId, "original-run", 1, Mode: mode)
            }, CerebralJson.Options);
        void Read(Fixture fixture, bool success = true, bool partial = false, bool truncated = false,
            string provider = "cerebral.interface", string evidenceId = presentation, string? payload = null) =>
            fixture.Tool(success, payload ?? Payload(), provider: provider, requestId: evidenceId, partial: partial, truncated: truncated);
        bool HasProof(Fixture fixture) => fixture.Step.State.GetProperty("nativeReviewFailure").ValueKind == JsonValueKind.Object;
        JsonObject LegacyModel(Fixture fixture, bool removeStatusSchema = false)
        {
            var state = JsonNode.Parse(fixture.Step.State.GetRawText())!.AsObject();
            state.Remove("initialFailedReviewRead"); state.Remove("initialFailedReviewSchemaLookupAttempted");
            state["phase"] = "model"; state["pendingName"] = null; state["pendingArguments"] = null;
            if (removeStatusSchema)
                state["selected"] = new JsonArray(state["selected"]!.AsArray()
                    .Where(item => item!["name"]!.GetValue<string>() != "interface.status").Select(item => item!.DeepClone()).ToArray());
            return state;
        }
        void RestoreModel(Fixture fixture, JsonObject state)
        {
            var context = new ContextWindow(state["contextRevision"]!.GetValue<long>(), state["exclusionRevision"]!.GetValue<long>(),
                state["contributions"]!.Deserialize<ContextContribution[]>(CerebralJson.Options)!);
            fixture.RestoreStep(new(CerebralJson.Element(state), new InvokeWorkflowCommand("cerebral.host:generate", 1,
                CerebralJson.Element(new { messages = Array.Empty<ModelInputMessage>(), context }))));
        }

        var fresh = Start();
        Check(IsRead(fresh), "fresh accepted repair reads the exact failed presentation before the first model decision");
        Check(fresh.Step.State.GetProperty("initialFailedReviewRead").GetProperty("presentationId").GetString() == presentation &&
            fresh.Step.State.GetProperty("initialFailedReviewRead").GetProperty("effectId").ValueKind == JsonValueKind.Null &&
            fresh.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 && !HasProof(fresh),
            "durable intent alone creates no effect, review identity or successful tool credit");
        Check(fresh.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Null &&
            fresh.Step.State.GetProperty("nativeControlCoverage").GetArrayLength() == 0 &&
            !fresh.Step.State.GetProperty("contributions").EnumerateArray().Any(item => item.GetProperty("category").GetString() == "model-output"),
            "the prelude infers no source/build/installed-control state and requires no model proposal");
        Read(fresh);
        Check(fresh.Is("generate") && HasProof(fresh), "complete exact correlated status evidence is adopted before the first model request");
        var effectId = fresh.Step.State.GetProperty("toolReceipts")[0].GetProperty("effectId").GetString();
        Check(fresh.Step.State.GetProperty("initialFailedReviewRead").GetProperty("effectId").GetString() == effectId &&
            fresh.Step.State.GetProperty("nativeReviewFailure").GetProperty("effectId").GetString() == effectId &&
            fresh.Step.State.GetProperty("nativeReviewFailure").GetProperty("sourceManifest").GetString() == source &&
            fresh.Step.State.GetProperty("nativeReviewFailure").GetProperty("packageFingerprint").GetString() == new string('a', 64),
            "the intent and strict proof bind only the actual effect and immutable source-package identity");
        Check(fresh.MetadataText.Contains("completeFailedReviewEvidenceAdopted", StringComparison.Ordinal) &&
            fresh.LastContributionText.Contains("Actual fixture native-load diagnostic.", StringComparison.Ordinal) &&
            fresh.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Null &&
            fresh.Step.State.GetProperty("nativeControlCoverage").GetArrayLength() == 0,
            "actual diagnostic evidence reaches model context without granting mutation, build or native interaction credit");
        var retainedAttempt = fresh.Step.State.GetProperty("initialFailedReviewRead").GetRawText();
        var retainedReceipt = fresh.Step.State.GetProperty("toolReceipts")[0].GetRawText();
        fresh.Model(new { action = "final", text = "Premature repair completion." });
        Check(fresh.Is("generate") && fresh.Step.State.GetProperty("initialFailedReviewRead").GetRawText() == retainedAttempt &&
            fresh.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "completion correction preserves strict replacement obligations and does not automatically reread");

        foreach (var test in new[] { "failed", "partial", "truncated", "foreign-provider", "evidence-id", "returned-id", "wrong-stage", "wrong-mode" })
        {
            var limited = Start();
            Read(limited, success: test != "failed", partial: test == "partial", truncated: test == "truncated",
                provider: test == "foreign-provider" ? "unrelated.interface" : "cerebral.interface",
                evidenceId: test == "evidence-id" ? new string('6', 32) : presentation,
                payload: test switch
                {
                    "returned-id" => Payload(returnedId: new string('6', 32)),
                    "wrong-stage" => Payload(stage: "PackageInstall"),
                    "wrong-mode" => Payload(mode: "installed"),
                    _ => Payload()
                });
            Check(limited.Is("generate") && !HasProof(limited) &&
                limited.Step.State.GetProperty("initialFailedReviewRead").GetProperty("effectId").GetString() ==
                    limited.Step.State.GetProperty("toolReceipts")[0].GetProperty("effectId").GetString(),
                test + " read retains its actual receipt but earns no failed-review proof or automatic retry");
            limited.Model(new { action = "final", text = "Unproved repair completion." });
            Check(limited.Is("generate") && limited.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1 && !HasProof(limited),
                test + " read is not automatically repeated during a model correction");
        }
        var unknown = Start(); unknown.Uncertain();
        Check(unknown.Step.Next is CompleteWorkflow { Success: false } && !HasProof(unknown) &&
            unknown.Step.State.GetProperty("initialFailedReviewRead").GetProperty("effectId").ValueKind == JsonValueKind.Null &&
            unknown.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "an unknown status effect terminates normally without fabricated receipt, proof or automatic replay");

        Check(Start(automatic: false).Is("generate"), "ordinary user work with repair-shaped requirements gains no automatic presentation authority");
        Check(Start(["interface.status", "plugin.verify", "plugin.present"]).Is("generate"),
            "a service-owned continuation without the complete repair prerequisites gets no automatic repair read");
        var installed = Start(["interface.status", "plugin.controls"], mounts: ["note.main"]);
        Check(installed.Is("tool") && installed.Command.Arguments.GetProperty("name").GetString() == "plugin.controls" &&
            !installed.Step.State.TryGetProperty("initialFailedReviewRead", out _),
            "installed QA retains the accepted mount inspection prelude and gains no failed-review read");
        Check(Start(["interface.status", "plugin.controls"]).Is("generate"),
            "missing installed mounts do not imply repair authority or a guessed native view");

        var legacy = Start(); RestoreModel(legacy, LegacyModel(legacy));
        legacy.Model(new { action = "tool", name = "plugin.present", arguments = new { path = source } });
        Check(IsRead(legacy) && legacy.Step.State.GetProperty("contributions").EnumerateArray().Any(item =>
            item.GetProperty("category").GetString() == "model-output"),
            "a completed legacy proposal is retained as reference and cannot dispatch ahead of the exact review read");
        Read(legacy);
        Check(legacy.Is("generate") && legacy.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1 &&
            legacy.Step.State.GetProperty("toolReceipts")[0].GetProperty("name").GetString() == "interface.status",
            "legacy migration regenerates after the actual read without executing its obsolete presentation proposal");

        foreach (var outcome in new[] { "failed", "partial", "foreign-provider", "complete" })
        {
            var inFlight = Start();
            var inFlightState = JsonNode.Parse(inFlight.Step.State.GetRawText())!.AsObject();
            inFlightState.Remove("initialFailedReviewRead");
            inFlight.RestoreState(CerebralJson.Element(inFlightState));
            Read(inFlight, success: outcome != "failed", partial: outcome == "partial",
                provider: outcome == "foreign-provider" ? "unrelated.interface" : "cerebral.interface");
            Check(inFlight.Is("generate") && HasProof(inFlight) == (outcome == "complete") &&
                inFlight.Step.State.GetProperty("initialFailedReviewRead").GetProperty("effectId").GetString() ==
                    inFlight.Step.State.GetProperty("toolReceipts")[0].GetProperty("effectId").GetString(),
                "legacy in-flight " + outcome + " exact read consumes only its actual terminal effect, with strict proof adoption");
            inFlight.Model(new { action = "final", text = "Premature completion." });
            Check(inFlight.Is("generate") && inFlight.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
                "legacy in-flight " + outcome + " outcome does not cause an automatic exact-read retry");
        }
        var foreignInFlight = Start();
        var foreignInFlightState = JsonNode.Parse(foreignInFlight.Step.State.GetRawText())!.AsObject();
        foreignInFlightState.Remove("initialFailedReviewRead");
        foreignInFlightState["pendingArguments"]!["arguments"]!["requestId"] = new string('6', 32);
        var foreignArguments = CerebralJson.Element(foreignInFlightState["pendingArguments"]);
        foreignInFlight.RestoreStep(new(CerebralJson.Element(foreignInFlightState), new InvokeWorkflowCommand("cerebral.host:tool", 1, foreignArguments)));
        Read(foreignInFlight, success: false, evidenceId: new string('6', 32));
        Check(IsRead(foreignInFlight) && !HasProof(foreignInFlight) &&
            foreignInFlight.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1 &&
            foreignInFlight.Step.State.GetProperty("initialFailedReviewRead").GetProperty("effectId").ValueKind == JsonValueKind.Null,
            "an unrelated legacy read keeps its real receipt and does not consume the accepted repair coordinate's one attempt");

        var deferred = Start();
        var deferredState = LegacyModel(deferred);
        deferredState["phase"] = "describe";
        deferredState["pendingArguments"] = JsonSerializer.SerializeToNode(new { names = new[] { "file.read" } }, CerebralJson.Options);
        deferredState["primaryCatalogNames"] = new JsonArray("file.read");
        deferredState["deferredToolName"] = "file.read";
        deferredState["deferredToolArguments"] = JsonSerializer.SerializeToNode(new { path = source }, CerebralJson.Options);
        deferred.RestoreStep(new(CerebralJson.Element(deferredState), new InvokeWorkflowCommand("cerebral.host:catalog", 1,
            CerebralJson.Element(new { names = new[] { "file.read" } }))));
        deferred.Catalog(exact: true, inputSchema: schema);
        Check(IsRead(deferred) && deferred.Step.State.GetProperty("deferredToolName").ValueKind == JsonValueKind.Null,
            "a legacy deferred schema response cannot dispatch its old file action before the repair prerequisite");
        Read(deferred);
        Check(deferred.Is("generate") && deferred.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "deferred migration regenerates with the real review receipt rather than auto-executing the saved proposal");

        var missingSchema = Start(); RestoreModel(missingSchema, LegacyModel(missingSchema, removeStatusSchema: true));
        missingSchema.Model(new { action = "tool", name = "plugin.present", arguments = new { path = source } });
        Check(missingSchema.Is("catalog") &&
            JsonElement.DeepEquals(missingSchema.Command.Arguments, CerebralJson.Element(new { names = new[] { "interface.status" } })) &&
            missingSchema.Step.State.GetProperty("initialFailedReviewSchemaLookupAttempted").GetBoolean() &&
            !missingSchema.Step.State.TryGetProperty("initialFailedReviewRead", out _),
            "legacy missing-schema preparation is exact, bounded and creates no semantic read intent or proof");
        var prepared = missingSchema.Fork(); prepared.Catalog(exact: true, inputSchema: statusSchema);
        Check(IsRead(prepared), "a valid prepared schema resumes the single exact read through ordinary tool dispatch");
        missingSchema.CatalogFailure("Actual exact schema unavailable.");
        Check(missingSchema.Is("generate") && !HasProof(missingSchema) &&
            missingSchema.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "failed schema preparation reaches normal model recovery without an automatic catalog/read loop");
        missingSchema.Model(new { action = "final", text = "Unproved completion." });
        Check(missingSchema.Is("generate") && missingSchema.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "completion correction after schema failure does not automatically repeat preparation");
        var exhausted = Start(); var exhaustedState = LegacyModel(exhausted, removeStatusSchema: true);
        exhaustedState["descriptions"] = 16; RestoreModel(exhausted, exhaustedState);
        exhausted.Model(new { action = "final", text = "Premature completion." });
        Check(exhausted.Is("generate") && exhausted.Step.State.GetProperty("descriptions").GetInt32() == 16 &&
            exhausted.Step.State.GetProperty("initialFailedReviewSchemaLookupAttempted").GetBoolean() &&
            !exhausted.Step.State.TryGetProperty("initialFailedReviewRead", out _),
            "automatic legacy preparation preserves the ordinary schema quota instead of broadening admission");

        var crowded = Start(); var crowdedState = LegacyModel(crowded);
        var pressureSource = JsonSerializer.SerializeToNode(Past("failed-review-pressure", 11, "Earlier retained evidence."), CerebralJson.Options)!;
        crowdedState["contributions"]!.AsArray().Add(pressureSource);
        var compactJson = new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false };
        // Test admitted migration pressure while reserving room for actual policy/command metadata.
        var targetBytes = WorkflowContract.MaximumStateBytes - 3 * 8192 + 2048;
        var beforePadding = JsonSerializer.SerializeToUtf8Bytes(crowdedState, compactJson).Length;
        var pressurePart = pressureSource["messages"]![0]!["content"]![0]!;
        pressurePart["text"] = pressurePart["text"]!.GetValue<string>() + new string('s', targetBytes - beforePadding);
        Check(JsonSerializer.SerializeToUtf8Bytes(crowdedState, compactJson).Length == targetBytes && targetBytes < WorkflowContract.MaximumStateBytes,
            "the pressure fixture measures a real bounded state above the existing receipt/source high-water mark");
        RestoreModel(crowded, crowdedState);
        crowded.Model(new { action = "tool", name = "plugin.present", arguments = new { path = source } });
        var proposal = crowded.Context.Contributions.Single(item => item.Category == "model-output");
        Check(crowded.Is("compact") && !crowded.Step.State.TryGetProperty("initialFailedReviewRead", out _) && proposal.ExactReadHandle is not null,
            "context pressure compacts the actual completed proposal before committing any automatic read intent");
        crowded.CompactSuccess();
        Check(IsRead(crowded) && crowded.Step.State.GetProperty("checkpoint").GetProperty("coveredSources").EnumerateArray().Any(item =>
            item.GetProperty("id").GetString() == proposal.Id),
            "successful compaction preserves proposal provenance and then dispatches the exact read once");
        Read(crowded);
        Check(crowded.Is("generate") && HasProof(crowded), "compacted legacy state adopts only its actual complete read evidence");

        fresh.RejectContext(ContextProjectionStatus.NeedsCompaction); fresh.CompactSuccess();
        Check(fresh.Is("generate") && fresh.Step.State.GetProperty("initialFailedReviewRead").GetRawText() == retainedAttempt &&
            fresh.Step.State.GetProperty("toolReceipts")[0].GetRawText() == retainedReceipt && HasProof(fresh),
            "later compaction preserves the exact attempt, receipt and strict proof without a new automatic read");
        fresh.Model(new { action = "blocked", reason = "Fixture boundary reached." });
        Check(fresh.Step.Next is CompleteWorkflow { Success: false }, "model-owned limitation reporting remains available after the prelude");

        foreach (var forge in new[] { "presentation", "pending-extra", "receipt", "phase" })
        {
            var invalid = Start();
            var state = JsonNode.Parse(invalid.Step.State.GetRawText())!.AsObject();
            switch (forge)
            {
                case "presentation": state["initialFailedReviewRead"]!["presentationId"] = new string('6', 32); break;
                case "pending-extra": state["pendingArguments"]!["arguments"]!["mountId"] = "foreign.mount"; break;
                case "receipt": state["initialFailedReviewRead"]!["effectId"] = "effect-unretained"; break;
                case "phase": state["phase"] = "model"; break;
            }
            invalid.Resume(CerebralJson.Element(state));
            Check(invalid.Step.Next is CompleteWorkflow { Success: false }, forge + " forged durable intent fails closed without dispatch");
        }
        Console.WriteLine("Initial failed-review read: " + checks + " deterministic harness checks passed; no native UI, provider dispatch or model execution.");
        return checks;
    }
}
