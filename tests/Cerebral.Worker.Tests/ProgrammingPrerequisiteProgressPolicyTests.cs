using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic child-process receipts; no SDK, model or desktop is invoked.</summary>
public static partial class ProgrammingHarnessTests
{
    public static int RunPrerequisiteProgressChecks()
    {
        var count = 0;
        PrerequisiteProgressPolicy((valid, message) =>
        { if (!valid) throw new InvalidOperationException("Prerequisite progress: " + message); count++; });
        return count;
    }

    static partial void PrerequisiteProgressPolicy(Action<bool, string> check)
    {
        int Count(Fixture fixture, string configuration = "Release") =>
            fixture.Step.State.GetProperty("repeatedBuildFailures").EnumerateArray()
                .Where(item => item.GetProperty("configuration").GetString() == configuration &&
                    item.TryGetProperty("missingAssetsSha256", out var hash) && hash.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("matchingFailures").GetInt32()).SingleOrDefault();
        Fixture New() => new(prerequisiteProgress: true);
        void NoRestore(Fixture fixture, string configuration = "Release", int timeout = 30) =>
            fixture.Process(["build", "Hello.csproj", "--no-restore", "-c", configuration], timeout);
        void Twice(Fixture fixture)
        {
            NoRestore(fixture); fixture.PrerequisiteFailure();
            fixture.Process(["build", "--verbosity", "quiet", "--configuration", "Release",
                "--disable-build-servers", "--no-restore", "--nologo"], 90);
            fixture.PrerequisiteFailure("pao");
        }

        using var plugin = new ProgrammingServicePlugin();
        var editions = ((IAgentHarnessModule)plugin).Harnesses.Where(h => h.Descriptor.Id == ProgrammingAgentHarness.Id).ToArray();
        check(editions.Count(h => h.Descriptor.Version == ProgrammingPrerequisiteProgressHarness.Version) == 1 &&
            editions.Count(h => h.Descriptor.Version == ProgrammingInterfacePresentationHarness.Version) == 1,
            "new 1.3.27 and frozen 1.3.26 are independently registered");

        var legacy = new Fixture(guardInterfacePresentation: true);
        NoRestore(legacy); legacy.PrerequisiteFailure();
        NoRestore(legacy); legacy.PrerequisiteFailure();
        NoRestore(legacy);
        check(legacy.IsTool && Count(legacy) == 0,
            "1.3.26 retains its existing advisory behavior for NETSDK1004");

        var repeated = New(); Twice(repeated);
        check(Count(repeated) == 2 && repeated.CheckCount == 0,
            "complete native and PAO child streams group the same prerequisite despite reporting, timeout and server switches");
        NoRestore(repeated);
        check(repeated.IsModel && repeated.Messages.Contains("normal build without --no-restore", StringComparison.Ordinal) &&
            repeated.Messages.Contains("Another --no-restore build was not dispatched", StringComparison.Ordinal),
            "third unchanged no-restore build is refused before dispatch with the actionable correction");
        repeated.Process(["build", "Hello.csproj", "-c", "Release"], 10);
        check(repeated.IsTool && !repeated.Argv.Contains("--no-restore", StringComparer.Ordinal),
            "normal restoring build is admitted without an unrelated source edit");
        repeated.Tool(true, JsonSerializer.Serialize(new { exitCode = 0, stdout = "Build succeeded", stderr = "" }), 0);
        check(Count(repeated) == 0 && repeated.CheckCount == 1,
            "confirmed successful normal build clears prerequisite lane and earns actual build credit");
        NoRestore(repeated); check(repeated.IsTool, "successful restore allows a later explicit no-restore build");

        var mutation = New(); Twice(mutation); mutation.InputWitness(); mutation.GuardWriteResult("changed");
        check(mutation.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 1 && Count(mutation) == 0,
            "proved source mutation clears the old generation");
        NoRestore(mutation); check(mutation.IsTool, "new source generation may be checked");
        var unchanged = New(); Twice(unchanged); unchanged.GuardWriteResult("identical");
        NoRestore(unchanged); check(unchanged.IsModel && Count(unchanged) == 2,
            "identical write cannot reset the prerequisite lane");

        var configurations = New();
        NoRestore(configurations); configurations.PrerequisiteFailure();
        NoRestore(configurations, "Debug"); configurations.PrerequisiteFailure();
        NoRestore(configurations); configurations.PrerequisiteFailure();
        check(Count(configurations) == 2 && Count(configurations, "Debug") == 1,
            "accepted configurations retain separate failure counts");
        NoRestore(configurations, "Debug"); check(configurations.IsTool, "Release limit does not block Debug");
        configurations.PrerequisiteFailure();
        NoRestore(configurations, "Debug"); check(configurations.IsModel, "Debug reaches its own limit");
        var foreign = New(); Twice(foreign);
        foreign.Process(["build", "Other.csproj", "--no-restore", "-c", "Release"]);
        check(foreign.IsModel && foreign.Messages.Contains("exact accepted project", StringComparison.Ordinal) && Count(foreign) == 2,
            "foreign project is rejected before prerequisite grouping");
        var compiler = New();
        NoRestore(compiler); compiler.CompilerFailure("CSC : error CS5001: No entry point", "native");
        NoRestore(compiler); compiler.CompilerFailure("CSC : error CS5001: No entry point", "native");
        compiler.Process(["build", "Hello.csproj", "-c", "Release"]);
        check(compiler.IsModel && compiler.Messages.Contains("complete compiler diagnostic set", StringComparison.Ordinal),
            "the existing complete C# compiler guard also blocks an equivalent restoring build");
        foreach (var failedCorrection in new[] { "wrapper-only", "wrong-provider", "mixed-errors", "timeout" })
        {
            var retained = New(); Twice(retained);
            retained.Process(["build", "Hello.csproj", "-c", "Release"]);
            check(retained.IsTool, "restoring correction remains admitted after prerequisite limit");
            retained.PrerequisiteFailure(failedCorrection);
            check(Count(retained) == 2, "unclassified failed correction does not erase confirmed missing-assets history: " + failedCorrection);
            NoRestore(retained); check(retained.IsModel, "failed correction cannot reopen no-restore loop: " + failedCorrection);
            retained.Process(["build", "Hello.csproj", "-c", "Release"]);
            retained.Tool(true, JsonSerializer.Serialize(new { exitCode = 0, stdout = "Build succeeded", stderr = "" }), 0);
            check(Count(retained) == 0 && retained.CheckCount == 1,
                "actual successful restoring build clears retained prerequisite history: " + failedCorrection);
        }

        foreach (var shape in new[] { "truncated", "partial", "timeout", "missing-stderr", "mismatched-exit",
            "wrapper-only", "plain", "fake-mention", "foreign-project", "foreign-assets", "mixed-errors",
            "wrong-provider", "wrong-request", "unfinished-provider" })
        {
            var uncredited = New();
            NoRestore(uncredited); uncredited.PrerequisiteFailure(shape);
            NoRestore(uncredited); uncredited.PrerequisiteFailure(shape);
            check(Count(uncredited) == 0 && uncredited.CheckCount == 0,
                "incomplete, unsupported or foreign diagnostic receives no grouping credit: " + shape);
            NoRestore(uncredited); check(uncredited.IsTool, "uncredited failure is not an arbitrary build ban: " + shape);
        }
        var uncertain = New(); NoRestore(uncertain); uncertain.PrerequisiteFailure();
        NoRestore(uncertain); uncertain.Uncertain();
        check(uncertain.Step.Next is CompleteWorkflow { Success: false } && Count(uncertain) == 1,
            "unknown outcome is terminal and never counted or replayed");
    }

