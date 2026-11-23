using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker.Tests;

/// <summary>Original-trace policy regression with deterministic receipts; no model, process or file tool executes.</summary>
public static partial class ProgrammingHarnessTests
{
    public static int RunLegacyCompletionChecks()
    {
        var checks = 0;
        LegacyCompletionPolicy((value, message) =>
        {
            if (!value) throw new InvalidOperationException("Legacy completion: " + message);
            checks++;
        });
        return checks;
    }

    static partial void LegacyCompletionPolicy(Action<bool, string> check)
    {
        int Number(Fixture fixture, string field) => fixture.Step.State.GetProperty(field).GetInt32();
        bool IsTool(Fixture fixture, string name) => fixture.IsTool && fixture.PendingTool == name;
        bool Passed(Fixture fixture) => fixture.Step.Next is CompleteWorkflow { Success: true };
        bool Failed(Fixture fixture) => fixture.Step.Next is CompleteWorkflow { Success: false };
        void ProjectRead(Fixture fixture, string project)
        {
            check(IsTool(fixture, "file.read"), "early done requests an ordinary project metadata tool effect");
            var args = fixture.Command.Arguments.GetProperty("arguments");
            check(args.GetProperty("path").GetString() == Path.GetFullPath(Path.Combine(fixture.Root, project)) &&
                args.GetProperty("startLine").GetInt32() == 1 && args.GetProperty("lineCount").GetInt32() == 128 &&
                args.EnumerateObject().Count() == 3, "metadata read is exactly the accepted project and a bounded range");
            check(Number(fixture, "repairs") == 0 && Number(fixture, "verificationCycles") == 1,
                "valid early completion uses one verification cycle and no invalid-action correction");
        }
        const string project = "build/Programming/TaskStages/VerificationCadence.Owner/ProgrammingVerificationCadence.Checks.csproj";
        // Actual original sequence: build348e, doneb367, redundant build991c/e1d52, doneb026,
        // reads8e26/ff6e +4389/44eb, then missing-verb action34dd. The fixed trace diverges at
        // FIRST done: project metadata read, then outstanding run only; no second model-invented CLI.
        var trace = new Fixture(requiredChecks: ["build", "run"], selectedTools: ["file.read", "process.run"], projectFile: project);
        trace.Process(["build", "--project", project, "--configuration", "Release", "-v", "minimal"]);
        check(IsTool(trace, "process.run") && trace.Argv[0] == "build", "original build proposal keeps normal command normalization");
        trace.Tool(true, "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n", 0);
        var originalBuild = trace.Step.State.GetProperty("checks")[0].GetProperty("receiptId").GetString();
        trace.Model(new { action = "done", summary = "The build completed." });
        ProjectRead(trace, project);
        check(trace.CheckCount == 1, "metadata request does not erase the existing real build check");
        trace.Tool(true, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        check(IsTool(trace, "process.run") && trace.Argv[0] == "run" && trace.Argv.Contains("--project") && trace.Argv.Contains("--no-build"),
            "first completion progresses directly to the outstanding configured run and reuses its passing build");
        check(Number(trace, "repairs") == 0 && trace.CheckCount == 1 && Number(trace, "verificationCycles") == 1,
            "real project-read progress does not spend another correction or verification cycle");
        trace.Tool(true, "All qualified checks passed.\n", 0);
        check(Passed(trace) && trace.CheckCount == 2 && Number(trace, "repairs") == 0,
            "real-shaped source/build/run receipts complete without another model decision");
        var completed = (CompleteWorkflow)trace.Step.Next;
        var facts = completed.Result.GetProperty("checks").EnumerateArray().ToArray();
        check(facts.Count(x => x.GetProperty("kind").GetString() == "build") == 1 &&
            facts.Single(x => x.GetProperty("kind").GetString() == "build").GetProperty("receiptId").GetString() == originalBuild &&
            facts.Single(x => x.GetProperty("kind").GetString() == "run").GetProperty("receiptId").GetString() != originalBuild,
            "completion retains the original build and a distinct confirmed run receipt");
        check(completed.Result.GetProperty("editCount").GetInt32() == 0 && completed.Result.GetProperty("readReceipts").GetArrayLength() == 1,
            "metadata read earns its actual receipt without claiming an implementation edit or review");


        foreach (var extension in new[] { ".sln", ".slnx" })
        {
            var solution = "Solutions/Fixture" + extension;
            var metadata = extension == ".sln" ? "Microsoft Visual Studio Solution File, Format Version 12.00" : "<Solution />";
            var solutionFlow = new Fixture(requiredChecks: ["build", "test"], projectFile: solution);
            solutionFlow.Model(new { action = "done", summary = "Verify the accepted solution" });
            ProjectRead(solutionFlow, solution);
            solutionFlow.Tool(true, metadata);
            check(IsTool(solutionFlow, "process.run") && solutionFlow.Argv[0] == "build" && solutionFlow.Argv[1] == solution &&
                solutionFlow.Step.State.GetProperty("readReceipts").GetArrayLength() == 1 && solutionFlow.CheckCount == 0 &&
                Number(solutionFlow, "editCount") == 0 && Number(solutionFlow, "repairs") == 0 && Number(solutionFlow, "verificationCycles") == 1,
                "exact accepted solution metadata advances to build with only one read receipt and no edit/check credit");
            solutionFlow.Tool(true, "Build succeeded", 0);
            check(IsTool(solutionFlow, "process.run") && solutionFlow.Argv[0] == "test" && solutionFlow.Argv[1] == solution &&
                solutionFlow.Argv.Contains("--no-build") && solutionFlow.CheckCount == 1,
                "solution build advances through the existing configured test dispatcher and reuses the passing build");
            solutionFlow.Tool(true, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 ms", 0);
            check(Passed(solutionFlow) && solutionFlow.CheckCount == 2 && Number(solutionFlow, "repairs") == 0 &&
                Number(solutionFlow, "verificationCycles") == 1 && Number(solutionFlow, "editCount") == 0,
                "both supported solution formats complete only after distinct passing build and test receipts");
            var solutionChecks = ((CompleteWorkflow)solutionFlow.Step.Next).Result.GetProperty("checks").EnumerateArray().ToArray();
            check(solutionChecks.Select(x => x.GetProperty("kind").GetString()).SequenceEqual(new[] { "build", "test" }) &&
                solutionChecks.Select(x => x.GetProperty("receiptId").GetString()).Distinct().Count() == 2 &&
                solutionChecks[1].GetProperty("testsPassed").GetInt32() == 2,
                "solution metadata creates neither a run check nor a fabricated passing test receipt");

            var preRead = new Fixture(requiredChecks: ["build"], projectFile: solution);
            preRead.Model(new { action = "tool", name = "file.read", arguments = new
            {
                path = Path.Combine(preRead.Root, "Solutions", "..", "Solutions", "Fixture" + extension)
            } });
            preRead.Tool(true, metadata);
            check(preRead.IsModel && preRead.Step.State.GetProperty("readReceipts").GetArrayLength() == 1 && preRead.CheckCount == 0 &&
                Number(preRead, "editCount") == 0,
                "a normalized exact accepted solution read earns only its completion-read prerequisite before done");
            preRead.Model(new { action = "done", summary = "Build the solution already read" });
            check(IsTool(preRead, "process.run") && preRead.Argv[0] == "build" && Number(preRead, "repairs") == 0,
                "an existing accepted solution receipt avoids a duplicate metadata request");

            var unrelated = new Fixture(requiredChecks: ["build"], projectFile: solution);
            unrelated.Model(new { action = "tool", name = "file.read", arguments = new
            {
                path = Path.Combine(unrelated.Root, "Other", "Fixture" + extension), startLine = 1, lineCount = 128
            } });
            unrelated.Tool(true, metadata);
            check(unrelated.IsModel && unrelated.Step.State.GetProperty("readReceipts").GetArrayLength() == 0 &&
                unrelated.CheckCount == 0 && Number(unrelated, "editCount") == 0,
                "a same-named unrelated solution metadata read earns no completion-read, edit or check credit");
            unrelated.Model(new { action = "done", summary = "Verify the accepted solution" });
            ProjectRead(unrelated, solution);
        }
        var unrelatedJson = new Fixture();
        unrelatedJson.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(unrelatedJson.Root, "settings.json") } });
        unrelatedJson.Tool(true, "{}");
        check(unrelatedJson.IsModel && unrelatedJson.Step.State.GetProperty("readReceipts").GetArrayLength() == 0 && unrelatedJson.CheckCount == 0,
            "generic unrelated metadata read retains its existing zero completion/check credit");
        unrelatedJson.Model(new { action = "done", summary = "Verify the accepted project" });
        ProjectRead(unrelatedJson, "Hello.csproj");

