using Cerebral.Core;

namespace Cerebral.Tools;

/// <summary>
/// Owner-only locator for one previously dispatched PAO job. These values are invocation scoped
/// and never replace saved execution evidence or become arguments to the original tool.
/// </summary>
public sealed record JobReceiptLookupHint(string EffectId, string ProviderId,
    string ProviderFingerprint, string JobIdHint);

/// <summary>Optional host capability; the public IToolDispatcher contract remains unchanged.</summary>
public interface IJobReceiptLookup
{
    string? CurrentProviderFingerprint(string providerId);
    void RequireFileEditAdmission(ToolCall call, ToolExecutionContext context, JobReceiptLookupHint hint);
    Task<ToolResult> ReconcileMissingJobAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence previousEvidence, JobReceiptLookupHint hint, CancellationToken cancellationToken);
}
