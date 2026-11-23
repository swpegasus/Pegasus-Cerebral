using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Conversation;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunPluginTargetInspectionOnlyAsync()
    {
        var targetChecks = RunPluginTargetInspectionChecks();
        var exportChecks = LocalGenerationConversationExportChecks.Run();
        Console.WriteLine($"Conversation plugin exact target: {targetChecks} deterministic policy/provider fixture checks; " +
            $"{exportChecks} versioned package export checks passed.");
        return Task.CompletedTask;
    }

    private static int RunPluginTargetInspectionChecks()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("Plugin exact target inspection: " + reason);
            checks++;
        }
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-target-fixture-" + Guid.NewGuid().ToString("N")));
        var projectRoot = Path.Combine(root, "opaque-project");
        var component = Path.Combine(projectRoot, "Desktop");
        var source = Path.Combine(component, "GeneratedDesktopPlugin.cs");
        var project = Path.Combine(component, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var manifest = Path.Combine(component, "desktop-plugin.json");
        var builtManifest = Path.Combine(component, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var hash = new string('a', 64);
        var basicSchema = CerebralJson.Element(new { type = "object", additionalProperties = true });
        var projectsSchema = CerebralJson.Element(new { type = "object", properties = new
        {
            root = new { type = "string" }, targetPath = new { type = "string" }
        }, additionalProperties = false });
        string Inspection(string target, string selected = "desktop") => JsonSerializer.Serialize(new
        {
            targetPath = target, targetMatched = true, targetComponent = selected,
            discoverySummaryOnly = false, scaffoldReady = false, limited = false, count = 1,
            desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
            projects = new[] { new { root = projectRoot, components = new[] { new
            {
                component = selected, source, manifest, build = new
                {
                    executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release",
                        "--disable-build-servers", "--verbosity", "minimal" },
                    workingDirectory = component, builtManifest
                }
            } } } }
        }, CerebralJson.Options);
        Fixture Create(ConversationAgentHarness? selectedHarness = null)
        {
            var fixture = new Fixture(warm: false, input: new { workingDirectory = root }, selectedHarness: selectedHarness);
            if (fixture.Step.Next is CompleteWorkflow) throw new InvalidOperationException(
                "Plugin exact target fixture could not start: " + fixture.Summary);
            fixture.History([]);
            if (fixture.Step.Next is CompleteWorkflow) throw new InvalidOperationException(
                "Plugin exact target fixture history failed: " + fixture.Summary);
            fixture.Catalog(forcedNames: ["plugin.projects", "file.write", "file.edit", "process.run",
                "plugin.verify", "plugin.present", "program.source.generate"]);
            fixture.Select("plugin.projects", projectsSchema);
            fixture.Select("file.write", basicSchema);
            return fixture;
        }
        void LookupResult(Fixture fixture, string target) =>
            fixture.Tool(true, Inspection(target), provider: "cerebral.plugin-sdk");
        foreach (var currentPublisher in new[] { false, true })
        {
            var unselected = new Fixture(warm: false, input: new { workingDirectory = root });
            unselected.History([]);
            unselected.Catalog(forcedNames: ["plugin.projects", "file.write"]);
            unselected.Select("file.write", basicSchema);
            unselected.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "deferred" } });
            Check(unselected.Is("catalog") && unselected.Command.Arguments.GetProperty("names")[0].GetString() == "plugin.projects",
                "unselected publisher resolves its exact schema before constructing any targetPath call");
            var oldSchema = CerebralJson.Element(new { type = "object", properties = new { root = new { type = "string" } },
                additionalProperties = false });
            unselected.Catalog(exact: true, inputSchema: currentPublisher ? projectsSchema : oldSchema);
            Check(unselected.Is("tool") && unselected.Command.Arguments.GetProperty("name").GetString() ==
                (currentPublisher ? "plugin.projects" : "file.write"),
                "exact schema capability selects current target lookup or older publisher ordinary compatibility");
        }
        var schemaFailure = new Fixture(warm: false, input: new { workingDirectory = root });
        schemaFailure.History([]);
        schemaFailure.Catalog(forcedNames: ["plugin.projects", "file.write"]);
        schemaFailure.Select("file.write", basicSchema);
        schemaFailure.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "deferred" } });
        schemaFailure.CatalogFailure("Publisher unavailable.");
        Check(schemaFailure.Is("generate") && !schemaFailure.Step.State.TryGetProperty("pendingPluginTargetAction", out _) &&
            schemaFailure.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "failed exact publisher schema lookup clears its deferred action without dispatching a target lookup or mutation");
        var repaired = Create();
        repaired.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "new source" } });
        Check(repaired.Is("tool") && repaired.Command.Arguments.GetProperty("name").GetString() == "plugin.projects" &&
            repaired.Command.Arguments.GetProperty("arguments").GetProperty("targetPath").GetString() == source,
            "ordinary source mutation is deferred behind an exact publisher target lookup");
        Check(repaired.Step.State.GetProperty("completionMilestones").EnumerateObject().Count() == 0,
            "lookup alone introduces no authoring completion obligation");
        LookupResult(repaired, source);
        Check(repaired.Is("tool") && repaired.Command.Arguments.GetProperty("name").GetString() == "file.write" &&
            repaired.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 0 &&
            repaired.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.Null,
            "matching receipt resumes the exact saved mutation with fresh component binding and no imported build credit");
        repaired.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = hash, bytes = 10 }));
        Check(repaired.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 1 &&
            repaired.Step.State.GetProperty("completionMilestones").TryGetProperty("plugin.verify", out _) &&
            repaired.Step.State.GetProperty("completionMilestones").TryGetProperty("plugin.present", out _),
            "confirmed ordinary source change acquires current verification and presentation obligations");
        repaired.Model(new { action = "final", text = "Repaired." });
        Check(repaired.Is("generate") && repaired.LastContributionText.Contains("plugin.verify", StringComparison.Ordinal),
            "an ordinary repair cannot finalize after writing and testing the old presentation");

        repaired.Select("process.run", basicSchema);
        object CanonicalBuild() => new { action = "tool", name = "process.run", arguments = new
        {
            executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release",
                "--disable-build-servers", "--verbosity", "minimal" }, workingDirectory = component
        } };
        repaired.Model(CanonicalBuild());
        repaired.Tool(true, "Compiled.", exitCode: 0);
        repaired.Select("plugin.verify", basicSchema);
        repaired.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = projectRoot } });
        repaired.Tool(true, JsonSerializer.Serialize(new { root = projectRoot, verified = true,
            desktop = new { manifest = builtManifest, packageFingerprint = hash } }), provider: "cerebral.plugin-sdk");
        Check(repaired.Is("generate"), "verification receipt leaves ordinary work usable: " +
            (repaired.Step.Next is CompleteWorkflow ? repaired.Summary : repaired.LastContributionText));
        var presentationSchema = CerebralJson.Element(new { type = "object", properties = new
        {
            path = new { type = "string" }, expectedPackageFingerprint = new { type = "string", minLength = 64, maxLength = 64 }
        }, required = new[] { "path" }, additionalProperties = false });
        repaired.Select("plugin.present", presentationSchema);
        Check(repaired.Is("generate"), "presentation schema leaves ordinary work usable: " +
            (repaired.Step.Next is CompleteWorkflow ? repaired.Summary : repaired.LastContributionText));
        repaired.Model(new { action = "tool", name = "plugin.present", arguments = new { path = builtManifest, expectedPackageFingerprint = hash } });
        repaired.Tool(true, "Owner review queued.");
        Check(repaired.Is("generate"), "presentation receipt leaves the ordinary follow-up usable: " +
            (repaired.Step.Next is CompleteWorkflow ? repaired.Summary : repaired.LastContributionText));
        var completedBinding = repaired.Step.State.GetProperty("pluginWork");
        var completedReceiptCount = repaired.Step.State.GetProperty("toolReceipts").GetArrayLength();
        repaired.Model(CanonicalBuild());
        Check(repaired.Is("generate") && repaired.LastContributionText.Contains("No repeated build was dispatched", StringComparison.Ordinal) &&
            repaired.Step.State.GetProperty("toolReceipts").GetArrayLength() == completedReceiptCount &&
            repaired.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").GetString() == completedBinding.GetProperty("buildEffectId").GetString() &&
            repaired.Step.State.GetProperty("pluginWork").GetProperty("verifyEffectId").GetString() == completedBinding.GetProperty("verifyEffectId").GetString() &&
            repaired.Step.State.GetProperty("pluginWork").GetProperty("presentEffectId").GetString() == completedBinding.GetProperty("presentEffectId").GetString(),
            "replaying a completed canonical generation preserves build/verify/present receipts and is suppressed before dispatch");
        var invalidated = repaired.Fork();
        invalidated.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = projectRoot } });
        invalidated.Tool(false, "Verification failed.", outcome: ToolExecutionOutcome.Failed);
        invalidated.Model(CanonicalBuild());
        Check(invalidated.Is("tool") && invalidated.Command.Arguments.GetProperty("name").GetString() == "process.run",
            "existing explicit verification invalidation admits the next canonical build without fabricating a source mutation");
        var completedLoop = repaired.Fork();
        completedLoop.Model(CanonicalBuild());
        completedLoop.Model(CanonicalBuild());
        Check(completedLoop.Step.Next is CompleteWorkflow { Success: false },
            "completed canonical build replay corrections are bounded even when the model keeps proposing a syntactically valid action");
        repaired.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "actually changed source" } });
        repaired.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('b', 64), bytes = 23 }));
        repaired.Model(CanonicalBuild());
        Check(repaired.Is("tool") && repaired.Command.Arguments.GetProperty("name").GetString() == "process.run",
            "a proven source change invalidates the completed generation and admits its next canonical rebuild");

        const string guardedSource = "new generation";
        var guardedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guardedSource))).ToLowerInvariant();
        foreach (var guardFactory in new Func<ConversationAgentHarness>[]
            { ConversationAgentHarness.CreateRepeatedSuccessGuardEdition, ConversationAgentHarness.CreateRepeatPreparedEdition })
        {
        var guarded = Create(guardFactory());
        guarded.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = guardedSource } });
        LookupResult(guarded, source);
        guarded.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = guardedHash, bytes = 14 }));
        guarded.Select("process.run", basicSchema);
        guarded.Model(CanonicalBuild());
        guarded.Tool(true, "Compiled.", exitCode: 0);
        var guardedBuild = guarded.Step.State.GetProperty("pluginWork");
        var guardedBuildReceipt = guardedBuild.GetProperty("buildEffectId").GetString();
        var guardedReceiptCount = guarded.Step.State.GetProperty("toolReceipts").GetArrayLength();
        Check(guardedBuild.GetProperty("successfulBuildGeneration").GetInt32() ==
              guardedBuild.GetProperty("sourceGeneration").GetInt32() &&
              guardedBuild.GetProperty("verifiedGeneration").ValueKind == JsonValueKind.Null,
            "new edition records a current-generation successful build before verification");
        var checkerRequest = new { action = "tool", name = "process.run", arguments = new
        {
            executableId = "dotnet", arguments = new[] { Path.Combine(root, "BehaviorProbeV2.dll") },
            workingDirectory = component
        } };
        var targetPins = new[]
        {
            new Cerebral.Worker.OwnerVerificationBudget.InputPin("targetSource", source, hash, 40),
            new Cerebral.Worker.OwnerVerificationBudget.InputPin("targetProject", project, hash, 40),
            new Cerebral.Worker.OwnerVerificationBudget.InputPin("targetSourceManifest", manifest, hash, 40),
            new Cerebral.Worker.OwnerVerificationBudget.InputPin("targetManifest", builtManifest, hash, 40),
            new Cerebral.Worker.OwnerVerificationBudget.InputPin("targetAssembly",
                Path.Combine(component, "bin", "Release", "net10.0-windows", "Plugin.dll"), hash, 40),
            new Cerebral.Worker.OwnerVerificationBudget.InputPin("targetDeps",
                Path.Combine(component, "bin", "Release", "net10.0-windows", "Plugin.deps.json"), hash, 40),
            new Cerebral.Worker.OwnerVerificationBudget.InputPin("targetSdk",
                Path.Combine(projectRoot, "sdk", "desktop", "Cerebral.Desktop.Plugins.dll"), hash, 40)
        };
        var recipeHash = new string('c', 64);
        var inputHash = Cerebral.Worker.OwnerVerificationBudget.InputIdentity(recipeHash, targetPins);
        var checkedRun = guarded.Fork();
        checkedRun.Model(checkerRequest);
        Check(checkedRun.Is("tool"), "the distinct checker request reaches ordinary tool admission");
        checkedRun.Tool(true, "All assertions passed.", provider: "cerebral.owner-verification",
            exitCode: 0, evidenceState: "completed", verification: new
            {
                schemaVersion = 1, recipeSha256 = recipeHash, inputSha256 = inputHash,
                effectId = checkedRun.NextEffectId, processDispatched = true, complete = true,
                exitCode = 0, inputs = targetPins, reportSha256 = new string('d', 64)
            });
        Check(checkedRun.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").GetString() == guardedBuildReceipt &&
              checkedRun.LastContributionText.Contains("Current pinned inputs passed all 10", StringComparison.Ordinal) &&
              checkedRun.LastContributionText.Contains("plugin.verify", StringComparison.Ordinal),
            "complete current-input checker receipt preserves build and gives a visible next action");
        var genericRun = guarded.Fork();
        genericRun.Model(checkerRequest);
        genericRun.Tool(true, "Generic process completed.", provider: "cerebral.process", exitCode: 0);
        Check(genericRun.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.Null,
            "ordinary process.run retains its build invalidation policy");
        var forgedRun = guarded.Fork();
        forgedRun.Model(checkerRequest);
        forgedRun.Tool(true, "Claimed checker pass.", provider: "cerebral.owner-verification",
            exitCode: 0, evidenceState: "completed", verification: new
            {
                schemaVersion = 1, recipeSha256 = recipeHash, inputSha256 = new string('e', 64),
                effectId = forgedRun.NextEffectId, processDispatched = true, complete = true,
                exitCode = 0, inputs = targetPins, reportSha256 = new string('d', 64)
            });
        Check(forgedRun.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.Null,
            "a mismatched input digest cannot preserve the canonical build");
        guarded.Model(CanonicalBuild());
        Check(guarded.Is("generate") &&
              guarded.LastContributionText.Contains("No repeated build was dispatched", StringComparison.Ordinal) &&
              guarded.LastContributionText.Contains("plugin.verify", StringComparison.Ordinal) &&
              guarded.Step.State.GetProperty("toolReceipts").GetArrayLength() == guardedReceiptCount &&
              guarded.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").GetString() == guardedBuildReceipt,
            "identical successful Release build is stopped before dispatch with stable receipt and next action");
        var guardedNoOp = guarded.Fork();
        guardedNoOp.Select("file.read", basicSchema);
        guardedNoOp.Model(new { action = "tool", name = "file.read", arguments = new { path = source } });
        guardedNoOp.Tool(true, JsonSerializer.Serialize(new
            { path = source, sha256 = guardedHash, startLine = 1, totalLines = 1, content = guardedSource }));
        guardedNoOp.Model(new { action = "tool", name = "file.write", arguments = new
            { path = source, content = guardedSource, collision = "replace", expectedSha256 = guardedHash } });
        Check(guardedNoOp.Is("tool"), "same-byte replacement follows ordinary tool admission");
        guardedNoOp.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = guardedHash, bytes = 14 }));
        Check(guardedNoOp.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 1 &&
              guardedNoOp.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").GetString() == guardedBuildReceipt &&
              guardedNoOp.LastContributionText.Contains("no source-generation or repair credit", StringComparison.Ordinal),
            "proven same-byte write preserves build evidence without granting source progress");
        guardedNoOp.Model(CanonicalBuild());
        Check(guardedNoOp.Is("generate") &&
              guardedNoOp.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").GetString() == guardedBuildReceipt,
            "same-byte write cannot reopen the completed current-generation build");
        var guardedCompacted = guarded.Fork();
        guardedCompacted.RejectContext(ContextProjectionStatus.NeedsCompaction);
        Check(guardedCompacted.Is("compact"), "repeat guard reaches the normal compaction boundary");
        guardedCompacted.CompactSuccess();
        guardedCompacted.Model(CanonicalBuild());
        Check(guardedCompacted.Is("generate") &&
              guardedCompacted.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").GetString() == guardedBuildReceipt &&
              guardedCompacted.Step.State.GetProperty("toolReceipts").GetArrayLength() == guardedReceiptCount,
            "compaction preserves the completed current-generation build and suppresses repeat dispatch");
        var guardedSaved = JsonDocument.Parse(guarded.Step.State.GetRawText()).RootElement.Clone();
        guarded.RestoreState(guardedSaved);
        guarded.Model(CanonicalBuild());
        Check(guarded.Is("generate") && guarded.Step.State.GetProperty("toolReceipts").GetArrayLength() == guardedReceiptCount &&
              guarded.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").GetString() == guardedBuildReceipt,
            "serialized state keeps the same generation and completed build evidence");
        var guardedChanged = guarded.Fork();
        guardedChanged.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "changed again" } });
        guardedChanged.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('b', 64), bytes = 13 }));
        guardedChanged.Model(CanonicalBuild());
        Check(guardedChanged.Is("tool") && guardedChanged.Command.Arguments.GetProperty("name").GetString() == "process.run" &&
              guardedChanged.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 2,
            "actual source mutation opens a new generation and admits its first canonical build");

        var guardedFailed = Create(guardFactory());
        guardedFailed.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "failed build" } });
        LookupResult(guardedFailed, source);
        guardedFailed.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = hash, bytes = 12 }));
        guardedFailed.Select("process.run", basicSchema);
        guardedFailed.Model(CanonicalBuild());
        guardedFailed.Tool(false, "Compiler error.", outcome: ToolExecutionOutcome.Failed, exitCode: 1);
        Check(guardedFailed.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.Null &&
              guardedFailed.Step.State.GetProperty("pluginWork").GetProperty("successfulBuildGeneration").ValueKind == JsonValueKind.Null,
            "failed canonical build never gains repeat-suppression success credit");
        var guardedUnknown = Create(guardFactory());
        guardedUnknown.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "unknown build" } });
        LookupResult(guardedUnknown, source);
        guardedUnknown.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = hash, bytes = 13 }));
        guardedUnknown.Select("process.run", basicSchema);
        guardedUnknown.Model(CanonicalBuild());
        guardedUnknown.Uncertain();
        Check(guardedUnknown.Step.Next is CompleteWorkflow { Success: false } &&
              guardedUnknown.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.Null,
            "unknown build outcome terminates without fabricated success credit");
        }

        var ordinary = Create();
        var ordinaryPath = Path.Combine(root, "plain.txt");
        ordinary.Model(new { action = "tool", name = "file.write", arguments = new { path = ordinaryPath, content = "ordinary" } });
        ordinary.Tool(true, JsonSerializer.Serialize(new { targetPath = ordinaryPath, targetMatched = false }), provider: "cerebral.plugin-sdk");
        Check(ordinary.Is("tool") && ordinary.Command.Arguments.GetProperty("name").GetString() == "file.write" &&
            ordinary.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Null,
            "an exact non-SDK receipt resumes ordinary programming without fabricated plugin ownership");
        ordinary.Tool(true, JsonSerializer.Serialize(new { path = ordinaryPath, sha256 = hash, bytes = 8 }));
        ordinary.Model(new { action = "tool", name = "file.write", arguments = new { path = ordinaryPath, content = "again" } });
        Check(ordinary.Is("tool") && ordinary.Command.Arguments.GetProperty("name").GetString() == "plugin.projects",
            "a non-SDK receipt is one-use and cannot bypass a later newly created project");

        foreach (var (provider, truncated, target) in new[]
            { ("fixture", false, source), ("cerebral.plugin-sdk", true, source), ("cerebral.plugin-sdk", false, ordinaryPath) })
        {
            var invalid = Create();
            invalid.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "not dispatched" } });
            invalid.Tool(true, Inspection(target), provider: provider, truncated: truncated);
            Check(invalid.Is("generate") && invalid.LastContributionText.Contains("No deferred authoring action", StringComparison.Ordinal) &&
                !invalid.Step.State.TryGetProperty("pendingPluginTargetAction", out _) &&
                invalid.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Null,
                "foreign, truncated, and wrong-target receipts never dispatch the saved source mutation");
        }
        var independentTargets = Create();
        foreach (var target in new[] { source, ordinaryPath })
        {
            independentTargets.Model(new { action = "tool", name = "file.write", arguments = new { path = target, content = "not dispatched" } });
            independentTargets.Tool(false, "Malformed recognized SDK source.", outcome: ToolExecutionOutcome.Rejected, provider: "cerebral.plugin-sdk");
        }
        Check(independentTargets.Step.State.GetProperty("admissionRejections").GetArrayLength() == 2,
            "identical target lookup rejection reasons retain independent budgets for different exact target paths");
        var foreignRequest = Create();
        foreignRequest.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "not dispatched" } });
        foreignRequest.Tool(true, Inspection(source), provider: "cerebral.plugin-sdk", requestId: "foreign-effect");
        Check(foreignRequest.Is("generate") && !foreignRequest.Step.State.TryGetProperty("pendingPluginTargetAction", out _) &&
            foreignRequest.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Null,
            "a non-null mismatched request ID cannot adopt binding or dispatch the saved mutation");

        var failed = Create();
        failed.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "not dispatched" } });
        failed.Tool(false, "Malformed recognized SDK source.", outcome: ToolExecutionOutcome.Rejected, provider: "cerebral.plugin-sdk");
        Check(failed.Is("generate") && failed.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Null,
            "a rejected malformed project preserves its receipt and stops the deferred mutation");

        failed.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "still not dispatched" } });
        Check(failed.Is("generate") && !failed.Step.State.TryGetProperty("pendingPluginTargetAction", out _),
            "a repeated rejected lookup clears the deferred proposal when exact admission replay is suppressed");
        failed.Model(new { action = "final", text = "Only inspected." });
        Check(failed.Step.Next is CompleteWorkflow,
            "the next model effect remains usable after a suppressed target lookup");

        var schemaLimit = new Fixture(warm: false, input: new { workingDirectory = root });
        schemaLimit.History([]);
        schemaLimit.Catalog(forcedNames: ["plugin.projects", "file.write"]);
        schemaLimit.Select("file.write", basicSchema);
        var exhausted = System.Text.Json.Nodes.JsonNode.Parse(schemaLimit.Step.State.GetRawText())!.AsObject();
        exhausted["descriptions"] = 16;
        schemaLimit.RestoreState(CerebralJson.Element(exhausted));
        schemaLimit.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "not dispatched" } });
        Check(schemaLimit.Is("generate") && !schemaLimit.Step.State.TryGetProperty("pendingPluginTargetAction", out _),
            "an exhausted schema budget clears target deferral without inventing a lookup or mutation");
        schemaLimit.Model(new { action = "final", text = "No mutation." });
        Check(schemaLimit.Step.Next is CompleteWorkflow,
            "the next model effect remains usable after a target schema quota correction");

        var build = Create();
        build.Select("process.run", basicSchema);
        build.Model(new { action = "tool", name = "process.run", arguments = new
        {
            executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release",
                "--disable-build-servers", "--verbosity", "minimal" }, workingDirectory = component
        } });
        LookupResult(build, project);
        Check(build.Is("tool") && build.Command.Arguments.GetProperty("name").GetString() == "process.run",
            "a canonical ordinary repair build acquires its exact SDK component binding before execution");
        build.Tool(true, "Canonical build completed.", exitCode: 0);
        Check(!build.Step.State.GetProperty("completionMilestones").TryGetProperty("plugin.verify", out _) &&
            !build.Step.State.GetProperty("completionMilestones").TryGetProperty("plugin.present", out _),
            "a confirmed canonical build of unchanged source adds no automatic verify/present obligations");
        build.Model(new { action = "final", text = "The existing target built; verification and presentation were not run." });
        Check(build.Step.Next is CompleteWorkflow { Success: true },
            "an honest final after an unchanged-source build is accepted");

        var generated = Create();
        generated.Select("program.source.generate", basicSchema);
        generated.Model(new { action = "tool", name = "program.source.generate", arguments = new { } });
        var plan = new ToolGenerationRequest("Generate the authorized source repair.", "file.write",
            CerebralJson.Element(new { path = source }), MaxTokens: 1024, CompleteAssignment: false);
        generated.Tool(true, "Plan.", followUp: true, generation: plan);
        Check(generated.Is("tool") && generated.Command.Arguments.GetProperty("name").GetString() == "plugin.projects" &&
            generated.Step.State.GetProperty("pendingPluginTargetAction").GetProperty("generation").ValueKind == JsonValueKind.Object,
            "a provider-owned raw sink resolves its SDK target before source generation");
        LookupResult(generated, source);
        Check(generated.Is("generate") && generated.Step.State.GetProperty("phase").GetString() == "generated-model",
            "a matched raw-sink lookup resumes the original admitted generation plan");
        generated.RawGeneration("public sealed class ExactTargetGeneratedSentinel { }");
        generated.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = hash, bytes = 51 }));
        Check(generated.Step.State.GetProperty("completionMilestones").TryGetProperty("plugin.verify", out _) &&
            generated.Step.State.GetProperty("completionMilestones").TryGetProperty("plugin.present", out _) &&
            !generated.Step.State.GetRawText().Contains("ExactTargetGeneratedSentinel", StringComparison.Ordinal),
            "generated source sinks acquire obligations and retain no raw source after their matched receipt");

        try
        {
            Directory.CreateDirectory(component);
            Directory.CreateDirectory(Path.Combine(projectRoot, "sdk", "desktop"));
            File.WriteAllText(Path.Combine(projectRoot, "PLUGIN.md"), "SDK source guide.");
            File.WriteAllText(source, "class GeneratedDesktopPlugin { }");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(manifest, "{\"id\":\"opaque-fixture\",\"name\":\"Opaque fixture\",\"version\":\"1.0.0\"}");
            File.Copy(typeof(IServicePlugin).Assembly.Location, Path.Combine(projectRoot, "sdk", "desktop", "Cerebral.Desktop.Plugins.dll"));
            var publisher = new PluginScaffoldToolExtension(null, typeof(IServicePlugin).Assembly.Location);
            var profile = new AgentProfile { Role = "lead", WorkerId = "local", AllowedRoots = [root],
                AllowedTools = ["plugin.projects"], Capabilities = ["filesystem.read", "desktop.plugins.manage"] };
            var context = new ToolExecutionContext(profile, "fixture", "fixture");
            ToolResult Resolve(object args, CancellationToken cancellation = default) => publisher.ExecuteAsync(
                new("plugin.projects", CerebralJson.Element(args)), context, cancellation).GetAwaiter().GetResult();
            var actual = Resolve(new { targetPath = source });
            var result = JsonDocument.Parse(actual.Content).RootElement;
            Check(actual.Success && result.GetProperty("targetMatched").GetBoolean() &&
                result.GetProperty("projects")[0].GetProperty("root").GetString() == projectRoot &&
                result.GetProperty("targetComponent").GetString() == "desktop",
                "real publisher resolves opaque source roots structurally without basename or prompt intent");
            Check(result.GetProperty("projects")[0].GetProperty("components")[0].GetProperty("build")
                    .GetProperty("arguments")[1].GetString() == project &&
                result.GetProperty("scaffoldReady").GetBoolean() == false,
                "real exact target lookup returns the current canonical build and grants no broad scaffold admission");
            File.WriteAllText(Path.Combine(root, "PLUGIN.md"), "Ordinary documentation with no fixed SDK scaffold.");
            var absent = Resolve(new { targetPath = ordinaryPath });
            Check(absent.Success && !JsonDocument.Parse(absent.Content).RootElement.GetProperty("targetMatched").GetBoolean(),
                "real publisher distinguishes ordinary file targets from SDK-owned source");
            var outside = Resolve(new { targetPath = Path.Combine(Path.GetDirectoryName(root)!, "foreign.txt") });
            Check(!outside.Success && outside.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "exact target lookup retains original allowed-root admission");
            Check(!Resolve(new { root = projectRoot, targetPath = source }).Success,
                "root and targetPath are mutually exclusive at the provider boundary");
            File.WriteAllText(manifest, "{ invalid manifest");
            var malformed = Resolve(new { targetPath = source });
            Check(!malformed.Success && malformed.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                malformed.Content.Contains(projectRoot, StringComparison.Ordinal),
                "proven SDK project with malformed metadata returns an actionable rejection rather than ordinary-file absence");
            File.Delete(manifest);
            File.Delete(source);
            var missingMetadata = Resolve(new { targetPath = source });
            Check(!missingMetadata.Success && missingMetadata.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                missingMetadata.Content.Contains(projectRoot, StringComparison.Ordinal),
                "guide plus canonical component project and copied SDK recognizes missing source/manifest as malformed ownership");
            File.WriteAllText(source, "class GeneratedDesktopPlugin { }");
            File.WriteAllText(manifest, "{\"id\":\"opaque-fixture\",\"name\":\"Opaque fixture\",\"version\":\"1.0.0\"}");
            File.Delete(Path.Combine(projectRoot, "PLUGIN.md"));
            var missingGuide = Resolve(new { targetPath = source });
            Check(!missingGuide.Success && missingGuide.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                missingGuide.Content.Contains("PLUGIN.md", StringComparison.Ordinal),
                "canonical component project, copied SDK, and source metadata recognizes a missing guide as malformed ownership");
            File.Delete(source);
            File.Delete(manifest);
            var missingAllMetadata = Resolve(new { targetPath = source });
            Check(!missingAllMetadata.Success && missingAllMetadata.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                missingAllMetadata.Content.Contains("PLUGIN.md", StringComparison.Ordinal),
                "fixed canonical project and copied SDK alone remain malformed scaffold ownership when guide/source/manifest are missing");
            File.WriteAllText(source, "class GeneratedDesktopPlugin { }");
            File.WriteAllText(manifest, "{\"id\":\"opaque-fixture\",\"name\":\"Opaque fixture\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(projectRoot, "PLUGIN.md"), "SDK source guide.");
            var service = Path.Combine(projectRoot, "Service");
            Directory.CreateDirectory(service);
            Directory.CreateDirectory(Path.Combine(projectRoot, "sdk", "service"));
            File.WriteAllText(Path.Combine(service, "GeneratedServicePlugin.cs"), "class GeneratedServicePlugin { }");
            File.WriteAllText(Path.Combine(service, "Cerebral.GeneratedPlugin.Service.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(service, "service-plugin.json"), "{\"id\":\"opaque-fixture\",\"name\":\"Opaque fixture\",\"version\":\"1.0.0\"}");
            File.Copy(typeof(IServicePlugin).Assembly.Location, Path.Combine(projectRoot, "sdk", "service", "Cerebral.Core.dll"));
            var ambiguous = Resolve(new { targetPath = projectRoot });
            Check(!ambiguous.Success && ambiguous.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                ambiguous.Content.Contains("unique component", StringComparison.Ordinal),
                "a cross-surface root target without one exact component fails closed");
            var selectedDesktop = Resolve(new { targetPath = source });
            Check(selectedDesktop.Success && JsonDocument.Parse(selectedDesktop.Content).RootElement
                .GetProperty("projects")[0].GetProperty("components").GetArrayLength() == 1,
                "an exact desktop coordinate selects only its current component in a cross-surface scaffold");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try { _ = Resolve(new { targetPath = source }, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "exact publisher lookup preserves cancellation before ancestor/source inspection");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return checks + RunScaffoldReadinessTargetChecks();
    }
}
