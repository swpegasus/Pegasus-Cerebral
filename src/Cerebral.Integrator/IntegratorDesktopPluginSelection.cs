using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private WrapPanel? desktopPluginSelectionBar;
    private TextBlock? desktopPluginSelectionCount;
    private Border? desktopPluginSelectionDetails;
    private TextBlock? desktopPluginSelectionDescriptionText;
    private Button? desktopPluginEnableSelection;
    private Button? desktopPluginDisableSelection;
    private Button? desktopPluginRemoveSelection;
    private readonly ContextMenu desktopPluginMenu = new();
    private bool desktopPluginSelectionInitialized;
    private bool desktopPluginPointerContextHasRow;

    private void InitializeDesktopPluginSelection()
    {
        if (desktopPluginSelectionInitialized) return;
        desktopPluginSelectionInitialized = true;
        desktopPluginList.SelectionMode = DataGridSelectionMode.Extended;
        desktopPluginList.SelectionUnit = DataGridSelectionUnit.FullRow;
        desktopPluginList.ContextMenu = desktopPluginMenu;
        desktopPluginList.SelectionChanged += (_, _) => UpdateDesktopPluginSelectionActions();
        desktopPluginList.PreviewMouseRightButtonDown += (_, args) =>
        {
            var row = SettingsRowFromSource(args.OriginalSource as DependencyObject);
            desktopPluginPointerContextHasRow = row?.Item is DesktopPluginInfo;
            if (row?.Item is not DesktopPluginInfo plugin) return;
            // A click within the existing selection keeps the group. Clicking another row
            // deliberately targets only that item rather than a previously selected group.
            SelectDesktopPluginContextTarget(plugin);
            PopulateDesktopPluginContextMenu();
            desktopPluginMenu.PlacementTarget = desktopPluginList;
            desktopPluginMenu.Placement = PlacementMode.MousePoint;
            desktopPluginMenu.IsOpen = true;
            args.Handled = true;
        };
        desktopPluginList.ContextMenuOpening += (_, args) =>
        {
            if (!CanOpenSettingsItemContextMenu(args.CursorLeft < 0 && args.CursorTop < 0,
                desktopPluginPointerContextHasRow, desktopPluginList.SelectedItems.Count > 0))
            {
                args.Handled = true; return;
            }
            PopulateDesktopPluginContextMenu();
        };
        desktopPluginList.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && desktopPluginList.SelectedItems.Count > 0)
            {
                desktopPluginList.UnselectAll(); args.Handled = true;
            }
            else if (args.Key == Key.Enter && CanConfigureSelectedDesktopPlugin())
            {
                Run(ConfigureDesktopPlugin); args.Handled = true;
            }
        };
        desktopPluginList.MouseDoubleClick += (_, args) =>
        {
            if (SettingsRowFromSource(args.OriginalSource as DependencyObject) is not null &&
                CanConfigureSelectedDesktopPlugin())
            {
                Run(ConfigureDesktopPlugin); args.Handled = true;
            }
        };
        desktopPluginSelectionCount = Ui.Text("", 13, Ui.Muted);
        desktopPluginSelectionCount.Margin = new Thickness(0, 8, 12, 0);
        desktopPluginEnableSelection = DesktopPluginActionButton("Enable", "DesktopPluginEnableSelected", () => SetSelectedDesktopPluginsEnabled(true));
        desktopPluginDisableSelection = DesktopPluginActionButton("Disable", "DesktopPluginDisableSelected", () => SetSelectedDesktopPluginsEnabled(false));
        desktopPluginRemoveSelection = DesktopPluginActionButton("Remove", "DesktopPluginRemoveSelected", RemoveDesktopPlugin);
        desktopPluginRemoveSelection.ToolTip = "Remove disabled packages while keeping their saved plugin data. Disable enabled plugins first.";
        var export = DesktopPluginActionButton("Export packages…", "DesktopPluginExportSelected", ExportSelectedDesktopPlugins);
        export.ToolTip = "Export installable packages with optional portable settings. This does not back up arbitrary plugin data, credentials, or permissions.";
        desktopPluginSelectionBar = Ui.Bar(desktopPluginSelectionCount,
            DesktopPluginActionButton("Update packages…", "DesktopPluginUpdateSelected", UpdateDesktopPlugin),
            desktopPluginEnableSelection, desktopPluginDisableSelection, export, desktopPluginRemoveSelection,
            Ui.Button("Clear selection", desktopPluginList.UnselectAll));
        desktopPluginSelectionBar.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetAutomationId(desktopPluginSelectionBar, "DesktopPluginSelectionActions");
        desktopPluginSelectionDescriptionText = Ui.Text("", 13, Ui.Muted);
        desktopPluginSelectionDescriptionText.Margin = new Thickness(0);
        desktopPluginSelectionDetails = new Border
        {
            BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer { MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = desktopPluginSelectionDescriptionText }
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(desktopPluginSelectionDetails, "DesktopPluginSelectionDetails");
        desktopPluginMenu.Background = Ui.Surface; desktopPluginMenu.Foreground = Ui.Ink;
        desktopPluginMenu.BorderBrush = Ui.Line;
    }

    private Button DesktopPluginActionButton(string text, string id, Func<Task> action)
    {
        var button = Ui.Button(text, () => Run(action));
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    private Button CreateDesktopPluginMaintenanceButton()
    {
        var button = Ui.Button("More ▾", () => { });
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, "DesktopPluginMore");
        var menu = new ContextMenu { Background = Ui.Surface, Foreground = Ui.Ink, BorderBrush = Ui.Line };
        menu.Items.Add(DesktopPluginMenuItem("Refresh list", "DesktopPluginRefresh", () => { RefreshDesktopPluginCatalog(); return Task.CompletedTask; }));
        menu.Items.Add(DesktopPluginMenuItem("Clean up removed packages", "DesktopPluginCleanupRemoved", CleanupRemovedDesktopPlugins));
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        };
        button.ContextMenu = menu;
        return button;
    }

    private void SelectDesktopPluginContextTarget(DesktopPluginInfo plugin)
    {
        if (!desktopPluginList.SelectedItems.Contains(plugin))
        {
            desktopPluginList.SelectedItems.Clear();
            desktopPluginList.SelectedItems.Add(plugin);
        }
        desktopPluginList.CurrentItem = plugin;
    }

    private DesktopPluginInfo[] SelectedDesktopPlugins()
    {
        var selected = desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select one or more desktop plugins first.");
        return selected;
    }

    private DesktopPluginInfo SingleSelectedDesktopPlugin()
    {
        var selected = SelectedDesktopPlugins();
        if (selected.Length != 1) throw new InvalidOperationException("Select one plugin for this action.");
        return selected[0];
    }

    private bool CanConfigureSelectedDesktopPlugin() => DesktopPluginConfigureUnavailableReason() is null;

    private string? DesktopPluginConfigureUnavailableReason()
    {
        var selected = desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().ToArray();
        return DesktopPluginConfigureUnavailableReason(selected,
            configurationPlugins is not null && configurationPluginOwner?.MatchesOwner() == true,
            selected is [var plugin] && configurationPlugins?.Panels.Any(panel => panel.PluginId == plugin.Id) == true);
    }

    // Presentation shares one reason; the actual open path retains its owner, generation and panel checks.
    internal static string? DesktopPluginConfigureUnavailableReason(IReadOnlyList<DesktopPluginInfo> selected,
        bool ownerAvailable, bool panelAvailable)
    {
        if (selected.Count == 0) return "Select one plugin to open its Configuration panels.";
        if (selected.Count != 1) return "Select only one plugin to open its Configuration panels.";
        if (!selected[0].Enabled) return "Enable this plugin before opening its Configuration panels.";
        if (selected[0].Error is not null) return "This plugin needs attention. Inspect its error before opening Configuration.";
        if (!ownerAvailable) return "The current Configuration plugin owner is unavailable. Refresh the connection and plugin list.";
        if (!panelAvailable) return "This plugin has no Configuration panel in the current runtime. Refresh the plugin list or inspect the package.";
        return null;
    }

    private static void SetDesktopPluginConfigureAvailability(FrameworkElement control, string? reason)
    {
        control.IsEnabled = reason is null;
        var help = reason ?? "Open the selected plugin's Configuration panels.";
        control.ToolTip = help;
        ToolTipService.SetShowOnDisabled(control, true);
        System.Windows.Automation.AutomationProperties.SetHelpText(control, help);
    }

    private void UpdateDesktopPluginSelectionActions()
    {
        if (desktopPluginSelectionBar is null) return;
        var selected = desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().ToArray();
        desktopPluginSelectionBar.Visibility = selected.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        desktopPluginSelectionCount!.Text = selected.Length == 1 ? "1 selected" : $"{selected.Length:N0} selected";
        desktopPluginEnableSelection!.IsEnabled = selected.Any(item => !item.Enabled);
        desktopPluginDisableSelection!.IsEnabled = selected.Any(item => item.Enabled);
        desktopPluginRemoveSelection!.IsEnabled = selected.Length > 0 && selected.All(item => !item.Enabled);
        desktopPluginSelectionDetails!.Visibility = selected.Length == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (selected is [var plugin])
            desktopPluginSelectionDescriptionText!.Text = $"{plugin.Name} · {plugin.Version} · {(plugin.Enabled ? "Enabled" : "Disabled")}\n" +
                plugin.Id + "\n" + plugin.Description +
                (plugin.Error is null ? "" : "\nNeeds attention: " + plugin.Error);
    }

    private void PopulateDesktopPluginContextMenu()
    {
        desktopPluginMenu.Items.Clear();
        var selected = desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().ToArray();
        if (selected.Length == 0) return;
        if (selected.Length == 1)
        {
            desktopPluginMenu.Items.Add(DesktopPluginMenuItem("Configure…", "DesktopPluginConfigure", ConfigureDesktopPlugin,
                unavailableReason: DesktopPluginConfigureUnavailableReason));
            desktopPluginMenu.Items.Add(DesktopPluginMenuItem("Inspect", "DesktopPluginInspect", InspectDesktopPlugin));
            desktopPluginMenu.Items.Add(new Separator());
        }
        desktopPluginMenu.Items.Add(DesktopPluginMenuItem("Update packages…", "DesktopPluginUpdateMenu", UpdateDesktopPlugin));
        desktopPluginMenu.Items.Add(DesktopPluginMenuItem("Enable", "DesktopPluginEnableMenu",
            () => SetSelectedDesktopPluginsEnabled(true), selected.Any(item => !item.Enabled)));
        desktopPluginMenu.Items.Add(DesktopPluginMenuItem("Disable", "DesktopPluginDisableMenu",
            () => SetSelectedDesktopPluginsEnabled(false), selected.Any(item => item.Enabled)));
        desktopPluginMenu.Items.Add(DesktopPluginMenuItem(selected.Length == 1 ? "Export package…" : "Export packages…",
            "DesktopPluginExportMenu", ExportSelectedDesktopPlugins));
        desktopPluginMenu.Items.Add(new Separator());
        desktopPluginMenu.Items.Add(DesktopPluginMenuItem("Remove", "DesktopPluginRemoveMenu", RemoveDesktopPlugin,
            selected.All(item => !item.Enabled)));
    }

    private MenuItem DesktopPluginMenuItem(string label, string id, Func<Task> action, bool enabled = true,
        Func<string?>? unavailableReason = null)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled };
        System.Windows.Automation.AutomationProperties.SetAutomationId(item, id);
        bool RefreshAvailability()
        {
            if (unavailableReason is not null)
                SetDesktopPluginConfigureAvailability(item, unavailableReason());
            return item.IsEnabled;
        }
        if (unavailableReason is not null) RefreshAvailability();
        item.Click += (_, _) =>
        {
            // An already-open menu can outlive its captured selection or plugin owner.
            if (unavailableReason is null || RefreshAvailability()) Run(action);
        };
        return item;
    }

    private static DataGridRow? SettingsRowFromSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is DataGridRow row) return row;
            source = source switch
            {
                FrameworkContentElement content => content.Parent,
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source),
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
        return null;
    }

    private Task SetSelectedDesktopPluginsEnabled(bool enabled)
    {
        var selection = SelectedDesktopPlugins();
        var selected = selection.Where(item => item.Enabled != enabled).ToArray();
        if (selected.Length == 0) return Task.CompletedTask;
        var manifests = (enabled ? selection : selected).ToDictionary(item => item.Id,
            item => ReadDesktopPluginBatchManifest(item, enabled), StringComparer.Ordinal);
        if (enabled) ValidateDesktopPluginEnableSelection(selection, manifests);
        var action = enabled ? "Enable" : "Disable";
        if (!Confirm($"{action} {DesktopPluginSelectionDescription(selected)}?" +
            (enabled ? "\n\n" + DesktopPluginCatalog.NativeTrustNotice : ""))) return Task.CompletedTask;
        var ordered = OrderDesktopPluginBatch(selected, manifests, dependenciesFirst: enabled);
        var outcomes = new List<DesktopPluginBatchOutcome>();
        foreach (var item in ordered)
        {
            Lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                var current = desktopCatalog.List().SingleOrDefault(plugin => plugin.Id == item.Id)
                    ?? throw new InvalidOperationException("This plugin is no longer installed.");
                if (current.Enabled == enabled) { outcomes.Add(new(item.Id, "Already " + (enabled ? "enabled" : "disabled"), DesktopPluginBatchStatus.Completed)); continue; }
                using var admission = enabled ? configurationPlugins?.ReserveForPublication(current.ManifestPath, enabled: true) : null;
                desktopCatalog.SetEnabled(item.Id, enabled);
                if (!ReloadConfigurationDesktopPlugins(admission))
                    throw new InvalidOperationException("Enablement is saved, but configuration activation needs attention.");
                outcomes.Add(new(item.Id, enabled ? "Enabled" : "Disabled", DesktopPluginBatchStatus.Completed));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                outcomes.Add(new(item.Id, "Failed: " + error.Message, DesktopPluginBatchStatus.Failed));
            }
        }
        RefreshDesktopPluginCatalog();
        ReportDesktopPluginBatch(action, outcomes);
        return Task.CompletedTask;
    }

    internal static void ValidateDesktopPluginEnableSelection(IReadOnlyList<DesktopPluginInfo> selected,
        IReadOnlyDictionary<string, DesktopPluginManifest> manifests)
    {
        // Check the entire requested group, including providers that are already enabled.
        // Otherwise enabling its disabled alternative would silently disable a selected item.
        if (selected.Select(item => manifests[item.Id]).Where(item => !string.IsNullOrWhiteSpace(item.ExclusiveGroup))
            .GroupBy(item => item.ExclusiveGroup, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("The selection contains alternative providers in the same exclusive group. Enable one provider at a time.");
    }

    private DesktopPluginManifest ReadDesktopPluginBatchManifest(DesktopPluginInfo item, bool enabling)
    {
        try { return desktopCatalog.Inspect(item.ManifestPath); }
        catch (Exception error) when (!enabling &&
            error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            // Disabling a broken package is still permitted by the catalog. Ordering may
            // omit its unreadable declarations; SetEnabled continues to enforce all actual
            // enabled dependents, so this fallback never bypasses the mutation gate.
            return new DesktopPluginManifest { Id = item.Id };
        }
    }

    internal static IReadOnlyList<DesktopPluginInfo> OrderDesktopPluginBatch(IReadOnlyList<DesktopPluginInfo> selected,
        IReadOnlyDictionary<string, DesktopPluginManifest> manifests, bool dependenciesFirst)
    {
        var byId = selected.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var ordered = new List<DesktopPluginInfo>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("The selected plugin dependencies contain a cycle.");
            foreach (var dependency in (manifests[id].Dependencies ?? []).Where(item => !item.Optional && byId.ContainsKey(item.Id))
                .OrderBy(item => item.Id, StringComparer.Ordinal)) Visit(dependency.Id);
            visiting.Remove(id); visited.Add(id); ordered.Add(byId[id]);
        }
        foreach (var item in selected.OrderBy(item => item.Id, StringComparer.Ordinal)) Visit(item.Id);
        if (!dependenciesFirst) ordered.Reverse();
        return ordered;
    }

    private Task RemoveSelectedDesktopPlugins()
    {
        var selected = SelectedDesktopPlugins();
        if (selected.Any(item => item.Enabled))
            throw new InvalidOperationException("Disable the selected plugins before removing their packages.");
        if (!Confirm("Remove " + DesktopPluginSelectionDescription(selected) +
            "?\n\nSaved plugin data will be kept. Package files still in use may need later cleanup.")) return Task.CompletedTask;
        var outcomes = new List<DesktopPluginBatchOutcome>();
        foreach (var item in selected)
        {
            Lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                configurationPlugins?.Disable(item.Id);
                var removal = desktopCatalog.RemoveWithResult(item.Id);
                outcomes.Add(removal.CleanupPending
                    ? new(item.Id, "Removed. Saved plugin data was kept. Some package files still need cleanup.\n\n" +
                        removal.CleanupError + "\n\nIf the files are still in use, exit both Cerebral and Configuration. Reopen Configuration and choose Desktop plugins → Manage → Clean up removed packages, then Go.",
                        DesktopPluginBatchStatus.CleanupPending)
                    : new(item.Id, "Removed. Saved plugin data was kept.", DesktopPluginBatchStatus.Completed));
            }
            catch (Exception error) when (error is not OperationCanceledException) { outcomes.Add(new(item.Id, "Failed: " + error.Message, DesktopPluginBatchStatus.Failed)); }
        }
        ReloadConfigurationDesktopPlugins(); RefreshDesktopPluginCatalog();
        ReportDesktopPluginBatch("Remove", outcomes);
        return Task.CompletedTask;
    }

    private Task ExportSelectedDesktopPlugins()
    {
        var selected = SelectedDesktopPlugins();
        if (selected.Length == 1) return ExportDesktopPlugin();
        var form = new FieldsDialog(this, "Export selected plugin packages",
            "Export one installable package per plugin, optionally including its portable settings. Only package files and selected portable settings are included. Saved plugin data, credentials, permissions, enablement and runtime state are excluded. Existing destination files are never replaced.", "Export")
            .Choice("settings", "Settings preset", ["Package only", "Publisher defaults", "Current portable settings"], "Package only")
            .Add("path", "Destination folder", settings.SharedDirectory)
            .ActionButton("Choose folder…", dialog =>
            {
                var picker = new OpenFolderDialog { Title = "Export plugin packages", Multiselect = false };
                if (picker.ShowDialog(dialog) == true) dialog.SetValue("path", picker.FolderName);
            });
        if (form.ShowDialog() != true) return Task.CompletedTask;
        var mode = form.Selection("settings") switch { "Publisher defaults" => "defaults", "Current portable settings" => "current", _ => "none" };
        var directory = Path.GetFullPath(form.Value("path"));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Choose an existing export folder.");
        var outcomes = new List<DesktopPluginBatchOutcome>();
        foreach (var item in selected)
        {
            Lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                if (item.Error is not null) throw new InvalidDataException("Repair this plugin before exporting it: " + item.Error);
                var path = Path.Combine(directory, item.Id + "-" + item.Version + CerebralPluginPackageContract.Extension);
                CerebralPluginPackage.Export(item, path, mode, ConfigurationProductPaths(), desktopPluginDataRoot);
                outcomes.Add(new(item.Id, "Exported to " + path, DesktopPluginBatchStatus.Completed));
            }
            catch (Exception error) when (error is not OperationCanceledException) { outcomes.Add(new(item.Id, "Failed: " + error.Message, DesktopPluginBatchStatus.Failed)); }
        }
        ReportDesktopPluginBatch("Export packages", outcomes);
        return Task.CompletedTask;
    }

    private static string DesktopPluginSelectionDescription(IReadOnlyList<DesktopPluginInfo> selected) =>
        selected.Count == 1 ? selected[0].Name : $"{selected.Count:N0} selected plugins\n\n" +
        string.Join("\n", selected.Take(8).Select(item => "• " + item.Name)) +
        (selected.Count > 8 ? $"\n… and {selected.Count - 8:N0} more" : "");

    private void ReportDesktopPluginBatch(string action, IReadOnlyList<DesktopPluginBatchOutcome> outcomes)
    {
        var failures = outcomes.Count(item => item.Status == DesktopPluginBatchStatus.Failed);
        var skipped = outcomes.Count(item => item.Status == DesktopPluginBatchStatus.Skipped);
        var pending = outcomes.Count(item => item.Status == DesktopPluginBatchStatus.CleanupPending);
        var completed = outcomes.Count - failures - skipped;
        var cleanup = pending > 0 ? $" ({pending:N0} with package cleanup pending)" : "";
        var needsAttention = failures > 0 || pending > 0;
        SetStatus($"{action}: {completed:N0} completed{cleanup}; {skipped:N0} skipped; {failures:N0} failed. " +
            "Each plugin is handled separately.", needsAttention);
        if (needsAttention)
            Ui.ShowText(this, action + " · Results", string.Join("\n\n", outcomes.Select(item => item.Id + "\n" + item.Result)), wrap: true);
    }

    private enum DesktopPluginBatchStatus { Completed, Skipped, Failed, CleanupPending }

    private sealed record DesktopPluginBatchOutcome(string Id, string Result, DesktopPluginBatchStatus Status);
}
