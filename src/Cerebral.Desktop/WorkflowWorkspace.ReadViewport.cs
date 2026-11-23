using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;

namespace Cerebral.Desktop;

/// <summary>Observed message coordinates from the rendered viewport, not a service read receipt.</summary>
public sealed record WorkflowReadViewport(string RunId, string ConversationId,
    long? HighestVisibleMessageSequence, long? LatestLoadedMessageSequence, bool HasNewer,
    long LoadedRunRevision = 0);

public sealed partial class WorkflowWorkspace
{
    private readonly List<(string EventId, long Sequence, FrameworkElement Card)> renderedMessageCards = [];
    private readonly List<(long Sequence, FrameworkElement Card)> renderedReadCards = [];
    private bool readViewportQueued;
    private WorkflowReadViewport? lastReadViewport;
    private Window? readViewportWindow;

    public event EventHandler<WorkflowReadViewport>? ReadViewportChanged;

    private void ConfigureReadViewportWindow()
    {
        Loaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (ReferenceEquals(window, readViewportWindow)) return;
            if (readViewportWindow is not null) readViewportWindow.Activated -= ReadViewportWindowActivated;
            readViewportWindow = window;
            if (window is not null) window.Activated += ReadViewportWindowActivated;
            ObserveReadViewport();
        };
        Unloaded += (_, _) =>
            DetachReadViewportWindow();
    }

    private void DetachReadViewportWindow()
    {
        if (readViewportWindow is not null) readViewportWindow.Activated -= ReadViewportWindowActivated;
        readViewportWindow = null;
    }

    private void ReadViewportWindowActivated(object? sender, EventArgs e) => ObserveReadViewport();

    internal static bool CanObserveReadViewport(bool presenterVisible, bool scrollerVisible,
        bool windowVisible, bool windowActive, bool minimized, double viewportHeight) =>
        presenterVisible && scrollerVisible && windowVisible && windowActive && !minimized && viewportHeight > 0;

    internal static bool IsMessageCardVisible(double top, double bottom, double viewportHeight) =>
        double.IsFinite(top) && double.IsFinite(bottom) && double.IsFinite(viewportHeight) &&
        bottom > top && viewportHeight > 0 &&
        Math.Min(bottom, viewportHeight) - Math.Max(top, 0) >= Math.Min(24, (bottom - top) / 2);

    internal static bool CountsAsMessage(string eventType) =>
        eventType is CerebralEventTypes.Message or "workflow.result";

    /// <summary>Queue a visible-card observation after WPF has arranged the current presenter.</summary>
    public void ObserveReadViewport()
    {
        Dispatcher.VerifyAccess();
        if (readViewportQueued || disposed || ReadViewportChanged is null) return;
        readViewportQueued = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            readViewportQueued = false;
            var window = Window.GetWindow(this);
            if (disposed || window is null || !CanObserveReadViewport(IsVisible, transcriptScroller.IsVisible,
                    window.IsVisible, window.IsActive, window.WindowState == WindowState.Minimized,
                    transcriptScroller.ViewportHeight) ||
                conversation?.Snapshot is not { RunId: { } runId } snapshot) return;
            var visible = new Rect(0, 0, transcriptScroller.ActualWidth, transcriptScroller.ViewportHeight);
            try
            {
                for (var ancestor = VisualTreeHelper.GetParent(transcriptScroller); ancestor is not null;
                     ancestor = VisualTreeHelper.GetParent(ancestor))
                {
                    if (ancestor is not FrameworkElement element ||
                        !(element.ClipToBounds || element is ScrollViewer or ScrollContentPresenter)) continue;
                    var clip = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
                    if (element.Clip is { } geometry) clip.Intersect(geometry.Bounds);
                    visible.Intersect(element.TransformToDescendant(transcriptScroller).TransformBounds(clip));
                    if (visible.IsEmpty) return;
                }
            }
            catch (InvalidOperationException) { return; }
            if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return;
            long? highestVisible = null;
            long? latestLoaded = null;
            foreach (var (sequence, card) in renderedReadCards)
            {
                latestLoaded = Math.Max(latestLoaded ?? -1, sequence);
                if (!card.IsVisible || card.ActualHeight <= 0) continue;
                Rect bounds;
                try { bounds = card.TransformToAncestor(transcriptScroller)
                    .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight)); }
                catch (InvalidOperationException) { continue; }
                if (bounds.Right > visible.Left && bounds.Left < visible.Right &&
                    IsMessageCardVisible(bounds.Top - visible.Top, bounds.Bottom - visible.Top, visible.Height))
                    highestVisible = Math.Max(highestVisible ?? -1, sequence);
            }
            var observation = new WorkflowReadViewport(runId, snapshot.ConversationId,
                highestVisible, latestLoaded, snapshot.HasNewer, snapshot.Run?.Revision ?? 0);
            if (observation == lastReadViewport) return;
            lastReadViewport = observation;
            ReadViewportChanged?.Invoke(this, observation);
        }, DispatcherPriority.Background);
    }

    /// <summary>An outer work-panel scroller changed visibility without resizing this transcript.</summary>
    public void RefreshReadViewportVisibility()
    {
        Dispatcher.VerifyAccess();
        lastReadViewport = null;
        ObserveReadViewport();
    }
}

internal static class WorkflowReadViewportChecks
{
    internal static int Check()
    {
        var checks = 0;
        void Require(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks++; }
        Require(WorkflowWorkspace.CanObserveReadViewport(true, true, true, true, false, 400),
            "An active non-minimized window admits viewport observation.");
        Require(!WorkflowWorkspace.CanObserveReadViewport(true, true, true, false, false, 400),
            "An inactive window cannot mark newly loaded messages viewed.");
        Require(!WorkflowWorkspace.CanObserveReadViewport(true, true, true, true, true, 400),
            "A minimized window cannot mark newly loaded messages viewed.");
        Require(!WorkflowWorkspace.CanObserveReadViewport(true, true, false, true, false, 400) &&
                !WorkflowWorkspace.CanObserveReadViewport(false, true, true, true, false, 400),
            "A hidden window or presenter cannot mark messages viewed.");
        Require(!WorkflowWorkspace.CanObserveReadViewport(true, true, true, true, false, 0),
            "An unarranged viewport cannot mark messages viewed.");
        Require(WorkflowWorkspace.IsMessageCardVisible(10, 50, 400) &&
                !WorkflowWorkspace.IsMessageCardVisible(399, 450, 400) &&
                !WorkflowWorkspace.IsMessageCardVisible(450, 500, 400),
            "Only a meaningfully visible message card advances the local read coordinate.");
        var viewedRun = new WorkflowReadViewport("run", "chat", 5, 5, false, 2);
        Require(viewedRun != viewedRun with { LoadedRunRevision = 3 },
            "A status-only run revision produces a new viewport observation.");
        Require(WorkflowWorkspace.CountsAsMessage(CerebralEventTypes.Message) &&
                WorkflowWorkspace.CountsAsMessage("workflow.result") &&
                !WorkflowWorkspace.CountsAsMessage("workflow.assignment") &&
                !WorkflowWorkspace.CountsAsMessage(CerebralEventTypes.ToolResult),
            "Unread coordinates count user and assistant messages, not other transcript cards or tools.");
        return checks;
    }
}
