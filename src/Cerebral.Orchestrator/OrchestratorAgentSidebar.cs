using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cerebral.Client;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

internal enum AgentSidebarState { Active, Stopping, Waiting, Failed, Completed, Idle, Unknown, Disconnected }
internal enum AgentSidebarCallerState { Running, Waiting, Failed, Completed }
internal sealed record AgentSidebarProfile(string AgentId, string Name, string WorkerId);
internal sealed record AgentSidebarOwnership(string ExecutionId, string AgentId, bool CancellationRequested);
// Only the caller that authenticated and correlated the accepted agent/run may supply this proof.
// Execution metrics do not carry lifecycle state; losing scheduler ownership is not an outcome.
internal sealed record AgentSidebarCallerProof(string IssuingInstallationId, string AgentId,
    string ExecutionId, AgentSidebarCallerState State);
internal sealed record AgentSidebarSample(string IssuingInstallationId, string ProcessGenerationId,
    DateTimeOffset SampledAt, bool Available, bool Partial, IReadOnlyList<AgentSidebarOwnership> Ownership,
    IReadOnlyList<AgentSidebarCallerProof>? Callers = null);
internal sealed record AgentSidebarEntry(string AgentId, string Name, AgentSidebarState State,
    int? OwnedContexts, string Text, string Details);

internal static class AgentSidebarProjection
{
    internal const int MaximumObservations = 256;
    internal const int MaximumVisibleRows = 32;
    internal static AgentSidebarSample Unavailable(string installationId) => new(installationId, "", default, false, true, []);

