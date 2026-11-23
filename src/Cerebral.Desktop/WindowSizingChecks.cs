using System.Windows;
using System.Windows.Controls;

namespace Cerebral.Desktop;

internal static class WindowSizingChecks
{
    public static int Verify(Window owner)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        var main = new Window { Width = 1280, Height = 840, MinWidth = 900, MinHeight = 620 };
        var preferredMinimum = new Size(main.MinWidth, main.MinHeight);
        try
        {
            WindowSizing.FitWithin(main, new Size(1920, 1040), preferredMinimum);
            Check(main.Width == 1280 && main.Height == 840, "A large work area must preserve the preferred window size.");
            WindowSizing.FitWithin(main, new Size(1280, 680), preferredMinimum);
            Check(main.Height <= 680 && main.MinHeight == 620, "A shorter work area must cap the main window without losing its ordinary minimum.");
            WindowSizing.FitWithin(main, new Size(960, 516), preferredMinimum);
            Check(main.Width <= 960 && main.Height <= 516 && main.MinHeight <= 516, "Minimum dimensions must not force the window below a small work area's bottom edge.");
            Check(main.ResizeMode == ResizeMode.CanResize && double.IsPositiveInfinity(main.MaxHeight), "Fitting must preserve normal user resizing.");
            WindowSizing.FitWithin(main, new Size(1920, 1040), preferredMinimum);
            Check(main.MinWidth == 900 && main.MinHeight == 620, "Moving back to a larger work area must restore the preferred minimum.");
        }
        finally { main.Close(); }

        // A dialog owner needs a native HWND; creating it does not show the app or fire Loaded/start services.
        new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        var dialog = new FieldsDialog(owner, "Configuration layout", "A form with enough fields to require scrolling.");
        try
        {
            for (var index = 0; index < 16; index++) dialog.Add("field" + index, "A configurable setting " + index, "Example value");
            dialog.Check("choice", "An optional setting", true);
            WindowSizing.FitWithin(dialog, new Size(960, 516), new Size(dialog.MinWidth, dialog.MinHeight));
            Check(dialog.Height <= 516 && dialog.Width <= 960, "A settings dialog must fit the available logical work area.");
            var root = (DockPanel)dialog.Content;
            var client = new Size(dialog.Width - 20, dialog.Height - 40);
            root.Measure(client); root.Arrange(new Rect(client)); root.UpdateLayout();
            var buttons = root.Children.OfType<WrapPanel>().Single(x => DockPanel.GetDock(x) == Dock.Bottom);
            var scroll = root.Children.OfType<ScrollViewer>().Single();
            var footer = buttons.TranslatePoint(new Point(), root);
            Check(footer.Y >= 0 && footer.Y + buttons.ActualHeight <= root.ActualHeight + 0.5, "Save and Cancel must remain inside a short dialog.");
            Check(buttons.Children.OfType<Button>().All(button => button.ActualHeight >= 30), "Dialog actions must retain usable height.");
            Check(scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0, "A short settings dialog must scroll its fields while preserving its action row.");
            Check(dialog.UseLayoutRounding, "Settings dialogs must use WPF layout rounding.");
        }
        finally { dialog.Close(); }
        return checks;
    }
}
