namespace Cerebral.Core;

/// <summary>Protocol-1 declaration for an already installed MCP executable; it is never executable code.</summary>
public sealed class PluginManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public int Protocol { get; set; } = 1;
    public string Kind { get; set; } = "mcp";
    public string Description { get; set; } = "";
    public McpRegistration Server { get; set; } = new();
}

public sealed record PluginSummary(string Id, string Name, string Version, int Protocol, string Kind, string Description,
    bool Configured, bool Enabled, bool ConfigurationChanged, DateTimeOffset InstalledAt);
public sealed record PluginInspection(PluginManifest Manifest, McpRegistration? ConfiguredServer, string RequiredCapability,
    string ToolNamePrefix, bool ConfigurationChanged, IReadOnlyList<string> Notes);
public sealed record PluginRemoval(string Id, bool RegistrationRemoved);