    /// <summary>Read the bounded public owner sample. Routes, models and configured presence do not establish activity.</summary>
    internal static AgentSidebarSample ReadSample(JsonElement value, string expectedInstallationId)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 1024 * 1024 ||
                !ValidObject(value) || value.GetProperty("schema").GetString() != "cerebral.execution-metrics/1" ||
                value.GetProperty("issuingInstallationId").GetString() != expectedInstallationId || !Installation(expectedInstallationId) ||
                !Installation(value.GetProperty("processGenerationId").GetString()) ||
                !value.GetProperty("processGenerationScope").GetString()!.StartsWith("issuing-worker-scheduler-instance;", StringComparison.Ordinal) ||
                !value.GetProperty("sampledAt").TryGetDateTimeOffset(out var sampled) || sampled == default)
                return Unavailable(expectedInstallationId);
            int Count(string key)
            {
                var count = value.GetProperty(key).GetInt32();
                if (count is < 0 or > MaximumObservations) throw new FormatException("Invalid execution count.");
                return count;
            }
            var maximum = Count("maximumObservations");
            var captured = Count("capturedOwnershipCount");
            var unprojectable = Count("unprojectableCount");
            var changed = Count("ownershipChangedCount");
            var observed = Count("observedLocalWorkingAgentCount");
            var executions = Count("observedLocalExecutionCount");
            if (maximum == 0 || captured > maximum || observed > executions || executions > captured ||
                unprojectable > captured || changed > unprojectable ||
                value.GetProperty("confirmedRemoteWorkingAgentCount").ValueKind != JsonValueKind.Null)
                return Unavailable(expectedInstallationId);
            var partial = value.GetProperty("partial").GetBoolean();
            var rows = value.GetProperty("observations");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumObservations ||
                rows.GetArrayLength() != captured) return Unavailable(expectedInstallationId);
            var owned = new List<AgentSidebarOwnership>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var unavailable = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (!ValidObject(row) || row.GetProperty("schedulerOwnerInstallationId").GetString() != expectedInstallationId)
                    return Unavailable(expectedInstallationId);
                var projection = row.GetProperty("projectionState").GetString();
                var kind = row.GetProperty("kind").GetString();
                if (projection != "observed")
                {
                    if (projection != "unavailable" || kind != "unprojectable") return Unavailable(expectedInstallationId);
                    unavailable++; continue;
                }
                var executionId = row.GetProperty("executionId").GetString();
                if (!Identifier(executionId) || !ids.Add(executionId!)) return Unavailable(expectedInstallationId);
                if (kind is "maintenance" or "remote-dispatch-or-reconciliation") continue;
                var agentId = row.GetProperty("agentId").GetString();
                if (kind is not ("workflow-execution" or "legacy-local-execution") || !Identifier(agentId) ||
                    row.GetProperty("executionOwnerInstallationId").GetString() != expectedInstallationId ||
                    row.GetProperty("executionWorkerId").GetString() != "local") return Unavailable(expectedInstallationId);
                owned.Add(new(executionId!, agentId!, row.GetProperty("cancellationRequested").GetBoolean()));
            }
            var truncated = value.GetProperty("truncated").GetBoolean();
            if (unavailable != unprojectable || partial != (truncated || unprojectable > 0 || changed > 0) ||
                value.GetProperty("state").GetString() != (partial ? "partial" : "observed") ||
                executions != owned.Count || observed != owned.Select(row => row.AgentId).Distinct(StringComparer.Ordinal).Count())
                return Unavailable(expectedInstallationId);
            var complete = value.GetProperty("localWorkingAgentCount");
            if (partial ? complete.ValueKind != JsonValueKind.Null : complete.GetInt32() != owned.Select(row => row.AgentId).Distinct(StringComparer.Ordinal).Count())
                return Unavailable(expectedInstallationId);
            return new(expectedInstallationId, value.GetProperty("processGenerationId").GetString()!, sampled, true, partial, owned.ToArray());
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or
            ArgumentException or OverflowException or NullReferenceException)
        { return Unavailable(expectedInstallationId); }
    }

    internal static IReadOnlyList<AgentSidebarEntry> Project(AgentSidebarSample sample, string expectedInstallationId,
        IReadOnlyList<AgentSidebarProfile> profiles, bool connected)
    {
        var available = sample.Available && sample.IssuingInstallationId == expectedInstallationId && Installation(expectedInstallationId) &&
            Installation(sample.ProcessGenerationId) && sample.SampledAt != default && sample.Ownership.Count <= MaximumObservations &&
            sample.Ownership.All(row => row is not null && Identifier(row.AgentId) && Identifier(row.ExecutionId)) &&
            sample.Ownership.Select(row => row.ExecutionId).Distinct(StringComparer.Ordinal).Count() == sample.Ownership.Count;
        var ownership = available ? sample.Ownership : [];
        var callers = available && sample.Callers is { Count: <= MaximumObservations } supplied &&
            supplied.All(row => row is not null && row.IssuingInstallationId == expectedInstallationId &&
                Identifier(row.AgentId) && Identifier(row.ExecutionId) && Enum.IsDefined(row.State)) &&
            supplied.Select(row => row.ExecutionId).Distinct(StringComparer.Ordinal).Count() == supplied.Count &&
            supplied.All(proof => ownership.All(row => row.ExecutionId != proof.ExecutionId || row.AgentId == proof.AgentId))
            ? supplied : [];
        var names = profiles.Take(MaximumObservations).Where(row => row is not null && Identifier(row.AgentId))
            .GroupBy(row => row.AgentId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var agents = names.Keys.Concat(ownership.Select(row => row.AgentId)).Concat(callers.Select(row => row.AgentId)).Distinct(StringComparer.Ordinal);
        return agents.Select(agentId =>
        {
            var contexts = ownership.Where(row => row.AgentId == agentId).ToArray();
            var caller = callers.Where(row => row.AgentId == agentId).ToArray();
            var callerById = caller.ToDictionary(row => row.ExecutionId, StringComparer.Ordinal);
            var active = contexts.Count(row => !row.CancellationRequested &&
                (!callerById.TryGetValue(row.ExecutionId, out var proof) || proof.State == AgentSidebarCallerState.Running));
            var waiting = caller.Count(row => row.State == AgentSidebarCallerState.Waiting);
            var stopping = contexts.Count(row => row.CancellationRequested);
            var state = !connected ? AgentSidebarState.Disconnected : !available ? AgentSidebarState.Unknown :
                active > 0 ? AgentSidebarState.Active : stopping > 0 ? AgentSidebarState.Stopping : waiting > 0 ? AgentSidebarState.Waiting :
                caller.Any(row => row.State == AgentSidebarCallerState.Failed) ? AgentSidebarState.Failed :
                caller.Any(row => row.State == AgentSidebarCallerState.Completed) ? AgentSidebarState.Completed :
                sample.Partial || !names.TryGetValue(agentId, out var profile) || profile.WorkerId != "local" ? AgentSidebarState.Unknown : AgentSidebarState.Idle;
            var count = connected && available && (contexts.Length > 0 || state == AgentSidebarState.Idle) ? contexts.Length : (int?)null;
            var text = state switch
            {
                AgentSidebarState.Active => "Active · " + (sample.Partial ? "≥" : "") + contexts.Length.ToString(CultureInfo.InvariantCulture) + " local context" + (contexts.Length == 1 ? "" : "s"),
                AgentSidebarState.Stopping => "Stopping · " + contexts.Length.ToString(CultureInfo.InvariantCulture) + " owned",
                AgentSidebarState.Waiting => "Waiting",
                AgentSidebarState.Failed => "Failed",
                AgentSidebarState.Completed => "Completed",
                AgentSidebarState.Idle => "Idle · local",
                AgentSidebarState.Disconnected => "Disconnected",
                _ => "Unknown"
            };
            var name = names.TryGetValue(agentId, out var named) && Identifier(named.Name) ? named.Name : agentId;
            var detail = $"Accepted agent: {agentId}\nExecution owner: {expectedInstallationId}\n" + (state switch
            {
                AgentSidebarState.Disconnected => "The Worker connection is disconnected; current agent activity is unobserved.",
                AgentSidebarState.Unknown => !available ? "Current execution observations are unavailable." : sample.Partial ?
                    "This partial sample has no matching confirmed local context; absence cannot establish idle." : "Remote execution is unobserved.",
                AgentSidebarState.Idle => "No matching scheduler-owned context was present in this complete local sample. This does not establish readiness, empty queues, or remote idleness.",
                AgentSidebarState.Waiting => "The current accepted caller explicitly reports waiting. Waiting is not inferred from elapsed time or model availability.",
                AgentSidebarState.Failed => "The current accepted caller explicitly reports failure. A failed observation request is never an agent failure.",
                AgentSidebarState.Completed => "The current accepted caller explicitly reports completion. Releasing scheduler ownership alone is not completion.",
                AgentSidebarState.Stopping => "Cancellation is requested; sampled scheduler ownership persists during cancellation and cleanup.",
                _ => "Active means sampled local scheduler ownership, including slot wait and cleanup. It does not prove current provider generation."
            }) + (available && connected ? $"\nOwned local contexts: {contexts.Length}; cancellation requested: {stopping}; explicit waiting callers: {waiting}.\nSampled: {sample.SampledAt:O}" : "") +
                "\nConfigured agents, loaded models and accepted model routes do not prove activity. Remote activity remains unobserved.";
            return new AgentSidebarEntry(agentId, name, state, count, text, detail);
        }).OrderBy(row => row.State == AgentSidebarState.Active ? 0 : row.State is AgentSidebarState.Waiting or AgentSidebarState.Stopping ? 1 : 2)
            .ThenBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.AgentId, StringComparer.Ordinal).Take(MaximumVisibleRows).ToArray();
    }

    private static bool ValidObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().GroupBy(field => field.Name, StringComparer.Ordinal).All(group => group.Count() == 1);
    private static bool Installation(string? value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);
}

