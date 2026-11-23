using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Desktop;

/// <summary>The currently mounted public draft only. No prompts, action envelopes or private reasoning.</summary>
public sealed record WorkflowGenerationPreviewDisplaySnapshot(bool Visible)
{
    public string? Kind { get; init; }
    public string? Operation { get; init; }
    public string PublicText { get; init; } = "";
    public int PublicTextBytes { get; init; }
    public bool Truncated { get; init; }
    public string? ConversationId { get; init; }
    public string? RequestId { get; init; }
    public string? RunId { get; init; }
    public string? EffectId { get; init; }
    public string? AgentId { get; init; }
    public long? EventSequence { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public string? MessageId { get; init; }
    public string? State { get; init; }
}

public sealed partial class WorkflowWorkspace
{
    private const string GenerationPreviewEventType = "workflow.generation.preview";
    private const int MaximumGenerationPreviewCharacters = 32 * 1024;
    private const int MaximumGenerationPreviewBytes = 128 * 1024;
    private static readonly UTF8Encoding PreviewUtf8 = new(false, true);
    private static readonly JsonSerializerOptions PreviewJson = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private static readonly HashSet<string> PreviewFields = new(StringComparer.Ordinal)
        { "schema", "conversationId", "requestId", "runId", "effectId", "agentId", "phase", "kind",
          "text", "operation", "truncated", "elapsedMilliseconds", "finish" };
    private Border generationPreviewHost = null!;
    private TextBlock generationPreviewCaption = null!;
    private RichTextBox generationPreviewText = null!;
    private Run generationPreviewRun = null!;
    private string generationPreviewPublicText = "";
    private Action<string> generationPreviewCopyText = Clipboard.SetText;
    private GenerationPreviewValue? generationPreview;
    private long generationPreviewSequence;
    private long generationPreviewFeedGeneration;
    private long generationPreviewRenderRevision;
    private long generationPreviewInputRevision;
    private bool generationPreviewClosed;
    private long generationPreviewSnapshotRevision;
    private string? generationPreviewState;
    private readonly HashSet<string> generationPreviewPriorMessages = new(StringComparer.Ordinal);

    public WorkflowGenerationPreviewDisplaySnapshot GenerationPreviewSnapshot =>
        generationPreviewHost.Visibility == Visibility.Visible && generationPreview is { Phase: "draft" } value
        ? new(true)
        {
            Kind = value.Kind, Operation = value.Operation, PublicText = generationPreviewPublicText,
            PublicTextBytes = PreviewUtf8.GetByteCount(generationPreviewPublicText), Truncated = value.Truncated,
            ConversationId = value.ConversationId, RequestId = value.RequestId, RunId = value.RunId,
            EffectId = value.EffectId, AgentId = value.AgentId, EventSequence = generationPreviewSequence,
            ElapsedMilliseconds = value.ElapsedMilliseconds,
            MessageId = "answer:" + value.RunId + ":" + value.EffectId, State = generationPreviewState
        } : new(false);

    /// <summary>Use only when replacing the authenticated event feed; a new Worker can restart its sequence.</summary>
    public void ResetGenerationPreviewFeed()
    {
        Dispatcher.VerifyAccess();
        StopActivityAnimations();
        generationPreviewFeedGeneration++;
        ResetGenerationMetricsFeed();
        generationPreviewSequence = 0;
        ClearGenerationPreview();
    }

