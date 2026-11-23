using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace Cerebral.Desktop;

/// <summary>Text-free, bounded caret or selection coordinates for one immutable transcript event.</summary>
public sealed record WorkflowInspectionTextSelection(string EventId, int ControlIndex,
    bool RichText, int TextLength, int Start, int Length);

public sealed partial class WorkflowWorkspace
{
    private const int MaximumInspectionTextLength = 1_000_000;
    private WorkflowInspectionTextSelection? inspectionTextSelection;

    private void ConfigureInspectionTextTracking()
    {
        transcriptScroller.AddHandler(TextBoxBase.SelectionChangedEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is TextBoxBase control) RememberInspectionTextSelection(control);
            }));
        transcriptScroller.AddHandler(Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) =>
            {
                if (e.NewFocus is TextBoxBase control) RememberInspectionTextSelection(control);
            }));
    }

    private void RememberInspectionTextSelection(TextBoxBase control)
    {
        if (disposed || !control.IsKeyboardFocusWithin && !HasSelection(control)) return;
        foreach (var (eventId, _, card) in renderedMessageCards)
        {
            if (!UiTree.IsWithin(control, card)) continue;
            var index = 0;
            foreach (var candidate in InspectionTextControls(card))
            {
                if (ReferenceEquals(candidate, control))
                {
                    inspectionTextSelection = CaptureTextSelection(eventId, index, control);
                    return;
                }
                index++;
            }
            return;
        }
    }

    private static bool HasSelection(TextBoxBase control) => control switch
    {
        TextBox text => text.SelectionLength > 0,
        RichTextBox rich => !rich.Selection.IsEmpty,
        _ => false
    };

    internal static WorkflowInspectionTextSelection? CaptureTextSelection(string eventId, int index,
        TextBoxBase control)
    {
        if (eventId.Length is < 1 or > 256 || index is < 0 or > 127) return null;
        if (control is TextBox text && text.Text.Length <= MaximumInspectionTextLength)
            return new(eventId, index, false, text.Text.Length, text.SelectionStart, text.SelectionLength);
        if (control is RichTextBox rich)
        {
            var start = rich.Document.ContentStart.GetOffsetToPosition(rich.Selection.Start);
            var end = rich.Document.ContentStart.GetOffsetToPosition(rich.Selection.End);
            var length = rich.Document.ContentStart.GetOffsetToPosition(rich.Document.ContentEnd);
            if (start >= 0 && end >= start && length <= MaximumInspectionTextLength)
                return new(eventId, index, true, length, start, end - start);
        }
        return null;
    }

    private static IEnumerable<TextBoxBase> InspectionTextControls(DependencyObject root)
    {
        if (root is TextBoxBase text) { yield return text; yield break; }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var control in InspectionTextControls(child)) yield return control;
    }

    private bool RestoreInspectionTextSelection(WorkflowInspectionTextSelection? saved)
    {
        if (saved is null) return true;
        var card = renderedMessageCards.FirstOrDefault(item => item.EventId == saved.EventId).Card;
        if (card is null) return false;
        var control = InspectionTextControls(card).Skip(saved.ControlIndex).FirstOrDefault();
        return control is not null && ApplyTextSelection(saved, control);
    }

    internal static bool ApplyTextSelection(WorkflowInspectionTextSelection saved, TextBoxBase control)
    {
        if (saved.EventId.Length is < 1 or > 256 || saved.ControlIndex is < 0 or > 127 ||
            saved.TextLength is < 0 or > MaximumInspectionTextLength ||
            saved.Start < 0 || saved.Length < 0 || saved.Start > saved.TextLength - saved.Length)
            return false;
        if (control is TextBox text && !saved.RichText && text.Text.Length == saved.TextLength)
        { text.Select(saved.Start, saved.Length); return true; }
        if (control is RichTextBox rich && saved.RichText)
        {
            var origin = rich.Document.ContentStart;
            if (origin.GetOffsetToPosition(rich.Document.ContentEnd) != saved.TextLength) return false;
            var start = origin.GetPositionAtOffset(saved.Start, LogicalDirection.Forward);
            var end = origin.GetPositionAtOffset(saved.Start + saved.Length, LogicalDirection.Forward);
            if (start is null || end is null) return false;
            rich.Selection.Select(start, end);
            return true;
        }
        return false;
    }
}

internal static class WorkflowInspectionTextChecks
{
    internal static int Check()
    {
        var checks = 0;
        void Require(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks++; }
        var source = new TextBox { Text = "hello world" };
        source.Select(2, 4);
        var saved = WorkflowWorkspace.CaptureTextSelection("message-a", 0, source);
        var target = new TextBox { Text = source.Text };
        Require(saved is not null && WorkflowWorkspace.ApplyTextSelection(saved, target) &&
                target.SelectionStart == 2 && target.SelectionLength == 4,
            "An immutable plain message retains its selection coordinates.");
        Require(!WorkflowWorkspace.ApplyTextSelection(saved!, new TextBox { Text = "changed" }) &&
                !WorkflowWorkspace.ApplyTextSelection(saved! with { Start = 99 }, target),
            "Changed text and out-of-range coordinates refuse restoration.");
        RichTextBox Rich() => new() { Document = new FlowDocument(new Paragraph(new Run("hello world"))) };
        var richSource = Rich();
        var begin = richSource.Document.ContentStart.GetPositionAtOffset(2, LogicalDirection.Forward);
        var end = richSource.Document.ContentStart.GetPositionAtOffset(6, LogicalDirection.Forward);
        if (begin is null || end is null) throw new InvalidOperationException("Rich selection fixture could not select text.");
        richSource.Selection.Select(begin, end);
        var richSaved = WorkflowWorkspace.CaptureTextSelection("message-b", 0, richSource);
        var richTarget = Rich();
        Require(richSaved is not null && WorkflowWorkspace.ApplyTextSelection(richSaved, richTarget) &&
                WorkflowWorkspace.CaptureTextSelection("message-b", 0, richTarget) == richSaved,
            "A re-rendered Markdown document retains its caret and selection offsets.");
        return checks;
    }
}
