using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunStaleControlCasOnlyAsync()
    {
        Console.WriteLine("Stale native text preconditions: " + RunStaleControlCasChecks() +
            " deterministic harness checks passed; no native UI, provider dispatch or model execution.");
        return Task.CompletedTask;
    }

    private static int RunStaleControlCasChecks()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Stale native text precondition: " + label);
            checks++;
        }
        const string presentation = "55555555555555555555555555555555", mount = "editor.main";
        const string field = "66666666666666666666666666666666";
        const string actual = "zebra\napple\nmango\nbanana\ncherry", replacement = actual + "\n\n\n";
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralStaleControlCas", "desktop-plugin.json"));
        var fingerprint = new string('a', 64);
        Fixture Start()
        {
            string[] required = ["interface.status", "plugin.controls"];
            var fixture = new Fixture(warm: false, input: new { requiredSuccessfulTools = required },
                requestId: "plugin-tests-" + presentation, caller: new(EventActorKind.Service, "cerebral.plugin-tests"),
                hostAddedRequiredTools: required, hostPluginTestMountIds: [mount]);
            fixture.History([]); fixture.Catalog(forcedNames: required);
            fixture.Catalog(exact: true, inputSchema: CerebralJson.Element(new { type = "object" }));
            CompleteInitialQaObservationFixture(fixture);
            fixture.Model(new { action = "tool", name = "interface.status", arguments = new { requestId = "reviewed" } });
            fixture.Tool(true, "{}", provider: "cerebral.interface", requestId: presentation);
            return fixture;
        }
        PluginControlSnapshot Before(string text = actual, bool limited = false, bool fieldLimited = false,
            bool visible = true, bool enabled = true, bool readOnly = false, bool duplicate = false, string id = field,
            string snapshotMount = mount) => new(snapshotMount, ["Ready"], [new("Transform", true, true)], limited)
            { Fields = duplicate ? [new(id, text, enabled, visible, readOnly, fieldLimited),
                new(id, text, enabled, visible, readOnly, fieldLimited)] : [new(id, text, enabled, visible, readOnly, fieldLimited)] };
        void Inspect(Fixture fixture, string text = actual)
        {
            fixture.Model(new { action = "tool", name = "plugin.controls",
                arguments = new { presentationId = presentation, mountId = mount, operation = "inspect" } });
            var receipt = Guid.NewGuid().ToString("N");
            fixture.Tool(true, JsonSerializer.Serialize(new
            {
                receipt, presentationId = presentation, mountId = mount, operation = "inspect", status = "completed",
                nativeStatus = "completed", sourceManifest = source, packageFingerprint = fingerprint,
                observation = new PluginControlObservation(Before(text), Before(text))
            }, CerebralJson.Options), provider: "cerebral.plugin.controls", requestId: receipt);
        }
        void Edit(Fixture fixture, string expected = "", string target = replacement, bool success = false,
            string status = "rejected", string native = "rejected", ToolExecutionOutcome outcome = ToolExecutionOutcome.Rejected,
            PluginControlSnapshot? before = null, PluginControlSnapshot? after = null, bool noObservation = false,
            bool textSet = false, bool click = false, bool recreated = false, bool? partial = null, bool truncated = false,
            string provider = "cerebral.plugin.controls", bool wrongReceipt = false, string returnedPresentation = presentation,
            string returnedMount = mount, string? returnedSource = null, string? returnedFingerprint = null,
            string? rawFlag = null, string? rawFlagValue = null)
        {
            fixture.Model(new { action = "tool", name = "plugin.controls",
                arguments = new { presentationId = presentation, mountId = mount, operation = "setText",
                    fieldId = field, expectedText = expected, text = target } });
            var receipt = Guid.NewGuid().ToString("N");
            var content = JsonSerializer.Serialize(new
            {
                receipt, presentationId = returnedPresentation, mountId = returnedMount, operation = "setText", status,
                nativeStatus = native, sourceManifest = returnedSource ?? source, packageFingerprint = returnedFingerprint ?? fingerprint,
                observation = noObservation ? null : new PluginControlObservation(before ?? Before(), after, click, recreated) { TextSet = textSet },
                assertion = new { status = success ? "passed" : status == "failed" ? "failed" : "inconclusive",
                    expectedTexts = new[] { target }, missingTexts = status == "failed" ? new[] { target } : Array.Empty<string>() },
                error = success ? null : "Fixture supplied a failed retained native result; diagnostic text is not classification evidence."
            }, CerebralJson.Options);
            if (rawFlag is not null)
            {
                var body = JsonNode.Parse(content)!.AsObject();
                var observed = body["observation"]!.AsObject();
                if (rawFlagValue == "omitted") observed.Remove(rawFlag);
                else observed[rawFlag] = rawFlagValue switch
                {
                    "null" => null, "true" => JsonValue.Create(true), "number" => JsonValue.Create(0),
                    _ => JsonValue.Create("false")
                };
                content = JsonSerializer.Serialize(body, CerebralJson.Options);
            }
            fixture.Tool(success, content, outcome: outcome, provider: provider,
                requestId: wrongReceipt ? Guid.NewGuid().ToString("N") : receipt, partial: partial, truncated: truncated);
        }
        void InvokeTransform(Fixture fixture, string text)
        {
            fixture.Model(new { action = "tool", name = "plugin.controls",
                arguments = new { presentationId = presentation, mountId = mount, operation = "invoke", buttonName = "Transform" } });
            var receipt = Guid.NewGuid().ToString("N");
            fixture.Tool(true, JsonSerializer.Serialize(new
            {
                receipt, presentationId = presentation, mountId = mount, operation = "invoke", status = "completed",
                nativeStatus = "completed", sourceManifest = source, packageFingerprint = fingerprint,
                observation = new PluginControlObservation(Before(text), Before(text), ClickDelivered: true)
            }, CerebralJson.Options), provider: "cerebral.plugin.controls", requestId: receipt);
        }
        JsonElement Failures(Fixture fixture) => fixture.Step.State.GetProperty("nativeControlFailures");
        JsonElement Coverage(Fixture fixture) => fixture.Step.State.GetProperty("nativeControlCoverage");
        string Receipt(Fixture fixture, string effect) => fixture.Step.State.GetProperty("toolReceipts")
            .EnumerateArray().Single(item => item.GetProperty("effectId").GetString() == effect).GetRawText();

        var corrected = Start(); Inspect(corrected); Edit(corrected);
        Check(Failures(corrected).GetArrayLength() == 0, "proven no-effect stale precondition is not an unresolved functional failure");
        var failedReceipt = corrected.Step.State.GetProperty("toolReceipts").EnumerateArray().Last();
        var failedEffect = failedReceipt.GetProperty("effectId").GetString()!;
        var originalReceipt = failedReceipt.GetRawText();
        Check(failedReceipt.GetProperty("state").GetString() == "Failed" && failedReceipt.GetProperty("outcome").GetString() == "Rejected",
            "original failed rejection remains immutable in the actual receipt ledger");
        Check(!Coverage(corrected)[0].GetProperty("interactionObserved").GetBoolean(),
            "rejected delivery earns no interaction credit from its complete before-view");
        Check(corrected.SystemText.Contains("stale setText precondition rejected before native delivery", StringComparison.Ordinal),
            "generic policy directs exact current-precondition recovery without source defect or interaction claim");
        corrected.Model(new { action = "final", text = "The editor was exercised." });
        Check(corrected.Is("generate") && corrected.LastContributionText.Contains("successful current-run receipts", StringComparison.Ordinal),
            "a later rejected edit cannot inherit the earlier successful inspect for mandatory tool completion");
        Inspect(corrected);
        corrected.Model(new { action = "final", text = "Fresh inspection proves the editor was exercised." });
        Check(corrected.Is("generate") && corrected.LastContributionText.Contains("Inspection alone", StringComparison.Ordinal),
            "fresh successful inspection after rejected edit still cannot finalize an interactive mount");
        corrected.Model(new { action = "tool", name = "plugin.controls",
            arguments = new { presentationId = presentation, mountId = mount, operation = "setText",
                fieldId = field, expectedText = "", text = replacement } });
        Check(corrected.Is("generate") && corrected.LastContributionText.Contains("No duplicate request was dispatched", StringComparison.Ordinal),
            "functional exclusion preserves exact rejected-request replay suppression");
        Inspect(corrected);
        corrected.RestoreState(JsonSerializer.Deserialize<JsonElement>(corrected.Step.State.GetRawText()));
        corrected.RejectContext(ContextProjectionStatus.NeedsCompaction); corrected.CompactSuccess();
        Check(Failures(corrected).GetArrayLength() == 0 && Receipt(corrected, failedEffect) == originalReceipt &&
              !Coverage(corrected)[0].GetProperty("interactionObserved").GetBoolean(),
            "serialization and compaction preserve failed receipt without importing interaction or a functional failure");
        Edit(corrected, actual, success: true, status: "completed", native: "completed",
            outcome: ToolExecutionOutcome.Succeeded, after: Before(replacement), textSet: true);
        Check(Failures(corrected).GetArrayLength() == 0 && Coverage(corrected)[0].GetProperty("interactionObserved").GetBoolean(),
            "a changed current CAS preimage and actual exact completed delivery establish required interaction");
        Check(Receipt(corrected, failedEffect) == originalReceipt, "real corrected delivery leaves original failed receipt byte-for-byte unchanged");
        InvokeTransform(corrected, replacement);
        corrected.Model(new { action = "final", text = "The declared changed editor value and enabled button delivery were observed; semantic behavior proof remains separate." });
        Check(corrected.Step.Next is CompleteWorkflow { Success: true },
            "real corrected delivery plus the separately delivered enabled button can reach minimum coverage without claiming full behavior proof");

        var ordinalPrecondition = Start();
        Edit(ordinalPrecondition, expected: "Value", target: "replacement", before: Before("value"));
        Check(Failures(ordinalPrecondition).GetArrayLength() == 0 && Coverage(ordinalPrecondition).GetArrayLength() == 0,
            "case-only stale preimage is an ordinal mismatch and proves setup failure without interaction credit");
        var explicitNonpartial = Start(); Edit(explicitNonpartial, partial: false);
        Check(Failures(explicitNonpartial).GetArrayLength() == 0 && Coverage(explicitNonpartial).GetArrayLength() == 0,
            "both real-producer omitted Partial and explicit false are nonpartial without importing interaction credit");
        foreach (var flag in new[] { "clickDelivered", "textSet", "instanceRecreated" })
        foreach (var value in new[] { "omitted", "null", "string", "number", "true" })
        {
            var fixture = Start(); Edit(fixture, rawFlag: flag, rawFlagValue: value);
            Check(Failures(fixture).GetArrayLength() == 1 && Coverage(fixture).GetArrayLength() == 0,
                flag + "=" + value + " cannot prove the absence of native delivery through deserialization defaults");
        }

        var rejectedOnly = Start(); Edit(rejectedOnly);
        rejectedOnly.Model(new { action = "final", text = "No native delivery occurred." });
        Check(rejectedOnly.Is("generate") && rejectedOnly.LastContributionText.Contains("successful current-run receipts", StringComparison.Ordinal),
            "the failed rejection does not satisfy the mandatory successful-tool gate");

        foreach (var scenario in new[] { "failed-status", "uncertain-native", "completed-native", "failed-outcome", "unknown-outcome",
                     "partial", "missing-observation", "limited-before", "limited-field", "hidden", "disabled", "read-only",
                     "duplicate-field", "missing-field", "equal-precondition", "after-present", "text-set", "click", "recreated",
                     "snapshot-mount", "missing-source", "invalid-source", "missing-package", "invalid-package" })
        {
            var fixture = Start();
            Edit(fixture, status: scenario == "failed-status" ? "failed" : "rejected",
                native: scenario == "uncertain-native" ? "uncertain" : scenario == "completed-native" ? "completed" : "rejected",
                outcome: scenario == "failed-outcome" ? ToolExecutionOutcome.Failed : scenario == "unknown-outcome" ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Rejected,
                before: Before(text: scenario == "equal-precondition" ? "" : actual, limited: scenario == "limited-before",
                    fieldLimited: scenario == "limited-field", visible: scenario != "hidden", enabled: scenario != "disabled",
                    readOnly: scenario == "read-only", duplicate: scenario == "duplicate-field",
                    id: scenario == "missing-field" ? new string('7', 32) : field,
                    snapshotMount: scenario == "snapshot-mount" ? "other.main" : mount),
                after: scenario == "after-present" ? Before() : null, noObservation: scenario == "missing-observation",
                textSet: scenario == "text-set", click: scenario == "click", recreated: scenario == "recreated", partial: scenario == "partial",
                returnedSource: scenario == "missing-source" ? "" : scenario == "invalid-source" ? "relative.json" : null,
                returnedFingerprint: scenario == "missing-package" ? "" : scenario == "invalid-package" ? "not-a-hash" : null);
            Check(Failures(fixture).GetArrayLength() == 1 && Coverage(fixture).GetArrayLength() == 0,
                scenario + " cannot use the no-effect stale-precondition exclusion or acquire interaction coverage");
        }

        Fixture Genuine()
        {
            var fixture = Start();
            Edit(fixture, status: "failed", native: "completed", outcome: ToolExecutionOutcome.Failed,
                before: Before(""), after: Before(""), textSet: true);
            return fixture;
        }
        var prior = Genuine();
        var priorFailure = Failures(prior).GetRawText();
        var node = JsonNode.Parse(prior.Step.State.GetRawText())!.AsObject();
        node["nativeControlFinalCorrections"] = 1;
        prior.RestoreState(CerebralJson.Element(node)); Edit(prior);
        Check(Failures(prior).GetRawText() == priorFailure &&
              prior.Step.State.GetProperty("nativeControlFinalCorrections").GetInt32() == 1,
            "excluding later stale setup preserves prior genuine or legacy same-tuple failure and final correction count");
        prior.RejectContext(ContextProjectionStatus.NeedsCompaction); prior.CompactSuccess();
        Check(Failures(prior).GetRawText() == priorFailure, "compaction preserves prior genuine failure despite later excluded stale setup");
        Edit(prior, actual, success: true, status: "completed", native: "completed",
            outcome: ToolExecutionOutcome.Succeeded, after: Before(replacement), textSet: true);
        Check(Failures(prior).GetRawText() == priorFailure,
            "a corrected different CAS tuple cannot clear an existing genuine or legacy completed-after failure");

        foreach (var scenario in new[] { "foreign", "wrong-receipt", "wrong-presentation", "wrong-mount", "truncated" })
        {
            var fixture = Genuine(); var retained = Failures(fixture).GetRawText();
            Edit(fixture, provider: scenario == "foreign" ? "foreign" : "cerebral.plugin.controls",
                wrongReceipt: scenario == "wrong-receipt", returnedPresentation: scenario == "wrong-presentation" ? new string('7', 32) : presentation,
                returnedMount: scenario == "wrong-mount" ? "other.main" : mount, truncated: scenario == "truncated");
            Check(Failures(fixture).GetRawText() == retained && Coverage(fixture).GetArrayLength() == 0,
                scenario + " receipt cannot reinterpret old failure or import native control credit");
        }
        return checks;
    }
}
