using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cerebral.Desktop;

/// <summary>Hidden task-owned WPF HWND checks; no Worker, model, network or preference writes.</summary>
public static class ExistingWorkerDesktopPresentationChecks
{
    public static int Verify()
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("Create the check Application before verifying attached-window presentation.");
        application.Dispatcher.VerifyAccess();
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }

        var processMode = RenderOptions.ProcessRenderMode;
        var shutdownMode = application.ShutdownMode;
        var mainWindow = application.MainWindow;
        Window? attached = null, normal = null;
        HwndSource? attachedSource = null, normalSource = null;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            attached = new Window { Title = "Owned existing-Worker presentation check", ShowActivated = false, ShowInTaskbar = false };
            normal = new Window { Title = "Owned ordinary presentation control", ShowActivated = false, ShowInTaskbar = false };
            var attachedInterop = new WindowInteropHelper(attached);
            var normalInterop = new WindowInteropHelper(normal);
            Check(attachedInterop.Handle == IntPtr.Zero && normalInterop.Handle == IntPtr.Zero,
                "Constructing the two hidden controls does not create their HWNDs.");

            var normalHandle = normalInterop.EnsureHandle();
            normalSource = HwndSource.FromHwnd(normalHandle)
                ?? throw new InvalidOperationException("The ordinary hidden control has no native source.");
            var normalMode = normalSource.CompositionTarget.RenderMode;
            var wrongThreadRefused = Task.Run(() =>
            {
                try { ExistingWorkerDesktopPresentation.Configure(attached); return false; }
                catch (InvalidOperationException) { return true; }
            }).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(wrongThreadRefused && attachedInterop.Handle == IntPtr.Zero,
                "Configuration refuses another thread before creating an attached HWND.");

            ExistingWorkerDesktopPresentation.Configure(attached);
            Check(attachedInterop.Handle == IntPtr.Zero,
                "Deferred configuration leaves HWND creation to the owning caller.");
            var attachedHandle = attachedInterop.EnsureHandle();
            attachedSource = HwndSource.FromHwnd(attachedHandle)
                ?? throw new InvalidOperationException("The attached hidden control has no native source.");
            Check(attachedHandle != normalHandle && attachedSource.Handle == attachedHandle && normalSource.Handle == normalHandle &&
                attachedSource.Dispatcher == attached.Dispatcher && normalSource.Dispatcher == normal.Dispatcher,
                "Each hidden Window retains its own distinct HWND and dispatcher-bound presentation source.");
            Check(attachedSource.CompositionTarget.RenderMode == RenderMode.SoftwareOnly,
                "The attached source selects software composition during SourceInitialized.");
            Check(normalSource.CompositionTarget.RenderMode == normalMode,
                "Configuring the attached Window preserves the ordinary Window's rendering mode.");
            Check(RenderOptions.ProcessRenderMode == processMode,
                "Deferred attached-window configuration preserves the process rendering mode.");
            Check(!attached.IsVisible && !normal.IsVisible,
                "Initializing the two sources does not show either Window.");

            ExistingWorkerDesktopPresentation.Configure(normal);
            Check(normalInterop.Handle == normalHandle && ReferenceEquals(HwndSource.FromHwnd(normalHandle), normalSource),
                "Immediate configuration reuses the preinitialized Window's own source.");
            Check(normalSource.CompositionTarget.RenderMode == RenderMode.SoftwareOnly &&
                attachedSource.CompositionTarget.RenderMode == RenderMode.SoftwareOnly,
                "The preinitialized source changes independently while the first attached source remains configured.");
            Check(RenderOptions.ProcessRenderMode == processMode && !attached.IsVisible && !normal.IsVisible,
                "Immediate configuration preserves process rendering mode and both hidden lifetimes.");
        }
        finally
        {
            try { attached?.Close(); }
            finally
            {
                try { normal?.Close(); }
                finally
                {
                    application.MainWindow = mainWindow;
                    application.ShutdownMode = shutdownMode;
                }
            }
        }

        Check(attachedSource is { IsDisposed: true } && normalSource is { IsDisposed: true },
            "Closing the two task-owned hidden Windows disposes both native presentation sources.");
        Check(ReferenceEquals(application.MainWindow, mainWindow) && application.ShutdownMode == shutdownMode &&
            RenderOptions.ProcessRenderMode == processMode,
            "Verification restores the caller's Application lifetime and preserves process rendering policy.");
        Console.WriteLine($"Existing-worker presentation: {checks} hidden native-window checks passed; no Worker/model/network or preference writes.");
        return checks;
    }
}
