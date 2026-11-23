using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunInvokePostconditionOnlyAsync()
    {
        RunInvokePostconditionChecks();
        return Task.CompletedTask;
    }

    private static int RunInvokePostconditionChecks()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Conversation invoke postcondition: " + name);
            checks++;
        }
        const string presentation = "77777777777777777777777777777777";
        const string mount = "note.main", button = "Restore", field = "88888888888888888888888888888888";
        const string saved = "café saved\nsecond line", unsaved = "unsaved draft";
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralInvokePostcondition", "Desktop", "desktop-plugin.json"));
        var schema = CerebralJson.Element(new { type = "object" });
        Fixture Start()
        {
            var required = new[] { "interface.status", "plugin.controls" };
            var fixture = new Fixture(warm: false, input: new { requiredSuccessfulTools = required },
                requestId: "plugin-tests-" + presentation, caller: new(EventActorKind.Service, "cerebral.plugin-tests"),
                hostAddedRequiredTools: required, hostPluginTestMountIds: [mount]);
            fixture.History([]); fixture.Catalog(forcedNames: required); fixture.Catalog(exact: true, inputSchema: schema);
            CompleteInitialQaObservationFixture(fixture);
            fixture.Model(new { action = "tool", name = "interface.status", arguments = new { requestId = "reviewed" } });
            fixture.Tool(true, "{}", provider: "cerebral.interface", requestId: presentation);
            return fixture;
        }
        void Invoke(Fixture fixture, IReadOnlyList<string>? expected, string after, bool success = true,
            string requestedButton = button, string status = "completed", string assertionStatus = "passed", bool limited = false)
        {
            var arguments = new Dictionary<string, object>
            {
                ["presentationId"] = "reviewed", ["mountId"] = mount, ["operation"] = "invoke", ["buttonName"] = requestedButton
            };
            if (expected is not null) arguments["expectedTexts"] = expected;
            fixture.Model(new { action = "tool", name = "plugin.controls", arguments });
            var receipt = Guid.NewGuid().ToString("N");
            var before = new PluginControlSnapshot(mount, ["Ready"], [new(requestedButton, true, true)])
                { Fields = [new(field, unsaved, true, true, false)] };
            var afterView = new PluginControlSnapshot(mount, ["Ready"], [new(requestedButton, true, true)], limited)
                { Fields = [new(field, after, true, true, false)] };
            fixture.Tool(success, JsonSerializer.Serialize(new
            {
                receipt, presentationId = presentation, mountId = mount, operation = "invoke", status,
                nativeStatus = "completed", sourceManifest = source, packageFingerprint = new string('a', 64),
                observation = new PluginControlObservation(before, afterView, ClickDelivered: true),
                assertion = expected is null ? null : new
                {
                    status = assertionStatus, expectedTexts = expected,
                    missingTexts = assertionStatus == "failed" ? expected : Array.Empty<string>(),
                    message = assertionStatus == "passed" ? "Actual declared after-values were visible." : "Actual after-value did not establish this postcondition."
                },
                error = success ? null : "Observed invocation postcondition failure."
            }, CerebralJson.Options), provider: "cerebral.plugin.controls", requestId: receipt);
        }
        JsonElement Failures(Fixture fixture) => fixture.Step.State.GetProperty("nativeControlFailures");
        var observed = Start();
        Check(observed.SystemText.Contains("each operation requested in the original task", StringComparison.Ordinal) &&
            observed.SystemText.Contains("Minimum interaction is not full behavior coverage", StringComparison.Ordinal) &&
            observed.SystemText.Contains("save/reopen result does not exercise a separate restore action", StringComparison.Ordinal),
            "model policy distinguishes requested operation results from minimum mount interaction");
        Check(observed.SystemText.Contains("save operation while changed value A is current", StringComparison.Ordinal) &&
            observed.SystemText.Contains("confirm its actual result/errors before reopening", StringComparison.Ordinal) &&
            observed.SystemText.Contains("Unsaved B disappearing under an explicit-save contract is not a persistence defect", StringComparison.Ordinal),
            "model policy requires actual save confirmation before a declared persistence check");
        Invoke(observed, [saved], unsaved, success: false, status: "failed", assertionStatus: "failed");
        Check(Failures(observed).GetArrayLength() == 1 &&
            Failures(observed)[0].GetProperty("operation").GetString() == "invoke" &&
            Failures(observed)[0].GetProperty("buttonName").GetString() == button &&
            Failures(observed)[0].GetProperty("expectedTexts")[0].GetString() == saved,
            "an actual correlated invocation mismatch retains its exact requested button and expectation set");
        var retainedFailure = Failures(observed)[0].GetRawText();
        Invoke(observed, null, unsaved);
        observed.Model(new { action = "final", text = "All requested behavior passes." });
        Check(observed.Is("generate") && Failures(observed)[0].GetRawText() == retainedFailure &&
            observed.LastContributionText.Contains("native plugin control failures", StringComparison.Ordinal),
            "a testless successful click cannot discharge an asserted invocation failure or certify completion");
        observed.RejectContext(ContextProjectionStatus.NeedsCompaction); observed.CompactSuccess();
        Check(observed.Is("generate") && Failures(observed)[0].GetRawText() == retainedFailure,
            "compaction preserves the exact unresolved operation/button/postcondition identity");
        Invoke(observed, ["different declared value"], "different declared value");
        Check(Failures(observed)[0].GetRawText() == retainedFailure,
            "a weaker or different successful expectation cannot replace the retained failed requirement");
        Invoke(observed, [saved], saved, requestedButton: "Other operation");
        Check(Failures(observed)[0].GetRawText() == retainedFailure,
            "matching text from another button does not resolve the failed requested operation");
        Invoke(observed, [saved], saved);
        Check(Failures(observed).GetArrayLength() == 0,
            "only the exact correlated successful invocation and declaration resolve that retained failure");
        observed.Model(new { action = "final", text = "The declared restore postcondition was observed." });
        Check(observed.Step.Next is CompleteWorkflow { Success: true },
            "a resolved declared postcondition can complete the existing minimum coverage policy without claiming full behavior proof");
        var limited = Start();
        Invoke(limited, [saved], saved, success: false, status: "uncertain", assertionStatus: "inconclusive", limited: true);
        Check(Failures(limited).GetArrayLength() == 1 && !Failures(limited)[0].GetProperty("completeObservation").GetBoolean(),
            "limited invocation evidence remains an unresolved limitation and cannot establish complete repair proof");
        var disputed = Start();
        const string declaredWhole = "beta\nalpha", observedWhole = "alpha\nbeta\nbeta";
        Invoke(disputed, [declaredWhole], observedWhole, success: false, status: "failed", assertionStatus: "failed");
        var disputedFailure = Failures(disputed)[0].GetRawText();
        var disputedEffect = Failures(disputed)[0].GetProperty("effectId").GetString();
        var disputedReceipt = disputed.Step.State.GetProperty("toolReceipts").EnumerateArray()
            .Single(item => item.GetProperty("effectId").GetString() == disputedEffect).GetRawText();
        Check(Failures(disputed).GetArrayLength() == 1 &&
            Failures(disputed)[0].GetProperty("completeObservation").GetBoolean() &&
            !Failures(disputed)[0].TryGetProperty("invokeFieldLineSetup", out _),
            "a full-field order or duplicate disagreement is not automatically classified as structural assertion setup");
        Invoke(disputed, [observedWhole], observedWhole);
        Check(Failures(disputed)[0].GetRawText() == disputedFailure &&
            disputed.Step.State.GetProperty("nativeControlFinalCorrections").GetInt32() == 0 &&
            disputed.Step.State.GetProperty("toolReceipts").EnumerateArray()
                .Single(item => item.GetProperty("effectId").GetString() == disputedEffect).GetRawText() == disputedReceipt,
            "a fresh passing corrected declaration preserves the unresolved failure, correction count and immutable Failed receipt");
        disputed.Model(new { action = "final", text = "The changed expected value passed, so all behavior passes." });
        Check(disputed.Is("generate") && Failures(disputed)[0].GetRawText() == disputedFailure &&
            disputed.Step.State.GetProperty("nativeControlFinalCorrections").GetInt32() == 1 &&
            disputed.LastContributionText.Contains("does not by itself prove a plugin defect", StringComparison.Ordinal) &&
            disputed.LastContributionText.Contains("complete actual before/after observation and the original accepted behavior criteria before changing source", StringComparison.Ordinal) &&
            disputed.LastContributionText.Contains("do not claim the failure is resolved by a different passing expectation", StringComparison.Ordinal),
            "successful final stays rejected while guidance distinguishes an assertion disagreement from a proved source defect");
        Check(disputed.LastContributionText.Contains("after a reset, re-establish that test input", StringComparison.Ordinal) &&
            disputed.LastContributionText.Contains("describe the criterion-based correction and retained failed receipt", StringComparison.Ordinal) &&
            disputed.LastContributionText.Contains("use blocked and state the exact remaining evidence gap", StringComparison.Ordinal),
            "recovery requires fresh transformation input, an explicit unresolved correction and a truthful evidence gap");
        disputed.Model(new { action = "blocked", reason = "The original criteria do not yet establish the exact expected whole-field outcome." });
        Check(disputed.Step.Next is CompleteWorkflow { Success: false } &&
            Failures(disputed)[0].GetRawText() == disputedFailure &&
            disputed.Step.State.GetProperty("toolReceipts").EnumerateArray()
                .Single(item => item.GetProperty("effectId").GetString() == disputedEffect).GetRawText() == disputedReceipt,
            "a truthful blocked outcome retains the unresolved assertion and its original Failed receipt");
        checks += RunInvokeFieldLineSetupChecks();
        Console.WriteLine("Conversation invoke postconditions: " + checks + " deterministic harness checks passed; no native UI, provider dispatch or model execution.");
        return checks;
    }
}
