using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Cerebral.Desktop;

/// <summary>Keep ordinary WPF windows usable within the current monitor's work area.</summary>
public static class WindowSizing
{
    private static readonly ConditionalWeakTable<Window, Action<bool>> AttachedFits = new();

    /// <summary>Reapplies the attached current-monitor clamp after late geometry hydration.</summary>
    /// <remarks>Attach first. Calls after Closed have no effect, including late asynchronous hydration callbacks.</remarks>
    public static void FitCurrentMonitor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (AttachedFits.TryGetValue(window, out var fit)) fit(true);
    }

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (AttachedFits.TryGetValue(window, out _)) return;
        window.UseLayoutRounding = true;
        // Derived windows can set their own minimum after their base constructor attaches this helper.
        Size? preferredMinimum = null;
        var closed = false;
        var queued = false;
        var pendingVisibility = false;
        var currentMonitor = IntPtr.Zero;
        void Fit(bool keepVisible)
        {
            if (closed) return;
            preferredMinimum ??= new Size(window.MinWidth, window.MinHeight);
            var minimum = preferredMinimum.Value;
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = handle == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(handle, 2);
            currentMonitor = monitor;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
            if (monitor == IntPtr.Zero || source?.CompositionTarget is null || !GetMonitorInfoW(monitor, ref info))
            {
                FitWithin(window, SystemParameters.WorkArea.Size, minimum);
                return;
            }
            // Native monitor bounds are pixels. WPF owns the conversion to logical layout units.
            var logical = source.CompositionTarget.TransformFromDevice.Transform(
                new Vector(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            FitWithin(window, new Size(logical.X, logical.Y), minimum);
            if (!keepVisible || window.WindowState != WindowState.Normal || !GetWindowRect(handle, out var bounds)) return;
            var x = Math.Clamp(bounds.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - (bounds.Right - bounds.Left)));
            var y = Math.Clamp(bounds.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - (bounds.Bottom - bounds.Top)));
            // Position only; preserve WPF's size, focus and Z order. User dragging within a monitor remains unrestricted.
            if (x != bounds.Left || y != bounds.Top) SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0215);
        }
        void QueueFit(bool keepVisible)
        {
            if (closed) return;
            pendingVisibility |= keepVisible;
            if (queued) return;
            queued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                queued = false;
                var reposition = pendingVisibility;
                pendingVisibility = false;
                Fit(reposition);
            }));
        }
        AttachedFits.Add(window, Fit);
        window.SourceInitialized += (_, _) => Fit(false);
        window.Loaded += (_, _) => QueueFit(true);
        window.DpiChanged += (_, _) => QueueFit(true);
        window.LocationChanged += (_, _) =>
        {
            if (closed || !window.IsLoaded) return;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero && MonitorFromWindow(handle, 2) != currentMonitor) QueueFit(false);
        };
        window.Closed += (_, _) => { closed = true; AttachedFits.Remove(window); };
    }

    internal static object Inspect(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Window DPI inspection requires an initialized HWND.");
        var context = GetWindowDpiAwarenessContext(handle);
        var awareness = AreDpiAwarenessContextsEqual(context, new IntPtr(-4)) ? "PerMonitorV2"
            : AreDpiAwarenessContextsEqual(context, new IntPtr(-3)) ? "PerMonitor"
            : AreDpiAwarenessContextsEqual(context, new IntPtr(-2)) ? "SystemAware"
            : "Unaware";
        return new { awareness, windowDpi = GetDpiForWindow(handle), windowShown = window.IsVisible };
    }

    // Shared calculation seam for deterministic small-work-area checks; does not alter the machine DPI.
    internal static void FitWithin(Window window, Size available, Size preferredMinimum)
    {
        if (!double.IsFinite(available.Width) || !double.IsFinite(available.Height) || available.Width <= 0 || available.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(available));
        window.MinWidth = Math.Min(preferredMinimum.Width, available.Width);
        window.MinHeight = Math.Min(preferredMinimum.Height, available.Height);
        if (window.WindowState != WindowState.Normal) return;
        if (double.IsFinite(window.Width)) window.Width = Math.Min(window.Width, available.Width);
        if (double.IsFinite(window.Height)) window.Height = Math.Min(window.Height, available.Height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr left, IntPtr right);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, int flags);
}
