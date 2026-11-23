using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunPresentationHandoffPolicyOnlyAsync()
    {
        Console.WriteLine($"Ordinary plugin presentation policy: {RunPresentationHandoffPolicyChecks()} deterministic harness checks passed; no model or native execution.");
        return Task.CompletedTask;
    }

    private static int RunPresentationHandoffPolicyChecks()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Ordinary plugin presentation policy: " + label);
            checks++;
        }
        const string marker = "The selected plugin component has reached its current-generation delivery checkpoint";
        const string presentation = "11111111111111111111111111111111";
        const string qaMount = "handoff.main", pendingPresentation = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralPresentationPolicy"));
        var directory = Path.Combine(root, "Desktop");
        var source = Path.Combine(directory, "GeneratedDesktopPlugin.cs");
        var project = Path.Combine(directory, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var builtManifest = Path.Combine(directory, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var hash = new string('a', 64);
        var basicSchema = CerebralJson.Element(new { type = "object", additionalProperties = true });
        var projectsSchema = CerebralJson.Element(new { type = "object", properties = new { root = new { type = "string" } },
            required = new[] { "root" }, additionalProperties = false });
        var presentSchema = CerebralJson.Element(new { type = "object", properties = new
        {
            path = new { type = "string" }, expectedPackageFingerprint = new { type = "string", minLength = 64, maxLength = 64 }
        }, required = new[] { "path" }, additionalProperties = false });
        var canonicalBuild = new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" };
        Fixture Bound(bool otherDeliverable = false, bool automaticQa = false)
        {
            var fixture = new Fixture(warm: false, input: new { workingDirectory = root,
                requiredSuccessfulTools = automaticQa ? new[] { "interface.status", "plugin.controls", "fixture.pending" } :
                    otherDeliverable ? new[] { "file.write" } : [] },
                requestId: automaticQa ? "plugin-tests-" + presentation : "conversation-run",
                hostAddedRequiredTools: automaticQa ? new[] { "interface.status", "plugin.controls" } : null,
                caller: automaticQa ? new EventActor(EventActorKind.Service, "cerebral.plugin-tests", "QA") : null,
                hostPluginTestMountIds: automaticQa ? new[] { qaMount } : null,
                pluginSource: automaticQa ? new { schemaVersion = 1, presentationId = presentation, root } : null);
            fixture.History([]);
            fixture.Catalog(forcedNames: automaticQa
                ? ["plugin.projects", "file.write", "file.read", "process.run", "plugin.verify", "plugin.present", "interface.status", "plugin.controls", "fixture.pending"]
                : ["plugin.projects", "file.write", "file.read", "process.run", "plugin.verify", "plugin.present"]);
            if (fixture.Is("catalog")) fixture.Catalog(exact: true, inputSchema: basicSchema,
                inputSchemas: automaticQa ? new Dictionary<string, JsonElement> { ["plugin.controls"] = PluginControlFixtureSchema() } : null);
            if (automaticQa)
            {
                Check(fixture.Is("tool") && fixture.Command.Arguments.GetProperty("name").GetString() == "plugin.controls",
                    "own-run automatic QA starts with its required read-only observation instead of restored foreign evidence");
                var observed = new PluginControlSnapshot(qaMount, [], [], false);
                var observationReceipt = new string('7', 32);
                fixture.Tool(true, JsonSerializer.Serialize(new
                {
                    receipt = observationReceipt, presentationId = presentation, mountId = qaMount, operation = "inspect",
                    status = "completed", nativeStatus = "completed", sourceManifest = builtManifest,
                    packageFingerprint = new string('d', 64), observation = new PluginControlObservation(observed, observed, false)
                }, CerebralJson.Options), provider: "cerebral.plugin.controls", requestId: observationReceipt);
            }
            fixture.Describe("plugin.projects", "file.write", "process.run", "plugin.verify", "plugin.present");
            fixture.Catalog(exact: true, inputSchemas: new Dictionary<string, JsonElement>
            {
                ["plugin.projects"] = projectsSchema, ["file.write"] = basicSchema, ["process.run"] = basicSchema,
                ["plugin.verify"] = basicSchema, ["plugin.present"] = presentSchema
            });
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
            fixture.Tool(true, JsonSerializer.Serialize(new
            {
                desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
                count = 1, limited = false,
                projects = new[] { new { root, components = new[] { new { component = "desktop", source,
                    build = new { executableId = "dotnet", arguments = canonicalBuild, workingDirectory = directory, builtManifest } } } } }
            }), provider: "cerebral.plugin-sdk");
            return fixture;
        }
        void BuildAndVerify(Fixture fixture)
        {
            fixture.Model(new { action = "tool", name = "process.run", arguments = new
                { executableId = "dotnet", arguments = canonicalBuild, workingDirectory = directory } });
            fixture.Tool(true, "Compiled.", exitCode: 0);
            fixture.Model(new { action = "tool", name = "plugin.verify", arguments = new { root } });
            fixture.Tool(true, JsonSerializer.Serialize(new { root, verified = true,
                desktop = new { manifest = builtManifest, packageFingerprint = hash } }), provider: "cerebral.plugin-sdk");
        }
        void Present(Fixture fixture, string? fingerprint = null, bool withIdentity = true, string? nextPresentation = null)
        {
            fixture.Model(new { action = "tool", name = "plugin.present", arguments = new
                { path = builtManifest, expectedPackageFingerprint = hash } });
            fixture.Tool(true, JsonSerializer.Serialize(new { requestId = withIdentity ? nextPresentation ?? presentation : null,
                status = "pending", packageFingerprint = fingerprint }), provider: "cerebral.interface",
                requestId: withIdentity ? nextPresentation ?? presentation : null);
        }
        var current = Bound();
        Check(current.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Object,
            "complete exact publisher inventory establishes the selected component binding");
        Check(current.Is("generate") && !current.SystemText.Contains(marker, StringComparison.Ordinal),
            "exact source inspection alone does not assert a delivery checkpoint");
        BuildAndVerify(current);
        Check(current.Is("generate") && !current.SystemText.Contains(marker, StringComparison.Ordinal),
            "current build and verification without presentation do not assert a delivery checkpoint");
        Present(current, hash);
        Check(current.Is("generate") && current.SystemText.Contains(marker, StringComparison.Ordinal),
            "matching current selected component proof adds lifecycle guidance to system policy without auto-finalizing");
        Check(current.SystemText.Contains("Source self-review", StringComparison.Ordinal) &&
            current.SystemText.Contains("for this generation belongs before the build/verify/present sequence; that delivery sequence", StringComparison.Ordinal) &&
            current.SystemText.Contains("is now complete", StringComparison.Ordinal) &&
            current.SystemText.Contains("finish this turn with a truthful report", StringComparison.Ordinal) &&
            current.SystemText.Contains("do not claim approval, installation or functional testing", StringComparison.Ordinal),
            "policy places source review before delivery and bounds the final claim to pending owner review");
        Check(current.SystemText.Contains("Continue other requested artifacts or remaining deliverables", StringComparison.Ordinal) &&
            current.SystemText.Contains("concrete defect found in source, a new failure receipt or an owner-requested change", StringComparison.Ordinal),
            "policy preserves other deliverables and evidence-driven or owner-requested repair");
        var delivered = current.Fork();
        delivered.Model(new { action = "final", text = "Built, verified and submitted for separate owner review; functional testing remains pending." });
        Check(delivered.Step.Next is CompleteWorkflow { Success: true },
            "the ordinary model still chooses a truthful final through the existing completion checks");

        var concreteRepair = current.Fork();
        concreteRepair.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "concrete repair" } });
        Check(concreteRepair.Is("tool") && concreteRepair.Command.Arguments.GetProperty("name").GetString() == "file.write",
            "checkpoint guidance does not block an actual source correction");
        concreteRepair.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('b', 64), bytes = 15 }));
        Check(concreteRepair.Is("generate") && !concreteRepair.SystemText.Contains(marker, StringComparison.Ordinal),
            "a real source mutation removes the current-generation checkpoint guidance");
        concreteRepair.Model(new { action = "final", text = "Fixed." });
        Check(concreteRepair.Is("generate") && concreteRepair.LastContributionText.Contains("plugin.verify", StringComparison.Ordinal),
            "newly changed source still needs current verification and presentation");

        var failedVerification = current.Fork();
        failedVerification.Model(new { action = "tool", name = "plugin.verify", arguments = new { root } });
        failedVerification.Tool(false, "A newly observed verification failure.");
        Check(failedVerification.Is("generate") && !failedVerification.SystemText.Contains(marker, StringComparison.Ordinal),
            "a new failure invalidates checkpoint guidance and preserves ordinary recovery");

        var ownerChange = current.Fork();
        ownerChange.Steer("Also correct the newly identified source defect.");
        ownerChange.Model(new { action = "final", text = "Prior result." });
        Check(ownerChange.Is("generate") && ownerChange.ModelText.Contains("newly identified source defect", StringComparison.Ordinal),
            "accepted owner steering remains exact input instead of being truncated by the checkpoint");
        ownerChange.Model(new { action = "tool", name = "file.write", arguments = new { path = source, content = "owner correction" } });
        Check(ownerChange.Is("tool"), "source work requested by the owner remains dispatchable after presentation");

        var multiArtifact = Bound(otherDeliverable: true);
        BuildAndVerify(multiArtifact);
        Present(multiArtifact, hash);
        Check(multiArtifact.Is("generate") && multiArtifact.SystemText.Contains(marker, StringComparison.Ordinal),
            "a component checkpoint preserves model continuation with other requested work outstanding");
        multiArtifact.Model(new { action = "final", text = "Done." });
        Check(multiArtifact.Is("generate") && multiArtifact.LastContributionText.Contains("file.write", StringComparison.Ordinal),
            "unfinished independent deliverables still prevent finalization");
        multiArtifact.Model(new { action = "tool", name = "file.write", arguments = new
            { path = Path.Combine(Path.GetDirectoryName(root)!, "other-requested-artifact.txt"), content = "other deliverable" } });
        Check(multiArtifact.Is("tool"), "another requested artifact remains eligible without changing the delivered component");

        foreach (var (fingerprint, identity) in new[] { ((string?)null, true), (new string('c', 64), true), (hash, false) })
        {
            var incomplete = Bound();
            BuildAndVerify(incomplete);
            Present(incomplete, fingerprint, identity);
            Check(incomplete.Is("generate") && !incomplete.SystemText.Contains(marker, StringComparison.Ordinal),
                "missing or mismatched package identity cannot activate checkpoint guidance");
        }

        var otherComponent = current.Fork();
        var otherRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralPresentationPolicySecond"));
        var otherDirectory = Path.Combine(otherRoot, "Desktop");
        var otherProject = Path.Combine(otherDirectory, "Cerebral.GeneratedPlugin.Desktop.csproj");
        otherComponent.Model(new { action = "tool", name = "plugin.projects", arguments = new { root = otherRoot } });
        otherComponent.Tool(true, JsonSerializer.Serialize(new
        {
            desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
            count = 1, limited = false,
            projects = new[] { new { root = otherRoot, components = new[] { new { component = "desktop",
                source = Path.Combine(otherDirectory, "GeneratedDesktopPlugin.cs"), build = new
                {
                    executableId = "dotnet", arguments = new[] { "build", otherProject, "--configuration", "Release",
                        "--disable-build-servers", "--verbosity", "minimal" }, workingDirectory = otherDirectory,
                    builtManifest = Path.Combine(otherDirectory, "bin", "Release", "net10.0-windows", "desktop-plugin.json")
                }
            } } } }
        }), provider: "cerebral.plugin-sdk");
        Check(otherComponent.Is("generate") && !otherComponent.SystemText.Contains(marker, StringComparison.Ordinal),
            "selecting another exact component cannot reuse the previous component's delivery checkpoint");

        var forgedReceipt = current.Fork();
        var altered = JsonNode.Parse(forgedReceipt.Step.State.GetRawText())!.AsObject();
        var buildEffect = altered["pluginWork"]!["buildEffectId"]!.GetValue<string>();
        foreach (var receipt in altered["toolReceipts"]!.AsArray())
            if (receipt!["effectId"]!.GetValue<string>() == buildEffect) receipt["outcome"] = "Failed";
        forgedReceipt.RestoreState(CerebralJson.Element(altered));
        forgedReceipt.Describe("file.read");
        forgedReceipt.Catalog(exact: true, inputSchema: basicSchema);
        Check(forgedReceipt.Is("generate") && !forgedReceipt.SystemText.Contains(marker, StringComparison.Ordinal),
            "generation fields without a matching successful build receipt do not promote host lifecycle guidance");

        var foreignAutomatic = new Fixture(warm: false, requestId: "plugin-tests-" + presentation,
            input: new { requiredSuccessfulTools = new[] { "interface.status", "plugin.controls" } },
            hostAddedRequiredTools: ["interface.status", "plugin.controls"],
            caller: new EventActor(EventActorKind.Service, "cerebral.plugin-tests", "QA"));
        foreignAutomatic.RestoreStep(current.Step);
        foreignAutomatic.Describe("file.read");
        Check(foreignAutomatic.Step.Next is CompleteWorkflow { Success: false } &&
            foreignAutomatic.Summary.Contains("failed-build recovery", StringComparison.Ordinal) &&
            foreignAutomatic.Summary.Contains("another run", StringComparison.Ordinal) &&
            foreignAutomatic.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 &&
            !foreignAutomatic.Step.State.TryGetProperty("admissionRejections", out _),
            "automatic QA rejects a foreign ordinary-run snapshot before any catalog or tool dispatch");

        // Build the QA proof in its own accepted run. One outstanding original obligation keeps
        // its model view available after replacement delivery without bypassing automatic handoff.
        var automatic = Bound(automaticQa: true);
        automatic.Select("file.read", basicSchema);
        automatic.Model(new { action = "tool", name = "file.read", arguments = new { path = source } });
        automatic.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('b', 64),
            startLine = 1, totalLines = 1, content = "old source" }));
        automatic.Model(new { action = "tool", name = "file.write", arguments = new
            { path = source, content = "qa repair", collision = "replace", expectedSha256 = new string('b', 64) } });
        automatic.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('c', 64), bytes = 9 }));
        BuildAndVerify(automatic);
        Present(automatic, hash, nextPresentation: pendingPresentation);
        Check(automatic.Is("generate") && !automatic.SystemText.Contains(marker, StringComparison.Ordinal) &&
            automatic.Step.State.GetProperty("readRecovery").GetProperty("runId").GetString() == "plugin-tests-" + presentation &&
            automatic.Step.State.GetProperty("failedBuildRecovery").GetProperty("runId").GetString() == "plugin-tests-" + presentation &&
            automatic.Step.State.GetProperty("failedBuildRecovery").GetProperty("failures").GetArrayLength() == 0 &&
            automatic.Step.State.GetProperty("pluginWork").GetProperty("presentRequestId").GetString() == pendingPresentation,
            "automatic QA uses its existing host-owned lifecycle instead of ordinary checkpoint guidance");
        const string metadataHeader = "Accepted metadata and host evidence:\n";
        var metadataOffset = automatic.MetadataText.IndexOf(metadataHeader, StringComparison.Ordinal);
        Check(metadataOffset >= 0, "the QA generation retains its host metadata view");
        using var metadata = JsonDocument.Parse(automatic.MetadataText[(metadataOffset + metadataHeader.Length)..]);
        var qaHandoff = metadata.RootElement.GetProperty("replacementAwaitingOwnerReview");
        Check(qaHandoff.GetProperty("state").GetString() == "pending-owner-review" &&
            qaHandoff.GetProperty("requestId").GetString() == pendingPresentation &&
            qaHandoff.GetProperty("reviewedPresentationId").GetString() == presentation &&
            !qaHandoff.GetProperty("approved").GetBoolean() && !qaHandoff.GetProperty("installed").GetBoolean() &&
            !qaHandoff.GetProperty("functionallyTested").GetBoolean(),
            "same-run QA exposes its exact pending replacement marker without claiming approval, installation or functional testing");
        Check(metadata.RootElement.GetProperty("missingSuccessfulTools").EnumerateArray()
            .Any(tool => tool.GetString() == "fixture.pending"),
            "the outstanding original obligation preserves continuation through the existing handoff rules");
        return checks;
    }
}
