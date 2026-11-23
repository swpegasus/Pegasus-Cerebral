using System.IO;
using System.Windows;
using Cerebral.Client;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // CLI refusal must never fall through to WPF startup or its modal error handler.
        if (args.Contains("--exit", StringComparer.Ordinal))
            return MainExitCommand.RunAsync(args).GetAwaiter().GetResult();
        try
        {
            if (args.Length == 1 && args[0] == "--check-main-cli-exit")
            {
                var checks = MainCliExitChecks.VerifyAsync().GetAwaiter().GetResult();
                Console.WriteLine($"Main CLI Exit: {checks} deterministic admission/client/entry assertions passed; no pipe, window, Worker, model or installed-state operation.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--check-tray-hide-notice")
            {
                var checks = OrchestratorTrayHideNoticeChecks.Verify();
                Console.WriteLine($"Tray notifications: {checks} deterministic launch-lifetime and bounded-target checks passed; no window, notification, Worker or installed-state operation.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--check-work-attention")
            {
                var checks = WorkAttentionChecks.Verify();
                Console.WriteLine($"Work attention: {checks} deterministic transition and fake activation checks passed; no window, notification, Worker or installed-state operation.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--check-main-semantic-exit-offscreen")
            {
                // This isolated check must remain bounded even before WPF starts or after its
                // fixture returns. A background thread cannot depend on the UI or thread pool.
                var watchdog = new System.Threading.Thread(static () =>
                {
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(45));
                    Environment.Exit(124);
                }) { IsBackground = true, Name = "Cerebral offscreen Exit watchdog" };
                watchdog.Start();
                Console.Error.WriteLine("Offscreen Main semantic Exit stage: entry.");
                try
                {
                    System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                    return OrchestratorLifecycleChecks.RunUnshownNormalSemanticExit(new Application());
                }
                finally { Console.Error.WriteLine("Offscreen Main semantic Exit stage: end."); }
            }
            // Set DPI awareness before WPF or the notification icon initializes a native window.
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            return RunWpf(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (!IsSelfTest(args))
                MessageBox.Show(error.Message, "Pegasus Cerebral encountered an error", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    // Command-line checks must report failure and exit without waiting for a modal dialog.
    private static bool IsSelfTest(string[] args) =>
        args.Any(argument => argument.StartsWith("--check-", StringComparison.Ordinal));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunWpf(string[] args)
    {
        var explicitPortable = args.Length == 1 && args[0] == "--portable-package";
        if (explicitPortable) args = [];
        else if (args.Contains("--portable-package", StringComparer.Ordinal))
            throw new ArgumentException("Use --portable-package only from the portable launcher.");
        var packageRoot = Cerebral.Core.InstallationPaths.InstallDirectory;
        if (explicitPortable ||
            File.Exists(Path.Combine(packageRoot, "Start-CerebralPortable.cmd")) ||
            File.Exists(Path.Combine(packageRoot, "portable-manifest.json")))
        {
            try { Cerebral.Core.PortablePackageAdmission.VerifyAt(packageRoot); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                if (!IsSelfTest(args))
                    MessageBox.Show(error.Message, "Pegasus Cerebral portable package", MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
        }
        if (args.Contains("--attach-existing", StringComparer.Ordinal) || args.Contains("--configuration-state", StringComparer.Ordinal) ||
            args.Contains("--input-guidance-layout-control", StringComparer.Ordinal) || args.Contains("--main-semantic-control", StringComparer.Ordinal) ||
            args.Contains("--configuration-entry", StringComparer.Ordinal) || args.Contains("--configuration-entry-sha256", StringComparer.Ordinal))
            return AttachExistingStartup.Run(args);
        if (Cerebral.Core.ApplicationVersion.TryWrite(args, "Orchestrator")) return 0;
        if (args.Length == 1 && args[0] == "--check-attach-existing-startup")
            return AttachExistingStartupChecks.Run(null);
        if (args.Length == 1 && args[0] == "--check-plugin-settings-generation")
        {
            Console.WriteLine($"Plugin settings generation checks passed ({OrchestratorWindow.CheckPluginSettingsGenerationFixture()}; deterministic receiver projection, no launch or IPC).");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--check-plugin-settings-owner")
        {
            Console.WriteLine($"Plugin settings owner checks passed ({OrchestratorWindow.CheckPluginSettingsOwnerFixture()}; deterministic no-send admission, no launch or IPC).");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--check-plugin-settings-peer-argv")
        {
            Console.WriteLine($"Plugin settings peer argv check passed ({OrchestratorWindow.CheckPluginSettingsPeerArgv()}; current process native observation, no launch or IPC).");
            return 0;
        }
        if (args.Contains("--desktop-control-call", StringComparer.Ordinal))
            return DesktopControlCommand.RunAsync(args).GetAwaiter().GetResult();
        if (args.Contains("--check-source-local-options", StringComparer.Ordinal))
        {
            Console.WriteLine($"Source-local startup: {SourceLocalStartupChecks.Verify()} deterministic checks passed.");
            return 0;
        }
        if (args.Contains("--check-local-pipe-ui", StringComparer.Ordinal))
        {
            _ = LocalDevelopmentPipeChecks.VerifyAsync(args.SkipWhile(x => x != "--worker").Skip(1).FirstOrDefault()).GetAwaiter().GetResult();
            return 0;
        }
        if (AiDictationAppAcceptance.IsParent(args)) return AiDictationAppAcceptance.RunParent(args);
        if (args.Length == 1 && args[0] == "--check-fresh-chat-draft")
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            Console.WriteLine($"Fresh chat drafts: {FreshChatDraftChecks.Run()} unshown WPF/adapter checks passed.");
            return 0;
        }
        // These two offscreen navigation fixtures construct WPF controls, but do not need
        // hardware rendering. Set the process mode before constructing the Application.
        if (args.Contains("--check-navigation-ui", StringComparer.Ordinal) ||
            args.Contains("--check-composer-layout-ui", StringComparer.Ordinal) ||
            args.Contains("--check-chat-workspace-ui", StringComparer.Ordinal) ||
            args.Contains("--check-team-views-ui", StringComparer.Ordinal))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application(); Ui.ApplyTheme(app);
        if (args.Length == 1 && args[0] == "--check-chat-docking-bars")
        {
            var checks = ChatDockingBarsChecks.Verify() + ChatDockHiddenTargetChecks.Verify();
            Console.WriteLine($"Chat docking checks passed ({checks}; STA unshown WPF controls and deterministic records; no Main, Worker, network, model or shown window).");
            return 0;
        }
        if (args.Contains("--check-composer-drafts-ui", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                try { await OrchestratorWindow.CheckComposerDraftsAsync(); }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { app.Shutdown(exit); }
            }));
            return app.Run();
        }
        if (args.Contains("--check-desktop-lifecycle", StringComparer.Ordinal)) return OrchestratorLifecycleChecks.Run(app);
        if (args.Contains("--check-desktop-control", StringComparer.Ordinal)) return DesktopControlChecks.Run(app);
        if (AiDictationAppAcceptance.IsChild(args)) return AiDictationAppAcceptance.RunChild(app, args);
        if (args.Contains("--check-core-no-plugins-ui", StringComparer.Ordinal)) return AiDictationAppAcceptance.CheckCoreWithoutPlugins();
        if (Array.IndexOf(args, "--check-workflow-service-ui") is var workflowIndex && workflowIndex >= 0)
        {
            if (args.Length < workflowIndex + 4) { Console.Error.WriteLine("Supply the isolated pipe name, Worker instance and review run."); return 2; }
            return WorkflowWorkspaceChecks.CheckService(app,
                new Cerebral.Core.LocalWorkerEndpoint(args[workflowIndex + 1], args[workflowIndex + 2]),
                args[workflowIndex + 3], args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault());
        }
        if (args.Contains("--check-attachment-composer-ui", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                try
                {
                    _ = await AttachmentComposerChecks.VerifyAsync(
                        args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault());
                }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { app.Shutdown(exit); }
            }));
            return app.Run();
        }
        if (args.Contains("--check-explicit-workflow-ui", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0; var window = new OrchestratorWindow();
                try
                {
                    var checks = await DesktopExplicitWorkflowChecks.VerifyAsync();
                    checks += await window.CheckExplicitWorkflowsAsync();
                    Console.WriteLine($"Explicit plugin workflows: {checks} deterministic WPF checks passed.");
                }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { window.Close(); app.Shutdown(exit); }
            }));
            return app.Run();
        }
        if (args.Contains("--check-conversation-properties-ui", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                var window = new OrchestratorWindow();
                try
                {
                    exit = await window.CheckConversationPropertiesAsync();
                    if (exit == 0) exit = await window.CheckConversationPathGrantsAsync();
                    if (exit == 0) exit = await window.CheckConversationPluginSettingsAsync();
                    if (exit == 0) exit = await window.CheckNativeConversationPreferencesAsync();
                }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { window.Close(); app.Shutdown(exit); }
            }));
            return app.Run();
        }
        if (args.Contains("--check-conversation-controls-ui", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                var window = new OrchestratorWindow();
                try { await window.CheckConversationControlsAsync(); }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { window.Close(); app.Shutdown(exit); }
            }));
            return app.Run();
        }
        if (args.Contains("--check-file-browser-races-ui", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                var window = new OrchestratorWindow();
                try { _ = await window.CheckFileBrowserRaceUiAsync(); }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { window.Close(); app.Shutdown(exit); }
            }));
            return app.Run();
        }
        if (args.Contains("--check-desktop-workflow-control", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                var uiRoot = Path.Combine(Path.GetTempPath(), "cerebral-work-control-unit-" + Guid.NewGuid().ToString("N"));
                var window = new OrchestratorWindow(
                    new Cerebral.Desktop.Plugins.DesktopPluginCatalog(Path.Combine(uiRoot, "plugins")),
                    Path.Combine(uiRoot, "plugin-data"));
                try { _ = await window.CheckDesktopWorkflowControlAsync(); }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally
                {
                    window.Close();
                    try { if (Directory.Exists(uiRoot)) Directory.Delete(uiRoot, true); }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    app.Shutdown(exit);
                }
            }));
            return app.Run();
        }
        if (Array.IndexOf(args, "--check-workflow-semantic-restart-service") is var semanticRestartIndex && semanticRestartIndex >= 0)
        {
            if (args.Length < semanticRestartIndex + 5)
            {
                Console.Error.WriteLine("Supply the isolated pipe name, Worker instance, absolute Worker data root and workflow run ID.");
                return 2;
            }
            return OrchestratorWindow.RunWorkflowSemanticRestartAcceptance(app,
                new Cerebral.Core.LocalWorkerEndpoint(args[semanticRestartIndex + 1], args[semanticRestartIndex + 2]),
                args[semanticRestartIndex + 3], args[semanticRestartIndex + 4]);
        }
        if (Array.IndexOf(args, "--check-workflow-control-service") is var controlIndex && controlIndex >= 0)
        {
            if (args.Length < controlIndex + 5)
            {
                Console.Error.WriteLine("Supply the isolated pipe name, Worker instance, absolute Worker data root and workflow run ID.");
                return 2;
            }
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                var uiRoot = Path.Combine(Path.GetTempPath(), "cerebral-work-control-ui-" + Guid.NewGuid().ToString("N"));
                var window = new OrchestratorWindow(
                    new Cerebral.Desktop.Plugins.DesktopPluginCatalog(Path.Combine(uiRoot, "plugins")),
                    Path.Combine(uiRoot, "plugin-data"));
                try
                {
                    var endpoint = new Cerebral.Core.LocalWorkerEndpoint(args[controlIndex + 1], args[controlIndex + 2]);
                    _ = await window.CheckWorkflowControlServiceAsync(endpoint, args[controlIndex + 3], args[controlIndex + 4]);
                }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally
                {
                    window.Close();
                    try { if (Directory.Exists(uiRoot)) Directory.Delete(uiRoot, true); }
                    catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                    app.Shutdown(exit);
                }
            }));
            return app.Run();
        }
        if (Array.IndexOf(args, "--check-programming-control-service") is var programmingIndex && programmingIndex >= 0)
        {
            if (args.Length < programmingIndex + 6)
            {
                Console.Error.WriteLine("Supply the isolated pipe name, Worker instance, absolute Worker data root, Programming run ID and conversation ID.");
                return 2;
            }
            return ProgrammingDesktopAcceptance.Run(app,
                new Cerebral.Core.LocalWorkerEndpoint(args[programmingIndex + 1], args[programmingIndex + 2]),
                args[programmingIndex + 3], args[programmingIndex + 4], args[programmingIndex + 5]);
        }
        if (args.Contains("--check-main-chat-timing", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                try
                {
                    var checks = await MainChatInterfaceTimingChecks.VerifyAsync();
                    Console.WriteLine($"Main chat timing: {checks} offscreen deterministic checks passed; no Worker, model or network.");
                }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { app.Shutdown(exit); }
            }));
            return app.Run();
        }
        if (args.Contains("--check-chat-workspace-ui", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                try
                {
                    var checks = await WorkflowChatWorkspaceChecks.VerifyAsync(args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault());
                    checks += await WorkflowWorkspaceConnectionChecks.VerifyAsync();
                    var window = new OrchestratorWindow();
                    try { checks += await window.CheckDurableChatNavigationAsync(); }
                    finally { window.Close(); }
                    Console.WriteLine($"Native Chat: {checks} deterministic WPF/adapter checks passed; no inference or network.");
                }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { app.Shutdown(exit); }
            }));
            return app.Run();
        }
        if (Array.IndexOf(args, "--check-pipe-service-ui") is var pipeIndex && pipeIndex >= 0)
        {
            if (args.Length < pipeIndex + 4)
            {
                Console.Error.WriteLine("Supply the isolated pipe name, Worker instance and absolute data directory.");
                return 2;
            }
            var dataDirectory = args[pipeIndex + 3];
            var window = new OrchestratorWindow(new Cerebral.Desktop.Plugins.DesktopPluginCatalog(),
                Path.Combine(dataDirectory, "desktop-inspection-plugin-data"));
            return DesktopDiagnostics.CheckPipeService(app, window,
                new Cerebral.Core.LocalWorkerEndpoint(args[pipeIndex + 1], args[pipeIndex + 2]),
                dataDirectory, args.SkipWhile(x => x != "--session").Skip(1).FirstOrDefault(),
                args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault());
        }
        if (args.Contains("--check-composer-layout-ui", StringComparer.Ordinal))
        {
            var fixtureRoot = Path.Combine(Path.GetTempPath(), "Cerebral.ComposerLayout-" + Guid.NewGuid().ToString("N"));
            var window = new OrchestratorWindow(new Cerebral.Desktop.Plugins.DesktopPluginCatalog(Path.Combine(fixtureRoot, "plugins")),
                Path.Combine(fixtureRoot, "plugin-data"), Path.Combine(fixtureRoot, "drafts"));
            return window.CheckComposerLayoutUi(args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault());
        }
        if (args.Contains("--check-team-views-ui", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                try { await new OrchestratorWindow().CheckTeamViewsUiAsync(); }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { app.Shutdown(exit); }
            }));
            return app.Run();
        }
        var diagnostic = args.Any(x => x is "--check-durable-chat-ui" or "--check-header-ui" or "--check-navigation-ui" or "--check-interface-preferences-ui" or "--check-activity-ui" or
            "--check-question-ui" or "--check-files-ui" or "--check-ui" or "--check-service-ui");
        if (diagnostic)
        {
            var window = new OrchestratorWindow();
            if (args.Contains("--check-durable-chat-ui", StringComparer.Ordinal)) return window.CheckDurableChatUi();
            if (args.Contains("--check-header-ui", StringComparer.Ordinal)) return window.CheckHeaderUi(args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault());
            if (args.Contains("--check-navigation-ui", StringComparer.Ordinal)) return window.CheckNavigationUi(args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault());
            if (args.Contains("--check-interface-preferences-ui", StringComparer.Ordinal)) return window.CheckInterfacePreferencesUi();
            if (args.Contains("--check-activity-ui", StringComparer.Ordinal)) return window.CheckActivityUi(args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault(), args.Contains("--summary-only", StringComparer.Ordinal));
            if (args.Contains("--check-question-ui", StringComparer.Ordinal)) return window.CheckQuestionUi();
            if (args.Contains("--check-files-ui", StringComparer.Ordinal)) return window.CheckFileInteractionUi();
            if (args.Contains("--check-ui", StringComparer.Ordinal)) return DesktopDiagnostics.Check(window);
            if (Array.IndexOf(args, "--check-service-ui") is var index && index >= 0)
                return DesktopDiagnostics.CheckService(app, window, args.ElementAtOrDefault(index + 1) ?? Cerebral.Core.CerebralDefaults.WorkerUrl, args.SkipWhile(x => x != "--session").Skip(1).FirstOrDefault());
        }

        SourceLocalOptions? sourceLocal;
        try { sourceLocal = SourceLocalOptions.FromArguments(args); }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Pegasus Cerebral source startup", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
        if (sourceLocal is not null)
        {
            var inherited = args.Contains("--source-local", StringComparer.Ordinal) ? null : DesktopInstancePaths.ReadInheritedSource();
            DesktopInstancePaths.UseSource(sourceLocal.DataDirectory, inherited?.InstallDirectory ?? ManagementProcess.SourceProgramDirectory);
        }
        var launchIdentity = DesktopLaunchIdentity(sourceLocal);
        using var instance = new DesktopApplicationInstance("Pegasus.Cerebral.Orchestrator", launchIdentity);
        if (!instance.IsPrimary)
        {
            if (instance.RequestActivation()) return 0;
            var ownerGeneration = instance.OwnerIsCurrent
                ? $"PID {instance.OwnerProcessId} · started {instance.OwnerStartedAt:O}"
                : "owner process generation could not be verified";
            MessageBox.Show($"Pegasus Cerebral could not activate the existing desktop because its connection identity does not match this launch or its owner generation is stale.\n\nRunning: {instance.OwnerIdentity}\nRequested: {instance.RequestedIdentity}\nOwner: {ownerGeneration}\n\nExit the running tray instance before opening this one.",
                "Pegasus Cerebral is already running", MessageBoxButton.OK, MessageBoxImage.Information);
            return 3;
        }
        if (sourceLocal is not null)
            return RunSourceLocal(app, instance, sourceLocal, args.Contains("--tray", StringComparer.Ordinal));
        return RunInstalledWithSplash(app, instance, args.Contains("--tray", StringComparer.Ordinal));
    }

    private static DesktopApplicationIdentity DesktopLaunchIdentity(SourceLocalOptions? sourceLocal)
    {
        if (sourceLocal is not null) return DesktopApplicationIdentity.SourceLocal(sourceLocal.DataDirectory);
        var connection = ConnectionStore.Read();
        if (!connection.Saved || connection.Preferences is not { } preferences)
            return DesktopApplicationIdentity.InstalledUser(InstalledWorkerBindingStore.ReadRegisteredUser()?.DataDirectory);
        return preferences.Transport switch
        {
            "installed-pipe" or "installed-user-pipe" =>
                DesktopApplicationIdentity.InstalledUser(InstalledWorkerBindingStore.ReadRegisteredUser()?.DataDirectory),
            "pipe" => DesktopApplicationIdentity.LocalWorker(preferences.LocalDataDirectory!),
            _ => DesktopApplicationIdentity.NetworkWorker(preferences.Url)
        };
    }

    private static int RunInstalledWithSplash(Application app, DesktopApplicationInstance instance, bool startHidden)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var startupLifetime = new CancellationTokenSource();
        var splash = CerebralStartupWindow.ForInstalled();
        OrchestratorWindow? mainWindow = null;
        OrchestratorTray? tray = null;
        DesktopControlServer? desktopControl = null;
        var replacingSplash = false;
        var starting = false;
        TaskCompletionSource? mainClosed = null;
        splash.Closed += (_, _) =>
        {
            if (replacingSplash) return;
            startupLifetime.Cancel();
            app.Shutdown();
        };
        instance.Listen(app.Dispatcher, () =>
        {
            if (tray is not null) tray.ShowCerebral();
            else { splash.Show(); splash.Activate(); }
        });
        splash.ConfigureRetry(StartAttemptAsync);
        splash.Show();
        app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () => await StartAttemptAsync()));
        async Task StartAttemptAsync()
        {
            if (starting || startupLifetime.IsCancellationRequested) return;
            starting = true; splash.BeginAttempt();
            try
            {
                await splash.WaitForFirstContentRenderedAsync(startupLifetime.Token);
                splash.SetProgress("Interface · Loading the conversation workspace…");
                startupLifetime.Token.ThrowIfCancellationRequested();
                var paths = DesktopInstancePaths.Current;
                mainWindow = new OrchestratorWindow(new(paths.UserPluginDirectory), paths.PluginDataDirectory,
                    composerDraftPaths: paths);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                mainClosed = closed;
                mainWindow.Closed += (_, _) => closed.TrySetResult();
                mainWindow.EnableOrdinaryPluginSettingsNavigation();
                app.MainWindow = mainWindow;
                await PrimeMainWindowAsync(mainWindow, splash, startupLifetime.Token, MainStartupMode.Installed);
                desktopControl = await DesktopControlServer.StartNormalSemanticAsync(mainWindow, paths.UserDataDirectory, startupLifetime.Token);
                _ = ObserveNormalDesktopControlAsync(desktopControl, mainWindow);
                tray = new OrchestratorTray(app, mainWindow);
                RequirePreparedWindow(mainWindow);
                splash.SetProgress(startHidden ? "Tray · Cerebral is ready in the notification area." : "Interface · Cerebral is ready.");
                startupLifetime.Token.ThrowIfCancellationRequested();
                replacingSplash = true;
                RevealPreparedWindow(mainWindow, splash, startHidden);
            }
            catch (OperationCanceledException) when (startupLifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                // A manual retry owns a fresh window/runtime/plugin generation. Do
                // not offer it until the previous hidden window actually closed.
                var retryAllowed = error is not MainStartupException { RetryAllowed: false };
                app.MainWindow = splash;
                Exception? cleanupFailure = null;
                var failedTray = tray; tray = null;
                try
                {
                    mainWindow?.ConfigureTrayLifetime(static () => false);
                    failedTray?.Dispose();
                }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { cleanupFailure ??= cleanup; }
                try
                {
                    if (desktopControl is not null) { await desktopControl.DisposeAsync(); desktopControl = null; }
                }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { cleanupFailure ??= cleanup; }
                try
                {
                    if (mainWindow is not null && mainClosed is not null)
                    {
                        if (!mainClosed.Task.IsCompleted) mainWindow.Close();
                        await mainClosed.Task.WaitAsync(TimeSpan.FromSeconds(15), startupLifetime.Token);
                        mainWindow = null; mainClosed = null;
                    }
                }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                {
                    cleanupFailure ??= cleanup;
                }
                if (cleanupFailure is not null) { retryAllowed = false; error.Data["MainStartupCleanup"] = cleanupFailure; }
                splash.ShowFailure(retryAllowed ? error : new MainStartupException(
                    error is MainStartupException original ? original.Stage : "Startup", error, retryAllowed: false));
            }
            finally { starting = false; }
        }
        try { return app.Run(); }
        finally
        {
            startupLifetime.Cancel();
            tray?.Dispose();
            if (desktopControl is not null) desktopControl.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task ObserveNormalDesktopControlAsync(DesktopControlServer server, OrchestratorWindow window)
    {
        try { await server.Completion.ConfigureAwait(false); }
        catch (Exception) { /* The owned descriptor is already withdrawn by listener cleanup. */ }
        if (server.IsStopping || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
        try { await window.Dispatcher.InvokeAsync(window.ReportNormalDesktopControlUnavailable); }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) when (window.Dispatcher.HasShutdownStarted) { }
    }

    private static int RunSourceLocal(Application app, DesktopApplicationInstance instance,
        SourceLocalOptions options, bool startHidden)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var startupLifetime = new CancellationTokenSource();
        var splash = CerebralStartupWindow.ForSource(options);
        splash.ShowActivated = !startHidden;
        OrchestratorWindow? mainWindow = null;
        OrchestratorTray? tray = null;
        DesktopControlServer? desktopControl = null;
        var replacingSplash = false;
        splash.Closed += (_, _) =>
        {
            if (replacingSplash) return;
            startupLifetime.Cancel();
            app.Shutdown();
        };
        instance.Listen(app.Dispatcher, () =>
        {
            if (tray is not null) tray.ShowCerebral();
            else { splash.Show(); splash.Activate(); }
        });
        splash.Show();
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                _ = await SourceLocalStartup.PrepareAsync(options, splash.SetProgress, startupLifetime.Token);
                splash.SetProgress("Plugins · Loading desktop extensions…");
                var paths = DesktopInstancePaths.Current;
                mainWindow = new OrchestratorWindow(new Cerebral.Desktop.Plugins.DesktopPluginCatalog(paths.PluginDirectory),
                    paths.PluginDataDirectory, composerDraftPaths: paths);
                splash.SetProgress("Interface · Enabling local semantic diagnostics…");
                desktopControl = await DesktopControlServer.StartAsync(mainWindow, options.DataDirectory, startupLifetime.Token);
                app.MainWindow = mainWindow;
                await PrimeMainWindowAsync(mainWindow, splash, startupLifetime.Token, MainStartupMode.SourceLocal);
                tray = new OrchestratorTray(app, mainWindow);
                RequirePreparedWindow(mainWindow);
                splash.SetProgress(startHidden ? "Tray · Source Cerebral is ready in the notification area." : "Interface · Opening the conversation workspace…");
                replacingSplash = true;
                RevealPreparedWindow(mainWindow, splash, startHidden);
            }
            catch (OperationCanceledException) when (startupLifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (desktopControl is not null) { await desktopControl.DisposeAsync(); desktopControl = null; }
                try { mainWindow?.Close(); } catch { }
                splash.ShowFailure(error);
            }
        }));
        try { return app.Run(); }
        finally
        {
            startupLifetime.Cancel();
            tray?.Dispose();
            if (desktopControl is not null) desktopControl.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    internal static async Task<DesktopStartupOutcome> PrimeMainWindowAsync(OrchestratorWindow window,
        CerebralStartupWindow splash, CancellationToken cancellationToken, MainStartupMode mode = MainStartupMode.AttachedExisting)
    {
        var startupStage = "Worker connection";
        EventHandler<DesktopStartupProgress> progress = (_, update) =>
        {
            if (!update.Error) startupStage = update.Stage;
            var message = update.Stage + " · " + update.Message;
            if (update.Error) splash.SetWarning(message); else splash.SetProgress(message);
        };
        window.StartupProgressChanged += progress;
        try
        {
            if (window.IsVisible)
                throw new MainStartupException("Interface", new InvalidOperationException("Main was shown before startup preparation."));
            splash.SetProgress("Interface · Preparing the conversation workspace…");
            _ = window.BeginStartupBeforeShowAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            DesktopStartupOutcome outcome;
            try { outcome = await window.WaitForInitialReadyAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Cerebral did not reach a usable startup state within 30 seconds. Open Configuration to inspect the service, model, and plugin settings.");
            }
            if (!outcome.Connected || !window.ServiceConnected)
                throw new MainStartupException(startupStage, outcome.Error ??
                    new InvalidOperationException(outcome.Diagnostic ?? "The verified Worker connection did not become ready."));
            await window.CompleteStartupAsync(mode, cancellationToken);
            splash.SetProgress("Interface · Preparing the initial layout…");
            window.PrepareUnshownStartupLayout(cancellationToken);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequirePreparedWindow(window);
            return outcome;
        }
        catch
        {
            window.InvalidateStartupPreparation();
            throw;
        }
        finally { window.StartupProgressChanged -= progress; }
    }

    internal static void RequirePreparedWindow(OrchestratorWindow window)
    {
        if (!window.PreparedForStartupReveal)
            throw new MainStartupException("Worker connection / interface", new InvalidOperationException(
                "The Worker disconnected, Main closed, or startup preparation did not finish before Main could be revealed."));
    }

    internal static void RevealPreparedWindow(OrchestratorWindow window, CerebralStartupWindow splash, bool startHidden)
    {
        RequirePreparedWindow(window);
        splash.Close();
        // Closing Splash runs its real Closed callbacks before Main may be shown.
        RequirePreparedWindow(window);
        if (startHidden) return;
        window.ShowInTaskbar = true;
        window.ShowActivated = true;
        DesktopApplicationInstance.Show(window);
    }
}
