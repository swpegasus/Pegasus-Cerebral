using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in installed-provider acceptance from an enrolled actual Worker executable.
/// Never reads a key in this test driver, modifies the installed Cerebral store or runs inference.</summary>
internal static class CortexNativeWorkerAcceptance
{
    /// <summary>Owner-authorized Source development binding only. Uses the normal owner API;
    /// does not provision/change the installed connector, grants, credentials or Cerebral installation.</summary>
    public static async Task SwitchSourceAsync(string workerExecutable, string credentialFile, string sourceData)
    {
        var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral", "Source");
        sourceData = Path.GetFullPath(sourceData);
        if (!OperatingSystem.IsWindows() || !sourceData.Equals(Path.GetFullPath(allowed), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(sourceData, "cerebral.db")))
            throw new ArgumentException("This opt-in switch is limited to the existing default Source development store.");
        ScopedFilePolicy.RejectLinks(sourceData);
        var cortex = InstalledCortexDiscovery.ReadRegistered() ?? throw new InvalidOperationException("Cortex is not registered.");
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        var native = new CortexNativeConnection(cortex.NativeHostPath, credentialFile, owner.SessionId); native.Validate();
        // Refuse to start/recover unrelated accepted work as a side effect of this binding update.
        var store = new StateStore(sourceData);
        if (store.ActiveJobs().Any(JobScheduler.IsActive)) throw new InvalidOperationException("Finish accepted Source jobs before switching its provider.");
        int unresolvedCancelled;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(sourceData, "engine.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM workflow_runs WHERE status NOT IN ('Completed','Failed','Cancelled') AND COALESCE(json_extract(payload,'$.cancelRequested'),0) <> 1;";
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidOperationException("Finish accepted Source workflows before switching its provider.");
            // Cancelled historical tests with unknown effects remain unresolved in the same journal.
            // They cannot authorize a replay or be marked complete by this connection-only update.
            command.CommandText = "SELECT COUNT(*) FROM workflow_runs WHERE status NOT IN ('Completed','Failed','Cancelled');";
            unresolvedCancelled = checked((int)(long)command.ExecuteScalar()!);
        }
        SqliteConnection.ClearAllPools();
        LocalWorkerHostTests.ChildWorker? child = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            child = await LocalWorkerHostTests.ChildWorker.StartAsync(sourceData, Path.Combine(sourceData, "Plugins"), deadline.Token, workerExecutable: workerExecutable);
            using var client = new CerebralClient(child.Endpoint);
            var current = await client.GetAsync<HostSettings>("settings.get", cancellationToken: deadline.Token);
            var beforeProfiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: deadline.Token);
            var profilesHash = System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(beforeProfiles, CerebralJson.Options));
            var previous = current.McpServers.SingleOrDefault(x => x.Id == "cortex");
            var registration = previous is null ? new McpRegistration { Id = "cortex" }
                : CerebralJson.Read<McpRegistration>(CerebralJson.Element(previous));
            registration.Executable = cortex.ExecutablePath; registration.Arguments = []; registration.Url = null;
            registration.Enabled = true; registration.RequiresConfiguration = false; registration.CortexNativeRequired = false;
            registration.ViaUserConnector = false; registration.CortexNative = native;
            if (previous is null) current.McpServers.Add(registration);
            else current.McpServers[current.McpServers.IndexOf(previous)] = registration;
            var saved = await client.GetAsync<HostSettings>("settings.save", current, cancellationToken: deadline.Token);
            try
            {
                var report = await client.GetAsync<CortexConnectionReport>("cortex.connection.inspect", new { serverId = "cortex" }, cancellationToken: deadline.Token);
                if (report.State != "ready" || report.Transport != "cortex-native-pipe" || report.NativeIdentity?.ClientId != "cerebral-source")
                    throw new InvalidOperationException("Source native binding did not pass identity/readiness: " + report.Message);
                var afterProfiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: deadline.Token);
                if (!profilesHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(afterProfiles, CerebralJson.Options))))
                    throw new InvalidOperationException("Source agent profiles changed during the connection switch.");
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, sourceOnly = true, hostRevision = saved.Revision,
                    transport = report.Transport, providerVersion = report.Version, clientId = report.NativeIdentity.ClientId,
                    agentProfilesAndGrantsPreserved = true, defaultWorker = saved.DefaultModelWorkerId, modelOperationsIssued = false,
                    unresolvedCancelledHistoricalRunsRetained = unresolvedCancelled,
                    installedConsumerChanged = false, connectorProvisioned = false, credentialRotated = false }, CerebralJson.Options));
            }
            catch
            {
                var latest = await client.GetAsync<HostSettings>("settings.get", cancellationToken: deadline.Token);
                if (latest.Revision != saved.Revision) throw new InvalidOperationException("Source settings changed concurrently; inspect the provider binding before retrying.");
                if (previous is null) latest.McpServers.RemoveAll(x => x.Id == "cortex");
                else latest.McpServers[latest.McpServers.FindIndex(x => x.Id == "cortex")] = previous;
                await client.GetAsync<HostSettings>("settings.save", latest, cancellationToken: deadline.Token);
                throw;
            }
            var stopped = await client.CommandAsync("service.stop-local", cancellationToken: deadline.Token);
            if (!stopped.Success) throw new InvalidOperationException("Owned Source Worker did not stop normally.");
            await child.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (child is not null) await child.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    public static async Task RunAsync(string workerExecutable, string credentialFile, string space)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows native acceptance only.");
        if (space != "project.cerebral-native-acceptance")
            throw new ArgumentException("Use the explicitly reserved native acceptance namespace.");
        var cortex = InstalledCortexDiscovery.ReadRegistered() ?? throw new InvalidOperationException("Cortex is not registered.");
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        var native = new CortexNativeConnection(cortex.NativeHostPath, credentialFile, owner.SessionId);
        native.Validate();
        var root = Path.Combine(Path.GetTempPath(), "cerebral-native-worker-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data"); var plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(plugins);
        LocalWorkerHostTests.ChildWorker? child = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var checks = 0;
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks++; Console.WriteLine("PASS: " + text); }
        var id = "worker-native-" + Guid.NewGuid().ToString("N");
        var content = "Actual Worker native acceptance — café.\nSecond line. " + id;
        try
        {
            LocalWorkerHostTests.Seed(data, root, 9);
            var store = new StateStore(data); var configuration = new Configuration(store);
            var settings = configuration.Get();
            settings.McpServers = [new() { Id = "cortex", Executable = cortex.ExecutablePath, Enabled = true,
                ViaUserConnector = false, CortexNative = native }];
            settings.CortexNamespaces = [space]; settings.CortexWriteNamespaces = [space];
            configuration.Save(settings);
            store.Put("agent", "native-probe", new AgentProfile { Id = "native-probe", Name = "Native acceptance",
                Capabilities = ["mcp.cortex", "cortex.write"],
                AllowedTools = CortexConnectionContract.CoreTools.Select(x => "mcp.cortex." + x).ToList() });
            SqliteConnection.ClearAllPools();
            child = await LocalWorkerHostTests.ChildWorker.StartAsync(data, plugins, deadline.Token, workerExecutable: workerExecutable);
            using (var client = new CerebralClient(child.Endpoint))
            {
                var health = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
                Check(health.LocalRun && Path.GetFullPath(health.DataDirectory) == Path.GetFullPath(data),
                    "exact enrolled Worker executable owns an isolated secured-pipe host");
                await Inspect(client);
                var tools = await client.GetAsync<List<ToolDescriptor>>("tools.list", new { profileId = "native-probe" }, cancellationToken: deadline.Token);
                Check(CortexConnectionContract.CoreTools.All(x => tools.Any(t => t.Name == "mcp.cortex." + x)),
                    "actual Worker discovers all eight installed native semantic schemas");
                var create = new { action = "create", @namespace = space, recordId = id,
                    title = "Cerebral actual Worker native acceptance", content, kind = "observation", tier = "verified",
                    provenance = new { source = "agent", evidence = "Actual Source Worker executable, secured pipe and installed native Cortex store." },
                    tags = new[] { "native-acceptance" }, idempotencyKey = id };
                var created = await Tool(client, "cortex_knowledge", create);
                Check(created.Success && !created.Truncated, "installed Cortex commits one bounded acceptance record through the actual Worker");
                var repeated = await Tool(client, "cortex_knowledge", create);
                Check(repeated.Success && Body(repeated).GetProperty("disposition").GetString() == "idempotent",
                    "explicit repeated write reconciles to the same installed record");
                await Read(client);
                var denied = await Tool(client, "cortex_search", new { namespaces = new[] { "project.outside-acceptance" }, text = id });
                Check(!denied.Success, "Cerebral's independent namespace restriction remains enforced");
                await Stop(client);
            }
            await child.DisposeAsync(); child = null;
            child = await LocalWorkerHostTests.ChildWorker.StartAsync(data, plugins, deadline.Token, workerExecutable: workerExecutable);
            using (var client = new CerebralClient(child.Endpoint))
            {
                await Inspect(client); await Read(client); await Stop(client);
            }
            Console.WriteLine($"RESULT: {checks} actual Worker / secured owner pipe / installed Cortex native-store checks passed; " +
                $"record {space}/knowledge/{id} retained. Isolated Cerebral data; no MCP, LocalService, PAO or inference acceptance.");
        }
        finally
        {
            if (child is not null) await child.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("cerebral-native-worker-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected acceptance cleanup path.");
            Directory.Delete(full, recursive: true);
        }

        async Task Inspect(CerebralClient client)
        {
            var report = await client.GetAsync<CortexConnectionReport>("cortex.connection.inspect", new { serverId = "cortex" }, cancellationToken: deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { report.State, report.Message, report.Transport,
                nativeClient = report.NativeIdentity?.ClientId, report.Version }, CerebralJson.Options));
            Check(report.State == "ready" && report.Transport == "cortex-native-pipe" && report.McpIdentity is null &&
                report.NativeIdentity?.Contract == CortexNativeProtocol.Contract && report.NativeIdentity.ClientId == "cerebral-source",
                "installed provider authenticates the actual enrolled Source Worker over the native pipe");
        }
        async Task<ToolResult> Tool(CerebralClient client, string name, object arguments) =>
            await client.GetAsync<ToolResult>("tools.call", new { profileId = "native-probe", name = "mcp.cortex." + name, arguments }, cancellationToken: deadline.Token);
        async Task Read(CerebralClient client)
        {
            var search = await Tool(client, "cortex_search", new { namespaces = new[] { space }, text = id, limit = 4, format = "compact" });
            Check(search.Success && search.Content.Contains(id, StringComparison.Ordinal), "bounded search discovers the exact committed record");
            var read = await Tool(client, "cortex_read", new { @namespace = space, recordType = "knowledge", recordId = id, maxUtf8Bytes = 8192 });
            Check(read.Success && !read.Truncated && Contains(Body(read), content), "exact typed read preserves Unicode and newline content");
        }
        async Task Stop(CerebralClient client)
        {
            var result = await client.CommandAsync("service.stop-local", cancellationToken: deadline.Token);
            Check(result.Success, "owned actual Worker stops through its secured local command");
            await child!.WaitForExitAsync(deadline.Token);
        }
    }

    private static JsonElement Body(ToolResult result)
    { using var document = JsonDocument.Parse(result.Content); return document.RootElement.GetProperty("structuredContent").Clone(); }
    private static bool Contains(JsonElement value, string text) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == text,
        JsonValueKind.Object => value.EnumerateObject().Any(x => Contains(x.Value, text)),
        JsonValueKind.Array => value.EnumerateArray().Any(x => Contains(x, text)), _ => false
    };
}
