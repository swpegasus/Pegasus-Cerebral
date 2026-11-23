namespace Cerebral.Core;

public sealed class CortexConnectionReport
{
    public string ServerId { get; set; } = "";
    public string State { get; set; } = "not-configured";
    public string Message { get; set; } = "";
    public string? Executable { get; set; }
    public bool ViaUserConnector { get; set; }
    public string? ConnectorUrl { get; set; }
    public string? ConnectorState { get; set; }
    public McpServerIdentity? McpIdentity { get; set; }
    public CortexNativeIdentity? NativeIdentity { get; set; }
    public string Transport { get; set; } = "mcp-stdio";
    public string? Product { get; set; }
    public string? Version { get; set; }
    public string? ProviderState { get; set; }
    public string? StorageKind { get; set; }
    public bool? Network { get; set; }
    public bool? Service { get; set; }
    public List<string> AvailableTools { get; set; } = [];
    public List<string> MissingTools { get; set; } = [];
    public DateTimeOffset InspectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class CortexConnectionContract
{
    public const string Product = "Pegasus Cortex";
    public static IReadOnlyList<string> CoreTools { get; } = Array.AsReadOnly(new[]
    {
        "cortex_start", "cortex_search", "cortex_read", "cortex_status",
        "cortex_namespaces", "cortex_task", "cortex_knowledge", "cortex_finish"
    });
}

public sealed record McpServerIdentity(string ProtocolVersion, string Name, string Version);
