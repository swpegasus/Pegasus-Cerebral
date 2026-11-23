using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cerebral.WindowWatchPlugin;

internal sealed record WindowTarget(nint Handle, int ProcessId, DateTime ProcessStartUtc, string Title)
{
    public string Label => $"{Title} (PID {ProcessId})";

    internal static IReadOnlyList<WindowTarget> List(nint excluded)
    {
        var windows = new List<WindowTarget>();
        EnumWindows((handle, _) =>
        {
            if (windows.Count >= 256) return false;
            if (handle != excluded && IsWindowVisible(handle) && !IsCloaked(handle) &&
                TryRead(handle) is { } target)
                windows.Add(target);
            return true;
        }, 0);
        return windows.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static WindowTarget? TryRead(nint handle)
    {
        var title = new StringBuilder(257);
        if (GetWindowText(handle, title, title.Capacity) == 0) return null;
        GetWindowThreadProcessId(handle, out var processId);
        if (processId is 0 or > int.MaxValue) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new(handle, (int)processId, process.StartTime.ToUniversalTime(), title.ToString());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    internal bool StillSelected()
    {
        if (!IsWindow(Handle)) return false;
        GetWindowThreadProcessId(Handle, out var pid);
        if (pid != ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            return process.StartTime.ToUniversalTime() == ProcessStartUtc;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    internal bool CanSample()
    {
        if (!IsWindowVisible(Handle) || IsIconic(Handle) || IsCloaked(Handle)) return false;
        if (GetWindowDisplayAffinity(Handle, out var affinity) && affinity != 0)
            throw new InvalidOperationException("The selected window restricts capture.");
        if (!GetWindowRect(Handle, out var bounds)) return false;
        var width = (long)bounds.Right - bounds.Left;
        var height = (long)bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return false;
        if (width > 4096 || height > 4096)
            throw new InvalidOperationException("Resize the selected window to at most 4096 pixels per side.");
        return true;
    }

    internal bool TryOutlineBounds(out (int X, int Y, int Width, int Height) bounds)
    {
        bounds = default;
        if (!IsWindowVisible(Handle) || IsIconic(Handle) || IsCloaked(Handle) ||
            !GetWindowRect(Handle, out var rect))
            return false;
        bounds = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool IsCloaked(nint handle) =>
        DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left, Top, Right, Bottom;
    }

    private delegate bool EnumProc(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}

// A destruction notification invalidates the chosen handle, even within the same process.
internal sealed class WindowSelection : IDisposable
{
    private readonly WinEvent callback;
    private nint hook;

    internal WindowSelection(WindowTarget target)
    {
        Target = target;
        callback = (_, _, handle, objectId, childId, _, _) =>
        {
            if (handle == target.Handle && objectId == 0 && childId == 0) Destroyed = true;
        };
        const uint objectDestroy = 0x8001;
        hook = SetWinEventHook(objectDestroy, objectDestroy, 0, callback, (uint)target.ProcessId, 0, 0);
        if (hook == 0)
            throw new InvalidOperationException("Windows could not track the lifetime of the selected window.");
    }

    internal WindowTarget Target { get; }
    internal bool Destroyed { get; private set; }

    public void Dispose()
    {
        if (hook == 0) return;
        UnhookWinEvent(hook);
        hook = 0;
        GC.KeepAlive(callback);
    }

    private delegate void WinEvent(nint hook, uint type, nint handle, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
