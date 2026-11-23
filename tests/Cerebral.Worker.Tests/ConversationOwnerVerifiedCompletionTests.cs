using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Conversation;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunOwnerVerifiedCompletionOnlyAsync()
    {
        var count = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Owner-verified Conversation: " + name); count++; }
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-owner-completion-" + Guid.NewGuid().ToString("N")));
        var projectRoot = Path.Combine(root, "project");
        var component = Path.Combine(projectRoot, "Desktop");
        var checkerRoot = Path.Combine(root, "checker");
        var checkerImage = Path.Combine(checkerRoot, "Checker.dll");
        var source = Path.Combine(component, "GeneratedDesktopPlugin.cs");
        var project = Path.Combine(component, "Generated.Desktop.csproj");
        var sourceManifest = Path.Combine(component, "desktop-plugin.json");
        var builtManifest = Path.Combine(component, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var assembly = Path.Combine(component, "bin", "Release", "net10.0-windows", "Generated.Desktop.dll");
        var deps = Path.Combine(component, "bin", "Release", "net10.0-windows", "Generated.Desktop.deps.json");
        var sdk = Path.Combine(projectRoot, "sdk", "desktop", "Cerebral.Desktop.Plugins.dll");
        var sha = new string('a', 64);
        var pins = new[]
        {
            new OwnerVerificationBudget.InputPin("targetSource", source, sha, 40),
            new OwnerVerificationBudget.InputPin("targetProject", project, sha, 40),
            new OwnerVerificationBudget.InputPin("targetSourceManifest", sourceManifest, sha, 40),
            new OwnerVerificationBudget.InputPin("targetManifest", builtManifest, sha, 40),
            new OwnerVerificationBudget.InputPin("targetAssembly", assembly, sha, 40),
            new OwnerVerificationBudget.InputPin("targetDeps", deps, sha, 40),
            new OwnerVerificationBudget.InputPin("targetSdk", sdk, sha, 40)
        };
        var files = new[]
        {
            new OwnerVerificationAdmission.File("checkerAssembly", OwnerVerificationAdmission.Scope.Checker,
                "Checker.dll", checkerImage, new string('1', 64), 10)
        }.Concat(pins.Select(pin => new OwnerVerificationAdmission.File(pin.Role,
            OwnerVerificationAdmission.Scope.Target,
            Path.GetRelativePath(projectRoot, pin.Path).Replace(Path.DirectorySeparatorChar, '/'), pin.Path))).ToArray();
        var recipe = new OwnerVerificationAdmission(2, "fixture-owner-check", "conversation-run", "conversation", "general",
            ConversationAgentHarness.Id, ConversationAgentHarness.OwnerVerifiedPreparedVersion, "dotnet",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            [], [checkerImage], checkerRoot, checkerRoot, projectRoot, files,
            new("fixture.descriptor/1", "fixture.closure/1", "1.0.0", "fixture.summary/1",
                "fixture.report/1", "checkerAssembly", ["targetAssembly"], ["one assertion"]), 2);
        recipe.Validate();
        var recipeHash = recipe.RecipeSha256;
        var inputHash = OwnerVerificationBudget.InputIdentity(recipeHash, pins);
        var settings = new { resolvedSettingsVersion = 8, ownerVerification = recipe };
        var basicSchema = CerebralJson.Element(new { type = "object", additionalProperties = true });
        var presentationSchema = CerebralJson.Element(new { type = "object", properties = new
        { path = new { type = "string" }, expectedPackageFingerprint = new { type = "string", minLength = 64, maxLength = 64 } },
            required = new[] { "path" }, additionalProperties = false });
        var projectSchema = CerebralJson.Element(new { type = "object", properties = new
        { root = new { type = "string" }, targetPath = new { type = "string" } }, additionalProperties = false });
        var inspection = JsonSerializer.Serialize(new
        {
            targetPath = source, targetMatched = true, targetComponent = "desktop", discoverySummaryOnly = false,
            scaffoldReady = false, limited = false, count = 1,
            desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
            projects = new[] { new { root = projectRoot, components = new[] { new
            {
                component = "desktop", source, manifest = sourceManifest,
                build = new { executableId = "dotnet", arguments = new[] { "build", project,
                    "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" },
                    workingDirectory = component, builtManifest }
            } } } }
        }, CerebralJson.Options);
        Fixture Create()
        {
            var fixture = new Fixture(warm: false,
                input: new { workingDirectory = root, requiredSuccessfulTools = new[]
                    { "process.run", "plugin.verify", "plugin.present" } },
                selectedHarness: ConversationAgentHarness.CreateOwnerVerifiedPreparedEdition(),
                settingsSnapshot: settings);
            if (fixture.Step.Next is CompleteWorkflow)
                throw new InvalidOperationException("Owner-verified fixture start: " + fixture.Summary);
            fixture.History([]);
            if (fixture.Step.Next is CompleteWorkflow)
                throw new InvalidOperationException("Owner-verified fixture history: " + fixture.Summary);
            fixture.Catalog(forcedNames: ["plugin.projects", "file.write", "process.run", "plugin.verify", "plugin.present"]);
            if (fixture.Is("catalog")) fixture.Catalog(exact: true, inputSchema: basicSchema,
                inputSchemas: new Dictionary<string, JsonElement> { ["plugin.present"] = presentationSchema });
            if (!fixture.Is("generate"))
                throw new InvalidOperationException("Owner-verified fixture catalog: " + fixture.Step.Next);
            fixture.Select("plugin.projects", projectSchema);
            fixture.Select("file.write", basicSchema);
            return fixture;
        }
        object Build() => new { action = "tool", name = "process.run", arguments = new
        { executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release",
            "--disable-build-servers", "--verbosity", "minimal" }, workingDirectory = component } };
        object Checker() => new { action = "tool", name = "process.run", arguments = new
        { executableId = "dotnet", arguments = new[] { checkerImage }, workingDirectory = checkerRoot,
            timeoutSeconds = 180, expectedExitCode = 0 } };
        object Receipt(string effectId, string hash, string input, object? targetPins = null) => new
        {
            schemaVersion = 1, recipeSha256 = hash, inputSha256 = input, effectId,
            processDispatched = true, complete = true, exitCode = 0,
            inputs = targetPins ?? pins, reportSha256 = new string('d', 64)
        };
        var missing = new Fixture(warm: false,
            selectedHarness: ConversationAgentHarness.CreateOwnerVerifiedPreparedEdition());
        Check(missing.Step.Next is CompleteWorkflow { Success: false },
            "new edition refuses a submission without the host-frozen recipe");
        var built = Create();
        built.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "new source" } });
        built.Tool(true, inspection, provider: "cerebral.plugin-sdk");
        built.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = sha, bytes = 10 }));
        built.Model(Build());
        built.Tool(true, "Compiled.", exitCode: 0);
        Check(built.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.String &&
            !built.Step.State.GetProperty("pluginWork").TryGetProperty("ownerCheck", out _),
            "successful build has no checker milestone");
        var nearMatch = built.Fork();
        nearMatch.Model(new { action = "tool", name = "process.run", arguments = new
        { executableId = "dotnet", arguments = new[] { checkerImage }, workingDirectory = projectRoot,
            timeoutSeconds = 180, expectedExitCode = 0 } });
        Check(nearMatch.Is("tool"), "the malformed checker request reaches the host boundary");
        nearMatch.Tool(false, "Use the frozen checker working directory; no checker process was started.",
            outcome: ToolExecutionOutcome.Rejected, provider: "cerebral.owner-verification",
            evidenceState: "not-dispatched", verification: new { schemaVersion = 1,
                recipeSha256 = recipeHash, inputSha256 = new string('0', 64),
                effectId = nearMatch.NextEffectId, processDispatched = false, complete = true,
                exitCode = (int?)null, inputs = Array.Empty<object>() });
        Check(nearMatch.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.String &&
              !nearMatch.Step.State.GetProperty("pluginWork").TryGetProperty("ownerCheck", out _),
            "a no-process near match preserves the current successful build without granting checker credit");
        nearMatch.Model(Checker());
        Check(nearMatch.Is("tool"), "the corrected exact checker request can proceed");
        nearMatch.Tool(true, "All assertions passed.", provider: "cerebral.owner-verification",
            exitCode: 0, evidenceState: "completed",
            verification: Receipt(nearMatch.NextEffectId, recipeHash, inputHash));
        Check(nearMatch.Step.State.GetProperty("pluginWork").TryGetProperty("ownerCheck", out _) &&
              nearMatch.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.String,
            "the corrected same-run check can bind to the preserved build");
        var finalEarly = built.Fork();
        finalEarly.Model(new { action = "final", text = "Done." });
        Check(finalEarly.Is("generate") && finalEarly.LastContributionText.Contains("admitted owner checker receipt", StringComparison.Ordinal),
            "build-only process.run cannot satisfy final");
        var verifyEarly = built.Fork();
        verifyEarly.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = projectRoot } });
        Check(verifyEarly.Is("generate") && verifyEarly.LastContributionText.Contains("admitted owner checker receipt", StringComparison.Ordinal),
            "plugin.verify refuses before a checker pass");
        var presentEarly = built.Fork();
        presentEarly.Model(new { action = "tool", name = "plugin.present", arguments = new { path = builtManifest } });
        Check(presentEarly.Is("generate") && presentEarly.LastContributionText.Contains("admitted owner checker receipt", StringComparison.Ordinal),
            "plugin.present refuses before a checker pass");
        void CheckBadChecker(string name, string provider, string hash, string input,
            Func<Fixture, object>? receipt = null, bool failed = false)
        {
            var attempt = built.Fork();
            attempt.Model(Checker());
            Check(attempt.Is("tool") && attempt.Command.Arguments.GetProperty("name").GetString() == "process.run",
                name + " checker intent reaches normal tool admission");
            attempt.Tool(!failed, "Checker result.", provider: provider,
                outcome: failed ? ToolExecutionOutcome.Failed : ToolExecutionOutcome.Succeeded,
                exitCode: failed ? 1 : 0, evidenceState: "completed",
                verification: receipt?.Invoke(attempt) ?? Receipt(attempt.NextEffectId, hash, input));
            Check(!attempt.Step.State.GetProperty("pluginWork").TryGetProperty("ownerCheck", out _) &&
                attempt.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.Null,
                name + " cannot preserve build or grant checker credit");
        }
        CheckBadChecker("wrong-provider", "cerebral.process", recipeHash, inputHash);
        var otherHash = new string('e', 64);
        CheckBadChecker("wrong-recipe", "cerebral.owner-verification", otherHash,
            OwnerVerificationBudget.InputIdentity(otherHash, pins));
        CheckBadChecker("wrong-effect", "cerebral.owner-verification", recipeHash, inputHash,
            attempt => Receipt("other-effect", recipeHash, inputHash));
        CheckBadChecker("wrong-input-pins", "cerebral.owner-verification", recipeHash, inputHash,
            attempt => Receipt(attempt.NextEffectId, recipeHash, inputHash,
                pins.Select(pin => pin.Role == "targetSource" ? pin with { Sha256 = new string('f', 64) } : pin).ToArray()));
        CheckBadChecker("failed-checker", "cerebral.owner-verification", recipeHash, inputHash, failed: true);
        var unknown = built.Fork();
        unknown.Model(Checker());
        unknown.Uncertain();
        Check(unknown.Step.Next is CompleteWorkflow { Success: false } &&
            !unknown.Step.State.GetProperty("pluginWork").TryGetProperty("ownerCheck", out _),
            "Unknown checker outcome cannot grant completion");
        var checkedRun = built.Fork();
        checkedRun.Model(Checker());
        checkedRun.Tool(true, "All assertions passed.", provider: "cerebral.owner-verification",
            exitCode: 0, evidenceState: "completed",
            verification: Receipt(checkedRun.NextEffectId, recipeHash, inputHash));
        Check(checkedRun.Step.State.GetProperty("pluginWork").TryGetProperty("ownerCheck", out var ownerCheck) &&
            ownerCheck.GetProperty("receipt").GetProperty("recipeSha256").GetString() == recipeHash &&
            checkedRun.Step.State.GetProperty("pluginWork").GetProperty("buildEffectId").ValueKind == JsonValueKind.String,
            "valid native checker receipt is bound to the accepted recipe and current build");
        var mutated = checkedRun.Fork();
        mutated.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "later source" } });
        mutated.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('b', 64), bytes = 12 }));
        Check(!mutated.Step.State.GetProperty("pluginWork").TryGetProperty("ownerCheck", out _),
            "later source mutation invalidates checker credit");
        mutated.Model(Build());
        mutated.Tool(true, "Rebuilt.", exitCode: 0);
        Check(!mutated.Step.State.GetProperty("pluginWork").TryGetProperty("ownerCheck", out _),
            "later successful build cannot inherit old checker credit");
        checkedRun.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = projectRoot } });
        Check(checkedRun.Is("tool"), "valid checker permits plugin.verify dispatch");
        checkedRun.Tool(true, JsonSerializer.Serialize(new { root = projectRoot, verified = true,
            desktop = new { manifest = builtManifest, packageFingerprint = sha } }), provider: "cerebral.plugin-sdk");
        if (checkedRun.Step.Next is CompleteWorkflow)
            throw new InvalidOperationException("Owner-verified verify receipt: " + checkedRun.Summary);
        checkedRun.Model(new { action = "tool", name = "plugin.present",
            arguments = new { path = builtManifest, expectedPackageFingerprint = sha } });
        Check(checkedRun.Is("tool"), "current verification and checker permit plugin.present dispatch");
        checkedRun.Tool(true, "Owner review queued.");
        checkedRun.Model(new { action = "final", text = "Built and checked; owner review is queued." });
        Check(checkedRun.Step.Next is CompleteWorkflow { Success: true },
            "final requires checker plus current verify and present receipts");
        Console.WriteLine($"Owner-verified Conversation: {count} deterministic actual-plugin policy checks passed.");
        return Task.CompletedTask;
    }
}
