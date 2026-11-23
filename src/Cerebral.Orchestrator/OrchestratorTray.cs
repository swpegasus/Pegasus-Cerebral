using System.Collections;
using System.ComponentModel;
using System.Windows;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Cerebral.Orchestrator;

/// <summary>X hides Main; explicit Exit saves Main and gracefully stops its enrolled desktop Worker.</summary>
internal sealed class OrchestratorTray : IDisposable
{
    private const string HideNoticeKey = "Pegasus.Cerebral.OrchestratorTray.HideNoticeShown/1";
    internal const int MaximumPendingWorkAttention = 16;
    private readonly Application app;
    private readonly Window window;
    private readonly Action newConversation;
    private readonly Func<bool> serviceConnected;
    private readonly Func<string> serviceStatusText;
    private readonly bool showHideNotice;
    private readonly bool presentDesktop;
    private readonly Forms.NotifyIcon notification;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripMenuItem status;
    private readonly Forms.ToolStripMenuItem reviewNotifications;
    private readonly Drawing.Icon icon;
    private readonly Func<IReadOnlyList<DesktopHostedAction>> pluginActions;
    private readonly List<Forms.ToolStripItem> mountedPluginActions = [];
    private readonly List<WorkAttentionTarget> pendingWorkAttention = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool exiting;
    private bool preparingExit;
    private bool disposed;
    private OrchestratorWindow? connectedWindow;

    public OrchestratorTray(Application app, OrchestratorWindow window)
        : this(app, window, presentDesktop: true) { }

    internal OrchestratorTray(Application app, OrchestratorWindow window, bool presentDesktop)
        : this(app, window, window.NewConversationFromTray, () => window.ServiceConnected,
            () => window.ServiceStatusText, showHideNotice: true,
            pluginActions: () => window.DesktopTrayActions, presentDesktop: presentDesktop)
    {
        connectedWindow = window;
        window.ServiceStatusChanged += OnServiceStatusChanged;
        window.FullExitRequested += OnFullExitRequested;
        window.WorkAttentionRequested += OnWorkAttentionRequested;
    }

