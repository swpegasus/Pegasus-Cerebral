using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private static void DescribeComputeNavigation(TabControl tabs)
    {
        AutomationProperties.SetName(tabs, "Compute setup: Machines, Models, then Agent profiles");
        var steps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfigurationTabMachines"] = "1. Connect or provision the machines that provide model execution.",
            ["ConfigurationTabModels"] = "2. Choose a machine, then manage its model library and verified capabilities.",
            ["ConfigurationTabAgents"] = "3. Choose agent behavior, required model capabilities, and its model route."
        };
        foreach (var tab in tabs.Items.OfType<TabItem>())
            if (steps.TryGetValue(AutomationProperties.GetAutomationId(tab), out var description))
                tab.ToolTip = description;
    }

    private static FieldsDialog AddAgentModelNeedsFields(FieldsDialog form, AgentProfile profile)
    {
        var extra = profile.RequiredModelCapabilities.Where(id => id is not (ModelCapabilityIds.TextGeneration or ModelCapabilityIds.ImageInput)).ToArray();
        return form.Tab("needs", "Model needs")
            .Check("requiresImages", "Understand attached images", profile.RequiredModelCapabilities.Contains(ModelCapabilityIds.ImageInput, StringComparer.Ordinal))
            .Add("modelNeedsHelp", "Model capabilities",
                "Text generation is included automatically. Enable image understanding only when this agent needs to read images; the selected model must have verified image support.\n\n" +
                (extra.Length == 0 ? "Other specialized requirements can be managed in Advanced." :
                    "Existing specialized requirements are preserved: " + string.Join(", ", extra) + ". Manage these in Advanced."), true)
            .SetReadOnly("modelNeedsHelp");
    }

    private static void ApplyAgentModelNeeds(AgentProfile profile, bool images)
    {
        // Change only the capability exposed here. Existing plugin-defined needs remain the owner's draft.
        if (images)
        {
            if (!profile.RequiredModelCapabilities.Contains(ModelCapabilityIds.ImageInput, StringComparer.Ordinal))
                profile.RequiredModelCapabilities.Add(ModelCapabilityIds.ImageInput);
        }
        else profile.RequiredModelCapabilities = profile.RequiredModelCapabilities
            .Where(id => id != ModelCapabilityIds.ImageInput).ToList();
    }

    private int CheckConfigurationNavigationLayout()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var compute = (TabControl)Tabs.Items.OfType<TabItem>().Single(tab => AutomationProperties.GetAutomationId(tab) == "ConfigurationAreaModelsAndAgents").Content;
        Check(compute.Items.Count >= 3 &&
            compute.Items.OfType<TabItem>().Take(3).Select(AutomationProperties.GetAutomationId)
                .SequenceEqual(["ConfigurationTabMachines", "ConfigurationTabModels", "ConfigurationTabAgents"]),
            "Compute navigation must begin with plain pages for Machines, Models and Agent profiles.");
        Check(AutomationProperties.GetName(compute).Contains("Machines, Models", StringComparison.Ordinal) &&
            compute.Items.OfType<TabItem>().Take(3).All(tab => tab.ToolTip is string text && text.Length > 20),
            "The compute sequence retains accessible guidance without changing stable tab IDs.");
        foreach (var tabs in Tabs.Items.OfType<TabItem>().Select(tab => (TabControl)tab.Content))
            Check(tabs.VerticalContentAlignment == VerticalAlignment.Stretch && tabs.HorizontalContentAlignment == HorizontalAlignment.Stretch,
                "Configuration content must use the available page viewport with its existing top-aligned page controls.");
        Check(settingsNavigation.Navigation.Items.Count == Tabs.Items.Count,
            "The Settings rail must expose every category without rendering tab headers.");
        SelectTab("Agents");
        Check(settingsNavigation.Navigation.SelectedItem is TreeViewItem { Header: ConfigurationSettingsNavigation.NavigationItem selectedPage } &&
              AutomationProperties.GetAutomationId(selectedPage.Tab) == "ConfigurationTabAgents",
            "Programmatic navigation must select the same page in the settings rail.");
        var beforeSearch = (TabItem)Tabs.SelectedItem;
        var beforePage = ((TabControl)beforeSearch.Content).SelectedItem;
        settingsNavigation.Search.Text = "desktop plugins";
        var desktopResult = settingsNavigation.VisiblePages.SingleOrDefault(item => AutomationProperties.GetAutomationId(item.Tab) == "ConfigurationTabDesktopPlugins");
        Check(desktopResult is not null,
            "Search must find a page across categories without loading live table data.");
        Check(ReferenceEquals(Tabs.SelectedItem, beforeSearch) && ReferenceEquals(((TabControl)beforeSearch.Content).SelectedItem, beforePage),
            "Typing a search must not replace the current detail page.");
        Check(settingsNavigation.SelectPage(desktopResult!.Tab) &&
              ReferenceEquals(Tabs.SelectedItem, Tabs.Items.OfType<TabItem>().Single(tab => ReferenceEquals(tab.Content, extensionTabs))) &&
              AutomationProperties.GetAutomationId((TabItem)extensionTabs.SelectedItem) == "ConfigurationTabDesktopPlugins",
            "Selecting a search result must route to its category and existing page.");
        settingsNavigation.Search.Text = "unmatched-navigation-fixture";
        Check(settingsNavigation.VisiblePages.Count == 0 && AutomationProperties.GetAutomationId((TabItem)extensionTabs.SelectedItem) == "ConfigurationTabDesktopPlugins",
            "An empty search result must keep the current detail page available.");
        SelectTab("Agents");
        Check(settingsNavigation.Search.Text.Length == 0 &&
              settingsNavigation.Navigation.SelectedItem is TreeViewItem { Header: ConfigurationSettingsNavigation.NavigationItem linkedPage } &&
              AutomationProperties.GetAutomationId(linkedPage.Tab) == "ConfigurationTabAgents",
            "A direct page link must clear a stale search and expose its actual navigation.");

        var temporaryPage = AddNestedTab(compute, "Temporary navigation fixture", Ui.Page("Temporary fixture", "Search-only fixture", new TextBlock()), "TemporaryFixture");
        try
        {
            settingsNavigation.Search.Text = "temporary navigation";
            Check(settingsNavigation.VisiblePages.Count == 1 && ReferenceEquals(settingsNavigation.VisiblePages[0].Tab, temporaryPage),
                "A page added after construction must appear in settings search.");
        }
        finally { compute.Items.Remove(temporaryPage); settingsNavigation.Search.Clear(); }
        Check(settingsNavigation.VisiblePages.All(item => item.Tab != temporaryPage),
            "Removing a dynamic page must retire its navigation entry.");

        // Resize the owner content, rather than a child whose parent would reapply the previous viewport.
        var ownerContent = (FrameworkElement)Content;
        ownerContent.Measure(new Size(1024, 768));
        ownerContent.Arrange(new Rect(0, 0, 1024, 768));
        ownerContent.UpdateLayout();
        Check(settingsNavigation.ColumnDefinitions[0].ActualWidth <= 220 && settingsNavigation.ColumnDefinitions[1].ActualWidth >= 700,
            $"A small monitor must reserve most of the Settings width for detail controls. Rail={settingsNavigation.ColumnDefinitions[0].ActualWidth}, detail={settingsNavigation.ColumnDefinitions[1].ActualWidth}, viewport={settingsNavigation.ActualWidth}.");
        static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
        {
            if (parent is not System.Windows.Media.Visual && parent is not System.Windows.Media.Media3D.Visual3D) yield break;
            for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
                yield return child; foreach (var nested in VisualChildren(child)) yield return nested;
            }
        }
        Check(!VisualChildren(settingsNavigation).OfType<System.Windows.Controls.Primitives.TabPanel>()
                .Any(panel => panel.IsVisible && panel.ActualWidth > 0 && panel.ActualHeight > 0),
            "Settings must render one category/page rail while keeping only hidden header containers for standard tab peers.");
        Check(settingsNavigation.Navigation.Foreground == Ui.Ink && settingsNavigation.Navigation.Background == Ui.Paper,
            "Navigation must use the live shared theme brushes rather than fixed palette colors.");
        var profile = new AgentProfile { RequiredModelCapabilities = [ModelCapabilityIds.TextGeneration, "plugin-specialized"] };
        var form = new FieldsDialog(this, "Model needs fixture");
        try
        {
            AddAgentModelNeedsFields(form, profile);
            Check(!form.Checked("requiresImages") && form.Raw("modelNeedsHelp").Contains("plugin-specialized", StringComparison.Ordinal),
                "Text-only agents must not acquire an image requirement, and plugin requirements remain visible.");
            ApplyAgentModelNeeds(profile, true);
            Check(profile.RequiredModelCapabilities.SequenceEqual([ModelCapabilityIds.TextGeneration, "plugin-specialized", ModelCapabilityIds.ImageInput]),
                "Image selection preserves existing requirements and adds only image input.");
            ApplyAgentModelNeeds(profile, true);
            Check(profile.RequiredModelCapabilities.Count(id => id == ModelCapabilityIds.ImageInput) == 1,
                "Repeated capability selection must remain idempotent.");
            ApplyAgentModelNeeds(profile, false);
            Check(profile.RequiredModelCapabilities.SequenceEqual([ModelCapabilityIds.TextGeneration, "plugin-specialized"]),
                "Clearing image selection cannot remove text or plugin-defined needs.");
            profile.RequiredModelCapabilities = [ModelCapabilityIds.ImageInput, "plugin-specialized", ModelCapabilityIds.TextGeneration];
            ApplyAgentModelNeeds(profile, true);
            Check(profile.RequiredModelCapabilities.SequenceEqual([ModelCapabilityIds.ImageInput, "plugin-specialized", ModelCapabilityIds.TextGeneration]),
                "An unchanged checked image need must preserve the owner's exact existing capability order.");
        }
        finally { form.DiscardDraftAndClose(); }
        return checks;
    }
    internal static async Task<int> CheckConfigurationNavigationAccessibilityAsync(CancellationToken token)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
        var row = new AgentProfile { Id = "configuration-fixture-helper", Name = "Fixture helper", Role = "helper" };
        var grid = Ui.Grid(("Profile", "Name", 1.5), ("Role", "Role", 1));
        AutomationProperties.SetAutomationId(grid, "ConfigurationFixtureAgents");
        grid.ItemsSource = new[] { row };
        grid.LoadingRow += (_, e) =>
        {
            AutomationProperties.SetAutomationId(e.Row, "ConfigurationFixtureAgentRow");
            AutomationProperties.SetName(e.Row, "Fixture helper");
        };
        var editCount = 0;
        var edit = Ui.Button("Edit fixture", () => editCount++);
        AutomationProperties.SetAutomationId(edit, "ConfigurationFixtureEdit");
        var machineCount = 0;
        var machine = Ui.Button("Inspect fixture machine", () => machineCount++);
        AutomationProperties.SetAutomationId(machine, "ConfigurationFixtureMachine");
        var compute = new TabControl();
        var machines = new TabItem { Header = "Machines", Content = Ui.Page("Machines", "Isolated fixture", machine) };
        var profiles = new TabItem { Header = "Agent profiles", Content = Ui.Page("Agent profiles", "Isolated fixture", grid, edit) };
        AutomationProperties.SetAutomationId(machines, "ConfigurationTabMachines");
        AutomationProperties.SetAutomationId(profiles, "ConfigurationTabAgents");
        compute.Items.Add(machines); compute.Items.Add(profiles);
        var category = new TabItem { Header = "Compute & agents", Content = compute };
        AutomationProperties.SetAutomationId(category, "ConfigurationAreaModelsAndAgents");
        var mounts = new TabControl();
        AutomationProperties.SetAutomationId(mounts, "ApplicationTabs");
        mounts.Items.Add(category);
        using var navigation = new ConfigurationSettingsNavigation(mounts);
        var host = new Window
        {
            Title = "Configuration accessibility fixture", Content = navigation,
            Width = 1024, Height = 768, Background = Ui.Surface, Foreground = Ui.Ink,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        async Task Flush()
        {
            token.ThrowIfCancellationRequested();
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
            host.UpdateLayout();
        }
        try
        {
            Check(navigation.SelectPage(profiles), "The fixture must use normal Settings rail routing.");
            host.Show();
            await Flush();
            Check(host.IsVisible && new WindowInteropHelper(host).Handle != IntPtr.Zero &&
                grid.IsVisible && grid.ActualWidth > 400 && grid.ActualHeight > 100,
                "A real isolated shown window must render the selected profile page with positive bounds.");
            Check(mounts.ItemContainerGenerator.ContainerFromItem(category) is TabItem &&
                compute.ItemContainerGenerator.ContainerFromItem(profiles) is TabItem,
                "Hidden item hosts must realize both levels of standard TabItem containers.");
            var visualNodes = ConfigurationFixtureVisuals(navigation);
            var headers = visualNodes.OfType<ItemsPresenter>().Where(item => item.Visibility == Visibility.Hidden).ToArray();
            Check(headers.Length >= 2 && headers.All(item => item.ActualWidth == 0 && item.ActualHeight == 0) &&
                !visualNodes.OfType<System.Windows.Controls.Primitives.TabPanel>()
                    .Any(panel => panel.IsVisible && panel.ActualWidth > 0 && panel.ActualHeight > 0),
                "The accessible item hosts must remain zero-size and hidden without drawing duplicate headers.");
            var peers = ConfigurationFixturePeers(host);
            var gridPeer = peers.SingleOrDefault(peer => peer.GetAutomationId() == "ConfigurationFixtureAgents");
            var rowPeer = peers.SingleOrDefault(peer => peer.GetAutomationId() == "ConfigurationFixtureAgentRow");
            var editPeer = peers.SingleOrDefault(peer => peer.GetAutomationId() == "ConfigurationFixtureEdit");
            Check(gridPeer is not null && !gridPeer.IsOffscreen() && gridPeer.GetBoundingRectangle().Width > 400 &&
                rowPeer is not null && rowPeer.GetName() == "Fixture helper" && !rowPeer.IsOffscreen() &&
                editPeer is not null && !editPeer.IsOffscreen(),
                "The actual WindowAutomationPeer tree must expose the selected headerless page's grid, exact row and Edit control.");
            var selection = rowPeer!.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new InvalidOperationException("The shown fixture row has no selection provider.");
            selection.Select();
            await Flush();
            Check(ReferenceEquals(grid.SelectedItem, row), "Native peer selection must select the exact fixture profile row.");
            var invoke = editPeer!.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("The shown Edit control has no invoke provider.");
            invoke.Invoke();
            await Flush();
            Check(editCount == 1, "The selected page's ordinary Edit peer must invoke its isolated fixture action once.");
            var width = checked((int)Math.Ceiling(navigation.ActualWidth));
            var height = checked((int)Math.Ceiling(navigation.ActualHeight));
            if (width is <= 0 or > 2048 || height is <= 0 or > 2048) throw new InvalidOperationException("Fixture render dimensions are out of bounds.");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(navigation);
            var pixels = new byte[checked(width * height * 4)];
            bitmap.CopyPixels(pixels, width * 4, 0);
            var colors = new HashSet<int>();
            for (var i = 0; i + 3 < pixels.Length; i += 256) colors.Add(BitConverter.ToInt32(pixels, i));
            Check(colors.Count > 3, "The shown fixture's WPF bitmap must contain rendered theme/control content.");
            var machineNode = peers.Single(peer => peer.GetAutomationId() == "SettingsNavigationConfigurationTabMachines");
            (machineNode.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new InvalidOperationException("The normal machine navigation has no selection provider.")).Select();
            await Flush();
            peers = ConfigurationFixturePeers(host);
            Check(ReferenceEquals(compute.SelectedItem, machines) &&
                peers.Any(peer => peer.GetAutomationId() == "ConfigurationFixtureMachine") &&
                peers.All(peer => peer.GetAutomationId() != "ConfigurationFixtureAgents"),
                $"Normal rail selection must replace the exposed detail page without retaining stale profile content. " +
                $"selectedMachines={ReferenceEquals(compute.SelectedItem, machines)}, " +
                $"machinePeer={peers.Any(peer => peer.GetAutomationId() == "ConfigurationFixtureMachine")}, " +
                $"profilePeer={peers.Any(peer => peer.GetAutomationId() == "ConfigurationFixtureAgents")}.");
            var machinePeer = peers.Single(peer => peer.GetAutomationId() == "ConfigurationFixtureMachine");
            (machinePeer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("The selected machine fixture action has no invoke provider.")).Invoke();
            await Flush();
            Check(machineCount == 1 && editCount == 1, "The new selected page must expose only its own ordinary action.");
            var profileNode = peers.Single(peer => peer.GetAutomationId() == "SettingsNavigationConfigurationTabAgents");
            (profileNode.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new InvalidOperationException("The normal profile navigation has no selection provider.")).Select();
            await Flush();
            Check(ReferenceEquals(compute.SelectedItem, profiles) &&
                ConfigurationFixturePeers(host).Any(peer => peer.GetAutomationId() == "ConfigurationFixtureAgentRow"),
                "Returning through normal navigation must expose the realized profile row again.");
            Check(navigation.Navigation.Foreground == Ui.Ink && navigation.Navigation.Background == Ui.Paper,
                "The accessible headerless template must retain the live shared theme brushes.");
            return checks;
        }
        finally { host.Close(); }
    }

    private static IReadOnlyList<AutomationPeer> ConfigurationFixturePeers(Window host)
    {
        var root = UIElementAutomationPeer.CreatePeerForElement(host)
            ?? throw new InvalidOperationException("The shown fixture Window has no automation peer.");
        var result = new List<AutomationPeer>();
        var queue = new Queue<(AutomationPeer Peer, int Depth)>();
        var visited = new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (peer, depth) = queue.Dequeue();
            if (!visited.Add(peer)) continue;
            if (depth > 24 || result.Count >= 1024) throw new InvalidOperationException("Fixture automation tree exceeded its bound.");
            result.Add(peer);
            // GetChildren caches its result. This in-process fixture needs a fresh peer snapshot
            // after SelectionItem.Select, rather than re-reading the previous page's cached list.
            peer.ResetChildrenCache();
            foreach (var child in peer.GetChildren() ?? []) queue.Enqueue((child, depth + 1));
        }
        return result;
    }

    private static IReadOnlyList<DependencyObject> ConfigurationFixtureVisuals(DependencyObject root)
    {
        var result = new List<DependencyObject>();
        var queue = new Queue<(DependencyObject Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth > 24 || result.Count >= 1024) throw new InvalidOperationException("Fixture visual tree exceeded its bound.");
            result.Add(node);
            if (node is not Visual && node is not System.Windows.Media.Media3D.Visual3D) continue;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                queue.Enqueue((VisualTreeHelper.GetChild(node, i), depth + 1));
        }
        return result;
    }

}
