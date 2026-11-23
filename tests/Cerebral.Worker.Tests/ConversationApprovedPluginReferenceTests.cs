using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunApprovedControlReferenceChecks(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ApprovedReferenceFixture");
        var directory = Path.Combine(root, "Desktop");
        var project = Path.Combine(directory, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var manifest = Path.Combine(directory, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var requestId = "123456781234123412341234567890ab";
        var fingerprint = new string('a', 64);
        var projectsSchema = CerebralJson.Element(new { type = "object", properties = new { root = new { type = "string" } } });
        JsonObject Payload()
        {
            var value = JsonSerializer.SerializeToNode(new
            {
                projects = new[] { new
                {
                    root, id = "fixture.plugin", name = "Fixture", version = "1.0.0",
                    components = new[] { new
                    {
                        component = "desktop", source = Path.Combine(directory, "GeneratedDesktopPlugin.cs"),
                        manifest = Path.Combine(directory, "desktop-plugin.json"), id = "fixture.plugin", name = "Fixture",
                        version = "1.0.0", error = (string?)null,
                        build = new { executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release",
                            "--disable-build-servers", "--verbosity", "minimal" }, workingDirectory = directory, builtManifest = manifest }
                    } }, error = (string?)null
                } },
                count = 1, scannedDirectories = 1, limited = false, discoverySummaryOnly = false, scaffoldReady = false,
                desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
                approvedControlReference = new
                {
                    schemaVersion = 1, requestId, status = "completed", sourceRoot = root,
                    sourceManifest = manifest, packageFingerprint = fingerprint, mountIds = new[] { "fixture.plugin.main" }
                }
            }, CerebralJson.Options)!.AsObject();
            return value;
        }
        Fixture Pending()
        {
            var fixture = new Fixture();
            fixture.Select("plugin.projects", projectsSchema);
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
            return fixture;
        }
        static bool HasReference(Fixture fixture) => fixture.Step.State.TryGetProperty("pluginWork", out var work) &&
            work.ValueKind == JsonValueKind.Object && work.TryGetProperty("approvedControlReference", out var reference) &&
            reference.ValueKind == JsonValueKind.Object;
        var valid = Pending();
        valid.Tool(true, Payload().ToJsonString(), provider: "cerebral.plugin-sdk");
        check(HasReference(valid), "exact successful host inventory retains the approved installation coordinate");
        const string marker = "\nAccepted metadata and host evidence:\n";
        using var metadata = JsonDocument.Parse(valid.MetadataText[(valid.MetadataText.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
        var anchor = metadata.RootElement.GetProperty("pluginWorkBinding").GetProperty("approvedControlReference");
        check(anchor.GetProperty("requestId").GetString() == requestId && anchor.GetProperty("referenceOnly").GetBoolean() &&
            !anchor.GetProperty("functionallyTested").GetBoolean(), "approved coordinate is bounded reference data and never a native pass");
        check(valid.Step.State.GetProperty("nativeControlCoverage").GetArrayLength() == 0 &&
            valid.Step.State.GetProperty("pluginWork").GetProperty("successfulBuildGeneration").ValueKind == JsonValueKind.Null &&
            valid.Step.State.GetProperty("pluginWork").GetProperty("verifiedGeneration").ValueKind == JsonValueKind.Null &&
            valid.Step.State.GetProperty("pluginWork").GetProperty("presentedGeneration").ValueKind == JsonValueKind.Null,
            "approved discovery does not mint build, verification, presentation or control completion evidence");
        var recovered = valid.Fork();
        using var recoveredDocument = JsonDocument.Parse(valid.Step.State.GetRawText());
        recovered.RestoreState(recoveredDocument.RootElement.Clone());
        recovered.Model(new { action = "describe", names = new[] { "file.read" } });
        check(HasReference(recovered) && recovered.Is("catalog"), "serialized recovery keeps only a receipt-correlated approved reference");

        foreach (var (label, change) in new (string, Action<JsonObject>)[]
        {
            ("wrong schema", value => value["schemaVersion"] = 2),
            ("pending", value => value["status"] = "pending"),
            ("foreign root", value => value["sourceRoot"] = Path.Combine(Path.GetTempPath(), "DifferentReferenceRoot")),
            ("foreign manifest", value => value["sourceManifest"] = Path.Combine(Path.GetTempPath(), "desktop-plugin.json")),
            ("invalid ID", value => value["requestId"] = new string('0', 32)),
            ("invalid fingerprint", value => value["packageFingerprint"] = new string('z', 64)),
            ("duplicate mounts", value => value["mountIds"] = new JsonArray("fixture.plugin.main", "fixture.plugin.main")),
            ("no mounts", value => value["mountIds"] = new JsonArray()),
            ("too many mounts", value => value["mountIds"] = new JsonArray(Enumerable.Range(0, Cerebral.Contracts.Workflows.WorkflowContract.MaximumPluginTestMounts + 1).Select(i => (JsonNode?)JsonValue.Create("fixture.plugin." + i)).ToArray())),
            ("unbounded mount", value => value["mountIds"] = new JsonArray(new string('a', 130))),
            ("unknown field", value => value["grantsControls"] = true)
        })
        {
            var fixture = Pending(); var payload = Payload(); change(payload["approvedControlReference"]!.AsObject());
            fixture.Tool(true, payload.ToJsonString(), provider: "cerebral.plugin-sdk");
            check(!HasReference(fixture), label + " reference is omitted without losing ordinary inventory");
        }
        foreach (var (label, deliver) in new (string, Action<Fixture>)[]
        {
            ("foreign provider", fixture => fixture.Tool(true, Payload().ToJsonString(), provider: "foreign")),
            ("failed", fixture => fixture.Tool(false, Payload().ToJsonString(), provider: "cerebral.plugin-sdk")),
            ("partial", fixture => fixture.Tool(true, Payload().ToJsonString(), partial: true, provider: "cerebral.plugin-sdk")),
            ("truncated", fixture => fixture.Tool(true, Payload().ToJsonString(), truncated: true, provider: "cerebral.plugin-sdk")),
            ("wrong receipt", fixture => fixture.Tool(true, Payload().ToJsonString(), requestId: "foreign-effect", provider: "cerebral.plugin-sdk")),
            ("no provider evidence", fixture => fixture.Tool(true, Payload().ToJsonString(), omitEvidence: true)),
            ("duplicate JSON", fixture => fixture.Tool(true, Payload().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"), provider: "cerebral.plugin-sdk"))
        })
        {
            var fixture = Pending(); deliver(fixture);
            check(!HasReference(fixture), label + " cannot contribute an approved reference");
        }
        var omitted = Pending(); var noReference = Payload(); noReference.Remove("approvedControlReference");
        omitted.Tool(true, noReference.ToJsonString(), provider: "cerebral.plugin-sdk");
        check(!HasReference(omitted), "older compatible inventories without an approval stay valid without invented coordinates");

        var forged = JsonNode.Parse(valid.Step.State.GetRawText())!.AsObject();
        forged["pluginWork"]!["approvedControlReference"]!["effectId"] = "missing-receipt";
        var forgedFixture = valid.Fork(); forgedFixture.RestoreState(CerebralJson.Element(forged));
        forgedFixture.Model(new { action = "describe", names = new[] { "file.read" } });
        check(forgedFixture.Step.Next is Cerebral.Contracts.Workflows.CompleteWorkflow { Success: false } &&
            forgedFixture.Summary.Contains("matching exact inspection receipt", StringComparison.Ordinal),
            "a recovered reference without its retained inspection receipt fails closed");

        var refresh = valid.Fork();
        refresh.Select("file.inspect", CerebralJson.Element(new { type = "object", properties = new { path = new { type = "string" } } }));
        refresh.Model(new { action = "tool", name = "file.inspect", arguments = new { path = Path.Combine(directory, "GeneratedDesktopPlugin.cs") } });
        refresh.Tool(true, "Observed source.");
        refresh.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
        refresh.Tool(true, noReference.ToJsonString(), provider: "cerebral.plugin-sdk");
        check(!HasReference(refresh), "a later exact inspection without the reference removes its earlier coordinate");

        var mutation = valid.Fork();
        mutation.Select("file.write", CerebralJson.Element(new { type = "object", properties = new
        {
            path = new { type = "string" }, content = new { type = "string" }, collision = new { type = "string" }
        } }));
        var newSource = Path.Combine(directory, "AdditionalSource.cs");
        mutation.Model(new { action = "tool", name = "file.write", arguments = new { path = newSource, content = "// changed", collision = "fail" } });
        mutation.Tool(true, JsonSerializer.Serialize(new { path = newSource, sha256 = new string('b', 64), bytes = 10, encoding = "utf8" }));
        check(!HasReference(mutation), "a source mutation cannot retain an approval for earlier package bytes");

        var process = valid.Fork();
        process.Select("process.run", CerebralJson.Element(new { type = "object", properties = new
        {
            executableId = new { type = "string" }, arguments = new { type = "array", items = new { type = "string" } },
            workingDirectory = new { type = "string" }
        } }));
        process.Model(new { action = "tool", name = "process.run", arguments = new
        {
            executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release",
                "--disable-build-servers", "--verbosity", "minimal" }, workingDirectory = directory
        } });
        process.Tool(true, "Built current source.", exitCode: 0);
        check(!HasReference(process), "a process that may change package bytes clears the approved lookup coordinate");
    }
}
