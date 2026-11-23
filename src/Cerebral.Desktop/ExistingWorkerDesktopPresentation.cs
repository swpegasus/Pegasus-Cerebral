using System.Windows;
using System.Windows.Interop;

namespace Cerebral.Desktop;

/// <summary>
/// Development presentation fallback for an explicitly attached existing Worker window.
/// Software composition can increase CPU use. Remove this fallback when the default renderer
/// passes native-window acceptance for the attachment path.
/// </summary>
internal static class ExistingWorkerDesktopPresentation
{
    internal static void Configure(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyToOwnSource(window);
            return;
        }

        void Unhook()
        {
            window.SourceInitialized -= Initialized;
            window.Closed -= Closed;
        }
        void Initialized(object? sender, EventArgs args)
        {
            try { ApplyToOwnSource(window); }
            finally { Unhook(); }
        }
        void Closed(object? sender, EventArgs args) => Unhook();

        window.SourceInitialized += Initialized;
        window.Closed += Closed;
    }

    private static void ApplyToOwnSource(Window window)
    {
        window.Dispatcher.VerifyAccess();
        // Reading Handle does not create or show a window. A hidden initialized Window can
        // have no RootVisual yet, so ownership comes from its own WindowInteropHelper handle.
        var handle = new WindowInteropHelper(window).Handle;
        var source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
        if (source is null || source.IsDisposed || source.Handle != handle || source.Dispatcher != window.Dispatcher)
            throw new InvalidOperationException("The attached window has no current presentation source of its own.");
        var target = source.CompositionTarget
            ?? throw new InvalidOperationException("The attached window has no current composition target.");
        target.RenderMode = RenderMode.SoftwareOnly;
    }
}