internal sealed class AgentSidebarView : Border, IDisposable
{
    private sealed record Row(Border Host, TextBlock Dot, TextBlock Name, TextBlock State)
    { internal AgentSidebarEntry? Entry { get; set; } }
    private readonly StackPanel rows = new();
    private readonly Dictionary<string, Row> controls = new(StringComparer.Ordinal);
    private bool subscribed;
    private bool disposed;
    internal Func<bool>? MotionPolicyForChecks { get; set; }

    internal AgentSidebarView()
    {
        Margin = new Thickness(0, 3, 4, 8); Padding = new Thickness(7, 6, 7, 6);
        BorderBrush = Ui.Line; BorderThickness = new Thickness(0, 1, 0, 1);
        var body = new StackPanel();
        var heading = Ui.Text("Agents", 11, Ui.Muted); heading.Margin = new Thickness(0, 0, 0, 3); body.Children.Add(heading);
        body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 154,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var scope = Ui.Text("Local observation · remote unobserved", 10, Ui.Muted); scope.TextWrapping = TextWrapping.Wrap;
        scope.ToolTip = "Shows at most 32 accepted or configured agent identities. Active rows come only from the bounded current owner sample; configuration is not activity evidence.";
        scope.Margin = new Thickness(0, 4, 0, 0); body.Children.Add(scope); Child = body;
        AutomationProperties.SetAutomationId(this, "AgentSidebar");
        AutomationProperties.SetName(this, "Accepted agent activity; local sampled ownership and remote uncertainty");
        Loaded += (_, _) => { Subscribe(); RefreshMotion(); };
        Unloaded += (_, _) => { StopMotion(); Unsubscribe(); };
        IsVisibleChanged += (_, _) => RefreshMotion();
    }