    private sealed partial class Fixture
    {
        public void PrerequisiteFailure(string shape = "native")
        {
            var project = Path.Combine(Root, "Hello.csproj");
            var assets = Path.Combine(Root, "obj", "project.assets.json");
            var printedProject = shape == "foreign-project" ? Path.Combine(Root, "Other.csproj") : project;
            var printedAssets = shape == "foreign-assets" ? Path.Combine(Root, "other", "project.assets.json") : assets;
            var diagnostic = $"C:\\dotnet\\sdk\\targets\\Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Assets file '{printedAssets}' not found. Run a NuGet package restore to generate this file. [{printedProject}]";
            var output = diagnostic + "\nBuild FAILED.\n" + diagnostic + "\n 1 Error(s)\nTime Elapsed 00:00:00.31";
            if (shape == "mixed-errors") output += "\nCSC : error CS1002: ; expected";
            if (shape == "fake-mention") output = "The model says NETSDK1004: assets file missing";
            var content = shape switch
            {
                "plain" => output,
                "wrapper-only" => JsonSerializer.Serialize(new { exitCode = 1, stdout = "", stderr = "", error = output }),
                "missing-stderr" => JsonSerializer.Serialize(new { exitCode = 1, stdout = output }),
                "mismatched-exit" => JsonSerializer.Serialize(new { exitCode = 2, stdout = output, stderr = "" }),
                "pao" or "timeout" => JsonSerializer.Serialize(new { data = new { exit = 1, @out = output,
                    err_tail = "", out_truncated = false, err_truncated = false, timed_out = shape == "timeout" } }),
                _ => JsonSerializer.Serialize(new { exitCode = 1, stdout = output, stderr = "" })
            };
            Result(WorkflowEffectState.Failed, CerebralJson.Element(new
            {
                name = "process.run", content, truncated = shape == "truncated",
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Failed,
                    shape == "wrong-provider" ? "deterministic-fixture" : "cerebral.process",
                    RequestId: shape == "wrong-request" ? "foreign-effect" : null,
                    State: shape == "unfinished-provider" ? "running" : "completed",
                    ExitCode: 1, Partial: shape == "partial"), followUpRequired = false
            }), "Process failed; inspect retained result");
        }
    }
}
