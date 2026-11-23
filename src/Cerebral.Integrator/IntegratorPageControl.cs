using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

internal sealed record ReceiverPageCommand(string? TargetPageId, string? PluginId, string ExpectedPageId, long ExpectedRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PanelId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPluginDirectory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPluginVersion = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
internal sealed record ReceiverPageCapabilities(string Schema, string[] Operations, string[] PageTargets,
    bool ExactPluginPanelSelection = false, bool ExactPluginGeneration = false);
internal sealed record ReceiverPageSnapshot(string State, DateTimeOffset CapturedAtUtc, int ProcessId,
    string ProcessStartUtcTicks, string? PageId, long Revision, bool Loaded, bool Visible,
    string? PluginId, string? PluginPanelId, bool PluginPanelLoaded, bool PluginPanelVisible,
    bool PluginPanelReady, string? PluginPanelErrorCode, string? SelectedPluginPanelId = null,
    bool PluginPanelAvailable = false, bool PluginPanelSelectionConfirmed = false,
    string? PluginPackageDirectory = null, string? PluginPackageVersion = null);

internal sealed class ConfigurationPageControlRefusal(string code) : InvalidOperationException(code)
{
    internal string Code { get; } = code;
}

// A submitted UI navigation may have selected or opened content before confirmation failed.
// Its caller must inspect the original receiver; retrying is not an absence proof.
internal sealed class ConfigurationPageNavigationUncertain(string code, Exception? inner = null)
    : InvalidOperationException(code, inner)
{
    internal string Code { get; } = code;
}

internal static class ConfigurationPageControl
{
    internal static readonly string[] PageTargets =
    [
        "ConfigurationTabDashboard", "ConfigurationTabSettings",
        "ConfigurationTabServiceExtensions", "ConfigurationTabDesktopPlugins"
    ];

    internal static ReceiverPageCapabilities Capabilities => new("pegasus.configuration-page-capabilities/2",
        ["snapshot.inspect", "capabilities.inspect", "page.inspect", "page.open", "plugin.panel.open", ConfigurationReadbackProtocol.PluginOwnerOperation, ConfigurationReadbackProtocol.PluginRuntimeOperation],
        [.. PageTargets], ExactPluginPanelSelection: true, ExactPluginGeneration: true);

    // Requests name one admitted immutable generation. Reject noncanonical paths rather
    // than silently turning a different spelling, stream, link or network path into a target.
    internal static bool ValidPluginGeneration(string? directory, string? version)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 4096 ||
            string.IsNullOrWhiteSpace(version) || version.Length > 256 || version.Any(char.IsControl)) return false;
        try
        {
            return directory.Equals(ExistingWorkerDesktopOptions.NormalizeLocalDirectory(directory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }

    internal static bool SamePluginGeneration(string? directory, string? version, string? expectedDirectory, string? expectedVersion) =>
        ValidPluginGeneration(directory, version) && ValidPluginGeneration(expectedDirectory, expectedVersion) &&
        string.Equals(directory, expectedDirectory, StringComparison.OrdinalIgnoreCase) && version == expectedVersion;

    internal static bool WindowPresented(Window? window) => window is { IsVisible: true } && window.WindowState != WindowState.Minimized;

    internal static bool ValidCommand(string operation, ReceiverPageCommand? command)
    {
        if (command is null || command.Extra is { Count: > 0 } || command.ExpectedRevision < 1 ||
            !ConfigurationReadbackProtocol.Identifier(command.ExpectedPageId, 128) ||
            !command.ExpectedPageId.StartsWith("ConfigurationTab", StringComparison.Ordinal)) return false;
        return operation switch
        {
            "page.open" => command.PluginId is null && command.PanelId is null &&
                command.ExpectedPluginDirectory is null && command.ExpectedPluginVersion is null && command.TargetPageId is not null &&
                PageTargets.Contains(command.TargetPageId, StringComparer.Ordinal),
            "plugin.panel.open" => command.TargetPageId is null && command.PluginId is { Length: > 0 and <= 128 } id &&
                id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') &&
                ConfigurationReadbackProtocol.Identifier(command.PanelId, 128) &&
                ValidPluginGeneration(command.ExpectedPluginDirectory, command.ExpectedPluginVersion),
            _ => false
        };
    }
}

public sealed partial class IntegratorWindow
{
    private long receiverPageRevision = 1;

    private void InitializeReceiverPageControl()
    {
        // The normal tab mounts own selection. This revision also catches an away-and-back
        // navigation between observation and an explicit page action.
        void Changed(object sender, SelectionChangedEventArgs args)
        {
            if (ReferenceEquals(args.OriginalSource, sender)) ReceiverPageChanged();
        }
        Tabs.SelectionChanged += Changed;
        foreach (var nested in Tabs.Items.OfType<TabItem>().Select(item => item.Content).OfType<TabControl>())
            nested.SelectionChanged += Changed;
    }

    private void ReceiverPageChanged()
    {
        Dispatcher.VerifyAccess();
        receiverPageRevision = checked(receiverPageRevision + 1);
    }

    private void RequireReceiverPageOwner(ReceiverExpected expected)
    {
        Dispatcher.VerifyAccess();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (Lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted ||
            expected.ProcessStartUtcTicks != process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            expected.ProcessId != Environment.ProcessId ||
            ExistingWorkerAttachment is not { } owner ||
            new ReceiverAttachment(owner.Endpoint.PipeName, owner.Endpoint.InstanceId,
                owner.WorkerDataDirectory, owner.InstallationId) != expected.Attachment ||
            DesktopInstancePaths.Current.DataDirectory != expected.ConfigurationStateDirectory)
            throw new ConfigurationPageControlRefusal("owner_binding_changed");
    }

    internal ReceiverPageSnapshot CaptureReceiverPage(ReceiverExpected expected)
    {
        RequireReceiverPageOwner(expected);
        return CapturePluginSettingsPage(expected.ProcessStartUtcTicks);
    }

    private (string? PageId, bool Loaded, bool Visible) SelectedConfigurationPagePresentation()
    {
        var category = Tabs.SelectedItem as TabItem;
        var page = (category?.Content as TabControl)?.SelectedItem as TabItem;
        var pageId = page is null ? null : AutomationProperties.GetAutomationId(page);
        var content = page?.Content as FrameworkElement;
        var loaded = page?.IsLoaded == true && content?.IsLoaded == true;
        // The Settings rail renders the selected tab content through a content-only template;
        // its TabItem header is intentionally not visible even while the page content is.
        var visible = ConfigurationPageControl.WindowPresented(this) && content?.IsVisible == true;
        return (pageId, loaded, visible);
    }

    private ReceiverPageSnapshot CapturePluginSettingsPage(string processStartUtcTicks)
    {
        var (pageId, loaded, visible) = SelectedConfigurationPagePresentation();
        var pluginWindow = desktopPluginConfigurationWindow;
        var selectedPanel = desktopPluginConfigurationTabs?.SelectedItem as TabItem;
        var panelId = selectedPanel is null ? null : AutomationProperties.GetAutomationId(selectedPanel);
        var panelContent = selectedPanel?.Content as FrameworkElement;
        var panelLoaded = selectedPanel?.IsLoaded == true && panelContent?.IsLoaded == true;
        var panelVisible = ConfigurationPageControl.WindowPresented(pluginWindow) && selectedPanel?.IsVisible == true &&
            panelContent?.IsVisible == true;
        var pluginId = configuredDesktopPluginId;
        var current = pluginId is not null && desktopPluginConfigurationScope?.MatchesCurrent() == true;
        var rawPanelId = selectedPanel?.Tag as string;
        var admitted = current && pluginId is not null ? ReadDesktopPluginConfigurationState(pluginId) : null;
        var declared = admitted is not null && rawPanelId is not null
            ? admitted.Panels.Where(panel =>
                panel.PluginId == pluginId && panel.Id == rawPanelId).ToArray() : null;
        // A label or automation ID alone is not an admitted panel identity. Retain the exact
        // declared view object from the current runtime/owner generation.
        var panelBound = current && declared is { Length: 1 } &&
            ReferenceEquals(selectedPanel?.Content, declared[0].View) &&
            panelId == $"ConfigurationPluginPanel.{pluginId}.{rawPanelId}";
        var panelAvailable = ServiceConnected && panelBound;
        var panelReady = panelAvailable && panelLoaded && panelVisible;
        var panelError = pluginWindow is null ? "panel_closed" : !current ? "plugin_owner_changed" :
            !panelBound ? "panel_identity_unavailable" : !panelLoaded ? "panel_not_loaded" :
            !panelVisible ? "panel_not_visible" : null;
        var state = !ServiceConnected ? "offline" : !loaded || !visible ? "partial" : "ready";
        return new(state, DateTimeOffset.UtcNow, Environment.ProcessId, processStartUtcTicks,
            pageId, receiverPageRevision, loaded, visible, pluginId, panelBound ? panelId : null,
            panelLoaded, panelVisible, panelReady, panelError, panelBound ? rawPanelId : null,
            panelAvailable, panelReady, panelBound ? admitted?.Directory : null, panelBound ? admitted?.Version : null);
    }

    internal ReceiverPageSnapshot OpenReceiverPage(ReceiverExpected expected, string operation, ReceiverPageCommand command)
    {
        RequireReceiverPageOwner(expected);
        if (!ConfigurationPageControl.ValidCommand(operation, command))
            throw new ConfigurationPageControlRefusal("page_arguments_invalid");
        var before = CaptureReceiverPage(expected);
        if (before.PageId != command.ExpectedPageId || before.Revision != command.ExpectedRevision)
            throw new ConfigurationPageControlRefusal("page_changed");
        if (!ServiceConnected || !ConfigurationPageControl.WindowPresented(this))
            throw new ConfigurationPageControlRefusal("page_not_ready");
        if (operation == "page.open")
        {
            var suffix = command.TargetPageId!["ConfigurationTab".Length..];
            SelectTab(suffix);
            return CaptureReceiverPage(expected);
        }
        if (before.PageId != "ConfigurationTabDesktopPlugins")
            throw new ConfigurationPageControlRefusal("plugin_page_required");
        var pluginId = command.PluginId!;
        var admitted = ReadDesktopPluginConfigurationState(pluginId)
            ?? throw new ConfigurationPageControlRefusal("plugin_panel_not_ready");
        RequireDesktopPluginConfigurationGeneration(admitted.Directory, admitted.Version,
            command.ExpectedPluginDirectory!, command.ExpectedPluginVersion!);
        _ = RequireDeclaredDesktopPluginConfigurationPanel(pluginId, command.PanelId!, admitted.Panels);
        try
        {
            var opened = ConfigureDesktopPluginById(pluginId, command.PanelId!,
                command.ExpectedPluginDirectory!, command.ExpectedPluginVersion!);
            if (!opened.IsCompletedSuccessfully)
                throw new ConfigurationPageNavigationUncertain("plugin_panel_navigation_unconfirmed");
            // The exact tab is selected synchronously. Loaded/visible may settle after Show;
            // the transport returns accepted until a later inspect proves exact readiness.
            var after = CaptureReceiverPage(expected);
            if (!ConfigurationPageControl.SamePluginGeneration(after.PluginPackageDirectory, after.PluginPackageVersion,
                command.ExpectedPluginDirectory, command.ExpectedPluginVersion))
                throw new ConfigurationPageNavigationUncertain("plugin_generation_unconfirmed");
            if (after.PluginId != pluginId || after.SelectedPluginPanelId != command.PanelId)
                throw new ConfigurationPageNavigationUncertain("plugin_panel_navigation_unconfirmed");
            return after;
        }
        catch (ConfigurationPageNavigationUncertain) { throw; }
        // This refusal is emitted only by the two pre-navigation generation guards in
        // ConfigureDesktopPlugin, before close, reparent, selection, restore or Show.
        catch (ConfigurationPageControlRefusal refusal) when (refusal.Code == "plugin_generation_changed") { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // Navigation was invoked, so an error cannot establish that no UI effect happened.
            throw new ConfigurationPageNavigationUncertain("plugin_panel_navigation_unconfirmed", error);
        }
    }
}
