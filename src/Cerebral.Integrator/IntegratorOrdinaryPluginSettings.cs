using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    internal void ListenForOrdinaryPluginSettings(string userDataRoot)
    {
        Dispatcher.VerifyAccess();
        _ = ConfigurationPluginSettingsChannel.ListenAsync(userDataRoot, (request, token) =>
            Dispatcher.InvokeAsync(() => OpenOrdinaryPluginSettingsAsync(request, token),
                DispatcherPriority.Normal, token).Task.Unwrap(), Lifetime.Token);
    }

    private async Task<string> OpenOrdinaryPluginSettingsAsync(ConfigurationPluginSettingsRequest request, CancellationToken token)
    {
        // Initial readiness belongs to normal Configuration. The navigation request does not
        // select a connection or load a different plugin generation to make itself succeed.
        await InitialConnectionSettled.WaitAsync(token);
        if (!ServiceConnected) return "unavailable";
        var client = Client;
        var connection = ServiceConnection;
        var owner = configurationPluginOwner;
        bool CurrentConnection() => !Lifetime.IsCancellationRequested && ExistingWorkerAttachment is null &&
            ServiceConnected && ReferenceEquals(client, Client) && ReferenceEquals(connection, ServiceConnection) &&
            owner?.MatchesOwner() == true && ReferenceEquals(owner, configurationPluginOwner) &&
            request.Transport == connection.Transport &&
            request.ConnectionIdentity == ConfigurationPluginSettingsChannel.ConnectionKey(ServiceConnectionIdentity);

        if (!CurrentConnection())
        {
            SetStatus("Plugin settings request belongs to a different or unavailable Worker. Check the connection in both windows.", true);
            return "changed";
        }
        var health = await client.GetAsync<HostInfo>("health", cancellationToken: token);
        connection.ValidateHost(health);
        if (!CurrentConnection() || health.InstallationId != request.InstallationId)
        {
            SetStatus("The connected Worker changed before opening plugin settings. Refresh both windows and try again.", true);
            return "changed";
        }
        var active = ReadDesktopPluginConfigurationState(request.PluginId);
        if (active is null || !ConfigurationPageControl.SamePluginGeneration(active.Directory, active.Version,
            request.PackageDirectory, request.PackageVersion))
        {
            SetStatus("The requested plugin generation is not active here. Check Desktop plugins and refresh before trying again.", true);
            return "changed";
        }
        _ = RequireDeclaredDesktopPluginConfigurationPanel(request.PluginId, request.PanelId, active.Panels);
        token.ThrowIfCancellationRequested();

        DesktopApplicationInstance.Show(this);
        SelectTab("DesktopPlugins");
        await ConfigureDesktopPluginById(request.PluginId, request.PanelId, request.PackageDirectory, request.PackageVersion);
        // Show and WPF load can settle on the next dispatcher pass. Inspect the actual mounted
        // view, not the request's labels, before acknowledging that navigation completed.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, token);
        if (!CurrentConnection()) return "unconfirmed";
        var page = CapturePluginSettingsPage("");
        return page.State == "ready" && page.PageId == "ConfigurationTabDesktopPlugins" &&
            page.PluginId == request.PluginId && page.SelectedPluginPanelId == request.PanelId &&
            page.PluginPanelReady && page.PluginPanelSelectionConfirmed &&
            ConfigurationPageControl.SamePluginGeneration(page.PluginPackageDirectory, page.PluginPackageVersion,
                request.PackageDirectory, request.PackageVersion)
            ? "opened" : "unconfirmed";
    }
}
