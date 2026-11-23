using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

internal enum MainChatTimingEnd
{
    Completed, Failed, Cancelled, CancelRequested, Rejected, Uncertain, NoSubmission, SendFailed,
    SelectionChanged, ConnectionChanged, Disposed, Timeout
}

internal sealed record MainChatTimingResult(
    string Schema, string CaseId, string? ConversationId, string? RequestId, string? AgentId,
    string End, bool ClosedBeforeAck, bool ClosedBeforePreviewRenderCallback,
    double? SendToSubmitCallMs, double? SendToSubmitAckMs,
    string? MissingSubmitCallReason, string? MissingSubmitAckReason,
    double? SendToFirstPreviewEnvelopeMs, string? FirstPreviewEnvelopeEffectId,
    string? MissingPreviewEnvelopeReason,
    double? SendToFirstAdmittedPreviewMs, string? FirstAdmittedPreviewEffectId,
    string? MissingAdmittedPreviewReason,
    double? SendToFirstPublicPreviewContentMutationMs, string? FirstPublicPreviewContentEffectId,
    string? MissingPublicPreviewContentReason,
    double? SendToPreviewRenderSchedulingCallbackMs,
    string? MissingPreviewRenderCallbackReason,
    double? SendToFinalCardMutationMs, string? FinalCardEventId, string? MissingFinalCardReason);

/// <summary>One Main-process monotonic origin; stages may arrive in any order.</summary>
internal sealed class MainChatInterfaceTimingProbe : IDisposable
{
    private readonly object sync = new();
    private readonly Func<long> clock;
    private readonly Action<MainChatTimingResult> write;
    private readonly Action detach;
    private readonly Timer deadline;
    private readonly long origin;
    private readonly string caseId;
    private string? conversationId, requestId, agentId;
    private long? submitCall, submitAck, previewEnvelope, admittedPreview, previewContentMutation, renderCallback, finalCard;
    private string? previewEnvelopeEffectId, admittedPreviewEffectId, previewContentEffectId, finalCardEventId;
    private bool closed, nonTranscriptResult;

