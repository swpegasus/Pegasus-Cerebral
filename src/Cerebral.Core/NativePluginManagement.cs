namespace Cerebral.Core;

// Host-private bridge data, separate from the stable desktop-plugin SDK. Native code
// and paths remain in the signed-in Desktop catalog; the Worker retains only identity.
public sealed record NativePluginCatalogEntry(string Id, string Name, string Version,
    string? Generation, string? PackageFingerprint, bool Enabled, bool Loaded, string? Error);
public sealed record NativePluginCatalogSnapshot(string CatalogFingerprint,
    IReadOnlyList<NativePluginCatalogEntry> Plugins, int Omitted = 0);
public sealed record NativePluginSelection(string Id, bool ExpectedEnabled,
    string Generation, string PackageFingerprint);
public sealed record NativePluginManagementResult(NativePluginCatalogSnapshot Catalog,
    IReadOnlyList<string> ChangedPluginIds);

public sealed class NativePluginManagementRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public int ProfileRevision { get; set; }
    public string JobId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string? Query { get; set; }
    public string? CatalogFingerprint { get; set; }
    public string? CatalogRequestId { get; set; }
    public NativePluginSelection? Selection { get; set; }
    public bool? Enabled { get; set; }
    public string Status { get; set; } = "pending";
    public string? ClaimId { get; set; }
    public NativePluginManagementResult? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
}
public sealed record NativePluginManagementClaim(bool Claimed, NativePluginManagementRequest Request);
