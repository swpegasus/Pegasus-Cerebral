using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunControlReferenceSchemaChecks(Action<bool, string> check)
    {
        const string presentation = "123456781234123412341234567890ab";
        const string otherPresentation = "abcdefabcdefabcdefabcdefabcdefab";
        var statusSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new
            {
                requestId = new
                {
                    oneOf = new object[]
                    {
                        new { type = "string", minLength = 32, maxLength = 32 },
                        new { type = "string", @enum = new[] { "reviewed" } }
                    },
                    description = "Exact request ID, or reviewed in automatic QA."
                }
            },
            required = new[] { "requestId" },
            additionalProperties = false
        });
        object ControlVariant(string operation)
        {
            var properties = new Dictionary<string, object>
            {
                ["presentationId"] = new
                {
                    oneOf = new object[]
                    {
                        new { type = "string", minLength = 32, maxLength = 32 },
                        new { type = "string", @enum = new[] { "reviewed" } }
                    },
                    description = "Exact presentation ID, or reviewed in automatic QA."
                },
                ["operation"] = new { type = "string", @enum = new[] { operation } },
                ["mountId"] = new { type = "string", minLength = 1, maxLength = 129 }
            };
            var required = new List<string> { "presentationId", "operation", "mountId" };
            if (operation == "invoke")
            {
                properties["buttonName"] = new { type = "string", minLength = 1, maxLength = 256 };
                required.Add("buttonName");
            }
            if (operation is "invoke" or "reopen")
                properties["expectedTexts"] = new { type = "array", minItems = 1, maxItems = 8, items = new { type = "string" } };
            if (operation == "setText")
            {
                properties["fieldId"] = new { type = "string", minLength = 32, maxLength = 32 };
                properties["expectedText"] = new { type = "string" };
                properties["text"] = new { type = "string" };
                required.AddRange(["fieldId", "expectedText", "text"]);
            }
            return new { type = "object", properties, required, additionalProperties = false };
        }
        var controlsSchema = CerebralJson.Element(new
        {
            oneOf = new[] { ControlVariant("inspect"), ControlVariant("setText"), ControlVariant("invoke"), ControlVariant("reopen") }
        });
        var selectedSchemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [InterfaceToolExtension.StatusToolName] = statusSchema,
            [PluginControlToolExtension.ToolName] = controlsSchema
        };
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [InterfaceToolExtension.StatusToolName] = "Static status contract mentioning reviewed.",
            [PluginControlToolExtension.ToolName] = "Static control contract mentioning reviewed."
        };

        static JsonElement Anchors(Fixture fixture)
        {
            const string marker = "\nAccepted metadata and host evidence:\n";
            var offset = fixture.MetadataText.IndexOf(marker, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(fixture.MetadataText[(offset + marker.Length)..]);
            return document.RootElement.Clone();
        }
        static JsonElement ToolArgumentsSchema(Fixture fixture, string name) => fixture.Command.Arguments
            .GetProperty("responseSchema").GetProperty("oneOf").EnumerateArray().Single(variant =>
                variant.GetProperty("properties").TryGetProperty("name", out var toolName) &&
                toolName.TryGetProperty("const", out var exact) && exact.GetString() == name)
            .GetProperty("properties").GetProperty("arguments");
        static bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException) { return true; }
        }
        void SelectControlSchemas(Fixture fixture)
        {
            fixture.Describe(InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName);
            fixture.Catalog(exact: true,
                forcedNames: [InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName],
                inputSchemas: selectedSchemas, descriptions: descriptions);
        }
        Fixture Ordinary(string requestId = "ordinary-control-reference")
        {
            var fixture = new Fixture(warm: false, requestId: requestId);
            fixture.History([]);
            fixture.Catalog(forcedNames: [InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName],
                descriptions: descriptions);
            SelectControlSchemas(fixture);
            return fixture;
        }
        void AssertOrdinaryProjection(Fixture fixture, string label, string? advertised)
        {
            var anchors = Anchors(fixture);
            var selected = anchors.GetProperty("selectedToolSchemas");
            var controlReferences = selected.EnumerateArray().Where(item =>
                item.GetProperty("name").GetString() is InterfaceToolExtension.StatusToolName or PluginControlToolExtension.ToolName).ToArray();
            check(controlReferences.Length == 2 && controlReferences.All(item =>
                    !item.GetRawText().Contains("reviewed", StringComparison.Ordinal)),
                label + " selected contracts omit the automatic-QA alias");
            check(!fixture.Command.Arguments.GetProperty("responseSchema").GetRawText().Contains("reviewed", StringComparison.Ordinal),
                label + " generation grammar omits the automatic-QA alias");
            var status = ToolArgumentsSchema(fixture, InterfaceToolExtension.StatusToolName);
            var controls = ToolArgumentsSchema(fixture, PluginControlToolExtension.ToolName);
            WorkflowSchema.Validate(CerebralJson.Element(new { requestId = otherPresentation }), status);
            WorkflowSchema.Validate(CerebralJson.Element(new { presentationId = otherPresentation, operation = "inspect", mountId = "fixture.main" }), controls);
            WorkflowSchema.Validate(CerebralJson.Element(new
            {
                presentationId = otherPresentation, operation = "setText", mountId = "fixture.main",
                fieldId = new string('f', 32), expectedText = "before", text = "after"
            }), controls);
            WorkflowSchema.Validate(CerebralJson.Element(new
            {
                presentationId = otherPresentation, operation = "invoke", mountId = "fixture.main",
                buttonName = "Apply", expectedTexts = new[] { "after" }
            }), controls);
            WorkflowSchema.Validate(CerebralJson.Element(new
            {
                presentationId = otherPresentation, operation = "reopen", mountId = "fixture.main",
                expectedTexts = new[] { "after" }
            }), controls);
            check(Rejects(() => WorkflowSchema.Validate(CerebralJson.Element(new { requestId = "reviewed" }), status)) &&
                  Rejects(() => WorkflowSchema.Validate(CerebralJson.Element(new { presentationId = "reviewed", operation = "inspect", mountId = "fixture.main" }), controls)),
                label + " grammar retains generic exact IDs but rejects the QA-only alias");
            if (advertised is not null)
                check(selected.GetRawText().Contains(advertised, StringComparison.Ordinal) &&
                      fixture.Command.Arguments.GetProperty("responseSchema").GetRawText().Contains(advertised, StringComparison.Ordinal),
                    label + " advertises the current approved request without constraining other valid IDs");
        }

        var ordinary = Ordinary();
        AssertOrdinaryProjection(ordinary, "ordinary no-reference", null);
        check(ordinary.Step.State.GetProperty("selected").GetRawText().Contains("reviewed", StringComparison.Ordinal),
            "ordinary projection does not rewrite the durable selected catalog contracts");

        var flatControlsSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new
            {
                presentationId = new
                {
                    oneOf = new object[]
                    {
                        new { type = "string", minLength = 32, maxLength = 32 },
                        new { type = "string", @enum = new[] { "reviewed" } }
                    }
                },
                operation = new { type = "string", @enum = new[] { "inspect" } },
                mountId = new { type = "string", minLength = 1, maxLength = 129 }
            },
            required = new[] { "presentationId", "operation", "mountId" },
            additionalProperties = false
        });
        var flat = new Fixture(warm: false);
        flat.History([]);
        flat.Catalog(forcedNames: [PluginControlToolExtension.ToolName], descriptions: descriptions);
        flat.Describe(PluginControlToolExtension.ToolName);
        flat.Catalog(exact: true, forcedNames: [PluginControlToolExtension.ToolName],
            inputSchema: flatControlsSchema, descriptions: descriptions);
        var flatSelected = Anchors(flat).GetProperty("selectedToolSchemas").EnumerateArray().Single();
        var flatGenerated = ToolArgumentsSchema(flat, PluginControlToolExtension.ToolName);
        check(!flatSelected.GetRawText().Contains("reviewed", StringComparison.Ordinal) &&
              !flatGenerated.GetRawText().Contains("reviewed", StringComparison.Ordinal),
            "an ordinary compatible flat plugin.controls schema omits the QA-only alias in both projections");
        WorkflowSchema.Validate(CerebralJson.Element(new
            { presentationId = otherPresentation, operation = "inspect", mountId = "fixture.main" }), flatGenerated);
        check(Rejects(() => WorkflowSchema.Validate(CerebralJson.Element(new
            { presentationId = "reviewed", operation = "inspect", mountId = "fixture.main" }), flatGenerated)) &&
              flat.Step.State.GetProperty("selected").GetRawText().Contains("reviewed", StringComparison.Ordinal),
            "flat projection retains all operation constraints and leaves the saved host schema unchanged");

        var qaLookingUser = Ordinary("plugin-tests-" + presentation);
        AssertOrdinaryProjection(qaLookingUser, "QA-looking user run", null);

        var serviceQa = new Fixture(warm: false,
            input: new { requiredSuccessfulTools = new[] { InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName } },
            requestId: "plugin-tests-" + presentation,
            caller: new EventActor(EventActorKind.Service, "cerebral.plugin-tests"));
        serviceQa.History([]);
        serviceQa.Catalog(forcedNames: [InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName], descriptions: descriptions);
        if (serviceQa.Is("catalog"))
            serviceQa.Catalog(exact: true,
                forcedNames: [InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName],
                inputSchemas: selectedSchemas, descriptions: descriptions);
        var qaSelected = Anchors(serviceQa).GetProperty("selectedToolSchemas");
        check(qaSelected.GetRawText().Contains("reviewed", StringComparison.Ordinal) &&
              serviceQa.Command.Arguments.GetProperty("responseSchema").GetRawText().Contains("reviewed", StringComparison.Ordinal),
            "the exact service-owned automatic QA run retains its bound reviewed alias");
        WorkflowSchema.Validate(CerebralJson.Element(new { requestId = "reviewed" }),
            ToolArgumentsSchema(serviceQa, InterfaceToolExtension.StatusToolName));
        WorkflowSchema.Validate(CerebralJson.Element(new { presentationId = "reviewed", operation = "inspect", mountId = "fixture.main" }),
            ToolArgumentsSchema(serviceQa, PluginControlToolExtension.ToolName));

        var root = Path.Combine(Path.GetTempPath(), "ConversationControlReferenceProjection");
        var desktop = Path.Combine(root, "Desktop");
        var project = Path.Combine(desktop, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var source = Path.Combine(desktop, "GeneratedDesktopPlugin.cs");
        var manifest = Path.Combine(desktop, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var projectsSchema = CerebralJson.Element(new { type = "object", properties = new { root = new { type = "string" } } });
        string InventoryPayload(bool includeReference)
        {
            var payload = JsonSerializer.SerializeToNode(new
            {
                projects = new[] { new
                {
                    root, id = "fixture.plugin", name = "Fixture", version = "1.0.0", error = (string?)null,
                    components = new[] { new
                    {
                        component = "desktop", source,
                        manifest = Path.Combine(desktop, "desktop-plugin.json"), id = "fixture.plugin", name = "Fixture",
                        version = "1.0.0", error = (string?)null,
                        build = new { executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" },
                            workingDirectory = desktop, builtManifest = manifest }
                    } }
                } },
                count = 1, scannedDirectories = 1, limited = false, discoverySummaryOnly = false, scaffoldReady = false,
                desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId
            }, CerebralJson.Options)!.AsObject();
            if (includeReference)
                payload["approvedControlReference"] = JsonSerializer.SerializeToNode(new
                {
                    schemaVersion = 1, requestId = presentation, status = "completed", sourceRoot = root,
                    sourceManifest = manifest, packageFingerprint = new string('a', 64), mountIds = new[] { "fixture.plugin.main" }
                }, CerebralJson.Options);
            return payload.ToJsonString(CerebralJson.Options);
        }
        var approved = new Fixture(warm: false);
        approved.History([Past("control-reference-earlier", 70, "Earlier reducible fixture context.")]);
        approved.Catalog(forcedNames: ["plugin.projects", InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName], descriptions: descriptions);
        approved.Select("plugin.projects", projectsSchema);
        approved.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
        approved.Tool(true, InventoryPayload(includeReference: true), provider: "cerebral.plugin-sdk");
        SelectControlSchemas(approved);
        AssertOrdinaryProjection(approved, "ordinary approved-reference", presentation);

        var refreshed = approved.Fork();
        refreshed.Select("file.inspect", CerebralJson.Element(new
        {
            type = "object", properties = new { path = new { type = "string" } },
            required = new[] { "path" }, additionalProperties = false
        }));
        refreshed.Model(new { action = "tool", name = "file.inspect", arguments = new { path = source } });
        refreshed.Tool(true, "Observed source before refreshing its exact project inventory.");
        refreshed.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
        check(refreshed.Is("tool") &&
              refreshed.Command.Arguments.GetProperty("name").GetString() == "plugin.projects",
            "an intervening source inspection admits a fresh exact project inventory without bypassing duplicate-read guards");
        refreshed.Tool(true, InventoryPayload(includeReference: false), provider: "cerebral.plugin-sdk");
        AssertOrdinaryProjection(refreshed, "ordinary reference-removed", null);
        check(!Anchors(refreshed).GetProperty("selectedToolSchemas").GetRawText().Contains(presentation, StringComparison.Ordinal) &&
              !refreshed.Command.Arguments.GetProperty("responseSchema").GetRawText().Contains(presentation, StringComparison.Ordinal) &&
              refreshed.Step.State.GetProperty("selected").GetRawText().Contains("reviewed", StringComparison.Ordinal),
            "a later exact inventory without approval stops advertising the old ID while preserving durable host schemas");

        var restored = approved.Fork();
        using (var saved = JsonDocument.Parse(approved.Step.State.GetRawText()))
            restored.RestoreState(saved.RootElement.Clone());
        restored.RejectContext(ContextProjectionStatus.NeedsCompaction);
        check(restored.Is("compact"), "approved-reference schema projection reaches real compaction with saved state");
        restored.CompactSuccess();
        check(restored.Is("generate"), "approved-reference schema projection resumes generation after compaction");
        AssertOrdinaryProjection(restored, "restored compacted ordinary run", presentation);
        check(restored.Step.State.GetProperty("selected").GetRawText().Contains("reviewed", StringComparison.Ordinal),
            "serialization and compaction recompute the projection without mutating selected host schemas");
    }
}
