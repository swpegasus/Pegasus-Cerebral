using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using Cerebral.Core;

namespace Cerebral.Client;

/// <summary>Explicit per-user connector lifecycle. The process is independent of either desktop window.</summary>
public sealed class UserConnectorManager
{
    public string DataDirectory { get; }
    public string ConfigurationPath => Path.Combine(DataDirectory, "connector.json");
    public string KeyPath => Path.Combine(DataDirectory, "access.key");
    private string InstancePath => Path.Combine(DataDirectory, "instance.json");
    private string RunLockPath => ConfigurationPath + ".run.lock";

    public UserConnectorManager(string? dataDirectory = null)
    {
        if (dataDirectory is not null && !Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("Connector data directory must be an absolute path.");
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(InstallationPaths.CurrentUserDataDirectory, "Connector"));
        RejectLinks(DataDirectory);
    }

    public static ConnectorConfiguration CreateConfiguration(IEnumerable<McpRegistration> providers, string listenUrl = ConnectorProtocol.DefaultUrl, string? previousListenUrl = null)
    {
        var endpoint = ConnectorProtocol.ValidateUrl(listenUrl).AbsoluteUri;
        var enabled = providers.Where(x => x.Enabled && x.ViaUserConnector).ToList();
        if (previousListenUrl is not null && ConnectorProtocol.ValidateUrl(previousListenUrl).AbsoluteUri is { } previous && previous != endpoint)
        {
            var excluded = enabled.Where(x => x.CortexNative is null && !string.IsNullOrWhiteSpace(x.Url) && ConnectorProtocol.ValidateUrl(x.Url).AbsoluteUri == previous)
                .Select(x => x.Id).ToList();
            if (excluded.Count != 0)
                throw new InvalidOperationException("Changing the managed connector address would leave enabled providers at its old address: "
                    + string.Join(", ", excluded) + ". Keep the current address or explicitly update those provider routes before provisioning.");
        }
        var configuration = new ConnectorConfiguration { ListenUrl = endpoint };
        foreach (var provider in enabled)
        {
            PortableRegistrationPolicy.RequireReady(provider);
            if (provider.CortexNativeRequired && provider.CortexNative is null)
                throw new ArgumentException("This Cortex definition requires a native owner-session binding and cannot use MCP.");
            if (provider.CortexNative is { } native)
            {
                native.Validate();
                var relay = native.Relay ?? throw new ArgumentException("Native Cortex connector providers require a local relay binding.");
                if (configuration.CortexRelay is not null && configuration.CortexRelay != relay)
                    throw new ArgumentException("One managed user connector cannot own conflicting Cortex relay bindings.");
                configuration.CortexRelay = relay;
                configuration.McpServers.Add(new() { Id = provider.Id, Executable = provider.Executable, Arguments = [],
                    Enabled = true, ViaUserConnector = false, CortexNativeRequired = true,
                    CortexNative = native with { Relay = null } });
                continue;
            }
            if (string.IsNullOrWhiteSpace(provider.Url)) continue; // Unrouted definitions are not silently reassigned.
            if (ConnectorProtocol.ValidateUrl(provider.Url).AbsoluteUri != endpoint) continue;
            configuration.McpServers.Add(new()
            {
                Id = provider.Id, Executable = provider.Executable, Arguments = provider.Arguments is null ? throw new ArgumentException("Connector provider arguments are required.") : [.. provider.Arguments],
                Enabled = true, ViaUserConnector = false, Url = null
            });
        }
        ConnectorProtocol.ValidateConfiguration(configuration);
        return configuration;
    }

