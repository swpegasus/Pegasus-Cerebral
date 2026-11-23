using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    public const string ToolCatalogPageVersion = "1.0.4-tool-catalog-page.1";
    private readonly bool useToolCatalogPages;
    private enum CatalogPageEdition { Enabled }
    private ConversationAgentHarness(CatalogPageEdition edition) : this(true, false, true)
    {
        useToolCatalogPages = edition == CatalogPageEdition.Enabled;
        Descriptor = Descriptor with { Version = ToolCatalogPageVersion };
    }
    /// <summary>Explicit context/receipt-aware edition; no accepted state or default is migrated.</summary>
    public static ConversationAgentHarness CreateToolCatalogPageEdition() => new(CatalogPageEdition.Enabled);

    private sealed record CatalogPageRequest(string? Query, string? AfterName);
    private sealed record ToolCatalogPageState(int SchemaVersion, string RunId)
    {
        public string? Query { get; init; }
        public string? NextAfterName { get; init; }
        public int? Remaining { get; init; }
        public CatalogPageRequest? LastRead { get; init; }
        public IReadOnlyList<CatalogPageRequest> Requests { get; init; } = [];
    }
    private sealed record CatalogV2(CatalogData Data, CatalogPageRequest? PageRequest, int Remaining, string? NextAfterName);

    private const string ToolCatalogPageGuidance = """

        This accepted edition supports discover{query?,afterName?} for one read-only catalog page.
        Query is a nonblank substring up to64 characters; afterName is the exact exclusive seek
        position shown by nextAfterName, at most256 characters. Omit unused fields; never send null.
        If catalogTruncated/hasMore is true, another bounded page can reveal omitted names.
        A fresh query starts a new search; omit afterName to start it. Retain the same query while
        following a returned cursor. Remaining counts current matching metadata at that host read,
        not a lease or stable total. Describe exact names to select schemas, then use the tool action.
        Discovery and describe share a lookup budget of16 attempts; remainingLookupAttempts shows
        that budget separately from remaining catalog matches, grants, model tokens or inference slots.
        A depleted lookup budget does not prove omitted tools unavailable. The selected-schema cache
        still holds the latest8.
        Neither a catalog row nor a schema is permission, execution or a successful tool receipt.
        """;

    private static bool CatalogEditionEffectVersion(HarnessState state, WorkflowEffect effect)
    {
        if (state.ToolCatalogPage is not null && effect.CommandId == Catalog &&
            state.Phase is "catalog" or "describe" or "catalog-page") return effect.CommandVersion == 2;
        return effect.CommandVersion == 1 || state.Phase == "context-contribution";
    }

    private static void ValidateToolCatalogPageState(HarnessState state, string runId, bool enabled)
    {
        if (enabled != (state.ToolCatalogPage is not null))
            throw new ArgumentException("The saved tool-catalog edition marker does not match this accepted definition.");
        if (state.ToolCatalogPage is not { } page) return;
        if (page.SchemaVersion != 2 || page.RunId != runId || state.Descriptions is < 0 or > 16 || page.Requests is null || page.Requests.Count > 16 ||
            page.Requests.Count > state.Descriptions || page.Remaining is < 0 || state.Discovery.Count > 64 || state.Selected.Count > 8)
            throw new ArgumentException("The saved tool-catalog page state is invalid.");
        ValidateCatalogPageText(page.Query, 64, query: true);
        ValidateCatalogPageText(page.NextAfterName, 256);
        if (page.Remaining is null && (page.Query is not null || page.NextAfterName is not null || page.LastRead is not null) ||
            page.Remaining is { } remaining && (page.LastRead is null || (remaining > 0) != (page.NextAfterName is not null)))
            throw new ArgumentException("The saved catalog continuation is inconsistent.");
        if (page.LastRead is { } last) ValidateCatalogPageRequest(last);
        if (page.LastRead is { } read && (page.Query != read.Query ||
            page.NextAfterName is { } next && read.AfterName is { } after && StringComparer.Ordinal.Compare(next, after) <= 0))
            throw new ArgumentException("The saved catalog query or continuation does not match its last read.");
        if (page.Remaining is > 0 && (state.Discovery.Count == 0 || state.Discovery[^1].Name != page.NextAfterName))
            throw new ArgumentException("The saved catalog continuation has no matching last visible row.");
        foreach (var request in page.Requests)
        {
            if (request is null) throw new ArgumentException("The saved catalog request is missing.");
            ValidateCatalogPageRequest(request);
        }
        for (var i = 0; i < page.Requests.Count; i++)
            if (page.Requests.Take(i).Any(prior => SameCatalogPageRequest(prior, page.Requests[i])))
                throw new ArgumentException("The saved catalog request list repeats a page.");
    }

    private static void ValidateCatalogPageText(string? value, int maximum, bool query = false)
    {
        if (value is null) return;
        if (value.Length is < 1 || value.Length > maximum || value.Any(char.IsControl) ||
            query && (string.IsNullOrWhiteSpace(value) || value != value.Trim()))
            throw new ArgumentException("Catalog query/cursor must be a bounded unambiguous string.");
    }
    private static void ValidateCatalogPageRequest(CatalogPageRequest request)
    { ValidateCatalogPageText(request.Query, 64, true); ValidateCatalogPageText(request.AfterName, 256); }
    private static CatalogPageRequest ReadCatalogPageRequest(JsonElement arguments)
    {
        ObjectFields(arguments, [], ["query", "afterName"]);
        string? Read(string name, int maximum, bool query = false)
        {
            if (!arguments.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Catalog fields must be strings when present.");
            var text = value.GetString()!; ValidateCatalogPageText(text, maximum, query); return text;
        }
        return new(Read("query", 64, true), Read("afterName", 256));
    }
    private static JsonElement DiscoveryArguments(JsonElement action) => Element(action.EnumerateObject()
        .Where(p => p.Name != "action").ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal));
    private static bool SameCatalogPageRequest(CatalogPageRequest a, CatalogPageRequest b) =>
        string.Equals(a.Query, b.Query, StringComparison.OrdinalIgnoreCase) && a.AfterName == b.AfterName;

    private static WorkflowStep RequestToolCatalogPage(Input input, HarnessState state, JsonElement action)
    {
        if (state.ToolCatalogPage is not { } page)
            return Repair(input, state, "Paged tool discovery is unavailable in this accepted edition.");
        var args = DiscoveryArguments(action);
        var request = ReadCatalogPageRequest(args);
        if (state.Descriptions >= 16)
            return Repair(input, state, CatalogLookupBudgetExhausted);
        if (page.Requests.Any(prior => SameCatalogPageRequest(prior, request)) ||
            page.LastRead is { } last && SameCatalogPageRequest(last, request))
            return Repair(input, state, "This catalog page was already requested. Use its shown continuation or a different query; no duplicate lookup was dispatched.");
        var next = state with { Phase = "catalog-page", PendingArguments = args,
            Descriptions = state.Descriptions + 1,
            ToolCatalogPage = page with { Requests = [.. page.Requests, request] } };
        return Fits(next) ? Invoke(next, Catalog, args) : ContextFull(state);
    }

    private const string CatalogLookupBudgetExhausted =
        "The shared catalog/schema lookup budget (16 attempts) is exhausted. Catalog continuation may remain; " +
        "omitted tools are not proven unavailable. No lookup was dispatched.";

    private static CatalogV2 ReadToolCatalogV2(HarnessState state, WorkflowEffect effect)
    {
        MatchPending(state, effect);
        var result = effect.Result ?? throw new ArgumentException("Catalog v2 returned no result.");
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > MaximumCatalogBytes)
            throw new ArgumentException("Catalog v2 exceeded48KiB.");
        ObjectFields(result, ["schemaVersion", "mode", "request", "tools", "truncated", "remaining", "nextAfterName"], ["helpers", "pluginSource"]);
        if (Integer(result.GetProperty("schemaVersion"), 2, 2) != 2 ||
            !JsonElement.DeepEquals(result.GetProperty("request"), state.PendingArguments!.Value))
            throw new ArgumentException("Catalog v2 schema or exact pending-request echo does not match.");
        var exact = state.Phase == "describe";
        if (RequiredText(result, "mode", 8) != (exact ? "names" : "page"))
            throw new ArgumentException("Catalog v2 returned a different lookup mode.");
        CatalogPageRequest? request = null;
        if (exact)
        {
            ObjectFields(result.GetProperty("request"), ["names"], []);
            var names = Strings(result.GetProperty("request").GetProperty("names"));
            if (names.Length is < 1 or > 8 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new ArgumentException("Catalog exact names are invalid.");
            foreach (var name in names) ValidateCatalogPageText(name, 256);
        }
        else request = ReadCatalogPageRequest(result.GetProperty("request"));
        var tools = result.GetProperty("tools");
        if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() > (exact ? 8 : 64))
            throw new ArgumentException("Catalog v2 returned too many rows.");
        var rowNames = new List<string>();
        foreach (var row in tools.EnumerateArray())
        {
            ObjectFields(row, ["name", "description", "capability"], exact ? ["inputSchema", "prerequisiteTools"] : ["prerequisiteTools"]);
            var name = RequiredText(row, "name", 256); ValidateCatalogPageText(name, 256);
            _ = RequiredText(row, "capability", 256);
            if (row.GetProperty("description").ValueKind != JsonValueKind.String || row.GetProperty("description").GetString()!.Length > 4096)
                throw new ArgumentException("Catalog description exceeds its bound.");
            if (rowNames.Contains(name, StringComparer.Ordinal) || !exact &&
                (rowNames.Count > 0 && StringComparer.Ordinal.Compare(name, rowNames[^1]) <= 0 ||
                 request!.AfterName is { } after && StringComparer.Ordinal.Compare(name, after) <= 0))
                throw new ArgumentException("Catalog page repeats a row or fails to advance in ordinal order.");
            if (exact && (!row.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object))
                throw new ArgumentException("Exact catalog result omitted a frozen schema.");
            if (row.TryGetProperty("prerequisiteTools", out var hints))
            {
                var related = Strings(hints);
                if (related.Length > 8 || related.Distinct(StringComparer.Ordinal).Count() != related.Length)
                    throw new ArgumentException("Catalog prerequisite hints exceed their bound.");
                foreach (var hint in related) ValidateCatalogPageText(hint, 256);
            }
            rowNames.Add(name);
        }
        var remaining = checked((int)Integer(result.GetProperty("remaining"), 0, int.MaxValue));
        var truncated = result.GetProperty("truncated").ValueKind switch {
            JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new ArgumentException("Catalog truncation must be Boolean.") };
        var nextValue = result.GetProperty("nextAfterName");
        var next = nextValue.ValueKind == JsonValueKind.Null ? null : RequiredText(result, "nextAfterName", 256);
        ValidateCatalogPageText(next, 256);
        if (truncated != (remaining > 0) || truncated && (exact || rowNames.Count == 0 || next != rowNames[^1]) ||
            !truncated && next is not null || exact && remaining != 0)
            throw new ArgumentException("Catalog continuation/remaining count is inconsistent or nonadvancing.");
        var data = result.Deserialize<CatalogData>(Json) ?? throw new ArgumentException("Catalog v2 data is missing.");
        if (result.TryGetProperty("helpers", out var helpers) &&
            (helpers.ValueKind != JsonValueKind.Array || helpers.GetArrayLength() > 16) ||
            result.TryGetProperty("pluginSource", out var source) && source.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Catalog v2 optional metadata has an invalid shape.");
        if (data.Helpers is { } helperRows &&
            (helperRows.Any(h => h is null || string.IsNullOrWhiteSpace(h.Id) || h.Id.Length > 128 ||
                h.Name is null || h.Name.Length > 128 || h.Purpose is null || h.Purpose.Length > 256 || h.Revision < 0) ||
             helperRows.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() != helperRows.Count))
            throw new ArgumentException("Catalog helpers exceed their accepted metadata bound.");
        return new(data, request, remaining, next);
    }

    private static HarnessState AdoptToolCatalogPosition(HarnessState state, CatalogV2 result) =>
        result.PageRequest is null ? state : state with { ToolCatalogPage = state.ToolCatalogPage! with {
            Query = result.PageRequest.Query, LastRead = result.PageRequest,
            NextAfterName = result.NextAfterName, Remaining = result.Remaining } };

    private static CatalogData RetainCatalogMetadata(HarnessState state, CatalogData data) => data with
    {
        Helpers = data.Helpers ?? state.Helpers,
        PluginSource = data.PluginSource ?? (state.PluginSource is { } source
            ? new(source.SchemaVersion, source.PresentationId, source.Root) : null)
    };

    private static WorkflowStep ReceiveToolCatalogPage(Input input, HarnessState state, WorkflowEffect effect, string runId)
    {
        if (effect.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed))
            return Finish(state, false, effect.Error ?? "Catalog page outcome is unconfirmed; no semantic tool was dispatched.");
        if (effect.State == WorkflowEffectState.Failed)
            return RequestModel(input, Append(state with { Phase = "model", PendingArguments = null },
                "catalog." + effect.Id, "catalog-result", "user", "Catalog page failed: " + effect.Error +
                ". Existing schemas, receipts and grants are unchanged; no semantic tool ran.", ContextRequirement.Required, effect.Id));
        var page = ReadToolCatalogV2(state, effect);
        var data = RetainCatalogMetadata(state, page.Data);
        var helpers = data.Helpers ?? [];
        var next = AdoptToolCatalogPosition(AdoptQaSourceCatalog(input, state, data, effect, runId), page) with {
            Phase = "model", PendingArguments = null, CatalogLoaded = true, CatalogTruncated = data.Truncated,
            Discovery = data.Tools.Select(tool => tool with { InputSchema = null }).ToArray(), Helpers = helpers.ToArray() };
        next = Append(next, "catalog." + effect.Id, "catalog-result", "user",
            "A bounded tool catalog page was read. Remaining at this read: " + page.Remaining +
            ". Names and schemas are metadata only; no semantic tool ran and no prior failure was repaired.",
            ContextRequirement.Required, effect.Id, complete: false);
        return Fits(next) ? RequestModel(input, next) : ContextFull(state);
    }

    private static JsonElement WithCatalogDiscoveryAction(JsonElement schema)
    {
        var generated = JsonNode.Parse(schema.GetRawText())!.AsObject();
        generated["oneOf"]!.AsArray().Add(JsonNode.Parse("""
            {"type":"object","properties":{"action":{"const":"discover"},"query":{"type":"string","minLength":1,"maxLength":64},"afterName":{"type":"string","minLength":1,"maxLength":256}},"required":["action"],"additionalProperties":false}
            """));
        return Element(generated);
    }
}
