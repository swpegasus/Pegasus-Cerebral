using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Cerebral.AiDictationPlugin;

/// <summary>Only this plugin-owned HWND is styled or moved. No foreground/focus API is used.</summary>
internal sealed class NonActivatingDictationWindow : IDisposable
{
    internal const int MouseActivateMessage = 0x0021;
    internal const long RequiredStyles = 0x08000000L | 0x00000080L; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
    internal const uint MoveFlags = 0x0010 | 0x0004 | 0x0001; // NOACTIVATE | NOZORDER | NOSIZE
    internal const uint StyleFlags = MoveFlags | 0x0002 | 0x0020; // NOMOVE | FRAMECHANGED
    private readonly Window window;
    private readonly FrameworkElement dragHandle;
    private readonly IDictationWindowNative native;
    private readonly Action<Exception>? reportFailure;
    private HwndSource? source;
    private nint handle;
    private bool dragging, disposed;
    private NativePoint initialCursor;
    private NativeRect initialBounds;

    internal NonActivatingDictationWindow(Window window, FrameworkElement dragHandle, IDictationWindowNative? native = null, Action<Exception>? reportFailure = null)
    {
        this.window = window; this.dragHandle = dragHandle; this.native = native ?? new DictationWindowNative(); this.reportFailure = reportFailure;
        window.ShowActivated = false; window.Focusable = false;
        dragHandle.MouseLeftButtonDown += BeginDrag; dragHandle.MouseMove += MoveDrag;
        dragHandle.MouseLeftButtonUp += EndDrag; dragHandle.LostMouseCapture += LostCapture;
    }

    internal void Prepare()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("The microphone window has no native source.");
        source.AddHook(Hook);
        Configure(native, handle);
    }

    internal static void Configure(IDictationWindowNative native, nint handle)
    {
        if (handle == 0) throw new ArgumentException("A plugin-owned window handle is required.", nameof(handle));
        native.SetStyles(handle, native.GetStyles(handle) | RequiredStyles);
        if ((native.GetStyles(handle) & RequiredStyles) != RequiredStyles)
            throw new InvalidOperationException("The microphone window could not preserve editor focus.");
        native.Position(handle, 0, 0, StyleFlags);
    }

    internal static nint MouseActivation(int message, ref bool handled)
    {
        if (message != MouseActivateMessage) return 0;
        handled = true; return 3; // MA_NOACTIVATE preserves the mouse click; MA_NOACTIVATEANDEAT would lose it.
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled) => MouseActivation(message, ref handled);
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (disposed || handle == 0 || !native.TryCursor(out initialCursor) || !native.TryBounds(handle, out initialBounds)) return;
        dragging = dragHandle.CaptureMouse(); e.Handled = dragging;
    }
    private void MoveDrag(object sender, MouseEventArgs e)
    {
        if (!dragging || e.LeftButton != MouseButtonState.Pressed || !native.TryCursor(out var cursor)) return;
        var x = initialBounds.Left + cursor.X - initialCursor.X;
        var y = initialBounds.Top + cursor.Y - initialCursor.Y;
        var position = ClampPosition(x, y, initialBounds.Right - initialBounds.Left, native.VirtualScreen);
        try { native.Position(handle, position.X, position.Y, MoveFlags); }
        catch (Win32Exception error)
        {
            dragHandle.ReleaseMouseCapture(); dragging = false; reportFailure?.Invoke(error);
        }
        e.Handled = true;
    }
    internal static NativePoint ClampPosition(int x, int y, int width, NativeRect screen)
    {
        // Keep a usable portion of the header reachable, including monitors at negative coordinates.
        var minimumX = screen.Left - Math.Max(0, width - 72);
        var maximumX = Math.Max(minimumX, screen.Right - 72);
        return new(Math.Clamp(x, minimumX, maximumX), Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - 40)));
    }
    private void EndDrag(object sender, MouseButtonEventArgs e) { if (dragging) dragHandle.ReleaseMouseCapture(); dragging = false; }
    private void LostCapture(object sender, MouseEventArgs e) => dragging = false;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; if (dragging) dragHandle.ReleaseMouseCapture(); dragging = false;
        dragHandle.MouseLeftButtonDown -= BeginDrag; dragHandle.MouseMove -= MoveDrag;
        dragHandle.MouseLeftButtonUp -= EndDrag; dragHandle.LostMouseCapture -= LostCapture;
        source?.RemoveHook(Hook); source = null; handle = 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint(int x, int y) { public int X = x, Y = y; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect { public int Left, Top, Right, Bottom; }
internal interface IDictationWindowNative
{
    long GetStyles(nint handle);
    void SetStyles(nint handle, long value);
    void Position(nint handle, int x, int y, uint flags);
    bool TryCursor(out NativePoint point);
    bool TryBounds(nint handle, out NativeRect rect);
    NativeRect VirtualScreen { get; }
}

internal sealed class DictationWindowNative : IDictationWindowNative
{
    private const int ExtendedStyle = -20;
    public long GetStyles(nint handle)
    {
        Marshal.SetLastPInvokeError(0);
        var value = IntPtr.Size == 8 ? GetWindowLongPtr(handle, ExtendedStyle) : (nint)GetWindowLong(handle, ExtendedStyle);
        if (value == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return value.ToInt64();
    }
    public void SetStyles(nint handle, long value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8 ? SetWindowLongPtr(handle, ExtendedStyle, (nint)value) : (nint)SetWindowLong(handle, ExtendedStyle, unchecked((int)value));
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    public void Position(nint handle, int x, int y, uint flags)
    {
        if (!SetWindowPos(handle, 0, x, y, 0, 0, flags)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    public bool TryCursor(out NativePoint point) => GetCursorPos(out point);
    public bool TryBounds(nint handle, out NativeRect rect) => GetWindowRect(handle, out rect);
    public NativeRect VirtualScreen => new() { Left = GetSystemMetrics(76), Top = GetSystemMetrics(77),
        Right = GetSystemMetrics(76) + GetSystemMetrics(78), Bottom = GetSystemMetrics(77) + GetSystemMetrics(79) };

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint handle, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
