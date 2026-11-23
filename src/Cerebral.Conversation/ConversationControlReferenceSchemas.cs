using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private static JsonElement GenerationActionSchema(Input input, HarnessState state)
    {
        if (input.AutomaticPluginTestPresentationId is not null) return GenerationActionSchema(state);

        // Project model-facing copies only. The saved selected contracts remain the exact
        // host catalog so recovery and runtime admission never depend on this guidance.
        var projected = state with
        {
            Selected = state.Selected.Select(tool => ProjectControlReferenceTool(state, tool)).ToArray()
        };
        return GenerationActionSchema(projected);
    }

    private static CatalogItem ProjectReadableConversationTool(Input input, HarnessState state, CatalogItem tool)
    {
        var readable = ProjectReadableConversationTool(tool);
        return input.AutomaticPluginTestPresentationId is not null
            ? readable
            : ProjectControlReferenceTool(state, readable);
    }

    private static CatalogItem ProjectControlReferenceTool(HarnessState state, CatalogItem tool)
    {
        if (tool.Name is not ("interface.status" or "plugin.controls")) return tool;

        var approved = state.PluginWork?.ApprovedControlReference?.RequestId;
        var description = tool.Name == "interface.status"
            ? approved is null
                ? "Read one exact presentation receipt owned by this conversation. Supply the exact 32-character requestId returned by interface.present or plugin.present. For an inspected existing plugin, use pluginWorkBinding.approvedControlReference.requestId when available. Pending or claimed means owner review is incomplete; completed proves presentation only."
                : $"Read one exact presentation receipt owned by this conversation. The current inspected plugin binding advertises requestId {approved}; use it for that installation. Another exact 32-character requestId remains allowed and is revalidated by the host. Completed proves presentation only."
            : approved is null
                ? "Inspect or test owner-approved installed plugin controls using an exact 32-character presentationId from plugin.present and interface.status. For an inspected existing plugin, use pluginWorkBinding.approvedControlReference.requestId when available. Every mount, operation and package identity is revalidated by the control host."
                : $"Inspect or test owner-approved installed plugin controls. The current inspected plugin binding advertises presentationId {approved}; use it with an exact reported mountId. Another exact 32-character presentationId remains allowed and is revalidated by the control host.";
        return tool with
        {
            Description = description,
            InputSchema = tool.InputSchema is { } schema
                ? ProjectOrdinaryControlReferenceSchema(tool.Name, schema, approved)
                : null
        };
    }

    private static JsonElement ProjectOrdinaryControlReferenceSchema(string toolName, JsonElement schema, string? approved)
    {
        if (schema.ValueKind != JsonValueKind.Object || JsonNode.Parse(schema.GetRawText()) is not JsonObject projected)
            throw new ArgumentException("The selected control-reference schema cannot be safely projected.");

        if (toolName == "interface.status")
        {
            if (projected["properties"] is not JsonObject properties ||
                properties["requestId"] is not JsonObject)
                throw new ArgumentException("The interface.status schema has no requestId coordinate.");
            properties["requestId"] = ExactControlReferenceSchema(
                "Exact 32-character requestId returned by interface.present or plugin.present.", approved);
        }
        else
        {
            if (projected["oneOf"] is JsonArray variants)
            {
                if (variants.Count is < 1 or > 8)
                    throw new ArgumentException("The plugin.controls schema has no bounded operation variants.");
                foreach (var variant in variants)
                {
                    if (variant is not JsonObject body || body["properties"] is not JsonObject properties ||
                        properties["presentationId"] is not JsonObject)
                        throw new ArgumentException("A plugin.controls operation has no presentationId coordinate.");
                    properties["presentationId"] = ExactControlReferenceSchema(
                        "Exact 32-character plugin.present requestId, also read through interface.status.", approved);
                }
            }
            else if (projected["properties"] is JsonObject properties &&
                     properties["presentationId"] is JsonObject)
                properties["presentationId"] = ExactControlReferenceSchema(
                    "Exact 32-character plugin.present requestId, also read through interface.status.", approved);
            else throw new ArgumentException("The plugin.controls schema has no presentationId coordinate.");
        }
        return Element(projected);
    }

    private static JsonObject ExactControlReferenceSchema(string purpose, string? approved)
    {
        var description = approved is null
            ? purpose + " Inspect an existing plugin's exact project root to discover an approvedControlReference when available."
            : purpose + $" The current inspected binding advertises {approved}; other exact IDs remain subject to host authorization.";
        return new JsonObject
        {
            ["type"] = "string",
            ["minLength"] = 32,
            ["maxLength"] = 32,
            ["description"] = description
        };
    }
}
