using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cerebral.Desktop;

public sealed record AttachmentDraftRow(string Id, string Name, long Length, string State, string? Error,
    bool CanOpen, bool CanRetry, bool CanRemove);

/// <summary>Bounded native file-reference presentation. No connection, file access or preference ownership.</summary>
public sealed class AttachmentDraftView : ScrollViewer
{
    private readonly StackPanel rows = new();
    public AttachmentDraftView()
    {
        Content = rows; MaxHeight = 94; Visibility = Visibility.Collapsed;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        AutomationProperties.SetAutomationId(this, "ComposerAttachments");
        AutomationProperties.SetName(this, "Selected files and images");
    }
    public void Present(IReadOnlyList<AttachmentDraftRow> values, Action<string> open, Action<string> retry, Action<string> remove)
    {
        if (values.Count > 16) throw new ArgumentException("At most 16 draft attachments are supported.");
        rows.Children.Clear(); Visibility = values.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in values)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            var openButton = Ui.Button("Open", () => open(item.Id)); openButton.IsEnabled = item.CanOpen;
            AutomationProperties.SetAutomationId(openButton, "OpenAttachment");
            openButton.ToolTip = "Open or preview the original selected file"; controls.Children.Add(openButton);
            if (item.State != "Ready")
            {
                var button = Ui.Button("Retry", () => retry(item.Id)); button.IsEnabled = item.CanRetry;
                AutomationProperties.SetAutomationId(button, "RetryAttachment");
                button.ToolTip = "Reconcile and resume this original import"; controls.Children.Add(button);
            }
            var removeButton = Ui.Button("Remove", () => remove(item.Id)); removeButton.IsEnabled = item.CanRemove;
            AutomationProperties.SetAutomationId(removeButton, "RemoveAttachment");
            removeButton.ToolTip = "Remove this file from the draft"; controls.Children.Add(removeButton);
            DockPanel.SetDock(controls, Dock.Right); row.Children.Add(controls);
            var failed = item.Error is not null;
            var label = Ui.Text(item.Name + " · " + AttachmentPresentation.Size(item.Length) + " · " + item.State, 12,
                failed ? Ui.ErrorText : Ui.Muted);
            label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextWrapping = TextWrapping.NoWrap;
            label.VerticalAlignment = VerticalAlignment.Center; label.ToolTip = item.Error ?? item.Name;
            label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && item.CanOpen) { e.Handled = true; open(item.Id); } };
            row.Children.Add(label);
            rows.Children.Add(new Border
            {
                Child = row,
                Background = failed ? Ui.ErrorBackground : Brushes.Transparent,
                BorderBrush = failed ? Ui.ErrorBorder : Brushes.Transparent,
                BorderThickness = failed ? new Thickness(3, 0, 0, 0) : new Thickness(0),
                Padding = failed ? new Thickness(7, 4, 4, 4) : new Thickness(0)
            });
        }
    }
    public static void EnableFileDrop(UIElement target, Func<bool> canReceive, Action<IReadOnlyList<string>> receive)
    {
        target.AllowDrop = true;
        target.PreviewDragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = canReceive() ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
        };
        target.PreviewDrop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            if (canReceive() && e.Data.GetData(DataFormats.FileDrop) is string[] paths) receive(paths);
        };
    }
}
