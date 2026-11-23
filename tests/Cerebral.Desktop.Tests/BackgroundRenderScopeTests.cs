using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class BackgroundRenderScopeTests
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
        var count = 0;
        var label = new TextBlock { Text = "Count: 0", Foreground = Brushes.Black, FontSize = 24 };
        var button = new Button { Content = "Increment" };
        button.Click += (_, _) => label.Text = "Count: " + ++count;
        var panel = new StackPanel { Background = Brushes.White };
        panel.Children.Add(label); panel.Children.Add(button);
        var window = new Window
        {
            Width = 480, Height = 320, Left = -19_000, Top = -19_000,
            ShowActivated = false, ShowInTaskbar = false, Content = panel
        };
        void Drain()
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        try
        {
            var foreground = GetForegroundWindow();
            using (DesktopBackgroundRenderScope.Begin(window))
            {
                Drain();
                Check(window.IsVisible && window.Opacity == 0, "A hidden window must realize without visible pixels.");
                Check(GetForegroundWindow() == foreground, "Background layout must not take the foreground.");
                Check(button.IsVisible && button.IsEnabled, "Actual native controls must be available during the scope.");
                var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                Check(provider is not null, "The retained button must expose native Invoke.");
                provider!.Invoke(); Drain();
                Check(count == 1 && label.Text == "Count: 1", "Native Invoke must execute the real Click handler.");
                var png = DesktopPanelCapture.CapturePng(panel);
                using var stream = new System.IO.MemoryStream(png);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Check(image.PixelWidth > 32 && image.PixelHeight > 32, "The actual mounted view must render a bounded PNG.");
                var stride = image.PixelWidth * 4; var pixels = new byte[stride * image.PixelHeight];
                image.CopyPixels(pixels, stride, 0);
                var first = pixels.AsSpan(0, 4).ToArray();
                Check(Enumerable.Range(1, pixels.Length / 4 - 1).Any(index =>
                    !pixels.AsSpan(index * 4, 4).SequenceEqual(first)), "Rendered native content must not be blank.");
            }
            Check(!window.IsVisible, "Disposal must return a tray window to hidden.");
            Check(window.Opacity == 1 && window.Left == -19_000 && window.Top == -19_000,
                "Disposal must restore the prior placement and opacity.");
            Check(GetForegroundWindow() == foreground, "Disposal must not steal focus.");
            try
            {
                using var scope = DesktopBackgroundRenderScope.Begin(window);
                throw new System.IO.IOException("Fixture renderer failure.");
            }
            catch (System.IO.IOException) { }
            Check(!window.IsVisible && window.Opacity == 1, "Failure unwinding must restore the hidden window.");
            window.Show(); Drain();
            foreground = GetForegroundWindow();
            using (DesktopBackgroundRenderScope.Begin(window))
                Check(window.IsVisible && window.Opacity == 1 && window.Left == -19_000,
                    "An already visible window must retain its presentation.");
            Check(window.IsVisible && GetForegroundWindow() == foreground,
                "Visible-window operations must preserve visibility and foreground.");
            window.WindowState = WindowState.Minimized; Drain();
            foreground = GetForegroundWindow();
            using (DesktopBackgroundRenderScope.Begin(window))
            {
                Drain();
                Check(window.WindowState == WindowState.Normal && window.Opacity == 0,
                    "A minimized window must render in a temporary invisible normal state.");
                Check(GetForegroundWindow() == foreground, "Minimized layout must not activate the window.");
            }
            Drain();
            Check(window.IsVisible && window.WindowState == WindowState.Minimized && window.Opacity == 1,
                "Disposal must restore minimized state and opacity.");
            Check(GetForegroundWindow() == foreground, "Minimized restoration must preserve foreground.");
        }
        finally { window.Close(); Drain(); }
        return checks;
    }
}
