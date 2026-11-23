using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Cerebral.Desktop;

/// <summary>Bounded diagnostics over the currently rendered activity viewports. Offsets are pixels.</summary>
public sealed record WorkflowActivityViewportSnapshot(string Key, double VerticalOffset, double ScrollableHeight, bool FollowingLatest)
{
    public int RenderedRowCount { get; init; }
    public IReadOnlyList<string> RenderedRowKeys { get; init; } = [];
    public bool RenderedRowKeysTruncated { get; init; }
    public string RenderedHeader { get; init; } = "";
    public bool RenderedHeaderTruncated { get; init; }
    public int RenderedSectionCount { get; init; }
    public IReadOnlyList<WorkflowActivitySectionSnapshot> RenderedSections { get; init; } = [];
    public bool RenderedSectionsTruncated { get; init; }
}

/// <summary>Bounded actual section headers and counts; command arguments and detail bodies are excluded.</summary>
public sealed record WorkflowActivitySectionSnapshot(string Key, string Header, int RowCount, bool HeaderTruncated);

/// <summary>Numeric evidence over activity rows committed to the visual tree, excluding hidden bookkeeping.</summary>
public sealed record WorkflowActivityDisplaySnapshot(int Count, long? FirstSequence, long? LastSequence);

public sealed partial class WorkflowWorkspace
{
    public const int MaximumActivityDiagnosticViewports = 40;
    public const int MaximumActivityDiagnosticHeaderCharacters = 512;

    public bool IsReadingHistory => readingHistory;

    /// <summary>The rendered rows can span several recent service pages; this is not a cumulative run count.</summary>
    public WorkflowActivityDisplaySnapshot DisplayedActivity
    {
        get
        {
            var count = 0; long? first = null; long? last = null;
            foreach (var item in activityViews.SelectMany(view => view.Items).Where(ShowActivityEntry))
            {
                count++;
                first = first is null ? item.Sequence : Math.Min(first.Value, item.Sequence);
                last = last is null ? item.Sequence : Math.Max(last.Value, item.Sequence);
            }
            return new(count, first, last);
        }
    }

    public IReadOnlyList<WorkflowActivityViewportSnapshot> ActivityViewportSnapshots => activityViews
        .Select(view =>
        {
            var header = view.Expander.Header as string ?? "";
            return new WorkflowActivityViewportSnapshot(view.Key, view.Scroller.VerticalOffset,
                view.Scroller.ScrollableHeight, view.FollowEnd)
            {
                RenderedRowCount = view.Rows.Count,
                RenderedRowKeys = view.Rows.Values.OrderBy(row => row.Model.FirstSequence)
                    .TakeLast(MaximumActivityDiagnosticViewports).Select(row => row.Model.Key).ToArray(),
                RenderedRowKeysTruncated = view.Rows.Count > MaximumActivityDiagnosticViewports,
                RenderedHeader = header[..Math.Min(header.Length, MaximumActivityDiagnosticHeaderCharacters)],
                RenderedHeaderTruncated = header.Length > MaximumActivityDiagnosticHeaderCharacters,
                RenderedSectionCount = view.Sections.Count,
                RenderedSections = view.Sections.Values.OrderBy(section => section.Model.Rows[0].FirstSequence)
                    .TakeLast(MaximumActivityDiagnosticViewports).Select(section =>
                    {
                        var rendered = section.Header.Text ?? "";
                        return new WorkflowActivitySectionSnapshot(section.Model.Key,
                            rendered[..Math.Min(rendered.Length, MaximumActivityDiagnosticHeaderCharacters)],
                            section.Model.Rows.Count, rendered.Length > MaximumActivityDiagnosticHeaderCharacters);
                    }).ToArray(),
                RenderedSectionsTruncated = view.Sections.Count > MaximumActivityDiagnosticViewports
            };
        }).ToArray();

    public WorkflowActivityViewportSnapshot TranscriptViewportSnapshot => new("transcript",
        transcriptScroller.VerticalOffset, transcriptScroller.ScrollableHeight,
        transcriptScroller.ScrollableHeight <= transcriptScroller.VerticalOffset + 32);

    private void QueuePendingScrollRestoration()
    {
        if (pendingScrollRestore is not { } restore) return;
        PassiveDesktopInteraction.Defer(Dispatcher, "transcript position", () =>
        {
            // Several layout notifications can queue the same restoration. Once it settles or
            // fails, stale callbacks must not replay it or repeat its diagnostic banner.
            if (ReferenceEquals(pendingScrollRestore, restore)) restore();
        }, message => report(message, true), () =>
        {
            if (pendingScrollRestore is not null && !ReferenceEquals(pendingScrollRestore, restore)) return;
            pendingScrollRestore = null;
            transcriptRestoring = transcriptApplyingRestore = false;
            foreach (var view in activityViews) view.Restoring = false;
        });
    }