    internal void SetEntries(IReadOnlyList<AgentSidebarEntry> entries)
    {
        if (disposed) return;
        var bounded = entries.Take(AgentSidebarProjection.MaximumVisibleRows).ToArray();
        var live = bounded.Select(entry => entry.AgentId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in controls.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            var row = controls[stale]; row.Dot.BeginAnimation(OpacityProperty, null); rows.Children.Remove(row.Host); controls.Remove(stale);
        }
        for (var index = 0; index < bounded.Length; index++)
        {
            var entry = bounded[index];
            if (!controls.TryGetValue(entry.AgentId, out var row))
            {
                var dot = Ui.Text("○", 13, Ui.Muted); dot.Width = 17; dot.Margin = new Thickness(0); dot.VerticalAlignment = VerticalAlignment.Center;
                var name = Ui.Text("", 12); name.Margin = new Thickness(0); name.TextTrimming = TextTrimming.CharacterEllipsis;
                var state = Ui.Text("", 10, Ui.Muted); state.Margin = new Thickness(0); state.TextTrimming = TextTrimming.CharacterEllipsis;
                var label = new StackPanel(); label.Children.Add(name); label.Children.Add(state);
                var content = new DockPanel(); DockPanel.SetDock(dot, Dock.Left); content.Children.Add(dot); content.Children.Add(label);
                var host = new Border { Child = content, Padding = new Thickness(0, 3, 0, 3) };
                AutomationProperties.SetAutomationId(host, "AgentSidebar-" + entry.AgentId);
                AutomationProperties.SetAutomationId(dot, "AgentSidebarMarker-" + entry.AgentId);
                AutomationProperties.SetAutomationId(state, "AgentSidebarState-" + entry.AgentId);
                row = new(host, dot, name, state); controls.Add(entry.AgentId, row); rows.Children.Insert(Math.Min(index, rows.Children.Count), host);
            }
            if (rows.Children.IndexOf(row.Host) != index) { rows.Children.Remove(row.Host); rows.Children.Insert(index, row.Host); }
            row.Entry = entry; row.Name.Text = entry.Name; row.State.Text = entry.Text; row.Host.ToolTip = entry.Details;
            row.Dot.Text = entry.State switch { AgentSidebarState.Active => "●", AgentSidebarState.Waiting => "◷", AgentSidebarState.Failed => "!",
                AgentSidebarState.Completed => "✓", AgentSidebarState.Stopping => "■", AgentSidebarState.Unknown => "?", AgentSidebarState.Disconnected => "×", _ => "○" };
            row.Dot.Foreground = entry.State == AgentSidebarState.Active ? Ui.Accent : entry.State == AgentSidebarState.Failed ? Ui.ErrorText : Ui.Muted;
            AutomationProperties.SetName(row.Host, entry.Name + ": " + entry.Text);
            AutomationProperties.SetHelpText(row.Host, entry.Details);
        }
        Visibility = bounded.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshMotion();
    }

