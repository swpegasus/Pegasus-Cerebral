using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Cerebral.Desktop.Plugins;
using Cerebral.PackageSettings;

namespace Cerebral.ResourcePausePlugin;

/// <summary>Owner UI for the service-owned local resource pause policy.</summary>
public sealed partial class ResourcePausePlugin : IDesktopPlugin, IDesktopConfigurationPlugin, IDesktopMountPlugin
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim ownerActionGate = new(1, 1);
    private readonly List<WeakReference<TextBlock>> summaries = [];
    private readonly List<WeakReference<TextBlock>> peerSummaries = [];
    private readonly List<ButtonSet> buttons = [];
    private IDesktopPluginHost? host;
    private IDesktopServiceHost? service;
    private ResourcePausePreferenceStore? preferenceStore;
    private PackagePreferenceSession<ResourcePausePreferences>? centralPreferences;
    private ResourceStatus? current;
    private HostDefaults? currentDefaults;
    private string? defaultRouteError;
    private int resourceTransitionVersion;
    private ResourcePresentation? pending;
    private bool disposed;

    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels =>
    [
        new("local-gpu", "Local GPU", CreateLocalPanel),
        new("local-model", "Local model preference", CreateLocalModelPanel),
        new("engine-control", "Engine", CreateEnginePanel),
        new("work-hold", "Work hold", CreateWorkHoldPanel),
        new("paired-workers", "Paired workers", CreatePeerPanel)
    ];
    public IReadOnlyList<IDesktopMountContribution> MountContributions =>
    [
        new DesktopMountView(new("resource-pause", DesktopMountSlots.SidebarTabs)
        {
            Order = 30,
            Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
        }, "Resource Pause", CreateLocalPanel),
        new DesktopMountView(new("resource-status", DesktopMountSlots.StatusBar)
        {
            Order = 30,
            Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
        }, "Local GPU availability", CreateStatusItem),
        new DesktopMountView(new("engine-status", DesktopMountSlots.StatusBar)
        {
            Order = 31,
            Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
        }, "Engine status and selection", CreateEngineStatusItem),
        new DesktopMountView(new("work-hold-status", DesktopMountSlots.StatusBar)
        {
            Order = 32,
            Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
        }, "Intentional work hold status", CreateWorkHoldStatusItem),
        Menu("pause-local", "Pause local GPU", token => PauseAsync(false, token), 10),
        Menu("pause-route", "Pause GPU and use paired workers", token => PauseAsync(true, token), 20),
        Menu("resume-local", "Activate preferred local model", ActivatePreferredLocalAsync, 30),
        Menu("refresh-status", "Refresh GPU resource status", RefreshAndReportAsync, 40)
    ];

    public void Initialize(IDesktopPluginHost pluginHost)
    {
        host = pluginHost;
        service = pluginHost as IDesktopServiceHost ??
            throw new InvalidOperationException("Pegasus Resource Pause requires the structured service-command host.");
        if (pluginHost is IDesktopPluginEnvironment environment)
            preferenceStore = new(environment.DataDirectory);
        centralPreferences = new(pluginHost, ResourcePauseSettingsPolicy.PluginId, ResourcePauseSettingsPolicy.PackageVersion,
            new ResourcePauseSettingsPolicy(), ResourcePauseSettingsPolicy.Decode, ResourcePauseSettingsPolicy.Encode, () => new(null));
    }

    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    private static DesktopMountAction Menu(string id, string label, Func<CancellationToken, Task> action, int order) =>
        new(new(id, DesktopMountSlots.Menus)
        {
            Order = order,
            Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
        }, label, action, "Control local GPU availability without stopping Pegasus Cerebral.");

    private FrameworkElement CreateStatusItem()
    {
        var button = ActionButton("Local GPU checking…", "ResourcePause.StatusToggle");
        button.MinHeight = 24;
        button.Padding = new Thickness(8, 2, 8, 2);
        SetTogglePresentation(button, new("Local GPU checking…", ResourceSignal.Unknown,
            "Unknown: waiting for the Worker to confirm local GPU availability.", true));
        button.Click += async (_, _) =>
        {
            await RunUiAsync(async () =>
            {
                if (current?.State == "active" && currentDefaults is not null)
                {
                    if (HasLocalDefault(currentDefaults)) await PauseAsync(true, lifetime.Token);
                    else await ActivatePreferredLocalAsync(lifetime.Token);
                }
                else if (current is { State: "paused", ActiveLocalAssignments: 0 }) await ActivatePreferredLocalAsync(lifetime.Token);
                else await RefreshAsync(lifetime.Token);
            });
        };
        TrackButtons(new(null, null, null, button));
        _ = RunUiAsync(() => RefreshAsync(lifetime.Token));
        return button;
    }

    private FrameworkElement CreateLocalPanel()
    {
        var root = PanelRoot();
        root.Children.Add(Heading("Local GPU"));
        root.Children.Add(Explanation("Activating the local GPU selects the preferred capable local model for new work that follows the default. Explicit profile pins and accepted runs keep their existing routes. An inactive local GPU admits no new local inference; eligible work can use compatible paired workers and unmatched work stays in the durable queue."));
        var summary = StatusText("Checking local resource state…", "ResourcePause.LocalStatus");
        TrackView(summaries, summary);
        root.Children.Add(summary);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var pause = ActionButton("Pause local GPU", "ResourcePause.PauseLocal");
        var pauseAndRoute = ActionButton("Pause and use paired workers", "ResourcePause.PauseAndRoute");
        var resume = ActionButton("Activate preferred local model", "ResourcePause.Resume");
        var refresh = ActionButton("Refresh status", "ResourcePause.Refresh");
        pause.Click += async (_, _) => await RunUiAsync(() => PauseAsync(false, lifetime.Token));
        pauseAndRoute.Click += async (_, _) => await RunUiAsync(() => PauseAsync(true, lifetime.Token));
        resume.Click += async (_, _) => await RunUiAsync(() => ActivatePreferredLocalAsync(lifetime.Token));
        refresh.Click += async (_, _) => await RunUiAsync(() => RefreshAsync(lifetime.Token));
        row.Children.Add(pause);
        row.Children.Add(pauseAndRoute);
        row.Children.Add(resume);
        row.Children.Add(refresh);
        root.Children.Add(row);
        TrackButtons(new(pause, pauseAndRoute, resume));
        _ = RunUiAsync(() => RefreshAsync(lifetime.Token));
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private FrameworkElement CreatePeerPanel()
    {
        var root = PanelRoot();
        root.Children.Add(Heading("Paired worker fallback"));
        root.Children.Add(Explanation("Only already paired workers that advertise the exact receiver profile and immediate capacity are eligible. Native workflow runs and unmatched jobs stay in the local durable queue."));
        var peers = StatusText("Checking paired capacity…", "ResourcePause.PeerStatus");
        TrackView(peerSummaries, peers);
        root.Children.Add(peers);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var route = ActionButton("Pause and route eligible work", "ResourcePause.RouteEligible");
        var pauseLocal = ActionButton("Pause and keep work local", "ResourcePause.KeepLocal");
        var refresh = ActionButton("Refresh capacity", "ResourcePause.RefreshPeers");
        route.Click += async (_, _) => await RunUiAsync(() => PauseAsync(true, lifetime.Token));
        pauseLocal.Click += async (_, _) => await RunUiAsync(() => PauseAsync(false, lifetime.Token));
        refresh.Click += async (_, _) => await RunUiAsync(() => RefreshAsync(lifetime.Token));
        row.Children.Add(route);
        row.Children.Add(pauseLocal);
        row.Children.Add(refresh);
        root.Children.Add(row);
        TrackButtons(new(pauseLocal, route, null));
        _ = RunUiAsync(() => RefreshAsync(lifetime.Token));
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task PauseAsync(bool reroute, CancellationToken cancellationToken)
    {
        await ownerActionGate.WaitAsync(cancellationToken);
        BeginResourceTransition();
        try { await PauseCoreAsync(reroute, cancellationToken); }
        finally { EndResourceTransition(); ownerActionGate.Release(); }
    }

    private async Task PauseCoreAsync(bool reroute, CancellationToken cancellationToken)
    {
        SetPending("Local GPU draining…", "Pending: waiting for the Worker to confirm local GPU deactivation.");
        try
        {
            var status = await CommandAsync("resources.local.pause", new { rerouteEligibleWork = reroute }, cancellationToken);
            pending = null;
            Apply(status);
            ReportHostStatus(status.Detail);
            _ = PollTransitionAsync();
        }
        catch (Exception error)
        {
            SetUnavailable("Local GPU deactivation was not confirmed: " + error.Message);
            throw;
        }
    }

    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await ownerActionGate.WaitAsync(cancellationToken);
        BeginResourceTransition();
        try { await ResumeCoreAsync(cancellationToken); }
        finally { EndResourceTransition(); ownerActionGate.Release(); }
    }

    private async Task ResumeCoreAsync(CancellationToken cancellationToken)
    {
        SetPending("Local GPU reactivating…", "Pending: waiting for the Worker to confirm local GPU activation.");
        try
        {
            var status = await CommandAsync("resources.local.resume", new { }, cancellationToken);
            pending = null;
            Apply(status);
            ReportHostStatus(status.Detail);
        }
        catch (Exception error)
        {
            SetUnavailable("Local GPU activation was not confirmed: " + error.Message);
            throw;
        }
    }

    private async Task RefreshAndReportAsync(CancellationToken cancellationToken)
    {
        var status = await RefreshAsync(cancellationToken);
        ReportHostStatus(status.Detail);
    }

    private async Task<ResourceStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        var version = Volatile.Read(ref resourceTransitionVersion);
        try
        {
            var status = await CommandAsync("resources.local.status", new { }, cancellationToken);
            HostDefaults? defaults = null;
            string? routeError = null;
            if (status.State == "active")
            {
                try
                {
                    defaults = await ServiceAsync<HostDefaults>("settings.get", new { }, cancellationToken);
                    ValidateDefaults(defaults);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    defaults = null;
                    routeError = Bound("The preferred model route could not be confirmed. Refresh status to inspect it. " + error.Message);
                }
            }
            // An earlier read must not replace the route confirmed by an owner transition.
            if ((version & 1) == 0 && version == Volatile.Read(ref resourceTransitionVersion))
            {
                currentDefaults = defaults;
                defaultRouteError = routeError;
                Apply(status);
            }
            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var detail = "Local GPU status could not be confirmed: " + error.Message;
            if ((version & 1) == 0 && version == Volatile.Read(ref resourceTransitionVersion))
                SetUnavailable(detail);
            throw new ResourceTransitionException(detail, error);
        }
        finally { refreshGate.Release(); }
    }

    private void BeginResourceTransition() => Interlocked.Increment(ref resourceTransitionVersion);
    private void EndResourceTransition() => Interlocked.Increment(ref resourceTransitionVersion);

    private async Task PollTransitionAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 120 && !lifetime.IsCancellationRequested; attempt++)
            {
                await Task.Delay(500, lifetime.Token);
                var status = await RefreshAsync(lifetime.Token);
                if (status.State is not ("draining" or "resuming")) return;
            }
            if (!lifetime.IsCancellationRequested)
                SetUnavailable("Local GPU transition remains unresolved after the bounded status check. Refresh status to check its current state.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { SetUnavailable("Resource Pause status refresh failed: " + error.Message); }
    }

    private async Task<ResourceStatus> CommandAsync(string operation, object arguments, CancellationToken cancellationToken)
    {
        var target = service ?? throw new InvalidOperationException("The Worker service connection is unavailable.");
        var result = await target.ExecuteServiceCommandAsync(operation, arguments, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? operation + " failed.");
        return result.Data.Deserialize<ResourceStatus>(Json) ?? throw new InvalidDataException(operation + " returned no usable status.");
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        if (CallbacksRetired) return;
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ResourceTransitionException) { }
        catch (Exception error)
        {
            SetUnavailable("Resource Pause is unavailable: " + error.Message);
        }
    }

    private void Apply(ResourceStatus status)
    {
        if (CallbacksRetired) return;
        current = status;
        if (status.State != "active") { currentDefaults = null; defaultRouteError = null; }
        var summary = $"{Friendly(status.State)} · local active {status.ActiveLocalAssignments} · local queued {status.QueuedLocalAssignments} · workflow queued {status.QueuedLocalWorkflows}\n{status.Detail}";
        if (status.State == "active") summary += "\n" + DefaultRouteDetail(currentDefaults, defaultRouteError);
        foreach (var text in LiveViews(summaries)) text.Text = summary;
        var peers = status.Peers.Count == 0
            ? "No paired capacity has been sampled. Local work stays queued."
            : string.Join(Environment.NewLine, status.Peers.Select(peer => peer.Reachable
                ? $"{peer.Name}: {peer.RoundTripLatencyMilliseconds ?? 0} ms · {peer.AvailableSlots ?? 0} slot(s) available · {peer.QueuedAssignments ?? 0} queued"
                : $"{peer.Name}: unavailable · {peer.Error}"));
        foreach (var text in LiveViews(peerSummaries)) text.Text = peers;
        UpdateButtons(status, pending ?? Present(status));
    }

    private void SetPending(string label, string detail)
    {
        if (CallbacksRetired) return;
        pending = new(label, ResourceSignal.Pending, detail, false);
        UpdateButtons(current, pending);
    }

    private void SetUnavailable(string detail)
    {
        if (CallbacksRetired) return;
        current = null;
        currentDefaults = null;
        defaultRouteError = null;
        pending = null;
        foreach (var text in LiveViews(summaries)) text.Text = detail;
        foreach (var text in LiveViews(peerSummaries)) text.Text = detail;
        UpdateButtons(null, new("Local GPU unavailable", ResourceSignal.Attention, "Needs attention: " + detail, true));
        ReportHostStatus(detail);
    }

    private void SetAttention(string label, string detail)
    {
        if (CallbacksRetired) return;
        current = null;
        pending = null;
        foreach (var text in LiveViews(summaries)) text.Text = detail;
        UpdateButtons(null, new(label, ResourceSignal.Attention, "Needs attention: " + detail, true));
        ReportHostStatus(detail);
    }

    private void UpdateButtons(ResourceStatus? status, ResourcePresentation presentation)
    {
        buttons.RemoveAll(set => !set.IsAlive);
        foreach (var set in buttons)
        {
            var pause = set.Pause;
            var route = set.Route;
            var resume = set.Resume;
            var toggle = set.Toggle;
            var canAct = pending is null && status is not null;
            if (pause is not null) pause.IsEnabled = canAct && status!.State is "active" or "faulted";
            if (route is not null) route.IsEnabled = canAct && status!.State is "active" or "paused" or "faulted";
            if (resume is not null) resume.IsEnabled = canAct && (status!.State == "active" || status is { State: "paused", ActiveLocalAssignments: 0 });
            if (toggle is not null) SetTogglePresentation(toggle, presentation);
        }
    }

    // Active readiness includes the saved model route; GPU admission alone cannot prove local preference.
    private ResourcePresentation Present(ResourceStatus status) => status.State switch
    {
        "active" when currentDefaults is null => new("Local model route unknown", ResourceSignal.Unknown,
            "Unknown: local GPU admission is active. " + DefaultRouteDetail(null, defaultRouteError) + "\n" + status.Detail, true),
        "active" when !HasLocalDefault(currentDefaults) => new(currentDefaults?.DefaultModelId.Length == 0 ? "Local GPU active · no default" : "Local GPU active · remote default", ResourceSignal.Attention,
            "Needs attention: local GPU admission is active. " + DefaultRouteDetail(currentDefaults, null) + "\n" + status.Detail, true),
        "active" => new("Local GPU active", ResourceSignal.Ready,
            "Ready: local GPU admission is active. " + DefaultRouteDetail(currentDefaults, null) + "\n" + status.Detail, true),
        "paused" when status.ActiveLocalAssignments == 0 => new("Local GPU inactive", ResourceSignal.Ready,
            "Ready: local GPU admission is inactive and no local assignments are running.\n" + status.Detail, true),
        "paused" => new("Local GPU attention", ResourceSignal.Attention,
            "Needs attention: inactive state is not confirmed while local assignments remain active.\n" + status.Detail, true),
        "draining" => new("Local GPU draining…", ResourceSignal.Pending,
            "Pending: local GPU deactivation has not settled.\n" + status.Detail, false),
        "resuming" => new("Local GPU reactivating…", ResourceSignal.Pending,
            "Pending: local GPU activation has not settled.\n" + status.Detail, false),
        "faulted" => new("Local GPU attention", ResourceSignal.Attention,
            "Needs attention: the local GPU transition failed closed.\n" + status.Detail, true),
        _ => new("Local GPU unknown", ResourceSignal.Unknown,
            "Unknown: the Worker has not confirmed a recognized local GPU state.\n" + status.Detail, true)
    };

    private static void SetTogglePresentation(Button button, ResourcePresentation presentation)
    {
        if (button.Content is not StackPanel content)
        {
            content = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var indicator = new Ellipse
            {
                Width = 9, Height = 9, StrokeThickness = 1,
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
            };
            indicator.SetResourceReference(Shape.StrokeProperty, DesktopThemeResourceKeys.TextBrush);
            AutomationProperties.SetAutomationId(indicator, "ResourcePause.StatusIndicator");
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
            AutomationProperties.SetAutomationId(label, "ResourcePause.StatusLabel");
            content.Children.Add(indicator);
            content.Children.Add(label);
            button.Content = content;
        }
        var circle = (Ellipse)content.Children[0];
        var text = (TextBlock)content.Children[1];
        if (presentation.Signal == ResourceSignal.Unknown)
            circle.SetResourceReference(Shape.FillProperty, DesktopThemeResourceKeys.MutedTextBrush);
        else
            circle.Fill = presentation.Signal switch
            {
                ResourceSignal.Ready => Brushes.LimeGreen,
                ResourceSignal.Pending => Brushes.Gold,
                _ => Brushes.IndianRed
            };
        var readiness = presentation.Signal switch
        {
            ResourceSignal.Ready => "Ready",
            ResourceSignal.Pending => "Pending",
            ResourceSignal.Attention => "Needs attention",
            _ => "Unknown"
        };
        text.Text = presentation.Label;
        AutomationProperties.SetName(circle, readiness);
        AutomationProperties.SetName(button, presentation.Label + ". " + readiness + ".");
        AutomationProperties.SetHelpText(button, presentation.Detail);
        AutomationProperties.SetLiveSetting(button, AutomationLiveSetting.Polite);
        button.ToolTip = presentation.Detail;
        button.IsEnabled = presentation.CanToggle;
    }

    private enum ResourceSignal { Unknown, Ready, Pending, Attention }
    private sealed record ResourcePresentation(string Label, ResourceSignal Signal, string Detail, bool CanToggle);
    private sealed class ResourceTransitionException(string message, Exception innerException) :
        InvalidOperationException(message, innerException);

    private static StackPanel PanelRoot() => new() { Margin = new Thickness(14) };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold };
    private static TextBlock Explanation(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 10) };
        block.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.MutedTextBrush);
        return block;
    }
    private static TextBlock StatusText(string text, string automationId)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        AutomationProperties.SetAutomationId(block, automationId);
        return block;
    }
    private static Button ActionButton(string text, string automationId)
    {
        var button = new Button { Content = text, MinHeight = 32, Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(10, 5, 10, 5) };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }
    private static string Friendly(string? state) => string.IsNullOrEmpty(state) ? "Unknown" : char.ToUpperInvariant(state[0]) + state[1..];

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        host = null; service = null; preferenceStore = null;
        cleanup.Attempt(() => lifetime.Cancel());
        cleanup.Attempt(DisposeStatusRefresh);
        cleanup.Attempt(ClearStatusBalloons);
        cleanup.Attempt(() => centralPreferences?.Dispose()); centralPreferences = null;
        cleanup.Attempt(DisposeExtendedState);
        cleanup.Attempt(lifetime.Dispose);
        current = null; currentDefaults = null; defaultRouteError = null; pending = null;
        summaries.Clear(); peerSummaries.Clear(); buttons.Clear();
        cleanup.Complete();
    }

    // Configuration windows own their views. The long-lived plugin must not retain a closed window.
    private static void TrackView<T>(List<WeakReference<T>> views, T view) where T : class
    {
        views.RemoveAll(reference => !reference.TryGetTarget(out _));
        views.Add(new(view));
    }

    private static IEnumerable<T> LiveViews<T>(List<WeakReference<T>> views) where T : class
    {
        views.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in views)
            if (reference.TryGetTarget(out var view)) yield return view;
    }

    private void TrackButtons(ButtonSet set)
    {
        buttons.RemoveAll(item => !item.IsAlive);
        buttons.Add(set);
    }

    private sealed class ButtonSet(Button? pause, Button? route, Button? resume, Button? toggle = null)
    {
        private readonly WeakReference<Button>? pauseView = pause is null ? null : new(pause);
        private readonly WeakReference<Button>? routeView = route is null ? null : new(route);
        private readonly WeakReference<Button>? resumeView = resume is null ? null : new(resume);
        private readonly WeakReference<Button>? toggleView = toggle is null ? null : new(toggle);
        public Button? Pause => Target(pauseView);
        public Button? Route => Target(routeView);
        public Button? Resume => Target(resumeView);
        public Button? Toggle => Target(toggleView);
        public bool IsAlive => Pause is not null || Route is not null || Resume is not null || Toggle is not null;
        private static Button? Target(WeakReference<Button>? reference) =>
            reference is not null && reference.TryGetTarget(out var view) ? view : null;
    }
    private sealed class ResourceStatus
    {
        public required string State { get; init; }
        public bool RerouteEligibleWork { get; init; }
        public required int ActiveLocalAssignments { get; init; }
        public int QueuedLocalAssignments { get; init; }
        public int QueuedLocalWorkflows { get; init; }
        public int RoutedAssignments { get; init; }
        public IReadOnlyList<string> ModelsToReload { get; init; } = [];
        public IReadOnlyList<PeerStatus> Peers { get; init; } = [];
        public string Detail { get; init; } = "";
    }
    private sealed class PeerStatus
    {
        public string WorkerId { get; init; } = "";
        public string Name { get; init; } = "";
        public bool Reachable { get; init; }
        public long? RoundTripLatencyMilliseconds { get; init; }
        public int? AvailableSlots { get; init; }
        public int? QueuedAssignments { get; init; }
        public IReadOnlyList<string> SharedProfileIds { get; init; } = [];
        public string? Error { get; init; }
    }
}
