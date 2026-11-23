using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker;

public sealed class NativePluginManagementToolExtension(StateStore store, EventHub events,
    Func<WorkerWorkflows> workflowHost) : IToolExtension, IToolRecoveryExtension, IToolExecutionTimeoutPolicy
{
    public const string Provider = "cerebral.desktop.plugins";
    private readonly NativePluginManagementRequests requests = new(store, events);
    private static readonly JsonSerializerOptions Compact = new(CerebralJson.Options)
        { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(NativePluginManagementRequests.ListTool,
            "List installed native desktop plugins and their actual enabled/loaded states. This is different from plugin.projects (source projects) and plugins.list (MCP registrations). Optional query filters ID/name; at most 25 entries are shown. Narrow the query when omitted is nonzero. Keep the returned receipt for a later enable/disable request. No configuration paths, guessed names, installation or code execution.",
            Schema(new { query = new { type = "string", maxLength = 100 } }, []), NativePluginManagementRequests.Capability),
        new(NativePluginManagementRequests.SetTool,
            "Request enabling or disabling one exact installed desktop plugin from a completed catalog receipt. Use catalogRequestId from desktop.plugins.list and the exact returned pluginId. The host binds its version and package internally, preserves dependency checks, and asks the owner to review the change. Only completed with observed enabled/loaded states proves success; uncertain means inspect the retained receipt, never repeat the change.",
            Schema(new { catalogRequestId = new { type = "string", minLength = 32, maxLength = 32 },
                pluginId = new { type = "string", minLength = 1, maxLength = 64 }, enabled = new { type = "boolean" } },
                ["catalogRequestId", "pluginId", "enabled"]), NativePluginManagementRequests.Capability)
            { PrerequisiteTools = [NativePluginManagementRequests.ListTool] },
        new(NativePluginManagementRequests.StatusTool,
            "Read one retained installed-plugin-management receipt from this conversation. Does not queue or replay any action. Use its exact requestId; pending or claimed is not completion.",
            Schema(new { requestId = new { type = "string", minLength = 32, maxLength = 32 } }, ["requestId"]),
            NativePluginManagementRequests.Capability)
    ];

    public int GetDefaultTimeoutSeconds(string toolName) => toolName switch
    {
        // Owner review waits inside this exact executing effect, bounded by its ten-minute receipt.
        NativePluginManagementRequests.SetTool => 600,
        NativePluginManagementRequests.ListTool or NativePluginManagementRequests.StatusTool => 30,
        _ => throw new ArgumentException("Unknown installed desktop plugin tool.", nameof(toolName))
    };

    private static JsonElement Schema(object properties, string[] required) =>
        CerebralJson.Element(new { type = "object", properties, required, additionalProperties = false });

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string? receipt = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCall(call);
            NativePluginManagementRequests.RequireExecuting(workflowHost(), context, call.Name);
            if (call.Name == NativePluginManagementRequests.StatusTool)
                return Project(requests.Read(context, Text(call.Arguments, "requestId"), call.Name));
            NativePluginManagementRequest request;
            if (call.Name == NativePluginManagementRequests.ListTool)
            {
                var query = call.Arguments.TryGetProperty("query", out _) ? Text(call.Arguments, "query") : null;
                request = requests.Create(context, "list", query);
            }
            else
            {
                var proofId = Text(call.Arguments, "catalogRequestId");
                var proof = requests.Read(context, proofId, call.Name);
                var id = Text(call.Arguments, "pluginId");
                var row = proof.Result?.Catalog.Plugins.SingleOrDefault(x => x.Id == id);
                if (proof.Operation != "list" || proof.Status != "completed" || row is null ||
                    row.Error is not null || row.Generation is null || row.PackageFingerprint is null)
                    throw new ArgumentException("Use a valid exact installed plugin ID from this conversation's completed catalog receipt.");
                request = requests.Create(context, "set-enabled", catalogFingerprint: proof.Result!.Catalog.CatalogFingerprint,
                    selection: new(row.Id, row.Enabled, row.Generation, row.PackageFingerprint),
                    enabled: call.Arguments.GetProperty("enabled").GetBoolean(), catalogRequestId: proofId);
            }
            receipt = request.Id;
            return Project(await requests.WaitAsync(context, request.Id, cancellationToken));
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or
            InvalidOperationException or KeyNotFoundException or JsonException)
        {
            return new(false, Bound(error.Message) + (receipt is null ? "" : " Inspect the retained receipt; do not repeat an unconfirmed change."))
                { Evidence = new(receipt is null ? ToolExecutionOutcome.Rejected : ToolExecutionOutcome.Unknown,
                    Provider, RequestId: receipt, State: receipt is null ? "rejected" : "unconfirmed") };
        }
    }

    public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence previousEvidence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidateCall(call);
            NativePluginManagementRequests.RequireExecuting(workflowHost(), context, call.Name, requireEffect: false);
            if (call.Name == NativePluginManagementRequests.StatusTool)
                return Task.FromResult(Project(requests.Read(context, Text(call.Arguments, "requestId"), call.Name)));
            if (previousEvidence.ProviderId != Provider || previousEvidence.RequestId != context.EffectId)
                throw new ArgumentException("The retained receipt does not belong to this exact management effect.");
            var request = requests.Read(context, context.EffectId!, call.Name);
            var same = call.Name == NativePluginManagementRequests.ListTool
                ? request.Operation == "list" && request.Query == (call.Arguments.TryGetProperty("query", out _) ? Text(call.Arguments, "query") : null)
                : request.Operation == "set-enabled" && request.CatalogRequestId == Text(call.Arguments, "catalogRequestId") &&
                    request.Selection?.Id == Text(call.Arguments, "pluginId") && request.Enabled == call.Arguments.GetProperty("enabled").GetBoolean();
            if (!same) throw new ArgumentException("The retained plugin-management effect has different input.");
            if (request.Status is "pending" or "claimed") request = requests.Interrupt(context, request.Id, false);
            return Task.FromResult(Project(request));
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            return Task.FromResult(new ToolResult(false, Bound(error.Message))
                { Evidence = previousEvidence with { Outcome = ToolExecutionOutcome.Unknown } });
        }
    }

    private static void ValidateCall(ToolCall call)
    {
        string[] fields = call.Name switch
        {
            NativePluginManagementRequests.ListTool => ["query"],
            NativePluginManagementRequests.SetTool => ["catalogRequestId", "pluginId", "enabled"],
            NativePluginManagementRequests.StatusTool => ["requestId"],
            _ => throw new ArgumentException("Unknown installed desktop plugin tool.")
        };
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Installed desktop plugin tools require an argument object.");
        var actual = call.Arguments.EnumerateObject().ToArray();
        if (actual.Any(x => !fields.Contains(x.Name, StringComparer.Ordinal)) ||
            actual.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            call.Name != NativePluginManagementRequests.ListTool && actual.Length != fields.Length)
            throw new ArgumentException("Use only the exact installed plugin tool fields, once each.");
        if (call.Name == NativePluginManagementRequests.SetTool &&
            call.Arguments.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("enabled must be a boolean.");
        foreach (var value in actual.Where(x => x.Name != "enabled"))
            if (value.Value.ValueKind != JsonValueKind.String) throw new ArgumentException(value.Name + " must be a string.");
    }

    private static ToolResult Project(NativePluginManagementRequest request)
    {
        var state = request.Status;
        var outcome = state == "completed" ? ToolExecutionOutcome.Succeeded :
            state is "claimed" or "uncertain" ? ToolExecutionOutcome.Unknown :
            state == "cancelled" ? ToolExecutionOutcome.Cancelled : ToolExecutionOutcome.Rejected;
        var rows = request.Result?.Catalog.Plugins.Take(25).Select(x => new
        {
            x.Id, x.Name, x.Version, x.Enabled, x.Loaded,
            error = x.Error is null ? null : x.Error[..Math.Min(x.Error.Length, 240)]
        }).ToArray();
        var omitted = request.Result is null ? 0 : request.Result.Catalog.Omitted + request.Result.Catalog.Plugins.Count - (rows?.Length ?? 0);
        return new(outcome == ToolExecutionOutcome.Succeeded, JsonSerializer.Serialize(new
        {
            receipt = request.Id, state, operation = request.Operation, pluginId = request.Selection?.Id,
            desiredEnabled = request.Enabled, plugins = rows, omitted,
            changedPluginIds = request.Result?.ChangedPluginIds, error = request.Error
        }, Compact)) { Evidence = new(outcome, Provider, RequestId: request.Id, State: state) };
    }
    private static string Text(JsonElement input, string name) => input.GetProperty(name).GetString()
        ?? throw new ArgumentException(name + " requires nonempty text.");
    private static string Bound(string value) => new(value.Where(c => !char.IsControl(c)).Take(2000).ToArray());
}
