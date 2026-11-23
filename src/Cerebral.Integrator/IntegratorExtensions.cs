using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly DataGrid plugins = Ui.Grid(("MCP declaration", "Name", 2), ("ID", "Id", 2), ("Version", "Version", 1),
        ("Configured", "Configured", 1), ("Enabled", "Enabled", 1), ("Config changed", "ConfigurationChanged", 1));
    private UIElement McpDeclarationsPage() => Ui.Page("MCP provider declarations",
        "Versioned MCP manifests describe an existing provider. Install registers it disabled; enable it in Tools & MCP and grant access to selected agents.",
        plugins, Ui.Button("Install manifest…", () => Run(InstallPlugin), true), Ui.Button("Validate manifest…", () => Run(ValidatePlugin)),
        Ui.Button("Inspect", () => Run(InspectPlugin)), Ui.Button("Remove", () => Run(RemovePlugin)), Ui.Button("Refresh", () => Run(RefreshPlugins)));
    private async Task RefreshPlugins() => plugins.ItemsSource = await Get<List<PluginSummary>>("plugins.list");

    private sealed record ServiceExtensionInventory(string Id, string Name, string Version, bool Loaded,
        IReadOnlyList<string> Tools, IReadOnlyList<string> Commands, string? Error)
    {
        public int ToolCount => Tools.Count;
        public int CommandCount => Commands.Count;
    }
    private readonly DataGrid serviceExtensions = Ui.Grid(("Service extension", "Name", 2), ("ID", "Id", 2),
        ("Version", "Version", 1), ("Loaded", "Loaded", 1), ("Tools", "ToolCount", .7),
        ("Commands", "CommandCount", .7), ("Problem", "Error", 3));
    private readonly TextBlock serviceExtensionSummary = Ui.Text("Connect to the Worker to inspect its loaded service extensions.", 12, Ui.Muted);
    private Button? serviceExtensionInspect;

    private UIElement ServiceExtensionsPage()
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(serviceExtensions, "NativeServiceExtensions");
        var body = new DockPanel();
        serviceExtensionSummary.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(serviceExtensionSummary, Dock.Top); body.Children.Add(serviceExtensionSummary);
        body.Children.Add(serviceExtensions);
        serviceExtensionInspect = Ui.Button("Inspect", () => Run(InspectServiceExtension));
        serviceExtensions.SelectionChanged += (_, _) => RefreshServiceExtensionSelectionAction();
        ServiceStatusChanged += (_, _) => RefreshServiceExtensionSelectionAction();
        RefreshServiceExtensionSelectionAction();
        return Ui.Page("Service extensions",
            "Native packages add Worker tools, commands and workflows. This inventory reports the active Worker. Install and update service packages through Cerebral's package workflow; the command console exposes its existing service-packages operations.",
            body, Ui.Button("Refresh", () => Run(RefreshServiceExtensions)),
            serviceExtensionInspect,
            Ui.Button("Package commands", () => { SelectTab("Diagnostics"); }));
    }

    private void RefreshServiceExtensionSelectionAction()
    {
        if (serviceExtensionInspect is not null)
        {
            var selected = IsConnected && serviceExtensions.SelectedItem is ServiceExtensionInventory;
            serviceExtensionInspect.IsEnabled = selected;
            serviceExtensionInspect.ToolTip = selected ? null : "Select a service extension to inspect it.";
        }
    }

    private static IReadOnlyList<ServiceExtensionInventory> ReadServiceExtensionInventory(JsonElement diagnostics)
    {
        if (!diagnostics.TryGetProperty("servicePlugins", out var inventory) || inventory.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Worker did not return its native service-extension inventory.");
        if (inventory.GetArrayLength() > 128) throw new InvalidDataException("The service-extension inventory exceeds the display bound.");
        var entries = CerebralJson.Read<List<ServiceExtensionInventory>>(inventory);
        if (entries.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 ||
            string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256 || item.Version is null || item.Version.Length > 128 ||
            item.Tools is null || item.Commands is null || item.Tools.Count > 512 || item.Commands.Count > 512 ||
            item.Tools.Concat(item.Commands).Any(name => name is null || name.Length > 256) ||
            item.Error?.Length > 4096) || entries.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new InvalidDataException("The Worker returned invalid or excessive service-extension metadata.");
        return entries;
    }

    private async Task RefreshServiceExtensions()
    {
        var selected = (serviceExtensions.SelectedItem as ServiceExtensionInventory)?.Id;
        try
        {
            var entries = ReadServiceExtensionInventory(await Get<JsonElement>("diagnostics"));
            serviceExtensions.ItemsSource = entries;
            serviceExtensions.SelectedItem = entries.FirstOrDefault(item => item.Id == selected);
            RefreshServiceExtensionSelectionAction();
            serviceExtensionSummary.Text = entries.Count == 0 ? "The active Worker reports no native service extensions." :
                $"{entries.Count(item => item.Loaded)} loaded · {entries.Count} reported · {entries.Count(item => item.Error is not null)} need attention";
        }
        catch
        {
            serviceExtensions.ItemsSource = null;
            RefreshServiceExtensionSelectionAction();
            serviceExtensionSummary.Text = "Service-extension inventory is unavailable. Refresh after resolving the Worker connection.";
            throw;
        }
    }

    private Task InspectServiceExtension()
    {
        var item = Selected<ServiceExtensionInventory>(serviceExtensions);
        Ui.ShowText(this, "Service extension · " + item.Name, Ui.Serialize(item));
        return Task.CompletedTask;
    }

    private PluginManifest? ChoosePluginManifest()
    {
        var picker = new OpenFileDialog { Title = "Choose MCP provider manifest", Filter = "JSON manifest (*.json)|*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return null;
        using var input = File.OpenRead(picker.FileName);
        if (input.Length > 65_536) throw new ArgumentException("Extension manifests must be at most 64 KiB.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return JsonSerializer.Deserialize<PluginManifest>(bytes, CerebralJson.Options) ?? throw new ArgumentException("The manifest is empty.");
    }
    private async Task ValidatePlugin()
    {
        var manifest = ChoosePluginManifest(); if (manifest is null) return;
        var result = await Get<PluginInspection>("plugins.validate", new { manifest });
        Ui.ShowText(this, "MCP declaration validation", Ui.Serialize(result));
    }
    private async Task InstallPlugin()
    {
        var manifest = ChoosePluginManifest(); if (manifest is null) return;
        var inspection = await Get<PluginInspection>("plugins.validate", new { manifest });
        var review = new FieldsDialog(this, "Install MCP provider declaration",
            "Review the provider executable and arguments. This saves a disabled MCP registration. It does not download, launch, enable, or grant agent access.", "Install disabled")
            .Add("manifest", "Manifest", Ui.Serialize(inspection.Manifest), true);
        if (review.ShowDialog() != true) return;
        manifest = Ui.Parse<PluginManifest>(review.Raw("manifest"));
        await Command("plugins.install", new { manifest });
        await RefreshSettings(); await RefreshPlugins(); SetStatus("MCP provider registered disabled. Enable it in Tools & MCP and assign agent grants when ready.");
    }
    private async Task InspectPlugin()
    {
        var item = Selected<PluginSummary>(plugins);
        Ui.ShowText(this, $"MCP declaration · {item.Name}", Ui.Serialize(await Get<PluginInspection>("plugins.inspect", new { id = item.Id })));
    }
    private async Task RemovePlugin()
    {
        var item = Selected<PluginSummary>(plugins);
        if (!Confirm($"Remove MCP declaration '{item.Name}' and its unchanged MCP registration? Disable it and remove its agent grants first. Provider files are retained.")) return;
        await Command("plugins.remove", new { id = item.Id });
        await RefreshSettings(); await RefreshPlugins(); SetStatus("MCP declaration removed.");
    }
    private async Task ShowDiagnosticTools()
    {
        var available = await Get<List<AgentProfile>>("agents.list");
        if (available.Count == 0) throw new InvalidOperationException("Configure an agent before inspecting its granted tools.");
        var form = new FieldsDialog(this, "Tool metadata", "Live tool names, capability requirements, descriptions and argument schemas for the selected profile.", "Inspect") { Height = 300 };
        form.Choice("profile", "Agent profile", available.Select(x => x.Id), available.FirstOrDefault(x => x.Role == "lead")?.Id ?? available[0].Id);
        if (form.ShowDialog() != true) return;
        diagnostics.Text = (await Command("tools.list", new { profileId = form.Selection("profile") })).Data.ToString();
    }
}
