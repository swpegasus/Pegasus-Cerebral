using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{

    private int CheckLocalRecoverySurface()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
        static DependencyObject[] Elements(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>(); pending.Push(root);
            var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
            while (pending.TryPop(out var item))
                if (seen.Add(item))
                    foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
                        pending.Push(child);
            return seen.ToArray();
        }
        var all = Elements(this);
        var header = all.Single(item => AutomationProperties.GetAutomationId(item) == "ApplicationHeader");
        var headerItems = Elements(header);
        Check(!headerItems.OfType<PasswordBox>().Any() &&
              !headerItems.OfType<Expander>().Any(item => Equals(item.Header, "Connection settings")) &&
              !headerItems.OfType<CheckBox>().Any(item => item.Content?.ToString()?.Contains("Start local services", StringComparison.Ordinal) == true),
            "The normal settings header must not require service, key or startup configuration.");
        foreach (var id in new[] { "ConfigurationLocalRecoverySummary", "ConfigurationRetryLocalConnection",
                     "ConfigurationOpenWorkerLog", "ConfigurationOpenDataFolder" })
            Check(all.Count(item => AutomationProperties.GetAutomationId(item) == id) == 1,
                "Each local recovery control must remain reachable exactly once: " + id);
        foreach (var id in new[] { "ConfigurationRetryLocalConnection", "ConfigurationOpenWorkerLog", "ConfigurationOpenDataFolder" })
            Check(all.OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == id).IsEnabled,
                "Local recovery controls must stay available without a Worker connection: " + id);
        Check(IsConnected || localRecoverySummary.Text.Contains("Offline", StringComparison.Ordinal),
            "An offline settings window must identify its local recovery state.");
        return checks;
    }

    private int CheckSettingsHierarchy()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
        checks += CheckLocalRecoverySurface();
        var areas = Tabs.Items.OfType<TabItem>().ToArray();
        var pages = areas.SelectMany(area => ((TabControl)area.Content).Items.OfType<TabItem>()
            .Select(page => (Area: area, Page: page))).ToArray();
        string AreaOf(string id) => AutomationProperties.GetAutomationId(pages.Single(pair =>
            AutomationProperties.GetAutomationId(pair.Page) == "ConfigurationTab" + id).Area);
        Check(pages.Select(pair => AutomationProperties.GetAutomationId(pair.Page)).Distinct(StringComparer.Ordinal).Count() == pages.Length,
            "Each settings page must have one route even when its category changes.");
        foreach (var id in new[] { "Settings", "Appearance", "Chats", "Backup" })
            Check(AreaOf(id) == "ConfigurationAreaSystem", "Everyday preferences belong to General.");
        foreach (var id in new[] { "Folders", "Resources", "Retention", "Access", "Engine", "Dashboard", "Diagnostics" })
            Check(AreaOf(id) == "ConfigurationAreaAdvanced", "Technical controls must be grouped under Advanced.");
        foreach (var id in new[] { "Cortex", "UserConnector", "PeerSharing" })
            Check(AreaOf(id) == "ConfigurationAreaConnections", "Distinct connection objects must retain their owning connection pages.");
        static bool Inside(DependencyObject child, DependencyObject parent)
        {
            for (DependencyObject? current = child; current is not null; current = LogicalTreeHelper.GetParent(current))
                if (ReferenceEquals(current, parent)) return true;
            return false;
        }
        var nativePage = (DependencyObject)pages.Single(pair =>
            AutomationProperties.GetAutomationId(pair.Page) == "ConfigurationTabServiceExtensions").Page.Content;
        var toolsPage = (DependencyObject)pages.Single(pair =>
            AutomationProperties.GetAutomationId(pair.Page) == "ConfigurationTabToolsAndMcp").Page.Content;
        Check(Inside(serviceExtensions, nativePage) && !Inside(plugins, nativePage),
            "Service extensions must display native Worker inventory rather than MCP declarations.");
        Check(Inside(plugins, toolsPage) && Inside(executables, toolsPage) && Inside(mcp, toolsPage),
            "MCP declarations, provider connections and executable registrations must remain available under Tools & MCP.");
        var inventory = ReadServiceExtensionInventory(CerebralJson.Element(new { servicePlugins = new[] {
            new { id = "fixture.native", name = "Fixture extension", version = "1.0", loaded = true,
                tools = new[] { "fixture.read" }, commands = new[] { "fixture.inspect" }, error = (string?)null }
        } }));
        Check(inventory.Single() is { Loaded: true, ToolCount: 1, CommandCount: 1 },
            "Native inventory must preserve the Worker's loaded state and contribution counts.");
        bool inventoryRejected = false;
        try { _ = ReadServiceExtensionInventory(CerebralJson.Element(new { plugins = Array.Empty<object>() })); }
        catch (InvalidDataException) { inventoryRejected = true; }
        Check(inventoryRejected, "A missing native inventory must not become an empty successful service-extension list.");
        var nativeConnection = new McpRegistration { Id = "custom-native", Enabled = true, ViaUserConnector = false,
            CortexNative = new(@"C:\Cortex\bin\host\PegasusCortex.Host.exe", @"C:\Cortex\client.key", 1) };
        var connections = new[] {
            new McpRegistration { Id = "cortex", Executable = @"C:\Cortex\PegasusCortex.exe", Arguments = ["mcp"] },
            new McpRegistration { Id = "standard-provider", Executable = @"C:\Tools\provider.exe" },
            nativeConnection,
            new McpRegistration { Id = "mixed-route", ViaUserConnector = false, Url = "http://127.0.0.1:1",
                CortexNative = nativeConnection.CortexNative }
        };
        Check(NativeCortexConnections(connections).SequenceEqual([nativeConnection]) && connections.Length == 4,
            "Cortex choices require an explicit unmixed native binding; names must not classify or modify standard MCP providers.");
        var labels = FriendlyCortexConnections(connections);
        Check(labels.Count == 1 && labels.Single().Value == nativeConnection.Id &&
              labels.Single().Key.Contains("native pipe", StringComparison.Ordinal),
            "The Cortex chooser must identify the exact native connection without relabeling standard MCP entries.");
        SelectTab("Settings");
        var advanced = settingsNavigation.Navigation.Items.OfType<TreeViewItem>().Single(item =>
            item.Header is ConfigurationSettingsNavigation.NavigationItem model &&
            AutomationProperties.GetAutomationId(model.Tab) == "ConfigurationAreaAdvanced");
        Check(!advanced.IsExpanded, "Advanced must remain collapsed before the owner selects it.");
        settingsNavigation.Search.Text = "memory";
        Check(settingsNavigation.VisiblePages.Any(page => AutomationProperties.GetAutomationId(page.Tab) == "ConfigurationTabResources"),
            "Settings search must discover a technical control by its short description.");
        SelectTab("Retention");
        Check(settingsNavigation.Search.Text.Length == 0 &&
            AutomationProperties.GetAutomationId((TabItem)Tabs.SelectedItem) == "ConfigurationAreaAdvanced",
            "A direct link to a relocated page must expose Advanced and clear a stale search.");
        SelectTab("Appearance");
        Check(appearanceTheme.Items.Count == 3 && messageTimeFormat.Items.Count == 2 && recentChatLimitInput.Text.Length > 0,
            "Moving interface preferences must keep their controls initialized.");
        Check(agents.ContextMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Advanced settings…")) == true &&
            workers.ContextMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Rename")) == true,
            "Item-specific agent settings and friendly machine naming must remain accessible through native menus.");
        SelectTab("Settings");
        return checks;
    }

    /// <summary>Read-only owner transport and native WPF rendering; no shown window, model invocation, or preference write.</summary>
    internal async Task CheckSettingsLayoutAsync(string captureDirectory)
    {
        Dispatcher.VerifyAccess();
        var directory = Path.GetFullPath(captureDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Create the task-owned settings capture directory first.");
        var saved = ConnectionStore.Read().Preferences;
        if (saved?.Transport != "pipe" || saved.LocalEndpoint is null || string.IsNullOrWhiteSpace(saved.LocalDataDirectory))
            throw new InvalidOperationException("Settings layout inspection requires the existing local owner pipe.");
        await InspectServiceUiAsync(saved.LocalEndpoint, saved.LocalDataDirectory, null);
        var checks = CheckSettingsHierarchy();
        var originalTheme = Ui.AppearanceTheme;
        var images = new List<string>();
        try
        {
            foreach (var theme in new[] { AppearanceThemePolicy.Light, AppearanceThemePolicy.Dark })
            {
                Ui.ApplyAppearanceTheme(theme);
                foreach (var route in new[] { "Settings", "Appearance", "Chats", "DesktopPlugins", "ServiceExtensions", "ToolsAndMcp", "Folders", "Resources", "Retention", "Access", "Dashboard" })
                {
                    SelectTab(route);
                    var view = (FrameworkElement)Content;
                    view.Measure(new Size(1024, 768)); view.Arrange(new Rect(0, 0, 1024, 768));
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    view.Measure(new Size(1024, 768)); view.Arrange(new Rect(0, 0, 1024, 768)); view.UpdateLayout();
                    if (route == "DesktopPlugins" && desktopPluginList.Columns.Any(column => column.ActualWidth < 40))
                        throw new InvalidOperationException("Plugin table columns did not finish native sizing: " +
                            string.Join(", ", desktopPluginList.Columns.Select(column => column.Header + "=" + column.ActualWidth)));
                    var page = (FrameworkElement)((TabItem)((TabControl)((TabItem)Tabs.SelectedItem).Content).SelectedItem).Content;
                    if (page.ActualWidth < 640 || page.ActualHeight < 300)
                        throw new InvalidOperationException("The selected settings page did not obtain its detail viewport: " + route);
                    var image = new RenderTargetBitmap(1024, 768, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Ui.Paper, null, new Rect(0, 0, 1024, 768));
                    image.Render(background); image.Render(view);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    var path = Path.Combine(directory, "settings-" + route + "-" + theme + "-1024.png");
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) encoder.Save(output);
                    images.Add(path);
                }
            }
        }
        finally { Ui.ApplyAppearanceTheme(originalTheme); }
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks, connected = IsConnected,
            plugins = desktopPluginList.Items.Count, serviceExtensions = serviceExtensions.Items.Count, captures = images, windowShown = false,
            modelCallsExecuted = false, settingsWritten = false, ownerNavigationWritten = false }));
    }
}
