using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Pure deterministic receipts. No SDK, child process, model or installed API is executed.</summary>
public static partial class ProgrammingHarnessTests
{
    public static int RunSourceFixChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Programming source fix: " + message); checks++; }
        InterfacePresentationPolicy(Check);
        SourceInputMembershipPolicy(Check);
        return checks;
    }

    private static void SourceInputMembershipPolicy(Action<bool, string> check)
    {
        int BuildFailures(Fixture f, string configuration = "Release") => f.Step.State.GetProperty("repeatedBuildFailures")
            .EnumerateArray().Where(item => item.GetProperty("configuration").GetString() == configuration)
            .Select(item => item.GetProperty("matchingFailures").GetInt32()).SingleOrDefault();
        int VerificationFailures(Fixture f) => f.Step.State.TryGetProperty("failedVerificationChecks", out var failed) ? failed.GetArrayLength() : 0;
        Fixture Failed(string lane)
        {
            var f = new Fixture(guardFailedChecks: true);
            if (lane == "build")
                for (var i = 0; i < 2; i++) { f.Dotnet("build"); f.CompilerFailure("CSC : error CS5001: Missing Main", "native"); }
            else { f.Dotnet(lane); f.VerificationFailure(); }
            return f;
        }
        bool Guarded(Fixture f, string lane) => lane == "build" ? BuildFailures(f) == 2 : VerificationFailures(f) == 1;

        foreach (var lane in new[] { "build", "run", "test" })
        {
            foreach (var file in new[] { "notes.txt", "Sibling/Unrelated.cs", "Program.cs" })
            {
                var unrelated = Failed(lane); unrelated.VerificationSourceWrite(file);
                check(Guarded(unrelated, lane) && unrelated.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 0,
                    lane + ": a proved mutation without accepted-project membership cannot unlock retries: " + file);
                unrelated.Dotnet(lane); check(unrelated.IsModel, lane + ": unchanged failure remains enforced before dispatch");
            }
            var project = Failed(lane); project.VerificationSourceWrite("Hello.csproj");
            check(!Guarded(project, lane) && project.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 1,
                lane + ": a proved exact accepted-project edit earns input progress");
            project.Dotnet(lane); check(project.IsTool, lane + ": exact project repair permits a new check");

            var evaluated = Failed(lane); evaluated.InputWitness();
            check(Guarded(evaluated, lane) && evaluated.CheckCount == 0 &&
                evaluated.Step.State.TryGetProperty("evaluatedInputs", out _),
                lane + ": metadata evaluation alone earns neither retry nor passing-check credit");
            evaluated.GuardWriteResult("changed");
            check(!Guarded(evaluated, lane) && !evaluated.Step.State.TryGetProperty("evaluatedInputs", out _) &&
                evaluated.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 1,
                lane + ": the subsequent complete matching mutation of an evaluated SDK input earns progress and consumes the witness");
            evaluated.Dotnet(lane); check(evaluated.IsTool, lane + ": ordinary SDK input repair can be reverified");

            var excluded = Failed(lane); excluded.InputWitness(); excluded.VerificationSourceWrite("Sibling/Unrelated.cs");
            check(Guarded(excluded, lane), lane + ": evaluation does not grant a broader root or suffix-based membership");
            var notes = Failed(lane); notes.InputWitness(); notes.VerificationSourceWrite("notes.txt");
            check(Guarded(notes, lane), lane + ": default None items without copy-to-output metadata do not credit notes");

            foreach (var file in new[] { "Resources.resx", "AnalyzerInput.json", "test-data.txt" })
            {
                var supported = Failed(lane); supported.InputWitness(); supported.VerificationSourceWrite(file);
                check(!Guarded(supported, lane), lane + ": evaluated build/verification input mutation is supported: " + file);
            }
        }

        foreach (var shape in new[] { "skip", "identical", "missing", "truncated", "partial", "wrong-path", "wrong-request" })
        {
            var noProof = Failed("run"); noProof.InputWitness(); noProof.GuardWriteResult(shape);
            check(VerificationFailures(noProof) == 1 && !noProof.Step.State.TryGetProperty("evaluatedInputs", out _),
                "membership never substitutes for a complete matching mutation proof, and the attempted mutation retires the witness: " + shape);
        }
        var created = Failed("run"); created.InputWitness(); created.GuardWriteResult("create");
        check(VerificationFailures(created) == 0, "a declared/evaluated input can be created with complete matching content/byte/hash proof");
        var range = new Fixture(selectedTools: ["file.read", "file.edit-range", "process.run"], guardFailedChecks: true);
        range.Dotnet("run"); range.VerificationFailure(); range.InputWitness(); range.VerificationRangeEdit();
        check(VerificationFailures(range) == 0, "matching complete range edit proof uses the same input policy");
        var edited = Failed("test"); edited.InputWitness(); edited.VerificationSourceEdit();
        check(VerificationFailures(edited) == 0, "matching complete file.edit proof uses the same input policy");

        foreach (var stale in new[] { "notes", "project", "import", "mkdir", "child" })
        {
            var f = Failed("run"); f.InputWitness();
            if (stale == "child") { f.Dotnet("build"); f.Tool(true, "Built", 0); }
            else if (stale == "mkdir") { f.Model(new { action = "tool", name = "file.mkdir", arguments = new { path = Path.Combine(f.Root, "new-dir") } }); f.Tool(true, "Created"); }
            else f.VerificationSourceWrite(stale == "project" ? "Hello.csproj" : stale == "import" ? "Directory.Build.props" : "notes.txt");
            check(!f.Step.State.TryGetProperty("evaluatedInputs", out _), "intervening file topology/mutation or child work retires evaluation: " + stale);
            if (stale == "project") { f.Dotnet("run"); f.VerificationFailure(); }
            f.GuardWriteResult("changed");
            check(VerificationFailures(f) == 1, "stale evaluation cannot credit a later Program.cs mutation: " + stale);
        }

        var lanes = new Fixture(guardFailedChecks: true);
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            for (var i = 0; i < 2; i++) { lanes.Dotnet("build", configuration); lanes.CompilerFailure("CSC : error CS5001: Missing Main", "native"); }
            lanes.Dotnet("run", configuration); lanes.VerificationFailure();
        }
        lanes.InputWitness(); lanes.GuardWriteResult("changed");
        check(BuildFailures(lanes) == 0 && BuildFailures(lanes, "Debug") == 2 && VerificationFailures(lanes) == 1 &&
            lanes.Step.State.GetProperty("failedVerificationChecks")[0].GetProperty("configuration").GetString() == "Debug",
            "Release-only evaluation retires only Release build/run failures, preserving rebased Debug guards");
        lanes.Dotnet("build", "Debug"); check(lanes.IsModel, "global mutation generation cannot bypass the surviving Debug build lane");
        lanes.Dotnet("run", "Debug"); check(lanes.IsModel, "global mutation generation cannot bypass the surviving Debug run lane");

        foreach (var invalid in new[] { "provider", "request", "state", "partial", "truncated", "followup", "exit", "stderr", "project", "configuration", "multitarget", "identity", "duplicate", "extra", "missing-items", "pao" })
        {
            var f = Failed("run"); f.InputWitness(shape: invalid);
            check(!f.Step.State.TryGetProperty("evaluatedInputs", out _), "unsupported metadata receipt earns no input witness: " + invalid);
            if (f.IsModel) { f.GuardWriteResult("changed"); check(VerificationFailures(f) == 1, "invalid evaluation cannot unlock source retries: " + invalid); }
        }
        foreach (var extra in new[] { "-target:Build", "-getTargetResult:Build", "-restore", "@override.rsp", "-property:Custom=true", "-getItem:ProjectReference" })
        {
            var f = Failed("run"); f.InputQuery(extra: extra);
            check(f.IsModel && !f.Step.State.TryGetProperty("evaluatedInputs", out _), "metadata query rejects non-closed argv before dispatch: " + extra);
        }
        var wrongProject = Failed("run"); wrongProject.InputQuery(project: "Sibling/Other.csproj");
        check(wrongProject.IsModel, "metadata evaluation cannot retarget a sibling project");
        var autoResponse = Failed("run"); autoResponse.Process(["msbuild", Path.Combine(autoResponse.Root, "Hello.csproj"), "-nologo", "-verbosity:quiet",
            "-property:Configuration=Release", "-getProperty:MSBuildProjectFullPath,Configuration,TargetFramework,TargetFrameworks,MSBuildAllProjects",
            "-getItem:Compile,EmbeddedResource,AdditionalFiles,Content,None"]);
        check(autoResponse.IsModel, "metadata query requires -noAutoResponse and cannot inherit automatic response-file build switches");

        foreach (var reportedProject in new[] { "Hello.csproj", "Sibling/Other.csproj" })
        {
            var f = Failed("run"); f.Dotnet("build");
            f.CompilerFailure(Path.Combine(f.Root, "Program.cs") + "(1,1): error CS1002: ; expected [" + Path.Combine(f.Root, reportedProject) + "]", "native");
            f.GuardWriteResult("changed");
            check(VerificationFailures(f) == (reportedProject == "Hello.csproj" ? 0 : 1),
                "complete compiler receipt must explicitly name the accepted project to bind an ordinary SDK source: " + reportedProject);
        }
        foreach (var relative in new[] { "source", "project" })
        {
            var f = Failed("run"); f.Dotnet("build");
            var source = relative == "source" ? "Program.cs" : Path.Combine(f.Root, "Program.cs");
            var project = relative == "project" ? "Hello.csproj" : Path.Combine(f.Root, "Hello.csproj");
            f.CompilerFailure(source + "(1,1): error CS1002: ; expected [" + project + "]", "native");
            f.GuardWriteResult("changed");
            check(VerificationFailures(f) == 1, "ambiguous relative compiler " + relative + " coordinates do not prove accepted-project input membership");
        }

        var literal = Failed("run"); literal.LiteralInputRead(); literal.GuardWriteResult("changed");
        check(VerificationFailures(literal) == 1, "explicit literal project text and in-body import switches cannot prove evaluated membership or exclude prior SDK/ancestor imports");
        var evaluatedLiteral = Failed("run"); evaluatedLiteral.LiteralInputRead(); evaluatedLiteral.InputWitness(); evaluatedLiteral.GuardWriteResult("changed");
        check(VerificationFailures(evaluatedLiteral) == 0, "an explicit-source project uses the same evaluated-input witness as ordinary default SDK items");
        var plain = Failed("run"); plain.LiteralInputRead(closed: false); plain.GuardWriteResult("changed");
        check(VerificationFailures(plain) == 1, "a plain SDK project read cannot establish default-glob membership or unknown ancestor imports");

        foreach (var witness in new[] { "project-read", "compiler" })
        {
            var f = Failed("run");
            if (witness == "project-read") f.LiteralInputRead();
            else { f.Dotnet("build"); f.CompilerFailure(Path.Combine(f.Root, "Program.cs") + "(1,1): error CS1002: ; expected [" + Path.Combine(f.Root, "Hello.csproj") + "]", "native"); }
            f.VerificationSourceWrite("notes.txt"); f.GuardWriteResult("changed");
            check(VerificationFailures(f) == 1, "an intervening mutation retires earlier membership evidence, including " + witness);
        }
        var replaced = Failed("run"); replaced.InputWitness(); replaced.InputWitness(shape: "provider"); replaced.GuardWriteResult("changed");
        check(VerificationFailures(replaced) == 1, "a failed replacement query cannot leave the former evaluation witness live");
    }

    private sealed partial class Fixture
    {
        public void InputQuery(string configuration = "Release", string project = "Hello.csproj", string? extra = null)
        {
            var arguments = new List<string> { "msbuild", Path.Combine(Root, project), "-nologo", "-noAutoResponse", "-verbosity:quiet",
                "-property:Configuration=" + configuration,
                "-getProperty:MSBuildProjectFullPath,Configuration,TargetFramework,TargetFrameworks,MSBuildAllProjects",
                "-getItem:Compile,EmbeddedResource,AdditionalFiles,Content,None" };
            if (extra is not null) arguments.Add(extra);
            Process(arguments.ToArray());
        }

        public void InputWitness(string configuration = "Release", string shape = "complete", string sourceFile = "Program.cs")
        {
            InputQuery(configuration);
            if (!IsTool) throw new InvalidOperationException("Closed metadata query did not dispatch");
            object Item(string file, string? copy = null) => new { Identity = file,
                FullPath = Path.Combine(Root, shape == "identity" ? "wrong.cs" : file),
                DefiningProjectFullPath = Path.Combine(Root, "Hello.csproj"), CopyToOutputDirectory = copy };
            var output = JsonSerializer.Serialize(new
            {
                Properties = new { MSBuildProjectFullPath = Path.Combine(Root, shape == "project" ? "Other.csproj" : "Hello.csproj"),
                    Configuration = shape == "configuration" ? "Other" : configuration, TargetFramework = "net10.0",
                    TargetFrameworks = shape == "multitarget" ? "net9.0;net10.0" : "", MSBuildAllProjects = Path.Combine(Root, "Hello.csproj") },
                Items = new Dictionary<string, object> { ["Compile"] = new[] { Item(sourceFile) },
                    ["EmbeddedResource"] = new[] { Item("Resources.resx") }, ["AdditionalFiles"] = new[] { Item("AnalyzerInput.json") },
                    ["Content"] = new[] { Item("test-data.txt", "PreserveNewest") }, ["None"] = new[] { Item("notes.txt") } }
            });
            if (shape == "duplicate") output = output.Insert(output.LastIndexOf('}'), ",\"Properties\":{}");
            if (shape == "extra") output = output.Insert(output.LastIndexOf('}'), ",\"TargetResults\":{}");
            if (shape == "missing-items") output = "{\"Properties\":{},\"Items\":{}}";
            var content = shape == "pao" ? JsonSerializer.Serialize(new { data = new { exit = 0, @out = output, err = "" } }) :
                JsonSerializer.Serialize(new { exitCode = 0, stdout = output, stderr = shape == "stderr" ? "warning" : "" });
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new { name = "process.run", content,
                truncated = shape == "truncated", followUpRequired = shape == "followup",
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, shape == "provider" ? "fixture" : "cerebral.process",
                    RequestId: shape == "request" ? "foreign" : null, State: shape == "state" ? "running" : "completed",
                    ExitCode: shape == "exit" ? 1 : 0, Partial: shape == "partial") }));
        }

        public void LiteralInputRead(bool closed = true)
        {
            var path = Path.Combine(Root, "Hello.csproj");
            var content = closed ? "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /></ItemGroup></Project>" :
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
            Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = 1 } });
            Tool(true, JsonSerializer.Serialize(new { path, sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                startLine = 1, totalLines = 1, content }));
        }
    }
}
