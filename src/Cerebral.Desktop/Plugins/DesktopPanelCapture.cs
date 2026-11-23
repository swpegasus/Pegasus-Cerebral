using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cerebral.Desktop.Plugins;

/// <summary>Renders one already-mounted WPF contribution into a bounded PNG for durable attachment admission.</summary>
public static class DesktopPanelCapture
{
    public const int MaximumDimension = 1024;

    public static byte[] CapturePng(FrameworkElement view)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.Dispatcher.VerifyAccess();
        var width = Math.Max(view.ActualWidth, view.DesiredSize.Width);
        var height = Math.Max(view.ActualHeight, view.DesiredSize.Height);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException("The mounted plugin view has no renderable layout yet.");
        var scale = Math.Min(1d, MaximumDimension / Math.Max(width, height));
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
        var source = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        source.Render(view);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Background(view), null, new Rect(0, 0, pixelWidth, pixelHeight));
            context.DrawImage(source, new Rect(0, 0, pixelWidth, pixelHeight));
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }

    private static Brush Background(DependencyObject view)
    {
        DependencyObject? current = view;
        while (current is not null)
        {
            var brush = current switch
            {
                Border { Background: not null } border => border.Background,
                Panel { Background: not null } panel => panel.Background,
                Control { Background: not null } control => control.Background,
                _ => null
            };
            if (brush is not null) return brush;
            current = VisualTreeHelper.GetParent(current);
        }
        return SystemColors.WindowBrush;
    }
}
