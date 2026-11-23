using System.Diagnostics;

namespace Cerebral.Desktop;

/// <summary>Scalar UI-tree observation for an optional, request-scoped Main diagnostic.</summary>
public enum WorkflowPresentationMarkKind { PreviewAdmitted, PublicPreviewContentMutated, FinalCardMutated }

/// <summary>No prompt, response, preview body, or private reasoning crosses this observer seam.</summary>
public sealed record WorkflowPresentationMark(
    WorkflowPresentationMarkKind Kind,
    string ConversationId,
    string RequestId,
    string RunId,
    string? EffectId,
    string AgentId,
    string? EventId,
    long MonotonicTimestamp);

public sealed partial class WorkflowWorkspace
{
    /// <summary>Optional scalar observer. A failing observer never affects chat rendering.</summary>
    public event Action<WorkflowPresentationMark>? PresentationMarked;

    private void PublishPresentationMark(WorkflowPresentationMarkKind kind, string conversationId,
        string requestId, string runId, string? effectId, string agentId, string? eventId = null)
    {
        var observers = PresentationMarked;
        if (observers is null) return;
        var mark = new WorkflowPresentationMark(kind, conversationId, requestId, runId,
            effectId, agentId, eventId, Stopwatch.GetTimestamp());
        foreach (Action<WorkflowPresentationMark> observer in observers.GetInvocationList())
        {
            try { observer(mark); }
            catch { /* An opt-in diagnostic cannot change normal presentation or submission. */ }
        }
    }
}
