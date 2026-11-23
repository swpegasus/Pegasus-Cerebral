using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private bool startupAttempted;
    private readonly TextBlock startupSummary = Ui.Text("Cerebral uses its existing local connection. This recovery window can open while Chat is closed.", 12, Ui.Muted);
    private readonly TextBlock localRecoverySummary = Ui.Text("", 12, Ui.Muted);

    private FrameworkElement OverviewContent()
    {
        var layout = new DockPanel();
        var recovery = new StackPanel();
        startupSummary.Margin = new Thickness(0, 0, 0, 8);
        localRecoverySummary.Margin = new Thickness(0, 0, 0, 8);
        System.Windows.Automation.AutomationProperties.SetAutomationId(localRecoverySummary, "ConfigurationLocalRecoverySummary");
        recovery.Children.Add(startupSummary);
        recovery.Children.Add(localRecoverySummary);
        recovery.Children.Add(receiverObservationDiagnostic);
        var retry = Ui.Button("Retry local connection", () => Run(RetryLocalConnectionAsync));
        System.Windows.Automation.AutomationProperties.SetAutomationId(retry, "ConfigurationRetryLocalConnection");
        var log = Ui.Button("Open Worker log", () => Run(OpenLocalWorkerLogAsync));
        System.Windows.Automation.AutomationProperties.SetAutomationId(log, "ConfigurationOpenWorkerLog");
        var data = Ui.Button("Open data folder", () => Run(OpenLocalDataFolderAsync));
        System.Windows.Automation.AutomationProperties.SetAutomationId(data, "ConfigurationOpenDataFolder");
        recovery.Children.Add(Ui.Bar(retry, log, data));
        recovery.Margin = new Thickness(0, 0, 0, 12);
        RefreshLocalRecoverySummary();
        ServiceStatusChanged += (_, _) => RefreshLocalRecoverySummary();
        DockPanel.SetDock(recovery, Dock.Top);
        layout.Children.Add(recovery);
        layout.Children.Add(overview);
        return layout;
    }

    private string LocalRecoveryDataDirectory()
    {
        if (ExistingWorkerAttachment is { } attached) return attached.WorkerDataDirectory;
        var saved = ConnectionStore.Read();
        return saved.Preferences is { Transport: "pipe", LocalDataDirectory: { } data }
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(data)) : DesktopInstancePaths.Current.DataDirectory;
    }

    private void RefreshLocalRecoverySummary()
    {
        if (ExistingWorkerAttachment is { } attached)
        {
            localRecoverySummary.Text = "Existing private Worker · " + (IsConnected ? "Connected" : "Offline; owning workspace manages its lifetime") +
                "\nWorker data: " + attached.WorkerDataDirectory +
                "\nPrivate desktop preferences: " + Path.GetDirectoryName(ConnectionStore.FilePath) +
                "\nReconnect uses this exact Worker binding. This interface does not start or replace it.";
            return;
        }
        var saved = ConnectionStore.Read();
        var connection = saved.Preferences?.Transport switch
        {
            "pipe" => "Local user Worker",
            "installed-user-pipe" => "Installed desktop Worker",
            "installed-pipe" => "Installed owner Worker",
            "http" when saved.Saved => "Saved service connection",
            _ => "Local connection unavailable"
        };
        var state = saved.Error ?? (IsConnected ? "Connected" : "Offline; logs and local folders remain available");
        localRecoverySummary.Text = connection + " · " + state +
            "\nLocal data: " + LocalRecoveryDataDirectory() +
            "\nDesktop preferences: " + Path.GetDirectoryName(ConnectionStore.FilePath) +
            "\nRecovery uses the selected folders. Worker settings and backup/restore require a connection.";
    }

    private Task OpenLocalWorkerLogAsync()
    {
        var path = Path.Combine(LocalRecoveryDataDirectory(), "local-worker.log");
        if (!File.Exists(path))
            throw new FileNotFoundException("No local user Worker log exists in the selected data folder. Installed service diagnostics remain available through service status and Windows Event Viewer.", path);
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })
            ?? throw new IOException("The local Worker log could not be opened.");
        return Task.CompletedTask;
    }

    private Task OpenLocalDataFolderAsync()
    {
        var path = LocalRecoveryDataDirectory();
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("The selected data folder does not exist yet: " + path);
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })
            ?? throw new IOException("The selected data folder could not be opened.");
        return Task.CompletedTask;
    }

    private async Task RetryLocalConnectionAsync()
    {
        if (ExistingWorkerAttachment is not null) { await ConnectAsync(); return; }
        startupAttempted = false;
        await BeforeInitialConnectAsync(ConnectionStore.Read());
        await ConnectAsync();
    }

    protected override async Task BeforeInitialConnectAsync(ConnectionSnapshot saved)
    {
        // Consumer recovery must not turn an immutable attachment into discovery or enrollment.
        if (ExistingWorkerAttachment is not null) { RefreshLocalRecoverySummary(); return; }
        if (startupAttempted) return;
        startupAttempted = true;
        startupSummary.Foreground = Ui.Muted;
        try
        {
            var paths = DesktopInstancePaths.Current;
            var isolatedLocal = paths.SourceLocal || paths.Portable;
            // Ordinary launches use the shared durable binding even when Configuration is opened
            // from another payload. Source and portable launches keep their explicit data ownership.
            var enrollment = !saved.Saved && !isolatedLocal && OperatingSystem.IsWindows()
                ? InstalledWorkerBindingStore.ReadRegisteredUser() : null;
            var transport = ResolveDefaultTransport(saved, isolatedLocal, enrollment is not null);
            var preferences = saved.Preferences!;
            if (!saved.Saved)
            {
                if (transport == "pipe")
                {
                    startupSummary.Text = "Finding the Worker for this local data folder…";
                    var connection = preferences.AutoStartLocalServices
                        ? await LocalDevelopment.StartPipeAsync(Lifetime.Token, paths.DataDirectory,
                            paths.SourceLocal ? typeof(Cerebral.Desktop.Plugins.IDesktopPlugin).Assembly.Location : null)
                        : await LocalDevelopment.DiscoverPipeAsync(Lifetime.Token, paths.DataDirectory)
                            ?? throw new InvalidOperationException("The selected local Worker is stopped and automatic startup is disabled.");
                    ConnectionStore.SaveFromIntegrator(connection.ToPreferences(preferences.AutoStartLocalServices), saved.Revision);
                    startupSummary.Text = "Local Worker ready. Configuration and Chat use the same saved connection.";
                    return;
                }
                else
                {
                    InstalledUserWorkerAdmission.RequireOrdinaryUserToken();
                    if (!InstalledDefaultMatchesDataDirectory(enrollment!.DataDirectory, paths.DataDirectory))
                        throw new InvalidOperationException("The enrolled Worker does not own the selected durable data folder. Inspect the selected folders and repair the installation binding before connecting.");
                    saved = ConnectionStore.SaveFromIntegrator(
                        ConnectionPreferences.ForInstalledUserPipe(preferences.AutoStartLocalServices), saved.Revision);
                }
                preferences = saved.Preferences!;
            }
            if (preferences.Transport == "pipe")
            {
                startupSummary.Text = "Checking the local Worker…";
                await LocalServiceStartup.EnsurePipeAsync(saved, Lifetime.Token);
                startupSummary.Text = "Local Worker ready. Configuration and Chat use the same saved connection.";
                return;
            }
            if (preferences.Transport == "installed-pipe")
            {
                if (isolatedLocal)
                {
                    await LocalServiceStartup.EnsureInstalledPipeAsync(saved, Lifetime.Token);
                    return;
                }
                LocalDevelopment.RequireInstalledServiceToUserTransition();
                saved = ConnectionStore.SaveFromIntegrator(
                    ConnectionPreferences.ForInstalledUserPipe(preferences.AutoStartLocalServices), saved.Revision);
                preferences = saved.Preferences!;
            }
            if (preferences.Transport == "installed-user-pipe")
            {
                startupSummary.Text = "Checking the installed desktop Worker…";
                await LocalServiceStartup.EnsureInstalledUserPipeAsync(saved, Lifetime.Token);
                startupSummary.Text = "Installed desktop Worker ready. Configuration and Chat use the same saved connection.";
                return;
            }
            startupSummary.Text = "Checking the saved service connection…";
            var result = await new ServiceStartup(new LocalServiceStartup()).EnsureAsync(preferences.Url, preferences.AccessKey,
                preferences.AutoStartLocalServices, Lifetime.Token);
            startupSummary.Text = result.Message;
            startupSummary.Foreground = result.Warning ? Ui.ErrorBorder : Ui.Muted;
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            startupSummary.Text = "Cerebral is offline: " + ex.Message +
                " Inspect the local log or data folder, then retry. No installer was run.";
            startupSummary.Foreground = Ui.ErrorBorder;
            // A valid saved connection can recover through the monitor if its existing Worker
            // becomes ready. Missing or invalid declarations cannot become default HTTP probes.
            throw;
        }
        finally { RefreshLocalRecoverySummary(); }
    }

    internal static string ResolveDefaultTransport(ConnectionSnapshot saved, bool isolatedLocal, bool enrolledWorker)
    {
        var preferences = saved.Preferences ?? throw new InvalidOperationException(
            saved.Error ?? "The saved Cerebral connection is invalid. Inspect its selected desktop preferences folder.");
        if (saved.Saved) return preferences.Transport;
        if (isolatedLocal) return "pipe";
        if (enrolledWorker) return "installed-user-pipe";
        throw new InvalidOperationException("The installed Worker owner binding is unavailable. Inspect the selected folders and use installed Worker setup or repair from Local recovery; a different data store will not be selected.");
    }

    internal static bool InstalledDefaultMatchesDataDirectory(string enrolledData, string selectedData) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(enrolledData)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedData)), StringComparison.OrdinalIgnoreCase);
}
