using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Cerebral.Desktop;

/// <summary>Walks both visual controls and document content from routed input events.</summary>
public static class UiTree
{
    public static DependencyObject? GetParent(DependencyObject source)
    {
        // Run and Hyperlink are content elements, even when their routed input is
        // delivered through a visual RichTextBox. Visual tree APIs reject them.
        if (source is ContentElement content)
            return ContentOperations.GetParent(content) ??
                (content as FrameworkContentElement)?.Parent ?? LogicalTreeHelper.GetParent(content);
        return source is Visual or Visual3D
            ? VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source)
            : LogicalTreeHelper.GetParent(source);
    }

    public static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