    public CortexNativeRelay CreateCortexRelayBinding(string workerExecutable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Cortex relay requires Windows.");
        if (!Path.IsPathFullyQualified(workerExecutable) || !File.Exists(workerExecutable) ||
            !Path.GetFileName(workerExecutable).Equals("Cerebral.Worker.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the actual Cerebral Worker executable for the signed-in user connector.");
        RejectLinks(workerExecutable);
        using var identity = WindowsIdentity.GetCurrent(); using var process = Process.GetCurrentProcess();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("No signed-in connector owner is available.");
        if (sid is InstalledWorkerAdmission.SystemSid or InstalledWorkerBinding.LocalServiceSid)
            throw new UnauthorizedAccessException("Cortex relay must be provisioned by the signed-in owner.");
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(DataDirectory).ToUpperInvariant() + "\n" + sid + "\n" + process.SessionId)))[..32];
        return new("Pegasus.Cerebral.Cortex." + id, sid, process.SessionId, Path.GetFullPath(workerExecutable));
    }

    public async Task<ConnectorStatus> StatusAsync(CancellationToken token = default)
    {
        if (!File.Exists(ConfigurationPath)) return new("not-configured", ConnectorProtocol.DefaultUrl, "Provision the connector from Integrator before starting it.", 0);
        try
        {
            var saved = ReadSaved();
            ConnectorHealth? health;
            try { health = await ProbeAsync(saved.Configuration.ListenUrl, saved.Key, token); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or InvalidOperationException)
            { return new("unavailable", saved.Configuration.ListenUrl, SafeError(ex), saved.Configuration.McpServers.Count); }
            if (health is null)
                return new(RunLockHeld() ? "starting" : "stopped", saved.Configuration.ListenUrl,
                    RunLockHeld() ? "The connector owns its configuration but has not returned authenticated health. Retry status before starting again." : "Connector is stopped.",
                    saved.Configuration.McpServers.Count);
            var expected = ReadInstance();
            if (expected is null) return new("unmanaged", saved.Configuration.ListenUrl,
                "A connector is responding, but this manager has no matching instance record. No process will be started or stopped.", saved.Configuration.McpServers.Count);
            try
            {
                ConnectorProtocol.ValidateHealth(health, expected);
                if (expected.ConfigurationSha256 != saved.Hash) throw new InvalidOperationException("The saved connector configuration changed while its recorded instance was running. Stop using the original configuration before replacing it.");
            }
            catch (InvalidOperationException ex) { return new("mismatch", saved.Configuration.ListenUrl, ex.Message, saved.Configuration.McpServers.Count); }
            return new(health.Stopping ? "stopping" : "running", saved.Configuration.ListenUrl,
                health.Stopping ? "Connector is stopping and cancelling active provider calls." : "Authenticated connector instance is available.",
                saved.Configuration.McpServers.Count, health.InstanceId, health.ProcessId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new("invalid-config", ConnectorProtocol.DefaultUrl, SafeError(ex), 0); }
    }

    /// <summary>Input and filesystem preflight, reusable before UI configuration writes or a connector stop.</summary>
    public static void ValidateProvisioning(ConnectorConfiguration configuration, string accessKey)
    {
        ConnectorProtocol.ValidateConfiguration(configuration); ConnectorProtocol.ValidateKey(accessKey);
        foreach (var server in configuration.McpServers)
        {
            RejectLinks(server.Executable);
            if (!File.Exists(server.Executable)) throw new FileNotFoundException("A configured connector provider executable is unavailable on this computer.", server.Executable);
        }
    }

    public async Task<ConnectorStatus> ProvisionAsync(ConnectorConfiguration configuration, string accessKey, CancellationToken token = default)
    {
        ValidateProvisioning(configuration, accessKey);
        Directory.CreateDirectory(DataDirectory); RejectLinks(DataDirectory);
        using var operation = OperationLock();
        using var stopped = StoppedLock();
        if (File.Exists(ConfigurationPath))
        {
            var prior = ReadSaved();
            await RequireUnusedEndpointAsync(prior.Configuration.ListenUrl, prior.Key, token);
        }
        await RequireUnusedEndpointAsync(configuration.ListenUrl, accessKey, token);
        token.ThrowIfCancellationRequested();
        WriteAtomic(KeyPath, Encoding.UTF8.GetBytes(accessKey));
        WriteAtomic(ConfigurationPath, JsonSerializer.SerializeToUtf8Bytes(configuration, CerebralJson.Options));
        return new("stopped", configuration.ListenUrl, "Connector provisioned. Start it explicitly; no startup registration was created.", configuration.McpServers.Count);
    }

    public async Task<ConnectorStatus> StartAsync(string workerExecutable, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Managed user-session launch currently supports Windows. The connector CLI can run directly on its supported host.");
        if (!Path.IsPathFullyQualified(workerExecutable) || !File.Exists(workerExecutable)
            || !Path.GetFileName(workerExecutable).Equals("Cerebral.Worker.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provide the verified absolute Cerebral.Worker.exe path from the installed or local build layout.");
        RejectLinks(workerExecutable);
        if (!File.Exists(ConfigurationPath)) throw new InvalidOperationException("Provision the connector before starting it.");
        using var operation = OperationLock();
        var saved = ReadSaved();
        var status = await StatusAsync(token);
        if (status.State == "running") return status;
        if (status.State != "stopped") throw new InvalidOperationException(status.Message);
        using (StoppedLock()) { }
        await RequireUnusedEndpointAsync(saved.Configuration.ListenUrl, saved.Key, token);
        var instance = new ConnectorInstance(Guid.NewGuid().ToString("N"), saved.Hash, DateTimeOffset.UtcNow);
        WriteAtomic(InstancePath, JsonSerializer.SerializeToUtf8Bytes(instance, CerebralJson.Options));
        var start = new ProcessStartInfo(workerExecutable)
        {
            // The connector outlives its management command. Shell execution prevents it from
            // inheriting redirected stdout/stderr handles from Integrator, setup, or an atomic
            // updater and holding those callers open until the connector itself stops.
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(workerExecutable)!
        };
        foreach (var argument in new[] { "connector", "--settings", ConfigurationPath, "--key", KeyPath,
            "--listen", saved.Configuration.ListenUrl, "--instance", instance.InstanceId })
            start.ArgumentList.Add(argument);
        // Do not attach a kill-on-close job or lifetime token to this process.
        using var process = Process.Start(start) ?? throw new IOException("Connector could not start.");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token); startup.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("Connector exited during startup with code " + process.ExitCode + ". Check its configuration and provider paths, then retry.");
                var health = await ProbeAsync(saved.Configuration.ListenUrl, saved.Key, startup.Token);
                if (health is not null)
                {
                    ConnectorProtocol.ValidateHealth(health, instance);
                    if (health.ProcessId != process.Id) throw new InvalidOperationException("Connector startup returned a different process identity. No process was stopped.");
                    return new("running", saved.Configuration.ListenUrl, "Connector started independently of the desktop windows.", saved.Configuration.McpServers.Count, health.InstanceId, health.ProcessId);
                }
                await Task.Delay(200, startup.Token);
            }
        }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Connector startup confirmation was interrupted. The process may still be starting; use Status before another start."); }
    }

    public async Task<ConnectorStatus> StopAsync(CancellationToken token = default)
    {
        if (!File.Exists(ConfigurationPath)) return await StatusAsync(token);
        using var operation = OperationLock();
        var saved = ReadSaved();
        var status = await StatusAsync(token);
        if (status.State == "stopped") return status;
        if (status.State != "running" || status.InstanceId is null) throw new InvalidOperationException(status.Message);
        using var http = Http(saved.Configuration.ListenUrl, saved.Key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "control/stop") { Content = JsonContent.Create(new ConnectorStopRequest(status.InstanceId), options: CerebralJson.Options) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new IOException("Connector refused the stop request with HTTP " + (int)response.StatusCode + ". No fallback process termination was attempted.");
        var reply = JsonSerializer.Deserialize<ConnectorStopReply>(await ReadBoundedAsync(response.Content, timeout.Token), CerebralJson.Options)
            ?? throw new IOException("Connector returned no stop acknowledgement.");
        if (reply.Protocol != ConnectorProtocol.Version || reply.InstanceId != status.InstanceId || !reply.Stopping)
            throw new InvalidOperationException("Connector stop acknowledgement did not match the requested instance.");
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var health = await ProbeAsync(saved.Configuration.ListenUrl, saved.Key, timeout.Token);
                if (health is null && !RunLockHeld()) return new("stopped", saved.Configuration.ListenUrl, "Connector stopped.", saved.Configuration.McpServers.Count);
                if (health is not null && health.InstanceId != status.InstanceId) throw new InvalidOperationException("A different connector now owns the endpoint. No further action was taken.");
                await Task.Delay(100, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
        }
        token.ThrowIfCancellationRequested();
        return new("stopping", saved.Configuration.ListenUrl, "Stop was acknowledged; completion is still pending. Refresh Status.", saved.Configuration.McpServers.Count, status.InstanceId, status.ProcessId);
    }

    private sealed record Saved(ConnectorConfiguration Configuration, string Key, string Hash);
    private Saved ReadSaved()
    {
        var bytes = ReadFile(ConfigurationPath, ConnectorProtocol.MaxConfigurationBytes);
        var configuration = JsonSerializer.Deserialize<ConnectorConfiguration>(bytes, CerebralJson.Options) ?? throw new ArgumentException("Connector configuration is empty.");
        ConnectorProtocol.ValidateConfiguration(configuration);
        var key = Encoding.UTF8.GetString(ReadFile(KeyPath, 4096)).Trim(); ConnectorProtocol.ValidateKey(key);
        return new(configuration, key, ConnectorProtocol.ConfigurationHash(bytes));
    }
    private ConnectorInstance? ReadInstance()
    {
        if (!File.Exists(InstancePath)) return null;
        return JsonSerializer.Deserialize<ConnectorInstance>(ReadFile(InstancePath, ConnectorProtocol.MaxControlBytes), CerebralJson.Options)
            ?? throw new ArgumentException("Connector instance record is empty.");
    }
    private FileStream OperationLock()
    {
        RejectLinks(DataDirectory);
        var path = Path.Combine(DataDirectory, "management.lock"); RejectLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another connector management operation is in progress."); }
    }
    private FileStream StoppedLock()
    {
        RejectLinks(RunLockPath);
        try { return new FileStream(RunLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("The connector is running or starting. Stop it before changing configuration or starting another instance."); }
    }
    private bool RunLockHeld()
    {
        RejectLinks(RunLockPath);
        if (!File.Exists(RunLockPath)) return false;
        try { using var file = new FileStream(RunLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
    private static async Task RequireUnusedEndpointAsync(string url, string key, CancellationToken token)
    {
        if (await ProbeAsync(url, key, token) is not null)
            throw new InvalidOperationException("A connector is already responding at this address. Stop the recorded instance before provisioning or starting.");
    }
    private static HttpClient Http(string url, string key)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = ConnectorProtocol.ValidateUrl(url), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
    private static async Task<ConnectorHealth?> ProbeAsync(string url, string key, CancellationToken token)
    {
        using var http = Http(url, key);
        // Windows may take about two seconds to report a refused loopback connection.
        // Keep a larger bounded budget so refusal remains distinct from an unresponsive listener.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync("health", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new IOException("Connector address returned HTTP " + (int)response.StatusCode + "; authenticated identity is unavailable. No process was started or stopped.");
            return JsonSerializer.Deserialize<ConnectorHealth>(await ReadBoundedAsync(response.Content, timeout.Token), CerebralJson.Options)
                ?? throw new IOException("Connector returned an empty health response.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Connector health timed out; its running state is unconfirmed."); }
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        using var output = new MemoryStream(); await using var input = await content.ReadAsStreamAsync(token);
        var buffer = new byte[4096]; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > ConnectorProtocol.MaxControlBytes) throw new IOException("Connector control response exceeds its bound.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private static byte[] ReadFile(string path, int maximum)
    {
        RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new ArgumentException("A connector configuration file exceeds its bound.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void RejectLinks(string path)
    {
        for (string? item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Connector state and executables must not traverse filesystem links.");
    }
    private static string SafeError(Exception error) => error is JsonException ? "Connector returned invalid bounded JSON." :
        error.Message.Length <= 600 ? error.Message : error.Message[..600];
}
