using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

// Explicitly opted-in, read-only installed-provider verification. Ordinary tests remain fixture-only.
internal static class InstalledCortexProbe
{
    public static async Task<int> RunAsync()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pegasus", "Cortex", "PegasusCortex.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Installed Cortex executable is unavailable.", executable);
        var root = Path.Combine(Path.GetTempPath(), "cerebral-cortex-probe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var registration = new McpRegistration { Id = "cortex", Executable = executable, Enabled = true, ViaUserConnector = true, Url = $"http://127.0.0.1:{port}/" };
        var settings = new HostSettings { AccessKey = Guid.NewGuid().ToString("N"), McpServers = [registration], CortexNamespaces = ["project.pegasus-cerebral", "knowledge.shared"] };
        var config = Path.Combine(root, "connector-settings.json"); var key = Path.Combine(root, "connector.key");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(settings, CerebralJson.Options)); await File.WriteAllTextAsync(key, settings.AccessKey);
        using var stop = new CancellationTokenSource();
        var connector = ToolDispatcher.RunConnectorAsync(["--settings", config, "--key", key, "--listen", registration.Url], stop.Token);
        try
        {
            var ready = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                using var client = new TcpClient();
                try { await client.ConnectAsync(IPAddress.Loopback, port); ready = true; break; }
                catch (SocketException) { await Task.Delay(50); }
            }
            if (!ready) throw new IOException("User connector did not start.");
            await using var tools = new ToolDispatcher(() => settings);
            var profile = new AgentProfile { Id = "cortex-probe", Capabilities = ["mcp.cortex"], AllowedTools = ["mcp.cortex.cortex_search"] };
            var exposed = await tools.ListAsync(profile, stop.Token);
            if (exposed.Count != 1 || exposed[0].Name != "mcp.cortex.cortex_search")
            {
                using var diagnostic = new HttpClient(); diagnostic.DefaultRequestHeaders.Authorization = new("Bearer", settings.AccessKey);
                using var body = new StringContent(JsonSerializer.Serialize(new { serverId = "cortex", action = "list" }), System.Text.Encoding.UTF8, "application/json");
                using var diagnosticResponse = await diagnostic.PostAsync(registration.Url + "mcp", body);
                var errorBody = await diagnosticResponse.Content.ReadAsStringAsync();
                throw new InvalidOperationException("Installed Cortex search schema was not exposed through the user connector: " + errorBody[..Math.Min(errorBody.Length, 2048)]);
            }
            Console.WriteLine("PASS: installed Cortex live schema through authenticated loopback user connector, read-only profile exposes only cortex_search");
            var context = new ToolExecutionContext(profile, Environment.GetEnvironmentVariable("CODEX_THREAD_ID") ?? "cortex-probe", "cortex-probe", root);
            var result = await tools.ExecuteAsync(new("mcp.cortex.cortex_search", CerebralJson.Element(new { text = "programming tenets", namespaces = settings.CortexNamespaces, maxUtf8Bytes = 4096, limit = 2, format = "compact" })), context, stop.Token);
            if (!result.Success) throw new InvalidOperationException("Installed Cortex read-only search failed: " + result.Content);
            using var response = JsonDocument.Parse(result.Content);
            if (response.RootElement.TryGetProperty("isError", out var error) && error.GetBoolean()) throw new InvalidOperationException("Installed Cortex returned an MCP error.");
            Console.WriteLine($"PASS: installed Cortex bounded read-only search succeeded through real MCP STDIO behind user connector; response envelope {result.Content.Length} characters, truncated={result.Truncated}");
            return 0;
        }
        finally
        {
            stop.Cancel();
            try { await connector; }
            finally
            {
                var full = Path.GetFullPath(root);
                if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("cerebral-cortex-probe-", StringComparison.Ordinal)) Directory.Delete(full, true);
                Console.WriteLine("CLEANUP: connector stopped; temporary settings/key removed; running worker profiles and model unchanged");
            }
        }
    }
}
