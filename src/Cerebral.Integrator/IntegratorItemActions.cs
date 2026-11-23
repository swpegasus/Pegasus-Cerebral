using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private void ConfigureSettingsItemActions(DataGrid list, params (string Label, Func<Task> Action)[] actions)
    {
        var menu = SettingsActionMenu(actions);
        list.ContextMenu = menu;
        var pointerHasRow = false;
        list.PreviewMouseRightButtonDown += (_, args) =>
        {
            var row = SettingsRowFromSource(args.OriginalSource as DependencyObject);
            pointerHasRow = row is not null;
            if (row is not null && !row.IsSelected) list.SelectedItem = row.Item;
        };
        list.ContextMenuOpening += (_, args) =>
        {
            if (!CanOpenSettingsItemContextMenu(args.CursorLeft < 0 && args.CursorTop < 0,
                pointerHasRow, list.SelectedItem is not null)) args.Handled = true;
        };
        menu.Opened += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = list.SelectedItem is not null;
        };
    }

    internal static bool CanOpenSettingsItemContextMenu(bool keyboardInvocation, bool pointerHasRow, bool hasSelection) =>
        hasSelection && (keyboardInvocation || pointerHasRow);

    private Button SettingsMoreButton(string label, params (string Label, Func<Task> Action)[] actions)
    {
        var menu = SettingsActionMenu(actions);
        var button = Ui.Button(label, () => { });
        button.ContextMenu = menu;
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        };
        return button;
    }

    private ContextMenu SettingsActionMenu(IEnumerable<(string Label, Func<Task> Action)> actions)
    {
        var menu = new ContextMenu { Background = Ui.Surface, Foreground = Ui.Ink, BorderBrush = Ui.Line };
        foreach (var (label, action) in actions)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Run(action);
            menu.Items.Add(item);
        }
        return menu;
    }
}
