using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class ProviderBindingTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var registration = new McpRegistration { Id = "race", Executable = Environment.ProcessPath!, Arguments = ["--fixture-mcp"], Enabled = true, ViaUserConnector = true };
        var direct = CerebralJson.Read<McpRegistration>(CerebralJson.Element(registration));
        direct.ViaUserConnector = false; direct.Url = null;
        check(ConnectorProtocol.ExecutionFingerprint(registration) == ConnectorProtocol.ExecutionFingerprint(direct),
            "connector execution identity ignores transport mode and URL");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveries = 0; var executions = 0; var hold = true; var identities = new List<string?>();
        var inspectFailure = false; var inspectionRequests = 0;
        const string privateInspectionError = "FIXTURE-PRIVATE-NON-NATIVE-ERROR";
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/health", () => new ConnectorHealth(ConnectorProtocol.Product, ConnectorProtocol.Version,
            Guid.NewGuid().ToString("N"), 123, DateTimeOffset.UtcNow, new string('a', 64), ["race"], false));
        app.MapPost("/mcp", async (HttpContext http) =>
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            var body = document.RootElement;
            identities.Add(body.TryGetProperty("expectedExecutionFingerprint", out var expected) ? expected.GetString() : null);
            if (inspectFailure && body.GetProperty("action").GetString() == "identity")
            {
                inspectionRequests++; http.Response.StatusCode = 502; await http.Response.WriteAsync(privateInspectionError); return;
            }
            if (body.GetProperty("action").GetString() == "list")
            {
                discoveries++;
                if (hold) { started.TrySetResult(); await release.Task.WaitAsync(http.RequestAborted); }
                await http.Response.WriteAsJsonAsync(new[] { new ToolDescriptor("mcp.race.status", "fixture",
                    CerebralJson.Element(new { type = "object" }), "mcp.race") }, CerebralJson.Options, http.RequestAborted);
            }
            else
            {
                executions++;
                await http.Response.WriteAsJsonAsync(new { isError = false, content = Array.Empty<object>(), structuredContent = new { state = "fixture" } },
                    CerebralJson.Options, http.RequestAborted);
            }
        });
        await app.StartAsync(); registration.Url = app.Urls.Single();
        var settings = new HostSettings { AccessKey = "fixture-access-key", McpServers = [registration], CortexNamespaces = [] };
        var profile = new AgentProfile { AllowedTools = ["mcp.race.status", "mcp.race.cortex_status"], Capabilities = ["mcp.race"] };
        var scope = new CortexAccessScope("fixture-project", registration.Id, [], null, true,
            ProviderFingerprint: CortexOperationPolicy.ProviderFingerprint(registration));
        var context = new ToolExecutionContext(profile, "fixture-session", "fixture-job");
        await using var dispatcher = new ToolDispatcher(() => settings);
        var call = new ToolCall("mcp.race.status", CerebralJson.Element(new { }));
        try
        {
            var stale = await dispatcher.ExecuteAsync(new("mcp.race.cortex_status", CerebralJson.Element(new { })), context with { Cortex = scope with { ProviderFingerprint = "different-provider" } }, default);
            check(!stale.Success && discoveries == 0, "stale project provider binding is rejected before discovery");
            var pending = dispatcher.ExecuteAsync(call, context, default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            registration.Arguments.Add("--changed-during-discovery");
            release.TrySetResult();
            var raced = await pending;
            check(!raced.Success && raced.Content.Contains("changed during discovery", StringComparison.Ordinal) && executions == 0,
                "provider registration changed during awaited discovery cannot execute a tool");
            registration.Arguments.RemoveAt(registration.Arguments.Count - 1); hold = false;
            check((await dispatcher.ExecuteAsync(call, context, default)).Success && executions == 1,
                "unchanged project provider binding dispatches normally");
            check(identities.Count == 3 && identities.All(x => x == ConnectorProtocol.ExecutionFingerprint(registration)),
                "every Cerebral connector discovery and call carries the expected execution identity");
            inspectFailure = true; var executionBeforeInspection = executions;
            var report = await dispatcher.InspectCortexAsync("race", default);
            check(report.State == "native-binding-required" && report.Transport == "mcp-user-connector" &&
                report.Message.Contains("enrolled native pipe", StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(report, CerebralJson.Options).Contains(privateInspectionError, StringComparison.Ordinal) &&
                !report.Message.Contains("Native handshake", StringComparison.Ordinal) && inspectionRequests == 0 && executions == executionBeforeInspection,
                "Cortex inspection refuses legacy HTTP before any identity request while unrelated MCP dispatch remains unchanged");
        }
        finally { release.TrySetResult(); await app.StopAsync(); }
    }

    public static async Task<int> RunPipeOnlyAsync()
    {
        var checks = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task<bool> ArchiveUnavailable(ToolDispatcher dispatcher, string reason)
        {
            try
            {
                // Deliberately invalid for archive parsing: missing enrollment must refuse
                // before local-path inspection, normalization, or process creation.
                await dispatcher.ExecuteHostUserPaoAsync(CerebralJson.Element(new { }), null, default);
                return false;
            }
            catch (InvalidOperationException error)
            { return error.Message.Contains(reason, StringComparison.OrdinalIgnoreCase); }
        }
        await using (var absent = new ToolDispatcher(() => new HostSettings()))
        {
            Check((await absent.InspectCortexAsync("absent", default)).State == "not-configured",
                "an optional unconfigured Cortex remains an explicit unavailable diagnostic");
            Check(await ArchiveUnavailable(absent, "enrolled native pipe"),
                "missing Cortex enrollment refuses archive work before any CLI or file access");
        }
        foreach (var viaConnector in new[] { false, true })
        {
            var settings = new HostSettings
            {
                McpServers = [new McpRegistration
                {
                    Id = "legacy", Enabled = true, Executable = "must-not-launch",
                    Arguments = [], ViaUserConnector = viaConnector,
                    Url = viaConnector ? "invalid://must-not-contact" : null
                }]
            };
            await using var dispatcher = new ToolDispatcher(() => settings);
            var report = await dispatcher.InspectCortexAsync("legacy", default);
            Check(report.State == "native-binding-required" &&
                report.Message.Contains("enrolled native pipe", StringComparison.Ordinal),
                "Cortex inspection refuses the legacy route before process or connector dispatch");
            Check(report.NativeIdentity is null && report.McpIdentity is null,
                "legacy registration is not represented as an authenticated Cortex identity");
            Check(await ArchiveUnavailable(dispatcher, "enrolled native pipe"),
                "an unrelated MCP registration cannot select a Cortex archive fallback");
        }
        var disabledSettings = new HostSettings
        {
            McpServers = [new McpRegistration { Id = "cortex", CortexNativeRequired = true, Enabled = false }]
        };
        await using (var disabled = new ToolDispatcher(() => disabledSettings))
            Check(await ArchiveUnavailable(disabled, "disabled"),
                "disabled native enrollment stays unavailable without a fallback");
        return checks;
    }

}
