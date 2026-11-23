using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

/// <summary>Native offscreen WPF/resource fixtures; no visible windows, services, model calls or network.</summary>
internal static class DesktopIconTests
{
    public static int Run()
    {
        var app = Application.Current ?? throw new InvalidOperationException("The icon fixture requires an STA WPF Application.");
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        var hadBrush = app.Resources.Contains(DesktopThemeResourceKeys.TextBrush);
        var priorBrush = app.Resources[DesktopThemeResourceKeys.TextBrush];
        var tempParent = Path.GetFullPath(Path.GetTempPath());
        var ownedRoot = Path.GetFullPath(Path.Combine(tempParent, "Cerebral-ChatIcons-" + Guid.NewGuid().ToString("N")));
        try
        {
            var index = Path.Combine(AppContext.BaseDirectory, "Resources", DesktopIcons.PackRelativeDirectory, "index.ini");
            Check(File.Exists(index), "Build output must carry the AI-readable physical icon pack.");
            var baseline = File.ReadAllText(index);
            Check(DesktopIcons.Definitions.Count == 11 && DesktopIcons.Definitions.Select(value => value.Key).Distinct().Count() == 11,
                "The pack must expose eleven distinct semantic identities.");
            var statusPixels = new List<byte[]>();
            foreach (var theme in new[] { DesktopThemeCatalog.Light, DesktopThemeCatalog.Dark })
            {
                app.Resources[DesktopThemeResourceKeys.TextBrush] = new SolidColorBrush(theme.Ink);
                Check(Contrast(theme.Ink, theme.Surface) >= 4.5, "Theme ink must maintain readable contrast for small native icons.");
                foreach (var definition in DesktopIcons.Definitions)
                foreach (var size in new[] { 14, 16, 20 })
                {
                    var icon = DesktopIcons.Create(definition.Key, size);
                    Check(AutomationProperties.GetName(icon) == definition.Label &&
                        AutomationProperties.GetHelpText(icon) == definition.Description,
                        "Every icon must expose its semantic name and description independently of color.");
                    var pixels = Render(icon, size);
                    Check(pixels.Where((_, position) => position % 4 == 3).Any(alpha => alpha != 0),
                        $"Native geometry must render nonempty pixels for {definition.Key} at {size}px.");
                    if (theme == DesktopThemeCatalog.Light && size == 16 && definition.Key.StartsWith("status.", StringComparison.Ordinal))
                        statusPixels.Add(pixels);
                }
            }
            Check(statusPixels.Select(Convert.ToHexString).Distinct().Count() == 7,
                "All seven status shapes must differ when rendered in the same monochrome ink.");
            // Match Ui.ThemeBrush: a bound brush stays mutable after native rendering.
            var inkState = new InkState { Ink = DesktopThemeCatalog.Light.Ink };
            var mutableInk = new SolidColorBrush();
            BindingOperations.SetBinding(mutableInk, SolidColorBrush.ColorProperty,
                new Binding(nameof(InkState.Ink)) { Source = inkState, Mode = BindingMode.OneWay });
            app.Resources[DesktopThemeResourceKeys.TextBrush] = mutableInk;
            var live = (ContentControl)DesktopIcons.Create(DesktopIconKeys.Success);
            var beforeThemeChange = Render(live, 16);
            inkState.Ink = DesktopThemeCatalog.Dark.Ink;
            var afterThemeChange = Render(live, 16);
            Check(!beforeThemeChange.SequenceEqual(afterThemeChange),
                "Existing native geometry pixels must follow the host's in-place brush update.");
            Check(live.Foreground is SolidColorBrush ink && ink.Color == DesktopThemeCatalog.Dark.Ink,
                "An existing icon must adopt a later theme without recreation.");

            var paths = new DesktopProductPaths(Path.Combine(ownedRoot, "app"), Path.Combine(ownedRoot, "data"),
                Path.Combine(ownedRoot, "user"), Path.Combine(ownedRoot, "projects"),
                Path.Combine(ownedRoot, "models"), Path.Combine(ownedRoot, "tools"), Path.Combine(ownedRoot, "shared"));
            var appPack = Path.GetFullPath(Path.Combine(paths.InstallDirectory, "Resources", DesktopIcons.PackRelativeDirectory));
            var sharedPack = Path.GetFullPath(Path.Combine(paths.SharedDirectory, DesktopIcons.PackRelativeDirectory));
            Directory.CreateDirectory(appPack); Directory.CreateDirectory(sharedPack);
            var appIndex = Path.Combine(appPack, "index.ini"); var sharedIndex = Path.Combine(sharedPack, "index.ini");
            File.WriteAllText(appIndex, baseline);
            File.WriteAllText(sharedIndex, baseline.Replace("label=Succeeded", "label=Fixture success", StringComparison.Ordinal));
            Check(DesktopIcons.GetPackStatus(paths).Directory == sharedPack &&
                AutomationProperties.GetName(DesktopIcons.Create(DesktopIconKeys.Success, productPaths: paths)) == "Fixture success",
                "Current shared product paths must supply dynamic metadata and take precedence over application output.");
            File.Delete(sharedIndex);
            Check(DesktopIcons.GetPackStatus(paths).Directory == appPack,
                "An absent optional shared pack must fall back to the current application resource path.");

            void Reject(string malformed, string message)
            {
                File.WriteAllText(sharedIndex, malformed);
                var selected = DesktopIcons.GetPackStatus(paths);
                Check(selected.Directory == appPack && selected.Diagnostic is { Length: > 0 }, message);
            }
            Reject("[pack]\ncontract=pegasus.cerebral.chat-icons/1", "Incomplete packs must report a diagnostic and use the valid application pack.");
            Reject(baseline + "\n[status.success]\nlabel=duplicate", "Duplicate sections must reject the optional pack.");
            Reject(baseline.Replace("label=Succeeded", "label=Succeeded\nlabel=duplicate", StringComparison.Ordinal),
                "Duplicate properties must reject the optional pack.");
            Reject(baseline.Replace("svg=svg/ic_fluent_checkmark_circle_20_regular.svg", "svg=svg/../escaped.svg", StringComparison.Ordinal),
                "Source metadata cannot escape the selected pack.");
            var geometryStart = baseline.IndexOf("geometry=", StringComparison.Ordinal);
            var geometryEnd = baseline.IndexOf('\n', geometryStart);
            var malformedGeometry = baseline[..geometryStart] + "geometry=M0 0L100000 100000Z" + baseline[geometryEnd..];
            Reject(malformedGeometry, "External geometry must stay within its design canvas.");
            Reject(baseline[..geometryStart] + "geometry=garbage" + baseline[geometryEnd..],
                "Invalid geometry syntax must degrade with a diagnostic.");
            Reject(baseline[..geometryStart] + "geometry=M0 0L20 20" + baseline[geometryEnd..],
                "A path without any filled area cannot hide a status icon.");
            Reject(baseline[..geometryStart] + "geometry=M0 0L1e999 1e999Z" + baseline[geometryEnd..],
                "Nonfinite geometry input must reject with a diagnostic.");
            Reject(new string('x', 128 * 1024 + 1), "Oversized optional packs must reject before unbounded parsing.");
            File.WriteAllText(sharedIndex, baseline);
            Check(DesktopIcons.GetPackStatus(paths).Directory == sharedPack && DesktopIcons.GetPackStatus(paths).Diagnostic is null,
                "Corrected external pack metadata must invalidate cached rejection and restore shared selection.");
            File.Delete(sharedIndex); File.Delete(appIndex);
            Check(DesktopIcons.GetPackStatus(paths).Diagnostic is null,
                "Absent optional directories must not manufacture a failure diagnostic.");
            var badSize = false; try { _ = DesktopIcons.Create(DesktopIconKeys.Success, double.NaN); } catch (ArgumentOutOfRangeException) { badSize = true; }
            Check(badSize, "Nonfinite icon sizes must reject explicitly.");
            var badKey = false; try { _ = DesktopIcons.Create("../unknown"); } catch (ArgumentException) { badKey = true; }
            Check(badKey, "Unknown semantic keys must reject without becoming paths.");
        }
        finally
        {
            if (hadBrush) app.Resources[DesktopThemeResourceKeys.TextBrush] = priorBrush;
            else app.Resources.Remove(DesktopThemeResourceKeys.TextBrush);
            var prefix = Path.TrimEndingDirectorySeparator(tempParent) + Path.DirectorySeparatorChar;
            if (!ownedRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped the temporary parent.");
            if (Directory.Exists(ownedRoot)) Directory.Delete(ownedRoot, recursive: true);
        }
        return checks;
    }

    internal static void WritePreview(string file)
    {
        var width = 720; var rowHeight = 38; var height = 52 + DesktopIcons.Definitions.Count * rowHeight;
        var sheet = new Grid { Width = width, Height = height, Background = Brushes.White };
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        sheet.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        var heading = new TextBlock { Text = "Native WPF chat/status icons  •  14 / 16 / 20 / 32 px", FontSize = 16,
            Foreground = Brushes.Black, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumnSpan(heading, 3); sheet.Children.Add(heading);
        var row = 1;
        foreach (var definition in DesktopIcons.Definitions)
        {
            sheet.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });
            var label = new TextBlock { Text = definition.Key, Foreground = Brushes.Black, FontSize = 13,
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row); sheet.Children.Add(label);
            var column = 1;
            foreach (var theme in new[] { DesktopThemeCatalog.Light, DesktopThemeCatalog.Dark })
            {
                var cell = new Border { Background = new SolidColorBrush(theme.Surface) };
                cell.Resources[DesktopThemeResourceKeys.TextBrush] = new SolidColorBrush(theme.Ink);
                var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var size in new[] { 14, 16, 20, 32 })
                {
                    var icon = DesktopIcons.Create(definition.Key, size); icon.Margin = new Thickness(18, 0, 8, 0);
                    icons.Children.Add(icon);
                }
                cell.Child = icons; Grid.SetRow(cell, row); Grid.SetColumn(cell, column++); sheet.Children.Add(cell);
            }
            row++;
        }
        sheet.Measure(new Size(width, height)); sheet.Arrange(new Rect(0, 0, width, height)); sheet.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(sheet);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }

    private sealed class InkState : INotifyPropertyChanged
    {
        private Color ink;
        public Color Ink
        {
            get => ink;
            set { ink = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ink))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static byte[] Render(FrameworkElement icon, int size)
    {
        icon.Measure(new Size(size, size)); icon.Arrange(new Rect(0, 0, size, size)); icon.UpdateLayout();
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(icon);
        var pixels = new byte[size * size * 4]; bitmap.CopyPixels(pixels, size * 4, 0); return pixels;
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value) { var x = value / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        var a = Luminance(first); var b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
