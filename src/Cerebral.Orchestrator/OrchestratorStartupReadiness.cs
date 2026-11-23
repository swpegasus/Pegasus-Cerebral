using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

internal enum MainStartupMode { AttachedExisting, SourceLocal, Installed }

public sealed partial class OrchestratorWindow
{
    private bool mainStartupPrepared;
    private CancellationToken mainStartupCancellation;

    internal Task<DesktopStartupOutcome> BeginStartupBeforeShowAsync()
    {
        Dispatcher.VerifyAccess();
        Lifetime.Token.ThrowIfCancellationRequested();
        var initialConnection = BeginInitialConnectionAsync();
        BeginDesktopPluginStartup();
        return initialConnection;
    }

    internal bool PreparedForStartupReveal =>
        mainStartupPrepared && IsConnected && !mainStartupCancellation.IsCancellationRequested &&
        !Lifetime.IsCancellationRequested && !Dispatcher.HasShutdownStarted;

    internal void InvalidateStartupPreparation() => mainStartupPrepared = false;

    internal void PrepareUnshownStartupLayout(CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        Lifetime.Token.ThrowIfCancellationRequested();
        if (IsVisible || !IsConnected || Content is not System.Windows.FrameworkElement content)
            throw new MainStartupException("Interface", new InvalidOperationException(
                "Main must remain unshown with its verified Worker and workspace available until startup completes."));
        // Layout the existing workspace without presenting a top-level HWND or
        // synthesizing Loaded. Real view-only Loaded work still runs on first Show.
        var width = Math.Max(MinWidth, double.IsFinite(Width) ? Width : MinWidth);
        var height = Math.Max(MinHeight, double.IsFinite(Height) ? Height : MinHeight);
        var size = new System.Windows.Size(width, height);
        content.Measure(size);
        content.Arrange(new System.Windows.Rect(size));
        content.UpdateLayout();
        mainStartupCancellation = cancellationToken;
        mainStartupPrepared = true;
    }

