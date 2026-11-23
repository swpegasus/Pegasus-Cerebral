namespace Pegasus.SharedMcp;

/// <summary>Fixed local child selection; arguments are never evaluated by a shell.</summary>
public sealed record StdioBackendOptions(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    public bool CortexMultiplex { get; init; }
    public int MaximumPendingRequests { get; init; } = 32;
    public int MaximumRequestBytes { get; init; } = 256 * 1024;
    public int MaximumResponseBytes { get; init; } = 2_200_000;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public string ClientName { get; init; } = "pegasus-shared-mcp-development";
}
