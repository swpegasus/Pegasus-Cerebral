using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly StackPanel teamSwitchRows = new();
    private ContentControl? teamMonitorSurface;
    private Border? teamMonitorHeader;
    private Grid? teamSidePane;
    private TextBlock? teamMonitorHeading;
    private TextBlock? teamMonitorState;
    private Button? teamPopoutButton;
    private readonly Dictionary<string, WorkflowWorkspace> teamPresenters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamPresenterScope> teamPresenterScopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamInspectionState> teamInspectionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamReadPosition> teamReadPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> teamWorkersExpanded = new(StringComparer.Ordinal);
    private readonly LinkedList<string> teamPresenterRecency = new();
    private readonly Dictionary<string, LinkedListNode<string>> teamPresenterNodes = new(StringComparer.Ordinal);
    private const int MaximumRetainedTeamPresenters = 16;
    private const int MaximumRetainedInspectionStates = 512;
    private IReadOnlyList<CollaborationViewTask> teamTasks = [];
    private CollaborationViewGraph? teamGraph;
    private string? teamSourceConversationId;
    private string? teamTaskId;
    private string? teamSelectedRunId;
    private string? teamPresenterConnection;
    private CerebralClient? teamPresenterClient;
    private long? teamSnapshotRow;
    private long? teamNextBeforeRow;
    private bool teamOlderPagesLoaded;
    private string? teamMonitorIssue;
    private long teamNavigationVersion;
    private bool teamTaskOpening;
    private Window? teamPopout;
    private WorkflowWorkspace? teamPopoutPresenter;
    private string? teamPopoutRunId;
    private ContentControl? teamPopoutContent;
    private TextBlock? teamPopoutHeading;
    private TextBlock? teamPopoutState;
    private bool teamMonitorActive;
    private long teamReadVersion;
    private readonly DispatcherTimer teamRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };

    // Reader-local coordinates survive a Main chat switch within one owner. Only a message
    // actually visible in the presenter can advance LastReadMessageSequence.
    private sealed record TeamPresenterScope(string TaskId, string SourceConversationId);
    private sealed record TeamInspectionState(TeamPresenterScope Scope, WorkflowInspectionViewState View);
    private sealed record TeamReadPosition(string TaskId, string SourceConversationId, long LastReadMessageSequence,
        long LatestObservedMessageSequence, long LastSeenRunRevision, DateTimeOffset? ReadAt);

    private bool TeamHasUnreadMessage(string taskId, string runId) =>
        teamReadPositions.TryGetValue(runId, out var read) && read.TaskId == taskId &&
        read.SourceConversationId == teamSourceConversationId &&
        read.LatestObservedMessageSequence > read.LastReadMessageSequence;

    private bool TeamHasUpdatedRun(string taskId, string runId, long revision) =>
        teamReadPositions.TryGetValue(runId, out var read) && read.TaskId == taskId &&
        read.SourceConversationId == teamSourceConversationId && revision > read.LastSeenRunRevision;

    private string TeamReadHint(string taskId, string runId) =>
        teamReadPositions.TryGetValue(runId, out var read) && read.TaskId == taskId &&
        read.SourceConversationId == teamSourceConversationId && read.ReadAt is { } at
            ? $"Last shown {at.ToLocalTime():g} · message event {read.LastReadMessageSequence}"
            : "No message shown in this window";

    private static TeamReadPosition AdvanceTeamReadPosition(TeamReadPosition? prior,
        string taskId, string sourceConversationId, WorkflowReadViewport viewport,
        long seenRunRevision, DateTimeOffset observedAt)
    {
        if (prior is null || prior.TaskId != taskId || prior.SourceConversationId != sourceConversationId)
            prior = null;
        var latest = Math.Max(prior?.LatestObservedMessageSequence ?? -1,
            viewport.LatestLoadedMessageSequence ?? -1);
        var read = Math.Max(prior?.LastReadMessageSequence ?? -1,
            viewport.HighestVisibleMessageSequence ?? -1);
        return new(taskId, sourceConversationId, read, latest,
            Math.Max(prior?.LastSeenRunRevision ?? 0, seenRunRevision),
            read > (prior?.LastReadMessageSequence ?? -1) ? observedAt : prior?.ReadAt);
    }

    private bool TeamPresenterMounted(TeamWorkPanel panel, WorkflowWorkspace presenter) =>
        ReferenceEquals(presenter, panel.Body.Content) && presenter.IsVisible &&
        ((coordinatorDockPanel is { IsExpanded: true } && panel.Body.IsDescendantOf(coordinatorDockPanel)) ||
         (workersDockPanel is { IsExpanded: true } && panel.Body.IsDescendantOf(workersDockPanel)));

    private void ObserveTeamReadViewport(string taskId, string runId, WorkflowWorkspace presenter,
        WorkflowReadViewport viewport)
    {
        var inMain = teamWorkPanels.TryGetValue(runId, out var panel) && TeamPresenterMounted(panel, presenter);
        var inPopout = teamPopoutRunId == runId && ReferenceEquals(presenter, teamPopoutPresenter);
        if ((!inMain && !inPopout) || !IsConnected ||
            teamPresenterConnection != ServiceConnectionIdentity ||
            !ReferenceEquals(teamPresenterClient, Client) ||
            teamGraph is not { Complete: true } graph ||
            graph.TaskId != taskId || graph.SourceConversationId != durableConversationId ||
            !teamPresenterScopes.TryGetValue(runId, out var scope) ||
            scope.TaskId != taskId || scope.SourceConversationId != graph.SourceConversationId ||
            !ReferenceEquals(teamPresenters.GetValueOrDefault(runId), presenter) ||
            TeamRun(runId) is not { } coordinate || viewport.RunId != runId ||
            viewport.ConversationId != coordinate.ConversationId) return;
        var prior = teamReadPositions.GetValueOrDefault(runId);
        var updated = AdvanceTeamReadPosition(prior, taskId, graph.SourceConversationId,
            viewport, viewport.LoadedRunRevision, DateTimeOffset.UtcNow);
        if (updated == prior) return;
        teamReadPositions[runId] = updated;
        while (teamReadPositions.Count > MaximumRetainedInspectionStates)
            teamReadPositions.Remove(teamReadPositions.Keys.First());
        RenderTeamSwitcher();
    }

    private sealed record TeamOpeningPreimage(string? TaskId, CollaborationViewGraph? Graph,
        string? RunId, bool Active, string? Issue, object? Content, string? Heading,
        string? State, System.Windows.Media.Brush? StateColor, object DockPresentation);

    private TeamOpeningPreimage CaptureTeamOpeningPreimage() => new(teamTaskId, teamGraph,
        teamSelectedRunId, teamMonitorActive, teamMonitorIssue, teamMonitorSurface?.Content,
        teamMonitorHeading?.Text, teamMonitorState?.Text, teamMonitorState?.Foreground, CaptureConversationDockPresentations());

    private void RestoreTeamOpeningPreimage(TeamOpeningPreimage prior)
    {
        teamTaskId = prior.TaskId; teamGraph = prior.Graph;
        teamSelectedRunId = prior.RunId; teamMonitorIssue = prior.Issue;
        teamMonitorActive = prior.Active;
        if (!prior.Active) { teamNavigationVersion++; workPanelState.SetMainExpanded(true); }
        else
        {
            teamMonitorActive = true;
            if (prior.RunId is { } runId && teamWorkPanels.TryGetValue(runId, out var panel))
            {
                workPanelState.SetExpanded(runId, true);
                teamMonitorSurface = panel.Body; teamMonitorHeader = panel.Header;
                teamMonitorHeading = panel.Heading; teamMonitorState = panel.State; teamPopoutButton = panel.Popout;
                if (TeamRun(runId)?.ParentRunId is null && coordinatorDockBody is not null)
                {
                    coordinatorDockBody.Content = panel.Body; coordinatorDockBody.Tag = runId;
                    coordinatorDockState?.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = panel.State });
                }
            }
            if (teamMonitorSurface is not null) teamMonitorSurface.Content = prior.Content;
            if (teamMonitorHeading is not null) teamMonitorHeading.Text = prior.Heading ?? "Coordinator · read-only";
            if (teamMonitorState is not null)
            {
                teamMonitorState.Text = prior.State ?? "";
                if (prior.StateColor is not null) teamMonitorState.Foreground = prior.StateColor;
            }
        }
        RenderTeamSwitcher();
        RestoreConversationDockPresentations(prior.DockPresentation);
    }

    private FrameworkElement BuildTeamSwitcher()
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 3, 8) };
        var list = new ScrollViewer { Content = teamSwitchRows, MaxHeight = 210,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        group.Children.Add(list);
        RenderTeamSwitcher();
        return group;
    }

    private void InitializeTeamMonitor(Grid workArea)
    {
        teamRefreshTimer.Tick += (_, _) =>
        {
            teamRefreshTimer.Stop();
            if (teamTasks.Count > 0 && teamSourceConversationId == durableConversationId)
                Run(RefreshTeamForCurrentMainAsync);
        };
        teamMonitorHeading = Ui.Text("Coordinator", 16);
        teamMonitorState = Ui.Text("", 12, Ui.Muted);
        var back = Ui.Button("Main", ShowMainTeamView);
        teamPopoutButton = Ui.Button("Pop out", PopOutTeamPresenter);
        AutomationProperties.SetAutomationId(back, "TeamBackToMain");
        AutomationProperties.SetAutomationId(teamPopoutButton, "TeamPopOut");
        var header = Ui.Bar(back, teamMonitorHeading, teamMonitorState, teamPopoutButton);
        teamMonitorHeader = new Border { Child = header, Visibility = Visibility.Collapsed,
            BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, 0, 0, 8) };
        var side = new Grid { ClipToBounds = true, Visibility = Visibility.Collapsed };
        teamSidePane = side;
        teamMonitorSurface = new ContentControl { Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetAutomationId(teamMonitorSurface, "TeamMonitorSurface");
        var scroller = new ScrollViewer { Content = teamPanelCards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroller.ScrollChanged += (_, _) =>
        {
            foreach (var entry in teamWorkPanels.Values.Where(p => p.Panel.IsExpanded))
                if (entry.Body.Content is WorkflowWorkspace presenter) presenter.RefreshReadViewportVisibility();
        };
        side.Children.Add(scroller);
        Closed += (_, _) =>
        {
            teamRefreshTimer.Stop();
            teamPopout?.Close();
            ClearStackedTeamPanels();
            foreach (var presenter in teamPresenters.Values) presenter.Dispose();
            teamPresenters.Clear();
            teamPresenterScopes.Clear();
            teamInspectionStates.Clear();
            teamPresenterRecency.Clear(); teamPresenterNodes.Clear();
        };
    }

    private void ShowTeamSide()
    {
        if (TeamRun(teamSelectedRunId) is not { } run) return;
        ShowConversationDock(run.ParentRunId is null ? coordinatorDockPanel! : workersDockPanel!);
    }

    private void QueueTeamRefresh()
    {
        if (teamTasks.Count > 0 && teamSourceConversationId == durableConversationId &&
            !teamRefreshTimer.IsEnabled) teamRefreshTimer.Start();
    }

    private void SetTeamMonitorIssue(string issue)
    {
        teamMonitorIssue = issue;
        if (teamMonitorActive && teamMonitorState is not null)
        { teamMonitorState.Text = "Unavailable · " + issue; teamMonitorState.Foreground = Ui.ErrorText; }
        UpdateTeamPopoutHeader();
    }

    private static IReadOnlyList<CollaborationViewTask> ReconcileTeamTasks(
        IReadOnlyList<CollaborationViewTask> latest, IReadOnlyList<CollaborationViewTask> retained,
        bool retainOlderPages, string? selectedTaskId = null)
    {
        if (!retainOlderPages) return latest;
        var merged = latest.Concat(retained).DistinctBy(task => task.TaskId).Take(200).ToArray();
        if (selectedTaskId is { } selected && merged.Length > 0 &&
            merged.All(task => task.TaskId != selected) &&
            retained.FirstOrDefault(task => task.TaskId == selected) is { } pinned)
            merged[^1] = pinned;
        return merged;
    }

    private void ReconcileTeamMonitorAfterRefresh(CollaborationViewGraph? graph)
    {
        if (!teamMonitorActive || teamSelectedRunId is not { } runId) return;
        var current = TeamRun(runId);
        if (current is null || graph?.Complete != true)
            SetTeamMonitorIssue(graph?.IncompleteReason ?? "Selected run is missing from the team graph.");
        else { teamMonitorIssue = null; UpdateTeamMonitorHeader(current); }
        UpdateTeamPopoutHeader();
    }

    private static async Task<(bool Current, CollaborationViewGraph? Graph, Exception? Error)>
        AwaitTeamGraphForRefreshAsync(Func<Task<CollaborationViewGraph>> read, Func<bool> stillCurrent)
    {
        try
        {
            var graph = await read();
            return stillCurrent() ? (true, graph, null) : (false, null, null);
        }
        catch (Exception) when (!stillCurrent())
        {
            return (false, null, null);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or KeyNotFoundException or
            IOException or TimeoutException)
        {
            return (true, null, error);
        }
    }

    private async Task RefreshTeamForCurrentMainAsync()
    {
        var version = ++teamReadVersion;
        var navigationVersion = teamNavigationVersion;
        var selectedTaskAtStart = teamTaskId;
        var source = IsNativeChat ? durableConversationId : null;
        if (!IsConnected || source is null || nativeChat?.Snapshot?.RunId is null)
        {
            if (teamMonitorActive && source == teamSourceConversationId)
            { SetTeamMonitorIssue("Team data cannot be refreshed on this connection."); return; }
            teamTasks = []; teamGraph = null; teamSourceConversationId = source;
            teamSnapshotRow = teamNextBeforeRow = null;
            teamOlderPagesLoaded = false;
            RenderTeamSwitcher();
            return;
        }
        var client = Client;
        var identity = ServiceConnectionIdentity;
        var selected = conversationSelectionVersion;
        bool StillCurrent() => version == teamReadVersion && navigationVersion == teamNavigationVersion &&
            selected == conversationSelectionVersion && selectedTaskAtStart == teamTaskId &&
            source == durableConversationId && IsConnected && ReferenceEquals(client, Client) &&
            identity == ServiceConnectionIdentity;
        CommandResult response;
        try
        {
            response = await client.CommandAsync("collaboration.view.list",
                new { sourceConversationId = source, limit = 40 }, cancellationToken: Lifetime.Token);
        }
        catch (Exception) when (!StillCurrent()) { return; }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        { SetTeamMonitorIssue("Team list refresh failed."); SetStatus(error.Message, true); return; }
        if (!StillCurrent()) return;
        if (!response.Success)
        {
            SetTeamMonitorIssue(response.Error ?? "Team list refresh failed.");
            SetStatus("Team view unavailable: " + (response.Error ?? "refresh failed"), true);
            return;
        }
        var page = CerebralJson.Read<CollaborationViewTaskPage>(response.Data);
        if (page.Tasks.Count > 40 || page.Tasks.Any(task => string.IsNullOrWhiteSpace(task.TaskId)))
            throw new InvalidDataException("The collaboration task page is invalid.");
        var retainOlderPages = teamOlderPagesLoaded && teamSourceConversationId == source;
        var tasks = ReconcileTeamTasks(page.Tasks, teamTasks, retainOlderPages, teamTaskId).ToList();
        if (!retainOlderPages && teamSourceConversationId == source && teamTaskId is { } retainedId &&
            tasks.All(task => task.TaskId != retainedId) &&
            teamTasks.FirstOrDefault(task => task.TaskId == retainedId) is { } retained)
            tasks.Add(retained);
        var mainRunId = nativeChat?.Snapshot?.RunId;
        var acceptedCurrent = mainRunId is not null && manuallySelectedTeamMainRun != mainRunId &&
            !workPanelState.IsFollowingSourceRun(mainRunId)
            ? tasks.FirstOrDefault(task => task.SourceRunId == mainRunId) : null;
        var taskId = acceptedCurrent?.TaskId ?? (teamMonitorActive && teamTaskId is not null ? teamTaskId :
            tasks.Any(task => task.TaskId == teamTaskId) ? teamTaskId : tasks.FirstOrDefault()?.TaskId);
        CollaborationViewGraph? graph = null;
        if (taskId is not null)
        {
            var result = await AwaitTeamGraphForRefreshAsync(
                () => ReadTeamGraphAsync(taskId, source, client, Lifetime.Token), StillCurrent);
            if (!result.Current) return;
            if (result.Error is { } error)
            { SetTeamMonitorIssue("Team graph refresh failed."); SetStatus(error.Message, true); return; }
            graph = result.Graph;
        }
        if (!StillCurrent()) return;
        if (teamTaskId != taskId && !teamMonitorActive) teamPopout?.Close();
        teamTasks = tasks; teamSourceConversationId = source; teamTaskId = taskId; teamGraph = graph;
        teamSnapshotRow = retainOlderPages ? teamSnapshotRow : page.SnapshotRow;
        teamNextBeforeRow = retainOlderPages ? teamNextBeforeRow : page.NextBeforeRow;
        teamOlderPagesLoaded = retainOlderPages;
        foreach (var stale in teamWorkersExpanded.Keys.Except(tasks.Select(task => task.TaskId), StringComparer.Ordinal).ToArray())
            teamWorkersExpanded.Remove(stale);
        RenderTeamSwitcher();
        ReconcileTeamMonitorAfterRefresh(graph);
        UpdateStackedTeamPanels(graph);
        UpdateTeamPopoutHeader();
        QueueSharedActivityUpdate();
    }

    private async Task LoadOlderTeamTasksAsync()
    {
        if (teamSourceConversationId is not { } source || teamNextBeforeRow is not { } before ||
            teamSnapshotRow is not { } snapshot || teamTasks.Count >= 200) return;
        var version = ++teamReadVersion;
        var client = Client; var identity = ServiceConnectionIdentity;
        var response = await client.CommandAsync("collaboration.view.list",
            new { sourceConversationId = source, limit = 40, beforeRow = before, snapshotRow = snapshot },
            cancellationToken: Lifetime.Token);
        if (version != teamReadVersion || source != durableConversationId ||
            !ReferenceEquals(client, Client) || identity != ServiceConnectionIdentity) return;
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "Older team tasks are unavailable.");
        var page = CerebralJson.Read<CollaborationViewTaskPage>(response.Data);
        if (page.SnapshotRow != snapshot || page.Tasks.Count > 40 ||
            page.NextBeforeRow is { } next && next >= before)
            throw new InvalidDataException("The older team task page changed its snapshot or cursor.");
        teamTasks = teamTasks.Concat(page.Tasks).DistinctBy(task => task.TaskId).Take(200).ToArray();
        teamNextBeforeRow = page.NextBeforeRow;
        teamOlderPagesLoaded = true;
        RenderTeamSwitcher();
    }

    private async Task<CollaborationViewGraph> ReadTeamGraphAsync(string taskId, string sourceConversationId, CerebralClient client,
        CancellationToken token)
    {
        var response = await client.CommandAsync("collaboration.view.graph",
            new { taskId, childOffset = 0, limit = CollaborationContract.MaximumViewChildren },
            cancellationToken: token);
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "Team view is unavailable.");
        var graph = CerebralJson.Read<CollaborationViewGraph>(response.Data);
        if (graph.Version != 1 || graph.TaskId != taskId ||
            graph.SourceConversationId != sourceConversationId || graph.ChildCount is < 0 or > CollaborationContract.MaximumViewChildren ||
            graph.Workers.Count > CollaborationContract.MaximumViewChildren || graph.NextChildOffset is not null ||
            graph.Complete && (graph.Coordinator is null || graph.Workers.Count != graph.ChildCount))
            throw new InvalidDataException("Team view returned incomplete or foreign navigation coordinates.");
        return graph;
    }

    private void RenderTeamSwitcher()
    {
        // One task picker and one Workers list retain navigation without per-run bars.
        RefreshCompactTeamNavigation();
    }

    private async Task OpenTeamTaskAsync(string taskId)
    {
        manuallySelectedTeamMainRun = nativeChat?.Snapshot?.RunId;
        if (teamTaskOpening) return;
        var navigationVersion = ++teamNavigationVersion;
        var presentationEpoch = conversationDockPresentationEpoch;
        if (teamSourceConversationId != durableConversationId) await RefreshTeamForCurrentMainAsync();
        if (navigationVersion != teamNavigationVersion) return;
        if (!teamTasks.Any(task => task.TaskId == taskId)) return;
        var source = durableConversationId ?? throw new InvalidOperationException("Open a Main conversation first.");
        var client = Client; var identity = ServiceConnectionIdentity;
        var graph = await ReadTeamGraphAsync(taskId, source, client, Lifetime.Token);
        if (navigationVersion != teamNavigationVersion || graph.SourceConversationId != durableConversationId || !ReferenceEquals(client, Client) ||
            identity != ServiceConnectionIdentity || !teamTasks.Any(task => task.TaskId == taskId)) return;
        if (!graph.Complete || graph.Coordinator is null)
        {
            SetStatus("Coordinator view is unavailable: " + graph.IncompleteReason, true);
            return;
        }
        if (teamTaskOpening) return;
        teamTaskOpening = true;
        var prior = CaptureTeamOpeningPreimage();
        teamTaskId = taskId; teamGraph = graph; teamMonitorIssue = null;
        try
        {
            ReconcileConversationDockTask(prior.TaskId, taskId);
            await OpenTeamRunAsync(graph.Coordinator, ownsNavigation: false, expectedPresentationEpoch: presentationEpoch);
            if (navigationVersion != teamNavigationVersion) return;
            if (!teamMonitorActive || teamSelectedRunId != graph.Coordinator.RunId)
                throw new InvalidOperationException("The selected Coordinator changed while opening its view.");
            if (prior.TaskId != taskId) teamPopout?.Close();
            RenderTeamSwitcher();
        }
        catch
        {
            if (navigationVersion == teamNavigationVersion && source == durableConversationId &&
                ReferenceEquals(client, Client) && identity == ServiceConnectionIdentity)
                RestoreTeamOpeningPreimage(prior);
            throw;
        }
        finally { teamTaskOpening = false; }
    }

    private CollaborationViewRun? TeamRun(string? runId) => runId is null || teamGraph is not { Complete: true } graph
        ? null : graph.Coordinator?.RunId == runId ? graph.Coordinator :
            graph.Workers.FirstOrDefault(worker => worker.RunId == runId);

    private static bool SameTeamIdentity(CollaborationViewRun? current, CollaborationViewRun expected) =>
        current is not null && current.RunId == expected.RunId &&
        current.ConversationId == expected.ConversationId && current.ProfileId == expected.ProfileId &&
        current.ProjectId == expected.ProjectId && current.ParentRunId == expected.ParentRunId &&
        current.Revision >= expected.Revision;

    private async Task OpenTeamRunAsync(CollaborationViewRun coordinate, bool selectPanel = true, bool ownsNavigation = true, long? expectedPresentationEpoch = null)
    {
        if (teamGraph is not { Complete: true } graph || graph.SourceConversationId != durableConversationId ||
            !SameTeamIdentity(TeamRun(coordinate.RunId), coordinate)) throw new InvalidOperationException("The selected team run changed. Refresh the team view.");
        var presentationEpoch = expectedPresentationEpoch ?? conversationDockPresentationEpoch;
        if (selectPanel && ownsNavigation) teamNavigationVersion++;
        var navigationVersion = teamNavigationVersion;
        if (selectPanel)
        {
            EnsureTeamWorkPanel(coordinate, graph.TaskId);
            workPanelState.SetExpanded(coordinate.RunId, true);
            if (presentationEpoch == conversationDockPresentationEpoch) ApplyWorkPanelSelection();
        }
        var owner = Client; var ownerIdentity = ServiceConnectionIdentity;
        if (teamPresenterConnection != ServiceConnectionIdentity || !ReferenceEquals(teamPresenterClient, Client))
        {
            teamPopout?.Close();
            if (teamPresenterConnection is not null || teamPresenterClient is not null) ClearStackedTeamPanels();
            if (teamMonitorSurface is not null) teamMonitorSurface.Content = null;
            foreach (var prior in teamPresenters.Values) prior.Dispose();
            teamPresenters.Clear(); teamPresenterScopes.Clear();
            teamPresenterRecency.Clear(); teamPresenterNodes.Clear();
            if (teamPresenterConnection is not null || teamPresenterClient is not null)
            { teamInspectionStates.Clear(); teamReadPositions.Clear(); teamWorkersExpanded.Clear(); }
            teamPresenterConnection = ServiceConnectionIdentity;
            teamPresenterClient = Client;
        }
        if (teamPresenters.ContainsKey(coordinate.RunId) &&
            (!teamPresenterScopes.TryGetValue(coordinate.RunId, out var scope) ||
             scope.TaskId != graph.TaskId || scope.SourceConversationId != graph.SourceConversationId))
            throw new InvalidDataException("A team run ID was reused by another task or Main conversation.");
        if (!teamPresenters.TryGetValue(coordinate.RunId, out var presenter))
        {
            var client = Client; var identity = ServiceConnectionIdentity;
            presenter = new WorkflowWorkspace((request, token) => TeamReadCommandAsync(client, identity,
                    graph.TaskId, coordinate, request, token), () => profiles, ReportNativeCompletionStatus,
                embedded: true);
            presenter.UseMonitoringOnly();
            presenter.ReadViewportChanged += (_, viewport) =>
                ObserveTeamReadViewport(graph.TaskId, coordinate.RunId, presenter, viewport);
            presenter.SnapshotChanged += (_, _) => { QueueSharedActivityUpdate(); RefreshWorkerDockRows(); };
            presenter.ActivityNavigationRequested += target =>
            {
                if (ReferenceEquals(client, Client) && identity == ServiceConnectionIdentity &&
                    teamGraph?.TaskId == graph.TaskId && teamGraph.SourceConversationId == graph.SourceConversationId &&
                    teamPresenters.TryGetValue(coordinate.RunId, out var current) && ReferenceEquals(current, presenter))
                    OpenTurnActivityPage(presenter, target);
            };
            try
            {
                await presenter.OpenRunSnapshotAsync(coordinate.RunId, Lifetime.Token);
                if (teamInspectionStates.TryGetValue(coordinate.RunId, out var saved) &&
                    saved.Scope.TaskId == graph.TaskId &&
                    saved.Scope.SourceConversationId == graph.SourceConversationId)
                    await presenter.RestoreInspectionStateAsync(saved.View, Lifetime.Token);
            }
            catch { presenter.Dispose(); throw; }
            if (!ReferenceEquals(owner, Client) || ownerIdentity != ServiceConnectionIdentity ||
                navigationVersion != teamNavigationVersion ||
                teamGraph?.TaskId != graph.TaskId || teamGraph.SourceConversationId != durableConversationId ||
                !SameTeamIdentity(TeamRun(coordinate.RunId), coordinate))
            { presenter.Dispose(); return; }
            teamPresenters.Add(coordinate.RunId, presenter);
            teamPresenterScopes.Add(coordinate.RunId, new(graph.TaskId, graph.SourceConversationId));
        }
        else await presenter.InspectRunAsync(coordinate.RunId, Lifetime.Token);
        if (!ReferenceEquals(owner, Client) || ownerIdentity != ServiceConnectionIdentity ||
            navigationVersion != teamNavigationVersion ||
            teamGraph?.TaskId != graph.TaskId || teamGraph.SourceConversationId != durableConversationId ||
            !SameTeamIdentity(TeamRun(coordinate.RunId), coordinate)) return;
        MountTeamWorkPanel(coordinate, presenter);
        teamSelectedRunId = coordinate.RunId; teamMonitorActive = true;
        RetainTeamPresenter(coordinate.RunId);
        // A late successful read may refresh retained data, but cannot undo a human collapse.
        if (presentationEpoch == conversationDockPresentationEpoch) ShowTeamSide();
        teamMonitorHeader.Visibility = teamMonitorSurface.Visibility = Visibility.Visible;
        UpdateTeamMonitorHeader(coordinate);
        presenter.ObserveReadViewport();
        RenderTeamSwitcher();
        QueueSharedActivityUpdate();
    }

    private void RetainTeamPresenter(string runId)
    {
        if (teamPresenterNodes.Remove(runId, out var prior)) teamPresenterRecency.Remove(prior);
        teamPresenterNodes[runId] = teamPresenterRecency.AddLast(runId);
        while (teamPresenterNodes.Count > MaximumRetainedTeamPresenters)
        {
            var oldest = teamPresenterRecency.First!;
            if (oldest.Value == runId || oldest.Value == teamGraph?.Coordinator?.RunId || teamPopoutPresenter == teamPresenters.GetValueOrDefault(oldest.Value))
            { teamPresenterRecency.RemoveFirst(); teamPresenterRecency.AddLast(oldest); continue; }
            teamPresenterRecency.RemoveFirst(); teamPresenterNodes.Remove(oldest.Value);
            if (teamPresenters.Remove(oldest.Value, out var presenter))
            {
                SaveTeamInspectionState(oldest.Value, presenter);
                if (teamWorkPanels.Remove(oldest.Value, out var panel))
                {
                    panel.Body.Content = Ui.Text("Open this row's details to reload its saved view.", 12, Ui.Muted);
                    // Cache eviction does not own the shared Workers docking bar or selection.
                }
                teamPresenterScopes.Remove(oldest.Value);
                presenter.Dispose();
            }
        }
    }

    private void SaveTeamInspectionState(string runId, WorkflowWorkspace presenter)
    {
        if (!teamPresenterScopes.TryGetValue(runId, out var scope) ||
            presenter.CaptureInspectionState() is not { } saved) return;
        teamInspectionStates[runId] = new(scope, saved);
        while (teamInspectionStates.Count > MaximumRetainedInspectionStates)
            teamInspectionStates.Remove(teamInspectionStates.Keys.First());
    }

    private async Task<CommandResult> TeamReadCommandAsync(CerebralClient client, string identity,
        string taskId, CollaborationViewRun coordinate, CommandRequest request, CancellationToken token)
    {
        if (!ReferenceEquals(client, Client) || identity != ServiceConnectionIdentity ||
            teamGraph?.TaskId != taskId || teamGraph.Complete != true ||
            !SameTeamIdentity(TeamRun(coordinate.RunId), coordinate))
            throw new InvalidOperationException("The team view belongs to an earlier owner connection or task.");
        if (request.Operation is not ("jobs.workflow.get" or "jobs.workflow.events" or "jobs.workflow.events.previous") ||
            request.Arguments.ValueKind != JsonValueKind.Object ||
            !request.Arguments.TryGetProperty("id", out var id) || id.GetString() != coordinate.RunId)
            throw new UnauthorizedAccessException("Team monitoring allows exact read-only run inspection only.");
        var result = await client.CommandAsync(request.Operation, request.Arguments, cancellationToken: token);
        if (!ReferenceEquals(client, Client) || identity != ServiceConnectionIdentity ||
            teamGraph?.TaskId != taskId || teamGraph.SourceConversationId != durableConversationId ||
            !SameTeamIdentity(TeamRun(coordinate.RunId), coordinate))
            throw new InvalidOperationException("The team view changed while reading its run.");
        if (result.Success && request.Operation == "jobs.workflow.get")
        {
            var run = CerebralJson.Read<WorkflowRun>(result.Data);
            if (run.Id != coordinate.RunId || run.Submission.ConversationId != coordinate.ConversationId ||
                run.Submission.ProfileId != coordinate.ProfileId || run.Submission.ProjectId != coordinate.ProjectId ||
                run.Submission.ParentRunId != coordinate.ParentRunId || run.Revision < coordinate.Revision ||
                run.Submission.DefinitionId != ConversationWorkflowContract.DefinitionId ||
                run.Submission.DefinitionVersion != ConversationWorkflowContract.DefinitionVersion ||
                run.Submission.Caller is not { Kind: EventActorKind.Service, Id: var caller } ||
                caller != (coordinate.ParentRunId is null ? "cerebral.collaboration" : "cerebral.coordination"))
                throw new InvalidDataException("The monitored run no longer matches its accepted team lineage.");
        }
        return result;
    }

    private void UpdateTeamMonitorHeader(CollaborationViewRun run)
    {
        if (teamMonitorHeading is null || teamMonitorState is null) return;
        teamMonitorHeading.Text = run.ParentRunId is null ? "Coordinator · read-only" : "Worker · " + RecordedTeamAgentName(run) + " · read-only";
        teamMonitorState.Text = teamMonitorIssue is { } issue ? "Unavailable · " + issue :
            run.Status + (teamGraph?.Synchronized == false && run.ParentRunId is null ? " · task updating" : "");
        teamMonitorState.Foreground = teamMonitorIssue is null ? Ui.Muted : Ui.ErrorText;
        if (teamPopoutButton is not null) teamPopoutButton.IsEnabled = teamPopout is null;
        UpdateTeamPopoutHeader();
    }

    private void UpdateTeamPopoutHeader()
    {
        if (teamPopout is null || teamPopoutRunId is not { } runId ||
            teamPopoutHeading is null || teamPopoutState is null) return;
        var run = TeamRun(runId);
        var issue = !teamMonitorActive || runId == teamSelectedRunId ? teamMonitorIssue : null;
        teamPopoutHeading.Text = run is null ? "Team conversation · read-only" :
            run.ParentRunId is null ? "Coordinator · read-only" :
                "Worker · " + RecordedTeamAgentName(run) + " · read-only";
        teamPopoutState.Text = issue is not null || run is null
            ? "Unavailable · " + (issue ?? teamGraph?.IncompleteReason ?? "Run is absent from the current team graph.")
            : run.Status + (teamGraph?.Synchronized == false && run.ParentRunId is null ? " · task updating" : "");
        teamPopoutState.Foreground = issue is null && run is not null ? Ui.Muted : Ui.ErrorText;
        teamPopout.Title = teamPopoutHeading.Text + " · " + teamPopoutState.Text;
    }

    private void ShowMainTeamView()
    {
        teamNavigationVersion++;
        teamMonitorActive = false; teamSelectedRunId = null; teamMonitorIssue = null;
        workPanelState.SetMainExpanded(true);
        if (mainWorkPanel is not null) OpenConversationPeer(mainWorkPanel);
        UpdateWorkPanelRows();
        RenderTeamSwitcher();
        QueueSharedActivityUpdate();
    }

    private void ResetTeamForChangedMain(bool ownerChanged = false)
    {
        teamRefreshTimer.Stop();
        ShowMainTeamView();
        teamPopout?.Close();
        ClearStackedTeamPanels();
        if (!ownerChanged)
            foreach (var (runId, presenter) in teamPresenters)
                SaveTeamInspectionState(runId, presenter);
        foreach (var presenter in teamPresenters.Values) presenter.Dispose();
        teamPresenters.Clear(); teamPresenterScopes.Clear();
        teamPresenterRecency.Clear(); teamPresenterNodes.Clear();
        if (ownerChanged)
        { teamInspectionStates.Clear(); teamReadPositions.Clear(); teamWorkersExpanded.Clear();
          teamPresenterConnection = null; teamPresenterClient = null; }
        teamTasks = []; teamGraph = null; teamTaskId = null; teamSourceConversationId = null;
        teamSnapshotRow = teamNextBeforeRow = null;
        teamOlderPagesLoaded = false;
        teamReadVersion++; teamNavigationVersion++;
        RenderTeamSwitcher();
        // A different Main root must not inherit rows, filter options or detail
        // windows, including when its first response is empty or arrives late.
        ClearSharedActivity(ownerChanged: true);
    }

    private void PopOutTeamPresenter()
    {
        if (!teamMonitorActive || teamSelectedRunId is not { } id || teamMonitorSurface is null ||
            teamPresenters.GetValueOrDefault(id) is not { } presenter || teamPopout is not null) return;
        teamMonitorSurface.Content = Ui.Text("This view is open in its pop-out window.", 12, Ui.Muted);
        teamPopoutPresenter = presenter;
        teamPopoutRunId = id;
        teamPopoutHeading = Ui.Text("Team conversation · read-only", 15);
        teamPopoutState = Ui.Text("", 12, Ui.Muted);
        AutomationProperties.SetAutomationId(teamPopoutHeading, "TeamPopoutHeading");
        AutomationProperties.SetAutomationId(teamPopoutState, "TeamPopoutState");
        var header = new Border { Child = Ui.Bar(teamPopoutHeading, teamPopoutState),
            BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 8, 10, 8) };
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        teamPopoutContent = new ContentControl { Content = presenter,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        body.Children.Add(teamPopoutContent);
        var window = new Window { Owner = this, Title = teamMonitorHeading?.Text ?? "Team view",
            Width = 940, Height = 680, MinWidth = 500, MinHeight = 360,
            Content = body };
        teamPopout = window;
        UpdateTeamPopoutHeader();
        window.Closed += (_, _) =>
        {
            if (teamPopoutContent is not null) teamPopoutContent.Content = null;
            window.Content = null;
            teamPopout = null; teamPopoutPresenter = null; teamPopoutRunId = null;
            teamPopoutContent = null; teamPopoutHeading = null; teamPopoutState = null;
            if (teamWorkPanels.TryGetValue(id, out var panel)) panel.Body.Content = presenter;
            if (teamPopoutButton is not null) teamPopoutButton.IsEnabled = true;
        };
        if (teamPopoutButton is not null) teamPopoutButton.IsEnabled = false;
        window.Show();
    }
}
