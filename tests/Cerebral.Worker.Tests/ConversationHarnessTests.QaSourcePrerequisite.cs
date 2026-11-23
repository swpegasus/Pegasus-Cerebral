using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static int QaSourcePrerequisiteChecks()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("QA source prerequisite: " + reason);
            checks++;
        }
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "qa-source-coordinate-fixture"));
        var directory = Path.Combine(root, "desktop");
        var source = Path.Combine(directory, "GeneratedDesktopPlugin.cs");
        var project = Path.Combine(directory, "Fixture.csproj");
        var manifest = Path.Combine(directory, "bin", "Release", "net8.0-windows", "desktop-plugin.json");
        var presentation = new string('a', 32);
        var coordinate = new { schemaVersion = 1, presentationId = presentation, root };
        var process = new { executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release",
            "--disable-build-servers", "--verbosity", "minimal" }, workingDirectory = directory };
        var processSchema = CerebralJson.Element(new { type = "object", properties = new
        {
            executableId = new { type = "string" }, arguments = new { type = "array", items = new { type = "string" } },
            workingDirectory = new { type = "string" }
        }, required = new[] { "executableId", "arguments", "workingDirectory" }, additionalProperties = false });
        var pathSchema = CerebralJson.Element(new { type = "object", properties = new { path = new { type = "string" } },
            required = new[] { "path" }, additionalProperties = false });
        var projectsSchema = CerebralJson.Element(new { type = "object", properties = new { root = new { type = "string" } },
            additionalProperties = false });
        Fixture Qa(object? metadata = null, bool useMetadata = true, string? id = null, EventActor? caller = null)
        {
            var fixture = new Fixture(warm: false,
                input: new { workingDirectory = root, requiredSuccessfulTools = new[] { "interface.status", "plugin.controls" } },
                requestId: id ?? "plugin-tests-" + presentation,
                hostAddedRequiredTools: ["interface.status", "plugin.controls"],
                caller: caller ?? new(EventActorKind.Service, "cerebral.plugin-tests"),
                pluginSource: useMetadata ? metadata ?? coordinate : null);
            fixture.History([Past("qa-source-history", 80, "Prior authorized request remains retained.")]);
            fixture.Catalog(forcedNames: ["plugin.projects", "file.read", "file.write", "file.edit", "process.run", "program.source.generate"]);
            if (fixture.Is("catalog")) fixture.Catalog(exact: true);
            return fixture;
        }
        JsonElement Anchors(Fixture fixture) => JsonDocument.Parse(fixture.MetadataText.Split(
            "Accepted metadata and host evidence:\n", StringSplitOptions.None).Last()).RootElement.Clone();
        string Inventory(string inventoryRoot, bool limited = false) => JsonSerializer.Serialize(new
        {
            count = 1, limited, discoverySummaryOnly = false, scaffoldReady = true,
            desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId,
            projects = new[] { new { root = inventoryRoot, components = new[] { new
            {
                component = "desktop", source, project, builtManifest = manifest,
                build = new { process.executableId, process.arguments, process.workingDirectory, builtManifest = manifest }
            } } } }
        }, CerebralJson.Options);
        void Inspect(Fixture fixture, string? content = null, string provider = "cerebral.plugin-sdk", bool truncated = false)
        {
            fixture.Select("plugin.projects", projectsSchema);
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
            Check(fixture.Is("tool"), "exact-root inventory remains an ordinary granted tool action");
            fixture.Tool(true, content ?? Inventory(root), provider: provider, truncated: truncated);
        }

        var fresh = Qa();
        Check(Anchors(fresh).GetProperty("approvedPluginSource").GetProperty("root").GetString() == root &&
            !Anchors(fresh).GetProperty("approvedPluginSource").GetProperty("inspected").GetBoolean() &&
            fresh.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Null &&
            fresh.Step.State.GetProperty("pluginBuildCandidates").GetArrayLength() == 0,
            "owner-review metadata supplies only an uninspected source coordinate with no mutable recipe or credit");
        foreach (var action in new object[]
        {
            new { action = "tool", name = "file.write", arguments = new { path = source, content = "attempt", collision = "fail" } },
            new { action = "tool", name = "file.edit", arguments = new { path = source, oldText = "old", newText = "new", expectedSha256 = new string('b', 64) } },
            new { action = "tool", name = "process.run", arguments = process },
            new { action = "tool", name = "program.source.generate", arguments = new { path = source, objective = "repair" } },
            new { action = "tool", name = "plugin.verify", arguments = new { root } },
            new { action = "tool", name = "plugin.present", arguments = new { path = manifest } }
        })
        {
            var blocked = fresh.Fork();
            blocked.Model(action);
            Check(blocked.Is("generate") && blocked.LastContributionText.Contains("current exact-root inventory", StringComparison.Ordinal) &&
                blocked.LastContributionText.Contains(JsonSerializer.Serialize(root), StringComparison.Ordinal) &&
                blocked.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
                "QA authoring/build is rejected before schema preparation or tool dispatch");
        }
        var read = fresh.Fork();
        read.Select("file.read", pathSchema);
        read.Model(new { action = "tool", name = "file.read", arguments = new { path = source } });
        Check(read.Is("tool"), "read-only diagnosis is allowed before source binding without implying authoring credit");

        var restored = fresh.Fork();
        restored.RejectContext(ContextProjectionStatus.NeedsCompaction);
        Check(restored.Is("compact"), "uninspected QA source coordinate supports normal compaction");
        restored.CompactSuccess();
        Check(Anchors(restored).GetProperty("approvedPluginSource").GetProperty("root").GetString() == root &&
            !Anchors(restored).GetProperty("approvedPluginSource").GetProperty("inspected").GetBoolean(),
            "compaction cannot erase or promote the host source-coordinate prerequisite");

        var prior = Qa(useMetadata: false);
        var oldState = JsonNode.Parse(prior.Step.State.GetRawText())!.AsObject();
        oldState.Remove("qaSourceCatalogChecked");
        oldState.Remove("qaSourceCatalogRefreshAttempted");
        oldState.Remove("pluginSource");
        prior.RestoreState(JsonDocument.Parse(oldState.ToJsonString(CerebralJson.Options)).RootElement.Clone());
        prior.Model(new { action = "tool", name = "process.run", arguments = process });
        Check(prior.Is("catalog") && prior.Command.Arguments.GetRawText() == "{}" &&
            prior.Step.State.GetProperty("deferredToolName").GetString() == "process.run",
            "an old admitted QA state refreshes the real host catalog once before its saved source/build action");
        // The upgraded host response supplies its retained owner coordinate; no accepted input is changed.
        var upgraded = prior.Fork();
        upgraded.Catalog(forcedNames: ["plugin.projects", "process.run"], sourceCoordinate: coordinate);
        Check(upgraded.Is("generate") && upgraded.LastContributionText.Contains("current exact-root inventory", StringComparison.Ordinal) &&
            upgraded.Step.State.GetProperty("pluginSource").GetProperty("root").GetString() == root &&
            upgraded.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "an upgraded old QA state revalidates the saved build and stops it before execution until exact inventory");
        prior.CatalogFailure("Bounded source lookup unavailable.");
        Check(prior.Is("generate"), "failed optional catalog refresh returns a retained diagnostic");
        prior.Model(new { action = "tool", name = "process.run", arguments = process });
        Check(prior.Is("generate") && prior.LastContributionText.Contains("source lookup failed", StringComparison.Ordinal),
            "a failed old-state refresh cannot allow authoring or repeat the lookup unchanged");

        var bound = Qa();
        Inspect(bound);
        Check(bound.Step.State.GetProperty("pluginSource").GetProperty("inspectionEffectId").ValueKind == JsonValueKind.String &&
            bound.Step.State.GetProperty("pluginWork").ValueKind == JsonValueKind.Object,
            "only actual successful exact-root SDK inventory establishes a current-run binding");
        bound.Select("process.run", processSchema);
        bound.Model(new { action = "tool", name = "process.run", arguments = process });
        Check(bound.Is("tool"), "the actual retained canonical build can execute after exact inspection");
        bound.Tool(false, "{\"exitCode\":1}", exitCode: 1);
        Check(bound.Step.State.GetProperty("pluginWork").GetProperty("lastCanonicalBuildEffectId").ValueKind == JsonValueKind.String &&
            bound.Step.State.GetProperty("pluginWork").GetProperty("successfulBuildGeneration").ValueKind == JsonValueKind.Null,
            "binding records the failed canonical build without claiming success");

        foreach (var invalid in new[] { Inventory(root, limited: true), "{\"projects\":[]}", "not json" })
        {
            var rejected = Qa();
            Inspect(rejected, invalid);
            rejected.Model(new { action = "tool", name = "process.run", arguments = process });
            Check(rejected.Is("generate") && rejected.LastContributionText.Contains("current exact-root inventory", StringComparison.Ordinal),
                "incomplete/malformed inventory cannot satisfy the prerequisite");
        }
        var migration = Qa(useMetadata: false);
        Inspect(migration);
        migration.Select("file.read", pathSchema);
        var unrelated = Path.Combine(Path.GetTempPath(), "qa-unrelated-reference.txt");
        migration.Model(new { action = "tool", name = "file.read", arguments = new { path = unrelated } });
        migration.Tool(true, "Unrelated retained reference.");
        var migrationState = JsonNode.Parse(migration.Step.State.GetRawText())!.AsObject();
        var priorInventory = migrationState["retainedFileReads"]!.AsObject().Count;
        migrationState.Remove("qaSourceCatalogChecked");
        migrationState.Remove("qaSourceCatalogRefreshAttempted");
        migrationState.Remove("pluginSource");
        migration.RestoreState(JsonDocument.Parse(migrationState.ToJsonString(CerebralJson.Options)).RootElement.Clone());
        migration.Model(new { action = "tool", name = "process.run", arguments = process });
        Check(migration.Is("catalog"), "old retained inventory still requires the private host coordinate lookup");
        migration.Catalog(forcedNames: ["plugin.projects", "process.run"], sourceCoordinate: coordinate);
        Check(migration.Is("generate") && migration.Step.State.GetProperty("retainedFileReads").EnumerateObject().Count() == priorInventory - 1,
            "migration clears only the old exact approved-root inventory cache");
        var beforeFreshInventory = migration.Step.State.GetProperty("toolReceipts").GetArrayLength();
        migration.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
        Check(migration.Is("tool") && migration.Step.State.GetProperty("toolReceipts").GetArrayLength() == beforeFreshInventory,
            "the required migrated exact inventory reaches a NEW real host tool effect instead of rejecting a retained read");
        migration.Tool(true, Inventory(root), provider: "cerebral.plugin-sdk");
        Check(migration.Step.State.GetProperty("pluginSource").GetProperty("inspectionEffectId").GetString() ==
            migration.Step.State.GetProperty("toolReceipts")[beforeFreshInventory].GetProperty("effectId").GetString(),
            "migration binds only the newly confirmed inventory effect");
        migration.Model(new { action = "tool", name = "file.read", arguments = new { path = unrelated } });
        Check(migration.Is("generate") && migration.LastContributionText.Contains("already has a retained result", StringComparison.OrdinalIgnoreCase),
            "unrelated retained file reads keep their ordinary no-duplicate behavior");

        var quota = Qa();
        var quotaState = JsonNode.Parse(quota.Step.State.GetRawText())!.AsObject();
        quotaState["descriptions"] = 16;
        quota.RestoreState(JsonDocument.Parse(quotaState.ToJsonString(CerebralJson.Options)).RootElement.Clone());
        quota.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
        Check(quota.Is("catalog") && quota.Command.Arguments.GetProperty("names").GetArrayLength() == 1 &&
            quota.Step.State.GetProperty("descriptions").GetInt32() == 16 &&
            quota.Step.State.GetProperty("qaSourceInventorySchemaLookupUsed").GetBoolean(),
            "exhausted old QA gets one narrow host-required inventory schema preparation without resetting its model quota");
        quota.Catalog(exact: true, inputSchema: projectsSchema);
        Check(quota.Is("tool"), "the prepared granted exact inventory continues through normal tool admission");
        quota.Tool(true, Inventory(root), provider: "cerebral.plugin-sdk");
        Check(quota.Step.State.GetProperty("pluginSource").GetProperty("inspectionEffectId").ValueKind == JsonValueKind.String,
            "the bounded schema preparation supplies no freshness until the real inventory receipt succeeds");

        var foreignProvider = Qa();
        Inspect(foreignProvider, provider: "foreign-provider");
        foreignProvider.Model(new { action = "tool", name = "process.run", arguments = process });
        Check(foreignProvider.Is("generate") && foreignProvider.LastContributionText.Contains("current exact-root inventory", StringComparison.Ordinal),
            "a copied inventory envelope from a foreign provider grants no QA source freshness");

        foreach (var bad in new object[]
        {
            new { schemaVersion = 2, presentationId = presentation, root },
            new { schemaVersion = 1, presentationId = new string('c', 32), root },
            new { schemaVersion = 1, presentationId = presentation, root = "relative" }
        })
        {
            var invalid = Qa(bad);
            Check(invalid.Step.Next is CompleteWorkflow { Success: false }, "invalid or foreign catalog source coordinates fail closed");
        }
        var noAddedRepair = new Fixture(warm: false,
            input: new { workingDirectory = root,
                requiredSuccessfulTools = new[] { "interface.status", "process.run", "plugin.verify", "plugin.present" } },
            requestId: "plugin-tests-" + presentation, caller: new(EventActorKind.Service, "cerebral.plugin-tests"),
            pluginSource: coordinate);
        noAddedRepair.History([]);
        noAddedRepair.Catalog(forcedNames: ["plugin.projects", "process.run", "plugin.verify", "plugin.present"]);
        if (noAddedRepair.Is("catalog")) noAddedRepair.Catalog(exact: true,
            inputSchemas: new Dictionary<string, JsonElement>
            {
                ["interface.status"] = CerebralJson.Element(new { type = "object", properties = new { requestId = new { type = "string" } },
                    required = new[] { "requestId" }, additionalProperties = false })
            });
        Check(noAddedRepair.Is("tool") && noAddedRepair.Command.Arguments.GetProperty("name").GetString() == "interface.status" &&
            JsonElement.DeepEquals(noAddedRepair.Command.Arguments.GetProperty("arguments"), CerebralJson.Element(new { requestId = presentation })),
            "zero-added service repair first reads its accepted exact review coordinate without guessing a mount");
        noAddedRepair.Tool(false, "This fixture has not supplied the failed native review yet.",
            provider: "cerebral.interface", requestId: presentation);
        Check(noAddedRepair.Is("generate") &&
            Anchors(noAddedRepair).GetProperty("approvedPluginSource").GetProperty("presentationId").GetString() == presentation &&
            Anchors(noAddedRepair).GetProperty("requiredSuccessfulTools").GetArrayLength() == 4,
            "zero-added repair-before-install obligations recognize exact service QA identity without changing original requirements");
        noAddedRepair.Model(new { action = "tool", name = "process.run", arguments = process });
        Check(noAddedRepair.Is("generate") && noAddedRepair.LastContributionText.Contains("current exact-root inventory", StringComparison.Ordinal),
            "zero-added repair QA must still obtain current exact-root inventory before source/build execution");
        var savedForeign = fresh.Fork();
        var foreignState = JsonNode.Parse(savedForeign.Step.State.GetRawText())!.AsObject();
        foreignState["pluginSource"]!["presentationId"] = new string('d', 32);
        savedForeign.RestoreState(JsonDocument.Parse(foreignState.ToJsonString(CerebralJson.Options)).RootElement.Clone());
        savedForeign.Model(new { action = "final", text = "Cannot accept foreign metadata." });
        Check(savedForeign.Step.Next is CompleteWorkflow { Success: false }, "a restored foreign owner coordinate is rejected before model action");
        var ordinary = new Fixture(input: new { workingDirectory = root });
        ordinary.Select("process.run", processSchema);
        ordinary.Model(new { action = "tool", name = "process.run", arguments = process });
        Check(ordinary.Is("tool"), "ordinary users retain scoped file/build operations without an automatic-QA prerequisite");
        return checks;
    }
}
