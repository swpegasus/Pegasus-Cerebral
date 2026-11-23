using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

/// <summary>Pure harness policy tests; effect results below are deterministic fixtures, not live tools.</summary>
public static partial class ProgrammingHarnessTests
{
    // Historical linked fixture projects may omit the new feature partial; the current lane explicitly includes it.
    static partial void StructuralRepairPolicy(Action<bool, string> check);
    static partial void PendingRepairContextPolicy(Action<bool, string> check);
    static partial void OwnerSteeringPolicy(Action<bool, string> check);
    static partial void EquivalentBuildFailurePolicy(Action<bool, string> check);
    static partial void PrerequisiteProgressPolicy(Action<bool, string> check);
    static partial void LegacyCompletionPolicy(Action<bool, string> check);
    static partial void CurrentBuildPolicy(Action<bool, string> check);
    static partial void FailedVerificationRetryPolicy(Action<bool, string> check);
    static partial void RunArgumentsPolicy(Action<bool, string> check);
    static partial void FinalizationProgressPolicy(Action<bool, string> check);

    public static int RunPolicyChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Programming harness: " + message);
            checks++;
        }

        var initial = new Fixture();
        Check(initial.Command.CommandId == "cerebral.host:generate", "first effect requests the host model bridge");
        Check(initial.Command.Arguments.GetProperty("toolNames").EnumerateArray().Any(x => x.GetString() == "process.run"), "generation carries the bounded semantic catalog");
        Check(initial.Command.Arguments.TryGetProperty("responseSchema", out _), "generation requests a structured action");
        var policyText = initial.Command.Arguments.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Check(policyText.Contains("successful build proves compilation", StringComparison.Ordinal) &&
            policyText.Contains("complete handler, including later assignments", StringComparison.Ordinal),
            "assembled system guidance distinguishes compilation from behavior and traces later overwrites");
        Check(policyText.Contains("canonical original source", StringComparison.Ordinal) &&
            policyText.Contains("current built artifacts", StringComparison.Ordinal) &&
            policyText.Contains("user's purpose", StringComparison.Ordinal),
            "assembled diagnosis guidance retains source/receipt navigation and current purpose-correlated evidence");
        Check(policyText.Contains("host plugin or", StringComparison.Ordinal) &&
            policyText.Contains("admitted host checker", StringComparison.Ordinal) &&
            policyText.Contains("Changing run flags", StringComparison.Ordinal) &&
            policyText.Contains("Do not add Main or force OutputType=Exe", StringComparison.Ordinal) &&
            policyText.Contains("report the mismatch for owner correction", StringComparison.Ordinal),
            "assembled artifact-aware guidance routes library validation to admitted host checks without changing its output type or accepted checks");
        using var plugin = new ProgrammingServicePlugin();
        Check(((IAgentHarnessModule)plugin).Harnesses.Single(item => item.Descriptor.Id == ProgrammingAgentHarness.Id && item.Descriptor.Version == ProgrammingAgentHarness.Version).Descriptor.Id == ProgrammingAgentHarness.Id, "plugin exports the harness through the optional SDK interface");

        var claims = new Fixture();
        claims.Model(new { action = "done", summary = "Everything works." });
        Check(claims.IsTool && claims.PendingTool == "file.read" && claims.CheckCount == 0,
            "a completion claim obtains real bounded project-reading evidence before verification or success");
        claims.Tool(false, "The project was not readable.");
        Check(claims.IsModel && claims.CheckCount == 0 && claims.Step.State.GetProperty("repairs").GetInt32() == 0,
            "failed automatic source reading returns real failure evidence without source credit or invalid-action corrections");

        var partial = new Fixture();
        partial.Model(new { action = "tool", name = "file.mkdir", arguments = new { path = partial.Root } }, ModelGenerationFinish.Length);
        Check(partial.IsModel, "length finish never dispatches even syntactically complete JSON");
        Check(partial.Messages.Contains("No action was dispatched", StringComparison.Ordinal), "length correction explicitly records no execution");
        var omitted = new Fixture();
        omitted.Model(new { action = "done", summary = "Done" }, outputOmitted: true);
        Check(omitted.Step.Next is CompleteWorkflow { Success: false }, "omitted model output cannot become an action");
        var missingGeneration = new Fixture();
        missingGeneration.MissingGeneration();
        Check(missingGeneration.Step.Next is CompleteWorkflow { Success: false }, "malformed host generation metadata fails without a null dereference");

        var repair = new Fixture();
        repair.Read();
        repair.Dotnet("build");
        repair.Tool(false, "Program.cs(1,1): error CS1002: ; expected", 1);
        Check(repair.IsModel && repair.LastContributionText.Contains("\"status\":\"failed\"", StringComparison.Ordinal) &&
            !repair.Messages.Contains("CS1002", StringComparison.Ordinal),
            "failed build first returns a compact status receipt without duplicating its diagnostic body");
        using (var status = JsonDocument.Parse(repair.LastContributionText.Split('\n')[0]))
        {
            var receipt = status.RootElement.GetProperty("receipt").GetString();
            repair.Model(new { action = "receipt", effectId = receipt, offset = 0 });
        }
        Check(repair.IsReceipt, "the compact failure exposes an exact receipt for diagnostics on demand");
        repair.ReceiptPage(WorkflowEffectState.Failed, "Program.cs(1,1): error CS1002: ; expected");
        Check(repair.IsModel && repair.Messages.Contains("CS1002", StringComparison.Ordinal),
            "the requested receipt page returns the exact failed build diagnostic before repair");
        Check(repair.CheckCount == 0, "failed build yields no passing check");
        repair.Write();
        repair.Dotnet("build"); repair.Tool(true, "Build succeeded", 0);
        repair.Dotnet("run"); repair.Tool(true, "Hello from the repaired project", 0);
        repair.Model(new { action = "done", summary = "Fixed the compiler error, built and ran the project." });
        Check(repair.Step.Next is CompleteWorkflow { Success: true }, "read/edit/build/run with real-shaped receipts satisfies completion");
        var completed = (CompleteWorkflow)repair.Step.Next;
        Check(completed.Result.GetProperty("checks").GetArrayLength() == 2, "completion includes both check receipt IDs");

        var compactCommand = new Fixture();
        compactCommand.Read(); compactCommand.Dotnet("build");
        compactCommand.Tool(false, new string('x', 12000) + "\nBuild FAILED.\n    1 Warning(s)\n    2 Error(s)", 1);
        Check(compactCommand.IsModel && compactCommand.LastContributionText.Contains("\"status\":\"failed\"", StringComparison.Ordinal) &&
            compactCommand.LastContributionText.Contains("\"commandsCompleted\":0", StringComparison.Ordinal) &&
            compactCommand.LastContributionText.Contains("\"commandsFailed\":1", StringComparison.Ordinal) &&
            compactCommand.LastContributionText.Contains("\"exitCode\":1", StringComparison.Ordinal) &&
            compactCommand.LastContributionText.Contains("\"errors\":2", StringComparison.Ordinal) &&
            compactCommand.LastContributionText.Contains("\"warnings\":1", StringComparison.Ordinal) &&
            !compactCommand.LastContributionText.Contains(new string('x', 256), StringComparison.Ordinal) &&
            compactCommand.LastContributionText.Length < 512,
            "failed commands return compact proven status and issue counts without reinserting verbose output");
        Check(compactCommand.Context.Contributions.Last() is { Complete: false, ExactReadHandle: not null },
            "compact failed command status preserves its exact diagnostic receipt");
        Check(completed.Result.GetProperty("editCount").GetInt32() == 1, "completion records the edited generation");
        Check(completed.Result.GetProperty("readReceipts").GetArrayLength() == 1, "source read is retained as host evidence");

        var edit = new Fixture();
        edit.Read(); edit.Dotnet("build"); edit.Tool(true, "Built", 0); edit.Dotnet("run"); edit.Tool(true, "Ran", 0);
        Check(edit.CheckCount == 2, "successful checks accumulate before edits");
        edit.Write();
        Check(edit.CheckCount == 0, "a later edit invalidates all earlier checks");
        edit.Model(new { action = "done", summary = "Done after another edit" });
        Check(edit.IsTool && edit.Argv[0] == "build", "stale checks cause the plugin to rerun build rather than accepting completion");
        edit.Tool(true, "Rebuilt after edit", 0);
        Check(edit.IsTool && edit.Argv[0] == "run", "run is repeated only after the replacement build passes");
        edit.Tool(true, "Actual rerun output", 0);
        Check(edit.Step.Next is CompleteWorkflow { Success: true }, "automatic checks complete without another model decision");

        var unchangedEdit = new Fixture();
        unchangedEdit.Model(new
        {
            action = "tool", name = "file.edit", arguments = new
            {
                path = Path.Combine(unchangedEdit.Root, "Program.cs"), oldText = "broken", newText = "broken", expectedSha256 = new string('a', 64)
            }
        });
        Check(unchangedEdit.IsModel && unchangedEdit.Messages.Contains("unchanged action was rejected", StringComparison.Ordinal) &&
              unchangedEdit.Step.State.GetProperty("editCount").GetInt32() == 0,
            "the harness rejects an unchanged source replacement before dispatch or edit credit");
        Check(!unchangedEdit.Context.Contributions.Any(source => source.Category == "model-output"),
            "rejected actions stay in the durable generation receipt and are not replayed as model context");

        var repeatedRead = new Fixture();
        repeatedRead.Read();
        repeatedRead.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(repeatedRead.Root, "Program.cs") } });
        Check(repeatedRead.IsModel && repeatedRead.Messages.Contains("exact file range was already read", StringComparison.Ordinal),
            "the harness corrects an identical source read before it can consume another tool transition");
        repeatedRead.Write();
        repeatedRead.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(repeatedRead.Root, "Program.cs") } });
        Check(repeatedRead.IsTool, "a confirmed source edit permits the same range to be read again");

        var diagnosticBuild = new Fixture();
        diagnosticBuild.Read(); diagnosticBuild.Write(); diagnosticBuild.Read();
        diagnosticBuild.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(diagnosticBuild.Root, "Program.cs") } });
        Check(diagnosticBuild.IsTool && diagnosticBuild.Argv[0] == "build" && diagnosticBuild.Argv.Contains("Release"),
            "a repeated read after an unchecked edit runs the already accepted build for compiler feedback");
        diagnosticBuild.Tool(false, "Program.cs(1,1): error CS1002: ; expected", 1,
            error: "The tool returned failed. Inspect its retained result and evidence.");
        Check(diagnosticBuild.CheckCount == 0 && diagnosticBuild.IsModel && diagnosticBuild.LastContributionText.Contains("CS1002", StringComparison.Ordinal),
            "diagnostic builds return actual compiler failure without credit or automatic finalization");
        diagnosticBuild.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(diagnosticBuild.Root, "Program.cs") } });
        Check(diagnosticBuild.IsModel && diagnosticBuild.Messages.Contains("exact file range was already read", StringComparison.Ordinal),
            "an unchanged failed generation cannot trigger another automatic diagnostic build");
        var pluginBaseline = new Fixture(requiredChecks: ["build"], presentManifest: "bin/Release/net10.0-windows/desktop-plugin.json");
        pluginBaseline.Read();
        pluginBaseline.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(pluginBaseline.Root, "Program.cs") } });
        Check(pluginBaseline.IsTool && pluginBaseline.Argv[0] == "build" && pluginBaseline.Step.State.GetProperty("editCount").GetInt32() == 0,
            "an existing plugin read loop may diagnose its accepted baseline before any new edit");
        var noOpFeedback = new Fixture(requiredChecks: ["build"], presentManifest: "bin/Release/net10.0-windows/desktop-plugin.json");
        noOpFeedback.Read();
        noOpFeedback.Model(new { action = "tool", name = "file.edit", arguments = new
        {
            path = Path.Combine(noOpFeedback.Root, "Program.cs"), oldText = "broken", newText = "broken", expectedSha256 = new string('a', 64)
        } });
        Check(noOpFeedback.IsTool && noOpFeedback.Argv[0] == "build" && noOpFeedback.Step.State.GetProperty("editCount").GetInt32() == 0,
            "an unchanged plugin edit obtains compiler evidence without dispatching or crediting the rejected mutation");

        var readableRead = new Fixture();
        readableRead.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(readableRead.Root, "Program.cs") } });
        readableRead.Tool(true, JsonSerializer.Serialize(new
        {
            path = Path.Combine(readableRead.Root, "Program.cs"), sha256 = new string('a', 64), startLine = 1, totalLines = 1,
            content = "Console.WriteLine(\"Readable\");"
        }));
        Check(readableRead.LastContributionText.Contains("Source:", StringComparison.Ordinal) &&
              readableRead.LastContributionText.Contains("Console.WriteLine(\"Readable\");", StringComparison.Ordinal) &&
              readableRead.LastContributionText.Contains("Path: " + Path.Combine(readableRead.Root, "Program.cs"), StringComparison.Ordinal) &&
              !readableRead.LastContributionText.Contains("\\u0022", StringComparison.Ordinal) &&
              !readableRead.LastContributionText.Contains("\\u0022path\\u0022", StringComparison.Ordinal),
            "file reads project source as readable payload rather than nested escaped tool JSON");

        var boundedRead = new Fixture();
        boundedRead.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(boundedRead.Root, "Program.cs") } });
        boundedRead.Tool(true, JsonSerializer.Serialize(new
        {
            path = Path.Combine(boundedRead.Root, "Program.cs"), sha256 = new string('b', 64), startLine = 1, totalLines = 200,
            content = string.Join('\n', Enumerable.Range(1, 200).Select(line => $"// bounded source line {line}: {new string('x', 40)}"))
        }));
        Check(boundedRead.LastContributionText.Contains("Displayed source lines: 1-", StringComparison.Ordinal) &&
              boundedRead.LastContributionText.Contains("Continuation: call file.read with startLine=", StringComparison.Ordinal) &&
              boundedRead.LastContributionText.Contains("lineCount=60", StringComparison.Ordinal) &&
              !boundedRead.LastContributionText.Contains("bounded source line 200", StringComparison.Ordinal),
            "large file reads expose a complete-line excerpt and an exact bounded continuation instead of clipping mid-source");

        ReadProjectionCompleteness(Check);
        RangeEditPolicy(Check);
        StructurePolicy(Check);
        WorkingSourcePolicy(Check);
        CoverageProgressPolicy(Check);
        StructuralRepairPolicy(Check);
        PendingRepairContextPolicy(Check);
        OwnerSteeringPolicy(Check);
        EquivalentBuildFailurePolicy(Check);
        PrerequisiteProgressPolicy(Check);
        LegacyCompletionPolicy(Check);
        CurrentBuildPolicy(Check);
        FailedVerificationRetryPolicy(Check);
        RunArgumentsPolicy(Check);
        FinalizationProgressPolicy(Check);
        InterfacePresentationPolicy(Check);

        var missingExit = new Fixture();
        missingExit.Read(); missingExit.Dotnet("build"); missingExit.Tool(true, "Build succeeded", null);
        Check(missingExit.CheckCount == 0, "success prose without host exit code does not count");
        var contradictory = new Fixture();
        contradictory.Read(); contradictory.Dotnet("build"); contradictory.Tool(false, "Exit zero but operation failed", 0);
        Check(contradictory.CheckCount == 0, "failed effect cannot count despite exit zero");
        var configuration = new Fixture();
        configuration.Read(); configuration.Dotnet("build", "Debug"); configuration.Tool(true, "Built", 0);
        configuration.Dotnet("run", "Release"); configuration.Tool(true, "Ran unrelated old release", 0);
        Check(configuration.CheckCount == 1, "different build configuration does not validate the executable");
        var noTests = new Fixture();
        noTests.Read(); noTests.Dotnet("build"); noTests.Tool(true, "Built", 0);
        noTests.Dotnet("test"); noTests.Tool(true, "Determining projects to restore...\nAll projects are up-to-date for restore.", 0);
        Check(noTests.CheckCount == 1 && noTests.Messages.Contains("test requirement remains unmet", StringComparison.Ordinal), "exit zero with no executed tests is not a test pass");
        var tests = new Fixture();
        tests.Read(); tests.Dotnet("build"); tests.Tool(true, "Built", 0);
        tests.Dotnet("test"); tests.Tool(true, JsonSerializer.Serialize(new { exitCode = 0, stdout = "Passed!  - Failed: 0, Passed: 3, Skipped: 1, Total: 4, Duration: 5 ms - Hello.Tests.dll (net10.0)", stderr = "" }), 0);
        Check(tests.CheckCount == 2 && tests.Step.State.GetProperty("checks")[1].GetProperty("testsPassed").GetInt32() == 3, "supported positive VSTest summary records real test counts");
        var truncatedTests = new Fixture();
        truncatedTests.Read(); truncatedTests.Dotnet("build"); truncatedTests.Tool(true, "Built", 0);
        truncatedTests.Dotnet("test"); truncatedTests.Tool(true, "Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 5 ms", 0, truncated: true);
        Check(truncatedTests.CheckCount == 1, "truncated test summary does not prove the full test run");

        var autoRun = new Fixture();
        autoRun.Read(); autoRun.Write(); autoRun.Dotnet("build"); autoRun.Tool(true, "Build succeeded", 0);
        autoRun.Model(new { action = "done", summary = "The application printed imaginary unicorns." });
        Check(autoRun.IsTool && autoRun.Command.CommandId == "cerebral.host:tool" && autoRun.Argv.SequenceEqual(new[] { "run", "--project", "Hello.csproj", "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal", "--no-build" }), "premature done dispatches the missing run using the registered host tool path");
        Check(autoRun.Step.State.GetProperty("completionDraft").GetString()!.Contains("imaginary unicorns", StringComparison.Ordinal) && autoRun.Step.State.GetProperty("completionDraftReceipt").GetString()!.StartsWith("fixture-effect-", StringComparison.Ordinal), "model completion draft and its generation receipt remain retained");
        autoRun.Tool(true, "Real program output", 0);
        Check(autoRun.Step.Next is CompleteWorkflow { Success: true } && !((CompleteWorkflow)autoRun.Step.Next).Result.GetProperty("summary").GetString()!.Contains("unicorns", StringComparison.Ordinal), "final host summary uses verified receipts rather than fabricated model output");

        var pluginLifecycle = new Fixture(requiredChecks: ["build"], presentManifest: Path.Combine("Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json"));
        pluginLifecycle.Read();
        pluginLifecycle.Model(new { action = "done", summary = "Ready for the required build and plugin review." });
        Check(pluginLifecycle.IsTool && pluginLifecycle.Argv[0] == "build", "plugin finalization still requires the accepted build first");
        pluginLifecycle.Tool(true, "Build succeeded", 0);
        Check(pluginLifecycle.IsTool && pluginLifecycle.PendingTool == "plugin.verify" &&
            pluginLifecycle.Command.Arguments.GetProperty("arguments").GetProperty("root").GetString() == pluginLifecycle.Root,
            "a passing build automatically verifies the exact accepted plugin root");
        pluginLifecycle.Tool(true, "Plugin build verified");
        Check(pluginLifecycle.IsTool && pluginLifecycle.PendingTool == "plugin.present" &&
            pluginLifecycle.Command.Arguments.GetProperty("arguments").GetProperty("path").GetString()!.EndsWith("desktop-plugin.json", StringComparison.Ordinal),
            "successful verification queues the exact built manifest once");
        pluginLifecycle.Tool(true, "Queued for owner review");
        Check(pluginLifecycle.Step.Next is CompleteWorkflow { Success: true } &&
            pluginLifecycle.Step.State.GetProperty("pluginVerified").GetBoolean() && pluginLifecycle.Step.State.GetProperty("pluginPresented").GetBoolean(),
            "programming completion records verified and queued plugin lifecycle receipts without claiming installation");

        var earlyPresent = new Fixture(requiredChecks: ["build"], presentManifest: Path.Combine("Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json"));
        earlyPresent.Model(new { action = "tool", name = "plugin.present", arguments = new { path = Path.Combine(earlyPresent.Root, "Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json") } });
        Check(earlyPresent.IsModel && earlyPresent.Step.State.GetProperty("repairs").GetInt32() == 1,
            "the model cannot present a plugin before source evidence, required checks, and verification");

        var contractAware = new Fixture(requiredChecks: ["build"],
            selectedTools: ["file.read", "file.list", "file.search", "file.write", "file.edit", "file.mkdir", "process.run", "plugin.contract"],
            presentManifest: Path.Combine("Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json"));
        Check(contractAware.Command.Arguments.GetProperty("toolNames").EnumerateArray().Any(item => item.GetString() == "plugin.contract"),
            "desktop plugin assignments expose the exact SDK contract tool to the model");
        contractAware.Model(new { action = "tool", name = "plugin.contract", arguments = new { topic = "new-conversation" } });
        Check(contractAware.IsTool && contractAware.PendingTool == "plugin.contract",
            "plugin contract retrieval dispatches without inventing a filesystem path");
        contractAware.Tool(true, "Exact new-conversation contract and compile-ready example");
        Check(contractAware.IsModel && contractAware.LastContributionText.Contains("new-conversation contract", StringComparison.Ordinal),
            "the exact desktop contract result returns to the programming model context");
        var preloadedContract = new Fixture(requiredChecks: ["build"],
            selectedTools: ["file.read", "file.list", "file.search", "file.write", "file.edit", "file.mkdir", "process.run", "plugin.contract"],
            presentManifest: Path.Combine("Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json"),
            contractTopic: "new-conversation");
        Check(preloadedContract.IsTool && preloadedContract.PendingTool == "plugin.contract" &&
            preloadedContract.Command.Arguments.GetProperty("arguments").GetProperty("topic").GetString() == "new-conversation",
            "an accepted desktop contract topic is retrieved before the first model turn");
        preloadedContract.Tool(true, "Exact preloaded new-conversation contract");
        Check(preloadedContract.IsModel && preloadedContract.LastContributionText.Contains("preloaded new-conversation", StringComparison.Ordinal),
            "the deterministic contract preflight becomes required model evidence");
        var storagePreflight = new Fixture(requiredChecks: ["build"],
            selectedTools: ["file.read", "file.list", "file.search", "file.write", "file.edit", "file.mkdir", "process.run", "plugin.contract"],
            contractTopic: "storage");
        Check(storagePreflight.IsTool && storagePreflight.PendingTool == "plugin.contract" &&
              storagePreflight.Command.Arguments.GetProperty("arguments").GetProperty("topic").GetString() == "storage",
            "private storage contract preflight reaches the existing SDK tool without model inference");
        var readableContract = new Fixture(requiredChecks: ["build"],
            selectedTools: ["file.read", "file.list", "file.search", "file.write", "file.edit", "file.mkdir", "process.run", "plugin.contract"],
            contractTopic: "new-conversation");
        readableContract.Tool(true, JsonSerializer.Serialize(new
        {
            topic = "new-conversation", contract = "pegasus.cerebral.desktop", apiVersion = 1,
            reference = new { summary = "Implement the landing view.", signatures = "public interface IDesktopMountPlugin {}", example = "public sealed class Plugin : IDesktopPlugin, IDesktopMountPlugin {}" },
            editing = "Edit once, then build."
        }));
        Check(readableContract.LastContributionText.Contains("Public signatures:", StringComparison.Ordinal) &&
              readableContract.LastContributionText.Contains("Complete example:", StringComparison.Ordinal) &&
              !readableContract.LastContributionText.Contains("\\u0022reference\\u0022", StringComparison.Ordinal),
            "plugin contracts enter model context as readable sections instead of nested escaped tool JSON");

        var autoFailure = new Fixture(); autoFailure.Read();
        autoFailure.Model(new { action = "done", summary = "Ready for checks" });
        Check(autoFailure.IsTool && autoFailure.Argv[0] == "build", "automatic verification starts with the missing build");
        autoFailure.Tool(false, "Program.cs: error CS0103: WritLine does not exist", 1);
        Check(autoFailure.IsModel && autoFailure.LastContributionText.Contains("\"status\":\"failed\"", StringComparison.Ordinal),
            "failed automatic build returns its compact status to the model");
        using (var status = JsonDocument.Parse(autoFailure.LastContributionText.Split('\n')[0]))
        {
            var receipt = status.RootElement.GetProperty("receipt").GetString();
            autoFailure.Model(new { action = "receipt", effectId = receipt, offset = 0 });
        }
        autoFailure.ReceiptPage(WorkflowEffectState.Failed, "Program.cs: error CS0103: WritLine does not exist");
        Check(autoFailure.IsModel && autoFailure.Messages.Contains("CS0103", StringComparison.Ordinal),
            "failed automatic build exposes exact compiler evidence through its requested receipt");
        autoFailure.Write(); autoFailure.Model(new { action = "done", summary = "Corrected the compiler error" });
        autoFailure.Tool(true, "Build succeeded", 0); autoFailure.Tool(true, "Real output", 0);
        Check(autoFailure.Step.Next is CompleteWorkflow { Success: true } && autoFailure.Step.State.GetProperty("verificationCycles").GetInt32() == 2, "model repair can be followed by a second bounded verification cycle");

        var runOnly = new Fixture(requiredChecks: ["run"]); runOnly.Read(); runOnly.Model(new { action = "done", summary = "Ready" });
        Check(runOnly.IsTool && !runOnly.Argv.Contains("--no-build"), "run without a matching build receipt does not skip compilation");
        var noTestPass = new Fixture(requiredChecks: ["test"]); noTestPass.Read(); noTestPass.Model(new { action = "done", summary = "Ready" });
        noTestPass.Tool(true, "No tests were discovered", 0);
        Check(noTestPass.IsModel && noTestPass.CheckCount == 0, "automatic test cannot pass from exit zero without executed tests");
        var paoTests = new Fixture(requiredChecks: ["test"]); paoTests.Read(); paoTests.Model(new { action = "done", summary = "Ready" });
        paoTests.Tool(true, JsonSerializer.Serialize(new { ok = true, data = new { exit = 0, @out = "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 ms", out_truncated = false, err_truncated = false, timed_out = false } }), 0);
        Check(paoTests.Step.Next is CompleteWorkflow { Success: true } && ((CompleteWorkflow)paoTests.Step.Next).Result.GetProperty("checks")[0].GetProperty("testsTotal").GetInt32() == 2, "actual PAO data.out receipt shape supplies supported test counts");
        var paoTruncated = new Fixture(requiredChecks: ["test"]); paoTruncated.Read(); paoTruncated.Model(new { action = "done", summary = "Ready" });
        paoTruncated.Tool(true, JsonSerializer.Serialize(new { ok = true, data = new { @out = "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 ms", out_truncated = true, err_truncated = false, timed_out = false } }), 0);
        Check(paoTruncated.IsModel && paoTruncated.CheckCount == 0, "PAO truncation flags prevent false complete test proof");

        var boundedCycles = new Fixture(requiredChecks: ["build"]); boundedCycles.Read();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            boundedCycles.Model(new { action = "done", summary = "Try required checks" });
            Check(boundedCycles.IsTool && boundedCycles.Argv[0] == "build", "bounded verification attempt dispatches through the host");
            boundedCycles.Tool(false, "Compiler failure remains", 1);
        }
        boundedCycles.Model(new { action = "done", summary = "Try forever" });
        Check(boundedCycles.Step.Next is CompleteWorkflow { Success: false }, "verification stops after three unsuccessful cycles");
        var blocked = new Fixture(); blocked.Read(); blocked.Model(new { action = "blocked", reason = "Required dependency is unavailable" });
        Check(blocked.Step.Next is CompleteWorkflow { Success: false }, "blocked model outcome does not force verification execution");
        var deniedSelection = false;
        try { _ = new Fixture(selectedTools: ["file.read"]); } catch (ArgumentException) { deniedSelection = true; }
        Check(deniedSelection, "automatic checks cannot bypass the selected process.run tool requirement");
        var unknown = new Fixture(); unknown.Read(); unknown.Model(new { action = "done", summary = "Ready" }); unknown.Uncertain();
        Check(unknown.Step.Next is CompleteWorkflow { Success: false }, "an unknown check never causes another automatic tool request");
        var cancelled = new Fixture(); cancelled.Read(); cancelled.Model(new { action = "done", summary = "Ready" }); cancelled.Cancel();
        Check(cancelled.Step.Next is CompleteWorkflow { Success: false }, "cancellation never dispatches the next automatic check");

        var optionsFirst = new Fixture();
        optionsFirst.Process(["build", "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal", "Hello.csproj"], 123);
        Check(optionsFirst.Command.CommandId == "cerebral.host:tool" && optionsFirst.Argv.SequenceEqual(new[] { "build", "Hello.csproj", "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" }), "valid options before the project are canonicalized for PAO");
        Check(optionsFirst.Command.Arguments.GetProperty("arguments").GetProperty("timeoutSeconds").GetInt32() == 123, "argv canonicalization preserves timeout and other tool fields");
        optionsFirst.Tool(true, "Built Release", 0);
        Check(optionsFirst.IsModel && optionsFirst.CheckCount == 1, "canonical pending arguments match the host receipt");
        var optionsBothSides = new Fixture();
        optionsBothSides.Process(["test", "--no-restore", "Hello.csproj", "--configuration", "Release"]);
        Check(optionsBothSides.Command.CommandId == "cerebral.host:tool" && optionsBothSides.Argv.SequenceEqual(new[] { "test", "Hello.csproj", "--no-restore", "--configuration", "Release" }), "test options may surround the single project path");
        var runOptions = new Fixture(); runOptions.Dotnet("build"); runOptions.Tool(true, "Built Release", 0);
        runOptions.Process(["run", "--configuration", "Release", "--project", "Hello.csproj", "--no-build"]);
        Check(runOptions.Command.CommandId == "cerebral.host:tool" && runOptions.Argv.SequenceEqual(new[] { "run", "--project", "Hello.csproj", "--configuration", "Release", "--no-build" }), "run project option may follow supported named options");
        foreach (var kind in new[] { "build", "test" })
        {
            var projectOption = new Fixture();
            projectOption.Process([kind, "--project", "Hello.csproj", "--configuration", "Release", "--no-restore"]);
            Check(projectOption.Command.CommandId == "cerebral.host:tool" &&
                projectOption.Argv.SequenceEqual(new[] { kind, "Hello.csproj", "--configuration", "Release", "--no-restore" }),
                kind + " accepts the common --project spelling only for the exact accepted project and canonicalizes it for dotnet");
        }
        foreach (var kind in new[] { "build", "test", "run" })
        {
            var inferredProject = new Fixture();
            inferredProject.Process([kind, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal"], 87);
            string[] expected = kind == "run"
                ? [kind, "--project", "Hello.csproj", "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal"]
                : [kind, "Hello.csproj", "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal"];
            Check(inferredProject.IsTool && inferredProject.Argv.SequenceEqual(expected) && inferredProject.Step.State.GetProperty("repairs").GetInt32() == 0,
                "omitted " + kind + " project is supplied from accepted input without consuming a correction");
            Check(inferredProject.Command.Arguments.GetProperty("arguments").GetProperty("timeoutSeconds").GetInt32() == 87,
                "project inference preserves explicit timeout for " + kind);
            var bareCommand = new Fixture(); bareCommand.Process([kind]);
            Check(bareCommand.IsTool && bareCommand.Argv.Contains("Hello.csproj", StringComparer.Ordinal), "bare " + kind + " remains confined to the accepted project");
        }
        foreach (var argv in new string[][]
        {
            ["run", "--configuration", "Release", "--no-build", "Hello.csproj"],
            ["run", "Hello.csproj", "--configuration", "Release", "--no-build"],
            ["run", "--configuration", "Release", "--no-build"]
        })
        {
            var positionalRun = new Fixture(); positionalRun.Dotnet("build"); positionalRun.Tool(true, "Built Release", 0); positionalRun.Process(argv);
            Check(positionalRun.IsTool && positionalRun.Argv.SequenceEqual(new[] { "run", "--project", "Hello.csproj", "--configuration", "Release", "--no-build" }),
                "run normalizes accepted positional or omitted project: " + string.Join(' ', argv));
            positionalRun.Tool(true, "Actual run result", 0);
            Check(positionalRun.IsModel && positionalRun.Step.State.GetProperty("repairs").GetInt32() == 0, "canonical run receipt matches without a format repair");
        }
        foreach (var invalid in new string[][]
        {
            ["build", "Hello.csproj", "Hello.csproj"],
            ["build", "--project", "Hello.csproj", "--project", "Hello.csproj"],
            ["test", "--project", "Other.csproj"],
            ["run", "--project", "Hello.csproj", "--project", "Hello.csproj"],
            ["run", "Hello.csproj", "Hello.csproj"],
            ["run", "Hello.csproj", "--project", "Hello.csproj"],
            ["run", "--project", "Hello.csproj", "Hello.csproj"],
            ["run", "--project"],
            ["run", "Other.csproj"],
            ["run", "--project", "..\\Other.csproj"],
            ["run", "Hello.csproj", "--", "application-argument"],
            ["run", "Hello.csproj", "application-argument"],
            ["run", "--unexpected"],
            ["test", "--no-restore", "Other.csproj"],
            ["build", "--configuration", "Release", "-t:Help", "Hello.csproj"],
            ["build", "-t:Help"],
            ["test", "--list-tests"]
        })
        {
            var invalidCommand = new Fixture(); invalidCommand.Process(invalid);
            Check(invalidCommand.IsModel, "invalid reordered command is not dispatched: " + string.Join(' ', invalid));
        }
        var wrongRoot = new Fixture();
        wrongRoot.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet", workingDirectory = Path.GetDirectoryName(wrongRoot.Root), arguments = new[] { "build" } } });
        Check(wrongRoot.IsModel, "project inference cannot override a different working root");

        var projectDirectory = new Fixture(projectFile: Path.Combine("Desktop", "Hello.csproj"));
        projectDirectory.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet", workingDirectory = Path.Combine(projectDirectory.Root, "Desktop"), arguments = new[] { "build" } } });
        Check(projectDirectory.IsTool && string.Equals(Path.GetFullPath(projectDirectory.WorkingDirectory), projectDirectory.Root, StringComparison.OrdinalIgnoreCase) &&
            projectDirectory.Argv.SequenceEqual(new[] { "build", Path.Combine("Desktop", "Hello.csproj") }),
            "accepted project directory is normalized to the registered root and exact project file");

        var wrongProject = new Fixture();
        wrongProject.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet", workingDirectory = wrongProject.Root, arguments = new[] { "build", "Other.csproj" } } });
        Check(wrongProject.IsModel && wrongProject.Messages.Contains("exact accepted project", StringComparison.Ordinal), "wrong-project verification is rejected before dispatch");
        var listTests = new Fixture();
        listTests.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet", workingDirectory = listTests.Root, arguments = new[] { "test", "Hello.csproj", "--list-tests" } } });
        Check(listTests.IsModel, "listing tests cannot masquerade as executing tests");
        var fakeTarget = new Fixture();
        fakeTarget.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet", workingDirectory = fakeTarget.Root, arguments = new[] { "build", "Hello.csproj", "-t:Help" } } });
        Check(fakeTarget.IsModel, "non-build MSBuild targets are excluded from check evidence");
        var outside = new Fixture();
        outside.Model(new { action = "tool", name = "file.write", arguments = new { path = Path.Combine(Path.GetDirectoryName(outside.Root)!, "outside.cs"), content = "x" } });
        Check(outside.IsModel, "file tools remain inside the explicit project root");
        var duplicate = new Fixture();
        duplicate.ModelText("{\"action\":\"done\",\"action\":\"blocked\",\"reason\":\"duplicate\"}");
        Check(duplicate.IsModel, "duplicate JSON keys do not select ambiguous actions");

        var bounded = new Fixture();
        bounded.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(bounded.Root, "Program.cs"), lineCount = 10 } });
        bounded.Tool(true, "First ten lines of source", truncated: true);
        Check(bounded.IsModel && bounded.Messages.Contains("truncated", StringComparison.Ordinal), "bounded partial reads stay explicit in the model context");
        var unrelated = new Fixture();
        unrelated.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(unrelated.Root, "notes.txt") } });
        unrelated.Tool(true, "Unrelated prose");
        Check(unrelated.Step.State.GetProperty("readReceipts").GetArrayLength() == 0, "unrelated text is not source-read evidence");
        var large = new Fixture();
        large.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(large.Root, "Program.cs") } });
        large.Tool(true, new string('x', 70 * 1024));
        Check(large.IsModel && large.Context.Contributions.Last() is { Complete: false, ExactReadHandle: not null } &&
            large.LastContributionText.Contains("Remaining content is retained", StringComparison.Ordinal) &&
            large.LastContributionText.Contains("\"contentTruncated\":true", StringComparison.Ordinal) &&
            large.LastContributionText.Length < 10000,
            "oversized payload becomes an explicitly labeled 8 KB projection with an exact receipt handle");

        var prunedEvidence = new Fixture(); prunedEvidence.Read();
        Check(!prunedEvidence.Context.Contributions.Any(source => source.Category == "model-output"),
            "an executed model tool action is represented by its receipt instead of being replayed in later prompts");
        prunedEvidence.Write();
        Check(prunedEvidence.Context.Contributions.Count(source => source.Category == "tool-result") == 1 &&
            !prunedEvidence.Messages.Contains("Console.WriteLine(\\\"Hello\\\")", StringComparison.Ordinal),
            "a confirmed source mutation drops stale source projections while retaining the current mutation receipt");

        var continuation = new Fixture(); continuation.Read();
        continuation.Model(new { action = "done", summary = "Ready for verification" });
        continuation.Tool(false, "Compiler failure before a correction", 1);
        continuation.ModelText("{ malformed action");
        continuation.Write(); continuation.Dotnet("build"); continuation.Tool(true, "Build succeeded", 0);
        var savedChecks = continuation.Step.State.GetProperty("checks").Clone();
        var savedReads = continuation.Step.State.GetProperty("readReceipts").Clone();
        var savedEdits = continuation.Step.State.GetProperty("editCount").GetInt32();
        var savedRepairs = continuation.Step.State.GetProperty("repairs").GetInt32();
        var savedCycles = continuation.Step.State.GetProperty("verificationCycles").GetInt32();
        continuation.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        Check(continuation.IsCompact, "typed exact overflow requests host compaction before another generation");
        Check(continuation.Context.Contributions.Last().Requirement == ContextRequirement.Required && continuation.Context.Contributions.Any(source => source.Requirement == ContextRequirement.Reducible), "latest evidence is required while older projections are reducible");
        continuation.CompactSuccess();
        Check(continuation.IsModel && continuation.Context.Checkpoint is not null, "validated checkpoint returns directly to normal generation");
        Check(JsonElement.DeepEquals(savedChecks, continuation.Step.State.GetProperty("checks")) && JsonElement.DeepEquals(savedReads, continuation.Step.State.GetProperty("readReceipts")), "compaction preserves exact check and source-read receipts");
        Check(continuation.Step.State.GetProperty("editCount").GetInt32() == savedEdits && continuation.Step.State.GetProperty("repairs").GetInt32() == savedRepairs && continuation.Step.State.GetProperty("verificationCycles").GetInt32() == savedCycles, "compaction does not replenish edit/repair/verification counters");
        Check(!continuation.Context.Contributions.Any(source => continuation.Context.Checkpoint!.CoveredSources.Any(covered => covered.Id == source.Id)), "adoption removes only the covered live projections and prevents duplicate checkpoint sources");
        continuation.Model(new { action = "done", summary = "The summary claims all checks were already successful" });
        Check(continuation.IsTool && continuation.Argv[0] == "run", "compaction cannot invent a missing successful run receipt");
        continuation.Tool(true, "Actual run after continuation", 0);
        Check(continuation.Step.Next is CompleteWorkflow { Success: true }, "continued assignment completes using its actual remaining check");

        var repeated = new Fixture(); repeated.Read(); repeated.ReadRange(2, 1); repeated.ContextRejected(ContextProjectionStatus.NeedsCompaction); repeated.CompactSuccess();
        repeated.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        Check(repeated.Step.Next is CompleteWorkflow { Success: false } && ((CompleteWorkflow)repeated.Step.Next).Error!.Contains("unchanged", StringComparison.Ordinal), "unchanged context cannot compact repeatedly");
        var requiredFloor = new Fixture(); requiredFloor.Read(); requiredFloor.ContextRejected(ContextProjectionStatus.RequiredOverflow);
        Check(requiredFloor.Step.Next is CompleteWorkflow { Success: false }, "required-floor overflow does not compact immutable evidence");
        var mismatchedBudget = new Fixture(); mismatchedBudget.Read(); mismatchedBudget.ReadRange(2, 1); mismatchedBudget.ContextRejected(ContextProjectionStatus.NeedsCompaction, revisionDelta: 1);
        Check(mismatchedBudget.Step.Next is CompleteWorkflow { Success: false }, "budget diagnostics must match the current context revision");

        var rejectedCheckpoint = new Fixture(); rejectedCheckpoint.Read(); rejectedCheckpoint.ReadRange(2, 1); rejectedCheckpoint.ContextRejected(ContextProjectionStatus.NeedsCompaction); rejectedCheckpoint.CompactSuccess();
        var oldCheckpoint = rejectedCheckpoint.Step.State.GetProperty("checkpoint").Clone();
        rejectedCheckpoint.Read(); rejectedCheckpoint.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        rejectedCheckpoint.CompactSuccess(checkpoint => checkpoint with { ExclusionRevision = checkpoint.ExclusionRevision + 1 });
        Check(rejectedCheckpoint.Step.Next is CompleteWorkflow { Success: false } && JsonElement.DeepEquals(oldCheckpoint, rejectedCheckpoint.Step.State.GetProperty("checkpoint")), "stale exclusion revision cannot replace the prior checkpoint");
        var forgedCoverage = new Fixture(); forgedCoverage.Read(); forgedCoverage.ReadRange(2, 1); forgedCoverage.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        forgedCoverage.CompactSuccess(checkpoint => checkpoint with { CoveredSources = [new("unrelated-source", 1)] });
        Check(forgedCoverage.Step.Next is CompleteWorkflow { Success: false } && forgedCoverage.Step.State.GetProperty("checkpoint").ValueKind == JsonValueKind.Null, "invented checkpoint coverage is rejected");
        var forgedEffect = new Fixture(); forgedEffect.Read(); forgedEffect.ReadRange(2, 1); forgedEffect.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        forgedEffect.CompactSuccess(checkpoint => checkpoint with { CompactionEffectId = "another-effect" });
        Check(forgedEffect.Step.Next is CompleteWorkflow { Success: false }, "a checkpoint from a different compaction effect is rejected");
        var noReduction = new Fixture(); noReduction.Read(); noReduction.ReadRange(2, 1); noReduction.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        noReduction.CompactSuccess(afterInputTokens: 1800);
        Check(noReduction.Step.Next is CompleteWorkflow { Success: false }, "nonreducing or over-capacity checkpoint is not adopted");
        var partialCheckpoint = new Fixture(); partialCheckpoint.Read(); partialCheckpoint.ReadRange(2, 1); partialCheckpoint.ContextRejected(ContextProjectionStatus.NeedsCompaction); partialCheckpoint.IncompleteCheckpoint();
        Check(partialCheckpoint.Step.Next is CompleteWorkflow { Success: false } && partialCheckpoint.Step.State.GetProperty("checkpoint").ValueKind == JsonValueKind.Null, "partial compactor output without a complete candidate cannot be adopted");
        foreach (var compactionOutcome in new[] { WorkflowEffectState.Failed, WorkflowEffectState.Unknown, WorkflowEffectState.Cancelled })
        {
            var failedCheckpoint = new Fixture(); failedCheckpoint.Read(); failedCheckpoint.ReadRange(2, 1); failedCheckpoint.ContextRejected(ContextProjectionStatus.NeedsCompaction); failedCheckpoint.CompactSuccess();
            var retained = failedCheckpoint.Step.State.GetProperty("checkpoint").Clone();
            failedCheckpoint.Read(); failedCheckpoint.ContextRejected(ContextProjectionStatus.NeedsCompaction); failedCheckpoint.CompactionFailure(compactionOutcome);
            Check(failedCheckpoint.Step.Next is CompleteWorkflow { Success: false } && JsonElement.DeepEquals(retained, failedCheckpoint.Step.State.GetProperty("checkpoint")), "failed/uncertain/cancelled compaction retains prior evidence: " + compactionOutcome);
        }
        var projectionBound = new Fixture();
        for (var index = 0; index < 12 && projectionBound.IsModel; index++)
        {
            projectionBound.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(projectionBound.Root, "Program.cs"), startLine = index + 1, lineCount = 1 } });
            projectionBound.Tool(true, new string((char)('a' + index), 6000));
        }
        Check(projectionBound.IsModel && JsonSerializer.SerializeToUtf8Bytes(projectionBound.Context, CerebralJson.Options).Length > 24 * 1024,
            "serialized evidence bytes alone do not request optional compaction");
        projectionBound.ContextRejected(ContextProjectionStatus.NeedsCompaction);
        Check(projectionBound.IsCompact, "only measured host context pressure requests compaction for the large projection");
        projectionBound.CompactSuccess();
        Check(projectionBound.IsModel, "measured projection compaction returns to normal generation");

        var receipts = new Fixture();
        receipts.Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(receipts.Root, "Program.cs") } });
        receipts.Tool(false, "Source read failed with details retained", 1);
        var currentFailure = receipts.Context.Contributions.Last();
        receipts.Model(new { action = "receipt", effectId = "fixture-effect-2", offset = 0 });
        Check(receipts.IsReceipt && receipts.Command.Arguments.GetProperty("limit").GetInt32() == 4096 && !receipts.Command.Arguments.TryGetProperty("runId", out _), "receipt action uses a fixed bounded host-scoped read");
        receipts.ReceiptPage(WorkflowEffectState.Failed, "Stored diagnostic", nextOffset: 17, totalBytes: 32);
        Check(receipts.IsModel && receipts.Context.Contributions.Any(source => source.Id == currentFailure.Id && source.Requirement == ContextRequirement.Required), "historical receipt read preserves the latest required tool failure");
        Check(receipts.CheckCount == 0 && receipts.Step.State.GetProperty("readReceipts").GetArrayLength() == 0, "retrieving a historical receipt never adds source-read or verification credits");
        Check(receipts.Context.Contributions.Last() is { Requirement: ContextRequirement.Reducible, Complete: false }, "partial receipt pages are explicit reducible reference data");
        receipts.Model(new { action = "receipt", effectId = "fixture-effect-2", offset = 17, expectedRevision = 10 });
        Check(receipts.IsReceipt && receipts.Command.Arguments.GetProperty("expectedRevision").GetInt64() == 10, "continued receipt pages preserve their explicit revision");
        receipts.ReceiptPage(WorkflowEffectState.Failed, "Last diagnostic", totalBytes: 32);
        receipts.Model(new { action = "receipt", effectId = "fixture-effect-2", offset = 0 });
        Check(receipts.IsModel && receipts.Step.State.GetProperty("repairs").GetInt32() == 1, "repeated receipt page is corrected before another lookup");

        var receiptLimit = new Fixture();
        for (var index = 0; index < 8; index++)
        {
            receiptLimit.Model(new { action = "receipt", effectId = "prior-effect-" + index, offset = 0 });
            receiptLimit.ReceiptPage(WorkflowEffectState.Succeeded, "Historic result");
        }
        receiptLimit.Model(new { action = "receipt", effectId = "prior-effect-9", offset = 0 });
        Check(receiptLimit.IsModel && receiptLimit.Step.State.GetProperty("repairs").GetInt32() == 1, "receipt lookups have a finite no-progress allowance");
        var foreignReceipt = new Fixture(); foreignReceipt.Model(new { action = "receipt", effectId = "stored-effect", offset = 0 });
        foreignReceipt.ReceiptPage(WorkflowEffectState.Succeeded, "Unrelated result", runId: "other-run");
        Check(foreignReceipt.Step.Next is CompleteWorkflow { Success: false }, "receipt page must belong to the current workflow");
        var missingReceipt = new Fixture(); missingReceipt.Model(new { action = "receipt", effectId = "missing-effect", offset = 0 });
        missingReceipt.MissingReceipt();
        Check(missingReceipt.IsModel && missingReceipt.Messages.Contains("Receipt not found", StringComparison.Ordinal), "failed receipt lookup returns its actual error for a bounded correction");
        var invalidReceipt = new Fixture(); invalidReceipt.Model(new { action = "receipt", effectId = "stored-effect", offset = "zero" });
        Check(invalidReceipt.IsModel && invalidReceipt.Step.State.GetProperty("repairs").GetInt32() == 1, "malformed receipt offset is corrected without throwing or dispatching");
        var longReceipt = new Fixture(); longReceipt.Model(new { action = "receipt", effectId = new string('x', 129), offset = 0 });
        Check(longReceipt.IsModel, "receipt effect ID uses the host's 128-character bound");
        var zeroRevision = new Fixture(); zeroRevision.Model(new { action = "receipt", effectId = "stored-effect", offset = 0, expectedRevision = 0 });
        Check(zeroRevision.IsModel, "receipt revision uses the host's positive revision bound");

        return checks;
    }

    private static void ReadProjectionCompleteness(Action<bool, string> check)
    {
        var raw = ProgrammingReadProjectionFixture.Result();
        check(System.Text.Encoding.UTF8.GetByteCount(ProgrammingReadProjectionFixture.Source) == 5940 &&
              System.Text.Encoding.UTF8.GetByteCount(raw) == 6918 &&
              Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                  System.Text.Encoding.UTF8.GetBytes(ProgrammingReadProjectionFixture.Source))).ToLowerInvariant() == ProgrammingReadProjectionFixture.Sha256,
            "the retained failed-model fixture preserves the actual 5940-byte source and 6918-byte raw read");

        var read = new Fixture();
        var path = Path.Combine(read.Root, "GeneratedServicePlugin.cs");
        read.Model(new { action = "tool", name = "file.read", arguments = new { path } });
        read.Tool(true, raw);
        var prefix = read.Context.Contributions.Last();
        check(!prefix.Complete && prefix.ExactReadHandle is not null &&
              prefix.ExactReadHandle == read.Step.State.GetProperty("readReceipts")[0].GetString(),
            "a shortened readable preview retains its exact receipt handle and cannot become complete source evidence");
        check(read.LastContributionText.Contains("Displayed source lines: 1-149 of 176", StringComparison.Ordinal) &&
              read.LastContributionText.Contains("startLine=150 and lineCount=27", StringComparison.Ordinal) &&
              read.LastContributionText.Contains(ProgrammingReadProjectionFixture.Sha256, StringComparison.Ordinal),
            "the actual bounded source preview preserves line coordinates, continuation and whole-file preimage");
        check(read.LastContributionText.Contains("\"contentTruncated\":true", StringComparison.Ordinal) &&
              read.LastContributionText.Contains("\"partial\":false", StringComparison.Ordinal),
            "a shortened model preview is incomplete without relabeling the successful raw command as partial");

        read.Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 150, lineCount = 27 } });
        read.Tool(true, ProgrammingReadProjectionFixture.Result(150, 27));
        var tail = read.Context.Contributions.Last();
        var tailText = read.LastContributionText;
        check(tail.Complete && tail.ExactReadHandle is not null &&
              tailText.Contains("\"contentTruncated\":false", StringComparison.Ordinal) &&
              tailText.Contains("\"partial\":false", StringComparison.Ordinal) &&
              tailText.Contains("return Task.FromResult(result);", StringComparison.Ordinal),
            "all returned tail source remains complete with a successful raw command and exact receipt");

        read.Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = 176 } });
        read.Tool(true, raw);
        check(!read.Context.Contributions.Last().Complete &&
              read.Context.Contributions.Single(source => source.Id == tail.Id).Complete &&
              read.Context.Contributions.Single(source => source.Id == tail.Id).Messages.Single().Content.Single() is TextContentPart retainedTail &&
              retainedTail.Text == tailText,
            "a broader shortened reread preserves the already-retained complete tail and its exact messages");

        read.Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 150, lineCount = 27 } });
        check(read.IsModel && read.Step.State.GetProperty("readReceipts").GetArrayLength() == 3 &&
              read.Step.State.GetProperty("editCount").GetInt32() == 0 && read.CheckCount == 0,
            "a duplicate continuation is rejected before dispatch without new source, edit or check credit");
        check(read.LastContributionText.Contains(path, StringComparison.Ordinal) &&
              read.LastContributionText.Contains("startLine=150, lineCount=27", StringComparison.Ordinal) &&
              read.LastContributionText.Contains("retained source and its exact receipt", StringComparison.Ordinal) &&
              !read.LastContributionText.Contains("continuation", StringComparison.OrdinalIgnoreCase),
            "duplicate-read feedback identifies the blocked range and retained evidence without inventing an unread continuation");
    }

    private sealed partial class Fixture
    {
        private readonly IAgentHarness harness;
        private readonly WorkflowSubmission submission;
        private IReadOnlyList<WorkflowSteeringEntry> ownerSteering = [];
        private int sequence;
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-programming-fixture"));
        public WorkflowStep Step { get; private set; }
        public InvokeWorkflowCommand Command => (InvokeWorkflowCommand)Step.Next;
        public bool IsModel => Step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:generate" };
        public bool IsTool => Step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:tool" };
        public bool IsCompact => Step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:compact" };
        public bool IsReceipt => Step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:receipt" };
        public string Messages => IsModel ? Command.Arguments.GetRawText() : "";
        public ContextWindow Context => Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
        public string LastContributionText => ((TextContentPart)Context.Contributions.Last().Messages.Single().Content.Single()).Text;
        public int CheckCount => Step.State.GetProperty("checks").GetArrayLength();
        public string PendingTool => Command.Arguments.GetProperty("name").GetString()!;
        public string[] Argv => Command.Arguments.GetProperty("arguments").GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        public string WorkingDirectory => Command.Arguments.GetProperty("arguments").GetProperty("workingDirectory").GetString()!;

        public Fixture(string[]? requiredChecks = null, string[]? selectedTools = null, string? presentManifest = null,
            string projectFile = "Hello.csproj", string? contractTopic = null, string? rootPath = null,
            bool guardFailedChecks = false, bool guardFinalization = false, bool guardInterfacePresentation = false,
            bool prerequisiteProgress = false)
        {
            harness = prerequisiteProgress ? new ProgrammingPrerequisiteProgressHarness() :
                guardInterfacePresentation ? new ProgrammingInterfacePresentationHarness() :
                guardFinalization ? new ProgrammingFinalizationProgressHarness() :
                guardFailedChecks ? new ProgrammingVerificationRetryHarness() : new ProgrammingAgentHarness();
            if (rootPath is not null) Root = Path.GetFullPath(rootPath);
            var input = new Dictionary<string, object> { ["rootPath"] = Root, ["projectFile"] = projectFile };
            if (requiredChecks is not null) input["requiredChecks"] = requiredChecks;
            if (selectedTools is not null) input["selectedTools"] = selectedTools;
            if (presentManifest is not null) input["presentManifest"] = presentManifest;
            if (contractTopic is not null) input["contractTopic"] = contractTopic;
            submission = new("programming-test", "conversation", "coding", harness.Descriptor.Id, harness.Descriptor.Version,
                "Read and fix the project, build it and run it.", null, CerebralJson.Element(input), WorkflowContract.EmptyObject);
            Step = harness.Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running }));
        }
        public void Model(object action, ModelGenerationFinish finish = ModelGenerationFinish.Stop, bool outputOmitted = false) =>
            ModelText(JsonSerializer.Serialize(action, CerebralJson.Options), finish, outputOmitted);
        public void ModelText(string text, ModelGenerationFinish finish = ModelGenerationFinish.Stop, bool outputOmitted = false) =>
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new { generation = new ModelGenerationResult(finish, text), outputOmitted, context = Projection(ContextProjectionStatus.Ready) }));
        public void MissingGeneration() => Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new { generation = (object?)null, outputOmitted = false }));
        public void Uncertain() => Result(WorkflowEffectState.Unknown, WorkflowContract.EmptyObject, "Unknown provider outcome");
        public void Cancel() => Step = harness.Advance(new(new WorkflowRun
        {
            Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Cancelling,
            CancelRequested = true, State = Step.State, Steering = ownerSteering
        }));
        public void Tool(bool success, string content, int? exitCode = null, bool truncated = false, string error = "Command failed")
        {
            var name = Command.Arguments.GetProperty("name").GetString();
            var outcome = success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed;
            Result(success ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed,
                CerebralJson.Element(new { name, content, truncated, evidence = new ToolExecutionEvidence(outcome, "deterministic-fixture", ExitCode: exitCode), followUpRequired = false }), success ? null : error);
        }
        public void Read()
        {
            Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(Root, "Program.cs") } });
            Tool(true, "Console.WriteLine(\"Hello\");");
        }
        public void ReadRange(int startLine, int lineCount)
        {
            Model(new { action = "tool", name = "file.read", arguments = new { path = Path.Combine(Root, "Program.cs"), startLine, lineCount } });
            Tool(true, $"Source lines {startLine}-{startLine + lineCount - 1}");
        }
        public void Write()
        {
            Model(new { action = "tool", name = "file.write", arguments = new { path = Path.Combine(Root, "Program.cs"), content = "Console.WriteLine(\"Repaired\");", collision = "replace" } });
            Tool(true, "Source replaced");
        }
        public void Dotnet(string kind, string configuration = "Release") => Model(new
        {
            action = "tool", name = "process.run", arguments = new
            {
                executableId = "dotnet", workingDirectory = Root,
                arguments = kind == "run" ? new[] { "run", "--project", "Hello.csproj", "-c", configuration } : new[] { kind, "Hello.csproj", "-c", configuration }
            }
        });
        public void Process(string[] arguments, int timeoutSeconds = 30) => Model(new
        {
            action = "tool", name = "process.run", arguments = new { executableId = "dotnet", workingDirectory = Root, arguments, timeoutSeconds }
        });
        public void ContextRejected(ContextProjectionStatus status, long revisionDelta = 0) =>
            Result(WorkflowEffectState.Failed, CerebralJson.Element(new { context = Projection(status) with { ContextRevision = Context.Revision + revisionDelta } }), "Exact context budget rejected preparation");
        public void CompactSuccess(Func<ContextCheckpoint, ContextCheckpoint>? change = null, int afterInputTokens = 256)
        {
            var window = Context;
            var checkpoint = new ContextCheckpoint("checkpoint-" + (sequence + 1), window.Revision, window.ExclusionRevision,
                ContextValidation.GetCoveredManifest(window), "Prior reads and edits are summarized; current required evidence and operational check receipts remain authoritative.", "fixture-effect-" + (sequence + 1))
                { SourceLocators = ContextValidation.GetCoveredLocators(window) };
            if (change is not null) checkpoint = change(checkpoint);
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
            {
                checkpoint, before = Metadata(1800), after = Metadata(afterInputTokens), passes = 1, usage = Array.Empty<object>()
            }));
        }
        public void CompactionFailure(WorkflowEffectState state) => Result(state, CerebralJson.Element(new { usage = Array.Empty<object>(), reason = "Compaction did not finish; no candidate adopted" }), "Compaction incomplete");
        public void IncompleteCheckpoint() => Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new { generation = new ModelGenerationResult(ModelGenerationFinish.Length, "partial summary") }));
        public void ReceiptPage(WorkflowEffectState referencedState, string json, int? nextOffset = null, int? totalBytes = null, string? runId = null)
        {
            var arguments = Command.Arguments;
            var offset = arguments.GetProperty("offset").GetInt32();
            var revision = arguments.TryGetProperty("expectedRevision", out var expected) ? expected.GetInt64() : 10;
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new WorkflowReceiptPage(runId ?? submission.RequestId,
                arguments.GetProperty("effectId").GetString()!, referencedState, null, null, revision, offset, 4096,
                totalBytes ?? offset + System.Text.Encoding.UTF8.GetByteCount(json), json, nextOffset, nextOffset is not null)));
        }
        public void MissingReceipt() => Result(WorkflowEffectState.Failed, WorkflowContract.EmptyObject, "Receipt not found in this workflow");
        private ContextProjection Projection(ContextProjectionStatus status) => new(status, Context.Revision, Context.ExclusionRevision,
            Context.Contributions.Select(source => new ContextSourceReference(source.Id, source.SourceRevision)).ToArray(), [], Metadata(status == ContextProjectionStatus.Ready ? 128 : 1800),
            status == ContextProjectionStatus.RequiredOverflow ? "Required task input exceeds the model context." : null);
        private static PreparedModelMetadata Metadata(int inputTokens) => new("fixture-instance", "fixture-adapter", "1.0.0", "fixture-model", inputTokens, 2048, 256, "deterministic-test") { SafetyReserveTokens = 128 };
        private void Result(WorkflowEffectState state, JsonElement data, string? error = null, JsonElement? effectArguments = null)
        {
            var command = Command;
            var effect = new WorkflowEffect("fixture-effect-" + ++sequence, command.CommandId, command.CommandVersion, effectArguments ?? command.Arguments,
                WorkflowReplayPolicy.Never, state, data, error);
            Step = harness.Advance(new(new WorkflowRun
            {
                Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running, State = Step.State,
                Signal = new(WorkflowSignalKind.EffectResult, data), Effect = effect, Steering = ownerSteering
            }));
        }
    }
}
