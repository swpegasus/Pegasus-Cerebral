using System.IO;
using System.Windows;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

internal static class IntegratorAttachedWorkerStartup
{
    internal static IntegratorWindow CreateWindow(ExistingWorkerDesktopOptions options,
        ConfigurationManagedPluginOwnerBinding? ownerBinding = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Internal callers use the same admission boundary as the exclusive command-line route.
        options = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", options.Endpoint.PipeName,
            options.Endpoint.InstanceId, options.WorkerDataDirectory, options.DesktopDataDirectory, options.InstallationId])!;
        ownerBinding?.RequireForChild(options);
        var desktop = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(options.DesktopDataDirectory);
        Directory.CreateDirectory(desktop);
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(desktop);
        DesktopPreferenceDiagnostics.UseProcessLocalDirectory(desktop);
        var paths = DesktopInstancePaths.UseAttachedExisting(desktop, AppContext.BaseDirectory);
        var window = new IntegratorWindow(persistWindowPreferences: true, isolatedSettingsFixture: false,
            desktopPluginCatalog: new DesktopPluginCatalog(ownerBinding?.CatalogRoot ?? Path.Combine(desktop, "Plugins", "Desktop")),
            desktopPluginDataDirectory: ownerBinding?.PluginDataRoot ?? paths.PluginDataDirectory,
            seedBundledDesktopPlugins: false, managedPluginOwnerBinding: ownerBinding);
        window.UseExistingLocalWorker(options.Endpoint, options.WorkerDataDirectory, options.InstallationId);
        window.PrepareExistingWorkerRecovery();
        return window;
    }

    internal static int Run(string[] args)
    {
        // Shared parsing validates the complete owner binding before preferences, singleton ownership,
        // plugin discovery or any Worker request. The UI-root key stays unchanged for every owner.
        var launch = ConfigurationManagedPluginOwnerBinding.ParseAttachedArguments(args);
        var options = launch.Options;
        var identity = ConfigurationInstanceBinding.Create(options, launch.OwnerBinding?.OwnerBindingId);
        using var instance = new DesktopApplicationInstance(identity.ApplicationId, identity.Identity);
        if (!instance.IsPrimary)
        {
            if (instance.RequestActivation()) return 0;
            throw new InvalidOperationException("This private Configuration state belongs to another Worker or managed plugin owner binding. Close that interface first.");
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        // Private stores must be selected before theme or preference access.
        var window = CreateWindow(options, launch.OwnerBinding);
        Ui.ApplyTheme(app);
        app.MainWindow = window;
        // This optional read-only channel exists only for this normal attached primary lifetime.
        var observation = window.StartReceiverObservation(options);
        try
        {
            instance.Listen(app.Dispatcher, () => DesktopApplicationInstance.Show(window));
            return app.Run(window);
        }
        finally { if (observation is not null) observation.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}

public sealed partial class IntegratorWindow
{
    internal void PrepareExistingWorkerRecovery()
    {
        if (ExistingWorkerAttachment is null) throw new InvalidOperationException("An existing Worker binding is required.");
        startupSummary.Text = "Attached to an existing private Worker. Its owning workspace controls startup and shutdown.";
        RefreshLocalRecoverySummary();
    }

    private void RequireWorkerManagementAllowed(string action)
    {
        if (ExistingWorkerAttachment is not null)
            throw new InvalidOperationException("This private interface cannot " + action +
                ". Its owning workspace controls the fixed Worker binding.");
    }

    private Task OpenCerebralApplication()
    {
        RequireWorkerManagementAllowed("open another Cerebral workspace");
        ManagementProcess.OpenApplication("Orchestrator");
        return Task.CompletedTask;
    }
}
