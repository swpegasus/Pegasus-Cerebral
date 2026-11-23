using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Cerebral.Client;
using Cerebral.Contracts;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    private Button CreateActivitySummary(ActivityView view)
    {
        var button = Ui.Button("Activity", () =>
        {
            if (!activityViews.Contains(view) || view.Items.FirstOrDefault() is not { } item ||
                Snapshot?.ConversationId != item.ConversationId) return;
            var rows = ProjectActivityRows(view.Items);
            ActivityNavigationRequested?.Invoke(new(item.ConversationId, item.TurnId)
            { FirstSequence = rows.Count > 0 ? rows.Min(row => row.FirstSequence) : null,
                LastSequence = rows.Count > 0 ? rows.Max(row => row.FirstSequence) : null });
        });
        button.ContentTemplate = ActivityCaptionTemplate();
        button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Expander.Header)) { Source = view.Expander });
        button.SetBinding(Control.ForegroundProperty, new Binding(nameof(Expander.Foreground)) { Source = view.Expander });
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(0); button.Margin = new Thickness(0, 5, 0, 6);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.ToolTip = "Open this turn in the chat's Activity panel";
        AutomationProperties.SetAutomationId(button, "WorkflowActivity");
        AutomationProperties.SetName(button, "Open this turn in the chat's Activity panel");
        return button;
    }

    private static DataTemplate ActivityCaptionTemplate()
    {
        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, new Binding());
        caption.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        return new DataTemplate { VisualTree = caption };
    }

    internal sealed record ActivityOutcomeCounts(int Completed, int Failed, int Cancelled, int Unconfirmed, bool Observed);

    internal static ActivityOutcomeCounts RecordedActivityOutcomes(IEnumerable<CerebralEvent> events)
    {
        // Count the latest recorded receipt per operation, not both a dispatch and its receipt,
        // or a previous Unknown receipt followed by an explicitly reconciled terminal receipt.
        var receipts = events.Where(item => item.Type == CerebralEventTypes.ToolResult)
            .OrderBy(item => item.Sequence).GroupBy(item => TryActivityOperationIdentity(item, out var identity)
                ? (object)identity : (item.ConversationId,item.TurnId,item.Id)).Select(group => group.Last()).ToArray();
        var completed = 0; var failed = 0; var cancelled = 0; var unconfirmed = 0;
        foreach (var item in receipts)
        {
            var state = ActivityReceiptState(item)?.ToLowerInvariant();
            var success = ToolResult(item).Success;
            if (state is "unknown" or "prepared" or "dispatched") unconfirmed++;
            else if (state == "cancelled") cancelled++;
            else if (state == "succeeded" && success != false || state is null && success == true) completed++;
            else if (state == "failed" && success != true || state is null && success == false) failed++;
            else unconfirmed++;
        }
        return new(completed, failed, cancelled, unconfirmed, receipts.Length > 0);
    }

    internal static int RecordedActivityCallCount(IEnumerable<CerebralEvent> events) =>
        events.Where(item=>item.Type==CerebralEventTypes.ToolCall)
            .Select(item=>TryActivityOperationIdentity(item,out var identity)?(object)identity:(item.ConversationId,item.TurnId,item.Id))
            .Distinct().Count();

    private static string RecordedActivityOutcomeCaption(IReadOnlyList<CerebralEvent> items)
    {
        var counts=RecordedActivityOutcomes(items);
        if(!counts.Observed) return "Completed receipts — · Failed receipts —";
        var text=$"{counts.Completed} completed receipts shown · {counts.Failed} failed receipts shown";
        if(counts.Cancelled>0) text+=$" · {counts.Cancelled} cancelled receipts shown";
        if(counts.Unconfirmed>0) text+=$" · {counts.Unconfirmed} unconfirmed receipts shown";
        return text;
    }

    private void ApplyCompactActivity(ActivityView view)
    {
        if (view.ReviewButton is null && view.Expander.Content is Border viewport)
        {
            var panel = new DockPanel();
            var button = Ui.Button("Review earlier activity", () =>
            {
                view.ReviewHistory = !view.ReviewHistory;
                view.InputRevision++;
                view.FollowEnd = !view.ReviewHistory;
                ApplyCompactActivity(view);
            });
            AutomationProperties.SetAutomationId(button, "WorkflowActivityHistory");
            AutomationProperties.SetName(button, "Review earlier operational activity");
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(8, 3, 0, 3);
            DockPanel.SetDock(button, Dock.Bottom);
            panel.Children.Add(button);
            viewport.Child = null;
            panel.Children.Add(view.Scroller);
            viewport.Child = panel;
            view.ReviewButton = button;
        }
        var rows = view.Rows.Values.OrderBy(row => row.Model.FirstSequence).ThenBy(row => row.Model.LastSequence).ToArray();
        // Late receipts belong in the rolling view even when their call began earlier.
        // Retain the canonical call order and the same controls for the selected rows.
        var rolling = rows.OrderBy(row => row.Model.LastSequence).ThenBy(row => row.Model.FirstSequence).TakeLast(2).ToHashSet();
        foreach (var row in rows) row.Expander.Visibility = view.ReviewHistory || rolling.Contains(row) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var section in view.Sections.Values)
        {
            section.Header.Visibility = view.ReviewHistory ? Visibility.Visible : Visibility.Collapsed;
            section.Container.Visibility = section.Model.Rows.Any(row => view.Rows[row.Key].Expander.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        view.Scroller.MaxHeight = view.ReviewHistory ? 320 : 150;
        if (view.ReviewButton is { } review)
        {
            var earlier = Math.Max(0, rows.Length - 2);
            review.Visibility = earlier > 0 || view.ReviewHistory ? Visibility.Visible : Visibility.Collapsed;
            review.Content = view.ReviewHistory ? "Return to latest two rows" : $"Review earlier activity ({earlier})";
        }
    }
}
