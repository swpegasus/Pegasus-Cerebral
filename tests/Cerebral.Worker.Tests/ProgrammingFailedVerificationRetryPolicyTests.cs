using System.Text.Json;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic policy/effect fixtures; no process, model or retained run is executed.</summary>
public static partial class ProgrammingHarnessTests
{
    static partial void FailedVerificationRetryPolicy(Action<bool, string> check)
    {
        int Failures(Fixture f) => f.Step.State.TryGetProperty("failedVerificationChecks", out var rows) ? rows.GetArrayLength() : 0;
        Fixture Failed(string kind = "run", string shape = "complete")
        {
            var f = new Fixture(requiredChecks: ["build", kind], guardFailedChecks: true);
            f.Dotnet(kind); f.VerificationFailure(shape);
            return f;
        }
        using var plugin = new ProgrammingServicePlugin();
        var exports = ((IAgentHarnessModule)plugin).Harnesses;
        check(exports.Count(item => item.Descriptor.Id == ProgrammingAgentHarness.Id &&
            item.Descriptor.Version == ProgrammingVerificationRetryHarness.Version) == 1 &&
            exports.Any(item => item.Descriptor.Version == ProgrammingAgentHarness.Version) &&
            exports.Any(item => item.Descriptor.Version == ProgrammingPreparedInterventionHarness.Version),
            "new immutable guarded edition is exported alongside both unchanged Develop editions");
        var old = new Fixture(); old.Dotnet("run"); old.VerificationFailure(); old.Dotnet("run");
        check(old.IsTool && !old.Step.State.TryGetProperty("failedVerificationChecks", out _),
            "legacy policy and serialized continuation shape retain their original behavior");
        check(new ProgrammingVerificationRetryHarness().Advance(new(new WorkflowRun
            { Id = "wrong-edition", Submission = old.OwnerSubmission(), State = old.Step.State }))
            .Next is CompleteWorkflow { Success: false }, "guarded wrapper refuses a different accepted edition");

        foreach (var kind in new[] { "run", "test" })
        {
            var repeated = Failed(kind);
            check(Failures(repeated) == 1 && repeated.CheckCount == 0 && repeated.IsModel &&
                repeated.Messages.Contains("Confirmed failed checks", StringComparison.Ordinal),
                "one confirmed failure returns retained repair guidance without passing credit: " + kind);
            repeated.Process([kind, "--project", Path.Combine(repeated.Root, "Hello.csproj"), "-c", "Release",
                "--verbosity", "quiet", "--no-restore", "--disable-build-servers"], 120);
            check(repeated.IsModel && repeated.Messages.Contains("Another equivalent check was not dispatched", StringComparison.Ordinal) &&
                repeated.Messages.Contains("receipt fixture-effect-", StringComparison.Ordinal) &&
                repeated.Step.State.GetProperty("repairs").GetInt32() == 1 && Failures(repeated) == 1,
                "second equivalent check is blocked before host launch despite reporting/project/timeout variants: " + kind);
            repeated.Dotnet(kind); repeated.Dotnet(kind);
            check(repeated.Step.Next is CompleteWorkflow { Success: false } && Failures(repeated) == 1,
                "repeated rejected proposals remain bounded by the original correction limit: " + kind);

            var build = Failed(kind); build.Dotnet("build"); build.Tool(true, "Built unchanged inputs", 0); build.Dotnet(kind);
            check(build.IsModel && Failures(build) == 1,
                "successful rebuild of unchanged inputs cannot unlock failed verification: " + kind);
            var auto = Failed(kind); auto.Read(); auto.Dotnet("build"); auto.Tool(true, "Built", 0);
            auto.Model(new { action = "done", summary = "PASS: I already ran it successfully" });
            check(auto.Step.Next is CompleteWorkflow { Success: false } && auto.CheckCount == 1 && Failures(auto) == 1,
                "claimed success cannot bypass the retry guard through automatic completion verification: " + kind);
        }

        var lanes = new Fixture(guardFailedChecks: true);
        foreach (var kind in new[] { "run", "test" })
            foreach (var configuration in new[] { "Release", "Debug" })
            { lanes.Dotnet(kind, configuration); check(lanes.IsTool, "distinct check/configuration remains independently admissible"); lanes.VerificationFailure(); }
        check(Failures(lanes) == 4, "failure memory is bounded to four admitted kind/configuration lanes");
        lanes.Dotnet("run"); check(lanes.IsModel, "other failures cannot evict the original failed run lane");
        var negative = Failed(shape: "negative-exit"); negative.Dotnet("run");
        check(negative.IsModel && Failures(negative) == 1, "a confirmed nonzero negative child exit also blocks an unchanged retry");
        foreach (var corruption in new[] { "duplicate", "kind", "configuration", "zero-exit", "receipt", "null" })
        {
            var invalid = Failed(); invalid.OwnerSeedState(node =>
            {
                var rows = node["failedVerificationChecks"]!.AsArray();
                if (corruption == "duplicate") rows.Add(rows[0]!.DeepClone());
                else if (corruption == "null") rows[0] = null;
                else rows[0]![corruption == "zero-exit" ? "exitCode" : corruption == "receipt" ? "receiptId" : corruption] =
                    corruption == "zero-exit" ? System.Text.Json.Nodes.JsonValue.Create(0) : System.Text.Json.Nodes.JsonValue.Create("");
            });
            invalid.Dotnet("run");
            check(invalid.Step.Next is CompleteWorkflow { Success: false },
                "inconsistent restored failure observations fail closed before dispatch: " + corruption);
        }

        foreach (var shape in new[] { "skip", "identical", "missing", "truncated", "partial", "wrong-path", "wrong-request" })
        {
            var unchanged = Failed(); unchanged.GuardWriteResult(shape); unchanged.Dotnet("run");
            check(unchanged.IsModel && Failures(unchanged) == 1,
                "unproved/no-op mutation cannot unlock a failed run even if legacy EditCount advances: " + shape);
        }
        var unrelated = Failed(); unrelated.VerificationSourceWrite("Notes.txt"); unrelated.Dotnet("run");
        check(unrelated.IsModel && Failures(unrelated) == 1,
            "a real changed unrelated file cannot unlock a source repair requirement");
        var rejected = Failed(); rejected.QueueGuardWrite(Path.Combine(rejected.Root, "Program.cs"), "new", "replace", new string('a', 64));
        rejected.Tool(false, "Rejected write"); rejected.Dotnet("run");
        check(rejected.IsModel && Failures(rejected) == 1, "failed source write preserves the observation");
        foreach (var shape in new[] { "changed", "create" })
        {
            var changed = Failed(); changed.InputWitness(); changed.GuardWriteResult(shape);
            check(Failures(changed) == 0, "complete matching changed-byte/source-creation proof retires failure lanes: " + shape);
            changed.Dotnet("run"); check(changed.IsTool, "proved source progress permits a new verification launch: " + shape);
        }
        var range = new Fixture(selectedTools: ["file.read", "file.edit-range", "process.run"], guardFailedChecks: true);
        range.Dotnet("run"); range.VerificationFailure(); range.InputWitness(); range.VerificationRangeEdit(); range.Dotnet("run");
        check(range.IsTool && Failures(range) == 0, "matching complete range edit proof also unlocks verification");
        var edited = Failed(); edited.InputWitness(); edited.VerificationSourceEdit(); edited.Dotnet("run");
        check(edited.IsTool && Failures(edited) == 0, "matching file.edit changed-byte proof unlocks the corrected-source check");

        var context = Failed(); context.ReadRange(1, 1); context.ReadRange(2, 1);
        context.ContextRejected(ContextProjectionStatus.NeedsCompaction); context.CompactSuccess();
        context.QueueOwner("Inspect the actual failure before another launch");
        context.Model(new { action = "done", summary = "old proposal" }); context.Dotnet("run");
        check(context.IsModel && Failures(context) == 1 && context.Step.State.GetProperty("ownerSteeringCount").GetInt32() == 1,
            "reads, compaction and incorporated owner steering preserve the failure guard");

        foreach (var shape in new[] { "truncated", "partial", "followup", "wrong-request", "zero", "missing-exit", "outcome" })
        {
            var unconfirmed = Failed(shape: shape);
            check(Failures(unconfirmed) == 0 && unconfirmed.CheckCount == 0,
                "unconfirmed child failure earns neither retry grouping nor passing credit: " + shape);
        }
        var unknown = Failed(); unknown.Dotnet("test"); unknown.Uncertain();
        check(unknown.Step.Next is CompleteWorkflow { Success: false } && Failures(unknown) == 1,
            "unknown effect remains terminal and cannot reset prior failed verification");
        var cancelled = Failed(); cancelled.Cancel();
        check(cancelled.Step.Next is CompleteWorkflow { Success: false } && Failures(cancelled) == 1,
            "cancellation remains terminal without dispatching another check");

        // Root818 B's observed sequence, with deterministic matching tool receipts. The original
        // held effect/run is never resumed: this is a fresh policy fixture of the decision boundary.
        var trace = new Fixture(guardFailedChecks: true); trace.Read();
        trace.Dotnet("build"); trace.Tool(true, "Built", 0);
        trace.Process(["run", "--project", "Hello.csproj", "--configuration", "Release", "--disable-build-servers", "--verbosity", "quiet", "--no-build"]);
        trace.VerificationFailure(); trace.Process(["run", "--project", "Hello.csproj", "--configuration", "Release", "--disable-build-servers", "--verbosity", "quiet", "--no-build"]);
        check(trace.IsModel && Failures(trace) == 1 && trace.CheckCount == 1,
            "Root818 duplicate baseline divergence is rejected before the second host launch");
        trace.InputWitness(); trace.GuardWriteResult("changed"); trace.Dotnet("build"); trace.Tool(true, "Rebuilt", 0);
        trace.Model(new { action = "done", summary = "PASS: all checks already succeeded" });
        check(trace.IsTool && trace.Argv[0] == "run" && trace.Argv.Contains("--no-build") && trace.CheckCount == 1,
            "a claimed run remains pending, unexecuted and uncredited until its own effect result");
        trace.Tool(true, "PASS stable-unique-lines\n", 0);
        check(trace.Step.Next is CompleteWorkflow { Success: true } completion &&
            completion.Result.GetProperty("checks").GetArrayLength() == 2 &&
            completion.Result.GetProperty("checks").EnumerateArray().Single(row => row.GetProperty("kind").GetString() == "run")
                .GetProperty("receiptId").GetString() != "fixture-effect-1",
            "only the new executed exit-zero receipt satisfies final run verification");
        var unknownFinal = new Fixture(guardFailedChecks: true); unknownFinal.Read();
        unknownFinal.Dotnet("build"); unknownFinal.Tool(true, "Built", 0);
        unknownFinal.Model(new { action = "done", summary = "PASS before any run" }); unknownFinal.Uncertain();
        check(unknownFinal.Step.Next is CompleteWorkflow { Success: false } && unknownFinal.CheckCount == 1,
            "an unknown final run cannot turn a claimed PASS into completion or replay");
    }