    private void ConfigureTranscriptScrollTracking()
    {
        void Changed()
        {
            transcriptInputRevision++;
            transcriptRestoring = false;
            pendingQuestionFocus = null;
        }
        transcriptScroller.ScrollChanged += (_, change) =>
        {
            // Accessibility/programmatic user scrolling need not produce a preview input event.
            // Renderer scrolling is explicitly tagged; extent/viewport clamping is layout only.
            if (transcriptApplyingRestore || !ReferenceEquals(change.OriginalSource, transcriptScroller) ||
                change.ExtentHeightChange != 0 || change.ViewportHeightChange != 0 ||
                change.ExtentWidthChange != 0 || change.ViewportWidthChange != 0 || change.VerticalChange == 0) return;
            Changed();
        };
        transcriptScroller.PreviewMouseDown += (_, _) => Changed();
        transcriptScroller.PreviewMouseWheel += (_, _) => Changed();
        transcriptScroller.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) Changed();
        };
        transcriptScroller.AddHandler(TextBoxBase.SelectionChangedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TextBox { SelectionLength: > 0 } ||
                e.OriginalSource is RichTextBox rich && !rich.Selection.IsEmpty) Changed();
        }));
    }

    private void ConfigureActivityScrollTracking(ActivityView view)
    {
        void Pause()
        {
            if (!activityViews.Contains(view)) return;
            view.InputRevision++;
            view.Restoring = false;
            view.FollowEnd = false;
        }
        view.Scroller.PreviewMouseDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source) return;
            PassiveDesktopInteraction.TryRun("activity inspection", () =>
            {
                if (ActivityInspectionTarget(source, view.Scroller)) Pause();
                else ResumeActivityFollowAfterOutsideClick(source);
            }, message => report(message, true));
        };
        view.Scroller.PreviewMouseWheel += (_, _) => Pause();
        view.Scroller.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) Pause();
        };
        view.Scroller.AddHandler(TextBoxBase.SelectionChangedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TextBox { SelectionLength: > 0 } ||
                e.OriginalSource is RichTextBox rich && !rich.Selection.IsEmpty) Pause();
        }));
        view.Scroller.ScrollChanged += (_, change) =>
        {
            // Nested text boxes also bubble this event. Neither their scroll nor layout clamping
            // is a change of the user's follow choice for the surrounding activity viewport.
            if (!activityViews.Contains(view) || view.Restoring ||
                !ReferenceEquals(change.OriginalSource, view.Scroller) ||
                change.ExtentHeightChange != 0 || change.ViewportHeightChange != 0 ||
                change.ExtentWidthChange != 0 || change.ViewportWidthChange != 0 ||
                change.VerticalChange == 0) return;
            view.FollowEnd = view.Scroller.ScrollableHeight <= view.Scroller.VerticalOffset + 16;
        };
    }

    /// <summary>Resumes live activity without moving keyboard focus or changing the composer.</summary>
    public void ResumeActivityFollowAfterOutsideClick(DependencyObject source)
    {
        PassiveDesktopInteraction.TryRun("activity follow", () =>
        {
            foreach (var view in activityViews.Where(view => view.Expander.IsExpanded))
            {
                if (ActivityInspectionTarget(source, view.Scroller)) continue;
                // Clear selection only inside activity; composer and other input text remain untouched.
                foreach (var text in ActivityTextControls(view.Scroller))
                {
                    if (text is TextBox plain && plain.SelectionLength > 0) plain.Select(plain.CaretIndex, 0);
                    else if (text is RichTextBox rich && !rich.Selection.IsEmpty) rich.Selection.Select(rich.CaretPosition, rich.CaretPosition);
                }
                view.InputRevision++;
                view.FollowEnd = true;
                view.Restoring = true;
                var revision = view.InputRevision;
                PassiveDesktopInteraction.Defer(Dispatcher, "activity position", () =>
                {
                    if (disposed || !activityViews.Contains(view) || revision != view.InputRevision) return;
                    try
                    {
                        view.Scroller.UpdateLayout();
                        view.Scroller.ScrollToEnd();
                        view.Scroller.UpdateLayout();
                    }
                    finally { view.Restoring = false; }
                }, message => report(message, true), () =>
                {
                    if (!activityViews.Contains(view) || revision != view.InputRevision) return;
                    view.Restoring = view.FollowEnd = false;
                });
            }
        }, message => report(message, true));
    }

    private static bool ActivityInspectionTarget(DependencyObject source, ScrollViewer viewport)
    {
        if (!UiTree.IsWithin(source, viewport)) return false;
        for (DependencyObject? current = source; current is not null && !ReferenceEquals(current, viewport);
            current = UiTree.GetParent(current))
        {
            if (current is TextBoxBase or ScrollBar or Thumb or ToggleButton) return true;
        }
        return false;
    }

    private static IEnumerable<TextBoxBase> ActivityTextControls(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is TextBoxBase text) yield return text;
            foreach (var nested in ActivityTextControls(child)) yield return nested;
        }
    }
}
