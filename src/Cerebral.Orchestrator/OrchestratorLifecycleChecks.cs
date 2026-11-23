using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

internal static class OrchestratorLifecycleChecks
{
    // Separate from Run: this checks the ordinary semantic Exit path without showing Main or publishing a tray icon.
    public static int RunUnshownNormalSemanticExit(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Console.Error.WriteLine("Offscreen Main semantic Exit stage: begin.");
        var ownedName = "cerebral-main-exit-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), ownedName);
        OrchestratorWindow? window = null;
        OrchestratorTray? tray = null;
        DesktopControlServer? control = null;
        Task<DesktopControlResponse>? clientTask = null;
        string? descriptorPath = null;
        Exception? startupFailure = null;
        var operations = new List<string>();
        var requestIds = new List<string>();
        var closeOrder = new List<string>();
        var stages = new ConcurrentQueue<string>();
        var exitRequests = 0;

        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(root);
                DesktopPreferenceDiagnostics.UseIsolatedProcess(root);
                ConnectionStore.UseProcessLocalDataDirectory(root);
                Ui.ApplyTheme(app);
                var fixtureWindow = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(root, "Plugins", "Desktop")),
                    Path.Combine(root, "PluginData", "Desktop"), composerDraftDirectory: Path.Combine(root, "Drafts"));
                window = fixtureWindow;
                fixtureWindow.EnterLifecycleInspectionMode();
                stages.Enqueue("fixture-created");
                if (fixtureWindow.HasInstalledUserPipeForLifecycleCheck || fixtureWindow.ServiceConnected)
                    throw new InvalidOperationException("The offscreen fixture selected a live Worker connection.");
                fixtureWindow.Closing += (_, _) => { closeOrder.Add("closing"); stages.Enqueue("closing"); };
                fixtureWindow.Closed += (_, _) => { closeOrder.Add("closed"); stages.Enqueue("closed"); };
                app.MainWindow = fixtureWindow;
                var fixtureControl = await DesktopControlServer.StartNormalSemanticAsync(fixtureWindow, root);
                control = fixtureControl;
                stages.Enqueue("control-ready");
                var descriptor = fixtureControl.DescriptorPath;
                descriptorPath = descriptor;
                var instance = fixtureControl.Endpoint.InstanceId;
                tray = new OrchestratorTray(app, fixtureWindow, presentDesktop: false);
                fixtureWindow.FullExitRequested += (_, _) => exitRequests++;
                clientTask = Task.Run(async () =>
                {
                    stages.Enqueue("client-start");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var session = new MainExitClientSession(instance);
                    return await session.ExecuteAsync(async (operation, requestId, arguments) =>
                    {
                        operations.Add(operation);
                        requestIds.Add(requestId);
                        var reply = await DesktopControlClient.ExecuteAsync(descriptor, operation, requestId,
                            arguments, deadline.Token).ConfigureAwait(false);
                        stages.Enqueue(operation + ":reply");
                        return reply;
                    }).ConfigureAwait(false);
                });
            }
            catch (Exception error)
            {
                startupFailure = error;
                if (!app.Dispatcher.HasShutdownStarted) app.Shutdown(1);
            }
        };

        var result = 1;
        try
        {
            var appExit = app.Run();
            stages.Enqueue("app-exit");
            if (startupFailure is not null) throw new InvalidOperationException("Offscreen Main startup failed.", startupFailure);
            if (clientTask is null || descriptorPath is null || control is null || tray is null)
                throw new InvalidOperationException("The offscreen Main semantic Exit fixture was incomplete.");
            DesktopControlResponse commit;
            try { commit = clientTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "The normal semantic Exit sequence did not return a confirmed reply; inspect its bounded stage markers.", error);
            }
            Check(commit.State == "accepted" && commit.Success &&
                  commit.Data.GetProperty("admissionAcknowledged").GetBoolean() &&
                  commit.Data.GetProperty("exitRequested").GetBoolean(),
                "Normal Main did not acknowledge one accepted Exit commit.");
            Check(operations.SequenceEqual([DesktopControlProtocol.CapabilitiesInspect,
                    DesktopControlProtocol.FullExitPrepare, DesktopControlProtocol.FullExitCommit]) &&
                  requestIds.Count == 3 && requestIds.Distinct(StringComparer.Ordinal).Count() == 3 &&
                  requestIds.All(id => Guid.TryParseExact(id, "N", out _)),
                "The semantic client did not send exactly one ordered, distinct prepare/commit sequence.");
            Check(exitRequests == 1 && closeOrder.SequenceEqual(["closing", "closed"]) &&
                  tray.IsExiting && !tray.IsNotificationVisible && !window!.IsVisible && appExit == 0,
                "The unshown Main did not close once and end its explicit application lifetime.");
            result = 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Offscreen Main semantic Exit check failed (" + error.GetType().Name +
                ") at " + string.Join(",", stages) + ": " + error.Message);
        }
        finally
        {
            try
            {
                tray?.Dispose();
                stages.Enqueue("control-dispose-begin");
                if (control is not null) control.DisposeAsync().AsTask().GetAwaiter().GetResult();
                stages.Enqueue("control-dispose-end");
                if (descriptorPath is not null && File.Exists(descriptorPath))
                    throw new InvalidOperationException("The owned normal semantic descriptor remained after server disposal.");
            }
            catch (Exception error)
            {
                result = 1;
                Console.Error.WriteLine("Offscreen Main semantic cleanup failed (" + error.GetType().Name + "): " + error.Message);
            }
            finally
            {
                try
                {
                    var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                    var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                    if (!string.Equals(Path.GetDirectoryName(resolved), temp, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetFileName(resolved), ownedName, StringComparison.Ordinal))
                        throw new InvalidOperationException("The offscreen fixture cleanup path is not its exact temporary root.");
                    if (Directory.Exists(resolved))
                    {
                        if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidOperationException("The offscreen fixture root became a link; preserve it for review.");
                        Directory.Delete(resolved, true);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    result = 1;
                    Console.Error.WriteLine("Offscreen Main temporary cleanup failed (" + error.GetType().Name + ").");
                }
            }
        }
        if (result == 0)
            Console.WriteLine("Offscreen Main semantic Exit: real same-user pipe, one acknowledged commit, unshown Closing/Closed, application exit and descriptor withdrawal; no Worker or model command.");
        return result;
    }

    public static int Run(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var root = Path.Combine(Path.GetTempPath(), "Pegasus-Cerebral-Desktop-Lifecycle-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        var exit = 1;
        Exception? failure = null;
        OrchestratorWindow? window = null;
        OrchestratorTray? tray = null;
        DesktopControlServer? control = null;
        string? descriptorPath = null;
        double reopenP95Milliseconds = 0;

        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            try
            {
                ConnectionStore.UseProcessLocalDataDirectory(root);
                window = new OrchestratorWindow(
                    new DesktopPluginCatalog(Path.Combine(root, "Plugins", "Desktop")),
                    Path.Combine(root, "PluginData", "Desktop"));
                window.EnterLifecycleInspectionMode();
                var retainedWindow = window;
                control = await DesktopControlServer.StartAsync(window, root);
                descriptorPath = control.DescriptorPath;
                Check(File.Exists(descriptorPath), "Source-local desktop control did not publish its owned endpoint."); checks++;

                tray = new OrchestratorTray(app, window, window.NewConversationFromTray,
                    () => false, () => "Lifecycle fixture is intentionally disconnected.", showHideNotice: false);
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.IsVisible && tray.IsNotificationVisible,
                    "The shown desktop did not retain its notification-area owner."); checks++;

                window.Close();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(!window.IsVisible && tray.IsNotificationVisible && !tray.IsExiting &&
                      !app.Dispatcher.HasShutdownStarted,
                    "Normal WPF close must hide the window while retaining the tray-owned application lifetime."); checks++;

                window.WindowState = WindowState.Minimized;
                tray.ShowCerebral();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.IsVisible && window.WindowState == WindowState.Normal,
                    "Tray activation must show and restore the retained desktop window."); checks++;

                Check(File.Exists(descriptorPath),
                    "Hiding and restoring the desktop must not retire its live semantic endpoint."); checks++;

                var reopenSamples = new List<double>();
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    window.Close();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var stopwatch = Stopwatch.StartNew();
                    tray.ShowCerebral();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    stopwatch.Stop();
                    reopenSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
                    Check(ReferenceEquals(window, retainedWindow) && window.IsVisible && File.Exists(descriptorPath),
                        "Tray reopen must reuse the initialized window and its live semantic endpoint.");
                }
                checks++;
                reopenSamples.Sort();
                reopenP95Milliseconds = reopenSamples[(int)Math.Ceiling(reopenSamples.Count * .95) - 1];
                Check(reopenP95Milliseconds < 2_000,
                    "The retained tray window exceeded the bounded two-second reopen limit."); checks++;

                var startup = CerebralStartupWindow.ForInstalled();
                startup.SetProgress("Plugins · Loading fixture.plugin…");
                startup.SetWarning("Plugins · fixture.optional needs attention.");
                startup.ShowFailure(new InvalidOperationException("Fixture startup failure."));
                Check(startup.DiagnosticText.Contains("Loading fixture.plugin", StringComparison.Ordinal) &&
                      startup.DiagnosticText.Contains("fixture.optional", StringComparison.Ordinal) &&
                      startup.CurrentStatus.Contains("Fixture startup failure", StringComparison.Ordinal),
                    "The startup splash must retain bounded progress, warning, and blocking failure diagnostics."); checks++;
                startup.Close();
                var attachedStartup = CerebralStartupWindow.ForAttached();
                attachedStartup.Show();
                Check(AttachExistingStartup.ShowEarlyWindowCloseFailure(attachedStartup, false, false, false) &&
                      !AttachExistingStartup.ShowEarlyWindowCloseFailure(attachedStartup, false, true, false),
                    "An early private window loss must make startup terminal once, without replaying the failure."); checks++;
                Check(!attachedStartup.OrdinaryConfigurationActionVisible &&
                      attachedStartup.CurrentStatus.Contains("closed before it became ready", StringComparison.Ordinal),
                    "A failed private attachment must show its error without opening ordinary Configuration discovery."); checks++;
                attachedStartup.Close();
                tray.ExitCerebral();
            }
            catch (Exception error)
            {
                failure = error;
                if (!app.Dispatcher.HasShutdownStarted) app.Shutdown(1);
            }
        };

        try { exit = app.Run(); }
        finally
        {
            tray?.Dispose();
            if (control is not null) control.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        try
        {
            if (failure is not null) throw new InvalidOperationException("Desktop lifecycle acceptance failed.", failure);
            Check(exit == 0 && tray is { IsExiting: true } && window is { IsVisible: false },
                "Explicit Exit did not close the retained desktop with a successful application result."); checks++;
            Check(descriptorPath is not null && !File.Exists(descriptorPath),
                "Explicit Exit retained a semantic endpoint for a dead desktop generation."); checks++;
            Console.WriteLine($"Desktop lifecycle: {checks} real WPF, tray, and named-pipe checks passed; 40 retained-window reopen cycles p95={reopenP95Milliseconds:F1} ms; no Worker or model command executed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public sealed partial class OrchestratorWindow
{
    internal void EnterLifecycleInspectionMode() => EnterInspectionMode();
    internal bool HasInstalledUserPipeForLifecycleCheck => ServiceConnection.Transport == "installed-user-pipe";
}
