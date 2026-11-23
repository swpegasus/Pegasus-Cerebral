using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

// Optional catalog command version. Version one and its accepted callers remain unchanged.
public sealed partial class WorkerWorkflowEffects
{
    public sealed record CatalogPageArguments(string? Query = null, string? AfterName = null,
        IReadOnlyList<string>? Names = null);

    public sealed record CatalogPageData(int SchemaVersion, string Mode, JsonElement Request,
        IReadOnlyList<CatalogEntry> Tools, bool Truncated, int Remaining, string? NextAfterName)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<HelperEntry>? Helpers { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PluginSourceCoordinate? PluginSource { get; init; }
    }

    private sealed class CatalogPageHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(CatalogCommand, 2,
            "Read one bounded, ordered page of currently permitted accepted model-visible tools, or 1–8 exact frozen input schemas. Query matches a name or concise description; afterName is a seek position, not execution authority or a stable lease.",
            Schema("""
                {"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":64},
                "afterName":{"type":"string","minLength":1,"maxLength":256},
                "names":{"type":"array","minItems":1,"maxItems":8,
                "items":{"type":"string","minLength":1,"maxLength":256}}},"additionalProperties":false}
                """),
            Schema("""
                {"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[2]},
                "mode":{"type":"string","enum":["page","names"]},
                "request":{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":64},
                "afterName":{"type":"string","minLength":1,"maxLength":256},
                "names":{"type":"array","minItems":1,"maxItems":8,
                "items":{"type":"string","minLength":1,"maxLength":256}}},"additionalProperties":false},
                "tools":{"type":"array","maxItems":64,"items":{"type":"object","properties":{
                "name":{"type":"string"},"description":{"type":"string"},"capability":{"type":"string"},
                "inputSchema":{},
                "prerequisiteTools":{"type":"array","maxItems":8,"items":{"type":"string","minLength":1,"maxLength":256}}},
                "required":["name","description","capability"],"additionalProperties":false}},
                "truncated":{"type":"boolean"},"remaining":{"type":"integer","minimum":0},
                "nextAfterName":{"oneOf":[{"type":"string","minLength":1,"maxLength":256},{"type":"null"}]},
                "helpers":{"type":"array","maxItems":16,"items":{"type":"object"}},
                "pluginSource":{"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},
                "presentationId":{"type":"string","minLength":32,"maxLength":32},
                "root":{"type":"string","minLength":1,"maxLength":2048}},
                "required":["schemaVersion","presentationId","root"],"additionalProperties":false}},
                "required":["schemaVersion","mode","request","tools","truncated","remaining","nextAfterName"],"additionalProperties":false}
                """), [], WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 30);

        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token) =>
            owner.ReadCatalogPageAsync(context, token);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            ExecuteAsync(context, token);
    }

    private async Task<WorkflowCommandResult> ReadCatalogPageAsync(WorkflowCommandContext context, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateCatalogPageRequest(context.Arguments);
            var args = Read<CatalogPageArguments>(context.Arguments);
            var (_, accepted) = ConversationOwner(context);
            var (_, effective) = Resolve(accepted);
            var listed = await tools.ListAsync(effective, token);
            token.ThrowIfCancellationRequested();
            var (snapshot, current) = Resolve(accepted);
            // The snapshot is the upper bound. Current grants, capability, model visibility and
            // exact descriptor identity can only narrow it. A later package cannot add a tool.
            var permitted = snapshot.Tools.Where(tool => tool.ModelVisible &&
                tool.Name.Length is > 0 and <= 256 && !tool.Name.Any(char.IsControl) &&
                WorkerExecutionSnapshot.AllowsTool(current, tool.Name) &&
                current.Capabilities.Contains(tool.Capability, StringComparer.Ordinal) &&
                !HostOwnsNativeCortexTool(snapshot, tool.Name) &&
                listed.Any(live => live.Name == tool.Name && live.ModelVisible &&
                    WorkerExecutionSnapshot.SameTool(tool, live)))
                .OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
            if (args.Names is { } names)
            {
                if (names.Any(name => !permitted.Any(tool => tool.Name == name)))
                    throw new AdmissionException("A requested tool is unavailable or outside the accepted and current model-visible grants.");
                var selected = names.Select(name => permitted.Single(tool => tool.Name == name)).ToArray();
                // Exact-name lookup is where the model receives the full argument contract.
                // The concise page remains clipped, and the existing response-byte check below
                // rejects a selection that cannot fit rather than silently losing guidance.
                var exact = selected.Select(tool => new CatalogEntry(tool.Name, tool.Description,
                    tool.Capability, tool.InputSchema.Clone())
                {
                    PrerequisiteTools = tool.PrerequisiteTools.Count == 0 ? null : tool.PrerequisiteTools
                        .Where(name => permitted.Any(candidate => candidate.Name == name))
                        .Distinct(StringComparer.Ordinal).Take(8).ToArray()
                }).ToArray();
                var exactData = Element(new CatalogPageData(2, "names", context.Arguments.Clone(),
                    exact, false, 0, null)
                    { PluginSource = ReadPluginSourceCoordinate(context, accepted, snapshot, current) });
                if (Size(exactData) > MaximumConversationEffectBytes)
                    throw new AdmissionException("Selected tool schemas exceed the bounded catalog response. Request fewer exact names.");
                return new(WorkflowEffectState.Succeeded, exactData);
            }
            var matching = permitted.Where(tool =>
                    (args.Query is null || tool.Name.Contains(args.Query, StringComparison.OrdinalIgnoreCase) ||
                     Clip(tool.Description, 256).Contains(args.Query, StringComparison.OrdinalIgnoreCase)) &&
                    (args.AfterName is null || StringComparer.Ordinal.Compare(tool.Name, args.AfterName) > 0))
                .ToArray();
            var entries = matching.Take(64).Select(tool => new CatalogEntry(tool.Name,
                Clip(tool.Description, 256), tool.Capability)
                {
                    // Related names remain hints. Grant-filter them independently of the selected row.
                    PrerequisiteTools = tool.PrerequisiteTools.Count == 0 ? null : tool.PrerequisiteTools
                        .Where(name => permitted.Any(candidate => candidate.Name == name))
                        .Distinct(StringComparer.Ordinal).Take(8).ToArray()
                }).ToArray();
            var helpers = AvailableHelpers(accepted, current);
            var pluginSource = ReadPluginSourceCoordinate(context, accepted, snapshot, current);

            CatalogPageData Page() => new(2, "page", context.Arguments.Clone(), entries,
                matching.Length > entries.Length, matching.Length - entries.Length,
                matching.Length > entries.Length && entries.Length > 0 ? entries[^1].Name : null)
                { Helpers = helpers, PluginSource = pluginSource };
            var data = Element(Page());
            while (Size(data) > MaximumConversationEffectBytes && entries.Length > 0)
            {
                entries = entries[..^1];
                data = Element(Page());
            }
            if (Size(data) > MaximumConversationEffectBytes || matching.Length > 0 && entries.Length == 0)
                throw new AdmissionException("A catalog page cannot fit one tool with its required binding metadata.");
            if (matching.Length > entries.Length &&
                (entries[^1].Name == args.AfterName ||
                 args.AfterName is not null && StringComparer.Ordinal.Compare(entries[^1].Name, args.AfterName) <= 0))
                throw new AdmissionException("The catalog page did not advance its seek position.");
            return new(WorkflowEffectState.Succeeded, data);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(WorkflowEffectState.Cancelled, Error: "Tool catalog page lookup was cancelled."); }
        catch (Exception error) when (IsOrdinary(error))
        { return new(WorkflowEffectState.Failed, Error: ConversationReadError(error)); }
    }

    private static void ValidateCatalogPageRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Catalog paging requires an argument object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!fields.Add(field.Name) || field.Name is not ("query" or "afterName" or "names"))
                throw new ArgumentException("Catalog request fields must be distinct and supported.");
            if (field.Name == "names")
            {
                if (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() is < 1 or > 8)
                    throw new ArgumentException("Exact catalog lookup requires 1–8 names.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in field.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name ||
                        name.Length is < 1 or > 256 || name.Any(char.IsControl) || !names.Add(name))
                        throw new ArgumentException("Exact catalog names must be distinct bounded strings.");
                }
            }
            else
            {
                if (field.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Catalog query and seek position must be strings.");
                var text = field.Value.GetString()!;
                var maximum = field.Name == "query" ? 64 : 256;
                if (text.Length is < 1 || text.Length > maximum || text.Any(char.IsControl) ||
                    field.Name == "query" && (string.IsNullOrWhiteSpace(text) || text != text.Trim()))
                    throw new ArgumentException("Catalog query or seek position is invalid.");
            }
        }
        if (fields.Contains("names") && (fields.Contains("query") || fields.Contains("afterName")))
            throw new ArgumentException("Exact names cannot be combined with a page query or seek position.");
    }
}
