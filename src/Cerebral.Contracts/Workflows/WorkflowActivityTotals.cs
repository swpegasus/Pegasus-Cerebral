using System.Text.Json.Serialization;
namespace Cerebral.Contracts.Workflows;

/// <summary>One run's host-owned receipt ledger, independent of a displayed event page.
/// Unknown outcomes remain unconfirmed; these counters never authorize or replay work.</summary>
public sealed record WorkflowActivityTotals(string ConversationId, string RunId, long RunRevision,
    long AsOfSequence, int Calls, int Completed, int Failed, int Cancelled, int Unconfirmed,
    bool CoverageComplete, string? DiagnosticCode = null)
{
    public string Schema { get; init; } = "cerebral.workflow.activity-totals/1";
    public string Source { get; init; } = "engine-effect-receipts-and-current-effect";
    public int Blocked { get; init; }
    public bool IsValidFor(string conversationId, string runId, long revision, long minimumSequence = 0) =>
        Schema == "cerebral.workflow.activity-totals/1" &&
        Source == "engine-effect-receipts-and-current-effect" &&
        ConversationId == conversationId && RunId == runId && RunRevision == revision && revision > 0 &&
        AsOfSequence >= minimumSequence && minimumSequence >= 0 &&
        Calls is >= 0 and <= WorkflowContract.MaximumSteps &&
        Completed is >= 0 and <= WorkflowContract.MaximumSteps && Failed is >= 0 and <= WorkflowContract.MaximumSteps &&
        Cancelled is >= 0 and <= WorkflowContract.MaximumSteps && Unconfirmed is >= 0 and <= WorkflowContract.MaximumSteps &&
        Blocked is >= 0 and <= WorkflowContract.MaximumSteps &&
        (long)Completed + Failed + Cancelled + Unconfirmed + Blocked == Calls &&
        (!CoverageComplete || DiagnosticCode is null) && (DiagnosticCode is null or
            "receipt-limit" or "invalid-receipt-metadata" or "missing-terminal-receipt" or "invalid-current-effect");
}
