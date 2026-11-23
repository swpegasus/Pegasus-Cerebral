using System.Windows;
using Cerebral.Desktop.Plugins;

namespace Cerebral.WindowWatchPlugin;

public sealed class WindowWatchPlugin : DesktopPluginBase
{
    private WatchWindow? window;
    private bool disposed;

    public override IReadOnlyList<DesktopAction> ComposerActions =>
        [new("open-window-watch", "Window Watch", OpenAsync, "Analyze a selected window with a local vision model")];

    private Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed) return Task.CompletedTask;

        if (window is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return Task.CompletedTask;
        }

        var opened = new WatchWindow();
        window = opened;
        opened.Closed += (_, _) =>
        {
            if (ReferenceEquals(window, opened)) window = null;
        };
        opened.Show();
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        disposed = true;
        window?.Close();
        base.Dispose();
    }
}