    private sealed partial class Fixture
    {
        public void VerificationFailure(string shape = "complete") => Result(WorkflowEffectState.Failed, CerebralJson.Element(new
        {
            name = "process.run", content = "{\"exitCode\":1,\"stdout\":\"\",\"stderr\":\"FAIL case-fold\\n\"}",
            truncated = shape == "truncated", followUpRequired = shape == "followup",
            evidence = new ToolExecutionEvidence(shape == "outcome" ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
                "deterministic-fixture", RequestId: shape == "wrong-request" ? "different-effect" : null,
                ExitCode: shape == "missing-exit" ? null : shape == "zero" ? 0 : shape == "negative-exit" ? -1 : 1, Partial: shape == "partial")
        }), "Actual check failed");

        public void VerificationSourceWrite(string file)
        {
            var path = Path.Combine(Root, file);
            QueueGuardWrite(path, "changed", "replace", new string('a', 64));
            Tool(true, JsonSerializer.Serialize(new { path, bytes = 7, sha256 = new string('b', 64) }));
        }

        public void VerificationSourceEdit()
        {
            var path = Path.Combine(Root, "Program.cs");
            Model(new { action = "tool", name = "file.edit", arguments = new
                { path, oldText = "broken", newText = "fixed", expectedSha256 = new string('a', 64) } });
            Tool(true, JsonSerializer.Serialize(new { path, bytes = 5, sha256 = new string('b', 64) }));
        }

        public void VerificationRangeEdit()
        {
            var path = Path.Combine(Root, "Program.cs");
            Model(new { action = "tool", name = "file.edit-range", arguments = new
                { path, expectedSha256 = new string('a', 64), startLine = 1, lineCount = 1, newText = "changed\n" } });
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
            {
                name = "file.edit-range", content = JsonSerializer.Serialize(new
                    { path, sha256Before = new string('a', 64), sha256 = new string('b', 64), bytes = 8, startLine = 1, lineCount = 1, changed = true }),
                truncated = false, followUpRequired = false,
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "deterministic-fixture", Partial: false)
            }));
        }
    }
}
