using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static int RunScaffoldReadinessTargetChecks()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("Scaffold readiness exact-target transition: " + reason);
            checks++;
        }
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-readiness-transition-" + Guid.NewGuid().ToString("N")));
        var projectRoot = Path.Combine(root, "opaque-project");
        var component = Path.Combine(projectRoot, "Desktop");
        var source = Path.Combine(component, "GeneratedDesktopPlugin.cs");
        var project = Path.Combine(component, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var manifest = Path.Combine(component, "desktop-plugin.json");
        var builtManifest = Path.Combine(component, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var ordinary = Path.Combine(root, "ordinary.txt");
        var projectsSchema = CerebralJson.Element(new { type = "object", properties = new
        { root = new { type = "string" }, targetPath = new { type = "string" } }, additionalProperties = false });
        var scaffoldSchema = CerebralJson.Element(new { type = "object", properties = new
        {
            targetDirectory = new { type = "string" }, pluginId = new { type = "string" }, name = new { type = "string" },
            component = new { type = "string", @enum = new[] { "desktop", "service", "cross-surface" } }
        }, required = new[] { "targetDirectory", "pluginId", "name", "component" }, additionalProperties = false });
        var verifySchema = CerebralJson.Element(new { type = "object", properties = new { root = new { type = "string" } },
            required = new[] { "root" }, additionalProperties = false });
        string Inspection(string target, bool matched) => matched
            ? JsonSerializer.Serialize(new
            {
                targetPath = target, targetMatched = true, targetComponent = "desktop",
                discoverySummaryOnly = false, scaffoldReady = false, limited = false, count = 1,
                projects = new[] { new { root = projectRoot, components = new[] { new
                {
                    component = "desktop", source, manifest, build = new
                    {
                        executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release",
                            "--disable-build-servers", "--verbosity", "minimal" }, workingDirectory = component, builtManifest
                    }
                } } } }
            }, CerebralJson.Options)
            : JsonSerializer.Serialize(new { targetPath = target, targetMatched = false }, CerebralJson.Options);
        Fixture Create(string readiness)
        {
            var fixture = new Fixture(warm: false, input: new { workingDirectory = root });
            fixture.History([]);
            fixture.Catalog(forcedNames: ["plugin.projects", "plugin.scaffold", "plugin.verify"]);
            fixture.Select("plugin.projects", projectsSchema);
            fixture.Select("plugin.scaffold", scaffoldSchema);
            if (readiness == "denied")
            {
                fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { root = Path.Combine(Path.GetDirectoryName(root)!, "outside-scope") } });
                fixture.Tool(false, "Project root is outside allowed roots.", outcome: ToolExecutionOutcome.Rejected, provider: "cerebral.plugin-sdk");
            }
            else if (readiness == "admitted")
            {
                fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
                fixture.Tool(true, JsonSerializer.Serialize(new
                {
                    discoverySummaryOnly = true, scaffoldReady = true, limited = false, count = 1,
                    projects = new[] { new { root = projectRoot, components = new[] { new { component = "desktop" } } } }
                }, CerebralJson.Options), provider: "cerebral.plugin-sdk");
            }
            return fixture;
        }
        static bool OffersScaffold(Fixture fixture) => fixture.Command.Arguments.GetProperty("responseSchema")
            .GetProperty("oneOf").EnumerateArray().Any(variant =>
            {
                if (!variant.GetProperty("properties").TryGetProperty("name", out var name)) return false;
                return name.TryGetProperty("const", out var exact) && exact.GetString() == "plugin.scaffold" ||
                    name.TryGetProperty("enum", out var names) && names.EnumerateArray().Any(item => item.GetString() == "plugin.scaffold");
            });
        void Lookup(Fixture fixture, string target, bool matched)
        {
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { targetPath = target } });
            fixture.Tool(true, Inspection(target, matched), provider: "cerebral.plugin-sdk");
        }
        void RecoverThroughSchema(Fixture fixture)
        {
            fixture.RestoreState(JsonDocument.Parse(fixture.Step.State.GetRawText()).RootElement.Clone());
            fixture.Describe("plugin.verify");
            if (fixture.Is("catalog")) fixture.Catalog(exact: true, inputSchema: verifySchema);
        }
        Fixture? deniedMatched = null;
        foreach (var matched in new[] { true, false })
        {
            var target = matched ? builtManifest : ordinary;
            var denied = Create("denied");
            var rejected = denied.Step.State.GetProperty("toolReceipts").EnumerateArray().Last().Clone();
            var rejectedId = rejected.GetProperty("effectId").GetString();
            var deniedReason = denied.Step.State.GetProperty("scaffoldReadiness").GetProperty("reason").GetString();
            Lookup(denied, target, matched);
            var current = denied.Step.State.GetProperty("toolReceipts").EnumerateArray().Last();
            var readiness = denied.Step.State.GetProperty("scaffoldReadiness");
            Check(!readiness.GetProperty("ready").GetBoolean() && readiness.GetProperty("effectId").GetString() == current.GetProperty("effectId").GetString() &&
                readiness.GetProperty("effectId").GetString() != rejectedId && readiness.GetProperty("reason").GetString() == deniedReason &&
                current.GetProperty("inventoryKind").GetString() == "exactReadOnly",
                (matched ? "matched" : "ordinary nonmatching") + " exact lookup advances only the existing denied latest-receipt anchor");
            Check(denied.Step.State.GetProperty("toolReceipts").EnumerateArray().Any(item =>
                    item.GetProperty("effectId").GetString() == rejectedId && JsonElement.DeepEquals(item, rejected)) &&
                denied.Step.State.GetProperty("admissionRejections").GetArrayLength() == 1,
                "the earlier rejected inventory and its recovery ledger entry remain immutable");
            RecoverThroughSchema(denied);
            Check(denied.Is("generate") && !OffersScaffold(denied) &&
                (matched ? denied.Step.State.GetProperty("pluginWork").GetProperty("build").GetProperty("root").GetString() == projectRoot :
                    denied.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Null),
                "serialized exact lookup survives the next successful schema effect with correct binding and no scaffold permission");
            var describedScaffold = denied.Fork();
            var receiptsBeforeDescription = denied.Step.State.GetProperty("toolReceipts").GetArrayLength();
            describedScaffold.Describe("plugin.scaffold");
            Check(describedScaffold.Is("generate") && !OffersScaffold(describedScaffold) &&
                describedScaffold.Step.State.GetProperty("toolReceipts").GetArrayLength() == receiptsBeforeDescription &&
                !describedScaffold.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean(),
                "cached scaffold schema cannot mint admission or an executable scaffold choice from a recovered denial");
            var cancelled = denied.Fork();
            cancelled.Cancel();
            Check(cancelled.Step.Next is CompleteWorkflow { Success: false } && cancelled.Summary.Contains("cancel", StringComparison.OrdinalIgnoreCase),
                "the repaired saved state preserves cancellation instead of failing restoration");
            if (matched) deniedMatched = denied;
            var absent = Create("absent");
            Lookup(absent, target, matched);
            RecoverThroughSchema(absent);
            Check(absent.Is("generate") && absent.Step.State.GetProperty("scaffoldReadiness").ValueKind == JsonValueKind.Null && !OffersScaffold(absent),
                "exact lookup leaves absent readiness absent through serialized recovery");
            var admitted = Create("admitted");
            var broadId = admitted.Step.State.GetProperty("scaffoldReadiness").GetProperty("effectId").GetString();
            Lookup(admitted, target, matched);
            RecoverThroughSchema(admitted);
            Check(admitted.Is("generate") && OffersScaffold(admitted) &&
                admitted.Step.State.GetProperty("scaffoldReadiness").GetProperty("effectId").GetString() == broadId,
                "classified broad admission retains its original independent anchor across exact lookup and serialization");
            var legacyAdmitted = Create("admitted");
            var legacyTrueState = JsonNode.Parse(legacyAdmitted.Step.State.GetRawText())!.AsObject();
            foreach (var receipt in legacyTrueState["toolReceipts"]!.AsArray()) receipt!.AsObject().Remove("inventoryKind");
            legacyAdmitted.RestoreState(CerebralJson.Element(legacyTrueState));
            legacyAdmitted.Describe("plugin.scaffold");
            Check(legacyAdmitted.Is("generate") && OffersScaffold(legacyAdmitted),
                "legacy true readiness retains its existing precise latest-anchor compatibility before any exact read");
            var legacyBroadId = legacyAdmitted.Step.State.GetProperty("scaffoldReadiness").GetProperty("effectId").GetString();
            Lookup(legacyAdmitted, target, matched);
            var legacyExactId = legacyAdmitted.Step.State.GetProperty("toolReceipts").EnumerateArray().Last().GetProperty("effectId").GetString();
            Check(legacyAdmitted.Is("generate") && !OffersScaffold(legacyAdmitted) &&
                !legacyAdmitted.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean(),
                "a matched or nonmatching exact read removes the cached executable scaffold choice from unclassified legacy true admission immediately");
            RecoverThroughSchema(legacyAdmitted);
            Check(legacyAdmitted.Is("generate") && !OffersScaffold(legacyAdmitted) &&
                !legacyAdmitted.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean() &&
                legacyAdmitted.Step.State.GetProperty("scaffoldReadiness").GetProperty("effectId").GetString() == legacyExactId && legacyExactId != legacyBroadId,
                "legacy true admission becomes a current exact-read denial and remains recoverable through the next serialized schema effect");
        }
        foreach (var mutation in new[] { "true-from-exact", "stale-false-anchor", "missing-anchor", "duplicate-anchor" })
        {
            var forged = deniedMatched!.Fork();
            var saved = JsonNode.Parse(forged.Step.State.GetRawText())!.AsObject();
            var receipts = saved["toolReceipts"]!.AsArray();
            var marker = saved["scaffoldReadiness"]!.AsObject();
            if (mutation == "true-from-exact") marker["ready"] = true;
            if (mutation == "stale-false-anchor") marker["effectId"] = receipts.First()!["effectId"]!.GetValue<string>();
            if (mutation == "missing-anchor") marker["effectId"] = "missing-effect";
            if (mutation == "duplicate-anchor") receipts.Add(receipts.Last()!.DeepClone());
            forged.RestoreState(CerebralJson.Element(saved));
            forged.Describe("plugin.scaffold");
            Check(forged.Step.Next is CompleteWorkflow { Success: false } && forged.Summary.Contains("receipt chain", StringComparison.Ordinal),
                mutation + " remains fail-closed under unchanged readiness validation");
        }
        var legacy = Create("denied");
        var legacyState = JsonNode.Parse(legacy.Step.State.GetRawText())!.AsObject();
        foreach (var receipt in legacyState["toolReceipts"]!.AsArray()) receipt!.AsObject().Remove("inventoryKind");
        legacy.RestoreState(CerebralJson.Element(legacyState));
        Lookup(legacy, builtManifest, true);
        RecoverThroughSchema(legacy);
        Check(legacy.Is("generate") && !OffersScaffold(legacy) && !legacy.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean(),
            "legacy latest false readiness remains recoverable without gaining admission");
        foreach (var mode in new[] { "rejected", "partial", "truncated", "foreign-provider", "foreign-request", "malformed" })
        {
            var invalid = Create("admitted");
            invalid.Model(new { action = "tool", name = "plugin.projects", arguments = new { targetPath = builtManifest } });
            invalid.Tool(mode != "rejected", mode == "malformed" ? "{" : Inspection(builtManifest, true),
                outcome: mode == "rejected" ? ToolExecutionOutcome.Rejected : null, partial: mode == "partial", truncated: mode == "truncated",
                provider: mode == "foreign-provider" ? "foreign" : "cerebral.plugin-sdk", requestId: mode == "foreign-request" ? "foreign-effect" : null);
            RecoverThroughSchema(invalid);
            Check(invalid.Is("generate") && !OffersScaffold(invalid) && !invalid.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean(),
                mode + " exact inventory invalidates prior broad admission and remains serializable");
        }
        return checks;
    }
}
