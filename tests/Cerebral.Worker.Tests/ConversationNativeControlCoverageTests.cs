using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static int RunNativeControlCoverageChecks()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("Conversation native-control coverage: " + reason);
            checks++;
        }

        const string presentation = "11111111111111111111111111111111";
        const string oldPresentation = "22222222222222222222222222222222";
        const string mountA = "note.main";
        const string mountB = "note.secondary";
        const string button = "Save";
        const string fieldA = "33333333333333333333333333333333";
        const string fieldB = "44444444444444444444444444444444";
        const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var runId = PluginControlReferences.TestRequestPrefix + presentation;
        var sourceManifest = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralConversationCoverage",
            "Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json"));
        var service = new EventActor(EventActorKind.Service, "cerebral.plugin-tests", "Cerebral plugin tests");
        var statusSchema = InterfaceStatusFixtureSchema();
        var controlSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new
            {
                presentationId = new { type = "string" }, mountId = new { type = "string" },
                operation = new { type = "string" }, buttonName = new { type = "string" },
                fieldId = new { type = "string" }, expectedText = new { type = "string" }, text = new { type = "string" },
                expectedTexts = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "presentationId", "mountId", "operation" }, additionalProperties = false
        });

        Fixture Start(IReadOnlyList<string> required, IReadOnlyList<string> hostAdded,
            EventActor? caller = null, string? exactRunId = null, IReadOnlyList<string>? approvedMounts = null)
        {
            var fixture = new Fixture(warm: false,
                input: new { requiredSuccessfulTools = required },
                requestId: exactRunId ?? runId,
                hostAddedRequiredTools: hostAdded,
                caller: caller,
                hostPluginTestMountIds: approvedMounts ?? (caller == service && required.Contains("plugin.controls") ? [mountA] : []));
            if (fixture.Step.Next is CompleteWorkflow) return fixture;
            fixture.History([]);
            fixture.Catalog(forcedNames: required.Append("plugin.controls").Distinct(StringComparer.Ordinal).ToArray());
            if (fixture.Is("catalog")) fixture.Catalog(exact: true, inputSchemas: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["interface.status"] = statusSchema,
                ["plugin.controls"] = controlSchema
            });
            CompleteInitialQaObservationFixture(fixture);
            return fixture;
        }

        Fixture Automatic(IReadOnlyList<string>? required = null, IReadOnlyList<string>? hostAdded = null,
            EventActor? caller = null, string? exactRunId = null, IReadOnlyList<string>? approvedMounts = null)
        {
            required ??= ["plugin.controls"];
            hostAdded ??= ["plugin.controls"];
            return Start(required, hostAdded, caller ?? service, exactRunId, approvedMounts);
        }

        void Status(Fixture fixture)
        {
            fixture.Model(new { action = "tool", name = "interface.status", arguments = new { requestId = presentation } });
            fixture.Tool(true, "{}", provider: "cerebral.interface", requestId: presentation);
        }

        static PluginControlSnapshot Snapshot(string mount, IReadOnlyList<PluginControlButton>? buttons = null,
            IReadOnlyList<PluginControlTextField>? fields = null, bool limited = false,
            IReadOnlyList<string>? texts = null) =>
            new(mount, texts ?? [], buttons ?? [], limited) { Fields = fields ?? [] };

        var staticView = Snapshot(mountA, texts: ["Ready"]);
        var interactiveA = Snapshot(mountA, buttons: [new(button, true, true)], texts: ["Ready"]);
        var interactiveB = Snapshot(mountB, buttons: [new("Reload", true, true)], texts: ["Ready"]);
        var limitedView = Snapshot(mountA, buttons: [new(button, true, true)], limited: true);
        var editableA = Snapshot(mountA, fields: [new(fieldA, "before", true, true, false)]);
        var editedA = Snapshot(mountA, fields: [new(fieldA, "after\n☃", true, true, false)]);

        var receiptSequence = 0;
        string NextReceipt() => (++receiptSequence).ToString("x32");

        void Control(Fixture fixture, string operation, string mount, PluginControlSnapshot before,
            PluginControlSnapshot? after = null, bool success = true, string? argumentPresentation = null,
            string? returnedPresentation = null, string? buttonName = null, string? fieldId = null,
            string? expectedText = null, string? text = null, bool clickDelivered = false,
            bool textSet = false, string? status = null, ToolExecutionOutcome? outcome = null,
            string? error = null, string provider = "cerebral.plugin.controls", string? returnedMount = null,
            string? returnedSourceManifest = null, string? returnedPackageFingerprint = null)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["presentationId"] = argumentPresentation ?? PluginControlReferences.ReviewedReference,
                ["mountId"] = mount,
                ["operation"] = operation
            };
            if (buttonName is not null) arguments["buttonName"] = buttonName;
            if (fieldId is not null) arguments["fieldId"] = fieldId;
            if (expectedText is not null) arguments["expectedText"] = expectedText;
            if (text is not null) arguments["text"] = text;
            fixture.Model(new { action = "tool", name = "plugin.controls", arguments });

            var receipt = NextReceipt();
            var body = new
            {
                receipt,
                presentationId = returnedPresentation ?? presentation,
                mountId = returnedMount ?? mount,
                operation,
                status = status ?? (success ? "completed" : "failed"),
                nativeStatus = "completed",
                sourceManifest = returnedSourceManifest ?? sourceManifest,
                packageFingerprint = returnedPackageFingerprint ?? fingerprint,
                observation = new PluginControlObservation(before, after ?? before, clickDelivered)
                {
                    TextSet = textSet
                },
                error
            };
            fixture.Tool(success, JsonSerializer.Serialize(body, CerebralJson.Options),
                outcome: outcome ?? (success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed),
                provider: provider, requestId: receipt);
        }

        void Inspect(Fixture fixture, PluginControlSnapshot snapshot, string? argumentPresentation = null,
            string? returnedPresentation = null, string provider = "cerebral.plugin.controls") =>
            Control(fixture, "inspect", snapshot.Id, snapshot, argumentPresentation: argumentPresentation,
                returnedPresentation: returnedPresentation, provider: provider);

        void Invoke(Fixture fixture, string mount, PluginControlSnapshot before, string buttonName,
            PluginControlSnapshot? after = null) =>
            Control(fixture, "invoke", mount, before, after, buttonName: buttonName, clickDelivered: true);

        void LegacyInvoke(Fixture fixture, bool success, string? resultSourceManifest = null,
            string? resultPackageFingerprint = null, string? returnedPresentation = null)
        {
            fixture.Model(new { action = "tool", name = "plugin.controls", arguments = new
                { presentationId = PluginControlReferences.ReviewedReference, mountId = mountA,
                    operation = "invoke", buttonName = button } });
            if (!fixture.Is("tool"))
                throw new InvalidOperationException("Legacy control fixture could not dispatch: " + fixture.LastContributionText);
            var receipt = NextReceipt();
            var body = new Dictionary<string, object?>
            {
                ["receipt"] = receipt,
                ["presentationId"] = returnedPresentation ?? presentation,
                ["mountId"] = mountA,
                ["operation"] = "invoke",
                ["status"] = success ? "completed" : "rejected",
                ["nativeStatus"] = success ? "completed" : "rejected",
                ["error"] = success ? null : "The legacy native invocation was rejected."
            };
            if (resultSourceManifest is not null) body["sourceManifest"] = resultSourceManifest;
            if (resultPackageFingerprint is not null) body["packageFingerprint"] = resultPackageFingerprint;
            fixture.Tool(success, JsonSerializer.Serialize(body, CerebralJson.Options),
                outcome: success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Rejected,
                provider: "cerebral.plugin.controls", requestId: receipt);
        }

        void SetText(Fixture fixture, string mount, PluginControlSnapshot before, PluginControlSnapshot? after,
            string fieldId, string expectedText, string text, bool success = true, string? status = null,
            string? error = null, string? returnedSourceManifest = null, string? returnedPackageFingerprint = null) =>
            Control(fixture, "setText", mount, before, after, success, fieldId: fieldId,
                expectedText: expectedText, text: text, textSet: success, status: status, error: error,
                returnedSourceManifest: returnedSourceManifest, returnedPackageFingerprint: returnedPackageFingerprint);

        static JsonElement Coverage(Fixture fixture) => fixture.Step.State.GetProperty("nativeControlCoverage");
        static JsonElement Failures(Fixture fixture) => fixture.Step.State.GetProperty("nativeControlFailures");

        Fixture RetainedLegacyInvokeFailure(string? retainedSourceManifest, string? retainedPackageFingerprint)
        {
            var fixture = Automatic();
            var saved = JsonNode.Parse(fixture.Step.State.GetRawText())!.AsObject();
            saved["nativeControlFailures"] = new JsonArray(JsonSerializer.SerializeToNode(new
            {
                presentationId = presentation,
                mountId = mountA,
                buttonName = button,
                effectId = "55555555555555555555555555555555",
                diagnostic = "The retained legacy invocation was rejected.",
                sourceManifest = retainedSourceManifest,
                packageFingerprint = retainedPackageFingerprint,
                operation = "invoke",
                expectedTexts = (string[]?)null,
                completeObservation = false
            }, CerebralJson.Options));
            fixture.RestoreState(CerebralJson.Element(saved));
            return fixture;
        }

        var trustedStatic = Automatic();
        Inspect(trustedStatic, staticView);
        trustedStatic.Model(new { action = "final", text = "The static view was inspected." });
        Check(trustedStatic.Step.Next is CompleteWorkflow { Success: true },
            "an exact service-owned automatic QA run may complete after a full static-view inspection");

        var inspectOnly = Automatic();
        Inspect(inspectOnly, interactiveA);
        inspectOnly.Model(new { action = "final", text = "The interactive view works." });
        Check(inspectOnly.Is("generate") && inspectOnly.LastContributionText.Contains(mountA, StringComparison.Ordinal) &&
              inspectOnly.LastContributionText.Contains("Inspection alone", StringComparison.Ordinal),
            "inspect-only evidence cannot complete automatic QA for an actionable mount");

        var limited = Automatic();
        Inspect(limited, limitedView);
        Check(Coverage(limited).GetArrayLength() == 0,
            "a limited native snapshot establishes no complete coverage");
        limited.Model(new { action = "final", text = "The limited view works." });
        Check(limited.Is("generate") && limited.LastContributionText.Contains("No complete current-package", StringComparison.Ordinal),
            "a limited native snapshot cannot satisfy the automatic-QA coverage gate");

        var foreignPayload = Automatic();
        Inspect(foreignPayload, staticView, returnedPresentation: oldPresentation);
        Check(Coverage(foreignPayload).GetArrayLength() == 0,
            "a returned foreign approval identity cannot be adopted for reviewed");
        var foreignProvider = Automatic();
        Inspect(foreignProvider, staticView, provider: "foreign.provider");
        Check(Coverage(foreignProvider).GetArrayLength() == 0,
            "a foreign evidence provider cannot establish native-control coverage");
        var oldApproval = Automatic();
        Inspect(oldApproval, staticView, argumentPresentation: oldPresentation, returnedPresentation: oldPresentation);
        oldApproval.Model(new { action = "final", text = "The old approval was inspected." });
        Check(oldApproval.Is("generate") && Coverage(oldApproval).GetArrayLength() == 0,
            "coverage for an older concrete approval cannot satisfy the exact automatic-QA presentation");

        var otherSource = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralConversationCoverage",
            "Other", "desktop-plugin.json"));
        var otherFingerprint = new string('b', 64);
        var conflictingSource = Automatic();
        Inspect(conflictingSource, staticView);
        Control(conflictingSource, "inspect", mountA, staticView, returnedSourceManifest: otherSource);
        Check(Coverage(conflictingSource).GetArrayLength() == 1 &&
              Coverage(conflictingSource)[0].GetProperty("identityConflictEffectId").ValueKind == JsonValueKind.String,
            "a same-mount source-manifest conflict remains explicit without replacing the prior immutable-package proof");
        conflictingSource.Model(new { action = "final", text = "The static mount was inspected." });
        Check(conflictingSource.Is("generate") && conflictingSource.LastContributionText.Contains("package identity conflicted", StringComparison.Ordinal),
            "conflicting source identity cannot leave prior static coverage reusable for completion");

        var conflictingFingerprint = Automatic();
        Inspect(conflictingFingerprint, staticView);
        Control(conflictingFingerprint, "inspect", mountA, staticView, returnedPackageFingerprint: otherFingerprint);
        conflictingFingerprint.Model(new { action = "final", text = "The static mount was inspected." });
        Check(conflictingFingerprint.Is("generate") &&
              Coverage(conflictingFingerprint)[0].GetProperty("identityConflictEffectId").ValueKind == JsonValueKind.String,
            "a same-mount package-fingerprint conflict fails closed instead of retaining reusable completion credit");

        var limitedConflictingIdentity = Automatic();
        Inspect(limitedConflictingIdentity, staticView);
        Control(limitedConflictingIdentity, "inspect", mountA, limitedView, returnedSourceManifest: otherSource);
        limitedConflictingIdentity.Model(new { action = "final", text = "The original static observation was complete." });
        Check(limitedConflictingIdentity.Is("generate") &&
              Coverage(limitedConflictingIdentity)[0].GetProperty("identityConflictEffectId").ValueKind == JsonValueKind.String,
            "a later successful limited snapshot from conflicting immutable bytes invalidates prior sufficient coverage");

        var crossMountIdentity = Automatic(approvedMounts: [mountA, mountB]);
        Inspect(crossMountIdentity, staticView);
        Control(crossMountIdentity, "inspect", mountB, Snapshot(mountB, texts: ["Ready"]),
            returnedSourceManifest: otherSource);
        crossMountIdentity.Model(new { action = "final", text = "Both static mounts were inspected." });
        Check(crossMountIdentity.Is("generate") &&
              crossMountIdentity.LastContributionText.Contains(mountB, StringComparison.Ordinal) &&
              Coverage(crossMountIdentity)[0].GetProperty("identityConflictEffectId").ValueKind == JsonValueKind.String,
            "different package identities across approved mounts cannot combine into one presentation's coverage");

        var invoked = Automatic();
        Inspect(invoked, interactiveA);
        Invoke(invoked, mountA, interactiveA, button);
        invoked.Model(new { action = "final", text = "The enabled control was exercised." });
        Check(invoked.Step.Next is CompleteWorkflow { Success: true } &&
              Coverage(invoked)[0].GetProperty("interactionObserved").GetBoolean(),
            "a delivered invoke against its exact enabled visible control provides minimum interaction coverage");

        var allButtons = Automatic();
        var upperLowerClear = Snapshot(mountA, buttons:
            [new("Uppercase", true, true), new("Lowercase", true, true), new("Clear", true, true)]);
        var clearOnly = Snapshot(mountA, buttons: [new("Clear", true, true)]);
        Invoke(allButtons, mountA, upperLowerClear, "Uppercase", clearOnly);
        Invoke(allButtons, mountA, clearOnly, "Clear", staticView);
        allButtons.Model(new { action = "final", text = "Uppercase and Clear passed." });
        Check(allButtons.Is("generate") && allButtons.LastContributionText.Contains("note.main / Lowercase", StringComparison.Ordinal) &&
              !allButtons.LastContributionText.Contains("note.main / Uppercase,", StringComparison.Ordinal) &&
              Coverage(allButtons)[0].GetProperty("buttons").GetArrayLength() == 3,
            "successful Uppercase and Clear invocations cannot cover an observed but skipped Lowercase button");
        var retainedButtons = Coverage(allButtons)[0].GetProperty("buttons").EnumerateArray().ToArray();
        Check(retainedButtons.Single(item => item.GetProperty("name").GetString() == "Lowercase")
                  .GetProperty("invokedEffectId").ValueKind == JsonValueKind.Null &&
              retainedButtons.Where(item => item.GetProperty("name").GetString() is "Uppercase" or "Clear")
                  .All(item => item.GetProperty("invokedEffectId").ValueKind == JsonValueKind.String),
            "button coverage retains separate delivered-invocation provenance for each exact ordinal name");
        Invoke(allButtons, mountA, Snapshot(mountA, buttons: [new("Lowercase", true, true)]), "Lowercase", staticView);
        allButtons.Model(new { action = "final", text = "All observed buttons were exercised and evaluated." });
        Check(allButtons.Step.Next is CompleteWorkflow { Success: true },
            "the automatic-QA coverage gate clears only after every retained unambiguous button name is invoked");

        var afterOnly = Automatic();
        var revealBefore = Snapshot(mountA, buttons: [new("Reveal", true, true)]);
        var revealAfter = Snapshot(mountA, buttons: [new("Reveal", true, true), new("Advanced", true, true)]);
        Invoke(afterOnly, mountA, revealBefore, "Reveal", revealAfter);
        Control(afterOnly, "reopen", mountA, staticView);
        afterOnly.Model(new { action = "final", text = "The initially visible control was exercised." });
        Check(afterOnly.Is("generate") && afterOnly.LastContributionText.Contains("note.main / Advanced", StringComparison.Ordinal),
            "a button first revealed only in After remains required after later complete snapshots no longer show it");

        var incremental = Automatic();
        var incrementalView = Snapshot(mountA, buttons:
            [new("First", true, true), new("Second", true, true), new("Third", true, true)]);
        Inspect(incremental, incrementalView);
        incremental.Model(new { action = "final", text = "Inspection completed." });
        Invoke(incremental, mountA, incrementalView, "First");
        incremental.Model(new { action = "final", text = "First completed." });
        Invoke(incremental, mountA, incrementalView, "Second");
        incremental.Model(new { action = "final", text = "Second completed." });
        Check(incremental.Is("generate") &&
              incremental.Step.State.GetProperty("nativeControlFinalCorrections").GetInt32() == 1 &&
              incremental.LastContributionText.Contains("note.main / Third", StringComparison.Ordinal),
            "each newly delivered missing button resets only the completion-correction budget and preserves the remaining obligation");
        Invoke(incremental, mountA, incrementalView, "Third");
        incremental.Model(new { action = "final", text = "All three enabled buttons were exercised." });
        Check(incremental.Step.Next is CompleteWorkflow { Success: true },
            "bounded correction accounting permits genuine sequential per-button progress to finish");

        var visibility = Automatic();
        var visibilityView = Snapshot(mountA, buttons:
            [new("Hidden", true, false), new("Disabled", false, true), new("Available", true, true)]);
        Invoke(visibility, mountA, visibilityView, "Available");
        Check(Coverage(visibility)[0].GetProperty("buttons").EnumerateArray().Single().GetProperty("name").GetString() == "Available",
            "hidden and disabled button labels do not become automatic invocation obligations");
        visibility.Model(new { action = "final", text = "The only enabled visible button was exercised." });
        Check(visibility.Step.Next is CompleteWorkflow { Success: true },
            "excluded hidden and disabled controls do not block otherwise complete button coverage");

        var ambiguousButtons = Automatic();
        var ambiguousView = Snapshot(mountA, buttons: [new("Save", true, true), new("Save", true, false)]);
        Inspect(ambiguousButtons, ambiguousView);
        Control(ambiguousButtons, "invoke", mountA, ambiguousView, ambiguousView,
            buttonName: "Save", clickDelivered: true);
        Check(Coverage(ambiguousButtons)[0].GetProperty("buttonNamesAmbiguous").GetBoolean() &&
              !Coverage(ambiguousButtons)[0].GetProperty("interactionObserved").GetBoolean(),
            "a synthetic delivered click cannot earn credit when one actionable button shares its name with a hidden duplicate");
        ambiguousButtons.Model(new { action = "final", text = "The duplicate Save controls were tested." });
        Check(ambiguousButtons.Is("generate") && ambiguousButtons.LastContributionText.Contains("ambiguous", StringComparison.OrdinalIgnoreCase),
            "ambiguous button-name evidence remains inconclusive at the automatic-QA completion gate");
        ambiguousButtons.Model(new { action = "blocked", reason = "The two enabled Save controls share one unsupported invocation coordinate." });
        Check(ambiguousButtons.Step.Next is CompleteWorkflow { Success: false },
            "automatic QA can report an honest blocked result without invoking an ambiguous or unsafe control");

        var overflow = Automatic();
        for (var batch = 0; batch < 4; batch++)
        {
            var names = Enumerable.Range(batch * 20, 20).Select(index => new PluginControlButton("B" + index, true, true)).ToArray();
            var snapshot = Snapshot(mountA, buttons: names);
            Invoke(overflow, mountA, snapshot, names[0].Name);
        }
        Check(Coverage(overflow)[0].GetProperty("buttons").GetArrayLength() == WorkflowContract.MaximumPluginTestMounts &&
              Coverage(overflow)[0].GetProperty("buttonNamesLimited").GetBoolean(),
            "the private button ledger preserves its bounded entries and marks additional observed names inconclusive");
        overflow.Model(new { action = "final", text = "The large dynamic control surface works." });
        Check(overflow.Is("generate") && overflow.LastContributionText.Contains("bounded ledger", StringComparison.Ordinal),
            "button-ledger overflow fails closed without discarding retained provenance or claiming completion");
        const string metadataMarker = "\nAccepted metadata and host evidence:\n";
        using (var metadata = JsonDocument.Parse(overflow.MetadataText[(overflow.MetadataText.IndexOf(metadataMarker, StringComparison.Ordinal) + metadataMarker.Length)..]))
        {
            var projected = metadata.RootElement.GetProperty("nativeControlCoverage")[0];
            Check(projected.GetProperty("missingButtonNames").GetArrayLength() == 8 &&
                  projected.GetProperty("missingButtonCount").GetInt32() == WorkflowContract.MaximumPluginTestMounts - 4 &&
                  projected.GetProperty("missingButtonNamesTruncated").GetBoolean(),
                "model-facing missing-button projection retains the total while exposing at most eight bounded names");
        }

        var legacySource = Automatic();
        Inspect(legacySource, interactiveA);
        var legacyCoverageState = JsonNode.Parse(legacySource.Step.State.GetRawText())!.AsObject();
        legacyCoverageState["nativeControlCoverage"]![0]!.AsObject().Remove("buttons");
        legacyCoverageState["nativeControlCoverage"]![0]!.AsObject().Remove("buttonNamesAmbiguous");
        legacyCoverageState["nativeControlCoverage"]![0]!.AsObject().Remove("buttonNamesLimited");
        legacyCoverageState["nativeControlCoverage"]![0]!.AsObject().Remove("identityConflictEffectId");
        legacyCoverageState["nativeControlCoverage"]![0]!.AsObject().Remove("legacyInteractiveButtonCoverageIncomplete");
        var legacy = legacySource.Fork();
        legacy.RestoreState(CerebralJson.Element(legacyCoverageState));
        legacy.Model(new { action = "final", text = "The old coverage remains sufficient." });
        Check(legacy.Is("generate") && legacy.LastContributionText.Contains("Legacy coverage", StringComparison.Ordinal),
            "historical saved coverage without the nullable button ledger cannot infer skipped-button completion");
        Invoke(legacy, mountA, interactiveA, button);
        legacy.Model(new { action = "final", text = "Fresh complete evidence exercised the current button." });
        Check(legacy.Is("generate") &&
              Coverage(legacy)[0].GetProperty("legacyInteractiveButtonCoverageIncomplete").GetBoolean() &&
              legacy.LastContributionText.Contains("older interactive record", StringComparison.Ordinal),
            "one fresh subset cannot reconstruct a legacy interactive record's previously unretained button inventory");

        var legacyStaticSource = Automatic();
        Inspect(legacyStaticSource, staticView);
        var legacyStaticState = JsonNode.Parse(legacyStaticSource.Step.State.GetRawText())!.AsObject();
        legacyStaticState["nativeControlCoverage"]![0]!.AsObject().Remove("buttons");
        legacyStaticState["nativeControlCoverage"]![0]!.AsObject().Remove("buttonNamesAmbiguous");
        legacyStaticState["nativeControlCoverage"]![0]!.AsObject().Remove("buttonNamesLimited");
        legacyStaticState["nativeControlCoverage"]![0]!.AsObject().Remove("identityConflictEffectId");
        legacyStaticState["nativeControlCoverage"]![0]!.AsObject().Remove("legacyInteractiveButtonCoverageIncomplete");
        var legacyStatic = legacyStaticSource.Fork();
        legacyStatic.RestoreState(CerebralJson.Element(legacyStaticState));
        Control(legacyStatic, "reopen", mountA, staticView);
        legacyStatic.Model(new { action = "final", text = "The static view was freshly and completely observed." });
        Check(legacyStatic.Step.Next is CompleteWorkflow { Success: true } &&
              !Coverage(legacyStatic)[0].GetProperty("legacyInteractiveButtonCoverageIncomplete").GetBoolean(),
            "a fresh complete static observation can safely initialize a legacy static record's empty button ledger");

        var failedInvoke = Automatic();
        Inspect(failedInvoke, interactiveA);
        Control(failedInvoke, "invoke", mountA, interactiveA, interactiveA, success: false,
            buttonName: button, clickDelivered: false, status: "failed", outcome: ToolExecutionOutcome.Failed,
            error: "The native click was not delivered.");
        Check(Coverage(failedInvoke)[0].GetProperty("buttons")[0].GetProperty("invokedEffectId").ValueKind == JsonValueKind.Null,
            "failed native invocation evidence cannot receive per-button delivery credit");
        failedInvoke.Model(new { action = "final", text = "The failed click was sufficient." });
        var correctionBeforeProgress = failedInvoke.Step.State.GetProperty("nativeControlFinalCorrections").GetInt32();
        var anotherButton = Snapshot(mountA, buttons: [new(button, true, true), new("Other", true, true)]);
        Invoke(failedInvoke, mountA, anotherButton, "Other");
        Check(Failures(failedInvoke).GetArrayLength() == 1 &&
              failedInvoke.Step.State.GetProperty("nativeControlFinalCorrections").GetInt32() == correctionBeforeProgress,
            "new button evidence cannot reset completion corrections while a genuine native failure remains unresolved");

        var textEdited = Automatic();
        Inspect(textEdited, editableA);
        SetText(textEdited, mountA, editableA, editedA, fieldA, "before", "after\n☃");
        textEdited.Model(new { action = "final", text = "The exact editable field was exercised." });
        Check(textEdited.Step.Next is CompleteWorkflow { Success: true } &&
              Coverage(textEdited)[0].GetProperty("interactionObserved").GetBoolean(),
            "an exact delivered setText with matching before and after values provides minimum interaction coverage");

        var textDoesNotCoverButton = Automatic();
        var buttonAndFieldBefore = Snapshot(mountA, buttons: [new(button, true, true)],
            fields: [new(fieldA, "before", true, true, false)]);
        var buttonAndFieldAfter = Snapshot(mountA, buttons: [new(button, true, true)],
            fields: [new(fieldA, "after", true, true, false)]);
        SetText(textDoesNotCoverButton, mountA, buttonAndFieldBefore, buttonAndFieldAfter, fieldA, "before", "after");
        textDoesNotCoverButton.Model(new { action = "final", text = "The editable field was exercised." });
        Check(textDoesNotCoverButton.Is("generate") &&
              textDoesNotCoverButton.LastContributionText.Contains("note.main / Save", StringComparison.Ordinal),
            "a successful field edit cannot substitute for an observed button's delivered invocation");

        var multiple = Automatic(approvedMounts: [mountA, mountB]);
        Inspect(multiple, interactiveA);
        Inspect(multiple, interactiveB);
        Invoke(multiple, mountA, interactiveA, button);
        multiple.Model(new { action = "final", text = "One mount was exercised." });
        Check(multiple.Is("generate") && multiple.LastContributionText.Contains(mountB, StringComparison.Ordinal),
            "every observed interactive mount needs its own delivered interaction");
        Invoke(multiple, mountB, interactiveB, "Reload");
        multiple.Model(new { action = "final", text = "Both mounts were exercised." });
        Check(multiple.Step.Next is CompleteWorkflow { Success: true },
            "delivered interactions across all observed interactive mounts satisfy minimum coverage");

        var durable = Automatic();
        Inspect(durable, interactiveA);
        Invoke(durable, mountA, interactiveA, button);
        var interactionEffect = Coverage(durable)[0].GetProperty("interactionEffectId").GetString();
        Inspect(durable, interactiveA);
        Check(Coverage(durable)[0].GetProperty("interactionEffectId").GetString() == interactionEffect &&
              Coverage(durable)[0].GetProperty("effectId").GetString() != interactionEffect,
            "later inspection updates observation provenance without replacing delivered interaction provenance");
        durable.RejectContext(ContextProjectionStatus.NeedsCompaction);
        Check(durable.Is("compact"), "the coverage fixture reaches the real compaction boundary");
        durable.CompactSuccess();
        var restoredState = durable.Step.State.Clone();
        var restored = durable.Fork();
        restored.RestoreState(restoredState);
        Check(restored.Is("generate") &&
              Coverage(restored)[0].GetProperty("interactionEffectId").GetString() == interactionEffect,
            "compaction and saved-state restoration preserve the exact interaction provenance");
        restored.Model(new { action = "final", text = "The restored interaction remains covered." });
        Check(restored.Step.Next is CompleteWorkflow { Success: true },
            "restored complete coverage remains sufficient for the same automatic QA run");

        var wrongSetTextObservation = Automatic();
        Inspect(wrongSetTextObservation, editableA);
        SetText(wrongSetTextObservation, mountA, editableA, editableA, fieldA, "before", "after\n☃");
        Check(!Coverage(wrongSetTextObservation)[0].GetProperty("interactionObserved").GetBoolean(),
            "a completed setText whose exact after value does not match the requested replacement earns no interaction credit");
        wrongSetTextObservation.Model(new { action = "final", text = "The editor was exercised." });
        Check(wrongSetTextObservation.Is("generate"),
            "a delivered-but-mismatched setText observation cannot complete interactive automatic QA");

        var failedEdits = Automatic();
        var failedExpected = "old π";
        var failedText = "new π";
        var failedBefore = Snapshot(mountA, fields: [new(fieldA, failedExpected, true, true, false)]);
        var failedAfter = Snapshot(mountA, fields: [new(fieldA, failedExpected, true, true, false)]);
        SetText(failedEdits, mountA, failedBefore, failedAfter, fieldA, failedExpected, failedText,
            success: false, status: "failed", error: "The native field rejected the replacement.");
        var uncertainExpected = "before 雪";
        var uncertainText = "after 雪";
        var uncertainBefore = Snapshot(mountB, fields: [new(fieldB, uncertainExpected, true, true, false)]);
        SetText(failedEdits, mountB, uncertainBefore, null, fieldB, uncertainExpected, uncertainText,
            success: false, status: "uncertain", error: "The after-state could not be confirmed.");
        Invoke(failedEdits, "note.other", Snapshot("note.other", buttons: [new("Reset", true, true)]), "Reset");
        var failures = Failures(failedEdits).EnumerateArray().OrderBy(item => item.GetProperty("mountId").GetString()).ToArray();
        string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        Check(failures.Length == 2 && failures.All(item => item.GetProperty("operation").GetString() == "setText") &&
              failures.Any(item => item.GetProperty("fieldId").GetString() == fieldA &&
                  item.GetProperty("expectedTextSha256").GetString() == Hash(failedExpected) &&
                  item.GetProperty("textSha256").GetString() == Hash(failedText) &&
                  item.GetProperty("expectedTextUtf8Bytes").GetInt32() == Encoding.UTF8.GetByteCount(failedExpected) &&
                  item.GetProperty("textUtf8Bytes").GetInt32() == Encoding.UTF8.GetByteCount(failedText)) &&
              failures.Any(item => item.GetProperty("fieldId").GetString() == fieldB &&
                  item.GetProperty("expectedTextSha256").GetString() == Hash(uncertainExpected) &&
                  item.GetProperty("textSha256").GetString() == Hash(uncertainText)) &&
              failures.All(item => !item.TryGetProperty("expectedText", out _) && !item.TryGetProperty("text", out _)),
            "failed and inconclusive setText evidence retains exact bounded CAS hashes and UTF-8 lengths through unrelated success without raw text");

        var differentFieldBefore = Snapshot(mountA, fields: [new(fieldB, "field-b-before", true, true, false)]);
        var differentFieldAfter = Snapshot(mountA, fields: [new(fieldB, "field-b-after", true, true, false)]);
        SetText(failedEdits, mountA, differentFieldBefore, differentFieldAfter, fieldB,
            "field-b-before", "field-b-after");
        Check(Failures(failedEdits).GetArrayLength() == 2,
            "a successful setText on a different field cannot clear a retained CAS failure");
        var differentTupleBefore = Snapshot(mountA, fields: [new(fieldA, "other-before", true, true, false)]);
        var differentTupleAfter = Snapshot(mountA, fields: [new(fieldA, "other-after", true, true, false)]);
        SetText(failedEdits, mountA, differentTupleBefore, differentTupleAfter, fieldA,
            "other-before", "other-after");
        Check(Failures(failedEdits).GetArrayLength() == 2,
            "a successful setText with different preimage and replacement values cannot clear a retained CAS failure");
        SetText(failedEdits, mountA, failedBefore,
            Snapshot(mountA, fields: [new(fieldA, failedText, true, true, false)]),
            fieldA, failedExpected, failedText, returnedSourceManifest: otherSource);
        Check(Failures(failedEdits).GetArrayLength() == 2,
            "a same-presentation success from a conflicting source manifest cannot clear retained native failure evidence");
        SetText(failedEdits, mountA, failedBefore,
            Snapshot(mountA, fields: [new(fieldA, failedText, true, true, false)]),
            fieldA, failedExpected, failedText, returnedPackageFingerprint: otherFingerprint);
        Check(Failures(failedEdits).GetArrayLength() == 2,
            "a same-presentation success from a conflicting package fingerprint cannot clear retained native failure evidence");
        SetText(failedEdits, mountA, failedBefore,
            Snapshot(mountA, fields: [new(fieldA, failedText, true, true, false)]),
            fieldA, failedExpected, failedText);
        Check(Failures(failedEdits).GetArrayLength() == 1 &&
              Failures(failedEdits)[0].GetProperty("mountId").GetString() == mountB &&
              Failures(failedEdits)[0].GetProperty("fieldId").GetString() == fieldB,
            "only a complete successful setText with the exact retained CAS tuple clears that failure and preserves the unrelated mount failure");

        var knownToMissingIdentity = RetainedLegacyInvokeFailure(sourceManifest, fingerprint);
        LegacyInvoke(knownToMissingIdentity, success: true);
        Check(Failures(knownToMissingIdentity).GetArrayLength() == 1,
            "a legacy success with missing package identity cannot clear a retained failure with known package identity");

        var missingToKnownIdentity = RetainedLegacyInvokeFailure(null, null);
        LegacyInvoke(missingToKnownIdentity, success: true,
            resultSourceManifest: sourceManifest, resultPackageFingerprint: fingerprint);
        Check(Failures(missingToKnownIdentity).GetArrayLength() == 1,
            "a current success with known package identity cannot reinterpret and clear a legacy identity-absent failure");

        var bothIdentityAbsent = RetainedLegacyInvokeFailure(null, null);
        LegacyInvoke(bothIdentityAbsent, success: true);
        Check(Failures(bothIdentityAbsent).GetArrayLength() == 0,
            "the historical exact same-presentation recovery remains compatible when both retained and later receipts omit both identity fields");

        var malformedToAbsentIdentity = RetainedLegacyInvokeFailure(null, null);
        LegacyInvoke(malformedToAbsentIdentity, success: true,
            resultSourceManifest: "not-a-canonical-path", resultPackageFingerprint: "not-a-fingerprint");
        Check(Failures(malformedToAbsentIdentity).GetArrayLength() == 1,
            "present but malformed package identity cannot masquerade as literal legacy identity absence");

        var malformedFailureEnvelope = Automatic();
        LegacyInvoke(malformedFailureEnvelope, success: false,
            resultSourceManifest: "not-a-canonical-path", resultPackageFingerprint: "not-a-fingerprint");
        Check(Failures(malformedFailureEnvelope).GetArrayLength() == 1 &&
              Failures(malformedFailureEnvelope)[0].GetProperty("packageIdentityIncomplete").GetBoolean(),
            "a newly retained malformed-identity failure records that it is not historical identity-absent evidence");
        var malformedFailureRestore = Automatic();
        var malformedFailureState = JsonNode.Parse(malformedFailureRestore.Step.State.GetRawText())!.AsObject();
        malformedFailureState["nativeControlFailures"] = JsonNode.Parse(Failures(malformedFailureEnvelope).GetRawText());
        malformedFailureRestore.RestoreState(CerebralJson.Element(malformedFailureState));
        LegacyInvoke(malformedFailureRestore, success: true);
        Check(Failures(malformedFailureRestore).GetArrayLength() == 1 &&
              Failures(malformedFailureRestore)[0].GetProperty("packageIdentityIncomplete").GetBoolean(),
            "a later literal-absence success cannot clear a failure that originated from malformed present identity fields");

        var partialLegacyIdentity = RetainedLegacyInvokeFailure(sourceManifest, null);
        LegacyInvoke(partialLegacyIdentity, success: true,
            resultSourceManifest: sourceManifest, resultPackageFingerprint: fingerprint);
        Check(Failures(partialLegacyIdentity).GetArrayLength() == 1,
            "a partially retained legacy package identity cannot clear against a later complete package identity");

        var malformedFailure = JsonNode.Parse(failedEdits.Step.State.GetRawText())!.AsObject();
        malformedFailure["nativeControlFailures"]![0]!["expectedTextSha256"] = "not-a-hash";
        var rejectedFailureState = failedEdits.Fork();
        rejectedFailureState.RestoreState(CerebralJson.Element(malformedFailure));
        rejectedFailureState.Model(new { action = "final", text = "Malformed failure state must not be trusted." });
        Check(rejectedFailureState.Step.Next is CompleteWorkflow { Success: false } &&
              rejectedFailureState.Summary.Contains("saved conversation state", StringComparison.Ordinal),
            "malformed retained setText CAS evidence fails closed during state restoration");

        var malformedCoverageSource = Automatic();
        Inspect(malformedCoverageSource, staticView);
        var malformedCoverage = JsonNode.Parse(malformedCoverageSource.Step.State.GetRawText())!.AsObject();
        malformedCoverage["nativeControlCoverage"]![0]!["interactionObserved"] = true;
        malformedCoverage["nativeControlCoverage"]![0]!["interactionEffectId"] = null;
        var rejectedCoverageState = malformedCoverageSource.Fork();
        rejectedCoverageState.RestoreState(CerebralJson.Element(malformedCoverage));
        rejectedCoverageState.Model(new { action = "final", text = "Malformed coverage state must not be trusted." });
        Check(rejectedCoverageState.Step.Next is CompleteWorkflow { Success: false } &&
              rejectedCoverageState.Summary.Contains("saved conversation state", StringComparison.Ordinal),
            "malformed retained interaction provenance fails closed during state restoration");

        var emptyDifference = Automatic(["interface.status", "plugin.controls"], [], service, runId);
        Status(emptyDifference);
        Inspect(emptyDifference, interactiveA);
        emptyDifference.Model(new { action = "final", text = "Inspection is sufficient." });
        Check(emptyDifference.Is("generate") && emptyDifference.LastContributionText.Contains("Inspection alone", StringComparison.Ordinal),
            "an exact service automatic-QA run remains covered when its original requirements leave an empty host-added difference");

        var ordinaryNamed = Start(["interface.status", "plugin.controls"], [], caller: null, exactRunId: runId);
        Status(ordinaryNamed);
        Inspect(ordinaryNamed, interactiveA);
        ordinaryNamed.Model(new { action = "final", text = "The ordinary run completed its accepted tools." });
        Check(ordinaryNamed.Step.Next is CompleteWorkflow { Success: true },
            "an ordinary run cannot acquire automatic-QA coverage policy merely by choosing a plugin-tests-shaped name");

        var repairSubset = Automatic(["interface.status"], ["interface.status"], service, runId);
        Status(repairSubset);
        repairSubset.Model(new { action = "final", text = "The admitted status check completed." });
        Check(repairSubset.Step.Next is CompleteWorkflow { Success: true },
            "a service repair subset lacking both original installed-QA tools is not promoted to native-control QA");


        var omittedApprovedMount = Automatic(approvedMounts: [mountA, mountB]);
        Inspect(omittedApprovedMount, staticView);
        omittedApprovedMount.Model(new { action = "final", text = "The visible static view was inspected." });
        Check(omittedApprovedMount.Is("generate") &&
              omittedApprovedMount.LastContributionText.Contains(mountB, StringComparison.Ordinal),
            "an inspected static view cannot hide another unobserved owner-approved mount");

        var historicalMountList = Automatic(approvedMounts: []);
        Inspect(historicalMountList, staticView);
        historicalMountList.Model(new { action = "final", text = "Historical inspection is sufficient." });
        Check(historicalMountList.Is("generate") && Coverage(historicalMountList).GetArrayLength() == 0 &&
              historicalMountList.LastContributionText.Contains("host-approved mount list is unavailable", StringComparison.Ordinal),
            "historical missing mount provenance cannot be inferred from a model-selected observation");

        var ordinaryMany = Start(["plugin.controls"], [], exactRunId: "ordinary-many-controls");
        for (var index = 0; index < 10; index++)
        {
            var mount = "ordinary.mount-" + index;
            Inspect(ordinaryMany, Snapshot(mount), argumentPresentation: presentation, returnedPresentation: presentation);
            Check(ordinaryMany.Is("generate") && Coverage(ordinaryMany).GetArrayLength() == 0,
                "ordinary successful controls do not accumulate an automatic-QA coverage ledger");
        }
        ordinaryMany.Model(new { action = "final", text = "The requested inspections completed." });
        Check(ordinaryMany.Step.Next is CompleteWorkflow { Success: true },
            "ordinary conversations are not terminated by the automatic-QA coverage record bound");

        var duplicateMounts = Automatic(approvedMounts: [mountA, mountA]);
        Check(duplicateMounts.Step.Next is CompleteWorkflow { Success: false },
            "duplicate host-approved mount provenance is rejected before a model effect");
        var externalMounts = Start(["plugin.controls"], [], approvedMounts: [mountA], exactRunId: "ordinary-metadata");
        Check(externalMounts.Step.Next is CompleteWorkflow { Success: false },
            "ordinary metadata cannot acquire approved-mount service provenance");


        void Reopen(Fixture fixture, string[]? expected, PluginControlSnapshot before, PluginControlSnapshot after,
            string status = "completed", string[]? missing = null, bool partial = false,
            ToolExecutionOutcome? outcome = null, string provider = "cerebral.plugin.controls",
            string package = fingerprint, bool recreated = true, int? policy = PluginControlAssertions.CurrentPolicyVersion,
            string nativeStatus = "completed", string? approval = null)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["presentationId"] = approval ?? PluginControlReferences.ReviewedReference,
                ["mountId"] = mountA, ["operation"] = "reopen"
            };
            if (expected is not null) arguments["expectedTexts"] = expected;
            fixture.Model(new { action = "tool", name = "plugin.controls", arguments });
            var receipt = NextReceipt();
            fixture.Tool(status == "completed", JsonSerializer.Serialize(new
            {
                receipt, presentationId = approval ?? presentation, mountId = mountA, operation = "reopen",
                status, nativeStatus, sourceManifest, packageFingerprint = package,
                assertion = expected is null ? null : new
                {
                    status = status == "completed" ? "passed" : status == "failed" ? "failed" : "inconclusive",
                    expectedTexts = expected, missingTexts = missing ?? [], policyVersion = policy
                },
                observation = new PluginControlObservation(before, after, InstanceRecreated: recreated),
                error = status == "completed" ? null : "Assertion did not establish persistence."
            }, CerebralJson.Options), outcome: outcome ?? (status == "completed" ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed),
                provider: provider, requestId: receipt, partial: partial);
        }

        const string note = "First café line\nSecond line ñ";
        var noteBefore = Snapshot(mountA, buttons: [new(button, true, true)], fields: [new(fieldA, note, true, true, false)], texts: ["Ready"]);
        var noteAfter = Snapshot(mountA, buttons: [new(button, true, true)], fields: [new(fieldB, note, true, true, false)], texts: ["Ready"]);
        string[] lineExpectations = ["First café line", "Second line ñ"];
        Fixture InvalidReopenSetup()
        {
            var fixture = Automatic();
            Invoke(fixture, mountA, noteBefore, button);
            Reopen(fixture, lineExpectations, noteBefore, noteAfter, "uncertain", lineExpectations);
            Inspect(fixture, noteBefore);
            return fixture;
        }

        var invalidSetup = InvalidReopenSetup();
        Check(Failures(invalidSetup).GetArrayLength() == 1 &&
              Failures(invalidSetup)[0].GetProperty("missingBeforeExpectations").GetBoolean() &&
              Failures(invalidSetup)[0].GetProperty("completeObservation").GetBoolean(),
            "individual lines absent from a complete multiline before-field classify only invalid assertion setup, not a persistence pass");
        invalidSetup.Model(new { action = "final", text = "Persistence passed." });
        Check(invalidSetup.Is("generate") && invalidSetup.LastContributionText.Contains("source mutation is not required", StringComparison.Ordinal),
            "an invalid setup blocks final success while requesting an exact corrected native test instead of mandatory source changes");
        Reopen(invalidSetup, [note], noteBefore, noteAfter);
        Check(Failures(invalidSetup).GetArrayLength() == 0,
            "a fully passed exact whole-value reopen resolves only invalid setup on the same installed package");
        invalidSetup.Model(new { action = "final", text = "The saved Unicode multiline value survived a fresh editable view." });
        Check(invalidSetup.Step.Next is CompleteWorkflow { Success: true },
            "corrected declared native evidence and delivered interaction allow normal QA completion");

        foreach (var lane in new[] { "testless", "static-only", "partial", "limited", "unknown", "not-recreated", "legacy-policy", "foreign", "wrong-package", "wrong-approval", "fields-disappeared", "contradictory-native" })
        {
            var unresolved = InvalidReopenSetup();
            var after = lane == "limited" ? Snapshot(mountA, fields: [new(fieldB, note, true, true, false)], limited: true) : noteAfter;
            Reopen(unresolved, lane == "testless" ? null : lane is "static-only" or "fields-disappeared" ? ["Ready"] : [note],
                lane == "fields-disappeared" ? Snapshot(mountA, texts: ["Ready"]) : noteBefore, after,
                lane == "unknown" ? "uncertain" : "completed", partial: lane == "partial",
                outcome: lane == "unknown" ? ToolExecutionOutcome.Unknown : null,
                provider: lane == "foreign" ? "third-party" : "cerebral.plugin.controls",
                package: lane == "wrong-package" ? new string('b', 64) : fingerprint,
                recreated: lane != "not-recreated", policy: lane == "legacy-policy" ? null : PluginControlAssertions.CurrentPolicyVersion,
                approval: lane == "wrong-approval" ? oldPresentation : null,
                nativeStatus: lane == "contradictory-native" ? "claimed" : "completed");
            Check(Failures(unresolved).EnumerateArray().Any(item =>
                    item.TryGetProperty("missingBeforeExpectations", out var setup) && setup.GetBoolean() &&
                    item.GetProperty("expectedTexts").EnumerateArray().Select(value => value.GetString()).SequenceEqual(lineExpectations)),
                lane + " reopen cannot resolve invalid setup without full current-policy same-package editable evidence");
        }

        foreach (var lane in new[] { "partial", "unknown", "not-recreated", "limited", "legacy-policy", "wrong-missing", "foreign" })
        {
            var conservative = Automatic();
            Invoke(conservative, mountA, noteBefore, button);
            var after = lane == "limited" ? Snapshot(mountA, fields: [new(fieldB, note, true, true, false)], limited: true) : noteAfter;
            Reopen(conservative, lineExpectations, noteBefore, after, "uncertain",
                lane == "wrong-missing" ? ["Ready"] : lineExpectations, partial: lane == "partial",
                outcome: lane == "unknown" ? ToolExecutionOutcome.Unknown : null,
                provider: lane == "foreign" ? "third-party" : "cerebral.plugin.controls",
                recreated: lane != "not-recreated", policy: lane == "legacy-policy" ? null : PluginControlAssertions.CurrentPolicyVersion);
            Check(Failures(conservative).EnumerateArray().All(item =>
                    !item.TryGetProperty("missingBeforeExpectations", out var flag) || !flag.GetBoolean()),
                lane + " observation cannot acquire invalid-setup provenance");
        }


        var staticSetup = Automatic();
        Reopen(staticSetup, ["Missing"], staticView, staticView, "uncertain", ["Missing"]);
        Check(Failures(staticSetup)[0].GetProperty("missingBeforeExpectations").GetBoolean() &&
              !Failures(staticSetup)[0].TryGetProperty("setupHadEditableFields", out _),
            "originally static invalid setup has no invented editable-field provenance");
        Reopen(staticSetup, ["Ready"], staticView, staticView);
        Check(Failures(staticSetup).GetArrayLength() == 0,
            "a corrected exact current-policy static assertion can resolve originally static invalid setup");


        var staticSameExpected = InvalidReopenSetup();
        var staticLines = Snapshot(mountA, texts: lineExpectations);
        Reopen(staticSameExpected, lineExpectations, staticLines, staticLines);
        Check(Failures(staticSameExpected).GetArrayLength() == 1 &&
              Failures(staticSameExpected)[0].GetProperty("setupHadEditableFields").GetBoolean(),
            "same originally missing expectations cannot bypass editable provenance through the legacy exact-expectation clearing path");

        var repeatedStaticSetup = InvalidReopenSetup();
        Reopen(repeatedStaticSetup, lineExpectations, staticView, staticView, "uncertain", lineExpectations);
        Check(Failures(repeatedStaticSetup)[0].GetProperty("setupHadEditableFields").GetBoolean(),
            "a later same-key invalid setup on a static view preserves the earlier editable witness requirement");
        Reopen(repeatedStaticSetup, lineExpectations, staticLines, staticLines);
        Check(Failures(repeatedStaticSetup).GetArrayLength() == 1,
            "repeated invalid setup cannot downgrade original editable provenance and permit a static-only pass");

        var actualFailure = Automatic();
        Invoke(actualFailure, mountA, noteBefore, button);
        var lostAfter = Snapshot(mountA, fields: [new(fieldB, "", true, true, false)], texts: ["Ready"]);
        Reopen(actualFailure, [note], noteBefore, lostAfter, "failed", [note]);
        Reopen(actualFailure, ["Ready"], noteBefore, noteAfter);
        Check(Failures(actualFailure).GetArrayLength() == 1 &&
              !Failures(actualFailure)[0].TryGetProperty("missingBeforeExpectations", out _),
            "a genuine missing-after persistence failure cannot be cleared by changing its assertions");
        Reopen(actualFailure, [note], Snapshot(mountA, fields: [new(fieldA, "", true, true, false)]), noteAfter,
            "uncertain", [note]);
        Check(!Failures(actualFailure)[0].TryGetProperty("missingBeforeExpectations", out _),
            "later invalid setup with the same declared tuple cannot downgrade an existing genuine or legacy failure");

        var legacySetup = InvalidReopenSetup();
        var legacyState = JsonNode.Parse(legacySetup.Step.State.GetRawText())!.AsObject();
        legacyState["nativeControlFailures"]![0]!.AsObject().Remove("missingBeforeExpectations");
        legacyState["nativeControlFailures"]![0]!.AsObject().Remove("setupHadEditableFields");
        legacySetup.RestoreState(CerebralJson.Element(legacyState));
        Reopen(legacySetup, [note], noteBefore, noteAfter);
        Check(Failures(legacySetup).GetArrayLength() == 1,
            "retained entries lacking setup classification remain conservative and are not silently reinterpreted");

        var preservedSetup = InvalidReopenSetup();
        var exactSetup = Failures(preservedSetup).GetRawText();
        preservedSetup.RejectContext(ContextProjectionStatus.NeedsCompaction);
        preservedSetup.CompactSuccess();
        Check(Failures(preservedSetup).GetRawText() == exactSetup,
            "compaction preserves setup provenance and the original receipt and declared expectations without rewriting evidence");

        var malformedSetup = InvalidReopenSetup();
        var malformedSetupState = JsonNode.Parse(malformedSetup.Step.State.GetRawText())!.AsObject();
        malformedSetupState["nativeControlFailures"]![0]!["completeObservation"] = false;
        malformedSetup.RestoreState(CerebralJson.Element(malformedSetupState));
        malformedSetup.Model(new { action = "final", text = "Malformed setup must not pass." });
        Check(malformedSetup.Step.Next is CompleteWorkflow { Success: false } &&
              malformedSetup.Summary.Contains("saved conversation state", StringComparison.Ordinal),
            "setup provenance without complete native observation fails closed during saved-state validation");

        checks += RunStaleControlCasChecks();
        return checks;
    }
}
