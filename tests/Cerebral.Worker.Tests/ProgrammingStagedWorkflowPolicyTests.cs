using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

/// <summary>Pure policy fixtures. No filesystem, process, provider, model, network or app is executed.</summary>
public static partial class ProgrammingStagedWorkflowPolicyTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Programming stages: " + message);
            checks++;
        }

        using var plugin = new ProgrammingServicePlugin();
        var ordinary = plugin.Harnesses.Where(x => x.Descriptor.Id == ProgrammingAgentHarness.Id &&
            x.Descriptor.Version == ProgrammingAgentHarness.Version).ToArray();
        Check(plugin.Harnesses.Count == 8 && ordinary.Length == 1 && ProgrammingAgentHarness.Version == "1.3.21" &&
            plugin.Harnesses.Select(x => (x.Descriptor.Id, x.Descriptor.Version)).Distinct().Count() == 8,
            "the eight explicit editions retain one unambiguous legacy descriptor matching the desktop/service identity");
        Check(plugin.Harnesses.Single(x => x.Descriptor.Id == ProgrammingAgentHarness.StagedId).Descriptor.Version == "1.0.0",
            "the staged edition is explicit and additive");
        var initial = new StageFixture();
        Check(initial.IsTool("file.read") && initial.StageName == "inputs", "the first action is an actual declared-input read, before inference");
        initial.ReadSources();
        Check(initial.IsTool("process.run") && initial.StageName == "baseline-build" && !initial.IsModel,
            "all required reads precede the deterministic baseline build");
        Check(initial.Argv.SequenceEqual(new[] { "build", "App.csproj", "-c", "Release", "--nologo", "-v", "minimal", "--disable-build-servers", "-m:1" }),
            "the staged caller carries its exact accepted build argv into the shared progress guard");
        initial.Process(0, "Build succeeded", "");
        Check(initial.StageName == "baseline-validate" && initial.IsTool("file.read"), "baseline proof is followed by declared-file revalidation");
        initial.ReadSources();
        Check(initial.IsModel && initial.StageName == "repair", "an ordinary successful baseline is expressible");
        initial.Edit();
        Check(initial.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 1 &&
            initial.Stages.GetProperty("epoch").GetInt32() == 1, "changed-byte proof advances source generation after pre-dispatch invalidation");
        initial.Model(new { action = "done", summary = "Ready" });
        initial.ReadSources();
        Check(initial.StageName == "build" && initial.IsTool("process.run"), "repair completion requests a fresh current build");
        initial.Process(0, "Build succeeded", "");
        initial.ReadSources();
        Check(initial.StageName == "check" && initial.Argv.SequenceEqual(new[] { Path.Combine(initial.Root, "bin", "Release", "net10.0", "App.dll") }),
            "the exact configured built DLL check follows the current build");
        initial.Process(0, "PASS\n", "");
        initial.ReadSources();
        Check(initial.Passed && initial.Final.GetProperty("baselineBuild").GetProperty("effectId").GetString() !=
            initial.Final.GetProperty("currentBuild").GetProperty("effectId").GetString(), "a historical baseline build is never reused as final build proof");
        Check(initial.Final.GetProperty("currentCheck").GetProperty("buildEffectId").GetString() ==
            initial.Final.GetProperty("currentBuild").GetProperty("effectId").GetString(), "the final check names its exact current build effect");
        Check(initial.Final.GetProperty("sourceManifestSha256").GetString() is { Length: 64 } &&
            initial.Final.GetProperty("mutationEffects").GetArrayLength() == 1, "completion exposes bounded manifest and real mutation references");

        var compile = new StageFixture(compilerBaseline: true);
        compile.ReadSources();
        compile.Process(1, $"Program.cs(1,1): error CS0103: The name 'missing' does not exist [{Path.Combine(compile.Root, "App.csproj")}]\n", "");
        compile.ReadSources();
        Check(compile.IsModel && compile.LastProcessState == WorkflowEffectState.Failed &&
            compile.Stages.GetProperty("baselineBuild").GetProperty("exitCode").GetInt32() == 1,
            "an explicitly expected compiler failure remains a failed effect while unlocking repair");

        var runtime = new StageFixture(runtimeBaseline: true);
        runtime.ReadSources(); runtime.Process(0, "", "");
        Check(runtime.StageName == "baseline-check", "an expected runtime failure first requires the successful baseline build");
        runtime.Process(1, "", "EXPECTED FAILURE\n"); runtime.ReadSources();
        Check(runtime.IsModel && runtime.LaunchFailures == 0 && runtime.LastProcessState == WorkflowEffectState.Succeeded,
            "the explicitly expected baseline runtime exit is accepted without a simulated launch failure");
        runtime.Edit(); runtime.Model(new { action = "done", summary = "Ready" }); runtime.CompleteVerification();
        Check(runtime.Passed && runtime.LaunchFailures == 0, "a later pass retains the actual accepted baseline exit");

        var mismatch = new StageFixture(compilerBaseline: true);
        mismatch.ReadSources(); mismatch.Process(1, "unrelated compiler failure", "");
        Check(mismatch.Failed, "a nonzero exit without the exact declared diagnostic cannot unlock repair");
        var successMismatch = new StageFixture(compilerBaseline: true);
        successMismatch.ReadSources(); successMismatch.Process(0, "Build succeeded", "");
        Check(successMismatch.Failed, "success does not satisfy an explicitly expected failure");
        var wrongRuntime = new StageFixture(runtimeBaseline: true);
        wrongRuntime.ReadSources(); wrongRuntime.Process(0, "", ""); wrongRuntime.Process(1, "", "different failure\n");
        Check(wrongRuntime.Failed, "runtime baseline expectations match exact complete streams");

        var clipped = new StageFixture();
        clipped.ReadSources(truncated: true);
        Check(clipped.Failed, "a truncated input read cannot establish complete inputs");
        var wrongHash = new StageFixture();
        wrongHash.ReadSources(hashOverride: new string('0', 64));
        Check(wrongHash.Failed, "an initial SHA mismatch stops before baseline dispatch");
        var incomplete = new StageFixture();
        incomplete.ReadSources(totalDelta: 1);
        Check(incomplete.Failed, "a complete receipt with incomplete whole-file coordinates earns no input credit");
        var wrongPath = new StageFixture();
        wrongPath.ReadSources(pathOverride: Path.Combine(wrongPath.Root, "Other.cs"));
        Check(wrongPath.Failed, "a foreign file-read result cannot satisfy the pending path");
        var premature = new StageFixture();
        premature.WrongEffect();
        Check(premature.Failed, "a model effect cannot replace the pending input-read effect");
        var baselineDrift = new StageFixture();
        baselineDrift.ReadSources(); baselineDrift.Process(0, "", "");
        baselineDrift.Files["Checks.cs"] += "// changed\n"; baselineDrift.ReadSources();
        Check(baselineDrift.Failed, "baseline source drift is caught before repair is unlocked");

        var immutable = StageFixture.InRepair();
        immutable.ModelEdit("Checks.cs", "changed");
        Check(immutable.IsModel && immutable.Stages.GetProperty("epoch").GetInt32() == 0,
            "immutable edits are rejected before dispatch or invalidation");
        var foreign = StageFixture.InRepair();
        foreign.ModelEdit("Other.cs", "changed", hash: new string('a', 64));
        Check(foreign.IsModel, "undeclared paths remain unavailable");
        var staleEdit = StageFixture.InRepair();
        staleEdit.ModelEdit("Program.cs", "changed", hash: new string('0', 64));
        Check(staleEdit.IsModel && staleEdit.Stages.GetProperty("mutationEffects").GetArrayLength() == 0,
            "a stale mutation preimage creates no progress");
        var unchanged = StageFixture.InRepair();
        unchanged.ModelEdit("Program.cs", unchanged.Files["Program.cs"]);
        Check(unchanged.IsModel && unchanged.Stages.GetProperty("epoch").GetInt32() == 0,
            "unchanged model edits dispatch no effect and do not advance generation");
        var skipped = StageFixture.InRepair();
        skipped.ModelEdit("Program.cs", "changed");
        Check(skipped.Stages.GetProperty("epoch").GetInt32() == 1, "credit is invalidated before the mutation result");
        skipped.MutationResult(skipped: true);
        Check(skipped.Failed && skipped.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 0,
            "a skipped or identical mutation cannot become changed-byte proof");
        var failedMutation = StageFixture.InRepair();
        failedMutation.ModelEdit("Program.cs", "changed"); failedMutation.MutationResult(success: false);
        Check(failedMutation.Failed && failedMutation.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null,
            "an unproved failed mutation stays invalidated without a retry");
        var command = StageFixture.InRepair();
        command.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet",
            workingDirectory = command.Root, arguments = new[] { "build", "App.csproj", "-c", "Release" } } });
        Check(command.IsModel && command.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null,
            "the model cannot substitute a process plan for deterministic verification");
        var missingMutation = StageFixture.InRepair();
        missingMutation.Model(new { action = "done", summary = "Already fixed" });
        Check(missingMutation.IsModel, "required source-change work cannot finish on a claim");
        var noChange = new StageFixture(requireChange: false); noChange.ToRepair();
        noChange.Model(new { action = "done", summary = "No change needed" }); noChange.CompleteVerification();
        Check(noChange.Passed && noChange.Final.GetProperty("mutationEffects").GetArrayLength() == 0,
            "a declared no-change task still needs fresh current build/check evidence");

        var buildFailure = StageFixture.InRepair();
        buildFailure.Edit(); buildFailure.Model(new { action = "done", summary = "Ready" }); buildFailure.ReadSources();
        buildFailure.Process(1, "compiler failure", "");
        Check(buildFailure.IsModel && buildFailure.StageName == "repair", "a confirmed current build failure returns actual diagnostics for repair");
        var checkFailure = StageFixture.InRepair();
        checkFailure.Edit(); checkFailure.Model(new { action = "done", summary = "Ready" }); checkFailure.ReadSources();
        checkFailure.Process(0, "", ""); checkFailure.ReadSources(); checkFailure.Process(1, "", "bad check\n");
        Check(checkFailure.IsModel && checkFailure.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null,
            "a failed check clears current completion credit before repair");
        var partialBuild = StageFixture.InRepair();
        partialBuild.Edit(); partialBuild.Model(new { action = "done", summary = "Ready" }); partialBuild.ReadSources();
        partialBuild.Process(0, "", "", truncated: true);
        Check(partialBuild.Failed, "an incomplete successful build cannot become current proof");
        foreach (var field in new[] { "epoch", "manifestSha256", "commandSha256" })
        {
            var staleBuild = StageFixture.InRepair();
            staleBuild.Edit(); staleBuild.Model(new { action = "done", summary = "Ready" }); staleBuild.ReadSources();
            staleBuild.Process(0, "", "");
            staleBuild.ChangeState(node => {
                if (field == "epoch") node["stages"]!["currentBuild"]![field] = 0;
                else node["stages"]!["currentBuild"]![field] = new string('0', 64);
            });
            staleBuild.ReadSources();
            Check(staleBuild.Failed, "stale build " + field + " cannot dispatch the current check");
        }
        var pao = new StageFixture();
        pao.ReadSources(); pao.PaoProcess(0); pao.ReadSources();
        Check(pao.IsModel, "a complete PAO build envelope with exact request/job joins can prove a baseline");
        foreach (var fault in new[] { "request", "bodyJob", "providerReceipt", "timeout", "partial" })
        {
            var badPao = new StageFixture(); badPao.ReadSources(); badPao.PaoProcess(0, fault);
            Check(badPao.Failed, "PAO " + fault + " cannot establish process proof");
        }
        var badNativeExit = new StageFixture(); badNativeExit.ReadSources(); badNativeExit.Process(0, "", "", bodyExit: 1);
        Check(badNativeExit.Failed, "native child exit and provider evidence must agree");
        var badNativeProvider = new StageFixture(); badNativeProvider.ReadSources();
        badNativeProvider.Process(0, "", "", providerId: "unrecognized.provider");
        Check(badNativeProvider.Failed, "unsupported process provider text is never upgraded into check evidence");
        var equivalent = StageFixture.InRepair(); equivalent.Edit();
        equivalent.Model(new { action = "done", summary = "Ready" }); equivalent.ReadSources();
        equivalent.Process(1, $"Program.cs(1,1): error CS1002: ; expected [{Path.Combine(equivalent.Root, "App.csproj")}]\n", "");
        for (var attempt = 0; attempt < 3; attempt++)
            equivalent.Model(new { action = "done", summary = "Try again" });
        Check(equivalent.Failed && equivalent.Step.Next is not InvokeWorkflowCommand &&
            equivalent.Step.State.GetProperty("verificationCycles").GetInt32() == 1,
            "unchanged failed-final retries stop at the correction bound without another build");
        var postBuild = StageFixture.InRepair();
        postBuild.Edit(); postBuild.Model(new { action = "done", summary = "Ready" }); postBuild.ReadSources();
        postBuild.Process(0, "", ""); postBuild.Files["Program.cs"] += "// drift"; postBuild.ReadSources();
        Check(postBuild.Failed, "post-build drift prevents check dispatch");
        var postCheck = StageFixture.InRepair();
        postCheck.Edit(); postCheck.Model(new { action = "done", summary = "Ready" }); postCheck.ReadSources();
        postCheck.Process(0, "", ""); postCheck.ReadSources(); postCheck.Process(0, "PASS\n", "");
        postCheck.Files["Checks.cs"] += "// drift"; postCheck.ReadSources();
        Check(postCheck.Failed, "post-check immutable drift prevents final success");

        var unknown = StageFixture.InRepair();
        unknown.ModelEdit("Program.cs", "changed"); unknown.Unknown();
        Check(unknown.Failed && unknown.Step.Next is not InvokeWorkflowCommand, "direct policy defense never replays an Unknown mutation");
        var cancel = new StageFixture(); cancel.Cancel();
        Check(cancel.Failed && cancel.Step.Next is not InvokeWorkflowCommand, "cancellation dispatches no stage action");
        var lost = StageFixture.InRepair(); lost.ChangeState(node => node.Remove("stages")); lost.Model(new { action = "done", summary = "resume" });
        Check(lost.Failed, "lost continuation cannot restart inputs or baseline");
        var badVersion = StageFixture.InRepair();
        badVersion.ChangeState(node => node["stages"]!["schemaVersion"] = 2); badVersion.Model(new { action = "done", summary = "resume" });
        Check(badVersion.Failed, "unsupported continuation versions fail closed");
        var badContract = StageFixture.InRepair();
        badContract.ChangeState(node => node["stages"]!["contractSha256"] = new string('0', 64));
        badContract.Model(new { action = "done", summary = "resume" });
        Check(badContract.Failed, "a contract hash mismatch cannot reuse staged proof");
        var compact = StageFixture.InRepair();
        var manifestBefore = compact.Stages.GetProperty("currentInputs").GetRawText();
        compact.Compact();
        Check(compact.IsModel && compact.StageName == "repair" && compact.Stages.GetProperty("currentInputs").GetRawText() == manifestBefore,
            "compaction preserves stage references and does not create source/check progress");
        var steer = StageFixture.InRepair();
        steer.Steering = [new("owner-update", 1, "Preserve the accepted scope and inspect carefully.", null, DateTimeOffset.UnixEpoch.AddDays(1))];
        steer.Model(new { action = "done", summary = "old choice" });
        Check(steer.IsModel && steer.StageName == "repair" && steer.Step.State.GetProperty("ownerSteeringCount").GetInt32() == 1 &&
            steer.Stages.GetProperty("mutationEffects").GetArrayLength() == 0, "owner steering supersedes undispatched choices without changing source evidence");
        var invalidPlan = new StageFixture(extraBuildArgument: "-t:Help");
        Check(invalidPlan.Failed, "arbitrary MSBuild targets are rejected at input intake");
        var invalidAssembly = new StageFixture(assembly: "../foreign.dll");
        Check(invalidAssembly.Failed, "the check cannot execute a DLL outside the configured build tree");

        return checks + RunGuidanceChecks() + RunSelectorChecks() + RunCompilerEnvelopeChecks() + RunRepairFeedbackChecks() + RunReadReuseChecks() + RunVerificationCadenceChecks();
    }

    public static int RunCatalogChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Programming catalog: " + message);
            checks++;
        }
        string[] Names(StageFixture fixture) => fixture.Command.Arguments.GetProperty("toolNames")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        // Exact twelve-name accepted catalog observed in the failed staged run.
        string[] accepted = ["plugin.projects", "plugin.scaffold", "file.read", "plugin.verify", "file.list",
            "file.search", "file.write", "file.edit", "file.mkdir", "process.run", "plugin.contract", "program.source.generate"];
        var current = new StageFixture(acceptedTools: accepted); current.ToRepair();
        Check(Names(current).SequenceEqual(new[] { "file.read", "file.edit", "file.write" }),
            "the real accepted twelve-name catalog offers only three supported repair tools");
        current.Model(new { action = "tool", name = "file.edit-range",
            arguments = new { path = Path.Combine(current.Root, "Program.cs"), expectedSha256 = new string('a', 64),
                startLine = 1, lineCount = 1, newText = "replacement" } });
        Check(current.IsModel && current.Stages.GetProperty("epoch").GetInt32() == 0,
            "an unadvertised richer tool cannot dispatch or invalidate staged evidence");
        var rich = new StageFixture(); rich.ToRepair();
        Check(Names(rich).SequenceEqual(new[] { "file.read", "file.edit", "file.write", "file.edit-range", "program.source.structure" }),
            "richer repair tools remain available when present in the accepted catalog");
        Check(new StageFixture(acceptedTools: ["file.read", "file.edit"]).Failed,
            "missing deterministic process permission refuses before host stages");
        Check(new StageFixture(acceptedTools: ["file.edit", "process.run"]).Failed,
            "missing deterministic read permission refuses before host stages");
        Check(new StageFixture(acceptedTools: ["file.read", "process.run"]).Failed,
            "required source change without a granted mutation tool refuses before host stages");
        var readOnly = new StageFixture(requireChange: false, acceptedTools: ["file.read", "process.run"]);
        readOnly.ToRepair();
        Check(Names(readOnly).SequenceEqual(new[] { "file.read" }),
            "a declared no-change task may inspect without receiving a mutation or model process tool");
        Check(new StageFixture(acceptedTools: ["file.read", "process.run", "file.edit", "file.edit"]).Failed,
            "duplicate frozen descriptor names refuse before effects");
        Check(new StageFixture(executionSettings: WorkflowContract.EmptyObject).Failed,
            "missing frozen catalog cannot acquire the old hardcoded defaults");
        Check(new StageFixture(executionSettings: CerebralJson.Element(new { resolvedSettingsVersion = 5, tools = "invalid" })).Failed,
            "wrong-kind frozen catalog refuses before effects");
        Check(new StageFixture(executionSettings: CerebralJson.Element(new { resolvedSettingsVersion = 4,
            tools = accepted.Select(name => new { name }).ToArray() })).Failed,
            "unsupported execution snapshot edition refuses before effects");
        return checks;
    }

    public static int RunGuidanceChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Programming staged guidance: " + message);
            checks++;
        }
        static string Policy(JsonElement arguments)
        {
            var message = arguments.GetProperty("messages")[0].Deserialize<ModelInputMessage>(CerebralJson.Options)!;
            var text = ((TextContentPart)message.Content.Single()).Text;
            return text[..text.IndexOf("\nAccepted project and host progress:\n", StringComparison.Ordinal)];
        }
        const string directBuild = """
            To verify use process.run with executableId=dotnet and separate argv: build/test
            <projectFile>, or run --project <projectFile>. Use either the accepted root or the
            directory containing the accepted projectFile as workingDirectory; the host normalizes it.
            You may omit the project because this plugin inserts the accepted projectFile. A single
            positional run project is normalized to --project only if it matches that accepted file.
            Optional arguments: -c/--configuration Debug|Release, --no-restore,
            --disable-build-servers, --nologo, -v/--verbosity minimal|quiet|normal,
            and --no-build for test/run. Use matching configurations for required checks.
            For a new project, run build without --no-restore so the SDK can create its assets
            file. Use --no-restore only after restore has succeeded for the current project.
            NETSDK1004 means the assets file is missing: use normal build without --no-restore,
            not a standalone restore command. Standalone restore is not an admitted check.
            """;
        var staged = StageFixture.InRepair();
        var regularInput = CerebralJson.Element(new {
            rootPath = staged.Root, projectFile = "App.csproj", configuration = "Release",
            selectedTools = new[] { "file.read", "file.edit", "file.write", "file.edit-range", "program.source.structure", "process.run" },
            requiredChecks = new[] { "build", "run" }
        });
        var submission = new WorkflowSubmission("guidance-regular", "guidance-conversation", "coding",
            ProgrammingAgentHarness.Id, ProgrammingAgentHarness.Version, "Inspect and repair this project.",
            null, regularInput, WorkflowContract.EmptyObject);
        var regular = new ProgrammingAgentHarness().Advance(new(new WorkflowRun {
            Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running
        }));
        Check(regular.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:generate" },
            "ordinary execution still requests its model normally");
        var regularArguments = ((InvokeWorkflowCommand)regular.Next).Arguments;
        var ordinaryPolicy = Policy(regularArguments);
        var stagedPolicy = Policy(staged.Command.Arguments);
        Check(ordinaryPolicy.Contains(directBuild + "\n", StringComparison.Ordinal),
            "ordinary execution retains the complete direct-build guidance verbatim");
        Check(!stagedPolicy.Contains("To verify use process.run", StringComparison.Ordinal) &&
            !stagedPolicy.Contains("Optional arguments: -c/--configuration", StringComparison.Ordinal),
            "staged execution has no competing direct-process instruction or argv tutorial");
        var stagedSuffix = stagedPolicy.IndexOf("\nStaged repair contract:", StringComparison.Ordinal);
        var ordinaryInputEvidence = ordinaryPolicy.IndexOf("\nRetry credit requires", StringComparison.Ordinal);
        Check(ordinaryInputEvidence >= 0 && ordinaryPolicy.Contains("-getItem:Compile,EmbeddedResource,AdditionalFiles,Content,None", StringComparison.Ordinal) &&
            ordinaryPolicy.Contains("-noAutoResponse", StringComparison.Ordinal) &&
            stagedPolicy.Contains("one accepted mutable required input at its exact current SHA", StringComparison.Ordinal) &&
            !stagedPolicy.Contains("-getItem:", StringComparison.Ordinal),
            "ordinary metadata discovery and staged declared-input evidence remain explicit without granting staged direct process commands");
        Check(stagedSuffix >= 0 && stagedPolicy[..stagedSuffix] ==
            ordinaryPolicy[..ordinaryInputEvidence].Replace(directBuild + "\n", "", StringComparison.Ordinal),
            "all common guidance is identical after removing only the ordinary direct-build and input-evidence blocks");
        Check(stagedPolicy.Contains("deterministic input/baseline/build/check stages own process commands", StringComparison.Ordinal) &&
            stagedPolicy.Contains("return done to begin fresh verification", StringComparison.Ordinal) &&
            stagedPolicy.Contains("Only real exit-zero receipts after the last source edit count", StringComparison.Ordinal),
            "staged policy ownership and evidence requirements remain explicit");
        var schema = regularArguments.GetProperty("responseSchema").GetRawText();
        Check(schema == staged.Command.Arguments.GetProperty("responseSchema").GetRawText(),
            "the response schema is unchanged between ordinary and staged execution");
        Check(!staged.Command.Arguments.GetProperty("toolNames").EnumerateArray().Any(x => x.GetString() == "process.run") &&
            regularArguments.GetProperty("toolNames").EnumerateArray().Any(x => x.GetString() == "process.run"),
            "only the staged model catalog excludes policy-owned processes");
        var before = staged.Stages.GetRawText();
        staged.Model(new { action = "tool", name = "process.run", arguments = new {
            executableId = "dotnet", workingDirectory = staged.Root, arguments = new[] { "build", "App.csproj", "-c", "Release" }
        } });
        Check(staged.IsModel && staged.Stages.GetRawText() == before &&
            staged.Step.State.GetProperty("repairs").GetInt32() == 1 &&
            staged.Step.State.GetProperty("editCount").GetInt32() == 0,
            "a fake invalid process proposal is rejected without dispatch or source/check credit");
        var context = staged.Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
        var correction = context.Contributions.Last(x => x.Category == "repair-diagnostic");
        Check(correction.Requirement == ContextRequirement.Required && correction.Complete &&
            ((TextContentPart)correction.Messages.Single().Content.Single()).Text.Contains("Return done", StringComparison.Ordinal),
            "the next fake-model context retains the complete policy-owned-process correction as required evidence");
        Check(!Policy(staged.Command.Arguments).Contains("To verify use process.run", StringComparison.Ordinal) &&
            staged.Command.Arguments.GetProperty("responseSchema").GetRawText() == schema,
            "corrective generation keeps coherent staged guidance and the same response schema");
        const string changed = "class Implementation { public const int Value = 1; }\n";
        var oldText = staged.Files["Program.cs"];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldText))).ToLowerInvariant();
        staged.Model(new { action = "tool", name = "file.edit", arguments = new {
            path = "Program.cs", oldText, newText = changed, expectedSha256 = hash
        } });
        Check(staged.IsTool("file.edit") &&
            staged.Command.Arguments.GetProperty("arguments").GetProperty("path").GetString() == Path.Combine(staged.Root, "Program.cs"),
            "an exact short selector still maps to the declared canonical file after a rejected model action");
        staged.MutationResult();
        staged.Model(new { action = "done", summary = "Ready for configured verification" });
        Check(staged.IsTool("file.read") && staged.StageName == "build-inputs",
            "done requests policy-owned fresh verification instead of claiming success");
        staged.CompleteVerification();
        Check(staged.Passed && staged.Step.State.GetProperty("verificationCycles").GetInt32() == 1 &&
            staged.Final.GetProperty("currentBuild").GetProperty("exitCode").GetInt32() == 0 &&
            staged.Final.GetProperty("currentCheck").GetProperty("exitCode").GetInt32() == 0,
            "only the complete fake receipt sequence can finish the staged fixture");
        var wrong = StageFixture.InRepair();
        before = wrong.Stages.GetRawText();
        wrong.Model(new { action = "tool", name = "file.edit", arguments = new {
            path = Path.Combine(wrong.Root + "-typo", "Program.cs"), oldText = wrong.Files["Program.cs"],
            newText = changed, expectedSha256 = hash
        } });
        Check(wrong.IsModel && wrong.Stages.GetRawText() == before,
            "a mistyped absolute directory remains rejected without staged proof changes");
        context = wrong.Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
        correction = context.Contributions.Last(x => x.Category == "repair-diagnostic");
        Check(correction.Requirement == ContextRequirement.Required && correction.Complete &&
            ((TextContentPart)correction.Messages.Single().Content.Single()).Text.Contains("repairFiles", StringComparison.Ordinal),
            "the exact-selector correction remains complete required context");
        var capped = StageFixture.InRepair();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            capped.Model(new { action = "tool", name = "process.run", arguments = new {
                executableId = "dotnet", workingDirectory = capped.Root, arguments = new[] { "build", "App.csproj" }
            } });
            if (attempt < 2) Check(capped.IsModel, "each available action correction requests only another fake-model turn");
        }
        Check(capped.Failed && capped.Step.State.GetProperty("repairs").GetInt32() == 2 &&
            capped.Step.State.GetProperty("verificationCycles").GetInt32() == 0 &&
            capped.Step.State.GetProperty("editCount").GetInt32() == 0,
            "the fixed two-correction limit still terminates a third invalid proposal without tool dispatch");
        return checks;
    }

    public static int RunSelectorChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Programming selectors: " + message);
            checks++;
        }
        string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        JsonElement RepairFiles(StageFixture fixture)
        {
            var message = fixture.Command.Arguments.GetProperty("messages")[1].Deserialize<ModelInputMessage>(CerebralJson.Options)!;
            var text = ((TextContentPart)message.Content.Single()).Text;
            using var document = JsonDocument.Parse(text[(text.IndexOf('\n') + 1)..]);
            return document.RootElement.GetProperty("repairFiles").Clone();
        }
        string Diagnostic(StageFixture fixture)
        {
            var window = fixture.Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
            return ((TextContentPart)window.Contributions.Last(source => source.Category == "repair-diagnostic").Messages.Single().Content.Single()).Text;
        }
        void Reject(StageFixture fixture, string path, string? hash = null)
        {
            var before = fixture.Stages.GetRawText();
            var edits = fixture.Step.State.GetProperty("editCount").GetInt32();
            var generation = fixture.Step.State.GetProperty("sourceMutationGeneration").GetInt32();
            var currentChecks = fixture.Step.State.GetProperty("checks").GetRawText();
            fixture.Model(new { action = "tool", name = "file.edit", arguments = new {
                path, expectedSha256 = hash ?? Hash(fixture.Files["Program.cs"]), oldText = fixture.Files["Program.cs"], newText = "changed"
            } });
            Check(fixture.IsModel && fixture.Stages.GetRawText() == before &&
                fixture.Step.State.GetProperty("editCount").GetInt32() == edits &&
                fixture.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == generation &&
                fixture.Step.State.GetProperty("checks").GetRawText() == currentChecks,
                "a rejected target/preimage requests no tool and preserves staged proofs: " + path);
            var diagnostic = Diagnostic(fixture);
            Check(Encoding.UTF8.GetByteCount(diagnostic) <= 4096 && diagnostic.Contains("repairFiles", StringComparison.Ordinal) &&
                diagnostic.Contains("expectedSha256", StringComparison.Ordinal),
                "the correction names bounded selector/hash guidance: " + path);
        }

        const string replacement = "class Implementation { public const string Value = \"\\\\n\"; }\n";
        var selector = StageFixture.InRepair();
        var original = selector.Files["Program.cs"];
        var originalHash = Hash(original);
        var targets = RepairFiles(selector).EnumerateArray().ToArray();
        Check(targets.Select(target => target.GetProperty("path").GetString()).SequenceEqual(new[] { "Program.cs", "Checks.cs", "App.csproj" }) &&
            targets[0].GetProperty("expectedSha256").GetString() == originalHash && targets[0].GetProperty("mutable").GetBoolean() &&
            !targets[1].GetProperty("mutable").GetBoolean(), "model guidance exposes the exact declared selectors, current hashes and mutability");
        selector.Model(new { action = "tool", name = "file.edit",
            arguments = new { path = "Program.cs", expectedSha256 = originalHash, oldText = original, newText = replacement } });
        var canonical = Path.Combine(selector.Root, "Program.cs");
        Check(selector.IsTool("file.edit") && selector.Command.Arguments.GetProperty("arguments").GetProperty("path").GetString() == canonical &&
            selector.Step.State.GetProperty("pendingArguments").GetProperty("path").GetString() == canonical,
            "a selector becomes the accepted canonical path before dispatch and saved proof arguments");
        var arguments = selector.Command.Arguments.GetProperty("arguments");
        Check(arguments.EnumerateObject().Count() == 4 && arguments.GetProperty("expectedSha256").GetString() == originalHash &&
            arguments.GetProperty("oldText").GetString() == original && arguments.GetProperty("newText").GetString() == replacement,
            "only path changes; source escaping and preimage arguments are preserved");
        Check(selector.Stages.GetProperty("epoch").GetInt32() == 1 &&
            selector.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 0,
            "admitted mutation invalidates before its result without granting changed-byte credit");
        selector.MutationResult();
        Check(selector.IsModel && selector.Stages.GetProperty("mutationEffects").GetArrayLength() == 1 &&
            selector.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 1 &&
            selector.Stages.GetProperty("currentInputs").EnumerateArray().Single(file => file.GetProperty("path").GetString() == canonical)
                .GetProperty("sha256").GetString() == Hash(replacement), "canonical receipt proof advances the declared file generation");
        Check(RepairFiles(selector)[0].GetProperty("expectedSha256").GetString() == Hash(replacement),
            "guidance publishes the current post-mutation hash, not the accepted initial hash");
        Reject(selector, "Program.cs", originalHash);
        Check(Diagnostic(selector).Contains(Hash(replacement), StringComparison.Ordinal), "stale-hash feedback carries the same current hash as model guidance");
        selector.Model(new { action = "tool", name = "file.read", arguments = new { path = "Program.cs", startLine = 1, lineCount = 1000 } });
        Check(selector.IsTool("file.read") && selector.Command.Arguments.GetProperty("arguments").GetProperty("path").GetString() == canonical,
            "a post-mutation selector read uses the same canonical file");
        selector.ReadSources();
        Check(selector.IsModel && selector.Stages.GetProperty("currentInputs")[0].GetProperty("sha256").GetString() == Hash(replacement),
            "a canonical read receipt agrees with the current declared-file proof");
        selector.Model(new { action = "done", summary = "Ready for configured verification" });
        selector.CompleteVerification();
        Check(selector.Passed, "selector edits still require the complete configured current build and check stages");

        var absolute = StageFixture.InRepair();
        absolute.ModelEdit("Program.cs", replacement);
        Check(absolute.IsTool("file.edit") &&
            absolute.Command.Arguments.GetProperty("arguments").GetProperty("path").GetString() == Path.Combine(absolute.Root, "Program.cs"),
            "an existing correct absolute file action remains compatible");
        var write = StageFixture.InRepair();
        write.Model(new { action = "tool", name = "file.write", arguments = new {
            path = "Program.cs", expectedSha256 = Hash(write.Files["Program.cs"]), content = replacement, collision = "replace"
        } });
        Check(write.IsTool("file.write") && write.Command.Arguments.GetProperty("arguments").GetProperty("path").GetString() == Path.Combine(write.Root, "Program.cs") &&
            write.Command.Arguments.GetProperty("arguments").GetProperty("content").GetString() == replacement &&
            write.Command.Arguments.GetProperty("arguments").GetProperty("collision").GetString() == "replace",
            "the selector bridge also preserves admitted write semantics");

        string[] rejected = ["Other.cs", "program.cs", "./Program.cs", ".\\Program.cs", "../Program.cs", "folder/../Program.cs",
            "\\Program.cs", "C:Program.cs", Path.Combine(absolute.Root, "Other.cs"),
            Path.Combine(absolute.Root + "-mangled", "Program.cs"), Path.GetFullPath(Path.Combine(absolute.Root, "..", "Program.cs"))];
        foreach (var path in rejected) Reject(StageFixture.InRepair(), path);
        var immutable = StageFixture.InRepair();
        Reject(immutable, "Checks.cs", Hash(immutable.Files["Checks.cs"]));
        Reject(StageFixture.InRepair(), "Program.cs", new string('0', 64));

        var process = StageFixture.InRepair();
        var processBefore = process.Stages.GetRawText();
        Check(!process.Command.Arguments.GetProperty("toolNames").EnumerateArray().Any(name => name.GetString() == "process.run"),
            "process.run remains absent from model tools");
        process.Model(new { action = "tool", name = "process.run", arguments = new {
            executableId = "dotnet", workingDirectory = process.Root, arguments = new[] { "build", "App.csproj", "-c", "Release" }
        } });
        Check(process.IsModel && process.Stages.GetRawText() == processBefore &&
            Diagnostic(process).Contains("staged policy", StringComparison.Ordinal), "only deterministic stages own process dispatch");
        var unavailable = new StageFixture(requireChange: false, acceptedTools: ["file.read", "process.run"]);
        unavailable.ToRepair();
        var unavailableBefore = unavailable.Stages.GetRawText();
        unavailable.Model(new { action = "tool", name = "file.edit", arguments = new {
            path = "Program.cs", expectedSha256 = Hash(unavailable.Files["Program.cs"]), oldText = unavailable.Files["Program.cs"], newText = replacement
        } });
        Check(unavailable.IsModel && unavailable.Stages.GetRawText() == unavailableBefore &&
            Diagnostic(unavailable).Contains("accepted catalog", StringComparison.Ordinal), "a selector cannot grant an unaccepted mutation tool");

        var longTargets = new StageFixture(sourceSelector: new string('x', 3900) + ".cs");
        longTargets.ToRepair();
        longTargets.ModelEdit("Other.cs", "changed", hash: new string('0', 64));
        var bounded = Diagnostic(longTargets);
        Check(Encoding.UTF8.GetByteCount(bounded) <= 4096 &&
            bounded.Contains("from repairFiles in the staged plan/status message", StringComparison.Ordinal) &&
            !bounded.Contains(new string('x', 3900), StringComparison.Ordinal),
            "an oversized target list uses bounded actionable correction instead of expanding required context");
        return checks;
    }

    public static int RunCompilerEnvelopeChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Programming compiler envelope: " + message);
            checks++;
        }
        const string changedSource = "namespace Acceptance\n{\n    public static class TextNormalizer\n    {\n        public static string Normalize(string text)\n        {\n            return text.Replace(\"\\r\\n\", \"\\n\").Replace('\\r', \"\\n\");\n        }\n    }\n}\n";
        string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        string Row(StageFixture fixture, int line = 7) =>
            $"{Path.Combine(fixture.Root, "Program.cs")}({line},61): error CS1503: Argument 2: cannot convert from 'string' to 'char' [{Path.Combine(fixture.Root, "App.csproj")}]\n";
        StageFixture Ready()
        {
            var fixture = StageFixture.InRepair();
            fixture.ModelEdit("Program.cs", changedSource); fixture.MutationResult();
            fixture.Model(new { action = "done", summary = "Request current verification" }); fixture.ReadSources();
            return fixture;
        }
        JsonElement Failure(StageFixture fixture) => fixture.Step.State.GetProperty("lastFailedBuild");
        bool HasCompilerCredit(StageFixture fixture)
        {
            var failed = Failure(fixture);
            return failed.ValueKind == JsonValueKind.Object &&
                (failed.GetProperty("compilerFailureSha256").ValueKind != JsonValueKind.Null ||
                 failed.GetProperty("diagnostic").ValueKind != JsonValueKind.Null);
        }

        var pao = Ready();
        var receipt = pao.PaoCompiler(Row(pao));
        var observed = Failure(pao);
        var diagnostic = observed.GetProperty("diagnostic");
        Check(pao.IsModel && pao.StageName == "repair" && observed.GetProperty("exitCode").GetInt32() == 1 &&
            observed.GetProperty("compilerFailureSha256").GetString() is { Length: 64 },
            "the complete err_tail-only PAO child supplies a failed compiler fingerprint using child exit1, not broker exit6");
        Check(observed.GetProperty("receiptId").GetString() == receipt && observed.GetProperty("configuration").GetString() == "Release" &&
            observed.GetProperty("editGeneration").GetInt32() == 1 && observed.GetProperty("mutationGeneration").GetInt32() == 1 &&
            diagnostic.GetProperty("receiptId").GetString() == receipt && diagnostic.GetProperty("editGeneration").GetInt32() == 1 &&
            diagnostic.GetProperty("observedSourceSha256").GetString() == Hash(changedSource),
            "the compiler observation is bound to the actual fixture receipt, current edit and proved source generation");
        Check(diagnostic.GetProperty("code").GetString() == "CS1503" && diagnostic.GetProperty("line").GetInt32() == 7 &&
            diagnostic.GetProperty("column").GetInt32() == 61 && diagnostic.GetProperty("path").GetString() == Path.Combine(pao.Root, "Program.cs") &&
            diagnostic.GetProperty("message").GetString() == "Argument 2: cannot convert from 'string' to 'char'",
            "the complete compiler row retains its exact location and message");
        Check(pao.Step.State.GetProperty("checks").GetArrayLength() == 0 &&
            pao.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null &&
            pao.Stages.GetProperty("currentCheck").ValueKind == JsonValueKind.Null,
            "diagnostic completeness grants no passing build/check credit");

        var native = Ready(); native.Process(1, Row(native), "");
        var fingerprint = Failure(native).GetProperty("compilerFailureSha256").GetString();
        Check(native.IsModel && fingerprint == observed.GetProperty("compilerFailureSha256").GetString() &&
            Failure(native).GetProperty("diagnostic").GetProperty("code").GetString() == "CS1503",
            "native parsing and the current PAO child produce the same diagnostic fingerprint");
        var equal = Ready(); equal.PaoCompiler(Row(equal), mode: "equal");
        Check(equal.IsModel && Failure(equal).GetProperty("compilerFailureSha256").GetString() == fingerprint,
            "equal string err/err_tail aliases remain one complete child stream");
        var stderrOnly = Ready(); stderrOnly.PaoCompiler("", Row(stderrOnly));
        Check(stderrOnly.IsModel && Failure(stderrOnly).GetProperty("compilerFailureSha256").GetString() == fingerprint,
            "a compiler row present only in err_tail is parsed completely");
        var legacy = Ready(); legacy.PaoCompiler(Row(legacy), mode: "legacy");
        Check(legacy.Failed && Failure(legacy).GetProperty("compilerFailureSha256").GetString() == fingerprint,
            "legacy err stays supported by the shared parser while the staged reader retains its stricter err_tail contract");

        pao.Model(new { action = "done", summary = "Request equivalent current verification" });
        Check(pao.IsModel && pao.Step.State.GetProperty("verificationCycles").GetInt32() == 1 &&
            Failure(pao).GetProperty("matchingFailures").GetInt32() == 1 &&
            pao.Step.State.GetProperty("repeatedBuildFailures").EnumerateArray().Single().GetProperty("mutationGeneration").GetInt32() == 1,
            "the staged no-progress guard stops the second unchanged PAO build while preserving its single actual compiler observation");
        pao.Model(new { action = "done", summary = "Repeat without source progress" });
        pao.Model(new { action = "done", summary = "Repeat again without source progress" });
        Check(pao.Failed && pao.Step.Next is not InvokeWorkflowCommand &&
            pao.Final.GetProperty("summary").GetString()!.Contains("unchanged source generation", StringComparison.Ordinal),
            "bounded corrections terminate unchanged PAO retries without dispatching another compiler build");

        foreach (var mode in new[] { "missing-stderr", "conflict", "tail-type", "err-type", "out-type",
            "missing-out-flag", "missing-err-flag", "missing-timeout-flag", "out-truncated", "err-truncated",
            "timeout", "outer-partial", "evidence-partial", "exit-mismatch" })
        {
            var invalid = Ready(); invalid.PaoCompiler(Row(invalid), mode: mode);
            Check(!HasCompilerCredit(invalid) && invalid.Step.State.GetProperty("checks").GetArrayLength() == 0 &&
                invalid.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null,
                "an incomplete/conflicting child has no diagnostic, compiler grouping or success credit: " + mode);
        }
        foreach (var bound in new[] { "input", "row", "count" })
        {
            var invalid = Ready();
            var text = bound switch {
                "input" => new string('x', 65537),
                "row" => Row(invalid).Replace("Argument 2: cannot convert from 'string' to 'char'", new string('x', 2049), StringComparison.Ordinal),
                _ => string.Concat(Enumerable.Range(1, 17).Select(line => Row(invalid, line)))
            };
            invalid.PaoCompiler(text);
            Check(!HasCompilerCredit(invalid), "the existing compiler " + bound + " bound still refuses complete-batch credit");
        }
        return checks;
    }


    public static int RunRepairFeedbackChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException("Repair feedback: " + message);
        }
        ContextWindow Window(StageFixture fixture) => fixture.Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
        ContextContribution Feedback(StageFixture fixture) => Window(fixture).Contributions.Last(source => source.Id.StartsWith("stage-feedback.", StringComparison.Ordinal));
        string Text(ContextContribution source) => ((TextContentPart)source.Messages.Single().Content.Single()).Text;
        StageFixture AtCheck(JsonElement? settings = null)
        {
            var fixture = new StageFixture(executionSettings: settings); fixture.ToRepair();
            fixture.Edit(); fixture.Model(new { action = "done", summary = "Ready" }); fixture.ReadSources();
            fixture.Process(0, "Build succeeded", ""); fixture.ReadSources();
            return fixture;
        }
        void AddHistory(StageFixture fixture, int characters = 4000, int count = 7) => fixture.ChangeState(node =>
        {
            var sources = node["contributions"]!.AsArray();
            for (var i = 0; i < count; i++)
                sources.Add(JsonSerializer.SerializeToNode(new ContextContribution("retained-history-" + i,
                    "pegasus.programming", "tool-result", 1, ContextRequirement.Reducible,
                    [new ModelInputMessage("user", [new TextContentPart(new string('h', characters))])], null, true), CerebralJson.Options));
            node["contextRevision"] = node["contextRevision"]!.GetValue<long>() + 1;
        });

        var failed = AtCheck(); failed.Process(1, "", "FAIL hex-codec reject\n");
        Check(failed.IsModel && failed.StageName == "repair", "a failed semantic check reaches corrective generation");
        var feedback = Feedback(failed);
        using var parsed = JsonDocument.Parse(Text(feedback));
        var body = parsed.RootElement;
        Check(feedback.Requirement == ContextRequirement.Required && feedback.Complete &&
            body.GetProperty("stderr").GetString() == "FAIL hex-codec reject\n" &&
            body.GetProperty("stdout").GetString() == "" && body.GetProperty("exitCode").GetInt32() == 1 &&
            body.GetProperty("receiptId").GetString() == feedback.ExactReadHandle,
            "the exact validated semantic diagnostic and receipt are in required model context");
        Check(failed.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null &&
            failed.Stages.GetProperty("currentCheck").ValueKind == JsonValueKind.Null &&
            failed.Step.State.GetProperty("verificationCycles").GetInt32() == 1 && !failed.Passed,
            "feedback grants no current build/check credit");
        failed.ModelEdit("Program.cs", "class Implementation { public const int Value = 2; }\n"); failed.MutationResult();
        failed.Model(new { action = "done", summary = "Corrected" }); failed.CompleteVerification();
        Check(failed.Passed && failed.Step.State.GetProperty("verificationCycles").GetInt32() == 2 &&
            failed.Final.GetProperty("currentCheck").GetProperty("buildEffectId").GetString() ==
            failed.Final.GetProperty("currentBuild").GetProperty("effectId").GetString(),
            "a later correction still needs fresh matched build/check receipts");

        var baseline = new StageFixture(runtimeBaseline: true);
        baseline.ReadSources(); baseline.Process(0, "", ""); baseline.Process(1, "", "EXPECTED FAILURE\n"); baseline.ReadSources();
        Check(baseline.IsModel && Window(baseline).Contributions.Any(source =>
            source.Id.StartsWith("stage-feedback.", StringComparison.Ordinal) && Text(source).Contains("EXPECTED FAILURE", StringComparison.Ordinal)),
            "an expected failed baseline retains its actual semantic output for the initial repair");

        foreach (var binding in new[] { "absent", "disabled", "available" })
        {
            var settings = CerebralJson.Element(new { resolvedSettingsVersion = 5,
                profile = new { automaticCompaction = binding != "disabled" },
                compactor = binding == "absent" ? null : new { profile = new { id = "fixture-compactor" }, model = new { id = "fixture-model" } },
                tools = new[] { "file.read", "file.edit", "file.write", "file.edit-range", "program.source.structure", "process.run" }.Select(name => new { name }).ToArray() });
            var large = AtCheck(settings); AddHistory(large); large.Process(1, "", "FAIL hex-codec reject\n");
            Check(large.IsModel && JsonSerializer.SerializeToUtf8Bytes(Window(large), CerebralJson.Options).Length > 24 * 1024 &&
                Text(Feedback(large)).Contains("FAIL hex-codec reject", StringComparison.Ordinal),
                binding + " optional compactor does not turn a byte hint into a required compaction effect");
            var mutations = large.Stages.GetProperty("mutationEffects").GetRawText();
            large.NeedsCompaction();
            Check(large.Step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:compact" },
                binding + " binding still honors measured host pressure");
            large.CompactionFailure();
            Check(large.Failed && large.Step.Next is not InvokeWorkflowCommand &&
                large.Final.GetProperty("summary").GetString()!.Contains("Compaction is disabled or no compactor was bound", StringComparison.Ordinal) &&
                large.Stages.GetProperty("mutationEffects").GetRawText() == mutations &&
                large.Step.State.GetProperty("checkpoint").ValueKind == JsonValueKind.Null,
                binding + " compaction failure stops truthfully without a checkpoint or replay");
        }

        var excerpt = AtCheck(); excerpt.Process(1, "", new string('x', 10000) + "\nFAIL check\n");
        Check(excerpt.IsModel && !Feedback(excerpt).Complete &&
            Text(Feedback(excerpt)).Contains("Remaining content is retained in the exact effect receipt", StringComparison.Ordinal),
            "large validated streams remain a marked bounded excerpt, never complete evidence");
        var invalid = AtCheck(); invalid.Process(1, "", "unconfirmed diagnostic", truncated: true);
        Check(invalid.Failed && !invalid.Step.State.GetProperty("contributions").EnumerateArray()
            .Any(source => source.GetProperty("id").GetString()!.StartsWith("stage-feedback.", StringComparison.Ordinal)),
            "incomplete process evidence never acquires validated staged feedback");
        var full = AtCheck(); AddHistory(full, 20000, 7); full.Process(1, "", "FAIL check\n");
        Check(full.Failed && full.Step.Next is not InvokeWorkflowCommand,
            "the existing 128 KiB state bound still stops oversized evidence");

        return checks + RunVerificationFeedbackLifetimeChecks();
    }


    private static int RunVerificationFeedbackLifetimeChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException("Verification feedback lifetime: " + message);
        }
        string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        ContextWindow Window(StageFixture fixture) => fixture.Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
        ContextContribution Feedback(StageFixture fixture) => Window(fixture).Contributions.Single(source =>
            source.Id.StartsWith("stage-feedback.", StringComparison.Ordinal));
        string Text(ContextContribution source) => ((TextContentPart)source.Messages.Single().Content.Single()).Text;
        JsonElement Status(StageFixture fixture)
        {
            var messages = fixture.Command.Arguments.GetProperty("messages").Deserialize<ModelInputMessage[]>(CerebralJson.Options)!;
            var text = ((TextContentPart)messages.Single(message => message.Role == "user").Content.Single()).Text;
            using var document = JsonDocument.Parse(text[(text.IndexOf('\n') + 1)..]);
            return document.RootElement.GetProperty("verificationFeedback").Clone();
        }
        string SourceHash(JsonElement files, string path) => files.EnumerateArray()
            .Single(file => file.GetProperty("path").GetString() == path).GetProperty("sha256").GetString()!;
        bool HasFeedback(StageFixture fixture) => fixture.Step.State.GetProperty("contributions").EnumerateArray()
            .Any(source => source.GetProperty("id").GetString()!.StartsWith("stage-feedback.", StringComparison.Ordinal));
        string Compiler(StageFixture fixture, string code, string message) =>
            $"{Path.Combine(fixture.Root, "Program.cs")}(1,61): error {code}: {message} [{Path.Combine(fixture.Root, "App.csproj")}]\n";
        StageFixture FailedBuild()
        {
            var fixture = new StageFixture(compilerBaseline: true, fourFiles: true);
            fixture.ReadSources();
            // Baseline line1 is the accepted missing-name diagnostic; the later real-shaped failure differs.
            fixture.PaoCompiler(Compiler(fixture, "CS0103", "The name 'missing' does not exist in the current context"));
            fixture.ReadSources(); fixture.Edit();
            fixture.Model(new { action = "done", summary = "Ready" }); fixture.ReadSources();
            fixture.PaoCompiler(Compiler(fixture, "CS1503", "Argument 2: cannot convert from 'string' to 'char'"));
            return fixture;
        }
        void ReadCurrent(StageFixture fixture, string path)
        {
            fixture.Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = 1 } });
            Check(fixture.IsTool("file.read"), "a newly changed file requires an actual fresh tool read");
            fixture.ReadSources();
        }
        StageFixture AtCheck()
        {
            var fixture = StageFixture.InRepair(); fixture.Edit();
            fixture.Model(new { action = "done", summary = "Ready" }); fixture.ReadSources();
            fixture.Process(0, "Build succeeded", ""); fixture.ReadSources();
            return fixture;
        }

        var failed = FailedBuild();
        var first = Feedback(failed); var observed = Status(failed);
        var program = Path.Combine(failed.Root, "Program.cs");
        var originalHash = Hash(failed.Files["Program.cs"]);
        var receipt = first.ExactReadHandle;
        Check(failed.IsModel && first.Requirement == ContextRequirement.Required && first.Complete &&
            first.Category == "stage-verification-feedback" && Text(first).Contains("CS1503", StringComparison.Ordinal),
            "the first postfailure generation retains the actual later compiler diagnostic");
        Check(!observed.GetProperty("isHistorical").GetBoolean() &&
            !observed.GetProperty("verificationCredit").GetBoolean() &&
            observed.GetProperty("observation").GetProperty("fact").GetProperty("effectId").GetString() == receipt &&
            SourceHash(observed.GetProperty("observation").GetProperty("sourceFiles"), program) == originalHash,
            "actual failure provenance binds its source hash without passing credit");
        Check(observed.GetProperty("observation").GetProperty("editGeneration").GetInt32() == 1 &&
            observed.GetProperty("observation").GetProperty("sourceMutationGeneration").GetInt32() == 1 &&
            observed.GetProperty("observation").GetProperty("fact").GetProperty("resultSha256").GetString()!.Length == 64,
            "the observation carries actual edit/mutation generations and retained-result hash");
        failed.ModelEdit("Program.cs", "class Implementation { public const int Value = 2; }\n"); failed.MutationResult();
        observed = Status(failed);
        Check(observed.GetProperty("isHistorical").GetBoolean() &&
            observed.GetProperty("classification").GetString() == "historical-failure-for-previous-source" &&
            !observed.GetProperty("observedSourceMatchesCurrentKnownSource").GetBoolean() &&
            Text(Feedback(failed)) == Text(first) && Feedback(failed).Requirement == ContextRequirement.Required,
            "a confirmed edit preserves the exact previous error as explicitly historical");
        Check(SourceHash(observed.GetProperty("observation").GetProperty("sourceFiles"), program) == originalHash &&
            SourceHash(observed.GetProperty("currentKnownSource").GetProperty("files"), program) == Hash(failed.Files["Program.cs"]) &&
            originalHash != Hash(failed.Files["Program.cs"]) &&
            observed.GetProperty("currentKnownSource").GetProperty("sourceMutationGeneration").GetInt32() == 2,
            "old and changed-source hashes/generations are both explicit");
        ReadCurrent(failed, "Program.cs");
        Check(failed.IsModel && Text(Feedback(failed)).Contains("CS1503", StringComparison.Ordinal) &&
            Feedback(failed).Requirement == ContextRequirement.Required && Status(failed).GetProperty("isHistorical").GetBoolean(),
            "an intervening actual file.read and next generation keep the historical error required");
        failed.ModelEdit("Helper.cs", "class Helper { public const int Value = 2; }\n"); failed.MutationResult();
        ReadCurrent(failed, "Helper.cs");
        observed = Status(failed);
        Check(Feedback(failed).ExactReadHandle == receipt &&
            observed.GetProperty("observation").GetProperty("editGeneration").GetInt32() == 1 &&
            observed.GetProperty("currentKnownSource").GetProperty("editGeneration").GetInt32() == 3 &&
            observed.GetProperty("currentKnownSource").GetProperty("sourceMutationGeneration").GetInt32() == 3 &&
            !observed.GetProperty("verificationCredit").GetBoolean(),
            "another file edit/read/generation cannot relabel the old error as a fresh result");
        Check(failed.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null &&
            failed.Stages.GetProperty("currentCheck").ValueKind == JsonValueKind.Null &&
            failed.Step.State.GetProperty("verificationCycles").GetInt32() == 1 && !failed.Passed &&
            failed.Step.State.GetProperty("lastFailedBuild").GetProperty("diagnostic").ValueKind == JsonValueKind.Null,
            "retained display feedback does not resurrect checks or stale structural diagnostic admission");
        var generation = failed.Step.State.GetProperty("sourceMutationGeneration").GetInt32();
        failed.Compact();
        Check(failed.IsModel && Feedback(failed).Id == first.Id && Text(Feedback(failed)) == Text(first) &&
            Feedback(failed).Requirement == ContextRequirement.Required && Status(failed).GetProperty("isHistorical").GetBoolean() &&
            failed.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == generation,
            "compaction preserves the one exact required diagnostic and adds no source progress");
        failed.Model(new { action = "done", summary = "Ready for another verification" }); failed.ReadSources();
        failed.PaoCompiler(Compiler(failed, "CS1002", "; expected"));
        observed = Status(failed);
        Check(failed.IsModel && Feedback(failed).Id != first.Id && Text(Feedback(failed)).Contains("CS1002", StringComparison.Ordinal) &&
            !Text(Feedback(failed)).Contains("CS1503", StringComparison.Ordinal) &&
            !Window(failed).Contributions.Any(source => source.Id == first.Id) &&
            !observed.GetProperty("isHistorical").GetBoolean(),
            "a validated later failure replaces the previous body instead of accumulating required diagnostics");
        Check(SourceHash(observed.GetProperty("observation").GetProperty("sourceFiles"), program) == Hash(failed.Files["Program.cs"]) &&
            observed.GetProperty("observation").GetProperty("sourceMutationGeneration").GetInt32() == generation &&
            failed.Step.State.GetProperty("verificationCycles").GetInt32() == 2,
            "supersession uses the new verification source snapshot and the existing cycle budget");
        failed.ModelEdit("Program.cs", "class Implementation { public const int Value = 3; }\n"); failed.MutationResult();
        failed.Model(new { action = "done", summary = "Corrected" }); failed.ReadSources();
        failed.Process(0, "Build succeeded", ""); failed.ReadSources();
        Check(failed.Stages.GetProperty("latestVerification").ValueKind == JsonValueKind.Null && !HasFeedback(failed) &&
            failed.Stages.GetProperty("currentCheck").ValueKind == JsonValueKind.Null && !failed.Passed && failed.IsTool("process.run"),
            "a validated successful build retires the old diagnostic without passing the outstanding check");
        failed.Process(0, "PASS\n", ""); failed.ReadSources();
        Check(failed.Passed && failed.Stages.GetProperty("latestVerification").ValueKind == JsonValueKind.Null && !HasFeedback(failed) &&
            failed.Step.State.GetProperty("verificationCycles").GetInt32() == 3 &&
            failed.Final.GetProperty("currentCheck").GetProperty("buildEffectId").GetString() ==
            failed.Final.GetProperty("currentBuild").GetProperty("effectId").GetString(),
            "successful completion still requires new matched build/check receipts after the last edit");

        foreach (var exit in new[] { 1, 0 })
        {
            var semantic = AtCheck();
            var diagnostic = exit == 1 ? "FAIL semantic detail\n" : "WRONG OUTPUT\n";
            semantic.Process(exit, exit == 0 ? diagnostic : "", exit == 1 ? diagnostic : "");
            var prior = Feedback(semantic); var status = Status(semantic);
            Check(semantic.IsModel && Text(prior).Contains(diagnostic.TrimEnd(), StringComparison.Ordinal) &&
                !status.GetProperty("observation").GetProperty("matchedExpectation").GetBoolean() &&
                status.GetProperty("observation").GetProperty("fact").GetProperty("exitCode").GetInt32() == exit,
                "semantic/check-output mismatch has actual bounded feedback even at exit zero: " + exit);
            semantic.ModelEdit("Program.cs", "class Implementation { public const int Value = 2; }\n"); semantic.MutationResult();
            ReadCurrent(semantic, "Program.cs");
            Check(Text(Feedback(semantic)) == Text(prior) && Feedback(semantic).Requirement == ContextRequirement.Required &&
                Status(semantic).GetProperty("isHistorical").GetBoolean() && !semantic.Passed,
                "file edits and intervening generation retain historical semantic/check feedback: " + exit);
            semantic.Model(new { action = "done", summary = "Corrected" }); semantic.CompleteVerification();
            Check(semantic.Passed && !HasFeedback(semantic) && semantic.Stages.GetProperty("latestVerification").ValueKind == JsonValueKind.Null,
                "later actual passing build/check results supersede semantic failure: " + exit);
        }

        var bounded = AtCheck(); bounded.Process(1, "", "FAIL bounded detail\n" + new string('x', 10000));
        var excerpt = Feedback(bounded);
        Check(!excerpt.Complete && !Status(bounded).GetProperty("observation").GetProperty("complete").GetBoolean() &&
            JsonSerializer.SerializeToUtf8Bytes(Text(excerpt), CerebralJson.Options).Length <= 8 * 1024 &&
            Text(excerpt).Contains("Remaining content is retained in the exact effect receipt", StringComparison.Ordinal) &&
            excerpt.ExactReadHandle == bounded.Stages.GetProperty("latestVerification").GetProperty("fact").GetProperty("effectId").GetString(),
            "payload bounds preserve a marked excerpt and exact receipt locator without upgrading completeness");
        bounded.ModelEdit("Program.cs", "class Implementation { public const int Value = 2; }\n"); bounded.MutationResult();
        ReadCurrent(bounded, "Program.cs");
        Check(Feedback(bounded).Id == excerpt.Id && Text(Feedback(bounded)) == Text(excerpt) &&
            Feedback(bounded).Requirement == ContextRequirement.Required && Status(bounded).GetProperty("isHistorical").GetBoolean(),
            "a bounded partial display stays exact and historical across edit/read/generation");
        foreach (var tamper in new[] { "body", "requirement", "source" })
        {
            var invalid = FailedBuild();
            invalid.ChangeState(node => {
                var latest = node["stages"]!["latestVerification"]!;
                if (tamper == "source") latest["sources"]![0]!["sha256"] = new string('a', 64);
                else {
                    var item = node["contributions"]!.AsArray().Single(source =>
                        source!["id"]!.GetValue<string>() == latest["contributionId"]!.GetValue<string>())!;
                    if (tamper == "body") item["messages"]![0]!["content"]![0]!["text"] = "replaced body";
                    else item["requirement"] = "reducible";
                }
            });
            invalid.Model(new { action = "done", summary = "Must reject damaged observation" });
            Check(invalid.Failed && invalid.Step.Next is not InvokeWorkflowCommand,
                "damaged retained observation fails closed before another dispatch: " + tamper);
        }
        return checks;
    }

    public static int RunReadReuseChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException("Staged read reuse: " + message);
        }
        JsonElement Window(StageFixture f) => f.Command.Arguments.GetProperty("context");
        JsonElement[] Sources(StageFixture f) => f.Step.State.GetProperty("readProjectionReferences").EnumerateArray().ToArray();
        string Body(StageFixture f, string id) => f.Step.State.GetProperty("contributions").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == id).GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        StageFixture Ready() { var f = new StageFixture(fourFiles: true); f.ToRepair(); return f; }
        JsonElement Ref(StageFixture f, string file) => Sources(f).Last(r => r.GetProperty("source").GetProperty("path").GetString() == Path.Combine(f.Root, file));
        void Read(StageFixture f, string file, int count = 100, int? start = null)
        {
            var args = new Dictionary<string, object> { ["path"] = file, ["lineCount"] = count };
            if (start is not null) args["startLine"] = start.Value;
            f.Model(new { action = "tool", name = "file.read", arguments = args });
        }

        foreach (var count in new[] { 100, 200, 1000 })
        {
            var f = Ready(); var original = Ref(f, "Helper.cs"); var id = original.GetProperty("contributionId").GetString()!;
            var exact = Body(f, id); var readCount = f.Step.State.GetProperty("readReceipts").GetArrayLength();
            f.ModelEdit("Program.cs", "class Implementation { public const int Value = 7; }\n");
            Check(f.IsTool("file.edit") && Sources(f).All(r => r.GetProperty("source").GetProperty("path").GetString() != Path.Combine(f.Root, "Program.cs")) &&
                Sources(f).Any(r => r.GetProperty("contributionId").GetString() == id),
                count + ": retire target before dispatch, retain only unrelated current path/hash bodies");
            f.MutationResult(); Read(f, "Helper.cs", count, count == 200 ? 1 : null);
            Check(f.IsModel && f.Step.State.GetProperty("workingSource").GetProperty("contributionId").GetString() == id &&
                Body(f, id) == exact && f.Step.State.GetProperty("readReceipts").GetArrayLength() == readCount &&
                f.Step.State.GetProperty("readObservations").GetInt32() == readCount &&
                f.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 1 &&
                f.Step.State.GetProperty("retainedReadSelections").GetInt32() == 1 &&
                f.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null,
                count + ": equivalent whole-file request selects the original body without read/freshness/check credit");
        }

        var target = Ready(); target.Edit(); Read(target, "Program.cs");
        Check(target.IsTool("file.read"), "a changed target requires a real source refresh");
        target.ReadSources();
        Check(target.IsModel && target.Step.State.GetProperty("workingSource").GetProperty("sha256").GetString() ==
            target.Stages.GetProperty("currentInputs").EnumerateArray().Single(f => f.GetProperty("path").GetString() == Path.Combine(target.Root, "Program.cs")).GetProperty("sha256").GetString(),
            "the real read, not the edit proposal, establishes the changed target body");

        foreach (var outcome in new[] { "failed", "unknown" })
        {
            var f = Ready(); f.ModelEdit("Program.cs", "changed");
            Check(Sources(f).All(r => r.GetProperty("source").GetProperty("path").GetString() != Path.Combine(f.Root, "Program.cs")),
                outcome + ": target visibility retires before outcome");
            if (outcome == "unknown") f.Unknown(); else f.MutationResult(success: false);
            Check(f.Failed && f.Step.Next is not InvokeWorkflowCommand &&
                f.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null,
                outcome + ": an unconfirmed mutation cannot continue on cached bodies or replay");
        }

        var stale = Ready(); stale.ModelEdit("Program.cs", "changed", new string('f', 64));
        Check(stale.IsModel && stale.Stages.GetProperty("epoch").GetInt32() == 0 &&
            stale.Stages.GetProperty("mutationEffects").GetArrayLength() == 0, "stale expected SHA refuses dispatch without invalidating or granting progress");
        stale.Edit(); Read(stale, "Program.cs"); Check(stale.IsTool("file.read"), "a later valid mutation still requires a real target refresh");
        stale.ReadSources(); Check(stale.IsModel, "fresh source is accepted after valid changed-byte evidence");

        foreach (var loss in new[] { "body", "mapping", "incomplete", "compaction" })
        {
            var f = Ready();
            if (loss == "compaction") f.Compact();
            else f.ChangeState(node =>
            {
                if (loss == "mapping") node["readProjectionReferences"] = new JsonArray();
                else foreach (var source in node["contributions"]!.AsArray().Where(s => s!["exactReadHandle"] is not null).ToArray())
                {
                    if (loss == "body") node["contributions"]!.AsArray().Remove(source);
                    else source!["complete"] = false;
                }
                node["workingSource"] = null;
            });
            Read(f, "Program.cs", 100);
            Check(f.IsTool("file.read"), loss + ": missing or incomplete exact bodies permit legitimate reacquisition");
        }
        var version = Ready(); version.ChangeState(node =>
        {
            foreach (var source in node["readProjectionReferences"]!.AsArray()) source!.AsObject().Remove("source");
            node["workingSource"] = null;
        });
        Read(version, "Program.cs", 200); Check(version.IsTool("file.read"), "legacy source mappings cannot establish whole-file coverage");
        version.ReadSources(); Check(version.IsModel, "a real later read restores a valid mapping");

        var externalDrift = Ready();
        var cachedText = externalDrift.Files["Program.cs"];
        var cachedHash = externalDrift.Stages.GetProperty("currentInputs").EnumerateArray().Single(file =>
            file.GetProperty("path").GetString() == Path.Combine(externalDrift.Root, "Program.cs")).GetProperty("sha256").GetString();
        externalDrift.Files["Program.cs"] += "// external drift\n";
        externalDrift.Model(new { action = "tool", name = "file.edit", arguments = new { path = "Program.cs", oldText = cachedText,
            newText = "must not land", expectedSha256 = cachedHash } });
        Check(externalDrift.IsTool("file.edit") && Sources(externalDrift).All(reference =>
            reference.GetProperty("source").GetProperty("path").GetString() != Path.Combine(externalDrift.Root, "Program.cs")),
            "previously valid cached SHA reaches the tool with target mappings already retired");
        externalDrift.RejectPreimageConflict();
        Check(externalDrift.Failed && externalDrift.Step.Next is not InvokeWorkflowCommand &&
            externalDrift.Stages.GetProperty("mutationEffects").GetArrayLength() == 0 &&
            externalDrift.Step.State.GetProperty("sourceMutationGeneration").GetInt32() == 0 &&
            externalDrift.Stages.GetProperty("currentInputs").EnumerateArray().Single(file =>
                file.GetProperty("path").GetString() == Path.Combine(externalDrift.Root, "Program.cs")).GetProperty("sha256").GetString() == cachedHash,
            "staged external drift conflict terminates without adopting external bytes or replaying a mutation");

        var invalidRange = Ready();
        invalidRange.Model(new { action = "tool", name = "file.read", arguments = new { path = "Program.cs", lineCount = 0 } });
        Check(invalidRange.Step.State.GetProperty("retainedReadSelections").GetInt32() == 0,
            "invalid ranges never acquire cached-source selection");
        var extended = Ready();
        extended.Model(new { action = "tool", name = "file.read", arguments = new { path = "Program.cs", lineCount = 100, extra = true } });
        Check(extended.Step.State.GetProperty("retainedReadSelections").GetInt32() == 0,
            "unknown argument shapes are never normalized into cached selections");

        var bounded = Ready(); Read(bounded, "Helper.cs", 100); Read(bounded, "Helper.cs", 200); Read(bounded, "Helper.cs", 1000);
        Check(bounded.Failed && bounded.Step.Next is not InvokeWorkflowCommand, "retained-source selection has a bounded no-progress allowance");
        var diagnostic = Ready(); diagnostic.Edit(); Read(diagnostic, "Helper.cs", 100); Read(diagnostic, "Helper.cs", 200); Read(diagnostic, "Helper.cs", 1000);
        Check(diagnostic.IsTool("file.read") && diagnostic.StageName == "build-inputs" &&
            diagnostic.Step.State.GetProperty("verificationCycles").GetInt32() == 1,
            "bounded duplicate requests after source progress enter fresh policy verification without acceptance");

        var drift = Ready(); drift.Edit(); drift.Model(new { action = "done", summary = "Ready" });
        drift.Files["Checks.cs"] += "// external drift\n"; drift.ReadSources();
        Check(drift.Failed, "retained immutable bodies never replace fresh whole-input drift detection");

        var budget = new StageFixture(runtimeBaseline: true, fourFiles: true, stepLimit: 48); budget.ToRepair();
        budget.ModelEdit("Program.cs", "class Implementation { public const int Value = 1; }\n"); budget.MutationResult();
        Read(budget, "Helper.cs", 100);
        budget.ModelEdit("Helper.cs", "class Helper { public const int Value = 1; }\n"); budget.MutationResult();
        Read(budget, "Helper.cs", 100); Check(budget.IsTool("file.read"), "budget fixture preserves a genuine post-mutation source read"); budget.ReadSources();
        var mutations = budget.Stages.GetProperty("mutationEffects").GetArrayLength();
        budget.ModelEdit("Helper.cs", budget.Files["Helper.cs"]);
        Check(budget.IsTool("file.read") && budget.StageName == "build-inputs" &&
            budget.Stages.GetProperty("mutationEffects").GetArrayLength() == mutations &&
            budget.Step.State.GetProperty("repairs").GetInt32() == 0, "a no-op requests diagnostics once without mutation, correction spend or completion");
        budget.ReadSources(); budget.Process(0, "Build succeeded", ""); budget.ReadSources(); budget.Process(1, "", "FAIL fixture reject\n");
        Check(budget.IsModel && !budget.Passed && budget.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null &&
            Window(budget).GetRawText().Contains("FAIL fixture reject", StringComparison.Ordinal),
            "a real-shaped failed check returns repair feedback and clears current proof");
        budget.ModelEdit("Program.cs", "class Implementation { public const int Value = 2; }\n"); budget.MutationResult();
        budget.Model(new { action = "done", summary = "Ready" }); budget.CompleteVerification();
        Check(budget.Passed && budget.EffectsCompleted == 45 && budget.EffectsCompleted < 48 &&
            budget.ProofReadCount == 28 && budget.Step.State.GetProperty("verificationCycles").GetInt32() == 2 &&
            budget.Final.GetProperty("currentCheck").GetProperty("buildEffectId").GetString() ==
            budget.Final.GetProperty("currentBuild").GetProperty("effectId").GetString(),
            "four-file baseline, failed verification, corrective edit and fresh final verification finish in45 effects with all28 proof reads");
        Console.WriteLine(JsonSerializer.Serialize(new { evidenceTier = "deterministic unit fixture", effectLimit = 48,
            effects = budget.EffectsCompleted, proofReads = budget.ProofReadCount, verificationCycles = 2, actualModelOrProcessCalls = 0 }));
        return checks;
    }

    private sealed class StageFixture
    {
        public readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-stage-policy-fixture"));
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal) {
            ["Program.cs"] = "class Implementation { }\n", ["Checks.cs"] = "class Checks { }\n",
            ["App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n"
        };
        private readonly WorkflowSubmission submission;
        public WorkflowSubmission RuntimeSubmission => submission;
        private int sequence;
        public int EffectsCompleted => sequence;
        public int ProofReadCount { get; private set; }
        public WorkflowStep Step { get; private set; }
        public IReadOnlyList<WorkflowSteeringEntry> Steering { get; set; } = [];
        public int LaunchFailures { get; private set; }
        public WorkflowEffectState? LastProcessState { get; private set; }
        public JsonElement Stages => Step.State.GetProperty("stages");
        public string StageName => Stages.GetProperty("step").GetString()!;
        public bool Passed => Step.Next is CompleteWorkflow { Success: true };
        public bool Failed => Step.Next is CompleteWorkflow { Success: false };
        public bool IsModel => Step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:generate" };
        public InvokeWorkflowCommand Command => (InvokeWorkflowCommand)Step.Next;
        public JsonElement Final => ((CompleteWorkflow)Step.Next).Result;
        public string[] Argv => Command.Arguments.GetProperty("arguments").GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToArray();
        public bool IsTool(string name) => Step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:tool" } cmd &&
            cmd.Arguments.GetProperty("name").GetString() == name;

        public StageFixture(bool compilerBaseline = false, bool runtimeBaseline = false, bool requireChange = true,
            string? extraBuildArgument = null, string assembly = "bin/Release/net10.0/App.dll",
            string[]? acceptedTools = null, JsonElement? executionSettings = null, string sourceSelector = "Program.cs", bool fourFiles = false, int stepLimit = 512, bool? verifyAfterEdit = null)
        {
            if (fourFiles) Files["Helper.cs"] = "class Helper\n{\n}\n";
            if (sourceSelector != "Program.cs") { Files[sourceSelector] = Files["Program.cs"]; Files.Remove("Program.cs"); }
            object expectedBuild = compilerBaseline
                ? new { exitCode = 1, compilerDiagnostic = new { code = "CS0103", identifier = "missing", path = sourceSelector, line = 1 } }
                : new { exitCode = 0 };
            var baseline = new Dictionary<string, object> { ["build"] = expectedBuild };
            if (runtimeBaseline) baseline["check"] = new { exitCode = 1, stdout = "", stderr = "EXPECTED FAILURE\n" };
            var argv = new List<string> { "build", "App.csproj", "-c", "Release", "--nologo", "-v", "minimal", "--disable-build-servers", "-m:1" };
            if (extraBuildArgument is not null) argv.Add(extraBuildArgument);
            var input = CerebralJson.Element(new {
                rootPath = Root, projectFile = "App.csproj", configuration = "Release",
                requiredInputs = Files.Select(file => new { path = file.Key, sha256 = Hash(file.Value), mutable = file.Key == sourceSelector || fourFiles && file.Key == "Helper.cs" }).ToArray(),
                build = new { arguments = argv, timeoutSeconds = 120 }, baseline,
                check = new { assemblyPath = assembly, timeoutSeconds = 30, stdout = "PASS\n", stderr = "" }, requireSourceChange = requireChange
            });
            if (verifyAfterEdit.HasValue)
            {
                var node = JsonNode.Parse(input.GetRawText())!.AsObject();
                node["verifyAfterEdit"] = verifyAfterEdit.Value;
                input = CerebralJson.Element(node);
            }
            var settings = executionSettings ?? CerebralJson.Element(new { resolvedSettingsVersion = 5,
                tools = (acceptedTools ?? new[] { "file.read", "file.edit", "file.write", "file.edit-range", "program.source.structure", "process.run" })
                    .Select(name => new { name }).ToArray() });
            submission = new("staged-fixture", "conversation", "coding", ProgrammingAgentHarness.StagedId,
                ProgrammingAgentHarness.StagedVersion, "Inspect and repair the declared source.", null, input, settings, StepLimit: stepLimit)
                { FailureLimits = new Dictionary<string, int> { ["cerebral.host:launch"] = 2 } };
            Step = Harness().Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running }));
        }
        private static IAgentHarness Harness()
        {
            using var plugin = new ProgrammingServicePlugin();
            return plugin.Harnesses.Single(item => item.Descriptor.Id == ProgrammingAgentHarness.StagedId);
        }
        public static StageFixture InRepair() { var fixture = new StageFixture(); fixture.ToRepair(); return fixture; }
        public void ToRepair() { ReadSources(); Process(0, "", ""); if (StageName == "baseline-check") Process(1, "", "EXPECTED FAILURE\n"); ReadSources(); }
        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        public void ReadSources(bool truncated = false, string? hashOverride = null, int totalDelta = 0, string? pathOverride = null)
        {
            for (var bound = 0; bound < 8 && IsTool("file.read"); bound++)
            {
                var path = Command.Arguments.GetProperty("arguments").GetProperty("path").GetString()!;
                var content = Files[Path.GetFileName(path)];
                var args = Command.Arguments.GetProperty("arguments");
                var start = args.TryGetProperty("startLine", out var first) ? first.GetInt32() : 1;
                var count = args.TryGetProperty("lineCount", out var length) ? length.GetInt32() : 200;
                var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                ReplyTool(true, JsonSerializer.Serialize(new { path = pathOverride ?? path, sha256 = hashOverride ?? Hash(content),
                    startLine = start, totalLines = lines.Length + totalDelta,
                    content = string.Join('\n', lines.Skip(start - 1).Take(count)) }, CerebralJson.Options), truncated: truncated);
            }
        }
        public void Process(int exit, string stdout, string stderr, bool truncated = false, int? bodyExit = null, string providerId = "cerebral.process")
        {
            if (!IsTool("process.run")) throw new InvalidOperationException("Fixture expected a process effect.");
            var expected = Command.Arguments.GetProperty("arguments").TryGetProperty("expectedExitCode", out var value) ? value.GetInt32() : 0;
            var accepted = exit == expected;
            LastProcessState = accepted ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed;
            // This remains a pure policy simulation. The separate integration fixture uses Worker itself.
            if (Argv[0] != "build" && !accepted) LaunchFailures++;
            ReplyTool(accepted, JsonSerializer.Serialize(new { exitCode = bodyExit ?? exit, stdout, stderr }, CerebralJson.Options),
                new(accepted ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed, providerId,
                    "stage-effect-" + (sequence + 1), State: "completed", Partial: false, ExitCode: exit), truncated);
        }

        public void ChangePendingExpectation(JsonNode? expected, bool remove = false)
        {
            if (!IsTool("process.run")) throw new InvalidOperationException("No pending process fixture.");
            var command = Command;
            var envelope = JsonNode.Parse(command.Arguments.GetRawText())!.AsObject();
            var arguments = envelope["arguments"]!.AsObject();
            if (remove) arguments.Remove("expectedExitCode"); else arguments["expectedExitCode"] = expected?.DeepClone();
            ChangeState(state => state["pendingArguments"] = arguments.DeepClone());
            Step = Step with { Next = command with { Arguments = CerebralJson.Element(envelope) } };
        }
        public void ProcessReceipt(int exit, WorkflowEffectState state, ToolExecutionOutcome outcome,
            int? bodyExit = null, bool partial = false, bool truncated = false, bool followUp = false,
            string? request = null, string provider = "cerebral.process",
            string stdout = "", string stderr = "EXPECTED FAILURE\n")
        {
            if (!IsTool("process.run")) throw new InvalidOperationException("No pending process fixture.");
            LastProcessState = state;
            Reply(state, CerebralJson.Element(new { name = "process.run",
                content = JsonSerializer.Serialize(new { exitCode = bodyExit ?? exit, stdout, stderr }, CerebralJson.Options),
                truncated, followUpRequired = followUp, evidence = new ToolExecutionEvidence(outcome, provider,
                    request ?? "stage-effect-" + (sequence + 1), State: "completed", Partial: partial, ExitCode: exit) }));
        }
        public void PaoProcess(int exit, string? fault = null)
        {
            var id = "stage-effect-" + (sequence + 1);
            var job = new string('a', 32);
            var raw = JsonSerializer.Serialize(new {
                ok = exit == 0, op = "dotnet_build", rid = id, jid = fault == "bodyJob" ? new string('b', 32) : job,
                state = "completed", data = new {
                    exit, @out = "Build result", err_tail = "", out_truncated = false, err_truncated = false,
                    timed_out = fault == "timeout"
                }
            }, CerebralJson.Options);
            ReplyTool(exit == 0, raw, new(exit == 0 ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
                "mcp.fixture-pao", fault == "request" ? "foreign-effect" : id, job, "completed", fault == "partial", exit),
                providerReceiptId: fault == "providerReceipt" ? new string('b', 32) : job);
        }
        public string PaoCompiler(string stdout, string stderr = "", string mode = "tail")
        {
            if (!IsTool("process.run") || Argv[0] != "build") throw new InvalidOperationException("Fixture expected a build effect.");
            var id = "stage-effect-" + (sequence + 1);
            var job = new string('b', 32);
            var child = new Dictionary<string, object?> {
                ["exit"] = mode == "exit-mismatch" ? 2 : 1, ["tool"] = "dotnet", ["out"] = stdout, ["err_tail"] = stderr,
                ["out_truncated"] = mode == "out-truncated", ["err_truncated"] = mode == "err-truncated", ["timed_out"] = mode == "timeout"
            };
            switch (mode)
            {
                case "legacy": child.Remove("err_tail"); child["err"] = stderr; break;
                case "equal": child["err"] = stderr; break;
                case "missing-stderr": child.Remove("err_tail"); break;
                case "conflict": child["err"] = "different child stream"; break;
                case "tail-type": child["err_tail"] = 12; child["err"] = stderr; break;
                case "err-type": child["err"] = 12; break;
                case "out-type": child["out"] = 12; break;
                case "missing-out-flag": child.Remove("out_truncated"); break;
                case "missing-err-flag": child.Remove("err_truncated"); break;
                case "missing-timeout-flag": child.Remove("timed_out"); break;
            }
            var raw = JsonSerializer.Serialize(new {
                ok = false, op = "dotnet_build", rid = id, jid = job, state = "failed", exitCode = 6,
                err = "Broker status only; never compiler stderr.", partial = mode == "outer-partial", data = child
            }, CerebralJson.Options);
            ReplyTool(false, raw, new(ToolExecutionOutcome.Failed, "mcp.fixture-pao", id, job, "failed", mode == "evidence-partial", 1),
                providerReceiptId: job);
            return id;
        }
        public void Model(object action)
        {
            var window = Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
            Reply(WorkflowEffectState.Succeeded, CerebralJson.Element(new {
                generation = new ModelGenerationResult(ModelGenerationFinish.Stop, JsonSerializer.Serialize(action, CerebralJson.Options)),
                outputOmitted = false, context = Projection(window, ContextProjectionStatus.Ready)
            }));
        }
        public void ModelEdit(string path, string text, string? hash = null) => Model(new {
            action = "tool", name = "file.edit", arguments = new {
                path = Path.Combine(Root, path), oldText = Files.GetValueOrDefault(path) ?? "old", newText = text,
                expectedSha256 = hash ?? Hash(Files[path])
            }
        });
        public void Edit() { ModelEdit("Program.cs", "class Implementation { public const int Value = 1; }\n"); MutationResult(); }
        public void MutationResult(bool skipped = false, bool success = true)
        {
            var args = Command.Arguments.GetProperty("arguments");
            var path = args.GetProperty("path").GetString()!;
            var text = args.GetProperty("newText").GetString()!;
            if (success && !skipped) Files[Path.GetFileName(path)] = text;
            var current = Files[Path.GetFileName(path)];
            ReplyTool(success, JsonSerializer.Serialize(new { path, sha256 = Hash(current), bytes = Encoding.UTF8.GetByteCount(current), skipped }, CerebralJson.Options),
                new(success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed, "fixture.mutation",
                    "stage-effect-" + (sequence + 1), Partial: false));
        }
        public void CompleteVerification()
        {
            ReadSources(); Process(0, "Build succeeded", ""); ReadSources(); Process(0, "PASS\n", ""); ReadSources();
        }
        public void RejectPreimageConflict() => ReplyTool(false, "{\"error\":\"preimage_changed\"}",
            new(ToolExecutionOutcome.Rejected, "fixture.mutation", "stage-effect-" + (sequence + 1), Partial: false));
        public void Unknown() => Reply(WorkflowEffectState.Unknown, WorkflowContract.EmptyObject);
        public void WrongEffect() => Reply(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject, commandId: "cerebral.host:generate");
        public void Cancel() => Step = Harness().Advance(new(new WorkflowRun {
            Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Cancelling, CancelRequested = true,
            State = Step.State, Steering = Steering
        }));
        public void ChangeState(Action<JsonObject> change)
        {
            var node = JsonNode.Parse(Step.State.GetRawText())!.AsObject(); change(node);
            Step = Step with { State = JsonSerializer.SerializeToElement(node, CerebralJson.Options) };
        }
        private static PreparedModelMetadata Metadata(int tokens) =>
            new("fixture-instance", "fixture-adapter", "1.0.0", "fixture-model", tokens, 8192, 256, "pure-policy") { SafetyReserveTokens = 128 };
        private static ContextProjection Projection(ContextWindow window, ContextProjectionStatus status) =>
            new(status, window.Revision, window.ExclusionRevision,
                window.Contributions.Select(source => new ContextSourceReference(source.Id, source.SourceRevision)).ToArray(), [], Metadata(128));
        public void NeedsCompaction()
        {
            var window = Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
            Reply(WorkflowEffectState.Failed, CerebralJson.Element(new { context = Projection(window, ContextProjectionStatus.NeedsCompaction) }));
        }
        public void CompactionFailure() => Reply(WorkflowEffectState.Failed, WorkflowContract.EmptyObject,
            error: "Compaction is disabled or no compactor was bound when this workflow was accepted.");
        public void Compact()
        {
            var before = Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
            Reply(WorkflowEffectState.Failed, CerebralJson.Element(new { context = Projection(before, ContextProjectionStatus.NeedsCompaction) }));
            var window = Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
            var checkpoint = new ContextCheckpoint("fixture-checkpoint", window.Revision, window.ExclusionRevision,
                ContextValidation.GetCoveredManifest(window), "Declared source history was summarized.", "stage-effect-" + (sequence + 1))
                { SourceLocators = ContextValidation.GetCoveredLocators(window) };
            Reply(WorkflowEffectState.Succeeded, CerebralJson.Element(new { checkpoint, before = Metadata(4000), after = Metadata(128), passes = 1, usage = Array.Empty<object>() }));
        }
        private void ReplyTool(bool success, string content, ToolExecutionEvidence? evidence = null, bool truncated = false, string? providerReceiptId = null)
        {
            if (IsTool("file.read") && Stages.GetProperty("pending").ValueKind == JsonValueKind.String) ProofReadCount++;
            Reply(success ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed,
                CerebralJson.Element(new { name = Command.Arguments.GetProperty("name").GetString(), content, truncated, evidence, followUpRequired = false }), providerReceiptId: providerReceiptId);
        }
        private void Reply(WorkflowEffectState state, JsonElement data, string? commandId = null, string? providerReceiptId = null, string? error = null)
        {
            if (sequence + 1 >= submission.StepLimit) throw new InvalidOperationException("Fixture exhausted its accepted effect budget before the next transition.");
            var command = Command;
            var effect = new WorkflowEffect("stage-effect-" + ++sequence, commandId ?? command.CommandId, command.CommandVersion,
                command.Arguments, WorkflowReplayPolicy.Never, state, data, error ?? (state == WorkflowEffectState.Failed ? "Fixture failure" : null), providerReceiptId);
            // A new instance on every step exercises JSON continuation rather than retained plugin fields.
            Step = Harness().Advance(new(new WorkflowRun {
                Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running, State = Step.State,
                Signal = new(WorkflowSignalKind.EffectResult, data), Effect = effect, Steering = Steering,
                FailedEffects = new Dictionary<string, int> { ["cerebral.host:launch"] = LaunchFailures }
            }));
        }
    }
}
