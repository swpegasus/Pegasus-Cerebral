using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingStagedWorkflowPolicyTests
{
    /// <summary>Deterministic policy transitions only; no model, process or filesystem tool executes.</summary>
    public static int RunVerificationCadenceChecks()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Verification cadence: " + message);
            checks++;
        }
        var compact = new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false };
        string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        int Number(StageFixture fixture, string field) => fixture.Step.State.GetProperty(field).GetInt32();
        ContextContribution Feedback(StageFixture fixture) => fixture.Step.State.GetProperty("contributions")
            .Deserialize<ContextContribution[]>(CerebralJson.Options)!.Single(source => source.Category == "stage-verification-feedback");
        StageFixture Ready(bool? cadence = null, bool fourFiles = false, bool runtimeBaseline = false, int limit = 512)
        {
            var fixture = new StageFixture(verifyAfterEdit: cadence, fourFiles: fourFiles, runtimeBaseline: runtimeBaseline, stepLimit: limit);
            fixture.ToRepair();
            return fixture;
        }
        StageFixture FailedFinal(bool cadence, string kind)
        {
            var fixture = Ready(cadence);
            fixture.Edit();
            if (!cadence) fixture.Model(new { action = "done", summary = "Verify the changed source" });
            fixture.ReadSources();
            if (kind == "build") fixture.Process(1, "actual compiler failure", "");
            else
            {
                fixture.Process(0, "", ""); fixture.ReadSources();
                fixture.Process(kind == "check-zero" ? 0 : 1, kind == "check-zero" ? "WRONG OUTPUT\n" : "", "FAIL actual check\n");
            }
            return fixture;
        }
        WorkflowStep StartChangedInput(StageFixture fixture, Action<JsonObject> change)
        {
            var node = JsonNode.Parse(fixture.RuntimeSubmission.Input.GetRawText())!.AsObject();
            change(node);
            var submission = fixture.RuntimeSubmission with { Input = CerebralJson.Element(node) };
            using var plugin = new ProgrammingServicePlugin();
            return plugin.Harnesses.Single(h => h.Descriptor.Id == ProgrammingAgentHarness.StagedId)
                .Advance(new(new WorkflowRun { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running }));
        }

        // Reconstruct the pre-option record's exact serialization shape, including null defaults.
        // An omitted/false optional flag must preserve already-admitted continuation hashes.
        var omitted = Ready();
        var disabled = Ready(false);
        var enabled = Ready(true);
        var oldShape = new {
            RootPath = omitted.Root, ProjectFile = "App.csproj", Configuration = "Release",
            RequiredInputs = omitted.Files.Select(file => new {
                Path = file.Key, Sha256 = Hash(file.Value), Mutable = file.Key == "Program.cs"
            }).ToArray(),
            Build = new {
                Arguments = new[] { "build", "App.csproj", "-c", "Release", "--nologo", "-v", "minimal", "--disable-build-servers", "-m:1" },
                TimeoutSeconds = 120
            },
            Baseline = new {
                Build = new { ExitCode = 0, Stdout = (string?)null, Stderr = (string?)null, CompilerDiagnostic = (object?)null },
                Check = (object?)null
            },
            Check = new { AssemblyPath = "bin/Release/net10.0/App.dll", TimeoutSeconds = 30, Stdout = "PASS\n", Stderr = "" },
            RequireSourceChange = true
        };
        var oldContractHash = Hash(JsonSerializer.Serialize(oldShape, compact));
        var omittedHash = omitted.Stages.GetProperty("contractSha256").GetString();
        var enabledHash = enabled.Stages.GetProperty("contractSha256").GetString();
        Check(omittedHash == oldContractHash && disabled.Stages.GetProperty("contractSha256").GetString() == oldContractHash,
            "omitted and explicit-false cadence preserve the exact old contract hash");
        Check(enabledHash != oldContractHash, "opting in changes the contract hash and cannot reuse another cadence's continuation");
        Check(omitted.Command.Arguments.GetProperty("responseSchema").GetRawText() ==
            enabled.Command.Arguments.GetProperty("responseSchema").GetRawText(), "the model action schema remains unchanged");
        using var descriptorPlugin = new ProgrammingServicePlugin();
        var descriptor = descriptorPlugin.Harnesses.Single(h => h.Descriptor.Id == ProgrammingAgentHarness.StagedId).Descriptor;
        var schema = descriptor.InputSchema;
        Check(schema.GetProperty("properties").GetProperty("verifyAfterEdit").GetProperty("type").GetString() == "boolean" &&
            !schema.GetProperty("required").EnumerateArray().Any(field => field.GetString() == "verifyAfterEdit") &&
            !schema.GetProperty("additionalProperties").GetBoolean(), "the input schema admits one optional boolean without opening unknown fields");
        var priorSchema = JsonNode.Parse(schema.GetRawText())!.AsObject();
        priorSchema["properties"]!.AsObject().Remove("verifyAfterEdit");
        var priorDescriptor = descriptor with { InputSchema = CerebralJson.Element(priorSchema) };
        var priorDescriptorHash = Hash(JsonSerializer.Serialize(priorDescriptor, compact));
        var descriptorHash = Hash(JsonSerializer.Serialize(descriptor, compact));
        Check(priorDescriptorHash != descriptorHash, "adding the optional input changes descriptor admission identity");
        Check(new StageFixture(verifyAfterEdit: true, fourFiles: true).Failed,
            "automatic cadence rejects multiple mutable files before dispatch");
        Check(StartChangedInput(enabled, node => {
            foreach (var file in node["requiredInputs"]!.AsArray()) file!["mutable"] = false;
            node["requireSourceChange"] = false;
        }).Next is CompleteWorkflow { Success: false }, "automatic cadence rejects zero mutable files before dispatch");
        Check(StartChangedInput(enabled, node => node["verifyAfterEdit"] = "true").Next is CompleteWorkflow { Success: false },
            "string cadence values are rejected rather than coerced");
        Check(StartChangedInput(enabled, node => node["verifyAfterEdit"] = null).Next is CompleteWorkflow { Success: false },
            "null cadence values do not silently disable verification");

        omitted.Edit(); disabled.Edit();
        Check(omitted.IsModel && disabled.IsModel && Number(omitted, "verificationCycles") == 0 &&
            Number(disabled, "verificationCycles") == 0, "existing single-file requests retain their explicit batch boundary");
        var batch = Ready(fourFiles: true);
        batch.Edit();
        Check(batch.IsModel && Number(batch, "verificationCycles") == 0, "the first file in a multi-file repair does not start premature verification");
        batch.ModelEdit("Helper.cs", "class Helper { public const int Value = 1; }\n"); batch.MutationResult();
        Check(batch.IsModel && Number(batch, "sourceMutationGeneration") == 2, "a second mutable file remains editable in the same batch");
        batch.Model(new { action = "done", summary = "Verify both changed files" }); batch.CompleteVerification();
        Check(batch.Passed && Number(batch, "verificationCycles") == 1 &&
            batch.Final.GetProperty("mutationEffects").GetArrayLength() == 2, "multi-file completion still verifies the complete logical batch");

        var beforeEdit = enabled.EffectsCompleted;
        enabled.ModelEdit("Program.cs", "class Implementation { public const int Value = 1; }\n");
        Check(enabled.IsTool("file.edit") && Number(enabled, "sourceMutationGeneration") == 0 &&
            Number(enabled, "verificationCycles") == 0, "dispatching an edit alone earns no mutation or verification progress");
        enabled.MutationResult();
        Check(enabled.IsTool("file.read") && enabled.StageName == "build-inputs" && !enabled.IsModel &&
            enabled.EffectsCompleted == beforeEdit + 2 && Number(enabled, "verificationCycles") == 1 &&
            Number(enabled, "sourceMutationGeneration") == 1, "a complete changed-byte receipt immediately requests policy verification without done or another model turn");
        Check(enabled.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null &&
            enabled.Stages.GetProperty("currentCheck").ValueKind == JsonValueKind.Null && !enabled.Passed,
            "automatic scheduling creates no passing proof");
        enabled.CompleteVerification();
        Check(enabled.Passed && enabled.ProofReadCount == 15 &&
            enabled.Final.GetProperty("currentCheck").GetProperty("buildEffectId").GetString() ==
            enabled.Final.GetProperty("currentBuild").GetProperty("effectId").GetString(),
            "automatic completion keeps all initial and post-command whole-input reads and exact current build/check join");

        foreach (var cadence in new[] { false, true })
        foreach (var kind in new[] { "build", "check", "check-zero" })
        {
            var failed = FailedFinal(cadence, kind);
            var feedback = Feedback(failed);
            var receipt = feedback.ExactReadHandle;
            var body = JsonSerializer.Serialize(feedback, compact);
            Check(failed.IsModel && Number(failed, "verificationCycles") == 1 &&
                !failed.Stages.GetProperty("latestVerification").GetProperty("matchedExpectation").GetBoolean(),
                "an actual final failure returns repair feedback: " + cadence + "/" + kind);
            var reads = failed.ProofReadCount;
            failed.Model(new { action = "done", summary = "Try the same source again" });
            Check(failed.IsModel && failed.StageName == "repair" && Number(failed, "verificationCycles") == 1 &&
                failed.ProofReadCount == reads && Number(failed, "repairs") == 1,
                "unchanged done spends one bounded correction and dispatches no verification reads/commands: " + cadence + "/" + kind);
            Check(JsonSerializer.Serialize(Feedback(failed), compact) == body &&
                Feedback(failed).Requirement == ContextRequirement.Required &&
                failed.Stages.GetProperty("currentBuild").ValueKind == JsonValueKind.Null &&
                failed.Stages.GetProperty("currentCheck").ValueKind == JsonValueKind.Null,
                "the no-progress correction retains exact required failure context without current credit: " + cadence + "/" + kind);
            failed.ModelEdit("Program.cs", "class Implementation { public const int Value = 2; }\n"); failed.MutationResult();
            Check(Number(failed, "sourceMutationGeneration") == 2 &&
                failed.Stages.GetProperty("latestVerification").GetProperty("sourceMutationGeneration").GetInt32() == 1 &&
                Feedback(failed).ExactReadHandle == receipt, "a new proved source generation keeps the earlier diagnostic historical: " + cadence + "/" + kind);
            if (!cadence) failed.Model(new { action = "done", summary = "Verify the corrected source" });
            Check(failed.IsTool("file.read") && Number(failed, "verificationCycles") == 2 &&
                Number(failed, "repairs") == 1, "a real edit unlocks fresh verification without resetting correction budgets: " + cadence + "/" + kind);
            failed.CompleteVerification();
            Check(failed.Passed && failed.Stages.GetProperty("latestVerification").ValueKind == JsonValueKind.Null,
                "only the fresh matched build/check sequence completes corrected source: " + cadence + "/" + kind);
        }

        foreach (var cadence in new[] { false, true })
        {
            var bounded = FailedFinal(cadence, "check");
            var reads = bounded.ProofReadCount;
            for (var attempt = 0; attempt < 3; attempt++)
                bounded.Model(new { action = "done", summary = "No source change" });
            Check(bounded.Failed && Number(bounded, "repairs") == 2 && Number(bounded, "verificationCycles") == 1 &&
                bounded.ProofReadCount == reads, "repeated done terminates at the existing correction bound without command retries: " + cadence);

            var noOp = FailedFinal(cadence, "build");
            var epoch = noOp.Stages.GetProperty("epoch").GetInt32();
            noOp.ModelEdit("Program.cs", noOp.Files["Program.cs"]);
            Check(noOp.IsModel && Number(noOp, "sourceMutationGeneration") == 1 &&
                Number(noOp, "verificationCycles") == 1 && noOp.Stages.GetProperty("epoch").GetInt32() == epoch,
                "an identical edit neither dispatches nor unlocks a failed generation: " + cadence);

            var read = FailedFinal(cadence, "check");
            read.Model(new { action = "tool", name = "file.read", arguments = new { path = "Program.cs", startLine = 1, lineCount = 1 } });
            if (read.IsTool("file.read")) read.ReadSources();
            read.Model(new { action = "done", summary = "After reading the same source" });
            Check(read.IsModel && Number(read, "verificationCycles") == 1 && Number(read, "sourceMutationGeneration") == 1,
                "a cached or actual source read cannot unlock the failed generation: " + cadence);

            var steer = FailedFinal(cadence, "check");
            steer.Steering = [new("failed-generation-steer", 1, "Keep the declared scope and diagnose the failure.", null, DateTimeOffset.UnixEpoch.AddDays(1))];
            steer.Model(new { action = "done", summary = "Superseded choice" });
            Check(steer.IsModel && Number(steer, "ownerSteeringCount") == 1 &&
                Number(steer, "sourceMutationGeneration") == 1, "owner steering is incorporated without source progress: " + cadence);
            // Exercise an independent epoch invalidation as well; it cannot replace byte proof.
            steer.ChangeState(node => node["stages"]!["epoch"] = node["stages"]!["epoch"]!.GetValue<int>() + 1);
            steer.Model(new { action = "done", summary = "Same bytes in another epoch" });
            Check(steer.IsModel && Number(steer, "verificationCycles") == 1 && Number(steer, "sourceMutationGeneration") == 1,
                "steering epoch changes do not substitute for changed-byte progress: " + cadence);
        }

        // Incomplete here describes a bounded displayed excerpt, not an unvalidated process result.
        var excerpt = Ready(true); excerpt.Edit(); excerpt.ReadSources(); excerpt.Process(0, "", ""); excerpt.ReadSources();
        excerpt.Process(1, "", "FAIL bounded output\n" + new string('x', 10000));
        Check(!Feedback(excerpt).Complete, "a fully validated long failure is retained as a marked bounded excerpt");
        excerpt.Model(new { action = "done", summary = "Retry despite the same long failure" });
        Check(excerpt.IsModel && Number(excerpt, "verificationCycles") == 1, "excerpt completeness cannot bypass the failed-generation guard");

        foreach (var outcome in new[] { "skipped", "failed", "unknown" })
        {
            var fixture = Ready(true); fixture.ModelEdit("Program.cs", "class Implementation { public const int Value = 1; }\n");
            if (outcome == "unknown") fixture.Unknown();
            else fixture.MutationResult(skipped: outcome == "skipped", success: outcome != "failed");
            Check(fixture.Failed && Number(fixture, "sourceMutationGeneration") == 0 &&
                Number(fixture, "verificationCycles") == 0 && fixture.Step.Next is not InvokeWorkflowCommand,
                "an unproved " + outcome + " mutation cannot trigger automatic verification or replay");
        }
        var immutable = Ready(true); immutable.ModelEdit("Checks.cs", "changed");
        Check(immutable.IsModel && Number(immutable, "verificationCycles") == 0 &&
            Number(immutable, "sourceMutationGeneration") == 0, "automatic cadence preserves immutable-file admission");
        var hashConflict = Ready(true); hashConflict.ModelEdit("Program.cs", "changed", hash: new string('0', 64));
        Check(hashConflict.IsModel && Number(hashConflict, "verificationCycles") == 0,
            "automatic cadence still requires the exact current mutation preimage");
        var drift = Ready(true); drift.Edit(); drift.Files["Checks.cs"] += "// external drift";
        drift.ReadSources();
        Check(drift.Failed, "automatic cadence keeps fresh immutable-input drift detection");

        var pendingSteer = Ready(true); pendingSteer.ModelEdit("Program.cs", "class Implementation { public const int Value = 1; }\n");
        pendingSteer.Steering = [new("edit-result-steer", 1, "Inspect the source before completing the assignment.", null, DateTimeOffset.UnixEpoch.AddDays(1))];
        pendingSteer.MutationResult();
        Check(pendingSteer.IsModel && Number(pendingSteer, "ownerSteeringCount") == 1 &&
            Number(pendingSteer, "sourceMutationGeneration") == 1 && Number(pendingSteer, "verificationCycles") == 0,
            "new owner steering takes precedence over automatic verification after a proved edit");
        var cancel = Ready(true); cancel.ModelEdit("Program.cs", "changed"); cancel.Cancel();
        Check(cancel.Failed && cancel.Step.Next is not InvokeWorkflowCommand && Number(cancel, "verificationCycles") == 0,
            "cancellation still stops before receipt-triggered verification");

        var expected = new StageFixture(verifyAfterEdit: true, compilerBaseline: true, requireChange: false);
        expected.ReadSources();
        expected.Process(1, $"Program.cs(1,1): error CS0103: The name 'missing' does not exist [{Path.Combine(expected.Root, "App.csproj")}]\n", "");
        expected.ReadSources();
        expected.Model(new { action = "done", summary = "Request the initial final verification" });
        Check(expected.IsTool("file.read") && Number(expected, "verificationCycles") == 1,
            "a matched expected compiler baseline is not an actual failed final verification");
        var expectedRuntime = new StageFixture(verifyAfterEdit: true, runtimeBaseline: true, requireChange: false);
        expectedRuntime.ToRepair(); expectedRuntime.Model(new { action = "done", summary = "Verify the no-change contract" });
        expectedRuntime.CompleteVerification();
        Check(expectedRuntime.Passed && Number(expectedRuntime, "sourceMutationGeneration") == 0 &&
            expectedRuntime.Final.GetProperty("mutationEffects").GetArrayLength() == 0,
            "an expected failed runtime baseline permits fresh initial final verification for a no-change contract");

        var zero = new StageFixture(verifyAfterEdit: true, requireChange: false); zero.ToRepair();
        zero.Model(new { action = "done", summary = "Verify the initial source" }); zero.ReadSources();
        zero.Process(1, "actual final failure at generation zero", "");
        zero.Model(new { action = "done", summary = "Retry the unchanged initial source" });
        Check(zero.IsModel && Number(zero, "sourceMutationGeneration") == 0 && Number(zero, "verificationCycles") == 1 &&
            zero.Stages.GetProperty("mutationEffects").GetArrayLength() == 0,
            "an actual failed final verification at generation zero is guarded even when source change is optional");

        var restored = FailedFinal(false, "check");
        restored.ModelEdit("Program.cs", "class Implementation { public const int Value = 2; }\n"); restored.MutationResult();
        restored.ModelEdit("Program.cs", "class Implementation { public const int Value = 1; }\n"); restored.MutationResult();
        Check(Number(restored, "sourceMutationGeneration") == 3 &&
            restored.Stages.GetProperty("latestVerification").GetProperty("sourceMutationGeneration").GetInt32() == 1,
            "two real changed-byte receipts remain new generations when the final bytes return to an earlier hash");
        restored.Model(new { action = "done", summary = "Verify the newly proved generation" });
        Check(restored.IsTool("file.read") && Number(restored, "verificationCycles") == 2,
            "a newly proved generation may request verification even when its bytes match an earlier failed generation");
        restored.CompleteVerification();
        Check(restored.Passed, "fresh matched receipts are still required after restoring earlier source bytes");

        var cycles = Ready(true);
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            cycles.ModelEdit("Program.cs", "class Implementation { public const int Value = " + cycle + "; }\n");
            cycles.MutationResult(); cycles.ReadSources(); cycles.Process(1, "compiler failure " + cycle, "");
        }
        Check(cycles.Failed && Number(cycles, "verificationCycles") == 3 && Number(cycles, "sourceMutationGeneration") == 3,
            "genuinely changed generations still stop after the existing three-cycle allowance");

        var budget = Ready(true, runtimeBaseline: true, limit: 48);
        budget.Edit(); budget.ReadSources(); budget.Process(0, "", ""); budget.ReadSources(); budget.Process(1, "", "FAIL empty case\n");
        budget.ModelEdit("Program.cs", "class Implementation { public const int Value = 2; }\n"); budget.MutationResult();
        budget.CompleteVerification();
        Check(budget.Passed && budget.EffectsCompleted == 31 && budget.EffectsCompleted < 48 &&
            budget.ProofReadCount == 21 && Number(budget, "verificationCycles") == 2,
            "single-file automatic correction completes in31 deterministic effects under the unchanged48 limit with all21 whole-input proof reads");
        Console.WriteLine(JsonSerializer.Serialize(new {
            evidenceTier = "deterministic unit/policy fixtures", verifyAfterEdit = true, effectLimit = 48,
            effects = budget.EffectsCompleted, proofReads = budget.ProofReadCount, verificationCycles = 2,
            oldContractSha256 = oldContractHash, omittedContractSha256 = omittedHash, enabledContractSha256 = enabledHash,
            descriptorHashDomain = "SHA-256 of compact CerebralJson serialization",
            priorDescriptorSha256 = priorDescriptorHash, descriptorSha256 = descriptorHash,
            priorInputSchemaSha256 = Hash(JsonSerializer.Serialize(priorDescriptor.InputSchema, compact)),
            inputSchemaSha256 = Hash(JsonSerializer.Serialize(descriptor.InputSchema, compact)),
            modelCalls = 0, processToolCalls = 0, runtimeAdopted = false
        }, compact));
        return checks;
    }
}
