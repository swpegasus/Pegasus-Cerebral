using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cerebral.Tools;

internal static class UserSessionConnector
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || args[i] is not ("--settings" or "--listen" or "--key" or "--instance") || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Usage: connector --settings <absolute-json-path> --key <absolute-key-file> [--listen http://127.0.0.1:6124/] [--instance <guid>]");
        }
        if (!options.TryGetValue("--settings", out var settingsPath) || !options.TryGetValue("--key", out var keyPath)
            || !Path.IsPathFullyQualified(settingsPath) || !Path.IsPathFullyQualified(keyPath))
            throw new ArgumentException("Connector requires absolute settings and key file paths.");
        ToolPolicy.RejectLinks(settingsPath); ToolPolicy.RejectLinks(keyPath); ToolPolicy.RejectLinks(settingsPath + ".run.lock");
        using var runLock = new FileStream(settingsPath + ".run.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var configurationBytes = await ReadBoundedFileAsync(settingsPath, ConnectorProtocol.MaxConfigurationBytes, ct);
        var configurationText = new UTF8Encoding(false, true).GetString(configurationBytes).TrimStart('\uFEFF');
        var configuration = JsonSerializer.Deserialize<ConnectorConfiguration>(configurationText, CerebralJson.Options)
            ?? throw new ArgumentException("Invalid connector settings.");
        using (var document = JsonDocument.Parse(configurationText))
        {
            // Existing manual HostSettings connector files remain readable; the provisioner writes only the narrow schema.
            if (!document.RootElement.TryGetProperty("schemaVersion", out _))
            {
                if (configuration.McpServers.Any(x => x.CortexNative is not null || x.CortexNativeRequired))
                    throw new ArgumentException("Native Cortex requires the narrow provisioned connector configuration; a legacy HostSettings file cannot downgrade it to stdio.");
                configuration.McpServers = configuration.McpServers.Where(x => x.Enabled).Select(x => new McpRegistration
                { Id = x.Id, Executable = x.Executable, Arguments = x.Arguments, Enabled = true, ViaUserConnector = false, RequiresConfiguration = x.RequiresConfiguration }).ToList();
            }
        }
        ConnectorProtocol.ValidateConfiguration(configuration);
        var key = new UTF8Encoding(false, true).GetString(await ReadBoundedFileAsync(keyPath, 4096, ct)).TrimStart('\uFEFF').Trim(); ConnectorProtocol.ValidateKey(key);
        var uri = ConnectorProtocol.ValidateUrl(options.GetValueOrDefault("--listen", configuration.ListenUrl));
        var instanceId = options.GetValueOrDefault("--instance", Guid.NewGuid().ToString("N"));
        if (!Guid.TryParseExact(instanceId, "N", out _)) throw new ArgumentException("Connector instance must be a GUID in N format.");
        var configurationHash = ConnectorProtocol.ConfigurationHash(configurationBytes);
        var startedAt = DateTimeOffset.UtcNow;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); // Do not retain MCP arguments or bearer credentials in request logs.
        builder.Services.Configure<HostOptions>(value => value.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.WebHost.ConfigureKestrel(value => { value.Limits.MaxRequestBodySize = ToolPolicy.MaxInput; value.Listen(IPAddress.Parse(uri.Host), uri.Port); });
        await using var app = builder.Build();
        using var capacity = new SemaphoreSlim(4, 4);
        var stopping = false;
        app.Use(async (http, next) =>
        {
            var supplied = http.Request.Headers.Authorization.ToString(); var expected = "Bearer " + key;
            if (http.Request.Headers.ContainsKey("Origin") || http.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            { http.Response.StatusCode = 403; return; }
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
            { http.Response.StatusCode = 401; return; }
            await next(http);
        });
        app.MapGet("/health", () => new ConnectorHealth(ConnectorProtocol.Product, ConnectorProtocol.Version, instanceId,
            Environment.ProcessId, startedAt, configurationHash, configuration.McpServers.Select(x => x.Id).ToArray(), stopping));
        app.MapPost("/control/stop", async (HttpContext http) =>
        {
            if (http.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            { http.Response.StatusCode = 415; return; }
            using var controlTimeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            controlTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var buffer = new MemoryStream(); var chunk = new byte[1024]; int read;
                while ((read = await http.Request.Body.ReadAsync(chunk, controlTimeout.Token)) > 0)
                {
                    if (buffer.Length + read > 1024) { http.Response.StatusCode = 413; return; }
                    buffer.Write(chunk, 0, read);
                }
                var request = JsonSerializer.Deserialize<ConnectorStopRequest>(buffer.ToArray(), CerebralJson.Options);
                if (request?.InstanceId != instanceId) { http.Response.StatusCode = 409; return; }
                stopping = true;
                http.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
                await http.Response.WriteAsJsonAsync(new ConnectorStopReply(ConnectorProtocol.Version, instanceId, true), CerebralJson.Options, http.RequestAborted);
            }
            catch (JsonException) { http.Response.StatusCode = 400; }
            catch (OperationCanceledException) { if (!http.Response.HasStarted) http.Response.StatusCode = 408; }
        });
        app.MapPost("/mcp", async (HttpContext http) =>
        {
            if (stopping) { http.Response.StatusCode = 503; return; }
            if (http.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true) { http.Response.StatusCode = 415; return; }
            if (!await capacity.WaitAsync(0, http.RequestAborted)) { http.Response.StatusCode = 429; return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, ct, app.Lifetime.ApplicationStopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { MaxDepth = 64 }, timeout.Token);
                var body = document.RootElement; var serverId = ToolPolicy.Text(body, "serverId");
                var server = configuration.McpServers.SingleOrDefault(x => x.Id == serverId)
                    ?? throw new UnauthorizedAccessException("Server is not registered and enabled in this connector.");
                if (server.CortexNative is not null)
                    throw new UnauthorizedAccessException("Native Cortex calls require the provisioned local pipe, not the MCP HTTP adapter.");
                if (body.TryGetProperty("expectedExecutionFingerprint", out var expectedProvider) &&
                    (expectedProvider.ValueKind != JsonValueKind.String || expectedProvider.GetString() != ConnectorProtocol.ExecutionFingerprint(server)))
                    throw new UnauthorizedAccessException("The connector provider executable or arguments differ from the requested registration. Reprovision or refresh the connection.");
                // Always launch the registered stdio executable in this user session. Never recurse through a connector URL.
                await using var client = await McpStdioClient.ConnectAsync(server, timeout.Token);
                var tools = await client.ListToolsAsync(server.Id, timeout.Token);
                object response;
                switch (ToolPolicy.Text(body, "action"))
                {
                    case "identity": response = client.ServerIdentity; break;
                    case "list": response = tools; break;
                    case "call":
                        var name = ToolPolicy.Text(body, "tool"); var arguments = body.GetProperty("arguments");
                        var descriptor = tools.SingleOrDefault(x => x.Name == $"mcp.{server.Id}.{name}")
                            ?? throw new ArgumentException("Tool is not exposed by the registered server.");
                        ToolDispatcher.ValidateSchema(arguments, descriptor.InputSchema);
                        response = await client.CallToolAsync(name, arguments, timeout.Token); break;
                    default: throw new ArgumentException("Unknown connector action.");
                }
                var json = JsonSerializer.Serialize(response, CerebralJson.Options);
                if (Encoding.UTF8.GetByteCount(json) > ToolPolicy.MaxInput) throw new InvalidOperationException("Connector response exceeds 1 MiB.");
                http.Response.ContentType = "application/json"; await http.Response.WriteAsync(json, timeout.Token);
            }
            catch (OperationCanceledException) { if (!http.Response.HasStarted) http.Response.StatusCode = 504; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or McpRemoteException)
            {
                if (!http.Response.HasStarted) { http.Response.StatusCode = ex is UnauthorizedAccessException ? 403 : ex is McpRemoteException ? 422 : 502; await http.Response.WriteAsJsonAsync(new { error = ToolPolicy.Limit(ex.Message).Content }, cancellationToken: http.RequestAborted); }
            }
            finally { capacity.Release(); }
        });
        var nativeClients = configuration.McpServers.Where(x => x.CortexNative is not null)
            .ToDictionary(x => x.Id, x => new CortexNativePipeClient(x.CortexNative!), StringComparer.Ordinal);
        using var stoppingToken = CancellationTokenSource.CreateLinkedTokenSource(ct, app.Lifetime.ApplicationStopping);
        try
        {
            var relay = CortexNativeRelayServer.RunAsync(configuration, key, nativeClients, capacity, stoppingToken.Token);
            var web = app.RunAsync(stoppingToken.Token);
            // A relay startup failure must also stop the management listener; do not report a
            // healthy user connector when its required native transport never started.
            if (configuration.CortexRelay is not null)
            {
                await Task.WhenAny(web, relay);
                stoppingToken.Cancel();
                await Task.WhenAll(web, relay);
            }
            else await web;
            return 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return 0; }
        finally { foreach (var client in nativeClients.Values) await client.DisposeAsync(); }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maximum, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        if (input.Length > maximum) throw new ArgumentException("Connector configuration exceeds its size limit.");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token); return bytes;
    }
}