    internal async Task CompleteStartupAsync(MainStartupMode mode, CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess(); cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected) throw new MainStartupException("Worker connection", new InvalidOperationException("The verified Worker connection is no longer available."));
        // Explicit attachments retain their read/attach-only startup authority. In
        // particular, this route must never prepare a model or start a Worker.
        if (mode == MainStartupMode.AttachedExisting) return;
        var client = Client; var identity = ServiceConnectionIdentity;
        var owner = desktopPluginOwner; var runtime = desktopPlugins;
        void RequireOwner()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected || !ReferenceEquals(Client, client) || ServiceConnectionIdentity != identity ||
                !ReferenceEquals(desktopPluginOwner, owner) || owner?.MatchesOwner() != true || !ReferenceEquals(desktopPlugins, runtime))
                throw new InvalidOperationException("The Worker or plugin owner changed during startup. Retry with the selected connection.");
        }
        RequireOwner();
        if (connectorStartup.ShouldAttempt(mode))
        {
            ReportStartupProgress("User connector", "Checking the provisioned optional user connector…");
            using var connectorLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token);
            var connector = await connectorStartup.CompleteAsync(mode, new LocalServiceStartup(), RequireOwner, connectorLifetime.Token);
            RequireOwner();
            if (connector is not null)
            {
                ReportStartupProgress("User connector", connector.Message, connector.Warning);
                if (connector.Warning) MountDesktopStatusItems();
            }
        }
        try
        {
            RequireOwner();
            if (!desktopPluginsStarted || runtime is null) throw new InvalidOperationException("Desktop plugin discovery did not finish.");
            if (desktopPluginStartupMountError is { } mountError) throw mountError;
            ReportStartupProgress("Plugins", "Waiting for enabled plugin initialization and declared preferences…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var receipts = await runtime.WaitForStartupAsync(timeout.Token);
            RequireOwner();
            if (desktopPluginStartupMountError is { } laterMountError) throw laterMountError;
            foreach (var receipt in receipts)
                ReportStartupProgress("Plugins", receipt.Contract == "startup-facet-completed"
                    ? receipt.Id + " · Declared startup/preferences completed."
                    : receipt.Id + " · Synchronous initialization returned; no asynchronous readiness was declared.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MainStartupPluginRecovery? recovery = null;
            if (runtime is not null && MainStartupPluginRecovery.Eligible(mode, error))
            {
                try { RequireOwner(); recovery = MainStartupPluginRecovery.Capture(mode, error, runtime, desktopPluginCatalog); }
                catch (Exception observation) when (observation is not OutOfMemoryException)
                { error.Data["MainStartupRecovery"] = observation; }
            }
            throw new MainStartupException("Plugins / preferences", error,
                retryAllowed: error is not DesktopPluginStartupException { CallbackStillRunning: true }, pluginRecovery: recovery);
        }
        if (mode == MainStartupMode.Installed)
        {
            try { await PrepareSelectedLocalMainModelAsync(cancellationToken); RequireOwner(); }
            catch (MainStartupException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { cancellationToken.ThrowIfCancellationRequested(); throw new MainStartupException("Selected Main model", error); }
        }
        RequireOwner();
    }

    private async Task PrepareSelectedLocalMainModelAsync(CancellationToken cancellationToken)
    {
        if (!ServiceIsLocal) throw new InvalidOperationException("Main startup may initialize only its selected local Worker model. Inspect the saved connection in Configuration.");
        var selected = RequireStartupLocalProfile(TryConversationProfile());
        var client = Client; var identity = ServiceConnectionIdentity;
        var profileId = selected.Id; var modelId = selected.ModelId;
        void RequireChoice()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = RequireStartupLocalProfile(TryConversationProfile());
            if (!IsConnected || !ReferenceEquals(Client, client) || ServiceConnectionIdentity != identity ||
                current.Id != profileId || current.ModelId != modelId)
                throw new InvalidOperationException("The selected local Main profile, model or Worker changed during startup.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The existing native model startup permits five minutes. Keep a bounded
        // margin for its own cleanup; cancellation never means permission to replay.
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        var token = timeout.Token;
        RequireChoice();
        var pause = await client.GetAsync<ResourcePauseStatus>("resources.local.status", cancellationToken: token);
        if (!SourceLocalStartup.ShouldLoadLocalModel(pause.State))
            throw new InvalidOperationException("Local model resources are " + pause.State + ". " + pause.Detail + " Inspect Configuration; startup will not resume or change this state.");
        var host = await client.GetAsync<HostInfo>("health", cancellationToken: token);
        ServiceConnection.ValidateHost(host); RequireChoice();
        var diagnostics = await client.GetAsync<WorkerModelDiagnostics>("models.runtime.status", cancellationToken: token);
        RequireStartupRuntimeOwner(host.InstallationId, diagnostics);
        // Runtime observation begins before the native /health loop completes.
        // Only the model record's loaded commit follows successful startup.
        var models = await client.GetAsync<List<ModelRecord>>("models.list", cancellationToken: token);
        var observed = models.SingleOrDefault(model => model.Id == modelId)
            ?? throw new InvalidOperationException("The selected local Main model is not registered.");
        RequireChoice();
        if (observed.State == "loading")
            throw new InvalidOperationException("The selected Main model is already initializing. Startup remains stopped; wait for that operation to settle before retrying. No second load was dispatched.");
        if (!StartupModelPrepared(modelId, observed, diagnostics))
        {
            if (diagnostics.Models.Count != 0)
                throw new InvalidOperationException("Another local model or an unconfirmed model process is present. Startup will not replace it. Inspect Configuration before retrying.");
            RequireChoice();
            ReportStartupProgress("Selected Main model", "Initializing " + selected.Name + " · " + modelId + "…");
            // One owner-typed load, never generation, model substitution, remote
            // routing, download, pause/resume or a retry loop.
            CommandResult loaded;
            try { loaded = await client.CommandAsync("models.load-tracked", new { id = modelId }, cancellationToken: token); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new MainStartupException("Selected Main model · initialization unconfirmed", error, retryAllowed: false);
            }
            if (!loaded.Success) throw new InvalidOperationException(loaded.Error ?? "The selected local Main model could not initialize.");
            var receipt = CerebralJson.Read<WorkerModelLoadResult>(loaded.Data);
            if (receipt.Model.Id != modelId || receipt.Model.State != "loaded")
                throw new InvalidOperationException("The model initialization receipt does not confirm the selected local Main model.");
            RequireChoice();
            diagnostics = await client.GetAsync<WorkerModelDiagnostics>("models.runtime.status", cancellationToken: token);
            RequireStartupRuntimeOwner(host.InstallationId, diagnostics);
            models = await client.GetAsync<List<ModelRecord>>("models.list", cancellationToken: token);
            observed = models.SingleOrDefault(model => model.Id == modelId)
                ?? throw new InvalidOperationException("The selected Main model disappeared after initialization.");
            if (!StartupModelPrepared(modelId, observed, diagnostics))
                throw new InvalidOperationException("The selected Main model did not report a live initialized runtime. Startup remains stopped.");
        }
        RequireChoice();
        ReportStartupProgress("Selected Main model", "The selected local Main model has an initialized runtime.");
    }

    internal static AgentProfile RequireStartupLocalProfile(AgentProfile? selected)
    {
        if (selected is null || string.IsNullOrWhiteSpace(selected.Id) || string.IsNullOrWhiteSpace(selected.ModelId) ||
            selected.WorkerId != "local" || selected.ModelWorkerId != "local")
            throw new InvalidOperationException("The selected Main conversation must identify its existing local profile and local model. Startup will not choose another profile or prepare a remote model. Inspect Configuration.");
        return selected;
    }

    internal static void RequireStartupRuntimeOwner(string? installationId, WorkerModelDiagnostics diagnostics)
    {
        if (string.IsNullOrWhiteSpace(installationId) || diagnostics.WorkerInstallationId != installationId || diagnostics.Models is null ||
            diagnostics.Models.Count > 256 || diagnostics.Models.Any(model => model is null || string.IsNullOrWhiteSpace(model.Id)) ||
            diagnostics.Models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != diagnostics.Models.Count)
            throw new InvalidOperationException("Model runtime diagnostics do not belong to the verified Worker or contain ambiguous model identities.");
    }

    internal static bool StartupModelPrepared(string modelId, ModelRecord model, WorkerModelDiagnostics diagnostics) =>
        model.Id == modelId && model.State == "loaded" && string.IsNullOrWhiteSpace(model.Error) &&
        diagnostics.Models.SingleOrDefault(item => item.Id == modelId) is { ProcessId: > 0, WorkingSetMb: > 0 };
}
