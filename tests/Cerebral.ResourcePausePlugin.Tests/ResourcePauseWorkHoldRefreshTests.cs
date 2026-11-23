using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin.Tests;

public static partial class Program
{
    private static void RunWorkHoldStatusRefreshChecks()
    {
        MountedWorkHoldConvergesRecoversAndStops();
        WorkHoldConfigurationConvergesWithoutStatusMount();
        DelayedWorkHoldObservationCannotReplaceOwnerResult();
    }

    private static void MountedWorkHoldConvergesRecoversAndStops()
    {
        var current = Hold("held", 26, 2);
        var reads = 0; var concurrent = 0; var maximumConcurrent = 0;
        var fail = false; var block = false; var cancelled = false;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<DesktopServiceCommandResult> Reply(CancellationToken token)
        {
            reads++;
            maximumConcurrent = Math.Max(maximumConcurrent, Interlocked.Increment(ref concurrent));
            try
            {
                if (block)
                {
                    entered.TrySetResult(true);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; throw; }
                }
                if (fail) throw new IOException("fixture status unavailable");
                return current;
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        var host = new FixtureHost { Reply = (operation, _, token) => operation == "engine.work-hold.status"
            ? Reply(token) : throw new InvalidOperationException(operation) };
        using var plugin = Create(host);
        var view = plugin.MountContributions.OfType<DesktopMountView>()
            .Single(item => item.Descriptor.Id == "work-hold-status").CreateView();
        var status = Find<Button>(view, "ResourcePause.WorkHoldStatus");
        PumpUntil(() => Label(status) == "Captured work held");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        current = Hold("resumed", 26, 4, enginePaused: false);
        PumpUntil(() => Label(status) == "Hold all work");
        Check(maximumConcurrent == 1 && host.Operations.All(operation => operation == "engine.work-hold.status"),
            "a mounted held-work label converges after an external resume using only serialized status reads");
        current = Hold("held", 27, 2);
        PumpUntil(() => Label(status) == "Captured work held");
        Check(Indicator(status).Fill is SolidColorBrush held && held.Color == Brushes.LimeGreen.Color,
            "a newer external human hold is observed without releasing or overriding it");
        fail = true;
        PumpUntil(() => Label(status) == "Work hold attention");
        Check(Indicator(status).Fill is SolidColorBrush attention && attention.Color == Brushes.IndianRed.Color,
            "a failed background hold observation clears the stale green confirmed label");
        fail = false; current = Hold("resumed", 27, 4, enginePaused: false);
        PumpUntil(() => Label(status) == "Hold all work");
        Check(status.IsEnabled, "a later successful status observation recovers the mounted hold control");
        block = true;
        PumpUntil(() => entered.Task.IsCompleted);
        var blockedReads = reads;
        var overlapDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1200);
        PumpUntil(() => DateTime.UtcNow >= overlapDeadline);
        Check(reads == blockedReads && maximumConcurrent == 1,
            "a slow hold observation cannot accumulate overlapping timer reads");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        PumpUntil(() => cancelled && Volatile.Read(ref concurrent) == 0);
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var unloadedReads = reads;
        var unloadedDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1200);
        PumpUntil(() => DateTime.UtcNow >= unloadedDeadline);
        Check(reads == unloadedReads && Label(status) == "Hold all work",
            "unload cancels an in-flight hold read and does not display cancellation as an attention fault");
        block = false;
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        PumpUntil(() => reads > unloadedReads && Label(status) == "Hold all work");
        Check(maximumConcurrent == 1, "reloading the same hold control starts one fresh read monitor");
        plugin.Dispose();
        var disposedReads = reads;
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var disposedDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1200);
        PumpUntil(() => DateTime.UtcNow >= disposedDeadline);
        Check(reads == disposedReads, "plugin disposal stops mounted status timers and prevents later load from restarting them");
    }

    private static void WorkHoldConfigurationConvergesWithoutStatusMount()
    {
        var current = Hold("held", 26, 2);
        var host = new FixtureHost { Reply = (operation, _, _) => Task.FromResult(operation == "engine.work-hold.status"
            ? current : throw new InvalidOperationException(operation)) };
        using var plugin = Create(host);
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "work-hold").CreateView();
        var resume = Find<Button>(panel, "ResourcePause.WorkHoldResume");
        var stop = Find<Button>(panel, "ResourcePause.WorkHoldStopNow");
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        current = Hold("resumed", 26, 4, enginePaused: false);
        PumpUntil(() => !resume.IsEnabled && stop.IsEnabled);
        Check(host.Operations.All(operation => operation == "engine.work-hold.status"),
            "the standalone Work hold settings panel observes external resume without requiring the Main status mount");
        current = Hold("held", 27, 2);
        PumpUntil(() => resume.IsEnabled && !stop.IsEnabled);
        Check(resume.IsEnabled && !stop.IsEnabled && host.Operations.All(operation => operation == "engine.work-hold.status"),
            "the standalone settings panel also observes a newer held generation without a control action");
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }

    private static void DelayedWorkHoldObservationCannotReplaceOwnerResult()
    {
        foreach (var failedObservation in new[] { false, true })
        {
            var current = Hold("held", 26, 2);
            var original = current;
            var delayed = new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockNext = false; var entered = false; var returned = false;
            async Task<DesktopServiceCommandResult> Status()
            {
                if (!blockNext) return current;
                blockNext = false; entered = true;
                try { return await delayed.Task; }
                finally { returned = true; }
            }
            var host = new FixtureHost { Reply = (operation, _, _) => operation switch
            {
                "engine.work-hold.status" => Status(),
                "engine.work-hold.resume" => Task.FromResult(current = Hold("resumed", 26, 4, enginePaused: false)),
                _ => throw new InvalidOperationException(operation)
            } };
            using var plugin = Create(host);
            var view = plugin.MountContributions.OfType<DesktopMountView>()
                .Single(item => item.Descriptor.Id == "work-hold-status").CreateView();
            var status = Find<Button>(view, "ResourcePause.WorkHoldStatus");
            var panel = plugin.ConfigurationPanels.Single(item => item.Id == "work-hold").CreateView();
            blockNext = true;
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => entered);
            Find<Button>(panel, "ResourcePause.WorkHoldResume").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Label(status) == "Hold all work");
            if (failedObservation) delayed.SetException(new IOException("older fixture observation failed"));
            else delayed.SetResult(original);
            PumpUntil(() => returned);
            var drainDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(80);
            PumpUntil(() => DateTime.UtcNow >= drainDeadline);
            Check(Label(status) == "Hold all work" && host.Operations.Count(operation => operation == "engine.work-hold.resume") == 1,
                failedObservation ? "a delayed old read failure cannot replace a confirmed local resume with attention"
                    : "a delayed old held projection cannot replace a confirmed local resume");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }
}