    internal OrchestratorTray(Application app, Window window, Action newConversation,
        Func<bool> serviceConnected, Func<string> serviceStatusText, bool showHideNotice,
        Func<IReadOnlyList<DesktopHostedAction>>? pluginActions = null, bool presentDesktop = true)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        // The tray decides whether Closing means hide or a requested Exit; Main owns the
        // bounded layout save and must read that intent before its Closing handler runs.
        if (window is OrchestratorWindow main)
            main.ConfigureTrayLifetime(() => !exiting && !disposed);
        this.newConversation = newConversation ?? throw new ArgumentNullException(nameof(newConversation));
        this.serviceConnected = serviceConnected ?? throw new ArgumentNullException(nameof(serviceConnected));
        this.serviceStatusText = serviceStatusText ?? throw new ArgumentNullException(nameof(serviceStatusText));
        this.showHideNotice = showHideNotice;
        this.presentDesktop = presentDesktop;
        this.pluginActions = pluginActions ?? (() => []);
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Cerebral.Desktop;component/Branding/cerebral-orchestrator.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("The Pegasus Cerebral tray icon is unavailable.");
        using (resource.Stream)
        using (var loaded = new Drawing.Icon(resource.Stream))
            icon = new Drawing.Icon(loaded, 32, 32);

        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open " + Branding.ApplicationName, null, (_, _) => ShowCerebral());
        menu.Items.Add("New conversation", null, (_, _) => OnUi(this.newConversation));
        reviewNotifications = new Forms.ToolStripMenuItem("Review notifications") { Enabled = false };
        reviewNotifications.Click += (_, _) => ShowPendingWorkAttention();
        menu.Items.Add(reviewNotifications);
        menu.Items.Add("Open " + Branding.ConfigurationName, null, (_, _) => OpenConfiguration());
        menu.Items.Add(new Forms.ToolStripSeparator());
        status = new Forms.ToolStripMenuItem("Worker not connected") { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit " + Branding.ApplicationName, null, (_, _) => ExitCerebral());
        notification = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = Branding.ApplicationName,
            ContextMenuStrip = menu,
            Visible = presentDesktop
        };
        notification.DoubleClick += OnDoubleClick;
        notification.BalloonTipClicked += OnBalloonTipClicked;
        menu.Opening += OnMenuOpening;
        window.Closing += OnWindowClosing;
        window.Closed += OnWindowClosed;
        app.SessionEnding += OnSessionEnding;
        RefreshStatus();
    }

    internal bool IsNotificationVisible => !disposed && notification.Visible;
    internal bool IsExiting => exiting;

    // Application properties survive a tray replacement within one launch and reset with a new Main process.
    internal static bool ClaimHideNotice(IDictionary applicationProperties, bool enabled, bool hideCompleted)
    {
        ArgumentNullException.ThrowIfNull(applicationProperties);
        if (!enabled || !hideCompleted || applicationProperties.Contains(HideNoticeKey)) return false;
        applicationProperties[HideNoticeKey] = true;
        return true;
    }

    // The OS balloon click carries no target identity. Keep exact notices separate and bounded.
    internal static bool AddPendingWorkAttention(List<WorkAttentionTarget> pending, WorkAttentionTarget target)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsWellFormed || pending.Contains(target)) return false;
        pending.Add(target);
        while (pending.Count > MaximumPendingWorkAttention) pending.RemoveAt(0);
        return true;
    }

    public void StartHidden()
    {
        if (disposed || exiting || !presentDesktop) return;
        window.ShowActivated = false;
        if (!window.IsVisible) window.Show();
        window.Hide();
        window.ShowActivated = true;
    }

    public void ShowCerebral()
    {
        if (disposed || exiting || !presentDesktop) return;
        OnUi(() => DesktopApplicationInstance.Show(window));
    }

    private void OpenConfiguration()
    {
        if (!presentDesktop) return;
        try { ManagementProcess.OpenApplication("Integrator"); }
        catch (Exception ex)
        {
            ShowCerebral();
            OnUi(() => MessageBox.Show(window, ex.Message, Branding.ApplicationName, MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    internal void ExitCerebral()
    {
        if (disposed || exiting || preparingExit) return;
        OnUi(() => _ = ExitCerebralAsync());
    }

    private void OnFullExitRequested(object? sender, EventArgs args) => ExitCerebral();

    private async Task ExitCerebralAsync()
    {
        if (disposed || exiting || preparingExit) return;
        preparingExit = true;
        var wasEnabled = window.IsEnabled;
        try
        {
            if (window is OrchestratorWindow main)
            {
                window.IsEnabled = false;
                await main.PrepareForFullExitAsync(lifetime.Token);
            }
            exiting = true;
            window.IsEnabled = wasEnabled;
            window.Close(); // Closing can still retain the window when a required owner save fails.
        }
        catch (Exception error)
        {
            exiting = false;
            if (window is OrchestratorWindow main) main.ReportFullExitBlocked(error);
            if (!disposed && presentDesktop) DesktopApplicationInstance.Show(window);
        }
        finally
        {
            preparingExit = false;
            if (!disposed) window.IsEnabled = wasEnabled;
        }
    }

    private void OnDoubleClick(object? sender, EventArgs args) => ShowCerebral();
    private void OnBalloonTipClicked(object? sender, EventArgs args) => ShowPendingWorkAttention();

    private void OnWorkAttentionRequested(WorkAttentionTarget target) => OnUi(() =>
    {
        if (disposed || exiting || !presentDesktop || !AddPendingWorkAttention(pendingWorkAttention, target)) return;
        RefreshReviewNotificationsMenu();
        notification.ShowBalloonTip(5000, Branding.ApplicationName,
            "Work needs attention. Choose its original item under Review notifications.", Forms.ToolTipIcon.Info);
    });

    private void ShowPendingWorkAttention() => OnUi(() =>
    {
        if (disposed || exiting) return;
        if (!presentDesktop) return;
        DesktopApplicationInstance.Show(window);
        if (pendingWorkAttention.Count > 0)
            connectedWindow?.ShowWorkAttention(pendingWorkAttention.ToArray(), RemoveExactPendingWorkAttention);
    });

    private void RemoveExactPendingWorkAttention(WorkAttentionTarget target)
    {
        if (disposed || !pendingWorkAttention.Remove(target)) return;
        RefreshReviewNotificationsMenu();
    }

    private void RefreshReviewNotificationsMenu()
    {
        reviewNotifications.Text = pendingWorkAttention.Count == 0
            ? "Review notifications" : $"Review notifications ({pendingWorkAttention.Count})";
        reviewNotifications.Enabled = pendingWorkAttention.Count > 0;
    }

    private void OnMenuOpening(object? sender, CancelEventArgs args)
    {
        foreach (var item in mountedPluginActions)
        {
            menu.Items.Remove(item);
            item.Dispose();
        }
        mountedPluginActions.Clear();
        RefreshReviewNotificationsMenu();
        var insertion = 4;
        foreach (var action in pluginActions())
        {
            var item = new Forms.ToolStripMenuItem(action.Label) { ToolTipText = action.ToolTip ?? "" };
            item.Click += (_, _) => ExecutePluginAction(action);
            menu.Items.Insert(insertion++, item);
            mountedPluginActions.Add(item);
        }
    }

    private void ExecutePluginAction(DesktopHostedAction action) => OnUi(async () =>
    {
        if (!presentDesktop) return;
        try { await action.ExecuteAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            MessageBox.Show(window, error.Message, Branding.ApplicationName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    private void OnWindowClosing(object? sender, CancelEventArgs args)
    {
        if (args.Cancel)
        {
            if (window is OrchestratorWindow { ComposerDraftCloseBlocked: true })
            {
                exiting = false;
                if (presentDesktop) DesktopApplicationInstance.Show(window);
            }
            return;
        }
        if (exiting) return;
        var wasVisible = window.IsVisible;
        args.Cancel = true;
        window.Hide();
        if (!presentDesktop) return;
        if (!ClaimHideNotice(app.Properties, showHideNotice, wasVisible && !window.IsVisible)) return;
        notification.ShowBalloonTip(3000, Branding.ApplicationName,
            "Pegasus Cerebral is in the notification area. Your Worker service and accepted tasks keep running.",
            Forms.ToolTipIcon.Info);
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        exiting = true;
        Dispose();
        if (!app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            app.Shutdown();
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs args) => exiting = true;
    private void OnServiceStatusChanged(object? sender, EventArgs args) => RefreshStatus();

    private void RefreshStatus() => OnUi(() =>
    {
        var connected = serviceConnected();
        status.Text = connected ? "Worker connected" : "Worker not connected";
        var detail = serviceStatusText();
        status.ToolTipText = detail.Length > 240 ? detail[..240] + "…" : detail;
        notification.Text = Branding.ApplicationName + (connected ? " · Connected" : " · Offline");
    });

    private void OnUi(Action action)
    {
        if (disposed || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
        if (window.Dispatcher.CheckAccess()) action();
        else window.Dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.Closing -= OnWindowClosing;
        window.Closed -= OnWindowClosed;
        if (connectedWindow is not null)
        {
            connectedWindow.ServiceStatusChanged -= OnServiceStatusChanged;
            connectedWindow.FullExitRequested -= OnFullExitRequested;
            connectedWindow.WorkAttentionRequested -= OnWorkAttentionRequested;
        }
        app.SessionEnding -= OnSessionEnding;
        notification.DoubleClick -= OnDoubleClick;
        notification.BalloonTipClicked -= OnBalloonTipClicked;
        menu.Opening -= OnMenuOpening;
        lifetime.Cancel();
        pendingWorkAttention.Clear();
        notification.Visible = false;
        notification.Dispose();
        menu.Dispose();
        icon.Dispose();
        lifetime.Dispose();
    }
}