        var initial = new Fixture(); initial.Model(new { action = "done", summary = "Ready" }); ProjectRead(initial, "Hello.csproj");
        initial.Tool(true, "<Project />");
        check(IsTool(initial, "process.run") && initial.Argv[0] == "build", "no-build completion starts the explicit required build");
        initial.Tool(true, "Build succeeded", 0);
        check(IsTool(initial, "process.run") && initial.Argv[0] == "run", "required checks retain deterministic order");
        initial.Tool(true, "Run passed", 0); check(Passed(initial), "all required receipts complete the initial early-done path");

        var alreadyRead = new Fixture(); alreadyRead.Read(); alreadyRead.Dotnet("build"); alreadyRead.Tool(true, "Built", 0);
        alreadyRead.Model(new { action = "done", summary = "Complete" });
        check(IsTool(alreadyRead, "process.run") && alreadyRead.Argv[0] == "run", "a valid source receipt avoids another metadata read");
        var runOnly = new Fixture(requiredChecks: ["run"]); runOnly.Model(new { action = "done", summary = "Run it" });
        runOnly.Tool(true, "<Project />");
        check(IsTool(runOnly, "process.run") && runOnly.Argv[0] == "run" && !runOnly.Argv.Contains("--no-build"),
            "run-only contract does not invent a separately required build or an unproved no-build option");
        runOnly.Tool(true, "Run passed", 0); check(Passed(runOnly), "run-only receipt satisfies only its accepted check contract");

