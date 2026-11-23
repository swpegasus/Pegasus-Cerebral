using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher
{
    /// <summary>Owner diagnostic only: inspects one configured provider without granting an agent any authority.</summary>
    public async Task<CortexConnectionReport> InspectCortexAsync(string serverId, CancellationToken cancellationToken)
    {
        var report = new CortexConnectionReport { ServerId = serverId };
        var host = _settings();
        var server = host.McpServers.SingleOrDefault(x => x.Id == serverId);
        if (server is null) return CortexInspectionResult(report, "not-configured", "Select or register the installed Cortex provider in Integrator.");
        report.Executable = server.Executable;
        report.ViaUserConnector = server.ViaUserConnector;
        report.ConnectorUrl = server.ViaUserConnector ? server.Url : null;
        report.Transport = server.CortexNative is not null ? "cortex-native-pipe" : server.ViaUserConnector ? "mcp-user-connector" : "mcp-stdio";
        if (!server.Enabled) return CortexInspectionResult(report, "disabled", "This provider is disabled. Enable it in Integrator before inspecting the live connection.");
        if (server.CortexNative is null)
            return CortexInspectionResult(report, "native-binding-required",
                "Cortex is unavailable through this legacy registration. Configure an enrolled native pipe connection; HTTP and stdio are not used for Cortex inspection.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var phase = "discovery";
        try
        {
            ValidateRegistration(server);
            phase = "identity";
            report.NativeIdentity = (await NativeProviderRequestAsync(server, "identity", null, null, timeout.Token)).Deserialize<CortexNativeIdentity>(CerebralJson.Options);
            phase = "discovery";
            var descriptors = await DiscoverAsync(server, timeout.Token);
            var prefix = $"mcp.{server.Id}.";
            var cortexTools = descriptors.Where(x => x.Name.StartsWith(prefix + "cortex_", StringComparison.Ordinal)).ToList();
            report.AvailableTools = cortexTools.Select(x => x.Name[prefix.Length..]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            report.MissingTools = CortexConnectionContract.CoreTools.Except(report.AvailableTools, StringComparer.Ordinal).ToList();
            if (report.MissingTools.Count != 0)
                return CortexInspectionResult(report, "incompatible", "The live provider is missing required Cortex tools: " + string.Join(", ", report.MissingTools) + ".");
            foreach (var name in CortexConnectionContract.CoreTools)
            {
                var matches = cortexTools.Where(x => x.Name == prefix + name).ToList();
                if (matches.Count != 1 || matches[0].InputSchema.ValueKind != JsonValueKind.Object)
                    return CortexInspectionResult(report, "incompatible", $"The live {name} schema is missing, duplicated or not an object.");
            }
            var arguments = CerebralJson.Element(new { });
            try { ValidateSchema(arguments, cortexTools.Single(x => x.Name == prefix + "cortex_status").InputSchema); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
            { return CortexInspectionResult(report, "incompatible", "The live cortex_status schema does not accept the supported empty status request."); }

            phase = "status";
            var result = await CallProviderAsync(server, "cortex_status", arguments, timeout.Token);
            var status = CortexStatusBody(result);
            report.Product = CortexStatusText(status, "product");
            report.Version = CortexStatusText(status, "version");
            report.ProviderState = CortexStatusText(status, "state");
            report.StorageKind = CortexStatusText(status, "storageKind");
            report.Network = CortexStatusBoolean(status, "network");
            report.Service = CortexStatusBoolean(status, "service");
            if (report.Product != CortexConnectionContract.Product || string.IsNullOrWhiteSpace(report.Version))
                return CortexInspectionResult(report, "incompatible", "The provider did not return the expected Pegasus Cortex product and a bounded version. Verify its installed path.");
            if (report.ProviderState != "ready")
                return CortexInspectionResult(report, "provider-error", "Pegasus Cortex answered but is not ready. Check its own installation and storage diagnostics.");
            return CortexInspectionResult(report, "ready", "Pegasus Cortex answered its read-only status request and exposes all eight required tools. Agent grants and project namespaces remain separate settings.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return CortexInspectionResult(report, phase == "connector" ? "connector-unavailable" : "provider-error",
                phase == "connector" ? "The user-session connector did not answer in time. Check its status in Integrator."
                    : $"Cortex {phase} timed out. The inspection was cancelled; inspect the installed provider before retrying.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or JsonException or McpRemoteException or System.ComponentModel.Win32Exception
            || server.CortexNative is not null && ex is InvalidDataException && CortexNativePipeClient.HasHandshakeFailure(ex))
        {
            // Provider error payloads and stderr may contain private data. Return a bounded phase-specific diagnosis.
            var nativeFailure = server.CortexNative is not null ? CortexNativePipeClient.HandshakeFailureSummary(ex) : "";
            var connectorFailure = phase == "connector" || server.CortexNative is null && server.ViaUserConnector && (ex is UnauthorizedAccessException || ex is HttpRequestException || ex.InnerException is HttpRequestException);
            return CortexInspectionResult(report, connectorFailure ? "connector-unavailable" : "provider-error",
                connectorFailure ? "The configured user-session connector is unavailable, incompatible or rejected authentication. Check connector status and provision it from this service."
                    : $"Cortex {phase} failed. Verify the installed executable, provider registration and Cortex's own status; no alternate provider was used." + nativeFailure);
        }
    }

    private static CortexConnectionReport CortexInspectionResult(CortexConnectionReport report, string state, string message)
    {
        report.State = state; report.Message = message;
        return report;
    }

    private static JsonElement CortexStatusBody(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(result.GetRawText()) > 131_072)
            throw new InvalidOperationException("Cortex status must be a bounded object.");
        if (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            throw new McpRemoteException("Cortex status reported an error.");
        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
            return structured;
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray().Take(8))
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        using var parsed = JsonDocument.Parse(text.GetString()!, new JsonDocumentOptions { MaxDepth = 32 });
                        if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("product", out _))
                            return parsed.RootElement.Clone();
                    }
                    catch (JsonException) { }
                }
        return result;
    }

    private static string? CortexStatusText(JsonElement body, string property)
    {
        if (!body.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is not null && text.Length <= 128 && !text.Any(char.IsControl) ? text : null;
    }
    private static bool? CortexStatusBoolean(JsonElement body, string property)
        => body.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
