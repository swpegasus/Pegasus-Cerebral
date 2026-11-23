using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows;

namespace Cerebral.ResourcePausePlugin;

public sealed partial class ResourcePausePlugin
{
    private readonly Dictionary<Button, Action> statusBalloons = [];

    private readonly HashSet<Action> statusRefreshStops = [];

    // Status may change through another owner. Observe while mounted, and stop all reads on unload/disposal.
    private void AttachStatusRefresh(FrameworkElement view, Func<CancellationToken, Task> refresh)
    {
        CancellationTokenSource? viewLifetime = null;
        DispatcherTimer? timer = null;
        EventHandler? tick = null;
        void Stop()
        {
            var activeTimer = timer;
            timer = null;
            if (activeTimer is not null)
            {
                activeTimer.Stop();
                if (tick is not null) activeTimer.Tick -= tick;
            }
            tick = null;
            var activeLifetime = viewLifetime;
            viewLifetime = null;
            statusRefreshStops.Remove(Stop);
            if (activeLifetime is null) return;
            cleanup.Attempt(activeLifetime.Cancel);
            cleanup.Attempt(activeLifetime.Dispose);
        }
        void Start()
        {
            if (CallbacksRetired || viewLifetime is not null) return;
            viewLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = viewLifetime.Token;
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            tick = async (_, _) =>
            {
                if (token.IsCancellationRequested) { Stop(); return; }
                await refresh(token);
            };
            timer.Tick += tick;
            statusRefreshStops.Add(Stop);
            timer.Start();
            _ = refresh(token);
        }
        view.Loaded += (_, _) => Start();
        view.Unloaded += (_, _) => Stop();
    }

    private void DisposeStatusRefresh()
    {
        foreach (var stop in statusRefreshStops.ToArray()) cleanup.Attempt(stop);
        statusRefreshStops.Clear();
    }


    private void ShowStatusBalloon(Button button, string message)
    {
        if (statusBalloons.TryGetValue(button, out var closePrevious)) closePrevious();
        var previous = button.ToolTip;
        var balloon = new ToolTip { Content = message, PlacementTarget = button, Placement = PlacementMode.Top, StaysOpen = true, IsOpen = true };
        button.ToolTip = balloon;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        EventHandler? tick = null;
        RoutedEventHandler? unloaded = null;
        void Close()
        {
            timer.Stop(); timer.Tick -= tick; balloon.IsOpen = false;
            button.Unloaded -= unloaded;
            if (ReferenceEquals(button.ToolTip, balloon)) button.ToolTip = previous;
            statusBalloons.Remove(button);
        }
        tick = (_, _) => Close(); unloaded = (_, _) => Close();
        statusBalloons.Add(button, Close);
        button.Unloaded += unloaded; timer.Tick += tick; timer.Start();
    }

    private void ClearStatusBalloons()
    {
        foreach (var close in statusBalloons.Values.ToArray()) cleanup.Attempt(close);
    }
}