        foreach (var selected in new[] { new[] { "process.run" }, new[] { "file.read" } })
        {
            var rejected = false;
            try { _ = new Fixture(selectedTools: selected); }
            catch (ArgumentException error)
            { rejected = error.Message.Contains("requires file.read and process.run", StringComparison.Ordinal); }
            check(rejected, "missing selected source/process authority is rejected at input admission before any effect");
        }
        var badRead = new Fixture(); badRead.Model(new { action = "done", summary = "Ready" }); badRead.Tool(false, "Read denied");
        check(badRead.IsModel && badRead.CheckCount == 0 && badRead.Step.State.GetProperty("readReceipts").GetArrayLength() == 0,
            "failed metadata read produces no source or check credit and returns its real failure");
        var cancelled = new Fixture(); cancelled.Model(new { action = "done", summary = "Ready" }); cancelled.Cancel();
        check(Failed(cancelled), "cancellation stops the pending metadata path before another effect");
        var unknown = new Fixture(); unknown.Model(new { action = "done", summary = "Ready" }); unknown.Uncertain();
        check(Failed(unknown) && unknown.CheckCount == 0, "unknown metadata outcome stops without replay or inferred credit");
        var steer = new Fixture(); steer.Model(new { action = "done", summary = "Ready" }); steer.QueueOwner("Inspect the current project before completing.");
        steer.Tool(true, "<Project />");
        check(steer.IsModel && Number(steer, "ownerSteeringCount") == 1 && steer.CheckCount == 0,
            "new owner input takes precedence over automatic checks after the project read");

        var failedRun = new Fixture(); failedRun.Read(); failedRun.Dotnet("build"); failedRun.Tool(true, "Built", 0);
        failedRun.Model(new { action = "done", summary = "Ready" }); failedRun.Tool(false, "Actual check failed", 1);
        check(failedRun.IsModel && failedRun.CheckCount == 1 && Number(failedRun, "verificationCycles") == 1,
            "failed configured run returns to model repair with actual failure rather than automatically retrying");
        failedRun.Model(new { action = "done", summary = "Retry the explicit check" });
        check(IsTool(failedRun, "process.run") && failedRun.Argv[0] == "run" && Number(failedRun, "verificationCycles") == 2,
            "an explicit later verification boundary retains its existing cycle allowance and no duplicate build");
        var uncertainRun = new Fixture(); uncertainRun.Read(); uncertainRun.Dotnet("build"); uncertainRun.Tool(true, "Built", 0);
        uncertainRun.Model(new { action = "done", summary = "Ready" }); uncertainRun.Uncertain();
        check(Failed(uncertainRun) && uncertainRun.CheckCount == 1, "unknown required-check outcome is never retried or credited");
        var boundedReads = new Fixture();
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            boundedReads.Model(new { action = "done", summary = "Ready" });
            check(IsTool(boundedReads, "file.read") && Number(boundedReads, "verificationCycles") == cycle, "each explicit completion attempt remains bounded");
            boundedReads.Tool(false, "Project read still failed");
        }
        boundedReads.Model(new { action = "done", summary = "Another attempt" });
        check(Failed(boundedReads) && Number(boundedReads, "verificationCycles") == 3 && Number(boundedReads, "repairs") == 0,
            "three failed verification cycles stop before a fourth metadata or process request");

        var invalid = new Fixture(); invalid.Read();
        var noVerb = new[] { "--project", "Hello.csproj", "--configuration", "Release", "-v", "minimal" };
        invalid.Process(noVerb);
        check(invalid.IsModel && Number(invalid, "repairs") == 1 && invalid.CheckCount == 0, "actual missing-verb proposal remains rejected without guessing run");
        invalid.Process(noVerb);
        check(invalid.IsModel && Number(invalid, "repairs") == 2 && invalid.CheckCount == 0, "second invalid-action correction retains its original limit");
        invalid.Process(noVerb);
        check(Failed(invalid) && Number(invalid, "repairs") == 2 && invalid.CheckCount == 0,
            "third ambiguous CLI fails before dispatch instead of silently adding a verb");
    }
}
