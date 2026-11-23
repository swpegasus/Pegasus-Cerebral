using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private int CheckDesktopPluginSelectionLayout()
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var page = (DockPanel)extensionTabs.Items.OfType<TabItem>().Single(tab =>
            AutomationProperties.GetAutomationId(tab) == "ConfigurationTabDesktopPlugins").Content;
        var heading = page.Children.OfType<StackPanel>().Single();
        var permanent = heading.Children.OfType<WrapPanel>().Single().Children.OfType<Button>()
            .Select(AutomationProperties.GetAutomationId).ToArray();
        Check(permanent.SequenceEqual(["DesktopPluginInstall", "DesktopPluginManageGo"]) &&
            heading.Children.OfType<WrapPanel>().Single().Children.OfType<ComboBox>().Single() == desktopPluginManage,
            "Desktop plugins must expose Install and the labeled Manage chooser with one Go action.");
        Check(desktopPluginList.SelectionMode == DataGridSelectionMode.Extended &&
            desktopPluginList.SelectionUnit == DataGridSelectionUnit.FullRow,
            "Plugin management must support native keyboard and full-row multiple selection.");
        Check(!CanOpenSettingsItemContextMenu(keyboardInvocation: false, pointerHasRow: false, hasSelection: true),
            "Right-clicking blank list space must not operate on a previously selected item.");
        Check(CanOpenSettingsItemContextMenu(keyboardInvocation: true, pointerHasRow: false, hasSelection: true),
            "Keyboard context-menu invocation must retain the current selected target without a pointer row.");
        Check(CanOpenSettingsItemContextMenu(keyboardInvocation: false, pointerHasRow: true, hasSelection: true),
            "A pointer context menu must remain available for an actual selected row.");
        Check(!CanOpenSettingsItemContextMenu(keyboardInvocation: true, pointerHasRow: false, hasSelection: false) &&
            !CanOpenSettingsItemContextMenu(keyboardInvocation: false, pointerHasRow: true, hasSelection: false),
            "Neither keyboard nor pointer invocation may open item actions without a selection.");

        bool ManageEnabled(string action) => desktopPluginManage.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, action)).IsEnabled;
        var originalItems = desktopPluginItems;
        var originalSearch = desktopPluginSearch.Text;
        var originalFilter = desktopPluginFilter.SelectedItem;
        var originalSelectedIds = desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().Select(item => item.Id).ToArray();
        var enabled = new DesktopPluginInfo("fixture.enabled", "Enabled fixture", "1.0.0", "Enabled fixture description", true, "enabled-directory", "enabled-manifest", null);
        var disabled = new DesktopPluginInfo("fixture.disabled", "Disabled fixture", "1.0.0", "Disabled fixture description", false, "disabled-directory", "disabled-manifest", null);
        var another = new DesktopPluginInfo("fixture.another", "Another fixture", "1.0.0", "Another fixture description", false, "another-directory", "another-manifest", null);
        try
        {
            desktopPluginItems = [enabled, disabled, another];
            desktopPluginSearch.Text = ""; desktopPluginFilter.SelectedIndex = 0;
            ApplyDesktopPluginFilter(); desktopPluginList.UnselectAll();
            Check(desktopPluginSelectionBar!.Visibility == Visibility.Collapsed &&
                desktopPluginSelectionDetails!.Visibility == Visibility.Collapsed,
                "Selection-only controls and metadata must disappear when no plugin is selected.");
            desktopPluginList.SelectedItems.Add(enabled); desktopPluginList.SelectedItems.Add(disabled);
            Check(desktopPluginSelectionBar!.Visibility == Visibility.Visible &&
                ManageEnabled("enable") && ManageEnabled("disable") &&
                !ManageEnabled("remove") && desktopPluginSelectionDetails!.Visibility == Visibility.Collapsed,
                "Mixed selections must offer distinct Enable and Disable actions without permitting removal of an enabled package.");
            PopulateDesktopPluginContextMenu();
            Check(!desktopPluginMenu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetAutomationId(item) is
                    "DesktopPluginConfigure" or "DesktopPluginInspect"),
                "Single-plugin actions must not accidentally target one item in a multiple selection.");
            SelectDesktopPluginContextTarget(enabled);
            Check(desktopPluginList.SelectedItems.Count == 2,
                "Right-clicking a selected plugin must preserve the entire selected group.");
            SelectDesktopPluginContextTarget(another);
            Check(desktopPluginList.SelectedItems.Count == 1 && ReferenceEquals(desktopPluginList.SelectedItem, another),
                "Right-clicking another plugin must target only that item.");
            PopulateDesktopPluginContextMenu();
            Check(desktopPluginMenu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "DesktopPluginConfigure").IsEnabled == false &&
                desktopPluginMenu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetAutomationId(item) == "DesktopPluginInspect"),
                "A disabled single plugin must retain Inspect and may not open Configuration.");
            Check(desktopPluginSelectionDetails!.Visibility == Visibility.Visible &&
                desktopPluginDetailsId.Text == another.Id &&
                desktopPluginSelectionDescriptionText!.Text.Contains(another.Description, StringComparison.Ordinal),
                "The selected plugin must have readable ID and description details beneath the list.");
            desktopPluginList.SelectedItems.Add(disabled);
            Check(ManageEnabled("remove") && !ManageEnabled("disable") &&
                ManageEnabled("enable"),
                "A disabled group must allow Remove and Enable without an ambiguous toggle.");
            ApplyDesktopPluginFilter();
            Check(desktopPluginList.SelectedItems.OfType<DesktopPluginInfo>().Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals([disabled.Id, another.Id]),
                "A catalog refresh must preserve every still-visible selected identity.");
            desktopPluginSearch.Text = "Another fixture";
            Check(desktopPluginList.SelectedItems.Count == 1 && ((DesktopPluginInfo)desktopPluginList.SelectedItem).Id == another.Id,
                "Filtering must exclude hidden plugins from subsequent bulk actions.");
            desktopPluginSearch.Text = "";
            Check(desktopPluginList.SelectedItems.Count == 1,
                "Clearing a filter must not resurrect hidden selections.");
            var tooManyRejected = false;
            desktopPluginList.SelectedItems.Add(disabled);
            try { _ = SingleSelectedDesktopPlugin(); } catch (InvalidOperationException) { tooManyRejected = true; }
            Check(tooManyRejected, "Single-item operations must reject multiple selections before reading or mutating plugin state.");
            var document = new FlowDocument(new Paragraph(new Run("A content element")));
            Check(SettingsRowFromSource(((Paragraph)document.Blocks.FirstBlock!).Inlines.FirstInline!) is null,
                "Content-element click sources must not be cast to Visual and crash plugin management.");

            var manifests = new Dictionary<string, DesktopPluginManifest>(StringComparer.Ordinal)
            {
                [enabled.Id] = new() { Id = enabled.Id, Dependencies = [new() { Id = disabled.Id }, new() { Id = "unselected.required" }] },
                [disabled.Id] = new() { Id = disabled.Id },
                [another.Id] = new() { Id = another.Id, Dependencies = [new() { Id = enabled.Id, Optional = true }] }
            };
            Check(OrderDesktopPluginBatch([enabled, disabled], manifests, dependenciesFirst: true).Select(item => item.Id)
                .SequenceEqual([disabled.Id, enabled.Id]),
                "Bulk enablement must order selected required dependencies before their dependents.");
            Check(OrderDesktopPluginBatch([enabled, disabled], manifests, dependenciesFirst: false).Select(item => item.Id)
                .SequenceEqual([enabled.Id, disabled.Id]),
                "Bulk disablement must order selected dependents before their required providers.");
            Check(OrderDesktopPluginBatch([another], manifests, dependenciesFirst: true).Single().Id == another.Id,
                "Batch ordering must not add optional or unselected plugins to the requested scope.");
            manifests[enabled.Id].ExclusiveGroup = "fixture.alternatives";
            manifests[disabled.Id].ExclusiveGroup = "fixture.alternatives";
            var mixedExclusiveRejected = false;
            try { ValidateDesktopPluginEnableSelection([enabled, disabled], manifests); }
            catch (InvalidOperationException) { mixedExclusiveRejected = true; }
            Check(mixedExclusiveRejected,
                "Enable must reject a selected enabled provider and its disabled alternative before deriving mutation targets.");
            var disabledExclusiveRejected = false;
            try { ValidateDesktopPluginEnableSelection([enabled with { Enabled = false }, disabled], manifests); }
            catch (InvalidOperationException) { disabledExclusiveRejected = true; }
            Check(disabledExclusiveRejected, "Two disabled alternative providers cannot both be requested for enablement.");
            ValidateDesktopPluginEnableSelection([disabled], manifests);
            Check(true, "Selecting one alternative must remain valid even when the manifest observation also includes another provider.");
            ValidateDesktopPluginEnableSelection([enabled, another], manifests);
            Check(true, "A selection with unrelated providers must remain valid.");
            manifests[disabled.Id].Dependencies = [new() { Id = enabled.Id }];
            var cycleRejected = false;
            try { _ = OrderDesktopPluginBatch([enabled, disabled], manifests, dependenciesFirst: true); }
            catch (InvalidOperationException) { cycleRejected = true; }
            Check(cycleRejected, "Selected dependency cycles must be rejected before a batch mutation.");
        }
        finally
        {
            desktopPluginItems = originalItems; desktopPluginSearch.Text = originalSearch;
            desktopPluginFilter.SelectedItem = originalFilter; ApplyDesktopPluginFilter();
            desktopPluginList.UnselectAll();
            foreach (var item in desktopPluginList.Items.OfType<DesktopPluginInfo>().Where(item => originalSelectedIds.Contains(item.Id, StringComparer.Ordinal)))
                desktopPluginList.SelectedItems.Add(item);
            PopulateDesktopPluginContextMenu();
        }
        return checks;
    }
}