    internal void RefreshMotion()
    {
        var permitted = !disposed && IsLoaded && IsVisible && (MotionPolicyForChecks?.Invoke() ??
            MotionPermitted(SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast, reducedMotion: false));
        foreach (var row in controls.Values)
        {
            var pulse = permitted && row.Entry?.State == AgentSidebarState.Active;
            if (pulse == row.Dot.HasAnimatedProperties) continue;
            row.Dot.BeginAnimation(OpacityProperty, pulse ? new DoubleAnimation(.55, 1, TimeSpan.FromSeconds(1.3))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, FillBehavior = FillBehavior.Stop } : null);
        }
    }

    internal static bool MotionPermitted(bool systemAnimation, bool highContrast, bool reducedMotion) => systemAnimation && !highContrast && !reducedMotion;
    private void StopMotion() { foreach (var row in controls.Values) row.Dot.BeginAnimation(OpacityProperty, null); }
    private void Subscribe() { if (subscribed || disposed) return; subscribed = true; SystemParameters.StaticPropertyChanged += SystemMotionChanged; }
    private void Unsubscribe() { if (!subscribed) return; subscribed = false; SystemParameters.StaticPropertyChanged -= SystemMotionChanged; }
    private void SystemMotionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Dispatcher.CheckAccess()) RefreshMotion();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RefreshMotion));
    }
    public void Dispose() { if (disposed) return; disposed = true; StopMotion(); Unsubscribe(); controls.Clear(); rows.Children.Clear(); }
}

public sealed partial class OrchestratorWindow
{
    private AgentSidebarView? agentSidebarView;
    private AgentSidebarSample? agentSidebarSample;
    private CerebralClient? agentSidebarClient;
    private string? agentSidebarInstallationId;

    private FrameworkElement BuildAgentSidebar()
    {
        if (agentSidebarView is not null) return agentSidebarView;
        agentSidebarView = new();
        ServiceStatusChanged += AgentSidebarConnectionChanged;
        Closed += (_, _) => { ServiceStatusChanged -= AgentSidebarConnectionChanged; agentSidebarView.Dispose(); agentSidebarSample = null; agentSidebarClient = null; };
        agentSidebarView.Unloaded += (_, _) => ResetAgentSidebarSnapshot();
        agentSidebarView.IsVisibleChanged += (_, _) =>
        {
            if (agentSidebarView.IsLoaded && !agentSidebarView.IsVisible) ResetAgentSidebarSnapshot();
        };
        RenderAgentSidebar();
        return agentSidebarView;
    }

    private void ApplyAgentSidebarSnapshot(CerebralClient capturedClient, string installationId, AgentSidebarSample sample)
    {
        if (Lifetime.IsCancellationRequested || !SystemWorkMetricsBindingCurrent(capturedClient, installationId)) return;
        agentSidebarClient = capturedClient; agentSidebarInstallationId = installationId; agentSidebarSample = sample;
        RenderAgentSidebar();
    }

    private void ResetAgentSidebarSnapshot()
    {
        agentSidebarSample = null; agentSidebarClient = null; agentSidebarInstallationId = null; RenderAgentSidebar();
    }

    private void AgentSidebarConnectionChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess()) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => AgentSidebarConnectionChanged(sender, args))); return; }
        if (agentSidebarClient is not null && (agentSidebarInstallationId is null || !SystemWorkMetricsBindingCurrent(agentSidebarClient, agentSidebarInstallationId)))
            ResetAgentSidebarSnapshot();
        else RenderAgentSidebar();
    }

    private void RenderAgentSidebar()
    {
        if (agentSidebarView is null) return;
        var installation = systemWorkMetricsInstallationId ?? "";
        var sample = agentSidebarSample ?? AgentSidebarProjection.Unavailable(installation);
        agentSidebarView.SetEntries(AgentSidebarProjection.Project(sample, installation,
            profiles.Take(AgentSidebarProjection.MaximumObservations).Select(profile => new AgentSidebarProfile(profile.Id, profile.Name, profile.WorkerId)).ToArray(), IsConnected));
    }
}
