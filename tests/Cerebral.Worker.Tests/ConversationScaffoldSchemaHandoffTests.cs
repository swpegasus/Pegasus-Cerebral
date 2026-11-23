using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static int RunScaffoldSchemaHandoffChecks()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("Scaffold schema handoff: " + reason);
            checks++;
        }

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-scaffold-schema-handoff"));
        var desktop = Path.Combine(root, "Desktop");
        var project = Path.Combine(desktop, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var source = Path.Combine(desktop, "GeneratedDesktopPlugin.cs");
        var sourceManifest = Path.Combine(desktop, "desktop-plugin.json");
        var builtManifest = Path.Combine(desktop, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var projectsSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new
            {
                root = new { type = "string" },
                targetPath = new { type = "string" }
            },
            additionalProperties = false
        });
        var scaffoldSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new
            {
                targetDirectory = new { type = "string" },
                pluginId = new { type = "string" },
                name = new { type = "string" },
                component = new { type = "string", @enum = new[] { "desktop", "service", "cross-surface" } }
            },
            required = new[] { "targetDirectory", "pluginId", "name", "component" },
            additionalProperties = false
        });
        var related = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["plugin.scaffold"] = ["plugin.contract"]
        };

        string Inventory(bool ready = true, bool limited = false, bool summary = true)
        {
            if (summary)
                return JsonSerializer.Serialize(new
                {
                    projects = new[] { new { root, components = new[] { new { component = "desktop" } } } },
                    count = 1,
                    scannedDirectories = 1,
                    limited,
                    discoverySummaryOnly = true,
                    scaffoldReady = ready
                }, CerebralJson.Options);
            return JsonSerializer.Serialize(new
            {
                projects = new[]
                {
                    new
                    {
                        root,
                        id = "fixture.scaffold-handoff",
                        name = "Scaffold Handoff",
                        version = "1.0.0",
                        error = (string?)null,
                        components = new[]
                        {
                            new
                            {
                                component = "desktop",
                                source,
                                manifest = sourceManifest,
                                id = "fixture.scaffold-handoff",
                                name = "Scaffold Handoff",
                                version = "1.0.0",
                                error = (string?)null,
                                build = new
                                {
                                    executableId = "dotnet",
                                    arguments = new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" },
                                    workingDirectory = desktop,
                                    builtManifest
                                }
                            }
                        }
                    }
                },
                count = 1,
                scannedDirectories = 1,
                limited,
                discoverySummaryOnly = false,
                scaffoldReady = ready,
                desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId
            }, CerebralJson.Options);
        }

        Fixture Create(bool advertiseScaffold = true, string requestId = "ordinary-scaffold-handoff",
            object? input = null, EventActor? caller = null)
        {
            var fixture = new Fixture(warm: false, input: input, requestId: requestId, caller: caller);
            fixture.History([Past("scaffold-handoff-history", 70, "Earlier reducible authoring context.")]);
            var names = advertiseScaffold
                ? new[] { "plugin.projects", "plugin.scaffold", "plugin.contract" }
                : new[] { "plugin.projects", "plugin.contract" };
            fixture.Catalog(forcedNames: names, prerequisites: related);
            if (fixture.Is("catalog"))
                fixture.Catalog(exact: true, prerequisites: related);
            fixture.Select("plugin.projects", projectsSchema);
            return fixture;
        }

        static bool Selected(Fixture fixture, string name) => fixture.Step.State.GetProperty("selected")
            .EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == name);

        var ordinary = Create();
        var descriptionsBefore = ordinary.Step.State.GetProperty("descriptions").GetInt32();
        ordinary.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
        ordinary.Tool(true, Inventory(), provider: "cerebral.plugin-sdk");
        Check(ordinary.Is("catalog") && ordinary.Command.Arguments.GetProperty("names").EnumerateArray()
                .Select(item => item.GetString()).SequenceEqual(new[] { "plugin.scaffold" }),
            "a complete correlated broad inventory prepares exactly the advertised scaffold schema once");
        Check(ordinary.Step.State.GetProperty("descriptions").GetInt32() == descriptionsBefore + 1 &&
              ordinary.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1 &&
              !Selected(ordinary, "plugin.scaffold"),
            "the handoff spends one ordinary schema lookup and creates no scaffold effect or success credit");
        ordinary.Catalog(exact: true, prerequisites: related, inputSchema: scaffoldSchema);
        Check(ordinary.Is("generate") && Selected(ordinary, "plugin.scaffold") &&
              !Selected(ordinary, "plugin.contract") &&
              ordinary.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "prepareRelated false retains only the exact scaffold contract and no semantic receipt");

        var exactAndCompact = ordinary.Fork();
        var broadEffect = exactAndCompact.Step.State.GetProperty("scaffoldReadiness").GetProperty("effectId").GetString();
        exactAndCompact.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
        exactAndCompact.Tool(true, Inventory(summary: false), provider: "cerebral.plugin-sdk");
        Check(exactAndCompact.Is("generate") && Selected(exactAndCompact, "plugin.scaffold") &&
              exactAndCompact.Step.State.GetProperty("scaffoldReadiness").GetProperty("effectId").GetString() == broadEffect,
            "an exact reference inspection preserves the selected schema without starting another handoff");
        var restored = exactAndCompact.Fork();
        restored.RestoreState(JsonDocument.Parse(exactAndCompact.Step.State.GetRawText()).RootElement.Clone());
        restored.RejectContext(ContextProjectionStatus.NeedsCompaction);
        Check(restored.Is("compact"), "the fixture reaches real compaction with its earlier reducible context");
        restored.CompactSuccess();
        Check(restored.Is("generate") && Selected(restored, "plugin.scaffold") &&
              restored.Step.State.GetProperty("scaffoldReadiness").GetProperty("effectId").GetString() == broadEffect,
            "serialization and compaction retain the exact selected schema without preparing it again");

        var alreadySelected = Create();
        alreadySelected.Select("plugin.scaffold", scaffoldSchema);
        alreadySelected.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
        alreadySelected.Tool(true, Inventory(), provider: "cerebral.plugin-sdk");
        Check(alreadySelected.Is("generate") && Selected(alreadySelected, "plugin.scaffold") &&
              alreadySelected.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "an already selected scaffold schema suppresses host preparation while readiness still becomes current");

        var qaPresentation = "123456781234123412341234567890ab";
        var serviceQa = Create(requestId: "plugin-tests-" + qaPresentation,
            input: new { requiredSuccessfulTools = new[] { "interface.status", "plugin.controls" } },
            caller: new EventActor(EventActorKind.Service, "cerebral.plugin-tests"));
        serviceQa.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
        serviceQa.Tool(true, Inventory(), provider: "cerebral.plugin-sdk");
        Check(!serviceQa.Is("catalog") && !Selected(serviceQa, "plugin.scaffold") &&
              serviceQa.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean(),
            "the exact service-owned automatic QA lineage retains readiness evidence but receives no new-scaffold schema handoff");

        var qaLookingUser = Create(requestId: "plugin-tests-abcdefabcdefabcdefabcdefabcdefab");
        qaLookingUser.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
        qaLookingUser.Tool(true, Inventory(), provider: "cerebral.plugin-sdk");
        Check(qaLookingUser.Is("catalog"),
            "a user-owned run with a QA-looking ID remains ordinary and receives the one contract preparation");

        var invalidCases = new (string Label, bool Advertise, Action<Fixture> Complete)[]
        {
            ("failed", true, fixture => fixture.Tool(false, Inventory(), provider: "cerebral.plugin-sdk")),
            ("truncated", true, fixture => fixture.Tool(true, Inventory(), provider: "cerebral.plugin-sdk", truncated: true)),
            ("partial", true, fixture => fixture.Tool(true, Inventory(), provider: "cerebral.plugin-sdk", partial: true)),
            ("limited", true, fixture => fixture.Tool(true, Inventory(limited: true), provider: "cerebral.plugin-sdk")),
            ("provider readiness false", true, fixture => fixture.Tool(true, Inventory(ready: false), provider: "cerebral.plugin-sdk")),
            ("malformed inventory", true, fixture => fixture.Tool(true, "{}", provider: "cerebral.plugin-sdk")),
            ("foreign provider", true, fixture => fixture.Tool(true, Inventory(), provider: "other-provider")),
            ("foreign provider request", true, fixture => fixture.Tool(true, Inventory(), provider: "cerebral.plugin-sdk", requestId: "foreign-effect")),
            ("unadvertised or grant-filtered scaffold", false, fixture => fixture.Tool(true, Inventory(), provider: "cerebral.plugin-sdk"))
        };
        foreach (var invalidCase in invalidCases)
        {
            var fixture = Create(invalidCase.Advertise, requestId: "scaffold-negative-" + checks);
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
            invalidCase.Complete(fixture);
            Check(!fixture.Is("catalog") && !Selected(fixture, "plugin.scaffold"),
                invalidCase.Label + " does not initiate host schema preparation");
        }

        var catalogFailure = Create(requestId: "scaffold-catalog-failure");
        catalogFailure.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
        catalogFailure.Tool(true, Inventory(), provider: "cerebral.plugin-sdk");
        var failureReceipts = catalogFailure.Step.State.GetProperty("toolReceipts").GetArrayLength();
        var failureDescriptions = catalogFailure.Step.State.GetProperty("descriptions").GetInt32();
        catalogFailure.CatalogFailure("Exact scaffold schema lookup was unavailable.");
        Check(catalogFailure.Is("generate") && !Selected(catalogFailure, "plugin.scaffold") &&
              catalogFailure.Step.State.GetProperty("toolReceipts").GetArrayLength() == failureReceipts &&
              catalogFailure.Step.State.GetProperty("descriptions").GetInt32() == failureDescriptions &&
              catalogFailure.LastContributionText.Contains("tool discovery failed", StringComparison.OrdinalIgnoreCase),
            "one failed host preparation returns to the model with its bounded catalog diagnostic and no retry or semantic credit");

        return checks;
    }
}
