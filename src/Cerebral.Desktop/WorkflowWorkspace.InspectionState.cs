using System.Windows.Threading;

namespace Cerebral.Desktop;

/// <summary>Small, text-free visual state for reopening an evicted exact-run presenter.</summary>
public sealed record WorkflowInspectionActivityState(string Key, double Offset, bool FollowEnd,
    bool ReviewHistory, bool Expanded);

public sealed record WorkflowInspectionViewState(string RunId, long? OldestEventSequence,
    bool ReadingHistory, IReadOnlyList<string> ExpandedIds, IReadOnlyList<string> CollapsedActivityIds,
    double TranscriptOffset, bool TranscriptFollowEnd,
    IReadOnlyList<WorkflowInspectionActivityState> Activities, bool Truncated)
{
    public WorkflowInspectionTextSelection? TextSelection { get; init; }
}

public sealed partial class WorkflowWorkspace
{
    private const int MaximumSavedInspectionIds = 64;
    private const int MaximumSavedActivityViewports = 40;
    private const int MaximumInspectionRestorePages = 8;

    public WorkflowInspectionViewState? CaptureInspectionState()
    {
        Dispatcher.VerifyAccess();
        if (conversation?.Snapshot.RunId is not { } runId) return null;
        var events = conversation.Snapshot.Events;
        var activities = activityViews.Take(MaximumSavedActivityViewports).Select(view =>
            new WorkflowInspectionActivityState(view.Key, view.Scroller.VerticalOffset,
                view.FollowEnd, view.ReviewHistory, view.Expander.IsExpanded)).ToArray();
        return new(runId, events.Count == 0 ? null : events[0].Sequence,
            readingHistory, expanded.Take(MaximumSavedInspectionIds).ToArray(),
            collapsedActivity.Take(MaximumSavedInspectionIds).ToArray(),
            transcriptScroller.VerticalOffset,
            transcriptScroller.ScrollableHeight <= transcriptScroller.VerticalOffset + 32,
            activities,
            expanded.Count > MaximumSavedInspectionIds || collapsedActivity.Count > MaximumSavedInspectionIds ||
            activityViews.Count > MaximumSavedActivityViewports)
        { TextSelection = inspectionTextSelection };
    }

    /// <summary>Restore only local inspection choices for the already opened exact run; never submit or change its owner.</summary>
    public async Task<bool> RestoreInspectionStateAsync(WorkflowInspectionViewState saved,
        CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(saved);
        if (conversation?.Snapshot.RunId != saved.RunId || mutationPending ||
            saved.ExpandedIds.Count > MaximumSavedInspectionIds ||
            saved.CollapsedActivityIds.Count > MaximumSavedInspectionIds ||
            saved.Activities.Count > MaximumSavedActivityViewports ||
            !double.IsFinite(saved.TranscriptOffset) || saved.TranscriptOffset < 0 ||
            saved.Activities.Any(view => !double.IsFinite(view.Offset) || view.Offset < 0))
            throw new InvalidOperationException("Inspection state does not match the selected exact run or its bounds.");
        var selected = conversation;
        var pages = 0;
        if (saved.ReadingHistory && saved.OldestEventSequence is { } oldest)
            while (selected.Snapshot.HasOlder && selected.Snapshot.Events.FirstOrDefault()?.Sequence > oldest &&
                   pages++ < MaximumInspectionRestorePages)
            {
                token.ThrowIfCancellationRequested();
                await selected.LoadOlderAsync(token);
                if (!ReferenceEquals(selected, conversation))
                    throw new InvalidOperationException("The selected run changed while restoring inspection history.");
            }
        var historyRestored = !saved.ReadingHistory || saved.OldestEventSequence is null ||
            selected.Snapshot.Events.FirstOrDefault()?.Sequence <= saved.OldestEventSequence;
        readingHistory = saved.ReadingHistory && historyRestored;
        expanded.Clear(); expanded.UnionWith(saved.ExpandedIds);
        collapsedActivity.Clear(); collapsedActivity.UnionWith(saved.CollapsedActivityIds);
        renderedVersion = null; Render();
        var byKey = saved.Activities.ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var view in activityViews)
            if (byKey.TryGetValue(view.Key, out var prior))
            {
                view.ReviewHistory = prior.ReviewHistory;
                view.FollowEnd = prior.FollowEnd;
                view.Expander.IsExpanded = prior.Expanded;
            }
        renderedVersion = null; Render();
        inspectionTextSelection = saved.TextSelection;
        var textRestored = RestoreInspectionTextSelection(saved.TextSelection);
        var inputRevision = transcriptInputRevision;
        var activityInputRevisions = activityViews.ToDictionary(view => view.Key, view => view.InputRevision,
            StringComparer.Ordinal);
        pendingScrollRestore = () =>
        {
            if (disposed || !ReferenceEquals(selected, conversation) || inputRevision != transcriptInputRevision)
            { pendingScrollRestore = null; return; }
            if (transcriptScroller.ViewportHeight <= 0) return;
            pendingScrollRestore = null;
            transcriptApplyingRestore = true;
            try
            {
                transcriptScroller.UpdateLayout();
                if (saved.TranscriptFollowEnd) transcriptScroller.ScrollToEnd();
                else transcriptScroller.ScrollToVerticalOffset(Math.Min(saved.TranscriptOffset, transcriptScroller.ScrollableHeight));
                foreach (var view in activityViews)
                    if (byKey.TryGetValue(view.Key, out var prior) &&
                        activityInputRevisions.TryGetValue(view.Key, out var revision) &&
                        view.InputRevision == revision)
                    {
                        view.Restoring = true;
                        try
                        {
                            view.Scroller.UpdateLayout();
                            if (prior.FollowEnd) view.Scroller.ScrollToEnd();
                            else view.Scroller.ScrollToVerticalOffset(Math.Min(prior.Offset, view.Scroller.ScrollableHeight));
                        }
                        finally { view.Restoring = false; }
                    }
                if (readingHistory)
                {
                    historyReleaseOffset = transcriptScroller.ScrollableHeight;
                    historyReleaseArmed = true;
                }
            }
            finally { transcriptApplyingRestore = false; }
        };
        await Dispatcher.InvokeAsync(QueuePendingScrollRestoration, DispatcherPriority.Loaded, token);
        if (!historyRestored || saved.Truncated || !textRestored)
            report("Some older inspection choices need a fresh Load earlier action.", false);
        return historyRestored && !saved.Truncated && textRestored;
    }
}
