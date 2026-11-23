using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Cerebral.Desktop;

/// <summary>A read-only document keeps ordinary selection and copying across paragraphs and tables.</summary>
internal static class SelectableMarkdown
{
    internal const double NormalBodyFontSize = 14;
    internal const double NormalBodyLineHeight = 22;
    private static readonly FontFamily NormalBodyFontFamily = new("Segoe UI");

    internal static Paragraph NormalBodyParagraph() => new()
    {
        Margin = new Thickness(0, 0, 0, 3), LineHeight = NormalBodyLineHeight
    };

    public static RichTextBox View(FlowDocument document)
    {
        document.FontFamily = NormalBodyFontFamily; document.FontSize = NormalBodyFontSize;
        document.Foreground = Ui.Ink; document.PagePadding = new Thickness(0);
        var view = new RichTextBox
        {
            Document = document, IsReadOnly = true, IsDocumentEnabled = true,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsUndoEnabled = false, SelectionBrush = new SolidColorBrush(Color.FromRgb(169, 216, 225))
        };
        RouteMouseWheelToAncestor(view);
        AutomationProperties.SetName(view, "Selectable message text");
        TextContextMenus.Attach(view);
        return view;
    }

    internal static void RouteMouseWheelToAncestor(FrameworkElement view)
    {
        view.PreviewMouseWheel += (_, e) =>
        {
            var parent = FindAncestorScrollViewer(view);
            if (parent is null) return;
            parent.ScrollToVerticalOffset(Math.Clamp(parent.VerticalOffset - e.Delta, 0, parent.ScrollableHeight));
            e.Handled = true;
        };
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject source)
    {
        for (var current = UiTree.GetParent(source); current is not null; current = UiTree.GetParent(current))
            if (current is ScrollViewer scroll) return scroll;
        return null;
    }
}
