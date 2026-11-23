using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in real local connector/Cortex acceptance. Never called by the ordinary fixture harness.</summary>
public static class ManagedConnectorProbe
{
    public static async Task<int> RunAsync(string cortexExecutable, string workerExecutable, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Managed connector process acceptance requires Windows.");
        ValidateExecutable(cortexExecutable, "PegasusCortex.exe");
        ValidateExecutable(workerExecutable, "Cerebral.Worker.exe");
        var temporaryBase = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directory = Path.Combine(temporaryBase, "cerebral-managed-connector-probe-" + Guid.NewGuid().ToString("N"));
        var manager = new UserConnectorManager(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var endpoint = $"http://127.0.0.1:{port}/";
        var registration = new McpRegistration
        { Id = "cortex", Executable = cortexExecutable, Arguments = [ConnectorProtocol.StableCortexMcpArgument], Enabled = true, ViaUserConnector = true, Url = endpoint };
        var configuration = UserConnectorManager.CreateConfiguration([registration], endpoint);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var settings = new HostSettings
        {
            AccessKey = key, McpServers = [registration],
            CortexNamespaces = ["project.pegasus-cerebral"], CortexWriteNamespaces = []
        };
        var profile = new AgentProfile
        {
            Id = "managed-cortex-probe", Name = "Isolated Cortex probe", ModelId = "no-inference", Capabilities = ["mcp.cortex"],
            AllowedTools = ["mcp.cortex.cortex_search", "mcp.cortex.cortex_read"], AllowedRoots = [directory]
        };
        const string query = "guided clarification author approval";
        const string recordId = "guided-clarification-author-approval";
        var passed = 0; var startAttempted = false; Exception? originalFailure = null;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + label);
            passed++; Console.WriteLine("PASS: " + label);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            Directory.CreateDirectory(directory);
            var provisioned = await manager.ProvisionAsync(configuration, key, deadline.Token);
            Check(provisioned.State == "stopped" && provisioned.ProviderCount == 1, "managed connector provisions one task-owned provider in stopped state");
            var configBytes = await File.ReadAllBytesAsync(manager.ConfigurationPath, deadline.Token);
            var configJson = Encoding.UTF8.GetString(configBytes);
            Check(!configJson.Contains(key, StringComparison.Ordinal) && !configJson.Contains("accessKey", StringComparison.OrdinalIgnoreCase),
                "managed connector configuration omits authentication material");

            startAttempted = true;
            var started = await manager.StartAsync(workerExecutable, deadline.Token);
            Check(started.State == "running" && started.InstanceId is not null && started.ProcessId is > 0,
                "managed connector starts and verifies the exact product, protocol, configuration and instance");
            var independentManager = new UserConnectorManager(directory);
            var status = await independentManager.StatusAsync(deadline.Token);
            Check(status.State == "running" && status.InstanceId == started.InstanceId && status.ProcessId == started.ProcessId,
                "a separate management client verifies the same persisted connector instance");
            var alreadyRunning = await independentManager.StartAsync(workerExecutable, deadline.Token);
            Check(alreadyRunning.InstanceId == started.InstanceId && alreadyRunning.ProcessId == started.ProcessId,
                "repeated managed start reuses the same connector process");

            var rejected = false;
            try { await manager.ProvisionAsync(configuration, key, deadline.Token); }
            catch (InvalidOperationException) { rejected = true; }
            var afterRejectedProvision = await File.ReadAllBytesAsync(manager.ConfigurationPath, deadline.Token);
            Check(rejected && configBytes.SequenceEqual(afterRejectedProvision),
                "live provisioning is rejected and preserves the active connector configuration");

            using (var unauthenticated = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) })
            using (var response = await unauthenticated.GetAsync(endpoint + "health", deadline.Token))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "connector health rejects unauthenticated requests");
            using (var authenticated = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) })
            {
                authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await authenticated.PostAsJsonAsync(endpoint + "control/stop",
                    new ConnectorStopRequest(Guid.NewGuid().ToString("N")), CerebralJson.Options, deadline.Token);
                Check(response.StatusCode == HttpStatusCode.Conflict, "connector stop rejects the wrong instance without terminating the running connector");
            }

            await using (var tools = new ToolDispatcher(() => settings))
            {
                var readiness = await tools.InspectCortexAsync("cortex", deadline.Token);
                Check(readiness.State == "ready" && readiness.Product == "Pegasus Cortex" && readiness.MissingTools.Count == 0,
                    "Cerebral owner diagnostics verify installed Cortex independently of any model profile");
                var exposed = await tools.ListAsync(profile, deadline.Token);
                Check(exposed.Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(profile.AllowedTools.Order(StringComparer.Ordinal)),
                    "installed Cortex exposes only the granted live search and typed-read schemas through the managed connector");
                var nativeTaskId = Guid.NewGuid().ToString("N");
                var context = new ToolExecutionContext(profile, nativeTaskId, "managed-cortex-probe", directory);
                profile.AllowedTools.Add("mcp.cortex.cortex_start");
                var brief = await tools.ExecuteAsync(new("mcp.cortex.cortex_start", CerebralJson.Element(new
                { taskId = nativeTaskId, namespaces = settings.CortexNamespaces, intent = "Read-only Cerebral independent client acceptance", maxUtf8Bytes = 4096, format = "compact" })), context, deadline.Token);
                RequireCompleteResponse(brief, "native Cerebral start");
                using var startDocument = JsonDocument.Parse(brief.Content);
                var startPayload = startDocument.RootElement.GetProperty("structuredContent");
                Check(startPayload.TryGetProperty("mode", out var startMode) && startMode.GetString() == "snapshot",
                    "installed Cortex accepts Cerebral's own persistent task identifier without Codex identity");
                profile.AllowedTools.Remove("mcp.cortex.cortex_start");
                var searched = await tools.ExecuteAsync(new("mcp.cortex.cortex_search", CerebralJson.Element(new
                {
                    text = query, namespaces = settings.CortexNamespaces, maxUtf8Bytes = 4096, limit = 2, format = "compact"
                })), context, deadline.Token);
                RequireCompleteResponse(searched, "Cortex search");
                var references = CortexContext.SearchReferences(searched.Content, settings.CortexNamespaces);
                Check(references.Count > 0, "bounded Cortex search returns an actual typed knowledge coordinate inside the explicit read namespaces");
                var selected = references.SingleOrDefault(x => x.Namespace == "project.pegasus-cerebral" && x.Id == recordId)
                    ?? throw new InvalidOperationException("The expected existing read-only probe record was not returned by search; no substitute or Cortex write was attempted.");
                var read = await tools.ExecuteAsync(new("mcp.cortex.cortex_read", CerebralJson.Element(new
                {
                    @namespace = selected.Namespace, recordType = "knowledge", recordId = selected.Id
                })), context, deadline.Token);
                RequireCompleteResponse(read, "Cortex typed read");
                var exact = CortexContext.ReadReference(read.Content, selected);
                Check(!string.IsNullOrWhiteSpace(exact.Content) && !string.IsNullOrWhiteSpace(exact.Reference.Revision),
                    "real Cortex typed read matches the searched namespace, record ID and knowledge type and supplies content plus revision");

                // These are isolated job/session records for the production context assembler, not an agent/model execution.
                var store = new StateStore(Path.Combine(directory, "state"));
                var events = new EventHub(); var hostConfiguration = new Configuration(store);
                var host = hostConfiguration.Get(); host.AccessKey = key; host.McpServers = [registration];
                host.CortexNamespaces = ["project.pegasus-cerebral"]; host.CortexWriteNamespaces = [];
                hostConfiguration.Save(host);
                const string projectId = "managed-cortex-project";
                store.Put("navigation", "sidebar", new NavigationState
                { Nodes = [new() { Id = projectId, Kind = "project", Title = "Temporary Cortex acceptance", Path = directory }] });
                var projects = new ProjectKnowledge(store, hostConfiguration, events);
                projects.Save(new() { ProjectId = projectId, CortexServerId = "cortex",
                    ReadNamespaces = ["project.pegasus-cerebral"], WriteNamespace = null, AutomaticRetrieval = true, RequireKnowledgeApproval = true });
                var session = new TaskSession
                {
                    Id = "managed-cortex-session", ProfileId = profile.Id, ProjectId = projectId, Title = "Temporary retrieval acceptance", Revision = 1,
                    Messages = [new() { Sequence = 1, Role = "user", Origin = "user", Content = query }]
                };
                var job = new AgentJob
                { Id = "managed-cortex-job", ProfileId = profile.Id, Profile = profile, SessionId = session.Id, Prompt = query, Status = "queued" };
                store.Put("agent", profile.Id, profile); store.Put("session", session.Id, session); store.Put("job", job.Id, job);
                var retrieval = new CortexContext(store, hostConfiguration, tools, events);
                var assembled = await retrieval.EnsureAsync(job, session, profile, exposed, deadline.Token);
                Check(assembled is { Status: "loaded" } && assembled.References.Any(x =>
                    x.Namespace == "project.pegasus-cerebral" && x.Id == recordId && !string.IsNullOrWhiteSpace(x.Revision)),
                    "production CortexContext loads exact references through the real connector for the selected temporary project");
                Check(assembled is not null && Encoding.UTF8.GetByteCount(assembled.Context) is > 0 and <= CortexContext.MaxContextBytes
                    && assembled.Context.Contains(recordId, StringComparison.Ordinal)
                    && assembled.Context.Contains("passive reference", StringComparison.Ordinal),
                    "production CortexContext returns at most 8192 UTF-8 bytes with exact source attribution and passive-reference labeling");
                Check(retrieval.Get(job.Id)?.Status == "loaded" && store.Get<TaskSession>("session", session.Id)!.Messages.Count == 2,
                    "temporary retrieval status and bounded session receipt are persisted locally");
                Console.WriteLine($"EVIDENCE: exact Cortex body bytes={Encoding.UTF8.GetByteCount(exact.Content)}, search/read envelopes={Encoding.UTF8.GetByteCount(searched.Content)}/{Encoding.UTF8.GetByteCount(read.Content)} bytes, assembled context={Encoding.UTF8.GetByteCount(assembled!.Context)} bytes. Raw provider content is not printed.");
            }

            var stopped = await independentManager.StopAsync(deadline.Token);
            if (stopped.State == "stopping")
            {
                for (var attempt = 0; attempt < 20 && stopped.State == "stopping"; attempt++)
                { await Task.Delay(100, deadline.Token); stopped = await independentManager.StatusAsync(deadline.Token); }
            }
            Check(stopped.State == "stopped", "authenticated manager stop confirms the exact connector has terminated");
            Check((await manager.StatusAsync(deadline.Token)).State == "stopped", "fresh status confirms no managed connector remains");
        }
        catch (Exception ex) { originalFailure = ex; throw; }
        finally
        {
            // Use an independent cleanup deadline even after cancellation. Never kill by name/port or erase
            // the only authentication record while a connector might still be running.
            var confirmedStopped = !startAttempted;
            string? cleanupError = null;
            if (startAttempted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    while (true)
                    {
                        var state = await manager.StatusAsync(cleanup.Token);
                        if (state.State is "stopped" or "not-configured") { confirmedStopped = true; break; }
                        if (state.State == "running") await manager.StopAsync(cleanup.Token);
                        else if (state.State is not ("starting" or "stopping"))
                            throw new InvalidOperationException("Connector shutdown could not be verified: " + state.State + ".");
                        await Task.Delay(200, cleanup.Token);
                    }
                }
                catch (Exception ex) { cleanupError = ex.GetType().Name; }
            }
            if (confirmedStopped)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                RemoveOwnedDirectory(directory, temporaryBase);
                Console.WriteLine("CLEANUP: exact managed connector stopped; task-owned configuration, key, instance and lock files removed.");
            }
            else
            {
                var failure = new IOException("Managed connector cleanup is unconfirmed (" + cleanupError + "). No unrelated process was terminated. Recovery files were retained at " + directory + "; inspect and stop that recorded instance before deleting them.");
                if (originalFailure is not null) throw new AggregateException("Probe operation and cleanup both failed.", originalFailure, failure);
                throw failure;
            }
        }
        Console.WriteLine($"RESULT: {passed} managed connector checks passed; actual local process, authenticated loopback control, installed read-only Cortex MCP and production context assembly evidence. No models, running worker settings or persistent services were changed.");
        return 0;
    }

    private static void RequireCompleteResponse(ToolResult result, string operation)
    {
        if (!result.Success || result.Truncated) throw new IOException(operation + " failed or was truncated. Provider output was not printed.");
        if (Encoding.UTF8.GetByteCount(result.Content) > 131_072) throw new IOException(operation + " exceeded its bounded response envelope.");
        using var document = JsonDocument.Parse(result.Content);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            throw new IOException(operation + " returned an MCP error or invalid envelope.");
    }

    private static void ValidateExecutable(string path, string expectedName)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.GetFileName(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provide the explicit existing absolute path to " + expectedName + ".");
    }

    private static void RemoveOwnedDirectory(string directory, string temporaryBase)
    {
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full)) return;
        if (!full.StartsWith(temporaryBase, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("cerebral-managed-connector-probe-", StringComparison.Ordinal)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Probe cleanup path validation failed.");
        var pending = new Stack<DirectoryInfo>(); pending.Push(new DirectoryInfo(full)); var count = 0;
        while (pending.TryPop(out var parent))
            foreach (var item in parent.EnumerateFileSystemInfos())
            {
                if (++count > 100 || !item.FullName.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Probe cleanup encountered unexpected or linked content; automatic cleanup stopped.");
                if (item is DirectoryInfo child) pending.Push(child);
            }
        Directory.Delete(full, recursive: true);
    }
}
