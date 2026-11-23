using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    internal static void VerifyPluginPhaseGuidance(Action<bool, string> check)
    {
        const string inspectionGuidance = "For approved-package inspection or testing, inspect current native state first";
        const string repairTrigger = "only after an observed defect or an owner-requested change.";
        const string authoringGuidance = "When creating or repairing source, build Release in this run";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralPhaseGuidance", "ReviewedPlugin"));
        var directory = Path.Combine(root, "Desktop");
        var project = Path.Combine(directory, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var sourcePath = Path.Combine(directory, "GeneratedDesktopPlugin.cs");
        var manifest = Path.Combine(directory, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var projectsSchema = CerebralJson.Element(new
        {
            type = "object", properties = new { root = new { type = "string" } }, additionalProperties = false
        });
        var readSchema = CerebralJson.Element(new
        {
            type = "object", properties = new { path = new { type = "string" } },
            required = new[] { "path" }, additionalProperties = false
        });
        var writeSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string" }, content = new { type = "string" },
                collision = new { type = "string", @enum = new[] { "fail", "skip", "replace" }, @default = "fail" },
                expectedSha256 = new { type = "string", minLength = 64, maxLength = 64 }
            },
            required = new[] { "path", "content" }, additionalProperties = false
        });
        var inventory = JsonSerializer.Serialize(new
        {
            count = 1, scannedDirectories = 1, limited = false,
            desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
            projects = new[]
            {
                new
                {
                    root, id = "reviewed.plugin", name = "Reviewed", version = "1.0.0",
                    components = new[]
                    {
                        new
                        {
                            component = "desktop", manifest = Path.Combine(directory, "desktop-plugin.json"),
                            source = sourcePath, id = "reviewed.plugin", name = "Reviewed", version = "1.0.0",
                            error = (string?)null,
                            build = new
                            {
                                executableId = "dotnet",
                                arguments = new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" },
                                workingDirectory = directory, builtManifest = manifest
                            }
                        }
                    },
                    error = (string?)null
                }
            }
        }, CerebralJson.Options);

        Fixture InspectedSource()
        {
            var fixture = new Fixture();
            fixture.Select("plugin.projects", projectsSchema);
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
            fixture.Tool(true, inventory);
            return fixture;
        }

        JsonElement Anchors(Fixture fixture)
        {
            const string header = "\nAccepted metadata and host evidence:\n";
            var text = fixture.MetadataText;
            using var document = JsonDocument.Parse(text[(text.IndexOf(header, StringComparison.Ordinal) + header.Length)..]);
            return document.RootElement.Clone();
        }

        var inspection = InspectedSource();
        check(inspection.Is("generate") && inspection.SystemText.Contains(inspectionGuidance, StringComparison.Ordinal) &&
              inspection.SystemText.Contains("reopen or invocation operations needed for the requested behavior", StringComparison.Ordinal) &&
              inspection.SystemText.Contains(repairTrigger, StringComparison.Ordinal),
            "read-only guidance preserves approved-package native inspection before optional source repair");
        check(inspection.SystemText.Contains(authoringGuidance, StringComparison.Ordinal) &&
              inspection.SystemText.Contains("test actual requested behavior after owner approval", StringComparison.Ordinal) &&
              !inspection.SystemText.Contains("\nBuild Release in this run", StringComparison.Ordinal),
            "read-only guidance conditions Release/build/verify/present on source creation or repair");
        check(inspection.SystemText.Contains("default-to-default is no proof", StringComparison.Ordinal) &&
              inspection.SystemText.Contains("Available controls are not tested controls", StringComparison.Ordinal),
            "native testing guidance requires behavioral and non-default persistence evidence rather than availability or unchanged defaults");
        check(inspection.SystemText.Contains("declare exact saved A still visible in the current observation in expectedTexts before the action", StringComparison.Ordinal) &&
              inspection.SystemText.Contains("Invoke the requested save operation while changed value A is current and confirm its actual result/errors before reopening", StringComparison.Ordinal) &&
              inspection.SystemText.Contains("native completion alone proves lifecycle delivery", StringComparison.Ordinal),
            "native persistence guidance confirms a real save then declares a bounded pre-action expectation instead of interpreting successful delivery as a test pass");
        check(inspection.SystemText.Contains("local helpers can shadow SDK helpers", StringComparison.Ordinal) &&
              inspection.SystemText.Contains("Never claim untested failure handling", StringComparison.Ordinal),
            "native testing guidance inspects contradictory helper behavior and preserves untested failure boundaries");
        check(inspection.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 0 &&
              inspection.Step.State.GetProperty("completionMilestones").EnumerateObject().Count() == 0 &&
              Anchors(inspection).GetProperty("missingSuccessfulTools").GetArrayLength() == 0,
            "inspection alone introduces no source-generation or authoring completion obligation");
        inspection.Model(new { action = "final", text = "The existing source coordinates were inspected without authoring." });
        check(inspection.Step.Next is CompleteWorkflow { Success: true },
            "the conditional guidance preserves successful read-only completion without a new build");

        var authoring = InspectedSource();
        var originalHash = new string('b', 64);
        authoring.Select("file.read", readSchema);
        authoring.Model(new { action = "tool", name = "file.read", arguments = new { path = sourcePath } });
        authoring.Tool(true, JsonSerializer.Serialize(new
        {
            path = sourcePath, sha256 = originalHash, startLine = 1, totalLines = 1, content = "Original reviewed source."
        }, CerebralJson.Options));
        authoring.Select("file.write", writeSchema);
        authoring.Model(new
        {
            action = "tool", name = "file.write",
            arguments = new { path = sourcePath, content = "Updated requested source.", collision = "replace", expectedSha256 = originalHash }
        });
        authoring.Tool(true, JsonSerializer.Serialize(new
        {
            path = sourcePath, sha256 = new string('c', 64), bytes = 25, lines = 1, words = 3
        }, CerebralJson.Options));
        check(authoring.Is("generate") && authoring.SystemText.Contains(authoringGuidance, StringComparison.Ordinal) &&
              authoring.SystemText.Contains(inspectionGuidance, StringComparison.Ordinal) &&
              authoring.SystemText.Contains(repairTrigger, StringComparison.Ordinal),
            "authoring guidance retains the conditional Release sequence and the approved-package inspection phase");
        var work = authoring.Step.State.GetProperty("pluginWork");
        check(work.GetProperty("sourceGeneration").GetInt32() == 1 &&
              work.GetProperty("successfulBuildGeneration").ValueKind == JsonValueKind.Null &&
              work.GetProperty("verifiedGeneration").ValueKind == JsonValueKind.Null &&
              work.GetProperty("presentedGeneration").ValueKind == JsonValueKind.Null,
            "a confirmed source mutation still requires build, verification, and presentation for its current generation");
        var anchors = Anchors(authoring);
        var required = anchors.GetProperty("requiredSuccessfulTools").EnumerateArray().Select(value => value.GetString()).ToArray();
        var missing = anchors.GetProperty("missingSuccessfulTools").EnumerateArray().Select(value => value.GetString()).ToArray();
        check(required.Contains("plugin.verify") && required.Contains("plugin.present") &&
              missing.Contains("plugin.verify") && missing.Contains("plugin.present") &&
              authoring.Step.State.GetProperty("completionMilestones").TryGetProperty("plugin.verify", out _) &&
              authoring.Step.State.GetProperty("completionMilestones").TryGetProperty("plugin.present", out _),
            "the policy wording preserves host-owned authoring milestones and their missing-evidence anchors");

        var receiptCount = authoring.Step.State.GetProperty("toolReceipts").GetArrayLength();
        var prematureVerification = authoring.Fork();
        prematureVerification.Model(new { action = "tool", name = "plugin.verify", arguments = new { root } });
        check(prematureVerification.Is("generate") &&
              prematureVerification.LastContributionText.Contains("no successful canonical Release build for generation 1", StringComparison.Ordinal) &&
              prematureVerification.Step.State.GetProperty("toolReceipts").GetArrayLength() == receiptCount,
            "inspection-phase guidance cannot bypass the current generation's canonical Release build guard");
        var prematurePresentation = authoring.Fork();
        prematurePresentation.Model(new { action = "tool", name = "plugin.present", arguments = new { path = manifest } });
        check(prematurePresentation.Is("generate") &&
              prematurePresentation.LastContributionText.Contains("has not been verified for current source generation 1", StringComparison.Ordinal) &&
              prematurePresentation.Step.State.GetProperty("toolReceipts").GetArrayLength() == receiptCount,
            "inspection-phase guidance cannot bypass current-generation verification before presentation");
        authoring.Model(new { action = "final", text = "The source repair is complete." });
        check(authoring.Is("generate") &&
              authoring.LastContributionText.Contains("plugin.verify", StringComparison.Ordinal) &&
              authoring.LastContributionText.Contains("plugin.present", StringComparison.Ordinal) &&
              authoring.Step.State.GetProperty("pluginWork").GetProperty("sourceGeneration").GetInt32() == 1 &&
              authoring.Step.State.GetProperty("toolReceipts").GetArrayLength() == receiptCount,
            "conditional model guidance leaves unfinished authoring obligations and generation evidence intact");
    }
}
