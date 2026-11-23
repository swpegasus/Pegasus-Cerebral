using System.Windows;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Keep WPF types out of this bootstrap so JIT/type initialization cannot set system DPI first.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        try { return RunWpf(args); }
        catch (Exception error) when (IsPrivateAttachmentInvocation(args))
        {
            return ReportAttachFailure(error);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ReportAttachFailure(Exception error)
    {
        Console.Error.WriteLine(error.Message);
        MessageBox.Show(error.Message, "Pegasus Cerebral Configuration attachment", MessageBoxButton.OK, MessageBoxImage.Error);
        return 2;
    }

    // A reserved private suffix must never fall through to discovery when its attachment is malformed.
    private static bool IsPrivateAttachmentInvocation(string[] args) =>
        args.Contains("--attach-existing", StringComparer.Ordinal) ||
        args.Any(value => value.StartsWith("--managed-plugin-owner", StringComparison.OrdinalIgnoreCase));

    internal static int DispatchStartup(string[] args, Func<string[], int> attached, Func<string[], int> standalone) =>
        IsPrivateAttachmentInvocation(args) ? attached(args) : standalone(args);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunWpf(string[] args) =>
        DispatchStartup(args, IntegratorAttachedWorkerStartup.Run, RunStandaloneWpf);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunStandaloneWpf(string[] args)
    {
        if (args.Contains("--configuration-control-call", StringComparer.Ordinal))
            return SemanticControlCommand.RunAsync(args, "--configuration-control-call", ConfigurationNormalControl.Product,
                ConfigurationNormalControl.PipePrefix, typeof(Program).Assembly.Location).GetAwaiter().GetResult();
        if (args.Length == 1 && args[0] == "--check-normal-configuration-control")
            return ConfigurationNormalControlChecks.Run();
        var explicitPortable = args.Length == 1 && args[0] == "--portable-package";
        if (explicitPortable) args = [];
        else if (args.Contains("--portable-package", StringComparer.Ordinal))
            throw new ArgumentException("Use --portable-package only from the portable launcher.");
        if (args.Contains("--check-plugin-configuration-lifetime", StringComparer.Ordinal))
        {
            if (args.Length != 1) throw new ArgumentException("Use --check-plugin-configuration-lifetime without other arguments.");
            return IntegratorPluginConfigurationLifetimeChecks.Run();
        }
        if (args.Contains("--check-first-run-setup-coordinator", StringComparer.Ordinal))
        {
            if (args.Length != 1) throw new ArgumentException("Use --check-first-run-setup-coordinator without other arguments.");
            return FirstRunSetupCoordinatorChecks.RunAsync().GetAwaiter().GetResult();
        }
        if (args.Contains("--check-attached-configuration", StringComparer.Ordinal))
        {
            if (args.Length != 1) throw new ArgumentException("The owned Configuration attachment checks accept no other options.");
            return IntegratorAttachedWorkerChecks.Run();
        }
        if (args.Contains("--check-agent-helper-workflow", StringComparer.Ordinal))
        {
            var navigationOnly = args.Length == 2 && args[1] == "--navigation-only";
            if (args.Length is < 1 or > 2 || args[0] != "--check-agent-helper-workflow" ||
                !navigationOnly && args.Skip(1).Any(x => x.StartsWith("--", StringComparison.Ordinal)))
                throw new ArgumentException("Use --check-agent-helper-workflow with an optional existing capture directory or --navigation-only.");
            return AgentHelperWorkflowChecks.Run(navigationOnly ? null : args.ElementAtOrDefault(1), navigationOnly);
        }
        if (Cerebral.Core.ApplicationVersion.TryWrite(args, "Integrator")) return 0;
        if (Array.IndexOf(args, "--inspect-worker-route") is var routeIndex && routeIndex >= 0)
        {
            if (args.ElementAtOrDefault(routeIndex + 1) is not { } endpoint) return 2;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                endpoint = WorkerNetworkRoute.ParseEndpoint(endpoint).Display,
                route = WorkerNetworkRoute.Describe(endpoint),
                localOnly = true, workerProbed = false
            }));
            return 0;
        }
        var packageRoot = Cerebral.Core.InstallationPaths.InstallDirectory;
        if (explicitPortable ||
            System.IO.File.Exists(System.IO.Path.Combine(packageRoot, "Start-CerebralPortable.cmd")) ||
            System.IO.File.Exists(System.IO.Path.Combine(packageRoot, "portable-manifest.json")))
        {
            try { Cerebral.Core.PortablePackageAdmission.VerifyAt(packageRoot); }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Pegasus Cerebral portable package", MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
        }
        if (args.Contains("--check-connection-startup", StringComparer.Ordinal))
        {
            Console.WriteLine($"Configuration connection startup: {IntegratorConnectionStartupChecks.Verify()} deterministic checks passed.");
            return 0;
        }
        if (args.Contains("--check-package-activation", StringComparer.Ordinal)) return IntegratorPackageActivationQueue.Check();
        DesktopInstancePaths instancePaths;
        try
        {
            instancePaths = DesktopInstancePaths.InitializeConfiguration(args, ManagementProcess.UsesRegisteredInstallation,
                ManagementProcess.SourceProgramDirectory);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Pegasus Cerebral Configuration startup", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
        var desktopPluginCatalog = new Cerebral.Desktop.Plugins.DesktopPluginCatalog(instancePaths.UserPluginDirectory);
        if (args.Contains("--inspect-desktop-instance", StringComparer.Ordinal))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceLocal = instancePaths.SourceLocal,
                connectionPath = ConnectionStore.FilePath,
                pluginDirectory = desktopPluginCatalog.RootDirectory,
                pluginDataDirectory = instancePaths.PluginDataDirectory,
                productPaths = instancePaths.ProductPaths(),
                plugins = desktopPluginCatalog.List().Take(100)
                    .Select(plugin => new { plugin.Id, plugin.Name, plugin.Version, plugin.Enabled,
                        error = plugin.Error is null ? null : plugin.Error[..Math.Min(200, plugin.Error.Length)] })
            }, Cerebral.Core.CerebralJson.Options));
            return 0;
        }
        var app = new Application(); Ui.ApplyTheme(app);
        if (args.Contains("--check-settings-presets", StringComparer.Ordinal) ||
            args.Contains("--check-settings-layout", StringComparer.Ordinal) ||
            args.Contains("--check-workflow-queue-ui", StringComparer.Ordinal) ||
            args.Contains("--check-engine-ui", StringComparer.Ordinal) ||
            args.Contains("--check-prompt-cache-ui", StringComparer.Ordinal) ||
            args.Contains("--check-configuration-preferences", StringComparer.Ordinal) ||
            args.Contains("--check-registration-edits", StringComparer.Ordinal) ||
            args.Contains("--check-settings-autosave", StringComparer.Ordinal))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exit = 0;
                try
                {
                    var window = new IntegratorWindow(persistWindowPreferences: false);
                    try
                    {
                        if (Array.IndexOf(args, "--check-settings-presets") is var presetsIndex && presetsIndex >= 0)
                            await window.CheckSettingsPresetsAsync(args.ElementAtOrDefault(presetsIndex + 1) ?? throw new ArgumentException("Settings preset checks require a capture directory."));
                        else if (Array.IndexOf(args, "--check-settings-layout") is var settingsIndex && settingsIndex >= 0)
                            await window.CheckSettingsLayoutAsync(args.ElementAtOrDefault(settingsIndex + 1) ?? throw new ArgumentException("Settings layout inspection requires a capture directory."));
                        else if (Array.IndexOf(args, "--check-engine-ui") is var engineIndex && engineIndex >= 0)
                            await window.CheckEnginePageAsync(args.ElementAtOrDefault(engineIndex + 1) ?? throw new ArgumentException("Engine UI inspection requires a capture file path."));
                        else if (Array.IndexOf(args, "--check-prompt-cache-ui") is var cacheIndex && cacheIndex >= 0)
                            await window.CheckPromptCachePageAsync(args.ElementAtOrDefault(cacheIndex + 1) ?? throw new ArgumentException("Cache diagnostics inspection requires a capture file path."));
                        else if (args.Contains("--check-settings-autosave", StringComparer.Ordinal)) await window.CheckSettingsAutoSaveAsync();
                        else if (args.Contains("--check-configuration-preferences", StringComparer.Ordinal)) await window.CheckConfigurationPreferencesAsync();
                        else if (args.Contains("--check-registration-edits", StringComparer.Ordinal)) await window.CheckRegistrationEditsAsync();
                        else await window.CheckWorkflowQueueUiAsync();
                    }
                    finally { window.Close(); }
                }
                catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
                finally { app.Shutdown(exit); }
            }));
            return app.Run();
        }
        // Direct diagnostics terminate without acquiring the normal application instance.
        var diagnosticWindow = args.Contains("--check-ui", StringComparer.Ordinal) ||
            args.Contains("--diagnostics", StringComparer.Ordinal) ||
            args.Contains("--check-service-ui", StringComparer.Ordinal)
            ? new IntegratorWindow()
            : null;
        if (args.Contains("--check-ui", StringComparer.Ordinal) || args.Contains("--diagnostics", StringComparer.Ordinal))
            return DesktopDiagnostics.Check(diagnosticWindow!);
        if (Array.IndexOf(args, "--check-service-ui") is var index && index >= 0)
            return DesktopDiagnostics.CheckService(app, diagnosticWindow!, args.ElementAtOrDefault(index + 1) ?? Cerebral.Core.CerebralDefaults.WorkerUrl, null);
        IntegratorPackageOpenRequest? packageRequest;
        try { packageRequest = IntegratorPackageActivationQueue.Parse(args); }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Pegasus Cerebral package", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
        var packageQueue = new IntegratorPackageActivationQueue();
        if (packageRequest is not null)
        {
            try { packageQueue.Enqueue(packageRequest); }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Pegasus Cerebral package", MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
        }
        using var instance = new DesktopApplicationInstance(ConfigurationPluginSettingsChannel.ApplicationId, instancePaths.ApplicationIdentity);
        if (!instance.IsPrimary)
        {
            if (instance.RequestActivation()) return 0;
            MessageBox.Show("Another Configuration window owns a different desktop data root. Close that window before opening this instance.\n\nRequested: " +
                instance.RequestedIdentity + "\nExisting: " + instance.OwnerIdentity,
                "Pegasus Cerebral Configuration", MessageBoxButton.OK, MessageBoxImage.Information);
            return 2;
        }
        var window = new IntegratorWindow(persistWindowPreferences: true, desktopPluginCatalog: desktopPluginCatalog,
            desktopPluginDataDirectory: instancePaths.PluginDataDirectory);
        SemanticControlServer? normalControl = null;
        var normalControlStarted = false;
        window.Loaded += async (_, _) =>
        {
            if (normalControlStarted) return;
            normalControlStarted = true;
            try
            {
                normalControl = await window.StartNormalControlAsync(instancePaths.UserDataDirectory);
                _ = ObserveNormalControlAsync(normalControl, window);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!window.Dispatcher.HasShutdownStarted) window.ReportNormalControlUnavailable(); }
        };
        if (!instancePaths.SourceLocal) window.ListenForOrdinaryPluginSettings(instancePaths.UserDataDirectory);
        var draining = false;
        async void DrainPackages()
        {
            if (draining) return;
            draining = true;
            try
            {
                // A second shell activation can arrive while a review dialog is
                // open. Re-read the durable queue after every batch so that signal
                // cannot be lost merely because one drain was already active.
                while (true)
                {
                    var pendingBatch = packageQueue.Pending();
                    if (pendingBatch.Count == 0) break;
                    foreach (var pending in pendingBatch)
                    {
                        await window.OpenPackageRequestAsync(pending.Request);
                        packageQueue.Complete(pending);
                    }
                }
            }
            finally { draining = false; }
        }
        window.Loaded += (_, _) => DrainPackages();
        instance.Listen(app.Dispatcher, () => { DesktopApplicationInstance.Show(window); DrainPackages(); });
        try { return app.Run(window); }
        finally { if (normalControl is not null) normalControl.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static async Task ObserveNormalControlAsync(SemanticControlServer server, IntegratorWindow window)
    {
        try { await server.Completion.ConfigureAwait(false); } catch (Exception) { }
        if (server.IsStopping || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
        try { await window.Dispatcher.InvokeAsync(window.ReportNormalControlUnavailable); }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) when (window.Dispatcher.HasShutdownStarted) { }
    }
}
