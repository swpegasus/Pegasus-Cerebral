using System.Text.Json;

namespace Cerebral.Core;

/// <summary>An exact proposed Cortex write. Approval applies only to these arguments and this project revision.</summary>
public sealed class KnowledgeProposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public int ProjectRevision { get; set; }
    public string ProviderFingerprint { get; set; } = "";
    public string QuestionId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public JsonElement Arguments { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
