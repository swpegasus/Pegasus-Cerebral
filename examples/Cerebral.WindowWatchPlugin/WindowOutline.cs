using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cerebral.WindowWatchPlugin;

internal sealed class WindowOutline : Window
{
    private (int X, int Y, int Width, int Height) previousBounds;

    internal WindowOutline()
    {
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        Width = 100;
        Height = 100;
        Content = new Border { BorderThickness = new Thickness(3), BorderBrush = Brushes.DeepSkyBlue };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        const int extendedStyle = -20;
        const long clickThrough = 0x20, noActivate = 0x08000000, toolWindow = 0x80;
        SetWindowLongPtr(handle, extendedStyle,
            (nint)(GetWindowLongPtr(handle, extendedStyle).ToInt64() | clickThrough | noActivate | toolWindow));
    }

    internal void Track(WindowTarget? target)
    {
        if (target is null || !target.TryOutlineBounds(out var bounds))
        {
            if (IsVisible) Hide();
            return;
        }
        var wasVisible = IsVisible;
        if (!wasVisible) Show();
        var handle = new WindowInteropHelper(this).Handle;
        const uint nextInZOrder = 2;
        if (wasVisible && previousBounds == bounds && GetWindow(target.Handle, nextInZOrder) == handle)
            return;

        const int padding = 4;
        const uint noActivate = 0x10, noOwnerZOrder = 0x200;
        if (SetWindowPos(handle, target.Handle, bounds.X - padding, bounds.Y - padding,
            bounds.Width + padding * 2, bounds.Height + padding * 2, noActivate | noOwnerZOrder))
            previousBounds = bounds;
        else
            Hide();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
