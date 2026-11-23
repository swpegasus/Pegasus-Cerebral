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
    private static int RunInvokeFieldLineSetupChecks()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Invoke field-line setup: " + name);
            checks++;
        }
        const string presentation = "77777777777777777777777777777777";
        const string mount = "note.main", button = "Transform", field = "88888888888888888888888888888888";
        const string whole = "beta\ncafé";
        string[] fragments = ["café", "beta", "Ready"];
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralInvokeSetup", "Desktop", "desktop-plugin.json"));
        Fixture Start()
        {
            var required = new[] { "interface.status", "plugin.controls" };
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
        void Invoke(Fixture fixture, string[]? expected, string after = whole, bool success = false,
            string requestedPresentation = presentation, string requestedMount = mount, string requestedButton = button,
            string? returnedSource = null, string? fingerprint = null, bool beforeLimited = false, bool afterLimited = false,
            bool fieldLimited = false, bool fieldVisible = true, bool click = true, bool textSet = false,
             bool recreated = false, string native = "completed", int? policy = PluginControlAssertions.CurrentPolicyVersion,
             string provider = "cerebral.plugin.controls", bool partial = false, bool truncated = false,
             bool wrongReceipt = false, bool duplicateButton = false, bool enabledButton = true,
             string[]? labels = null, string[]? returnedMissing = null, PluginControlTextField[]? afterFields = null,
             string? errorText = null)
        {
            var arguments = new Dictionary<string, object>
            {
                ["presentationId"] = requestedPresentation, ["mountId"] = requestedMount,
                ["operation"] = "invoke", ["buttonName"] = requestedButton
            };
            if (expected is not null) arguments["expectedTexts"] = expected;
            fixture.Model(new { action = "tool", name = "plugin.controls", arguments });
            var receipt = Guid.NewGuid().ToString("N");
            var buttons = new List<PluginControlButton> { new(requestedButton, enabledButton, true) };
            if (duplicateButton) buttons.Add(new(requestedButton, false, false));
            var before = new PluginControlSnapshot(requestedMount, labels ?? ["Ready"], buttons, beforeLimited)
                { Fields = [new(field, "café\nbeta\n", true, true, false)] };
            var afterView = new PluginControlSnapshot(requestedMount, labels ?? ["Ready"], buttons, afterLimited)
                { Fields = afterFields ?? [new(field, after, true, fieldVisible, false, fieldLimited)] };
            var exact = afterView.Texts.Concat(afterView.Fields.Where(item => item.Visible && !item.Limited).Select(item => item.Text));
            var missing = expected?.Where(value => !exact.Contains(value, StringComparer.Ordinal)).ToArray() ?? [];
            var status = success ? "completed" : native == "completed" ? "failed" : "uncertain";
            fixture.Tool(success, JsonSerializer.Serialize(new
            {
                receipt, presentationId = requestedPresentation, mountId = requestedMount, operation = "invoke",
                status, nativeStatus = native, sourceManifest = returnedSource ?? source, packageFingerprint = fingerprint ?? new string('a', 64),
                observation = new PluginControlObservation(before, afterView, ClickDelivered: click, InstanceRecreated: recreated) { TextSet = textSet },
                assertion = expected is null ? null : new
                {
                    status = success ? "passed" : status == "failed" ? "failed" : "inconclusive",
                    policyVersion = policy, expectedTexts = expected, missingTexts = success ? [] : returnedMissing ?? missing,
                    message = "Fixture asserted the actual declared native postcondition."
                },
                error = success ? null : errorText ?? "Observed invocation assertion failed."
            }, CerebralJson.Options), provider: provider, requestId: wrongReceipt ? Guid.NewGuid().ToString("N") : receipt,
                outcome: success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed, partial: partial, truncated: truncated);
        }
        JsonElement Failures(Fixture fixture) => fixture.Step.State.GetProperty("nativeControlFailures");
        bool HasSetup(Fixture fixture) => Failures(fixture).GetArrayLength() == 1 &&
            Failures(fixture)[0].TryGetProperty("invokeFieldLineSetup", out var value) && value.ValueKind == JsonValueKind.Object;
        Fixture FailedSetup()
        {
            var fixture = Start(); Invoke(fixture, fragments); return fixture;
        }
        var corrected = FailedSetup();
        Check(HasSetup(corrected), "complete actual fragment mismatch is independently classified as incorrect setup");
        var witness = Failures(corrected)[0].GetProperty("invokeFieldLineSetup");
        Check(witness.GetProperty("wholeFieldCharacters").GetInt32() == whole.Length &&
              witness.GetProperty("wholeFieldUtf8Bytes").GetInt32() == System.Text.Encoding.UTF8.GetByteCount(whole) &&
              witness.GetProperty("missingExpectationIndexes").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual([0, 1]) &&
              !witness.GetRawText().Contains("café", StringComparison.Ordinal),
            "witness stores only bounded digest lengths and missing indexes, preserving valid label index");
        Check(corrected.MetadataText.Contains("\"incorrectInvokeFieldLineSetup\":true", StringComparison.Ordinal) &&
              !corrected.MetadataText.Contains("wholeFieldSha256", StringComparison.Ordinal) &&
              !corrected.MetadataText.Contains("missingExpectationIndexes", StringComparison.Ordinal),
            "model anchors expose setup category and useful failure identity without opaque witness internals");
        var failedEffect = Failures(corrected)[0].GetProperty("effectId").GetString();
        var failedReceipt = corrected.Step.State.GetProperty("toolReceipts").EnumerateArray()
            .Single(item => item.GetProperty("effectId").GetString() == failedEffect).GetRawText();
        Check(corrected.SystemText.Contains("input different from its intended after-value", StringComparison.Ordinal) &&
              corrected.SystemText.Contains("include", StringComparison.Ordinal) &&
              corrected.SystemText.Contains("requested edge cases", StringComparison.Ordinal),
            "generic policy requires transformation-changing input and requested edge cases");
        Invoke(corrected, null, success: true); // Minimum tool success cannot discharge the declared failed assertion.
        corrected.Model(new { action = "final", text = "Passed all checks." });
        Check(corrected.Is("generate") && corrected.LastContributionText.Contains("incorrect multiline assertion setup", StringComparison.Ordinal) &&
              !corrected.LastContributionText.Contains("repeat their same declared expectations", StringComparison.OrdinalIgnoreCase),
            "setup final correction names full field recovery without directing source repair");
        var restoredState = JsonSerializer.Deserialize<JsonElement>(corrected.Step.State.GetRawText());
        corrected.RestoreState(restoredState);
        corrected.RejectContext(ContextProjectionStatus.NeedsCompaction); corrected.CompactSuccess();
        Check(HasSetup(corrected), "serialization and compaction preserve setup witness and failed receipt anchor");
        Invoke(corrected, [whole, "Ready"], success: true);
        Check(Failures(corrected).GetArrayLength() == 0 &&
              corrected.Step.State.GetProperty("nativeControlFinalCorrections").GetInt32() == 0,
            "same approved exact full field plus originally valid label resolves only the setup ledger");
        Check(corrected.Step.State.GetProperty("toolReceipts").EnumerateArray()
            .Single(item => item.GetProperty("effectId").GetString() == failedEffect).GetRawText() == failedReceipt,
            "successful correction never changes the historical Failed receipt");
        corrected.Model(new { action = "final", text = "Observed declared Transform postconditions and reported limitations." });
        Check(corrected.Step.Next is CompleteWorkflow { Success: true },
            "corrected setup can reach truthful successful terminal after actual declared postconditions");

         foreach (var newline in new[] { "\r\n", "\r" })
         {
             var alternate = Start(); var value = "beta" + newline + "café";
             Invoke(alternate, fragments, value);
             Check(HasSetup(alternate), "exact alternate newline line shape is classified without normalization");
             Invoke(alternate, [value, "Ready"], value, success: true);
             Check(Failures(alternate).GetArrayLength() == 0, "exact alternate newline whole field resolves its setup");
         }

         const string expectedLf = "alpha\nbeta", actualCrlf = "alpha\r\nbeta";
         var representation = Start();
         Invoke(representation, [expectedLf, "Ready"], actualCrlf, errorText: new string('x', 512));
         var representationFailure = Failures(representation)[0];
         var representationFailureJson = representationFailure.GetRawText();
         var representationDiagnostic = representationFailure.GetProperty("diagnostic").GetString()!;
         var representationEffect = representationFailure.GetProperty("effectId").GetString();
         var representationReceipt = representation.Step.State.GetProperty("toolReceipts").EnumerateArray()
             .Single(item => item.GetProperty("effectId").GetString() == representationEffect).GetRawText();
         Check(representationDiagnostic.Length <= 512 &&
               representationDiagnostic.Contains("Possible line-ending representation mismatch", StringComparison.Ordinal) &&
               representationDiagnostic.Contains("neither a plugin defect nor a pass", StringComparison.Ordinal) &&
               representationDiagnostic.Contains("Compare the original requirement", StringComparison.Ordinal) &&
               representationDiagnostic.Contains("do not edit source solely for this mismatch", StringComparison.Ordinal) &&
               representationDiagnostic.Contains("failed receipt remains unresolved", StringComparison.Ordinal) &&
               !HasSetup(representation),
             "one fully observed exact whole editable-field CRLF mismatch adds bounded advisory evidence without creating a dischargeable setup witness");
         Invoke(representation, [actualCrlf, "Ready"], actualCrlf, success: true);
         Check(Failures(representation).GetArrayLength() == 1 &&
               Failures(representation)[0].GetRawText() == representationFailureJson &&
               representation.Step.State.GetProperty("toolReceipts").EnumerateArray()
                   .Single(item => item.GetProperty("effectId").GetString() == representationEffect).GetRawText() == representationReceipt,
             "a later exact CRLF pass cannot discharge or mutate the original LF Failed receipt");

         var actualCr = "alpha\rbeta";
         var crRepresentation = Start();
         Invoke(crRepresentation, [expectedLf, "Ready"], actualCr);
         Check(Failures(crRepresentation).GetArrayLength() == 1 &&
               Failures(crRepresentation)[0].GetProperty("diagnostic").GetString()!
                   .Contains("Possible line-ending representation mismatch", StringComparison.Ordinal),
             "a lone-CR versus LF whole editable-field difference receives the same advisory without normalization");

         foreach (var scenario in new[] { "content", "indentation", "terminal-newline", "unicode", "duplicate-fields",
                      "missing-label", "disabled", "hidden", "readonly", "limited", "partial", "truncated", "foreign",
                      "wrong-receipt", "old-policy", "wrong-missing" })
         {
             var fixture = Start();
             var expectedValue = scenario == "indentation" ? "{\n  \"a\": 1\n}" :
                 scenario == "unicode" ? "café\nbeta" : expectedLf;
             var afterValue = scenario switch
             {
                 "content" => "alpha\r\ngamma",
                 "indentation" => "{\r\n    \"a\": 1\r\n}",
                 "terminal-newline" => actualCrlf + "\r\n",
                 "unicode" => "cafe\r\nbeta",
                 _ => actualCrlf
             };
             var expectedValues = scenario == "missing-label"
                 ? new[] { expectedValue, "Ready", "Missing status" }
                 : new[] { expectedValue, "Ready" };
             PluginControlTextField[]? fields = scenario switch
             {
                 "duplicate-fields" =>
                 [new(field, afterValue, true, true, false),
                  new(new string('9', 32), afterValue, true, true, false)],
                 "disabled" => [new(field, afterValue, false, true, false)],
                 "readonly" => [new(field, afterValue, true, true, true)],
                 _ => null
             };
              Invoke(fixture, expectedValues, afterValue,
                  partial: scenario == "partial", truncated: scenario == "truncated",
                  provider: scenario == "foreign" ? "foreign" : "cerebral.plugin.controls",
                  wrongReceipt: scenario == "wrong-receipt", fieldVisible: scenario != "hidden",
                  fieldLimited: scenario == "limited",
                  policy: scenario == "old-policy" ? null : PluginControlAssertions.CurrentPolicyVersion,
                 returnedMissing: scenario == "wrong-missing" ? ["different"] : null,
                 afterFields: fields);
             Check(Failures(fixture).GetArrayLength() == 0 ||
                   !Failures(fixture)[0].GetProperty("diagnostic").GetString()!
                       .Contains("Possible line-ending representation mismatch", StringComparison.Ordinal),
                 scenario + " cannot obtain whole-field line-ending representation advice");
         }
         var extra = FailedSetup();
        Invoke(extra, [whole, "Ready", "Additional"], success: true, labels: ["Ready", "Additional"]);
        Check(Failures(extra).GetArrayLength() == 0, "additional exact passed postconditions do not weaken corrected set");

        foreach (var scenario in new[] { "testless", "label-only", "field-only", "different-field", "different-button", "different-mount",
                     "different-presentation", "different-source", "different-package", "limited", "partial", "foreign",
                     "wrong-receipt", "no-click", "old-policy", "after-missing" })
        {
            var fixture = FailedSetup(); var before = Failures(fixture)[0].GetRawText();
            Invoke(fixture, scenario == "testless" ? null : scenario == "label-only" ? ["Ready"] :
                scenario == "field-only" ? [whole] : scenario == "different-field" ? ["beta\ncafe", "Ready"] : [whole, "Ready"],
                after: scenario == "different-field" ? "beta\ncafe" : scenario == "after-missing" ? "not the witnessed output" : whole,
                success: true, requestedPresentation: scenario == "different-presentation" ? new string('3', 32) : presentation,
                requestedMount: scenario == "different-mount" ? "other.main" : mount,
                requestedButton: scenario == "different-button" ? "Other" : button,
                returnedSource: scenario == "different-source" ? source + ".other" : null,
                fingerprint: scenario == "different-package" ? new string('b', 64) : null,
                afterLimited: scenario == "limited", partial: scenario == "partial",
                provider: scenario == "foreign" ? "foreign" : "cerebral.plugin.controls", wrongReceipt: scenario == "wrong-receipt",
                click: scenario != "no-click", policy: scenario == "old-policy" ? null : PluginControlAssertions.CurrentPolicyVersion);
            Check(Failures(fixture).GetArrayLength() == 1 && Failures(fixture)[0].GetRawText() == before,
                scenario + " cannot clear or alter exact setup failure");
        }

        foreach (var scenario in new[] { "genuine-missing", "substring", "case", "space", "accent", "before-limited", "after-limited",
                     "field-limited", "hidden", "no-click", "text-set", "recreated", "not-completed", "old-policy", "partial",
                     "foreign", "wrong-receipt", "wrong-missing", "duplicate-button", "disabled-button", "oversized-field", "distributed" })
        {
            var fixture = Start();
            var output = scenario switch
            {
                "genuine-missing" => "beta\nmissing café", "substring" => "beta\nxcaféx",
                "case" => "beta\nCafé", "space" => "beta\n café ", "accent" => "beta\ncafe",
                "oversized-field" => "beta\ncafé\n" + new string('x', 256), _ => whole
            };
            Invoke(fixture, fragments, output, beforeLimited: scenario == "before-limited", afterLimited: scenario == "after-limited",
                fieldLimited: scenario == "field-limited", fieldVisible: scenario != "hidden", click: scenario != "no-click",
                textSet: scenario == "text-set", recreated: scenario == "recreated", native: scenario == "not-completed" ? "uncertain" : "completed",
                policy: scenario == "old-policy" ? null : PluginControlAssertions.CurrentPolicyVersion, partial: scenario == "partial",
                provider: scenario == "foreign" ? "foreign" : "cerebral.plugin.controls", wrongReceipt: scenario == "wrong-receipt",
                returnedMissing: scenario == "wrong-missing" ? ["café"] : null, duplicateButton: scenario == "duplicate-button",
                enabledButton: scenario != "disabled-button", afterFields: scenario == "distributed"
                    ? [new(field, "beta\nother", true, true, false), new(new string('9', 32), "café\nother", true, true, false)] : null);
            Check(!HasSetup(fixture), scenario + " cannot establish the special assertion-shape setup category");
        }
        var budget = Start();
        var validLabels = Enumerable.Range(0, 7).Select(index => new string((char)('A' + index), 256)).ToArray();
        var budgetExpected = new[] { "é" }.Concat(validLabels).ToArray();
        Invoke(budget, budgetExpected, "é\n" + new string('漢', 254), labels: validLabels);
        Check(!HasSetup(budget), "whole field and retained valid labels must fit the existing aggregate assertion byte bound");

        var genuine = Start();
        Invoke(genuine, fragments, "actual missing output");
        Invoke(genuine, fragments);
        Check(Failures(genuine).GetArrayLength() == 1 && !HasSetup(genuine),
            "a later same-key line shape cannot downgrade a prior genuine or legacy invocation failure");
        Invoke(genuine, [whole, "Ready"], success: true);
        Check(Failures(genuine).GetArrayLength() == 1,
            "full-field correction cannot clear a genuine retained failed original declaration");
        var legacy = FailedSetup();
        var legacyNode = JsonNode.Parse(legacy.Step.State.GetRawText())!.AsObject();
        legacyNode["nativeControlFailures"]![0]!.AsObject().Remove("invokeFieldLineSetup");
        legacy.RestoreState(CerebralJson.Element(legacyNode));
        Invoke(legacy, [whole, "Ready"], success: true);
        Check(Failures(legacy).GetArrayLength() == 1 && !HasSetup(legacy), "absent legacy witness retains exact strict semantics");
        Invoke(legacy, fragments, success: true, afterFields:
            [new(field, "café", true, true, false), new(new string('9', 32), "beta", true, true, false)]);
        Check(Failures(legacy).GetArrayLength() == 0, "legacy genuine exact original successful declarations still resolve");

        foreach (var scenario in new[] { "empty-indexes", "duplicate-indexes", "unordered-indexes", "outside-index",
                     "bad-digest", "uppercase-digest", "over-characters", "zero-characters", "bad-byte-count", "missing-complete",
                     "missing-source", "missing-package", "reopen-category", "missing-anchor", "successful-anchor", "wrong-tool-anchor", "truncated-anchor" })
        {
            var fixture = FailedSetup();
            var node = JsonNode.Parse(fixture.Step.State.GetRawText())!.AsObject();
            var failure = node["nativeControlFailures"]![0]!.AsObject();
            var setup = failure["invokeFieldLineSetup"]!.AsObject();
            var anchor = node["toolReceipts"]!.AsArray().Select(item => item!.AsObject())
                .Single(item => item["effectId"]!.GetValue<string>() == failure["effectId"]!.GetValue<string>());
            switch (scenario)
            {
                case "empty-indexes": setup["missingExpectationIndexes"] = new JsonArray(); break;
                case "duplicate-indexes": setup["missingExpectationIndexes"] = new JsonArray(0, 0); break;
                case "unordered-indexes": setup["missingExpectationIndexes"] = new JsonArray(1, 0); break;
                case "outside-index": setup["missingExpectationIndexes"] = new JsonArray(3); break;
                case "bad-digest": setup["wholeFieldSha256"] = "not a digest"; break;
                case "uppercase-digest": setup["wholeFieldSha256"] = setup["wholeFieldSha256"]!.GetValue<string>().ToUpperInvariant(); break;
                case "over-characters": setup["wholeFieldCharacters"] = 257; break;
                case "zero-characters": setup["wholeFieldCharacters"] = 0; break;
                case "bad-byte-count": setup["wholeFieldUtf8Bytes"] = 1; break;
                case "missing-complete": failure["completeObservation"] = false; break;
                case "missing-source": failure["sourceManifest"] = null; break;
                case "missing-package": failure["packageFingerprint"] = null; break;
                case "reopen-category": failure["operation"] = "reopen"; break;
                case "missing-anchor": failure["effectId"] = "effect-not-retained"; break;
                case "successful-anchor": anchor["state"] = "Succeeded"; anchor["outcome"] = "Succeeded"; break;
                case "wrong-tool-anchor": anchor["name"] = "file.read"; break;
                case "truncated-anchor": anchor["truncated"] = true; break;
            }
            fixture.RestoreState(CerebralJson.Element(node));
            fixture.Model(new { action = "final", text = "Do not trust malformed state." });
            Check(fixture.Step.Next is CompleteWorkflow { Success: false }, scenario + " saved setup witness fails closed");
        }
        return checks;
    }
}
