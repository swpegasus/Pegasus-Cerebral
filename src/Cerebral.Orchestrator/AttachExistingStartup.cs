using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

internal static class AttachExistingStartup
{
    internal sealed record Options(ExistingWorkerDesktopOptions Worker, string? ConfigurationState, bool LayoutObservation = false, bool SemanticControl = false,
        ConfigurationLaunchEntry? ConfigurationEntry = null);

    internal static Options ParseOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        // Main-only suffixes are ordered and closed; orphan/mixed flags never reach discovery.
        const string observationFlag = "--input-guidance-layout-control";
        const string semanticFlag = "--main-semantic-control";
        var hasObservation = args.Contains(observationFlag, StringComparer.Ordinal);
        var hasSemantic = args.Contains(semanticFlag, StringComparer.Ordinal);
        if (hasObservation && hasSemantic)
            throw new ArgumentException("Choose either layout-only observation or semantic Main control, never both.");
        if (hasSemantic && (args.Length is not (7 or 9 or 13) || args[^1] != semanticFlag ||
            args.Count(x => x == semanticFlag) != 1))
            throw new ArgumentException("Place --main-semantic-control once, after the complete existing-Worker attachment and optional Configuration state.");
        if (hasObservation && (args.Length is not (7 or 9 or 13) || args[^1] != observationFlag ||
            args.Count(x => x == observationFlag) != 1))
            throw new ArgumentException("Place --input-guidance-layout-control once, after the complete existing-Worker attachment and optional Configuration state.");
        var attachmentArguments = hasObservation || hasSemantic ? args[..^1] : args;
        var hasConfiguration = attachmentArguments.Contains("--configuration-state", StringComparer.Ordinal);
        if (hasConfiguration && (attachmentArguments.Length is not (8 or 12) || attachmentArguments[6] != "--configuration-state"))
            throw new ArgumentException("Use the existing Worker binding, optional Configuration state, optional Configuration entry/hash pair, then one control-mode flag.");
        if (attachmentArguments.Length == 12 && (attachmentArguments[8] != "--configuration-entry" || attachmentArguments[10] != "--configuration-entry-sha256"))
            throw new ArgumentException("Place --configuration-entry ENTRY --configuration-entry-sha256 SHA256 after Configuration state.");
        var worker = ExistingWorkerDesktopOptions.FromArguments(hasConfiguration ? attachmentArguments[..6] : attachmentArguments)
            ?? throw new ArgumentException("An explicit existing Worker binding is required for private desktop startup.");
        var configuration = hasConfiguration ? ValidateConfigurationState(worker, attachmentArguments[7]) : null;
        var entry = attachmentArguments.Length == 12
            ? ConfigurationLaunchEntry.Parse(attachmentArguments[9], attachmentArguments[11]) : null;
        return new(worker, configuration, hasObservation, hasSemantic, entry);
    }

    private static string ValidateConfigurationState(ExistingWorkerDesktopOptions worker, string directory)
    {
        var target = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", worker.Endpoint.PipeName,
            worker.Endpoint.InstanceId, worker.WorkerDataDirectory, directory, worker.InstallationId])!;
        if (ScopedFilePolicy.IsWithin(target.DesktopDataDirectory, worker.DesktopDataDirectory) ||
            ScopedFilePolicy.IsWithin(worker.DesktopDataDirectory, target.DesktopDataDirectory))
            throw new ArgumentException("Configuration state must be separate from this private interface's state.");
        return target.DesktopDataDirectory;
    }

    // This route never calls SourceLocalStartup, discovery, Worker startup or model preparation.
    internal static OrchestratorWindow CreateWindow(ExistingWorkerDesktopOptions options, string? configurationState = null,
        ConfigurationLaunchEntry? configurationEntry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Internal callers share the same admission checks as the command-line boundary.
        options = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", options.Endpoint.PipeName,
            options.Endpoint.InstanceId, options.WorkerDataDirectory, options.DesktopDataDirectory, options.InstallationId])!;
        if (configurationState is not null) configurationState = ValidateConfigurationState(options, configurationState);
        if (configurationEntry is not null && configurationState is null)
            throw new ArgumentException("Configuration entry requires explicit Configuration state.");
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(options.WorkerDataDirectory);
        var desktop = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(options.DesktopDataDirectory);
        Directory.CreateDirectory(desktop);
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(desktop);
        DesktopPreferenceDiagnostics.UseProcessLocalDirectory(desktop);
        var paths = DesktopInstancePaths.UseAttachedExisting(desktop, AppContext.BaseDirectory);
        var window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(desktop, "Plugins", "Desktop")),
            paths.PluginDataDirectory, composerDraftPaths: paths, attachedComposerStartup: options);
        window.UseExistingLocalWorker(options.Endpoint, options.WorkerDataDirectory, options.InstallationId, configurationState);
        if (configurationEntry is not null)
            window.ConfigureConfigurationLaunch(options, configurationState ?? throw new ArgumentException("Configuration entry requires explicit Configuration state."), configurationEntry);
        return window;
    }

    internal static int Run(string[] args)
    {
        var parsed = ParseOptions(args);
        var options = parsed.Worker;
        using var instance = new DesktopApplicationInstance(options.GetApplicationId("Orchestrator"), options.ApplicationIdentity);
        if (!instance.IsPrimary)
        {
            if (parsed.SemanticControl)
                throw new InvalidOperationException("Semantic control requires a new primary private Main instance. Close the existing interface before retrying this explicit startup.");
            if (parsed.LayoutObservation)
                throw new InvalidOperationException("Layout observation requires a new primary private Main instance. Close the existing interface before retrying this explicit startup.");
            if (instance.RequestActivation()) return 0;
            throw new InvalidOperationException("This private desktop state is already used by another Worker binding. Close that interface first.");
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.ApplyTheme(app);
        var splash = CerebralStartupWindow.ForAttached();
        app.MainWindow = splash;
        OrchestratorWindow? window = null;
        var replacingSplash = false;
        var startupFailureShown = false;
        using var startupLifetime = new CancellationTokenSource();
        DesktopControlServer? observation = null;
        Exception? semanticPrimary = null;
        splash.Closed += (_, _) =>
        {
            if (replacingSplash) return;
            startupLifetime.Cancel();
            app.Shutdown(startupFailureShown ? 1 : 0);
        };
        try
        {
            instance.Listen(app.Dispatcher, () =>
            {
                if (window?.IsVisible == true) { window.Show(); window.Activate(); }
                else { splash.Show(); splash.Activate(); }
            });
            splash.Show();
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    splash.SetProgress("Interface · Loading the selected private workspace…");
                    window = CreateWindow(options, parsed.ConfigurationState, parsed.ConfigurationEntry);
                    window.Closed += (_, _) =>
                    {
                        if (ShowEarlyWindowCloseFailure(splash, replacingSplash, startupFailureShown,
                                startupLifetime.IsCancellationRequested)) startupFailureShown = true;
                        startupLifetime.Cancel();
                        if (replacingSplash) app.Shutdown();
                    };
                    if (parsed.LayoutObservation)
                    {
                        splash.SetProgress("Interface · Enabling read-only layout observation…");
                        observation = await DesktopControlServer.StartReadOnlyObservationAsync(window,
                            options.DesktopDataDirectory, startupLifetime.Token);
                        _ = WatchObservationAsync(observation, window, startupLifetime.Token);
                    }
                    else if (parsed.SemanticControl)
                    {
                        splash.SetProgress("Interface · Enabling private semantic control…");
                        observation = await DesktopControlServer.StartSemanticAttachAsync(window,
                            options.DesktopDataDirectory, startupLifetime.Token);
                        _ = WatchSemanticControlAsync(observation, window, startupLifetime.Token);
                    }
                    app.MainWindow = window;
                    await Program.PrimeMainWindowAsync(window, splash, startupLifetime.Token);
                    Program.RequirePreparedWindow(window);
                    splash.SetProgress("Interface · Opening the conversation workspace…");
                    replacingSplash = true;
                    Program.RevealPreparedWindow(window, splash, startHidden: false);
                }
                catch (OperationCanceledException) when (startupLifetime.IsCancellationRequested)
                {
                    try { window?.Close(); } catch { }
                }
                catch (Exception error)
                {
                    replacingSplash = false;
                    startupFailureShown = true;
                    semanticPrimary = parsed.SemanticControl ? error : null;
                    if (window is not null)
                    {
                        try { window.Close(); }
                        catch (Exception cleanup) { PreserveSemanticCleanupFailure(error, "window-close", cleanup); }
                    }
                    splash.ShowFailure(error);
                }
            }));
            return app.Run();
        }
        finally
        {
            if (parsed.SemanticControl) CleanupSemanticControl(startupLifetime, observation, semanticPrimary);
            else
            {
                startupLifetime.Cancel();
                if (observation is not null)
                    Task.Run(async () => await observation.DisposeAsync()).GetAwaiter().GetResult();
            }
        }
    }

    internal static bool ShowEarlyWindowCloseFailure(CerebralStartupWindow splash, bool replacingSplash,
        bool startupFailureShown, bool cancellationRequested)
    {
        if (replacingSplash || startupFailureShown || cancellationRequested || !splash.IsVisible) return false;
        splash.ShowFailure(new InvalidOperationException(
            "The selected private Main window closed before it became ready. Close this startup window and retry the exact attachment."));
        return true;
    }

    // Keep the first failure intact while still attempting every owned cleanup step.
    internal static void PreserveSemanticCleanupFailure(Exception primary, string stage, Exception cleanup)
    {
        try { primary.Data["MainSemanticAttach." + stage] = cleanup; }
        catch { /* Diagnostic storage must not replace the original failure. */ }
    }

    private static void CleanupSemanticControl(CancellationTokenSource lifetime, DesktopControlServer? control,
        Exception? primary)
    {
        Exception? failure = primary;
        try { lifetime.Cancel(); }
        catch (Exception cleanup)
        {
            if (failure is null) failure = cleanup;
            else PreserveSemanticCleanupFailure(failure, "cancel", cleanup);
        }
        try
        {
            if (control is not null)
                Task.Run(async () => await control.DisposeAsync()).GetAwaiter().GetResult();
        }
        catch (Exception cleanup)
        {
            if (failure is null) failure = cleanup;
            else PreserveSemanticCleanupFailure(failure, "dispose", cleanup);
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task WatchSemanticControlAsync(DesktopControlServer control, OrchestratorWindow window,
        CancellationToken cancellationToken)
    {
        try
        {
            await control.Completion.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception) { }
        if (cancellationToken.IsCancellationRequested) return;
        await window.Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            MessageBox.Show(window, "The explicitly requested Main semantic-control endpoint stopped. Close this interface and retry the private startup.",
                "Private Main control unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            window.Close();
        });
    }

    private static async Task WatchObservationAsync(DesktopControlServer observation, OrchestratorWindow window,
        CancellationToken cancellationToken)
    {
        try
        {
            await observation.Completion.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception) { }
        if (cancellationToken.IsCancellationRequested) return;
        await window.Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            MessageBox.Show(window, "The explicitly requested read-only layout-observation endpoint stopped. Close this interface and retry the private startup.",
                "Private layout observation unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            window.Close();
        });
    }
}
