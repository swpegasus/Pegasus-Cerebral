namespace Cerebral.Core;

public sealed class ProjectKnowledgeSettings
{
    public string ProjectId { get; set; } = "";
    public int Revision { get; set; }
    public string CortexServerId { get; set; } = "";
    public List<string> ReadNamespaces { get; set; } = [];
    public string? WriteNamespace { get; set; }
    public bool AutomaticRetrieval { get; set; } = true;
    public bool AutomaticContinuity { get; set; }
    public bool RequireKnowledgeApproval { get; set; } = true;
}
public sealed record CortexAccessScope(string ProjectId, string ServerId, IReadOnlyList<string> ReadNamespaces,
    string? WriteNamespace, bool RequireKnowledgeApproval, bool KnowledgeWriteApproved = false, string? ProviderFingerprint = null);

public sealed class CortexRetrievalStatus
{
    public string JobId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public int UserSequence { get; set; }
    public int SettingsRevision { get; set; }
    public string ScopeFingerprint { get; set; } = "";
    public string Context { get; set; } = "";
    public string Status { get; set; } = "not-requested";
    public string Message { get; set; } = "";
    public List<CortexReference> References { get; set; } = [];
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record CortexReference(string Namespace, string Id, string? Revision = null, bool Excerpt = false, int? SourceBytes = null);
