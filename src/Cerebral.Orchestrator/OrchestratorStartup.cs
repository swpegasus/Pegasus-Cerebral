using System.Windows.Media;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private bool automaticServiceStartupAttempted;
    private readonly MainConnectorStartup connectorStartup = new();

    protected override async Task BeforeInitialConnectAsync(ConnectionSnapshot saved)
    {
        if (automaticServiceStartupAttempted) return;
        automaticServiceStartupAttempted = true;
        try
        {
            var paths = DesktopInstancePaths.Current;
            if (!saved.Saved && !paths.SourceLocal && !paths.Portable)
            {
                InstalledUserWorkerAdmission.RequireOrdinaryUserToken();
                var enrollment = InstalledWorkerBindingStore.ReadRegisteredUser()
                    ?? throw new InvalidOperationException("The installed desktop Worker enrollment is unavailable.");
                if (!InstalledWorkerBinding.PathEquals(enrollment.DataDirectory, paths.DataDirectory, true))
                    throw new UnauthorizedAccessException("The installed desktop Worker owns a different data directory.");
                saved = ConnectionStore.SaveFromIntegrator(ConnectionPreferences.ForInstalledUserPipe(), saved.Revision);
            }
            var preferences = saved.Preferences
                ?? throw new InvalidOperationException(saved.Error ?? "Repair the saved connection in Pegasus Cerebral Configuration.");
            if (preferences.Transport == "pipe")
            {
                await LocalServiceStartup.EnsurePipeAsync(saved, Lifetime.Token);
                return;
            }
            if (preferences.Transport == "installed-pipe")
            {
                if (paths.SourceLocal || paths.Portable)
                {
                    await LocalServiceStartup.EnsureInstalledPipeAsync(saved, Lifetime.Token);
                    return;
                }
                // An ordinary desktop launch moves its own saved connection to the signed-in
                // Worker mode. The headless SCM contract remains available to service clients.
                LocalDevelopment.RequireInstalledServiceToUserTransition();
                var replacement = ConnectionStore.SaveFromIntegrator(
                    ConnectionPreferences.ForInstalledUserPipe(preferences.AutoStartLocalServices), saved.Revision);
                await LocalServiceStartup.EnsureInstalledUserPipeAsync(replacement, Lifetime.Token);
                connectorStartup.NoteReadyWorker(replacement.Preferences!.AutoStartLocalServices, paths.SourceLocal, paths.Portable);
                return;
            }
            if (preferences.Transport == "installed-user-pipe")
            {
                await LocalServiceStartup.EnsureInstalledUserPipeAsync(saved, Lifetime.Token);
                connectorStartup.NoteReadyWorker(preferences.AutoStartLocalServices, paths.SourceLocal, paths.Portable);
                return;
            }
            var result = await new ServiceStartup(new LocalServiceStartup()).EnsureAsync(
                preferences.Url, preferences.AccessKey, preferences.AutoStartLocalServices, Lifetime.Token);
            if (!result.Warning) return;
            sessionState.Text = result.Message;
            sessionState.Foreground = Ui.ErrorBorder;
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // The ordinary connection attempt still runs. A separately started worker can remain usable
            // even when this user-session startup path cannot start an optional provider.
            sessionState.Text = "Automatic local startup did not complete: " + ex.Message;
            sessionState.Foreground = Ui.ErrorBorder;
        }
    }
}

internal sealed record MainConnectorStartupResult(string State, bool Warning)
{
    internal string Message => State switch
    {
        "running" => "The authenticated user connector is running. Provider readiness is checked separately.",
        "not-configured" or "empty" => "No provisioned user providers need connector startup.",
        _ => "The optional user connector did not confirm startup (" + State + "). " +
            "The Worker remains usable. Inspect User-session connector in Configuration before another start."
    };
}

// This one-shot Main policy runs after the initial Worker connection, outside its
// readiness deadline. The existing manager retains connector ownership/admission.
internal sealed class MainConnectorStartup
{
    private bool eligible, attempted;
    internal MainConnectorStartupResult? Outcome { get; private set; }

    internal void NoteReadyWorker(bool automatic, bool sourceLocal, bool portable) =>
        eligible = automatic && !sourceLocal && !portable;

    internal bool ShouldAttempt(MainStartupMode mode) => mode == MainStartupMode.Installed && eligible && !attempted;

    internal async Task<MainConnectorStartupResult?> CompleteAsync(MainStartupMode mode,
        IServiceStartupOperations operations, Action requireOwner, CancellationToken token)
    {
        if (!ShouldAttempt(mode)) return null;
        ArgumentNullException.ThrowIfNull(operations); ArgumentNullException.ThrowIfNull(requireOwner);
        token.ThrowIfCancellationRequested(); requireOwner();
        attempted = true; // An uncertain status or accepted start is never replayed by Main.
        var status = await ObserveAsync(operations.ConnectorStatusAsync, requireOwner, token);
        if (status is null) return Outcome = new("unavailable", true);
        if (status.State == "stopped" && status.ProviderCount > 0)
        {
            status = await ObserveAsync(operations.StartConnectorAsync, requireOwner, token);
            if (status is null) return Outcome = new("unconfirmed", true);
        }
        return Outcome = status.State switch
        {
            "running" => new("running", false),
            "not-configured" => new("not-configured", false),
            "stopped" when status.ProviderCount == 0 => new("empty", false),
            "stopped" or "starting" or "stopping" or "unmanaged" or "mismatch" or "invalid-config" or "unavailable" => new(status.State, true),
            _ => new("unavailable", true)
        };
    }

    private static async Task<ConnectorStatus?> ObserveAsync(Func<CancellationToken, Task<ConnectorStatus>> operation,
        Action requireOwner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); requireOwner();
        ConnectorStatus? status = null;
        try { status = await operation(token); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // No provider exception text, credentials or paths enter the UI. A failed
            // wait does not establish that an accepted connector start was reversed.
        }
        // Manager.Start can wrap cancellation in InvalidOperationException. Required
        // cancellation and owner fences therefore run after either outcome, OUTSIDE catch.
        token.ThrowIfCancellationRequested(); requireOwner();
        return status;
    }
}
