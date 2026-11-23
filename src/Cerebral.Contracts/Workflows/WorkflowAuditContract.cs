using System.Text.Json;

namespace Cerebral.Contracts.Workflows;

/// <summary>Optional advisory feature. Implementations receive bounded data, never execution or host handles.</summary>
public sealed record WorkflowAuditDescriptor(string Id, string PackageId, string Version, long Generation,
    string Fingerprint, IReadOnlyList<string> Purposes);
public sealed record WorkflowAuditPolicy(string? AdapterId = null, bool Required = false, int TimeoutMilliseconds = 1500);
public sealed record WorkflowAuditRequest(string RequestId, string RunId, string ConversationId, long RunRevision,
    long ConversationRevision, long EngineEpoch, string Purpose, string InputSha256, string ProposalSha256,
    JsonElement Context, long CreatedMonotonicMilliseconds, long DeadlineMonotonicMilliseconds);
public sealed record WorkflowAuditResult(string RequestId, string AdapterId, long Generation, string Fingerprint,
    string InputSha256, string ProposalSha256, string Disposition, string Summary,
    bool AdvisoryOnly = true, bool DirectActionAllowed = false);
public interface IWorkflowAuditAdapter
{
    WorkflowAuditDescriptor Descriptor { get; }
    Task<WorkflowAuditResult> ReviewAsync(WorkflowAuditRequest request, CancellationToken cancellationToken);
}
public interface IWorkflowAuditModule
{
    IReadOnlyList<IWorkflowAuditAdapter> WorkflowAuditors { get; }
}
