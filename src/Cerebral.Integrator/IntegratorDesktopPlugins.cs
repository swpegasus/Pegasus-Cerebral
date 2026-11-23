using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly string desktopPluginDataRoot;
    private readonly DesktopPluginCatalog desktopCatalog;
    private readonly ConfigurationManagedPluginOwnerBinding? managedPluginOwnerBinding;
    private readonly bool seedBundledDesktopPlugins;
    private readonly DataGrid desktopPluginList = Ui.Grid(("Plugin", "Name", 2), ("Version", "Version", 1), ("Enabled", "Enabled", 1), ("Description", "Description", 4), ("Error", "Error", 3));
    private readonly TextBox desktopPluginSearch = Ui.Input();
    private readonly ComboBox desktopPluginFilter = new() { Width = 170, ItemsSource = new[] { "All plugins", "Enabled", "Disabled", "Configurable", "Needs attention" }, SelectedIndex = 0 };
    private readonly ComboBox desktopPluginManage = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private Button? desktopPluginManageGo;
    private readonly TextBlock desktopPluginDetailsName = Ui.Text("", 12);
    private readonly TextBlock desktopPluginDetailsVersion = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock desktopPluginDetailsStatus = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock desktopPluginDetailsId = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock desktopPluginDetailsLocation = Ui.Text("", 12, Ui.Muted);
    private IReadOnlyList<DesktopPluginInfo> desktopPluginItems = [];
    private DesktopPluginHostAdapter? configurationPluginHost;
    private ConfigurationPluginOwnerScope? configurationPluginOwner;
    private DesktopPluginRuntime? configurationPlugins;
    private Window? desktopPluginConfigurationWindow;
    private TabControl? desktopPluginConfigurationTabs;
    private string? configuredDesktopPluginId;
    private DesktopPluginConfigurationScope? desktopPluginConfigurationScope;
    private string? desktopPluginCleanupCursor;

    private void InitializeConfigurationDesktopPlugins()
    {
        CreateConfigurationDesktopPluginRuntime();
        Loaded += (_, _) =>
        {
            // An explicitly isolated catalog still exercises the ordinary load path in attachment checks.
            if (seedBundledDesktopPlugins && Environment.GetCommandLineArgs().Any(x => x.StartsWith("--check-", StringComparison.Ordinal) || x == "--diagnostics")) return;
            if (seedBundledDesktopPlugins)
            {
                var root = BundledDesktopPluginPaths.DirectoryPath;
                foreach (var bundled in Directory.Exists(root)
                             ? Directory.EnumerateFiles(root, DesktopPluginCatalog.ManifestFileName, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
                             : Enumerable.Empty<string>())
                {
                    try { desktopCatalog.SeedBundled(bundled, BundledDesktopPluginPaths.EnableOnFirstInstall(bundled)); }
                    catch (Exception ex) { SetStatus($"Bundled plugin {Path.GetFileName(Path.GetDirectoryName(bundled))} could not be prepared: " + ex.Message, true); }
                }
            }
            RefreshDesktopPluginCatalog();
            ReloadConfigurationDesktopPlugins();
        };
        Closed += (_, _) => DisposeConfigurationDesktopPlugins();
    }

    private ConfigurationPluginOwnerState ConfigurationPluginOwner() =>
        new(IsConnected ? Client : null, ServiceConnectionIdentity, queueConnectionEpoch, IsConnected);

    private void CreateConfigurationDesktopPluginRuntime()
    {
        configurationPluginOwner = new(ConfigurationPluginOwner(), () => Dispatcher.CheckAccess()
            ? ConfigurationPluginOwner() : Dispatcher.Invoke(ConfigurationPluginOwner));
        configurationPluginHost = configurationPluginOwner.CreateAdapter(Dispatcher, text => SetStatus(text),
            desktopPluginDataRoot, ConfigurationProductPaths);
        var runtime = new DesktopPluginRuntime(desktopCatalog, configurationPluginHost, Dispatcher);
        configurationPlugins = runtime;
        runtime.Changed += (_, _) =>
        {
            if (ReferenceEquals(configurationPlugins, runtime)) RefreshConfigurationDesktopPlugins();
        };
    }

    private void EnsureConfigurationDesktopPluginOwner()
    {
        if (configurationPluginOwner?.MatchesOwner() == true) return;
        CloseDesktopPluginConfiguration();
        var previous = configurationPlugins;
        configurationPlugins = null; configurationPluginHost = null; configurationPluginOwner = null;
        // Retirement cancels old host access; the existing load ledger preserves any drain/cleanup barrier.
        previous?.Dispose();
        CreateConfigurationDesktopPluginRuntime();
    }

    private bool ReloadConfigurationDesktopPlugins(DesktopPluginLoadReservation? admission = null)
    {
        try
        {
            EnsureConfigurationDesktopPluginOwner();
            if (configurationPlugins is null)
                throw new InvalidOperationException("The configuration plugin runtime is unavailable.");
            configurationPlugins.LoadEnabled(admission);
            var failures = configurationPlugins.Status.Where(item => item.Error is not null)
                .Select(item => item.Id + ": " + item.Error).ToList();
            foreach (var selected in desktopCatalog.List().Where(item => item.Enabled && item.Error is null))
            {
                var actual = configurationPlugins.GetLoadedPackage(selected.Id);
                if (actual is null || !actual.Value.Directory.Equals(selected.Directory, StringComparison.OrdinalIgnoreCase) || actual.Value.Version != selected.Version)
                    failures.Add(selected.Id + ": the selected package is not active in this Configurator; refresh or restart after pending cleanup.");
            }
            if (failures.Count == 0) return true;
            SetStatus("Package settings are saved; configuration activation needs attention: " +
                string.Join("; ", failures.Take(4)) + (failures.Count > 4 ? "; additional plugin diagnostics are available in Inspect." : ""), true);
            return false;
        }
        catch (Exception ex) { SetStatus("Plugin setup panels could not refresh: " + ex.Message, true); return false; }
    }

    private void MountConfigurationDesktopPlugins()
    {
        ReconcileDesktopPluginConfiguration();
        if (configurationPlugins is null) return;
        ApplyDesktopPluginFilter();
        var failures = configurationPlugins.Status.Where(x => x.Error is not null).ToList();
        if (failures.Count > 0)
            SetStatus("Desktop plugin load failed: " + string.Join("; ", failures.Take(3).Select(x => $"{x.Id}: {x.Error}")), true);
    }

    private void RefreshConfigurationDesktopPlugins()
    {
        IReadOnlyList<DesktopPluginInfo> plugins;
        try { if (!desktopCatalog.TryList(out plugins)) return; }
        catch (Exception error) { DesktopPluginCatalogUnavailable(error); return; }
        RefreshConfigurationDesktopPlugins(() => plugins);
    }

    private void RefreshConfigurationDesktopPlugins(Func<IReadOnlyList<DesktopPluginInfo>> readCatalog)
    {
        RefreshDesktopPluginCatalog(readCatalog);
        MountConfigurationDesktopPlugins();
    }

    private void DisposeConfigurationDesktopPlugins()
    {
        CloseDesktopPluginConfiguration();
        configurationPlugins?.Dispose(); configurationPlugins = null; configurationPluginHost = null; configurationPluginOwner = null;
    }

    private DesktopProductPaths ConfigurationProductPaths()
    {
        // Project only the managed plugin environment. Preferences and the receiver continue
        // using this process's independent Configuration UI state.
        return managedPluginOwnerBinding is { } owner
            ? owner.ProductPaths(settings.ProjectsDirectory, settings.ModelDirectory, settings.ToolsDirectory, settings.SharedDirectory)
            : DesktopInstancePaths.Current.ProductPaths(settings.ProjectsDirectory, settings.ModelDirectory,
                settings.ToolsDirectory, settings.SharedDirectory);
    }
    private UIElement DesktopPluginsPage()
    {
        InitializeDesktopPluginSelection();
        InitializeDesktopPluginManageControls();
        desktopPluginSearch.Width = 300;
        desktopPluginSearch.ToolTip = "Filter installed plugins by name, ID, version, or description";
        System.Windows.Automation.AutomationProperties.SetAutomationId(desktopPluginList, "DesktopPluginList");
        System.Windows.Automation.AutomationProperties.SetAutomationId(desktopPluginSearch, "DesktopPluginSearch");
        System.Windows.Automation.AutomationProperties.SetAutomationId(desktopPluginFilter, "DesktopPluginFilter");
        desktopPluginSearch.TextChanged += (_, _) => ApplyDesktopPluginFilter();
        desktopPluginFilter.SelectionChanged += (_, _) => ApplyDesktopPluginFilter();
        var filterBar = Ui.Bar(Ui.Text("Find", 13, Ui.Muted), desktopPluginSearch, Ui.Text("Show", 13, Ui.Muted), desktopPluginFilter);
        foreach (var label in filterBar.Children.OfType<TextBlock>()) label.Margin = new Thickness(0, 7, 8, 0);
        var body = new DockPanel();
        DockPanel.SetDock(filterBar, Dock.Top); body.Children.Add(filterBar);
        DockPanel.SetDock(desktopPluginSelectionBar!, Dock.Top); body.Children.Add(desktopPluginSelectionBar!);
        DockPanel.SetDock(desktopPluginSelectionDetails!, Dock.Bottom); body.Children.Add(desktopPluginSelectionDetails!);
        body.Children.Add(desktopPluginList);
        RefreshDesktopPluginCatalog();
        var install = Ui.Button("Install…", () => Run(InstallDesktopPlugin), true);
        System.Windows.Automation.AutomationProperties.SetAutomationId(install, "DesktopPluginInstall");
        var manageLabel = Ui.Text("Manage", 13, Ui.Muted);
        manageLabel.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetLabeledBy(desktopPluginManage, manageLabel);
        return Ui.Page("Desktop plugins", "Select one or more plugins. Right-click for actions on the selection.",
            body, install, manageLabel, desktopPluginManage, desktopPluginManageGo!);
    }
    private void InitializeDesktopPluginManageControls()
    {
        // Keep selection, right-click handling and the existing guarded bulk operations in their
        // selection owner. This page composes a compact action chooser over those same handlers.
        desktopPluginSelectionBar!.Children.Clear();
        desktopPluginSelectionBar.Children.Add(desktopPluginSelectionCount!);
        foreach (var (id, name) in new[]
        {
            ("configure", "Configure…"), ("inspect", "Inspect"),
            ("update", "Update packages…"), ("enable", "Enable"), ("disable", "Disable"),
            ("export", "Export packages…"), ("remove", "Remove"),
            ("clear", "Clear selection"), ("refresh", "Refresh list"), ("cleanup", "Clean up removed packages")
        })
            desktopPluginManage.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        desktopPluginManage.SelectedIndex = 8;
        System.Windows.Automation.AutomationProperties.SetAutomationId(desktopPluginManage, "DesktopPluginManage");
        desktopPluginManageGo = Ui.Button("Go", () => Run(RunDesktopPluginManageAction));
        System.Windows.Automation.AutomationProperties.SetAutomationId(desktopPluginManageGo, "DesktopPluginManageGo");
        desktopPluginManage.SelectionChanged += (_, _) => UpdateDesktopPluginManageActions();
        desktopPluginList.SelectionChanged += (_, _) => UpdateDesktopPluginManageActions();

        if (desktopPluginSelectionDetails!.Child is ScrollViewer previousDetails) previousDetails.Content = null;
        var metadata = new Grid();
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Detail(string label, TextBlock value)
        {
            var row = metadata.RowDefinitions.Count;
            metadata.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Ui.Text(label, 12, Ui.Muted);
            name.Margin = new Thickness(0, 2, 12, 2);
            value.Margin = new Thickness(0, 2, 0, 2);
            value.TextWrapping = TextWrapping.NoWrap;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            System.Windows.Automation.AutomationProperties.SetLabeledBy(value, name);
            Grid.SetRow(name, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            metadata.Children.Add(name); metadata.Children.Add(value);
        }
        Detail("Name", desktopPluginDetailsName);
        Detail("Version", desktopPluginDetailsVersion);
        Detail("Status", desktopPluginDetailsStatus);
        Detail("Description", desktopPluginSelectionDescriptionText!);
        Detail("Plugin ID", desktopPluginDetailsId);
        Detail("Location", desktopPluginDetailsLocation);
        desktopPluginSelectionDetails!.Child = new ScrollViewer
        {
            MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = metadata
        };
        UpdateDesktopPluginManageActions();
    }

    private bool CanRunDesktopPluginManageAction(string id)
    {
        var selected = desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().ToArray();
        return id switch
        {
            "configure" => CanConfigureSelectedDesktopPlugin(),
            "inspect" => selected.Length == 1,
            "update" or "export" or "clear" => selected.Length > 0,
            "enable" => selected.Any(plugin => !plugin.Enabled),
            "disable" => selected.Any(plugin => plugin.Enabled),
            "remove" => selected.Length > 0 && selected.All(plugin => !plugin.Enabled),
            "refresh" or "cleanup" => true,
            _ => false
        };
    }

    private void UpdateDesktopPluginManageActions()
    {
        if (desktopPluginManageGo is null) return;
        var configureReason = DesktopPluginConfigureUnavailableReason();
        foreach (var item in desktopPluginManage.Items.OfType<ComboBoxItem>())
        {
            if (Equals(item.Tag, "configure")) SetDesktopPluginConfigureAvailability(item, configureReason);
            else item.IsEnabled = item.Tag is string id && CanRunDesktopPluginManageAction(id);
        }
        if (desktopPluginManage.SelectedItem is ComboBoxItem { Tag: "configure" })
            SetDesktopPluginConfigureAvailability(desktopPluginManageGo, configureReason);
        else
        {
            desktopPluginManageGo.IsEnabled = desktopPluginManage.SelectedItem is ComboBoxItem { Tag: string action } &&
                CanRunDesktopPluginManageAction(action);
            desktopPluginManageGo.ToolTip = null;
            ToolTipService.SetShowOnDisabled(desktopPluginManageGo, false);
            System.Windows.Automation.AutomationProperties.SetHelpText(desktopPluginManageGo, "");
        }
        if (desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().ToArray() is not [var plugin]) return;
        desktopPluginDetailsName.Text = plugin.Name;
        desktopPluginDetailsVersion.Text = plugin.Version;
        desktopPluginDetailsStatus.Text = plugin.Error is null ? plugin.Enabled ? "Enabled" : "Disabled" : "Needs attention: " + plugin.Error;
        desktopPluginDetailsStatus.Foreground = plugin.Error is null ? Ui.Muted : Ui.ErrorText;
        desktopPluginSelectionDescriptionText!.Text = plugin.Description;
        desktopPluginDetailsId.Text = plugin.Id;
        desktopPluginDetailsLocation.Text = plugin.Directory;
        foreach (var value in new[] { desktopPluginDetailsName, desktopPluginDetailsVersion, desktopPluginDetailsStatus,
            desktopPluginSelectionDescriptionText!, desktopPluginDetailsId, desktopPluginDetailsLocation })
            value.ToolTip = value.Text;
    }

    private Task RunDesktopPluginManageAction()
    {
        var id = (desktopPluginManage.SelectedItem as ComboBoxItem)?.Tag as string;
        if (id is null || !CanRunDesktopPluginManageAction(id))
            throw new InvalidOperationException("Choose an action available for the current plugin selection.");
        return id switch
        {
            "configure" => ConfigureDesktopPlugin(),
            "inspect" => InspectDesktopPlugin(),
            "update" => UpdateDesktopPlugin(),
            "enable" => SetSelectedDesktopPluginsEnabled(true),
            "disable" => SetSelectedDesktopPluginsEnabled(false),
            "export" => ExportSelectedDesktopPlugins(),
            "remove" => RemoveDesktopPlugin(),
            "clear" => ClearDesktopPluginSelection(),
            "refresh" => RefreshDesktopPluginList(),
            "cleanup" => CleanupRemovedDesktopPlugins(),
            _ => throw new InvalidOperationException("The selected plugin action is unavailable.")
        };
    }

    private Task ClearDesktopPluginSelection()
    {
        desktopPluginList.UnselectAll();
        return Task.CompletedTask;
    }

    private Task RefreshDesktopPluginList()
    {
        RefreshDesktopPluginCatalog();
        return Task.CompletedTask;
    }

    private void RefreshDesktopPluginCatalog()
    {
        IReadOnlyList<DesktopPluginInfo> plugins;
        try { if (!desktopCatalog.TryList(out plugins)) return; }
        catch (Exception error) { DesktopPluginCatalogUnavailable(error); return; }
        RefreshDesktopPluginCatalog(() => plugins);
    }

    private void RefreshDesktopPluginCatalog(Func<IReadOnlyList<DesktopPluginInfo>> readCatalog)
    {
        try
        {
            desktopPluginItems = readCatalog();
            ReconcileDesktopPluginConfiguration();
            ApplyDesktopPluginFilter();
            RefreshAppearanceThemeAction();
        }
        catch (Exception error) { DesktopPluginCatalogUnavailable(error); }
    }

    private void DesktopPluginCatalogUnavailable(Exception error)
    {
        desktopPluginItems = [];
        desktopPluginList.ItemsSource = Array.Empty<DesktopPluginInfo>();
        CloseDesktopPluginConfiguration();
        RefreshAppearanceThemeAction();
        SetStatus("Desktop plugin storage is unavailable: " + error.Message, true);
    }

    private void RefreshAppearanceThemeAction()
    {
        if (appearanceThemeSettings is null) return;
        var plugin = desktopPluginItems.SingleOrDefault(item => item.Id == "pegasus.appearance");
        var status = configurationPlugins?.Status.SingleOrDefault(item => item.Id == "pegasus.appearance");
        var loaded = configurationPlugins?.GetLoadedPackage("pegasus.appearance");
        var ready = AppearanceThemeSettingsAvailable(plugin, status, loaded,
            configurationPlugins?.Panels.Any(panel => panel.PluginId == "pegasus.appearance") == true);
        appearanceThemeSettings.IsEnabled = ready;
        appearanceThemeSettingsHelp.Text = plugin switch
        {
            null => "Install the Appearance plugin from Desktop plugins to edit theme colors.",
            { Enabled: false } => "Enable the Appearance plugin in Desktop plugins to edit theme colors.",
            { Error: not null } => "Inspect the Appearance plugin in Desktop plugins before editing theme colors.",
            _ when status?.Error is not null => "Appearance plugin activation needs attention. Inspect Desktop plugins.",
            _ when loaded is { } current && (!current.Directory.Equals(plugin.Directory, StringComparison.OrdinalIgnoreCase) || current.Version != plugin.Version) =>
                "The selected Appearance plugin version is not active. Refresh or inspect Desktop plugins.",
            _ when !ready => "The Appearance settings panel is unavailable. Inspect Desktop plugins.",
            _ => "Open the enabled Appearance plugin's color settings."
        };
        appearanceThemeSettings.ToolTip = appearanceThemeSettingsHelp.Text;
    }

    private static bool AppearanceThemeSettingsAvailable(DesktopPluginInfo? plugin, DesktopPluginLoadStatus? status,
        (string Directory, string Version)? loaded, bool panelPresent) =>
        plugin is { Enabled: true, Error: null } && status is { Loaded: true, Error: null } &&
        loaded is { } current && current.Directory.Equals(plugin.Directory, StringComparison.OrdinalIgnoreCase) &&
        current.Version == plugin.Version && panelPresent;

    internal static IReadOnlyList<DesktopPluginInfo> CreateDesktopPluginDisplayRows(
        IReadOnlyList<DesktopPluginInfo> catalogItems, IReadOnlyList<DesktopPluginLoadStatus> runtimeStatus)
    {
        var runtimeById = runtimeStatus.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return catalogItems.Select(item =>
        {
            if (item.Error is not null || !runtimeById.TryGetValue(item.Id, out var status)) return item;
            return status.Loaded || status.Error is null ? item : item with { Error = status.Error };
        }).ToArray();
    }
    private void ApplyDesktopPluginFilter()
    {
        var selectedIds = desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var search = desktopPluginSearch.Text.Trim();
        var filter = desktopPluginFilter.SelectedItem as string ?? "All plugins";
        bool Configurable(DesktopPluginInfo item) => configurationPlugins?.Panels.Any(panel => panel.PluginId == item.Id) == true;
        var displayItems = CreateDesktopPluginDisplayRows(desktopPluginItems,
            configurationPlugins?.Status ?? Array.Empty<DesktopPluginLoadStatus>());
        var filtered = displayItems.Where(item =>
            (search.Length == 0 || new[] { item.Name, item.Id, item.Version, item.Description }.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase))) &&
            filter switch
            {
                "Enabled" => item.Enabled,
                "Disabled" => !item.Enabled,
                "Configurable" => item.Enabled && Configurable(item),
                "Needs attention" => item.Error is not null,
                _ => true
            }).ToArray();
        desktopPluginList.ItemsSource = filtered;
        foreach (var item in filtered.Where(item => selectedIds.Contains(item.Id))) desktopPluginList.SelectedItems.Add(item);
        UpdateDesktopPluginSelectionActions();
        UpdateDesktopPluginManageActions();
    }

    private Task ConfigureDesktopPlugin() => ConfigureDesktopPlugin(SingleSelectedDesktopPlugin());

    private Task ConfigureDesktopPluginById(string pluginId) => ConfigureDesktopPluginById(pluginId, null);

    private Task ConfigureDesktopPluginById(string pluginId, string? panelId) =>
        ConfigureDesktopPluginById(pluginId, panelId, null, null);

    private Task ConfigureDesktopPluginById(string pluginId, string? panelId, string? expectedDirectory, string? expectedVersion)
    {
        var plugin = desktopCatalog.List().SingleOrDefault(item => item.Id == pluginId)
            ?? throw new InvalidOperationException("This configuration plugin is not installed. Install it from Desktop plugins.");
        return ConfigureDesktopPlugin(plugin, panelId, expectedDirectory, expectedVersion);
    }

    private Task ConfigureDesktopPlugin(DesktopPluginInfo plugin) => ConfigureDesktopPlugin(plugin, null);

    private Task ConfigureDesktopPlugin(DesktopPluginInfo plugin, string? panelId) =>
        ConfigureDesktopPlugin(plugin, panelId, null, null);

    private Task ConfigureDesktopPlugin(DesktopPluginInfo plugin, string? panelId, string? expectedDirectory, string? expectedVersion)
    {
        var exactGeneration = expectedDirectory is not null || expectedVersion is not null;
        if (exactGeneration)
        {
            // The catalog can advance before this window refreshes its cached admission.
            // Check its fresh selection before cached reads or any panel preparation.
            RequireFreshDesktopPluginConfigurationGeneration(plugin, expectedDirectory, expectedVersion);
            var before = ReadDesktopPluginConfigurationState(plugin.Id);
            RequireDesktopPluginConfigurationGeneration(before?.Directory, before?.Version, expectedDirectory, expectedVersion);
        }
        else if (!plugin.Enabled) throw new InvalidOperationException("Enable this plugin before opening its configuration.");
        _ = ConfigurationPluginOwnerScope.PreparePanels(plugin.Id, configurationPluginOwner,
            configurationPlugins,
            // Targeted navigation only uses the runtime already inspected by its caller.
            // Reload can close or replace existing UI, so refuse it before any such effect.
            () => exactGeneration ? throw new ConfigurationPageControlRefusal("plugin_generation_changed")
                : ReloadConfigurationDesktopPlugins(), () => configurationPlugins);
        var prepared = ReadDesktopPluginConfigurationState(plugin.Id);
        // PreparePanels may reload the runtime. Fence that transition before touching
        // the existing window or mounting any view from the prepared generation.
        if (exactGeneration)
            RequireDesktopPluginConfigurationGeneration(prepared?.Directory, prepared?.Version, expectedDirectory, expectedVersion);
        var captured = prepared
            ?? throw new InvalidOperationException("The selected plugin or Configuration owner changed. Reopen its settings.");
        if (panelId is not null) _ = RequireDeclaredDesktopPluginConfigurationPanel(plugin.Id, panelId, captured.Panels);
        if (desktopPluginConfigurationWindow is { IsVisible: true } open && configuredDesktopPluginId == plugin.Id &&
            desktopPluginConfigurationScope?.MatchesCurrent() == true)
        {
            if (panelId is not null)
                SelectDesktopPluginConfigurationPanel(desktopPluginConfigurationTabs
                    ?? throw new InvalidOperationException("The admitted plugin panel tabs are unavailable."),
                    plugin.Id, panelId, captured.Panels);
            if (open.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(open);
            open.Activate();
            ReceiverPageChanged();
            return Task.CompletedTask;
        }
        CloseDesktopPluginConfiguration();
        var tabs = CreateDesktopPluginConfigurationTabs(plugin.Id, captured.Panels);
        desktopPluginConfigurationTabs = tabs;
        if (panelId is not null) SelectDesktopPluginConfigurationPanel(tabs, plugin.Id, panelId, captured.Panels);
        tabs.SelectionChanged += (sender, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, sender)) ReceiverPageChanged();
        };
        var window = new Window
        {
            Owner = this,
            Icon = Icon,
            Title = plugin.Name + " · Plugin configuration",
            Width = 980,
            Height = 720,
            MinWidth = 720,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var close = Ui.Button("Close", window.Close, true);
        window.Content = Ui.Page(plugin.Name, "These settings belong to the plugin. Changes use the plugin's own save behavior and remain separate from Cerebral's core settings.", tabs, close);
        window.Closed += (_, _) =>
        {
            foreach (var tab in tabs.Items.OfType<TabItem>()) tab.Content = null;
            if (ReferenceEquals(desktopPluginConfigurationWindow, window))
            {
                desktopPluginConfigurationWindow = null;
                desktopPluginConfigurationTabs = null;
                configuredDesktopPluginId = null;
                desktopPluginConfigurationScope = null;
                ServiceStatusChanged -= OnDesktopPluginConfigurationStatusChanged;
                ReceiverPageChanged();
            }
        };
        WindowSizing.Attach(window);
        TrackDesktopPluginConfiguration(window, plugin.Id,
            new(captured, () => ReadDesktopPluginConfigurationState(plugin.Id)));
        window.Show();
        ReceiverPageChanged();
        return Task.CompletedTask;
    }

    internal static TabControl CreateDesktopPluginConfigurationTabs(string pluginId, IReadOnlyList<DesktopHostedPanel> panels)
    {
        var tabs = new TabControl { Background = Ui.Surface, BorderBrush = Ui.Line, BorderThickness = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(tabs, "DesktopPluginConfigurationPanels");
        foreach (var panel in panels)
        {
            if (panel.PluginId != pluginId) throw new InvalidOperationException("A plugin configuration window cannot mount another plugin's panel.");
            if (panel.View.Parent is ContentControl old) old.Content = null;
            else if (panel.View.Parent is not null) throw new InvalidOperationException("The plugin configuration panel is already mounted in an unsupported host.");
            var tab = Ui.TabPage(panel.Title, panel.View, new Thickness(14, 9, 14, 9));
            tab.Tag = panel.Id;
            System.Windows.Automation.AutomationProperties.SetAutomationId(tab, $"ConfigurationPluginPanel.{panel.PluginId}.{panel.Id}");
            tabs.Items.Add(tab);
        }
        return tabs;
    }

    internal static void RequireFreshDesktopPluginConfigurationGeneration(DesktopPluginInfo? plugin,
        string? expectedDirectory, string? expectedVersion)
    {
        if (plugin is not { Enabled: true, Error: null })
            throw new ConfigurationPageControlRefusal("plugin_generation_changed");
        RequireDesktopPluginConfigurationGeneration(plugin.Directory, plugin.Version, expectedDirectory, expectedVersion);
    }

    internal static void RequireDesktopPluginConfigurationGeneration(string? directory, string? version,
        string? expectedDirectory, string? expectedVersion)
    {
        if (!ConfigurationPageControl.SamePluginGeneration(directory, version, expectedDirectory, expectedVersion))
            throw new ConfigurationPageControlRefusal("plugin_generation_changed");
    }

    internal static DesktopHostedPanel RequireDeclaredDesktopPluginConfigurationPanel(string pluginId, string panelId,
        IReadOnlyList<DesktopHostedPanel> panels)
    {
        if (!ConfigurationReadbackProtocol.Identifier(panelId, 128))
            throw new ConfigurationPageControlRefusal("plugin_panel_target_invalid");
        if (panels.Any(panel => panel.PluginId != pluginId))
            throw new ConfigurationPageControlRefusal("plugin_panel_owner_mismatch");
        var declared = panels.Where(panel => panel.PluginId == pluginId && panel.Id == panelId).ToArray();
        if (declared.Length != 1)
            throw new ConfigurationPageControlRefusal(declared.Length == 0
                ? "plugin_panel_not_declared" : "plugin_panel_identity_ambiguous");
        return declared[0];
    }

    internal static void SelectDesktopPluginConfigurationPanel(TabControl tabs, string pluginId, string panelId,
        IReadOnlyList<DesktopHostedPanel> panels)
    {
        var declared = RequireDeclaredDesktopPluginConfigurationPanel(pluginId, panelId, panels);
        var candidates = tabs.Items.OfType<TabItem>().Where(tab => tab.Tag as string == panelId &&
            System.Windows.Automation.AutomationProperties.GetAutomationId(tab) ==
                $"ConfigurationPluginPanel.{pluginId}.{panelId}" && ReferenceEquals(tab.Content, declared.View)).ToArray();
        if (candidates.Length != 1)
            throw new ConfigurationPageControlRefusal("plugin_panel_view_unavailable");
        tabs.SelectedItem = candidates[0];
    }

    private void TrackDesktopPluginConfiguration(Window window, string pluginId, DesktopPluginConfigurationScope scope)
    {
        desktopPluginConfigurationWindow = window;
        configuredDesktopPluginId = pluginId;
        desktopPluginConfigurationScope = scope;
        ServiceStatusChanged -= OnDesktopPluginConfigurationStatusChanged;
        ServiceStatusChanged += OnDesktopPluginConfigurationStatusChanged;
    }

    private void OnDesktopPluginConfigurationStatusChanged(object? sender, EventArgs args) =>
        ReconcileDesktopPluginConfiguration();

    private void ReconcileDesktopPluginConfiguration()
    {
        // Presentation/status refresh can occur before a save callback displays its acknowledgement.
        // Keep the same editor only while its admitted owner, package and panel instances are current.
        if (desktopPluginConfigurationWindow is not null &&
            desktopPluginConfigurationScope?.MatchesCurrent() != true)
            CloseDesktopPluginConfiguration();
    }

    private DesktopPluginConfigurationState? ReadDesktopPluginConfigurationState(string pluginId)
    {
        var runtime = configurationPlugins;
        var owner = configurationPluginOwner;
        if (runtime is null || owner?.MatchesOwner() != true) return null;
        var selected = desktopPluginItems.Where(item => item.Id == pluginId).ToArray();
        return DesktopPluginConfigurationState.Capture(runtime, owner, true,
            selected.Length == 1 ? selected[0] : null, runtime.GetLoadedPackage(pluginId),
            runtime.Panels.Where(panel => panel.PluginId == pluginId).ToArray());
    }

    // These are identities already admitted by the runtime, not a second package loader.
    private sealed record DesktopPluginConfigurationState(object Runtime, object Owner, string Id,
        string Version, string Directory, string ManifestPath, IReadOnlyList<DesktopHostedPanel> Panels)
    {
        internal static DesktopPluginConfigurationState? Capture(object? runtime, object? owner, bool ownerCurrent,
            DesktopPluginInfo? selected, (string Directory, string Version)? loaded,
            IReadOnlyList<DesktopHostedPanel> panels)
        {
            if (runtime is null || owner is null || !ownerCurrent || selected is not { Enabled: true, Error: null } ||
                loaded is not { } package || selected.Version != package.Version ||
                !string.Equals(selected.Directory, package.Directory, StringComparison.OrdinalIgnoreCase) ||
                panels.Count == 0 || panels.Any(panel => panel.PluginId != selected.Id))
                return null;
            return new(runtime, owner, selected.Id, selected.Version, selected.Directory, selected.ManifestPath, panels.ToArray());
        }

        internal bool SameAdmission(DesktopPluginConfigurationState current) =>
            ReferenceEquals(Runtime, current.Runtime) && ReferenceEquals(Owner, current.Owner) &&
            Id == current.Id && Version == current.Version &&
            string.Equals(Directory, current.Directory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ManifestPath, current.ManifestPath, StringComparison.OrdinalIgnoreCase) &&
            Panels.Count == current.Panels.Count && Panels.Zip(current.Panels).All(pair =>
                pair.First.Id == pair.Second.Id && ReferenceEquals(pair.First.View, pair.Second.View));
    }

    private sealed class DesktopPluginConfigurationScope(DesktopPluginConfigurationState captured,
        Func<DesktopPluginConfigurationState?> current)
    {
        internal bool MatchesCurrent() => current() is { } now && captured.SameAdmission(now);
    }

    private void CloseDesktopPluginConfiguration()
    {
        var window = desktopPluginConfigurationWindow;
        desktopPluginConfigurationWindow = null;
        desktopPluginConfigurationTabs = null;
        configuredDesktopPluginId = null;
        desktopPluginConfigurationScope = null;
        ServiceStatusChanged -= OnDesktopPluginConfigurationStatusChanged;
        window?.Close();
        if (window is not null) ReceiverPageChanged();
    }
    private async Task InstallDesktopPlugin()
    {
        var picker = new OpenFileDialog { Title = "Select a Cerebral plugin package", Filter = "Cerebral plugin package|*.CerebralPlugin|Unpacked desktop plugin manifest|desktop-plugin.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (picker.FileName.EndsWith(CerebralPluginPackageContract.Extension, StringComparison.OrdinalIgnoreCase))
        {
            await ImportCerebralPluginPackage(picker.FileName);
            return;
        }
        var manifest = desktopCatalog.Inspect(picker.FileName);
        if (!Confirm("Install " + manifest.Name + " " + manifest.Version + " disabled? Its package will be copied to your user plugin folder. Enabling is a separate choice.")) return;
        desktopCatalog.Install(picker.FileName); RefreshDesktopPluginCatalog();
        if (ReloadConfigurationDesktopPlugins()) SetStatus("Native plugin installed disabled.");
    }
    private async Task UpdateDesktopPlugin()
    {
        var selected = SelectedDesktopPlugins();
        var picker = new OpenFileDialog
        {
            Title = selected.Length == 1 ? "Select a newer package for " + selected[0].Name : "Select newer packages for the selected plugins",
            Filter = "Cerebral plugin package|*.CerebralPlugin|Unpacked desktop plugin manifest|desktop-plugin.json",
            CheckFileExists = true,
            Multiselect = true
        };
        if (picker.ShowDialog(this) != true) return;
        // Validate the complete ID mapping before reviewing any update. Each accepted package
        // still goes through its existing reviewed transaction; this is not a batch transaction.
        var selectedIds = selected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(string Path, string Id)>();
        foreach (var path in picker.FileNames)
        {
            string id;
            if (path.EndsWith(CerebralPluginPackageContract.Extension, StringComparison.OrdinalIgnoreCase))
            {
                using var prepared = CerebralPluginPackage.Prepare(path);
                id = prepared.Inspection.Package.Id;
            }
            else id = desktopCatalog.Inspect(path).Id;
            if (!selectedIds.Contains(id))
                throw new InvalidDataException($"The selected package is {id}; choose updates only for the selected plugins.");
            if (candidates.Any(candidate => candidate.Id == id))
                throw new InvalidDataException("Choose only one update package for " + id + ".");
            candidates.Add((path, id));
        }
        var outcomes = new List<DesktopPluginBatchOutcome>();
        foreach (var candidate in candidates)
        {
            Lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                var applied = await ApplyDesktopPluginUpdate(candidate.Path, candidate.Id);
                outcomes.Add(new(candidate.Id, applied ? "Updated" : "Skipped",
                    applied ? DesktopPluginBatchStatus.Completed : DesktopPluginBatchStatus.Skipped));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                outcomes.Add(new(candidate.Id, "Failed: " + error.Message, DesktopPluginBatchStatus.Failed));
            }
        }
        ReportDesktopPluginBatch("Package updates", outcomes);
    }

    private async Task<bool> ApplyDesktopPluginUpdate(string path, string expectedId)
    {
        if (path.EndsWith(CerebralPluginPackageContract.Extension, StringComparison.OrdinalIgnoreCase))
            return await ImportCerebralPluginPackageCore(path, expectedId);
        var plugin = desktopCatalog.List().SingleOrDefault(item => item.Id == expectedId)
            ?? throw new InvalidOperationException("The selected plugin is no longer installed. Refresh the list.");
        var candidate = desktopCatalog.Inspect(path);
        if (!candidate.Id.Equals(plugin.Id, StringComparison.Ordinal))
            throw new InvalidDataException($"The selected package is {candidate.Id}; choose an update for {plugin.Id}.");
        if (!Confirm($"Update {plugin.Name} from {plugin.Version} to {candidate.Version}?\n\nCerebral stages an immutable generation, switches it atomically, cancels the old contribution, and loads the update in running windows. The current enable setting is preserved.")) return false;
        EnsureConfigurationDesktopPluginOwner();
        var runtime = configurationPlugins ?? throw new InvalidOperationException("The configuration plugin runtime is unavailable.");
        var owner = configurationPluginOwner;
        await runtime.UpdateReviewedAsync(path, null, enableCandidate: false,
            () => ReferenceEquals(configurationPlugins, runtime) && ReferenceEquals(configurationPluginOwner, owner) &&
                owner?.MatchesOwner() == true,
            MountConfigurationDesktopPlugins, Lifetime.Token);
        RefreshDesktopPluginCatalog();
        return true;
    }

    internal Task ImportCerebralPluginPackage(string path, string? expectedId = null) =>
        ImportCerebralPluginPackageCore(path, expectedId);

    private async Task<bool> ImportCerebralPluginPackageCore(string path, string? expectedId)
    {
        using var prepared = CerebralPluginPackage.Prepare(path);
        var package = prepared.Inspection.Package;
        if (expectedId is not null && !package.Id.Equals(expectedId, StringComparison.Ordinal))
            throw new InvalidDataException($"The selected package is {package.Id}; choose an update for {expectedId}.");
        var current = desktopCatalog.List().SingleOrDefault(x => x.Id == package.Id);
        if (current is not null)
        {
            if (current.Error is not null) throw new InvalidDataException("The installed package must be repaired before updating it: " + current.Error);
            if (!Version.TryParse(current.Version, out var installed) || !Version.TryParse(package.Version, out var candidate) || candidate <= installed)
                throw new InvalidOperationException($"The package version must be newer than installed version {current.Version}; candidate {package.Version} was not applied.");
        }
        var settings = package.SettingsMode switch
        {
            "defaults" => "publisher defaults",
            "current" => "the exporter's portable current preset",
            _ => "no settings preset"
        };
        var productPaths = ConfigurationProductPaths();
        var reviewOwner = ConfigurationPluginOwner();
        var destinations = prepared.ResourceDestinations(productPaths);
        var fingerprint = NativeDesktopPluginPackage.ComputeFingerprint(prepared.DesktopManifestPath);
        var action = current is null ? "Install disabled" : "Update";
        var version = current is null ? package.Version : $"{current.Version} → {package.Version}";
        var enablement = current is null ? "The new plugin will be disabled. You can enable it separately."
            : $"The plugin will remain {(current.Enabled ? "enabled" : "disabled")}.";
        if (!ReviewCerebralPluginPackage(action, $"{package.Name} · {version}",
            $"Package ID: {package.Id}\nSettings preset: {settings}\nFiles: {prepared.Inspection.FileCount:N0} ({prepared.Inspection.ExpandedBytes / 1048576d:N1} MiB expanded)",
            enablement, destinations))
            return false;
        Lifetime.Token.ThrowIfCancellationRequested();
        if (reviewOwner != ConfigurationPluginOwner() || productPaths != ConfigurationProductPaths())
            throw new InvalidOperationException("The Configuration connection or resource locations changed during review. Open the package again to review the current destination.");
        if (current is null)
        {
            prepared.InstallNewReviewed(desktopCatalog, productPaths, desktopPluginDataRoot, fingerprint);
            if (!ReloadConfigurationDesktopPlugins())
                throw new InvalidOperationException("The package is saved, but configuration activation needs attention. Inspect the plugin before taking another action.");
        }
        else
        {
            EnsureConfigurationDesktopPluginOwner();
            var runtime = configurationPlugins ?? throw new InvalidOperationException("The configuration plugin runtime is unavailable.");
            var owner = configurationPluginOwner;
            await prepared.UpdateReviewedAsync(desktopCatalog, runtime, productPaths, desktopPluginDataRoot,
                expectedId, fingerprint, () => ReferenceEquals(configurationPlugins, runtime) &&
                    ReferenceEquals(configurationPluginOwner, owner) && owner?.MatchesOwner() == true,
                MountConfigurationDesktopPlugins, Lifetime.Token, current);
        }
        RefreshDesktopPluginCatalog();
        SetStatus(current is null
            ? "Cerebral plugin installed disabled. Review it, then enable it when ready."
            : "Cerebral plugin selected and this Configurator refreshed. Its enable setting and unrelated plugin data were preserved.");
        return true;
    }

    private bool ReviewCerebralPluginPackage(string action, string package, string details, string enablement,
        IReadOnlyList<string> destinations)
    {
        var review = new Window
        {
            Owner = this, Icon = Icon, Title = "Review Cerebral plugin package", ShowInTaskbar = false,
            Width = 680, Height = 520, MinWidth = 420, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Paper, Foreground = Ui.Ink
        };
        WindowSizing.Attach(review);
        AutomationProperties.SetAutomationId(review, "ConfigurationPluginPackageReview");
        var root = new DockPanel { Margin = new Thickness(22) };
        var heading = Ui.Text(package, 22);
        heading.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var approve = Ui.Button(action, () => review.DialogResult = true, true);
        var cancel = Ui.Button("Cancel", () => review.DialogResult = false);
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        AutomationProperties.SetAutomationId(approve, "ConfigurationPluginPackageApprove");
        AutomationProperties.SetAutomationId(cancel, "ConfigurationPluginPackageCancel");
        var buttons = Ui.Bar(approve, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(Ui.Text(details + "\n\n" + enablement));
        body.Children.Add(Ui.Text(destinations.Count == 0 ? "\nResource directories: none" :
            "\nThis package replaces the following resource directories:\n" + string.Join("\n", destinations)));
        body.Children.Add(Ui.Text("\nPortable presets do not grant access.\n\n" + DesktopPluginCatalog.NativeTrustNotice, 13, Ui.Muted));
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        review.Content = root;
        review.Loaded += (_, _) => cancel.Focus();
        return review.ShowDialog() == true;
    }

    private Task ExportDesktopPlugin()
    {
        var plugin = Selected<DesktopPluginInfo>(desktopPluginList);
        if (plugin.Error is not null) throw new InvalidDataException("Repair this plugin before exporting it: " + plugin.Error);
        var safeName = new string(plugin.Name.Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x).ToArray());
        var defaultName = safeName + CerebralPluginPackageContract.Extension;
        var form = new FieldsDialog(this, "Export Cerebral plugin",
            "Create one installable package. Native files are compressed; declared model, tool and data resources are stored without recompression. Portable presets contain only the plugin-owned bounded settings document; machine paths, credentials, enablement, grants and runtime state are excluded.", "Export")
            .Choice("settings", "Settings preset", ["Package only", "Publisher defaults", "Current portable settings"], "Package only")
            .Add("path", "Destination", Path.Combine(settings.SharedDirectory, defaultName))
            .ActionButton("Choose destination…", dialog =>
            {
                var picker = new SaveFileDialog { Filter = "Cerebral plugin package|*.CerebralPlugin", FileName = defaultName, OverwritePrompt = true };
                if (picker.ShowDialog(dialog) == true) dialog.SetValue("path", picker.FileName);
            });
        if (form.ShowDialog() != true) return Task.CompletedTask;
        var mode = form.Selection("settings") switch { "Publisher defaults" => "defaults", "Current portable settings" => "current", _ => "none" };
        CerebralPluginPackage.Export(plugin, form.Value("path"), mode, ConfigurationProductPaths(), desktopPluginDataRoot);
        SetStatus("Cerebral plugin package exported to " + form.Value("path"));
        return Task.CompletedTask;
    }
    private Task InspectDesktopPlugin()
    {
        var plugin = Selected<DesktopPluginInfo>(desktopPluginList);
        var bytes = Directory.Exists(plugin.Directory)
            ? Directory.EnumerateFiles(plugin.Directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;
        var panels = configurationPlugins?.Panels.Where(panel => panel.PluginId == plugin.Id).Select(panel => new { panel.Id, panel.Title }).ToArray() ?? [];
        Ui.ShowText(this, plugin.Name, Ui.Serialize(new { package = desktopCatalog.Inspect(plugin.ManifestPath), installedBytes = bytes, configurationPanels = panels }) + "\n\n" + DesktopPluginCatalog.NativeTrustNotice);
        return Task.CompletedTask;
    }
    private Task CleanupRemovedDesktopPlugins()
    {
        var batch = desktopCatalog.CleanupRemovedPackages(32, desktopPluginCleanupCursor);
        desktopPluginCleanupCursor = batch.NextAfterId;
        RefreshDesktopPluginCatalog();
        var pending = batch.Results.Where(item => item.CleanupPending).ToArray();
        var message = batch.Results.Count == 0
            ? "No removed plugin packages need cleanup."
            : $"Removed package cleanup: {batch.Results.Count - pending.Length} cleared; {pending.Length} pending. Saved plugin data was kept.";
        if (pending.Length != 0)
            message += " " + string.Join("; ", pending.Take(2).Select(item => item.Id + ": " + item.CleanupError));
        if (batch.NextAfterId is not null) message += " More remain; choose Clean up removed again to continue.";
        SetStatus(message, pending.Length != 0);
        return Task.CompletedTask;
    }

    private Task RemoveDesktopPlugin() => RemoveSelectedDesktopPlugins();
}
