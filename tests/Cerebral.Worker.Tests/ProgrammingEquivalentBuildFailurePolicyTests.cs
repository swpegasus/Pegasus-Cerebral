using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    static partial void EquivalentBuildFailurePolicy(Action<bool, string> check)
    {
        const string missingMain = "CSC : error CS5001: Program does not contain a static 'Main' method suitable for an entry point";
        int Count(Fixture f, string configuration) => f.Step.State.GetProperty("repeatedBuildFailures").EnumerateArray()
            .Where(item => item.GetProperty("configuration").GetString() == configuration)
            .Select(item => item.GetProperty("matchingFailures").GetInt32()).SingleOrDefault();
        void Failed(Fixture f, string output = missingMain, string shape = "native")
        {
            check(f.IsTool && f.PendingTool == "process.run", "fixture build really dispatched before recording failure");
            f.CompilerFailure(output, shape);
        }
        Fixture Twice()
        {
            var f = new Fixture();
            f.Dotnet("build"); Failed(f);
            f.Process(["build", "--no-restore", "--verbosity", "quiet", "--configuration", "Release", "--disable-build-servers", "--nologo"], 90);
            Failed(f, "Build FAILED.\n" + missingMain + "\n" + missingMain + "\n 1 Error(s)\nTime Elapsed 00:00:01.11", "pao");
            return f;
        }

        var variants = Twice();
        check(Count(variants, "Release") == 2 && variants.CheckCount == 0,
            "reporting/restore/server/project spelling and timeout variants share two identical compiler failures");
        variants.Process(["build", "Hello.csproj", "-v", "minimal", "-c", "Release"], 10);
        check(variants.IsModel && variants.Messages.Contains("Another equivalent build was not dispatched", StringComparison.Ordinal) &&
            variants.Step.State.GetProperty("repairs").GetInt32() == 1 && Count(variants, "Release") == 2,
            "third equivalent build is rejected before dispatch through the existing bounded correction path");
        check(variants.Progress().GetProperty("failedBuildAdvice").GetProperty("equivalentBuildLimit").GetInt32() == 2,
            "progress explains the enforced two-failure limit");

        var lanes = new Fixture();
        foreach (var configuration in new[] { "Release", "Debug", "Release", "Debug" })
        { lanes.Dotnet("build", configuration); Failed(lanes); }
        check(Count(lanes, "Release") == 2 && Count(lanes, "Debug") == 2,
            "alternating configurations preserves separate bounded failure lanes");
        lanes.Dotnet("build"); check(lanes.IsModel, "Release lane rejects its own third repeat");
        lanes.Dotnet("build", "Debug"); check(lanes.IsModel, "Debug lane independently rejects its own third repeat");
        lanes.Dotnet("build", "Debug");
        check(lanes.Step.Next is CompleteWorkflow { Success: false } &&
            lanes.Step.State.GetProperty("repairs").GetInt32() == 2,
            "repeated rejected actions cannot reset or exceed the existing two-correction cap");

        var defaults = new Fixture(); defaults.Process(["build"]); Failed(defaults);
        defaults.Process(["build", "--project", Path.Combine(defaults.Root, "Hello.csproj"), "-c", "Debug"]); Failed(defaults);
        check(Count(defaults, "Debug") == 2 && Count(defaults, "Release") == 0,
            "implicit Debug equals explicit Debug while accepted Release remains distinct");
        defaults.Dotnet("build"); check(defaults.IsTool, "a different effective configuration remains executable");
        Failed(defaults);
        check(Count(defaults, "Release") == 1, "different configuration starts its own observation");

        var current = Twice(); current.ReadRange(1, 1); current.ReadRange(2, 1);
        current.ContextRejected(Cerebral.Contracts.Context.ContextProjectionStatus.NeedsCompaction); current.CompactSuccess();
        current.QueueOwner("Inspect the compiler failure and fix the cause");
        current.Model(new { action = "done", summary = "stale" });
        check(Count(current, "Release") == 2 && current.Step.State.GetProperty("ownerSteeringCount").GetInt32() == 1,
            "reads, compaction and incorporated steering preserve failure observations");
        current.Dotnet("build"); check(current.IsModel, "context changes alone do not unlock another failed build");
        current.InputWitness(); current.GuardWriteResult("changed");
        check(!current.Step.State.GetProperty("repeatedBuildFailures").EnumerateArray().Any() &&
            current.Step.State.GetProperty("editCount").GetInt32() == 1,
            "a confirmed source mutation clears previous-generation failure lanes");
        current.Dotnet("build"); Failed(current);
        check(Count(current, "Release") == 1, "post-edit build failure begins a new generation");

        var rejectedWrite = Twice();
        rejectedWrite.Model(new { action = "tool", name = "file.write", arguments = new
            { path = Path.Combine(rejectedWrite.Root, "Program.cs"), content = "attempted change" } });
        check(rejectedWrite.IsTool, "fixture mutation dispatched before its rejected result");
        rejectedWrite.Tool(false, "Write rejected", error: "No source change confirmed");
        check(Count(rejectedWrite, "Release") == 2 && rejectedWrite.Step.State.GetProperty("editCount").GetInt32() == 0,
            "a failed source mutation cannot unlock or reset matching compiler failures");
        rejectedWrite.Dotnet("build"); check(rejectedWrite.IsModel, "failed mutation does not permit a third equivalent build");

        foreach (var shape in new[] { "skip", "identical", "missing", "truncated", "partial", "wrong-path", "wrong-request" })
        {
            var noChange = Twice(); noChange.GuardWriteResult(shape);
            check(noChange.Step.State.GetProperty("editCount").GetInt32() == 1 &&
                noChange.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 0 && Count(noChange, "Release") == 2,
                "successful operation without valid changed-byte proof preserves the guard independently of legacy EditCount: " + shape);
            noChange.Dotnet("build"); check(noChange.IsModel, "unproved/no-op write cannot unlock third build: " + shape);
        }
        var created = Twice(); created.InputWitness(); created.GuardWriteResult("create");
        check(created.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 1 && Count(created, "Release") == 0,
            "complete matching creation receipt advances the proved generation and clears lanes");
        created.Dotnet("build"); check(created.IsTool, "genuine creation permits a new-generation build");
        var uncertainWrite = Twice(); uncertainWrite.QueueGuardWrite(Path.Combine(uncertainWrite.Root, "Program.cs"), "new", "replace", new string('a', 64));
        uncertainWrite.Uncertain();
        check(uncertainWrite.Step.Next is CompleteWorkflow { Success: false } && Count(uncertainWrite, "Release") == 2 &&
            uncertainWrite.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 0,
            "unknown write remains terminal and cannot establish mutation proof or unlock a build");

        var diagnosis = new Fixture();
        diagnosis.Dotnet("build"); Failed(diagnosis);
        diagnosis.Dotnet("build"); Failed(diagnosis, missingMain + " (different diagnostic detail)");
        check(Count(diagnosis, "Release") == 1, "same compiler code with different message is not the same diagnostic");
        diagnosis.Dotnet("build"); Failed(diagnosis, Path.Combine(diagnosis.Root, "Program.cs") + "(1,1): error CS1002: ; expected");
        check(Count(diagnosis, "Release") == 1, "different compiler diagnostic does not acquire repeat credit");

        var locations = new Fixture();
        locations.Dotnet("build"); Failed(locations, "Program.cs(1,1): error CS1002: ; expected");
        locations.Dotnet("build"); Failed(locations, "Program.cs(2,1): error CS1002: ; expected");
        check(Count(locations, "Release") == 1, "compiler location remains part of exact diagnostic identity");
        var order = new Fixture(); const string other = "Program.cs(2,1): error CS1002: ; expected";
        order.Dotnet("build"); Failed(order, missingMain + "\n" + other);
        order.Dotnet("build"); Failed(order, other + "\n" + missingMain + "\n" + other);
        check(Count(order, "Release") == 2, "duplicate summary/order noise does not distinguish an identical complete diagnostic set");

        foreach (var shape in new[] { "truncated", "partial", "timeout", "bad-exit", "mismatched-exit", "array", "generic", "non-compiler", "huge", "long-line", "many-errors" })
        {
            var incomplete = new Fixture();
            incomplete.Dotnet("build"); Failed(incomplete, missingMain, shape);
            incomplete.Dotnet("build"); Failed(incomplete, missingMain, shape);
            check(Count(incomplete, "Release") == 0 && incomplete.CheckCount == 0,
                "unsupported/incomplete result acquires no compiler grouping or check credit: " + shape);
            incomplete.Dotnet("build");
            check(incomplete.IsTool, "unrelated/unconfirmed failures are not arbitrarily banned: " + shape);
        }
        var different = Twice();
        var generic = new Fixture(); generic.Dotnet("build"); generic.Tool(false, "unclassified failure", 1);
        generic.Process(["build", "Hello.csproj", "-c", "Release", "--no-restore"]); generic.Tool(false, "unclassified failure", 1);
        check(generic.Step.State.GetProperty("lastFailedBuild").GetProperty("matchingFailures").GetInt32() == 1 && Count(generic, "Release") == 0,
            "unrecognized failures preserve exact-argv advisory semantics instead of broadening command grouping");
        different.Process(["build", "Other.csproj", "-c", "Release"]);
        check(different.IsModel && different.Messages.Contains("exact accepted project", StringComparison.Ordinal),
            "foreign-project admission remains prior to equivalent-build grouping");
        different.Process(["build", "Hello.csproj", "-c", "Release", "--invented-switch"]);
        check(different.IsModel && different.Messages.Contains("Unsupported verification argument", StringComparison.Ordinal),
            "unsupported command switches cannot enter semantic normalization");

        var checks = Twice(); checks.Dotnet("test");
        check(checks.IsTool, "failed-build grouping does not ban a different check kind");
        checks.Tool(false, "test failure", 1);
        checks.Dotnet("run"); check(checks.IsTool, "run admission and host run limits remain unchanged");
        var passed = new Fixture(); passed.Dotnet("build"); Failed(passed);
        passed.Dotnet("build"); passed.Tool(true, "Build succeeded", 0);
        check(Count(passed, "Release") == 0 && passed.CheckCount == 1,
            "confirmed passing build clears its failed lane without weakening check evidence");
        var otherPass = new Fixture(); otherPass.Dotnet("build"); Failed(otherPass);
        otherPass.Dotnet("build", "Debug"); otherPass.Tool(true, "Build succeeded", 0);
        check(Count(otherPass, "Release") == 1, "passing another configuration preserves this configuration's failed lane");
        otherPass.Dotnet("build"); Failed(otherPass);
        otherPass.Dotnet("build"); check(otherPass.IsModel, "another configuration's pass cannot unlock the third equivalent failure");

        var automatic = Twice(); automatic.ReadRange(1, 1);
        automatic.Model(new { action = "done", summary = "try again automatically" });
        check(automatic.Step.Next is CompleteWorkflow { Success: false } && automatic.CheckCount == 0,
            "automatic completion verification cannot bypass the guard or claim a passing check");
        var cancelled = Twice(); cancelled.Cancel();
        check(cancelled.Step.Next is CompleteWorkflow { Success: false } && Count(cancelled, "Release") == 2,
            "cancellation remains terminal without dispatch or loss of confirmed history");
        var unknown = new Fixture(); unknown.Dotnet("build"); Failed(unknown);
        unknown.Dotnet("build"); unknown.Uncertain();
        check(unknown.Step.Next is CompleteWorkflow { Success: false } && Count(unknown, "Release") == 1,
            "unknown build is never replayed or credited as a matching confirmed failure");
    }

    /// <summary>Real local native writes; model/build-failure effects are deterministic fixtures.</summary>
    public static async Task<int> RunEquivalentBuildMutationChecksAsync(string current)
    {
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Equivalent-build native mutation: " + message); checks++; }
        var parent = Path.GetFullPath(Path.Combine(current, "artifacts")) + Path.DirectorySeparatorChar;
        var scratch = Path.GetFullPath(Path.Combine(parent, "EquivalentBuildMutationEvidenceFixture"));
        if (!scratch.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Directory.Exists(scratch))
            throw new IOException("The exact native mutation fixture directory must be absent.");
        Directory.CreateDirectory(scratch);
        try
        {
            var settings = new HostSettings { McpServers = [] };
            var profile = new AgentProfile { AllowedRoots = [scratch], AllowedTools = ["file.read", "file.write", "process.run"],
                Capabilities = ["filesystem.read", "filesystem.write", "process.execute"] };
            await using var dispatcher = new ToolDispatcher(() => settings, stateDirectory: Path.Combine(scratch, "dispatcher-state"));
            foreach (var mode in new[] { "skip", "identical", "changed", "create" })
            {
                const string original = "class Original { }\n";
                var path = Path.Combine(scratch, mode == "create" ? "Created.cs" : "Program.cs");
                if (mode != "create") await File.WriteAllTextAsync(path, original, new UTF8Encoding(false, true));
                var before = mode == "create" ? null : Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                var f = new Fixture(rootPath: scratch);
                for (var attempt = 0; attempt < 2; attempt++)
                { f.Dotnet("build"); f.CompilerFailure("CSC : error CS5001: No entry point", "native"); }
                Check(f.Step.State.GetProperty("repeatedBuildFailures")[0].GetProperty("matchingFailures").GetInt32() == 2,
                    "guard is saturated before real native " + mode);
                var content = mode == "identical" ? original : "class Changed { }\n";
                // Membership is a deterministic SDK-evaluation receipt here; only the native
                // byte mutation below is real filesystem evidence in this isolated fixture.
                f.InputWitness(sourceFile: mode == "create" ? "Created.cs" : "Program.cs");
                f.QueueGuardWrite(path, content, mode == "skip" ? "skip" : mode == "create" ? "fail" : "replace", before);
                Check(f.IsTool, "write admitted before real native " + mode);
                var result = await dispatcher.ExecuteAsync(new("file.write", f.Command.Arguments.GetProperty("arguments")),
                    new(profile, "equivalent-build-mutation-fixture", "equivalent-build-mutation-fixture", scratch)
                    { EnforceProfileScope = true }, CancellationToken.None);
                Check(result.Success && !result.Truncated && result.EffectiveOutcome == ToolExecutionOutcome.Succeeded,
                    "real native write returned complete success: " + mode);
                f.HostEquivalentTool(result);
                Check(await File.ReadAllTextAsync(path) == (mode == "skip" ? original : content), "actual file bytes reflect native " + mode);
                var changed = mode is "changed" or "create";
                Check(f.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == (changed ? 1 : 0) &&
                    f.Step.State.GetProperty("repeatedBuildFailures").GetArrayLength() == (changed ? 0 : 1),
                    "only proven real mutation clears the joined saturated guard: " + mode);
                f.Dotnet("build");
                Check(changed ? f.IsTool : f.IsModel, "third build dispatch agrees with actual native mutation: " + mode);
            }
            Console.WriteLine("PASS nativeMutation=" + checks + " realWrites=4 buildFailures=deterministic-fixtures modelCalls=0 activeRuntimeCalls=0");
            return checks;
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private sealed partial class Fixture
    {
        public void QueueGuardWrite(string path, string content, string collision, string? before)
        {
            var arguments = new Dictionary<string, object?> { ["path"] = path, ["content"] = content, ["collision"] = collision };
            if (before is not null) arguments["expectedSha256"] = before;
            Model(new { action = "tool", name = "file.write", arguments });
        }
        public void GuardWriteResult(string shape)
        {
            const string content = "class Changed { }\n";
            var path = Path.Combine(Root, "Program.cs");
            var before = new string('a', 64);
            var after = shape == "identical" ? before : shape == "create"
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))) : new string('b', 64);
            QueueGuardWrite(path, content, shape == "create" ? "fail" : shape == "skip" ? "skip" : "replace", shape == "create" ? null : before);
            var result = shape == "missing" ? "success without mutation proof" : shape == "skip"
                ? JsonSerializer.Serialize(new { path, skipped = true })
                : JsonSerializer.Serialize(new { path = shape == "wrong-path" ? Path.Combine(Root, "foreign.cs") : path,
                    bytes = Encoding.UTF8.GetByteCount(content), sha256 = after });
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
            {
                name = "file.write", content = result, truncated = shape == "truncated",
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "deterministic-fixture",
                    RequestId: shape == "wrong-request" ? "different-effect" : null, Partial: shape == "partial"),
                followUpRequired = false
            }));
        }
        public void CompilerFailure(string diagnostic, string shape)
        {
            var content = shape switch
            {
                "pao" => JsonSerializer.Serialize(new { data = new { exit = 1, @out = diagnostic, err = "", out_truncated = false, err_truncated = false, timed_out = false } }),
                "timeout" => JsonSerializer.Serialize(new { data = new { exit = 1, @out = diagnostic, err = "", out_truncated = false, err_truncated = false, timed_out = true } }),
                "bad-exit" => JsonSerializer.Serialize(new { exitCode = "1", stdout = diagnostic, stderr = "" }),
                "mismatched-exit" => JsonSerializer.Serialize(new { exitCode = 2, stdout = diagnostic, stderr = "" }),
                "array" => "[\"unsupported process envelope\"]",
                "generic" => "Process exited with code 1",
                "non-compiler" => "project.csproj : error MSB1001: Unknown switch.",
                "huge" => new string('x', 65537) + "\n" + diagnostic,
                "long-line" => diagnostic + new string('x', 2048),
                "many-errors" => string.Join('\n', Enumerable.Range(1, 17).Select(line => $"Program.cs({line},1): error CS1002: ; expected")),
                _ => JsonSerializer.Serialize(new { exitCode = 1, stdout = diagnostic, stderr = "" })
            };
            Result(WorkflowEffectState.Failed, CerebralJson.Element(new
            {
                name = "process.run", content, truncated = shape == "truncated",
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Failed, "deterministic-fixture", ExitCode: 1, Partial: shape == "partial"),
                followUpRequired = false
            }), "Process failed; inspect retained result");
        }
    }
}

