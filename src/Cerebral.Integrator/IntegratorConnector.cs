using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly TextBox connectorState = Ui.Json();
    private Button? connectorProvision, connectorStart, connectorStop, connectorStatus;

    private void RefreshConnectorActions()
    {
        var allowed = ExistingWorkerAttachment is null;
        var reason = allowed ? null : "The owning workspace manages the user-session connector; this attached interface cannot inspect or change it.";
        foreach (var button in new[] { connectorProvision, connectorStart, connectorStop, connectorStatus })
            if (button is not null) { button.IsEnabled = allowed; button.ToolTip = reason; }
    }

    private UIElement ConnectorPage()
    {
        connectorState.IsReadOnly = true;
        connectorProvision = Ui.Button("Provision from service…", () => Run(ProvisionConnectorAsync), true);
        connectorStart = Ui.Button("Start connector", () => Run(StartConnectorAsync));
        connectorStop = Ui.Button("Stop connector", () => Run(StopConnectorAsync));
        connectorStatus = Ui.Button("Status", () => Run(RefreshConnectorAsync));
        RefreshConnectorActions();
        connectorState.Loaded += (_, _) => RefreshConnectorActions();
        return Ui.Page("User-session connector",
            "Provides the local Cortex pipe relay and runs other registered providers as your signed-in Windows user. It works independently of both windows. Provision from the connected local service, then start explicitly.",
            connectorState, connectorProvision, connectorStart, connectorStop, connectorStatus);
    }

    private async Task RefreshConnectorAsync()
    {
        RefreshConnectorActions();
        if (ExistingWorkerAttachment is not null)
        {
            connectorState.Text = "User-session connector management belongs to the owning workspace. This attached interface does not inspect or change the normal user connector.";
            return;
        }
        var state = await UserConnectorProcess.Manager.StatusAsync(Lifetime.Token);
        connectorState.Text = $"{state.State}\n{state.Message}\n\nAddress: {state.Url}\nProviders: {state.ProviderCount}\n" +
            $"Configuration: {UserConnectorProcess.Manager.ConfigurationPath}\n" +
            (state.InstanceId is null ? "" : $"Instance: {state.InstanceId}\nProcess: {state.ProcessId}\n") +
            "\nAdd or edit providers in Tools & MCP (or Cortex), then stop and provision again to apply changes.";
    }

    private async Task ProvisionConnectorAsync()
    {
        RequireWorkerManagementAllowed("provision a user-session connector");
        if (!IsConnected || ServiceConnection.Transport != "pipe" && (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var service) || !service.IsLoopback))
            throw new InvalidOperationException("Connect Integrator to the service on this computer before provisioning its user-session connector.");
        await RefreshSettings();
        var state = await UserConnectorProcess.Manager.StatusAsync(Lifetime.Token);
        var form = new FieldsDialog(this, "Provision user-session connector",
            "Copies enabled native Cortex pipe bindings and other providers routed to this connector. The connector must be stopped before replacing its configuration. A missing connector access key is created privately; Cortex enrollment remains separate.",
            "Provision").Add("url", "Connector loopback address", state.Url);
        if (form.ShowDialog() != true) return;
        await RefreshSettings();
        state = await UserConnectorProcess.Manager.StatusAsync(Lifetime.Token);
        var configuration = UserConnectorManager.CreateConfiguration(settings.McpServers, form.Value("url"), state.State == "not-configured" ? null : state.Url);
        var createdKey = string.IsNullOrEmpty(settings.AccessKey);
        var accessKey = createdKey ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : settings.AccessKey;
        UserConnectorManager.ValidateProvisioning(configuration, accessKey);
        var result = await UserConnectorProcess.Manager.ProvisionAsync(configuration, accessKey, Lifetime.Token);
        if (createdKey)
        {
            settings.AccessKey = accessKey;
            try { await SaveSettings(); }
            catch { throw new InvalidOperationException("Local connector files were provisioned, but the service key could not be saved. Reconnect and provision again before starting the connector."); }
        }
        await RefreshConnectorAsync();
        SetStatus(result.Message + (configuration.McpServers.Count == 0 ? " No eligible providers were configured; add them in Tools & MCP or Cortex, then provision again." : ""));
    }

    private async Task StartConnectorAsync()
    {
        RequireWorkerManagementAllowed("start a user-session connector");
        var result = await UserConnectorProcess.StartAsync(Lifetime.Token);
        await RefreshConnectorAsync(); SetStatus(result.Message);
    }

    private async Task StopConnectorAsync()
    {
        RequireWorkerManagementAllowed("stop a user-session connector");
        var result = await UserConnectorProcess.Manager.StopAsync(Lifetime.Token);
        await RefreshConnectorAsync(); SetStatus(result.Message);
    }
}
