using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;

internal static class CortexNativeInteropProbe
{
    // Real source-provider interoperability only. This entry point is test assembly code;
    // it refuses installed endpoints/keys and does not run PAO or load a model.
    public static async Task<int> RunAsync(string descriptorPath)
    {
        var fixtureRoot = Path.GetFullPath("C:\\Development\\Pegasus-Cortex\\.tmp\\native-interop");
        if (!Path.IsPathFullyQualified(descriptorPath) || !Within(descriptorPath, fixtureRoot))
            throw new ArgumentException("Choose the exact isolated Cortex source-harness descriptor.");
        descriptorPath = Path.GetFullPath(descriptorPath);
        ScopedFilePolicy.RejectLinks(descriptorPath);
        await using var descriptorFile = new FileStream(descriptorPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (descriptorFile.Length is < 1 or > 16_384) throw new InvalidDataException("Interop descriptor exceeds its bound.");
        var bytes = new byte[checked((int)descriptorFile.Length)]; await descriptorFile.ReadExactlyAsync(bytes);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
        var descriptor = document.RootElement;
        string Text(string field) => descriptor.GetProperty(field).GetString() ?? throw new InvalidDataException("Missing public fixture metadata.");
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        var credentialFile = Text("credentialFile");
        var space = Text("fixtureNamespace");
        if (Text("contract") != CortexNativeProtocol.Contract || Text("ownerSid") != owner.AccountSid ||
            descriptor.GetProperty("session").GetInt32() != owner.SessionId || space != "project.cerebral-interop" ||
            !Path.IsPathFullyQualified(credentialFile) || !Within(credentialFile, fixtureRoot))
            throw new UnauthorizedAccessException("Interop metadata is not the isolated current-user Cortex fixture.");
        var native = new CortexNativeConnection(Text("expectedHostExecutable"), credentialFile, owner.SessionId, PipeName: Text("nativePipe"));
        native.Validate();
        if (!native.PipeName!.StartsWith("pegasus-cortex-interop-", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Interop must use its unique test endpoint, never the installed native pipe.");
        var registration = new McpRegistration { Id = "cortex", Executable = native.HostExecutable, Enabled = true, ViaUserConnector = false, CortexNative = native };
        var host = new HostSettings { McpServers = [registration], CortexNamespaces = [space], CortexWriteNamespaces = [space] };
        var profile = new AgentProfile { Capabilities = ["mcp.cortex", "cortex.write"], AllowedTools = CortexConnectionContract.CoreTools.Select(x => "mcp.cortex." + x).ToList() };
        var context = new ToolExecutionContext(profile, Guid.NewGuid().ToString("N"), "native-source-interop");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        var id = "native-consumer-" + Guid.NewGuid().ToString("N");
        var content = "Native source interoperability verified — café.\nSecond line. " + id;
        var create = CerebralJson.Element(new { action = "create", @namespace = space, recordId = id,
            title = "Cerebral native source interoperability", content, kind = "observation", tier = "verified",
            provenance = new { source = "agent", evidence = "Isolated production native consumer and real source Cortex SQLite provider." },
            tags = new[] { "source-interop" }, idempotencyKey = id });
        await using (var dispatcher = new ToolDispatcher(() => host))
        {
            var report = await dispatcher.InspectCortexAsync("cortex", deadline.Token);
            Check(report.State == "ready" && report.Transport == "cortex-native-pipe" && report.McpIdentity is null && report.NativeIdentity?.Contract == CortexNativeProtocol.Contract,
                "actual source resident validates native identity, catalog and ready status");
            var tools = await dispatcher.ListAsync(profile, deadline.Token);
            Check(CortexConnectionContract.CoreTools.All(x => tools.Any(t => t.Name == "mcp.cortex." + x)), "all eight real semantic schemas are available to the consumer");
            var result = await dispatcher.ExecuteAsync(new("mcp.cortex.cortex_knowledge", create), context, deadline.Token);
            Check(result.Success && !result.Truncated, "actual SQLite create returns a matching complete committed receipt");
            result = await dispatcher.ExecuteAsync(new("mcp.cortex.cortex_knowledge", create), context, deadline.Token);
            Check(result.Success && Body(result).GetProperty("disposition").GetString() == "idempotent", "explicit idempotency retry retains the same record revision");
            await SearchAndReadAsync(dispatcher, "initial exact typed read");
            var denied = await dispatcher.ExecuteAsync(new("mcp.cortex.cortex_search", CerebralJson.Element(new { namespaces = new[] { "project.outside-fixture" }, text = id })), context, deadline.Token);
            Check(!denied.Success && denied.Content.Contains("enabled namespace", StringComparison.Ordinal), "Cerebral project namespace restrictions remain independent of Cortex routing defaults");
        }
        await using (var reopened = new ToolDispatcher(() => host))
            await SearchAndReadAsync(reopened, "fresh authenticated connection retains exact SQLite content");
        var relayRoot = Path.Combine(Path.GetTempPath(), "cerebral-native-source-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(relayRoot);
        try
        {
            var relay = new CortexNativeRelay("Pegasus.Cerebral.Interop." + Guid.NewGuid().ToString("N"), owner.AccountSid, owner.SessionId, owner.Executable);
            var forwarded = CerebralJson.Read<McpRegistration>(CerebralJson.Element(registration));
            forwarded.ViaUserConnector = true; forwarded.CortexNative = native with { Relay = relay };
            var relayHost = new HostSettings { AccessKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                McpServers = [forwarded], CortexNamespaces = [space], CortexWriteNamespaces = [space] };
            var configuration = UserConnectorManager.CreateConfiguration([forwarded]);
            configuration.ListenUrl = "http://127.0.0.1:0/";
            var configurationPath = Path.Combine(relayRoot, "connector.ini"); var accessPath = Path.Combine(relayRoot, "access.key");
            await File.WriteAllBytesAsync(configurationPath, JsonSerializer.SerializeToUtf8Bytes(configuration, CerebralJson.Options));
            await File.WriteAllTextAsync(accessPath, relayHost.AccessKey);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var running = ToolDispatcher.RunConnectorAsync(["--settings", configurationPath, "--key", accessPath], stop.Token);
            try
            {
                await using var throughRelay = new ToolDispatcher(() => relayHost);
                var report = await throughRelay.InspectCortexAsync("cortex", deadline.Token);
                Check(report.State == "ready" && report.NativeIdentity?.Contract == CortexNativeProtocol.Contract && report.McpIdentity is null,
                    "actual source provider is ready through the production owner-session relay");
                await SearchAndReadAsync(throughRelay, "real SQLite content survives both production native pipe hops");
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        finally { Directory.Delete(relayRoot, recursive: true); }
        Console.WriteLine($"RESULT: {checks} real source Cortex/consumer IPC and SQLite interoperability checks passed. Isolated namespace/database only; no installed provider, real PAO, LocalService or inference acceptance.");
        return 0;

        async Task SearchAndReadAsync(ToolDispatcher dispatcher, string label)
        {
            var search = await dispatcher.ExecuteAsync(new("mcp.cortex.cortex_search", CerebralJson.Element(new { namespaces = new[] { space }, text = id, limit = 4, format = "compact" })), context, deadline.Token);
            Check(search.Success && ContainsExact(Body(search), id), "bounded search discovers the stored exact reference");
            var read = await dispatcher.ExecuteAsync(new("mcp.cortex.cortex_read", CerebralJson.Element(new { @namespace = space, recordType = "knowledge", recordId = id, maxUtf8Bytes = 8192 })), context, deadline.Token);
            Check(read.Success && !read.Truncated && ContainsExact(Body(read), content), label);
        }
    }
    private static JsonElement Body(ToolResult result)
    { using var document = JsonDocument.Parse(result.Content); return document.RootElement.GetProperty("structuredContent").Clone(); }
    private static bool ContainsExact(JsonElement value, string text) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == text,
        JsonValueKind.Object => value.EnumerateObject().Any(x => ContainsExact(x.Value, text)),
        JsonValueKind.Array => value.EnumerateArray().Any(x => ContainsExact(x, text)),
        _ => false
    };
    private static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
