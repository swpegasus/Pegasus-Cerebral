using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    internal static bool ShouldReleaseTextEntryFocus(DependencyObject source, IInputElement? focusedElement)
    {
        if (focusedElement is not (TextBoxBase or PasswordBox)) return false;

        // Let interactive controls manage their own focus. Clearing focus during the
        // preview event closes a ComboBox popup before its item can accept the click.
        // Only an otherwise passive background click should dismiss text entry.
        for (DependencyObject? current = source; current is not null; current = current switch
        {
            Visual or Visual3D => VisualTreeHelper.GetParent(current),
            FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
            ContentElement content => ContentOperations.GetParent(content),
            _ => LogicalTreeHelper.GetParent(current)
        })
        {
            if (current is Control { Focusable: true, IsEnabled: true } and not Window) return false;
        }
        return true;
    }
}