    private FrameworkElement CreateGenerationPreview()
    {
        generationPreviewCaption = Ui.Text("Response preview · provisional", 12, Ui.Muted);
        generationPreviewRun = new Run();
        var paragraph = SelectableMarkdown.NormalBodyParagraph();
        paragraph.Inlines.Add(generationPreviewRun);
        generationPreviewText = SelectableMarkdown.View(new FlowDocument(paragraph));
        generationPreviewText.IsReadOnly = true; generationPreviewText.Background = Ui.Surface;
        generationPreviewText.Foreground = Ui.Ink; generationPreviewText.Padding = new Thickness(0);
        generationPreviewText.Margin = new Thickness(0, 8, 0, 0);
        generationPreviewText.MaxHeight = double.PositiveInfinity;
        generationPreviewText.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        generationPreviewText.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        // The provisional answer remains plain public text, including when the document's own
        // selection reaches its terminal paragraph marker. Do not put that marker on the clipboard.
        generationPreviewText.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, args) =>
            {
                var selected = GenerationPreviewSelectedPublicText();
                if (selected.Length > 0)
                {
                    try { generationPreviewCopyText(selected); }
                    catch (Exception error) when (error is ExternalException or InvalidOperationException)
                    { report("The selected response could not be copied.", true); }
                }
                args.Handled = true;
            },
            (_, args) => { args.CanExecute = GenerationPreviewSelectedPublicText().Length > 0; args.Handled = true; }));
        generationPreviewText.PreviewMouseWheel += (_, _) => generationPreviewInputRevision++;
        generationPreviewText.PreviewMouseDown += (_, _) => generationPreviewInputRevision++;
        generationPreviewText.PreviewKeyDown += (_, _) => generationPreviewInputRevision++;
        AutomationProperties.SetAutomationId(generationPreviewText, "WorkflowGenerationPreviewText");
        var content = new StackPanel();
        content.Children.Add(generationPreviewCaption); content.Children.Add(generationPreviewText);
        generationPreviewHost = new Border
        {
            Background = Ui.Surface, BorderBrush = Ui.Accent, BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12), Margin = new Thickness(0, 10, 0, 0), Child = content,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(generationPreviewHost, "WorkflowGenerationPreview");
        return generationPreviewHost;
    }

    internal (int Start, int Length) GenerationPreviewSelectionRange()
    {
        Dispatcher.VerifyAccess();
        var origin = generationPreviewRun.ContentStart;
        var start = Math.Clamp(origin.GetOffsetToPosition(generationPreviewText.Selection.Start), 0, generationPreviewPublicText.Length);
        var end = Math.Clamp(origin.GetOffsetToPosition(generationPreviewText.Selection.End), start, generationPreviewPublicText.Length);
        start = PreviewUtf16Boundary(generationPreviewPublicText, start, end: false);
        end = PreviewUtf16Boundary(generationPreviewPublicText, end, end: true);
        return (start, end - start);
    }

    internal string GenerationPreviewSelectedPublicText()
    {
        var (start, length) = GenerationPreviewSelectionRange();
        return generationPreviewPublicText.Substring(start, length);
    }

    internal void SelectGenerationPreviewText(int start, int length)
    {
        Dispatcher.VerifyAccess();
        start = PreviewUtf16Boundary(generationPreviewPublicText, Math.Clamp(start, 0, generationPreviewPublicText.Length), end: false);
        var requestedEnd = Math.Clamp((long)start + Math.Max(0, length), start, generationPreviewPublicText.Length);
        var end = PreviewUtf16Boundary(generationPreviewPublicText, (int)requestedEnd, end: true);
        var origin = generationPreviewRun.ContentStart;
        var first = origin.GetPositionAtOffset(start, LogicalDirection.Forward) ?? generationPreviewRun.ContentEnd;
        var last = origin.GetPositionAtOffset(end, LogicalDirection.Forward) ?? generationPreviewRun.ContentEnd;
        generationPreviewText.Selection.Select(first, last);
    }

    private static int PreviewUtf16Boundary(string text, int index, bool end) =>
        index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index])
            ? end ? index + 1 : index - 1 : index;

    private void NotifyGenerationPreview(ServiceEvent item)
    {
        if (item.Type != GenerationPreviewEventType || disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            var selection = generation; var connection = connectionEpoch;
            var feed = Volatile.Read(ref generationPreviewFeedGeneration);
            PassiveDesktopInteraction.Defer(Dispatcher, "response preview", () =>
            {
                if (selection == generation && connection == connectionEpoch && feed == generationPreviewFeedGeneration) NotifyGenerationPreview(item);
            }, message => report(message, true));
            return;
        }
        if (!IsLoaded || !TryReadGenerationPreview(item, out var value) || !CurrentGenerationPreview(value) ||
            value.Phase == "draft" && !CanReceiveGenerationDraft()) return;
        ReconcileGenerationPreview();
        if (item.Sequence <= generationPreviewSequence) return;
        if (value.Phase != "draft" && (generationPreview is null || !SamePreviewIdentity(generationPreview, value))) return;
        if (generationPreview is { } previous)
        {
            if (!SamePreviewIdentity(previous, value))
            {
                var run = conversation!.Snapshot.Run!;
                if (value.AgentId != previous.AgentId || value.EffectId == previous.EffectId ||
                    !generationPreviewClosed && (run.EffectId != value.EffectId ||
                        run.EffectState != Cerebral.Contracts.Workflows.WorkflowEffectState.Dispatched)) return;
                generationPreviewClosed = false;
                generationPreviewSnapshotRevision = run.Revision;
            }
            else if (value.ElapsedMilliseconds < previous.ElapsedMilliseconds || generationPreviewClosed && value.Phase == "draft") return;
            if (SamePreviewIdentity(previous, value) && value.Phase == "draft" && (value.Kind != previous.Kind ||
                value.Kind == "final" && !value.Text!.StartsWith(previous.Text!, StringComparison.Ordinal) ||
                value.Kind == "tool" && value.Operation != previous.Operation)) return;
        }
        if (generationPreview is null || !SamePreviewIdentity(generationPreview, value))
        {
            generationPreviewSnapshotRevision = conversation!.Snapshot.Run!.Revision;
            generationPreviewPriorMessages.Clear();
            foreach (var message in conversation.Snapshot.Events.Where(item => item.Type == "workflow.result" || IsTranscriptCard(item)))
                generationPreviewPriorMessages.Add(message.Id);
        }
        generationPreviewSequence = item.Sequence;
        if (value.Phase == "draft")
            PublishPresentationMark(WorkflowPresentationMarkKind.PreviewAdmitted, value.ConversationId,
                value.RequestId, value.RunId, value.EffectId, value.AgentId);
        if (value.Phase != "draft")
        {
            if (generationPreview is not { Kind: "final" } settledAnswer || !SamePreviewIdentity(settledAnswer, value) || value.Phase == "unavailable")
            { ClearGenerationPreview(); return; }
            // A provider settlement is not adoption into the durable conversation. Keep the one
            // correlated public answer visible until its canonical message arrives, without replay.
            generationPreviewClosed = true;
            var currentRun = conversation!.Snapshot.Run!;
            generationPreviewState = currentRun.Status == WorkflowStatus.Cancelled ? "Cancelled · partial response" :
                currentRun.Status == WorkflowStatus.Failed ? "Failed · partial response" :
                currentRun.EffectState == WorkflowEffectState.Unknown ? "Outcome unconfirmed · partial response" : value.Finish switch
                { "Cancelled" => "Cancellation result pending · partial response", "Unknown" => "Outcome unconfirmed · partial response",
                    "Length" => "Response limit reached · awaiting saved response", _ => "Awaiting saved response" };
            RenderGenerationPreview(settledAnswer);
            return;
        }
        generationPreview = value;
        generationPreviewState = "Responding";
        RenderGenerationPreview(value);
    }

    private bool CurrentGenerationPreview(GenerationPreviewValue value) =>
        conversation?.Snapshot is { MutationUncertain: false, Run: { } run } snapshot &&
        snapshot.ConversationId == value.ConversationId && snapshot.RunId == value.RunId &&
        run.Id == value.RequestId && run.Id == value.RunId && run.ConversationId == value.ConversationId &&
        run.ProfileId == value.AgentId;

    private bool CanReceiveGenerationDraft() => conversation?.Snapshot.Run is { } run &&
        run.Status is WorkflowStatus.Running or WorkflowStatus.Queued && !run.CancelRequested && run.EffectState != WorkflowEffectState.Unknown;

    private static bool SamePreviewIdentity(GenerationPreviewValue left, GenerationPreviewValue right) =>
        left.ConversationId == right.ConversationId && left.RequestId == right.RequestId &&
        left.RunId == right.RunId && left.EffectId == right.EffectId && left.AgentId == right.AgentId;

    private void ReconcileGenerationPreview()
    {
        if (generationPreview is not { } value) return;
        var run = conversation?.Snapshot.Run;
        if (!CurrentGenerationPreview(value)) { ClearGenerationPreview(); return; }
        var adopted = conversation!.Snapshot.Events.Concat(retainedTranscriptEvents).Any(item =>
            !generationPreviewPriorMessages.Contains(item.Id) &&
            (item.Type == "workflow.result" || item.Type == CerebralEventTypes.Message && IsTranscriptCard(item)) &&
            item.Actor.Kind == EventActorKind.Agent &&
            item.TurnId == value.RunId && item.ConversationId == value.ConversationId &&
            (item.Actor.Id == value.AgentId || item.Provenance.AgentId == value.AgentId));
        if (adopted) { ClearGenerationPreview(); return; }
        if (value.Kind == "tool" && !CanReceiveGenerationDraft()) { ClearGenerationPreview(); return; }
        if (run!.Status == WorkflowStatus.Cancelled || run.CancelRequested || run.Status == WorkflowStatus.Failed || run.EffectState == WorkflowEffectState.Unknown)
        {
            generationPreviewClosed = true;
            generationPreviewState = run.Status == WorkflowStatus.Cancelled ? "Cancelled · partial response" :
                run.Status == WorkflowStatus.Failed ? "Failed · partial response" : run.EffectState == WorkflowEffectState.Unknown ?
                "Outcome unconfirmed · partial response" : "Cancellation requested · partial response";
            RenderGenerationPreview(value);
        }
        else if (run.IsTerminal)
        { generationPreviewClosed = true; generationPreviewState = "Awaiting saved response"; RenderGenerationPreview(value); }
        else if (run.Revision > generationPreviewSnapshotRevision && run.EffectId != value.EffectId)
            ClearGenerationPreview();
    }

    private void ClearGenerationPreview()
    {
        generationPreview = null; generationPreviewClosed = false; generationPreviewSnapshotRevision = 0;
        generationPreviewState = null; generationPreviewPriorMessages.Clear();
        ClearGenerationMetrics();
        // The event sequence is a text-free feed fence. Keep it until connection reset so a queued
        // older preview cannot reappear after unload, settlement or navigation.
        RenderGenerationPreview(null);
    }

    private void RenderGenerationPreview(GenerationPreviewValue? value)
    {
        var text = value?.Text ?? "";
        var caption = value?.Kind == "tool" ? "Preparing operation · " + value.Operation + " · provisional" :
            "Cerebral · " + (generationPreviewState ?? "Responding") + (value?.Truncated == true ? " · display limit reached" : "");
        var visible = value is null ? Visibility.Collapsed : Visibility.Visible;
        var previewContentMutation = value is { Phase: "draft", Kind: "final" } && text.Length > 0 &&
            (generationPreviewPublicText != text || generationPreviewText.Visibility != Visibility.Visible ||
             generationPreviewHost.Visibility != Visibility.Visible);
        if (generationPreviewHost.Visibility == visible && generationPreviewPublicText == text &&
            generationPreviewCaption.Text == caption) return;
        MountGenerationPreviewInActivity();
        var previewInput = generationPreviewInputRevision;
        var previewOffset = generationPreviewText.VerticalOffset;
        var previewFollow = generationPreviewText.ExtentHeight <= generationPreviewText.ViewportHeight + previewOffset + 16 && generationPreviewText.Selection.IsEmpty;
        var view = CurrentGenerationActivity();
        var activityOffset = view?.Scroller.VerticalOffset ?? 0;
        var activityFollow = view?.FollowEnd == true && generationPreviewText.Selection.IsEmpty;
        var activityInput = view?.InputRevision;
        var offset = transcriptRestoring ? transcriptRestoreOffset : transcriptScroller.VerticalOffset;
        var follow = !readingHistory && (transcriptRestoring ? transcriptRestoreFollowEnd :
            transcriptScroller.ScrollableHeight <= offset + 32) && generationPreviewText.Selection.IsEmpty &&
            !HasFocusedConversationSelection();
        var (selectionStart, selectionLength) = GenerationPreviewSelectionRange();
        generationPreviewHost.Visibility = visible;
        generationPreviewHost.Tag = value is null ? null : "answer:" + value.RunId + ":" + value.EffectId;
        generationPreviewCaption.Text = caption;
        if (generationPreviewPublicText != text)
        {
            generationPreviewRun.Text = text;
            generationPreviewPublicText = text;
            SelectGenerationPreviewText(selectionStart, selectionLength);
        }
        generationPreviewText.Visibility = value?.Kind == "final" ? Visibility.Visible : Visibility.Collapsed;
        if (previewContentMutation && value is not null && generationPreviewHost.Visibility == Visibility.Visible &&
            generationPreviewText.Visibility == Visibility.Visible)
            PublishPresentationMark(WorkflowPresentationMarkKind.PublicPreviewContentMutated, value.ConversationId,
                value.RequestId, value.RunId, value.EffectId, value.AgentId);
        var revision = ++generationPreviewRenderRevision;
        var inputRevision = transcriptInputRevision;
        var feed = generationPreviewFeedGeneration;
        PassiveDesktopInteraction.Defer(Dispatcher, "response preview position", () =>
        {
            if (disposed || revision != generationPreviewRenderRevision || inputRevision != transcriptInputRevision || feed != generationPreviewFeedGeneration) return;
            transcriptApplyingRestore = true;
            try
            {
                generationPreviewText.UpdateLayout();
                if (previewInput == generationPreviewInputRevision)
                {
                    if (previewFollow) generationPreviewText.ScrollToEnd();
                    else generationPreviewText.ScrollToVerticalOffset(previewOffset);
                }
                if (view is not null && activityInput == view.InputRevision)
                {
                    view.Scroller.UpdateLayout();
                    if (activityFollow) view.Scroller.ScrollToEnd();
                    else view.Scroller.ScrollToVerticalOffset(activityOffset);
                }
                transcriptScroller.UpdateLayout();
                if (follow) transcriptScroller.ScrollToEnd();
                else transcriptScroller.ScrollToVerticalOffset(Math.Clamp(offset, 0, transcriptScroller.ScrollableHeight));
                transcriptScroller.UpdateLayout();
            }
            finally { transcriptApplyingRestore = false; }
        }, message => report(message, true));
    }

    private bool HasFocusedConversationSelection() => Keyboard.FocusedElement is DependencyObject element &&
        UiTree.IsWithin(element, conversationFlow) &&
        (element is TextBox { SelectionLength: > 0 } || element is RichTextBox rich && !rich.Selection.IsEmpty);

    private static bool TryReadGenerationPreview(ServiceEvent item, out GenerationPreviewValue value)
    {
        value = null!;
        if (item.Sequence <= 0 || item.Data.ValueKind != JsonValueKind.Object || item.Data.GetRawText().Length > 6 * MaximumGenerationPreviewCharacters + 4096) return false;
        try
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in item.Data.EnumerateObject())
                if (!PreviewFields.Contains(field.Name) || !fields.Add(field.Name)) return false;
            if (!fields.Contains("truncated") || !fields.Contains("elapsedMilliseconds")) return false;
            var parsed = item.Data.Deserialize<GenerationPreviewValue>(PreviewJson);
            if (parsed is null || parsed.Schema != "cerebral.generation-preview/1" ||
                !PreviewIdentifier(parsed.ConversationId) || !PreviewIdentifier(parsed.RequestId) ||
                !PreviewIdentifier(parsed.RunId) || !PreviewIdentifier(parsed.EffectId) ||
                !PreviewIdentifier(parsed.AgentId) || item.JobId != parsed.RunId || parsed.ElapsedMilliseconds < 0 ||
                parsed.Phase is not ("draft" or "settled" or "unavailable") ||
                parsed.Phase == "settled" && parsed.Finish is not ("Stop" or "Length" or "Cancelled" or "Unknown") ||
                parsed.Phase != "settled" && parsed.Finish is not null) return false;
            if (parsed.Phase == "draft")
            {
                if (parsed.Finish is not null) return false;
                if (parsed.Kind == "final")
                {
                    if (parsed.Operation is not null || string.IsNullOrEmpty(parsed.Text) || !PreviewText(parsed.Text)) return false;
                }
                else if (parsed.Kind == "tool")
                {
                    if (parsed.Text is not null || parsed.Truncated || !PreviewOperation(parsed.Operation, 256)) return false;
                }
                else return false;
            }
            else if (parsed.Kind is not null || parsed.Text is not null || parsed.Operation is not null || parsed.Truncated) return false;
            value = parsed; return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    private static bool PreviewIdentifier(string? value) => value is { Length: > 0 and <= 256 } &&
        !value.Any(char.IsControl) && ValidPreviewUnicode(value);

    private static bool PreviewOperation(string? value, int maximum) => value is { Length: > 0 } &&
        value.Length <= maximum && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private static bool PreviewText(string text) => text.Length <= MaximumGenerationPreviewCharacters &&
        !text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) &&
        PreviewUtf8.GetByteCount(text) <= MaximumGenerationPreviewBytes;

    private static bool ValidPreviewUnicode(string value)
    {
        try { _ = PreviewUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private sealed record GenerationPreviewValue(string Schema, string ConversationId, string RequestId,
        string RunId, string EffectId, string AgentId, string Phase, string? Kind, string? Text,
        string? Operation, bool Truncated, long ElapsedMilliseconds, string? Finish);
}
