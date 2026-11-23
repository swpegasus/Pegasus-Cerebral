using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingStagedWorkflowPolicyTests
{
    public static int RunExpectedProcessOutcomePolicyChecks()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Expected process outcome policy: " + message);
        }
        StageFixture Baseline()
        {
            var fixture = new StageFixture(runtimeBaseline: true);
            fixture.ReadSources(); fixture.Process(0, "", "");
            return fixture;
        }
        var commandJson = new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false };
        string CommandHash(JsonElement arguments) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arguments, commandJson)))).ToLowerInvariant();
        JsonElement Feedback(StageFixture fixture) => fixture.Command.Arguments.GetProperty("context").GetProperty("contributions")
            .EnumerateArray().Single(source => source.GetProperty("id").GetString()!.StartsWith("stage-feedback.", StringComparison.Ordinal));
        string FeedbackText(StageFixture fixture) => Feedback(fixture).GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        JsonElement FeedbackStatus(StageFixture fixture)
        {
            var message = fixture.Command.Arguments.GetProperty("messages").EnumerateArray().Single(item => item.GetProperty("role").GetString() == "user");
            var text = message.GetProperty("content")[0].GetProperty("text").GetString()!;
            using var document = JsonDocument.Parse(text[(text.IndexOf('\n') + 1)..]);
            return document.RootElement.GetProperty("verificationFeedback").Clone();
        }
        var accepted = Baseline();
        Check(accepted.Command.Arguments.GetProperty("arguments").GetProperty("expectedExitCode").GetInt32() == 1,
            "only the policy-owned baseline check declares its configured exit");
        Check(accepted.Argv.SequenceEqual(new[] { Path.Combine(accepted.Root, "bin", "Release", "net10.0", "App.dll") }),
            "the declared direct DLL command is unchanged");
        accepted.Process(1, "", "EXPECTED FAILURE\n"); accepted.ReadSources();
        Check(accepted.IsModel && accepted.LastProcessState == WorkflowEffectState.Succeeded && accepted.LaunchFailures == 0 &&
            accepted.Stages.GetProperty("baselineCheck").GetProperty("exitCode").GetInt32() == 1,
            "accepted baseline retains actual nonzero exit while unlocking repair");

        var old = Baseline(); old.ChangePendingExpectation(null, remove: true);
        old.Process(1, "", "EXPECTED FAILURE\n"); old.ReadSources();
        Check(old.IsModel && old.LastProcessState == WorkflowEffectState.Failed && old.LaunchFailures == 1,
            "old pending arguments without the field keep default-zero outcome semantics");
        var oldPromoted = Baseline(); oldPromoted.ChangePendingExpectation(null, remove: true);
        oldPromoted.ProcessReceipt(1, WorkflowEffectState.Succeeded, ToolExecutionOutcome.Succeeded);
        Check(oldPromoted.Failed, "a missing expectation never retrospectively promotes a nonzero result");
        // A retained observation must keep the admitted legacy command fingerprint, not the
        // modern configured-exit representation, across subsequent source and model turns.
        var retained = Baseline(); retained.ChangePendingExpectation(null, remove: true);
        var admittedLegacy = retained.Command.Arguments.GetProperty("arguments").Clone();
        var legacyHash = CommandHash(admittedLegacy);
        var modernArguments = JsonNode.Parse(admittedLegacy.GetRawText())!.AsObject(); modernArguments["expectedExitCode"] = 1;
        var modernHash = CommandHash(CerebralJson.Element(modernArguments));
        retained.Process(1, "", "EXPECTED FAILURE\n"); retained.ReadSources();
        var observation = retained.Stages.GetProperty("latestVerification");
        var originalFeedback = FeedbackText(retained);
        var historicalEffect = observation.GetProperty("fact").GetProperty("effectId").GetString();
        Check(retained.IsModel && retained.LastProcessState == WorkflowEffectState.Failed && retained.LaunchFailures == 1 &&
            observation.GetProperty("stage").GetString() == "baseline-check" &&
            observation.GetProperty("fact").GetProperty("commandSha256").GetString() == legacyHash && legacyHash != modernHash,
            "legacy feedback preserves its original absent-field hash and failed/default-zero classification");
        Check(Feedback(retained).GetProperty("requirement").GetString() == "required" &&
            originalFeedback.Contains("EXPECTED FAILURE", StringComparison.Ordinal) &&
            !FeedbackStatus(retained).GetProperty("verificationCredit").GetBoolean(),
            "the legacy baseline observation retains required actual feedback without final verification credit");
        retained.Edit();
        Check(retained.IsModel && FeedbackStatus(retained).GetProperty("isHistorical").GetBoolean() &&
            retained.Stages.GetProperty("latestVerification").GetProperty("fact").GetProperty("commandSha256").GetString() == legacyHash &&
            FeedbackText(retained) == originalFeedback,
            "a confirmed edit keeps the exact legacy command and labels its prior diagnostic historical");
        retained.Model(new { action = "tool", name = "file.read", arguments = new { path = "Program.cs", startLine = 1, lineCount = 1 } });
        Check(retained.IsTool("file.read"), "the changed legacy-continuation source dispatches a fresh bounded file read");
        retained.ReadSources();
        Check(retained.IsModel && Feedback(retained).GetProperty("requirement").GetString() == "required" &&
            FeedbackText(retained) == originalFeedback && FeedbackStatus(retained).GetProperty("isHistorical").GetBoolean() &&
            retained.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null &&
            retained.Stages.GetProperty("currentCheck").ValueKind == JsonValueKind.Null,
            "an intervening file tool and later generation preserve historical legacy feedback without current proof");
        retained.Model(new { action = "done", summary = "Verify corrected current source" }); retained.CompleteVerification();
        Check(retained.Passed && retained.LaunchFailures == 1 &&
            retained.Stages.GetProperty("latestVerification").ValueKind == JsonValueKind.Null &&
            retained.Final.GetProperty("currentCheck").GetProperty("effectId").GetString() != historicalEffect &&
            retained.Final.GetProperty("currentCheck").GetProperty("buildEffectId").GetString() ==
            retained.Final.GetProperty("currentBuild").GetProperty("effectId").GetString(),
            "legacy compatibility never promotes the old result; completion requires new matched current build/check receipts");
        foreach (var corruption in new[] { "arbitrary", "timeout", "argv" })
        {
            var invalid = Baseline(); invalid.ChangePendingExpectation(null, remove: true);
            var admitted = JsonNode.Parse(invalid.Command.Arguments.GetProperty("arguments").GetRawText())!.AsObject();
            invalid.Process(1, "", "EXPECTED FAILURE\n"); invalid.ReadSources();
            if (corruption == "timeout") admitted["timeoutSeconds"] = admitted["timeoutSeconds"]!.GetValue<int>() + 1;
            if (corruption == "argv") admitted["arguments"]![0] = Path.Combine(invalid.Root, "another.dll");
            var wrongHash = corruption == "arbitrary" ? new string('a', 64) : CommandHash(CerebralJson.Element(admitted));
            invalid.ChangeState(node => node["stages"]!["latestVerification"]!["fact"]!["commandSha256"] = wrongHash);
            invalid.Model(new { action = "done", summary = "Must reject altered command provenance" });
            Check(invalid.Failed && invalid.Step.Next is not InvokeWorkflowCommand &&
                invalid.Final.GetProperty("summary").GetString()!.Contains("command binding", StringComparison.Ordinal),
                "legacy baseline compatibility rejects a different command fingerprint: " + corruption);
        }
        var compiler = new StageFixture(compilerBaseline: true); compiler.ReadSources();
        compiler.PaoCompiler($"{Path.Combine(compiler.Root, "Program.cs")}(1,1): error CS0103: The name 'missing' does not exist in the current context [{Path.Combine(compiler.Root, "App.csproj")}]\n");
        compiler.ReadSources();
        compiler.ChangeState(node => node["stages"]!["latestVerification"]!["fact"]!["commandSha256"] = legacyHash);
        compiler.Model(new { action = "done", summary = "Must reject a baseline-check hash on a compiler build" });
        Check(compiler.Failed && compiler.Step.Next is not InvokeWorkflowCommand &&
            compiler.Final.GetProperty("summary").GetString()!.Contains("command binding", StringComparison.Ordinal),
            "the exact legacy check fingerprint is not a fallback for another verification stage");

        foreach (var actual in new[] { 0, 2 })
        {
            var wrong = Baseline(); wrong.Process(actual, "", "EXPECTED FAILURE\n");
            Check(wrong.Failed && wrong.LastProcessState == WorkflowEffectState.Failed,
                "a mismatched child exit is failed even when that actual exit is zero");
        }
        var wrongText = Baseline(); wrongText.Process(1, "", "different failure\n");
        Check(wrongText.Failed, "accepted process exit alone grants no semantic baseline credit");

        foreach (var bad in new[] { "state", "outcome", "exit", "body", "partial", "truncated", "followup", "request", "provider", "unknown", "cancelled" })
        {
            var fixture = Baseline();
            fixture.ProcessReceipt(bad == "exit" ? 2 : 1,
                bad == "state" ? WorkflowEffectState.Failed : bad == "unknown" ? WorkflowEffectState.Unknown :
                bad == "cancelled" ? WorkflowEffectState.Cancelled : WorkflowEffectState.Succeeded,
                bad == "outcome" ? ToolExecutionOutcome.Failed : ToolExecutionOutcome.Succeeded,
                bodyExit: bad == "body" ? 0 : null, partial: bad == "partial", truncated: bad == "truncated",
                followUp: bad == "followup", request: bad == "request" ? "another-effect" : null,
                provider: bad == "provider" ? "arbitrary.provider" : "cerebral.process");
            Check(fixture.Failed, "inconsistent or incomplete receipt is rejected: " + bad);
        }
        foreach (var bad in new[] { "-1", "256", "1.5", "\"1\"", "null" })
        {
            var fixture = Baseline(); fixture.ChangePendingExpectation(JsonNode.Parse(bad));
            fixture.ProcessReceipt(1, WorkflowEffectState.Succeeded, ToolExecutionOutcome.Succeeded);
            Check(fixture.Failed, "malformed saved expected exit is rejected: " + bad);
        }
        var final = Baseline(); final.Process(1, "", "EXPECTED FAILURE\n"); final.ReadSources();
        final.Edit(); final.Model(new { action = "done", summary = "Ready" }); final.ReadSources();
        Check(!final.Command.Arguments.GetProperty("arguments").TryGetProperty("expectedExitCode", out _),
            "final build preserves its default-zero command shape");
        final.Process(0, "", ""); final.ReadSources();
        Check(!final.Command.Arguments.GetProperty("arguments").TryGetProperty("expectedExitCode", out _),
            "final check preserves its default-zero command shape");
        final.ChangePendingExpectation(JsonValue.Create(1));
        final.ProcessReceipt(1, WorkflowEffectState.Succeeded, ToolExecutionOutcome.Succeeded, stdout: "PASS\n", stderr: "");
        Check(final.Failed, "a nonzero final check cannot gain credit through an injected expectation");
        var build = new StageFixture(compilerBaseline: true); build.ReadSources();
        Check(!build.Command.Arguments.GetProperty("arguments").TryGetProperty("expectedExitCode", out _),
            "baseline compiler builds keep their original failed-effect contract");
        return checks;
    }
}
