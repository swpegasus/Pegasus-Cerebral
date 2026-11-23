using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private RendererLayoutSession? mainLayoutSession;
    private RendererLayoutClient? mainLayoutOwner;
    private CerebralClient? mainLayoutTransport;
    private DispatcherTimer? mainLayoutTimer;
    private bool mainLayoutRead;
    private bool mainLayoutClosing;
    private bool mainLayoutCloseAllowed;
    private bool mainLayoutClosed;
    private Func<bool>? mainLayoutHidesOnClose;
    private Task? mainLayoutSaveTask;
    private RendererLayoutClient? mainLayoutSavingOwner;
    private string? mainLayoutFailureMessage;

    // The tray owns whether Closing means hide or Exit. A cancelled/hiding Closing event
    // must never leave a permanent renderer-close guard or schedule a second Close.
    internal void ConfigureTrayLifetime(Func<bool> hidesOnClose) =>
        mainLayoutHidesOnClose = hidesOnClose ?? throw new ArgumentNullException(nameof(hidesOnClose));

    private void InitializeMainLayout(bool persist)
    {
        if (!persist) return;
        if (mainLayoutSession is not null) throw new InvalidOperationException("Main layout persistence is already initialized.");
        // Read the legacy geometry once; all subsequent persistence uses the central opaque layout.
        WindowPreferenceStore.RestoreLegacyGeometry(this, "Orchestrator");
        mainLayoutSession = new("Orchestrator", OrchestratorSettingsLayoutCodec.SchemaVersion,
            values => OrchestratorSettingsLayoutCodec.Read(values), CaptureMainLayout, ApplyMainLayoutAsync);
        mainLayoutTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(450) };
        mainLayoutTimer.Tick += async (_, _) =>
        {
            mainLayoutTimer.Stop();
            if (mainLayoutOwner is not { IsCurrent: true } owner || mainLayoutSession is not { Supported: true, Blocked: false }) return;
            try { await FlushMainLayoutAsync(owner); }
            catch (Exception error) { ReportMainLayoutSaveFailure(error); }
        };
        void GeometryChanged(object? _, EventArgs __)
        {
            if (mainLayoutRead) MainLayoutChanged();
        }
        LocationChanged += GeometryChanged; SizeChanged += GeometryChanged; StateChanged += GeometryChanged;
        Closing += CloseWithMainLayout;
        Closed += (_, _) => { mainLayoutClosed = true; mainLayoutTimer.Stop(); mainLayoutTransport?.Dispose(); };
    }

    private void MainLayoutChanged()
    {
        if (!IsLoaded || mainLayoutSession is null || mainLayoutSession.Hydrating || mainLayoutClosed) return;
        mainLayoutSession.Changed();
        QueueMainLayoutSave();
    }

    private void QueueMainLayoutSave()
    {
        if (mainLayoutClosing || mainLayoutClosed || !mainLayoutRead || mainLayoutTimer is null ||
            mainLayoutSession is not { Supported: true, Blocked: false, Dirty: true } || mainLayoutOwner is not { IsCurrent: true }) return;
        mainLayoutTimer.Stop(); mainLayoutTimer.Start();
    }

    private async Task HydrateMainLayoutAsync(HostInfo info)
    {
        if (mainLayoutSession is null) return;
        var ownerClient = Client;
        var transport = CreateIndependentClient();
        var priorTransport = mainLayoutTransport;
        mainLayoutTransport = transport;
        var owner = new RendererLayoutClient("Orchestrator",
            (operation, arguments, token) => transport.CommandAsync(operation, arguments, cancellationToken: token),
            () => IsConnected && ReferenceEquals(ownerClient, Client) && ReferenceEquals(transport, mainLayoutTransport),
            RendererLayoutClient.CaptureBackendIdentity(info, ServiceConnection.Transport, ServiceConnectionIdentity));
        try
        {
            await LoadMainLayoutOwnerAsync(owner);
        }
        catch (Exception error)
        {
            // Optional layout storage must not prevent the rest of Main from connecting.
            SetStatus("Main layout could not be loaded; it will not be overwritten: " + error.Message, true);
        }
        finally { priorTransport?.Dispose(); }
    }

    private async Task LoadMainLayoutOwnerAsync(RendererLayoutClient owner)
    {
        if (mainLayoutSession is null) return;
        mainLayoutTimer?.Stop(); mainLayoutRead = false; mainLayoutOwner = owner;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await mainLayoutSession.LoadAsync(owner, false, timeout.Token);
        if (!ReferenceEquals(mainLayoutOwner, owner) || !owner.IsCurrent) return;
        mainLayoutRead = true;
        if (mainLayoutSession.Blocked) ReportMainLayoutSaveFailure(new InvalidOperationException(mainLayoutSession.Status));
        else ClearMainLayoutFailure();
        QueueMainLayoutSave();
    }

    private Task FlushMainLayoutAsync(RendererLayoutClient owner)
    {
        if (mainLayoutSaveTask is { IsCompleted: false } running && ReferenceEquals(mainLayoutSavingOwner, owner)) return running;
        mainLayoutSavingOwner = owner;
        return mainLayoutSaveTask = FlushLatestMainLayoutAsync(owner);
    }

    private async Task FlushLatestMainLayoutAsync(RendererLayoutClient owner)
    {
        if (mainLayoutSession is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        do
        {
            await mainLayoutSession.FlushAsync(owner, false, timeout.Token);
        }
        while (mainLayoutSession.Dirty && !mainLayoutSession.Blocked && owner.IsCurrent);
        ClearMainLayoutFailure();
    }

    private void ReportMainLayoutSaveFailure(Exception error)
    {
        if (mainLayoutClosed) return;
        var message = mainLayoutSession is { Blocked: true } session ? session.Status :
            "Layout save did not complete. The current window changes are retained: " + error.Message;
        mainLayoutFailureMessage = message;
        SetStatus(message, true);
    }

    private void ClearMainLayoutFailure()
    {
        if (mainLayoutFailureMessage is null || mainLayoutClosed || mainLayoutSession is null || mainLayoutSession.Blocked) return;
        if (Status.Text == mainLayoutFailureMessage) SetStatus(mainLayoutSession.Status);
        mainLayoutFailureMessage = null;
    }

    private JsonElement CaptureMainLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return OrchestratorSettingsLayoutCodec.Write(new(new(new(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            WindowState == WindowState.Maximized), workspacePreferredWidth, activityPreferredWidth, composerPreferredHeight));
    }

    private async Task ApplyMainLayoutAsync(JsonElement? values)
    {
        if (values is null) return; // No central record: keep the read-only legacy geometry fallback.
        var layout = OrchestratorSettingsLayoutCodec.Read(values.Value);
        workspacePreferredWidth = layout?.WorkspacePreferredWidth ?? 248;
        activityPreferredWidth = layout?.ActivityPreferredWidth ?? 340;
        composerPreferredHeight = layout?.ComposerPreferredHeight;
        WindowState = WindowState.Normal;
        if (layout is null)
        {
            Width = 1280; Height = 840;
            var work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
        }
        else
        {
            var bounds = layout.Window.NormalBounds;
            Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (layout.Window.Maximized) WindowState = WindowState.Maximized;
        }
        WindowSizing.FitCurrentMonitor(this);
        UpdateConversationLayout();
        // Monitor fitting and native layout may queue geometry events; keep them within hydration.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private async void CloseWithMainLayout(object? sender, CancelEventArgs e)
    {
        if (e.Cancel) return; // Another owner blocked close; do not schedule a second attempt.
        if (mainLayoutCloseAllowed || !mainLayoutRead || mainLayoutSession is not { Supported: true, Blocked: false, Dirty: true } ||
            mainLayoutOwner is not { IsCurrent: true } owner) return;
        mainLayoutTimer?.Stop();
        if (mainLayoutHidesOnClose?.Invoke() == true)
        {
            // The tray will hide synchronously after this handler yields. Reopening while an
            // ACK is pending keeps the same live window; settlement must never hide it again.
            try { await FlushMainLayoutAsync(owner); }
            catch (Exception error) { ReportMainLayoutSaveFailure(error); }
            return;
        }
        e.Cancel = true;
        if (mainLayoutClosing) return;
        mainLayoutClosing = true;
        try { await FlushMainLayoutAsync(owner); }
        catch (Exception error) { ReportMainLayoutSaveFailure(error); }
        finally
        {
            mainLayoutClosing = false;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (mainLayoutClosed) return;
                mainLayoutCloseAllowed = true;
                try { Close(); }
                finally
                {
                    // Closing is cancellable by any later owner. Permission lasts for this
                    // one attempt, including when the retained window remains alive.
                    mainLayoutCloseAllowed = false;
                    QueueMainLayoutSave();
                }
            }), DispatcherPriority.Send);
        }
    }
}
