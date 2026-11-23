using System.Windows;

namespace Cerebral.Desktop;

/// <summary>Temporarily realizes an existing native window for background rendering without taking focus.</summary>
public sealed class DesktopBackgroundRenderScope : IDisposable
{
    private readonly Window window;
    private readonly bool restore;
    private readonly bool wasVisible;
    private readonly WindowState state;
    private readonly double opacity, left, top;
    private readonly bool showActivated, showInTaskbar;
    private bool disposed;

    private DesktopBackgroundRenderScope(Window window)
    {
        window.Dispatcher.VerifyAccess();
        this.window = window;
        wasVisible = window.IsVisible;
        state = window.WindowState;
        restore = !wasVisible || state == WindowState.Minimized;
        opacity = window.Opacity; left = window.Left; top = window.Top;
        showActivated = window.ShowActivated; showInTaskbar = window.ShowInTaskbar;
        if (!restore) return;
        try
        {
            // Keep the actual retained visual tree and HWND. Hiding a window makes its controls
            // unavailable to native Invoke, so realize it off screen only for this operation.
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Opacity = 0; window.Left = -20_000; window.Top = -20_000;
            window.WindowState = WindowState.Normal;
            window.Show(); window.UpdateLayout();
        }
        catch { Dispose(); throw; }
    }

    public static DesktopBackgroundRenderScope Begin(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new(window);
    }

    public void Dispose()
    {
        window.Dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        if (!restore) return;
        window.Hide();
        window.Left = left; window.Top = top;
        window.Opacity = opacity; window.ShowActivated = showActivated;
        window.ShowInTaskbar = showInTaskbar; window.WindowState = state;
        if (wasVisible)
        {
            // Restoring a minimized window must not activate it.
            window.ShowActivated = false;
            try { window.Show(); }
            finally { window.ShowActivated = showActivated; }
        }
    }
}
