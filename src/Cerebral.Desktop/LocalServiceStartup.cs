using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Desktop;

public sealed class LocalServiceStartup : IServiceStartupOperations
{
    public static async Task EnsurePipeAsync(ConnectionSnapshot saved, CancellationToken token)
    {
        var preferences = saved.Preferences ?? throw new InvalidOperationException(saved.Error ?? "Repair the saved connection.");
        if (preferences.Transport != "pipe") throw new ArgumentException("A saved local pipe is required.");
        var paths = DesktopInstancePaths.Current;
        var sourceSdk = paths.SourceLocal && Path.TrimEndingDirectorySeparator(Path.GetFullPath(preferences.LocalDataDirectory!)).Equals(
            paths.DataDirectory, StringComparison.OrdinalIgnoreCase) ? typeof(Cerebral.Desktop.Plugins.IDesktopPlugin).Assembly.Location : null;
        var connection = preferences.AutoStartLocalServices
            ? await LocalDevelopment.StartPipeAsync(token, preferences.LocalDataDirectory, sourceSdk)
            : await LocalDevelopment.DiscoverPipeAsync(token, preferences.LocalDataDirectory);
        if (connection is not null && connection.Endpoint != preferences.LocalEndpoint)
            ConnectionStore.SaveFromIntegrator(connection.ToPreferences(preferences.AutoStartLocalServices), saved.Revision);
    }

    public static async Task EnsureInstalledPipeAsync(ConnectionSnapshot saved, CancellationToken token)
    {
        var preferences = saved.Preferences ?? throw new InvalidOperationException(saved.Error ?? "Repair the saved connection.");
        if (preferences.Transport != "installed-pipe") throw new ArgumentException("A saved installed pipe is required.");
        _ = InstalledWorkerBindingStore.ReadRegistered()
            ?? throw new InvalidOperationException("The installed Worker owner binding is unavailable. Repair the installation before connecting.");
        if (preferences.AutoStartLocalServices)
            await ManagementProcess.RunAsync(["start"], token);
    }

    public static async Task EnsureInstalledUserPipeAsync(ConnectionSnapshot saved, CancellationToken token)
    {
        var preferences = saved.Preferences ?? throw new InvalidOperationException(saved.Error ?? "Repair the saved connection.");
        if (preferences.Transport != "installed-user-pipe")
            throw new ArgumentException("A saved installed user pipe is required.");
        if (preferences.AutoStartLocalServices)
            _ = await LocalDevelopment.StartInstalledUserPipeAsync(token).ConfigureAwait(false);
        else
            _ = await LocalDevelopment.DiscoverInstalledUserPipeAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The installed user Worker is stopped and automatic startup is disabled.");
    }

    public async Task<HostInfo?> ProbeWorkerAsync(string url, string accessKey, CancellationToken token)
    {
        using var client = new CerebralClient(url, accessKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { return await client.GetAsync<HostInfo>("health", cancellationToken: timeout.Token); }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { return null; }
    }
    public async Task<bool> IsWorkerInstalledAsync(CancellationToken token)
    {
        var status = await ManagementProcess.RunAsync(["status"], token);
        using var document = JsonDocument.Parse(status);
        var root = document.RootElement;
        if (root.GetProperty("service").GetString() != "PegasusCerebral") throw new InvalidDataException("Unexpected installed service identity.");
        return root.GetProperty("installed").GetBoolean();
    }
    public async Task StartInstalledWorkerAsync(CancellationToken token) => await ManagementProcess.RunAsync(["start"], token);
    public async Task StartLocalWorkerAsync(string url, CancellationToken token) => await LocalDevelopment.StartAsync(url, token);
    public Task<ConnectorStatus> ConnectorStatusAsync(CancellationToken token) => UserConnectorProcess.Manager.StatusAsync(token);
    public Task<ConnectorStatus> StartConnectorAsync(CancellationToken token) => UserConnectorProcess.StartAsync(token);
}