    internal MainChatInterfaceTimingProbe(string caseId, Func<long> clock, Action<MainChatTimingResult> write,
        Action detach, TimeSpan? limit = null)
    {
        if (caseId.Length != 32 || caseId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lower-case 32-digit case ID is required.", nameof(caseId));
        this.caseId = caseId;
        this.clock = clock;
        this.write = write;
        this.detach = detach;
        origin = clock();
        deadline = new Timer(_ => Finish(MainChatTimingEnd.Timeout), null, limit ?? TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
    }

    internal bool IsClosed { get { lock (sync) return closed; } }
    internal bool IsBound { get { lock (sync) return requestId is not null; } }
    internal string? RequestId { get { lock (sync) return requestId; } }

    internal void Bind(string conversation, string request, string agent)
    {
        lock (sync)
        {
            if (closed || requestId is not null || !Bounded(conversation) || !Bounded(request) || !Bounded(agent)) return;
            conversationId = conversation; requestId = request; agentId = agent;
        }
    }

    internal void MarkSubmitCall(string request)
    {
        var stamp = clock();
        lock (sync) if (!closed && requestId == request) submitCall ??= stamp;
    }

    internal void MarkConfirmedAck(string request)
    {
        var stamp = clock();
        lock (sync) if (!closed && requestId == request) submitAck ??= stamp;
    }

    internal void ObservePreviewEnvelope(ServiceEvent item)
    {
        if (item.Type != "workflow.generation.preview" || item.Data.ValueKind != JsonValueKind.Object) return;
        // Read identity strings only. Never materialize or retain the event body or its text field.
        if (!Id(item.Data, "conversationId", out var conversation) || !Id(item.Data, "requestId", out var request) ||
            !Id(item.Data, "runId", out var run) || !Id(item.Data, "effectId", out var effect) ||
            !Id(item.Data, "agentId", out var agent)) return;
        var stamp = clock();
        lock (sync)
        {
            if (closed || previewEnvelope is not null || item.JobId != request ||
                conversationId != conversation || requestId != request || run != request || agentId != agent) return;
            previewEnvelope = stamp; previewEnvelopeEffectId = effect;
        }
    }

    /// <returns>True only for the first matching public preview content mutation; caller may arm one render callback.</returns>
    internal bool ObservePresentation(WorkflowPresentationMark mark)
    {
        lock (sync)
        {
            if (closed || conversationId != mark.ConversationId || requestId != mark.RequestId ||
                mark.RunId != requestId || agentId != mark.AgentId || mark.MonotonicTimestamp < origin) return false;
            switch (mark.Kind)
            {
                case WorkflowPresentationMarkKind.PreviewAdmitted when admittedPreview is null && Bounded(mark.EffectId):
                    admittedPreview = mark.MonotonicTimestamp; admittedPreviewEffectId = mark.EffectId; break;
                case WorkflowPresentationMarkKind.PublicPreviewContentMutated when previewContentMutation is null && Bounded(mark.EffectId):
                    previewContentMutation = mark.MonotonicTimestamp; previewContentEffectId = mark.EffectId; return true;
                case WorkflowPresentationMarkKind.FinalCardMutated when finalCard is null && Bounded(mark.EventId):
                    finalCard = mark.MonotonicTimestamp; finalCardEventId = mark.EventId; break;
            }
            return false;
        }
    }

    internal void MarkRenderSchedulingCallback()
    {
        var stamp = clock();
        lock (sync) if (!closed && previewContentMutation is not null && renderCallback is null) renderCallback = stamp;
    }

    internal void MarkNonTranscriptResult(string conversation, string request)
    {
        lock (sync) if (!closed && conversationId == conversation && requestId == request)
            nonTranscriptResult = true;
    }

    internal void Finish(MainChatTimingEnd end)
    {
        MainChatTimingResult result;
        lock (sync)
        {
            if (closed) return;
            closed = true;
            result = new("cerebral.main-chat-interface-timing/1", caseId, conversationId, requestId, agentId,
                end.ToString(), requestId is not null && submitAck is null,
                previewContentMutation is not null && renderCallback is null,
                Offset(submitCall), Offset(submitAck),
                submitCall is null ? requestId is null ? "not_bound" : "no_submit_call" : null,
                submitAck is null ? requestId is null ? "not_bound" : "closed_before_ack" : null,
                Offset(previewEnvelope), previewEnvelopeEffectId,
                previewEnvelope is null ? "no_correlated_preview_envelope" : null,
                Offset(admittedPreview), admittedPreviewEffectId,
                admittedPreview is null ? "no_admitted_public_preview" : null,
                Offset(previewContentMutation), previewContentEffectId,
                previewContentMutation is null ? "no_public_preview_content_mutation" : null,
                Offset(renderCallback), renderCallback is null ? previewContentMutation is null
                    ? "no_public_preview_content_mutation" : "closed_before_preview_render_callback" : null,
                Offset(finalCard), finalCardEventId, finalCard is null ? nonTranscriptResult
                    ? "canonical_result_not_in_transcript" : "no_transcript_final_card" : null);
        }
        deadline.Dispose();
        try { detach(); } catch { /* Diagnostic teardown cannot affect chat. */ }
        try { write(result); } catch { /* Diagnostic output cannot affect chat. */ }
    }

    public void Dispose() => Finish(MainChatTimingEnd.Disposed);

    private double? Offset(long? stamp) => stamp is null ? null :
        Math.Round(Stopwatch.GetElapsedTime(origin, stamp.Value).TotalMilliseconds, 3);
    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => !char.IsControl(c));
    private static bool Id(JsonElement data, string name, out string value)
    {
        value = "";
        if (!data.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var candidate = property.GetString();
        if (!Bounded(candidate)) return false;
        value = candidate!; return true;
    }
}

/// <summary>Posts terminal closure after the current synchronous Workspace presentation pass.</summary>
internal static class MainChatTimingTerminalDeferral
{
    internal static void Queue(Dispatcher dispatcher, Func<bool> sameOwner,
        Func<WorkflowConversationSnapshot?> snapshot, string conversationId, string requestId,
        Action<MainChatTimingEnd> finish, Action unavailable)
    {
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!sameOwner()) return;
                    var current = snapshot();
                    if (current?.ConversationId != conversationId || current.RunId != requestId ||
                        current.Run is not { } run || run.Id != requestId ||
                        (!run.IsTerminal && !run.CancelRequested)) return;
                    finish(run.CancelRequested && !run.IsTerminal
                        ? MainChatTimingEnd.CancelRequested : run.Status switch
                        {
                            WorkflowStatus.Completed => MainChatTimingEnd.Completed,
                            WorkflowStatus.Cancelled => MainChatTimingEnd.Cancelled,
                            _ => MainChatTimingEnd.Failed
                        });
                }
                catch { try { unavailable(); } catch { /* Diagnostic-only. */ } }
            }), DispatcherPriority.Background);
        }
        catch { try { unavailable(); } catch { /* Diagnostic-only. */ } }
    }
}
