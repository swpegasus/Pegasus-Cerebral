using System.Net;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic loopback contract fixture, not live Cortex or desktop evidence.</summary>
public static class CortexConnectionTests
{
    public static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        var mode = "ready"; var requests = 0;
        var key = Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapGet("/health", async (HttpContext http) =>
        {
            requests++;
            if (mode == "authentication") { http.Response.StatusCode = 401; return; }
            if (mode == "cancel") { await Task.Delay(TimeSpan.FromSeconds(10), http.RequestAborted); return; }
            await http.Response.WriteAsJsonAsync(new ConnectorHealth(
                mode == "connector-identity" ? "Wrong connector" : ConnectorProtocol.Product, ConnectorProtocol.Version,
                Guid.NewGuid().ToString("N"), 123, DateTimeOffset.UtcNow, new string('a', 64),
                mode == "unprovisioned" ? [] : ["cortex"], false));
        });
        app.MapPost("/mcp", async (HttpContext http) =>
        {
            requests++;
            if (http.Request.Headers.Authorization.ToString() != "Bearer " + key) { http.Response.StatusCode = 401; return; }
            if (mode == "provider-failure") { http.Response.StatusCode = 502; await http.Response.WriteAsync("private-provider-detail"); return; }
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            var action = document.RootElement.GetProperty("action").GetString();
            switch (action)
            {
                case "identity":
                    await http.Response.WriteAsJsonAsync(new McpServerIdentity("2025-11-25", mode == "mcp-identity" ? "another-product" : "pegasus-cortex",
                        mode == "no-version" ? "" : "1.2026.09.10.12.17.A+fixture"), CerebralJson.Options); break;
                case "list":
                    var names = CortexConnectionContract.CoreTools.Where(name => mode != "missing-tool" || name != "cortex_finish");
                    var tools = names.Select(name => new ToolDescriptor("mcp.cortex." + name, "fixture",
                        mode == "bad-schema" && name == "cortex_read" ? CerebralJson.Element("invalid") : CerebralJson.Element(new { type = "object", properties = new { } }), "mcp.cortex")).ToArray();
                    await http.Response.WriteAsJsonAsync(tools, CerebralJson.Options); break;
                case "call":
                    if (document.RootElement.GetProperty("tool").GetString() != "cortex_status"
                        || document.RootElement.GetProperty("arguments").EnumerateObject().Any())
                        throw new Exception("Diagnostic must only call namespace-independent cortex_status with empty arguments.");
                    var status = new { product = mode == "status-product" ? "Another product" : "Pegasus Cortex",
                        version = "1.2026.09.10.12.17.A", state = mode == "not-ready" ? "blocked" : "ready", storageKind = "sqlite-v2", network = false, service = false };
                    if (mode == "text-status")
                        await http.Response.WriteAsJsonAsync(new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(status) } }, isError = false });
                    else await http.Response.WriteAsJsonAsync(new { structuredContent = status, isError = mode == "status-error" });
                    break;
                default: throw new Exception("Unexpected diagnostic operation.");
            }
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var host = new HostSettings { AccessKey = key, McpServers = [new() { Id = "cortex", Enabled = true, ViaUserConnector = true, Url = address + "/", Executable = "not-launched-by-fixture" }] };
            await using var dispatcher = new ToolDispatcher(() => host);
            Task<CortexConnectionReport> Inspect(CancellationToken ct = default) => dispatcher.InspectCortexAsync("cortex", ct);
            var before = JsonSerializer.Serialize(host, CerebralJson.Options);
            var report = await Inspect();
            Check(report.State == "ready" && report.AvailableTools.Count == 8 && report.MissingTools.Count == 0, "owner diagnostic accepts complete live-contract fixture without an agent profile");
            Check(report.McpIdentity?.Name == "pegasus-cortex" && report.Product == "Pegasus Cortex" && report.StorageKind == "sqlite-v2"
                && report.Network == false && report.Service == false, "diagnostic reports initialize and semantic status identities separately");
            Check(JsonSerializer.Serialize(host, CerebralJson.Options) == before, "read-only diagnostic preserves provider configuration and grants");
            var priorRequests = requests; host.McpServers[0].Enabled = false;
            Check((await Inspect()).State == "disabled" && requests == priorRequests, "disabled Cortex diagnostic does not contact its connector");
            Check((await dispatcher.InspectCortexAsync("absent", default)).State == "not-configured" && requests == priorRequests, "unconfigured diagnostic does not contact providers");
            host.McpServers[0].Enabled = true;
            foreach (var test in new[] {
                ("authentication", "connector-unavailable"), ("connector-identity", "connector-unavailable"),
                ("unprovisioned", "provider-error"), ("mcp-identity", "incompatible"), ("no-version", "incompatible"),
                ("missing-tool", "incompatible"), ("bad-schema", "incompatible"),
                ("status-product", "incompatible"), ("not-ready", "provider-error"), ("status-error", "provider-error") })
            {
                mode = test.Item1; report = await Inspect();
                Check(report.State == test.Item2, "Cortex inspection classifies " + mode);
                if (mode == "missing-tool") Check(report.MissingTools.SequenceEqual(["cortex_finish"]), "diagnostic identifies exact missing semantic tool");
            }
            mode = "text-status"; Check((await Inspect()).State == "ready", "diagnostic accepts bounded MCP text status fallback");
            mode = "provider-failure"; report = await Inspect();
            Check(report.State == "provider-error" && !JsonSerializer.Serialize(report).Contains("private-provider-detail"), "provider diagnostics keep private error payloads out of reports");
            mode = "cancel"; using var cancellation = new CancellationTokenSource(40); var cancelled = false;
            try { await Inspect(cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "owner cancellation propagates through connector diagnostic");

            var path = Path.Combine(Path.GetTempPath(), "cerebral-cortex-discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                var executable = Path.Combine(path, "PegasusCortex.exe");
                await File.WriteAllTextAsync(executable, "not executable; path validation fixture only");
                Check(InstalledCortexDiscovery.ValidateExplicitPath(executable) == executable, "installed Cortex explicit-path validation accepts an existing exact filename");
                var rejected = false; try { InstalledCortexDiscovery.ValidateExplicitPath("PegasusCortex.exe"); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "installed Cortex discovery rejects relative executable paths");
                var other = Path.Combine(path, "other.exe"); await File.WriteAllTextAsync(other, "");
                rejected = false; try { InstalledCortexDiscovery.ValidateExplicitPath(other); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "installed Cortex discovery rejects another executable filename");
            }
            finally { Directory.Delete(path, true); }
        }
        finally { using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(shutdown.Token); }
        Console.WriteLine($"RESULT: {passed} Cortex connection checks passed (deterministic loopback fixture; no provider or process launch).");
    }
}
