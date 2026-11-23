using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Desktop;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Core;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Local WPF state checks only; no Worker, service, model or shown window.</summary>
    internal async Task<int> CheckTeamViewsUiAsync()
    {
        var checks = CheckEmbeddedActivityScopeContracts();
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        static CollaborationViewTask TaskRow(string id, long revision) =>
            new(id, revision, CollaborationTaskStatus.Running, "coordinator-chat", "lead", null,
                "coordinator-run", revision, DateTimeOffset.UnixEpoch);
        static CollaborationViewGraph Graph(string taskId, bool complete = true) =>
            new(1, taskId, 1, CollaborationTaskStatus.Running, "main-chat", "main", null,
                "coordinator-chat", "lead", null,
                complete ? new("coordinator-run", "coordinator-chat", "lead", null, null, WorkflowStatus.Running, 1) : null,
                complete ? [new("worker-run", "worker-chat", "helper", null, "coordinator-run", WorkflowStatus.Running, 1)] : [],
                complete ? 1 : 0, null, complete, complete, complete ? null : "fixture-unavailable");

        teamTasks = [TaskRow("task-a", 1), TaskRow("older-b", 1)];
        teamTaskId = "task-a"; teamGraph = Graph("task-a"); teamSourceConversationId = "main-chat";
        teamMonitorActive = true; teamSelectedRunId = "worker-run";
        var marker = new TextBlock { Text = "Retained exact worker presenter" };
        var workerPanel = EnsureTeamWorkPanel(teamGraph.Workers[0], "task-a");
        teamMonitorSurface = workerPanel.Body; teamMonitorHeader = workerPanel.Header;
        teamMonitorHeading = workerPanel.Heading; teamMonitorState = workerPanel.State; teamPopoutButton = workerPanel.Popout;
        teamMonitorSurface.Content = marker; teamMonitorSurface.Visibility = Visibility.Visible;
        teamMonitorHeader.Visibility = Visibility.Visible;
        changingWorkPanels = true;
        try { workerPanel.Panel.IsExpanded = true; }
        finally { changingWorkPanels = false; }
        ShowTeamSide();
        teamMonitorHeading.Text = "Worker · helper · read-only"; teamMonitorState.Text = "Running";
        var layout = (FrameworkElement)Content;
        layout.Measure(new Size(1280, 840)); layout.Arrange(new Rect(0, 0, 1280, 840)); layout.UpdateLayout();
        Check(mainWorkPanel?.IsExpanded == false && coordinatorDockPanel?.IsExpanded == false && workersDockPanel?.IsExpanded == true &&
            workersDockPanel.ActualWidth > 200 && workersDockPanel.ActualHeight > 70 &&
            ReferenceEquals(teamMonitorSurface.Content, marker),
            "Read-only worker inspection selects only Workers while retaining the exact Main controls and worker presenter.");
        Check(ReferenceEquals(coordinatorDockPanel!.Parent, workersDockPanel!.Parent) &&
            !workersDockPanel.IsDescendantOf(mainWorkPanel!) &&
            coordinatorDockPanel.Template.FindName("DockHeaderButton", coordinatorDockPanel) is System.Windows.Controls.Primitives.ToggleButton &&
            workersDockPanel.Template.FindName("DockHeaderButton", workersDockPanel) is System.Windows.Controls.Primitives.ToggleButton,
            "Main, Coordinator and Workers retain their actual compact peer docking headers.");
        Check(sharedActivityPanel is not null && !workPanelLayout!.Children.Contains(sharedActivityPanel) &&
            sharedActivityPanel.Content is ActivityTimelineView && pages.ContainsKey("activity"),
            "The one existing Activity timeline is registered only on its independent page.");
        var priorDraft = prompt.Text; var priorChat = CurrentConversationId;
        SelectPage("activity");
        layout.Measure(new Size(720, 540)); layout.Arrange(new Rect(0, 0, 720, 540)); layout.UpdateLayout();
        var activityTable = ((ActivityTimelineView)sharedActivityPanel!.Content).Children.OfType<ListView>().Single();
        Check(ReferenceEquals(Tabs.SelectedItem, pages["activity"]) && activityTable.ActualHeight >= 70 &&
            !mainWorkPanel!.IsExpanded && workersDockPanel.IsExpanded && prompt.Text == priorDraft && CurrentConversationId == priorChat,
            "The compact Activity page has usable height and preserves Main, side inspection, draft and chat identity.");
        layout.Measure(new Size(1280, 840)); layout.Arrange(new Rect(0, 0, 1280, 840)); layout.UpdateLayout();
        Check(activityTable.ActualHeight >= 70, "Resizing the dedicated Activity page retains its table.");
        SelectPage("chat"); layout.UpdateLayout();
        Check(!mainWorkPanel.IsExpanded && workersDockPanel.IsExpanded && ReferenceEquals(teamMonitorSurface.Content, marker),
            "Returning to Chat retains the exact worker presenter and hidden Main state without another nested Activity panel.");
        workerOrder!.SelectedIndex = 1; workerActiveFirst!.IsChecked = false;
        RenderTeamSwitcher();
        Check(workerOrder.SelectedIndex == 1 && workerActiveFirst.IsChecked == false &&
            ReferenceEquals(workerDockGrid!.ItemsSource, workerDockRows) && teamSwitchRows.Children.Count == 0,
            "Refreshing compact navigation preserves existing ordering choices and the one current Workers data grid.");
        SetTeamMonitorIssue("Fixture list unavailable.");
        Check(teamMonitorActive && ReferenceEquals(teamMonitorSurface.Content, marker) &&
              workersWorkPanel?.IsExpanded == true &&
              conversationPrimarySurface?.Visibility == Visibility.Visible &&
              composerLayerHost?.Visibility == Visibility.Visible &&
              teamMonitorState.Text.StartsWith("Unavailable", StringComparison.Ordinal),
            "A failed background read retains Main state and the selected read-only worker panel.");
        teamGraph = Graph("task-a", complete: false);
        ReconcileTeamMonitorAfterRefresh(teamGraph);
        Check(teamMonitorActive && ReferenceEquals(teamMonitorSurface.Content, marker) &&
              workersWorkPanel?.IsExpanded == true &&
              teamMonitorState.Text.Contains("fixture-unavailable", StringComparison.Ordinal),
            "An incomplete refreshed graph retains the inspected pane and marks it unavailable.");
        teamGraph = Graph("task-a");
        var prior = CaptureTeamOpeningPreimage();
        teamTaskId = "task-b"; teamGraph = Graph("task-b");
        teamMonitorSurface.Content = new TextBlock { Text = "Opening another task" };
        teamMonitorHeading.Text = "Coordinator · opening";
        RestoreTeamOpeningPreimage(prior);
        Check(teamTaskId == "task-a" && teamGraph?.TaskId == "task-a" &&
              ReferenceEquals(teamMonitorSurface.Content, marker) &&
              teamMonitorHeading.Text == "Worker · helper · read-only" &&
              teamMonitorState.Text.StartsWith("Unavailable", StringComparison.Ordinal),
            "A failed task switch restores task identity, presenter and header together.");

        var latest = new[] { TaskRow("new-c", 1), TaskRow("task-a", 2) };
        var merged = ReconcileTeamTasks(latest, teamTasks, retainOlderPages: true);
        Check(merged.Select(row => row.TaskId).SequenceEqual(["new-c", "task-a", "older-b"]) &&
              merged[1].Revision == 2,
            "A normal refresh updates current rows while retaining explicitly loaded older tasks.");
        Check(ReconcileTeamTasks(latest, teamTasks, retainOlderPages: false)
              .Select(row => row.TaskId).SequenceEqual(["new-c", "task-a"]),
            "A new page set starts from the current snapshot when older pages were not loaded.");
        var oversized = Enumerable.Range(0, 240).Select(index => TaskRow("older-" + index, 1)).ToArray();
        Check(ReconcileTeamTasks(latest, oversized, retainOlderPages: true).Count == 200,
            "Retained task navigation remains bounded at 200 rows.");
        var pinned = ReconcileTeamTasks(latest, oversized, retainOlderPages: true,
            selectedTaskId: "older-239");
        Check(pinned.Count == 200 && pinned.Any(row => row.TaskId == "older-239"),
            "The currently inspected older task remains reachable when new rows meet the navigation bound.");

        var heldSuccess = new TaskCompletionSource<CollaborationViewGraph>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialNavigation = teamNavigationVersion;
        var lateSuccess = AwaitTeamGraphForRefreshAsync(() => heldSuccess.Task,
            () => initialNavigation == teamNavigationVersion);
        Check(!lateSuccess.IsCompleted, "A held graph read remains pending until its result arrives.");
        teamNavigationVersion++;
        heldSuccess.SetResult(Graph("task-a"));
        var discardedSuccess = await lateSuccess;
        Check(!discardedSuccess.Current && discardedSuccess.Graph is null && discardedSuccess.Error is null,
            "A late graph success cannot publish after a newer human task selection.");

        var heldFailure = new TaskCompletionSource<CollaborationViewGraph>(TaskCreationOptions.RunContinuationsAsynchronously);
        initialNavigation = teamNavigationVersion;
        var lateFailure = AwaitTeamGraphForRefreshAsync(() => heldFailure.Task,
            () => initialNavigation == teamNavigationVersion);
        teamNavigationVersion++;
        heldFailure.SetException(new InvalidOperationException("Old task graph failed after navigation."));
        var discardedFailure = await lateFailure;
        Check(!discardedFailure.Current && discardedFailure.Graph is null && discardedFailure.Error is null,
            "A late graph failure cannot mark the newer selection unavailable.");
        var currentGraph = await AwaitTeamGraphForRefreshAsync(
            () => Task.FromResult(Graph("task-a")), () => true);
        Check(currentGraph.Current && currentGraph.Graph?.TaskId == "task-a" && currentGraph.Error is null,
            "A current graph result remains available for the owner-bound commit path.");

        var viewed = AdvanceTeamReadPosition(null, "task-a", "main-chat",
            new WorkflowReadViewport("worker-run", "worker-chat", 2, 5, false), 1,
            DateTimeOffset.UnixEpoch);
        Check(viewed.LastReadMessageSequence == 2 && viewed.LatestObservedMessageSequence == 5,
            "Only visible message coordinates advance the local read mark.");
        var history = AdvanceTeamReadPosition(viewed, "task-a", "main-chat",
            new WorkflowReadViewport("worker-run", "worker-chat", 3, 4, true), 1,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        Check(history.LastReadMessageSequence == 3 && history.LatestObservedMessageSequence == 5,
            "An older history page cannot mark a newer unseen message read.");
        teamReadPositions["worker-run"] = history;
        Check(TeamHasUnreadMessage("task-a", "worker-run"),
            "The selected chat retains an unread message after an older-page observation.");
        var caughtUp = AdvanceTeamReadPosition(history, "task-a", "main-chat",
            new WorkflowReadViewport("worker-run", "worker-chat", 5, 5, false), 2,
            DateTimeOffset.UnixEpoch.AddSeconds(2));
        teamReadPositions["worker-run"] = caughtUp;
        Check(!TeamHasUnreadMessage("task-a", "worker-run") &&
              TeamHasUpdatedRun("task-a", "worker-run", 3),
            "Unread messages and a newer run revision have separate indicators.");
        teamReadPositions["worker-run"] = AdvanceTeamReadPosition(caughtUp, "task-a", "main-chat",
            new WorkflowReadViewport("worker-run", "worker-chat", 5, 5, false, 3), 3,
            DateTimeOffset.UnixEpoch.AddSeconds(3));
        Check(!TeamHasUpdatedRun("task-a", "worker-run", 3),
            "Showing a status-only revision clears its Updated indicator.");
        teamSourceConversationId = "another-main";
        Check(!TeamHasUnreadMessage("task-a", "worker-run") &&
              !TeamHasUpdatedRun("task-a", "worker-run", 3),
            "A read mark from another Main conversation cannot be adopted.");
        teamSourceConversationId = "main-chat";

        teamGraph = Graph("task-a"); teamMonitorIssue = null;
        teamPopout = new Window(); teamPopoutRunId = "worker-run";
        teamPopoutHeading = new TextBlock(); teamPopoutState = new TextBlock();
        UpdateTeamPopoutHeader();
        Check(teamPopoutHeading.Text.Contains("Worker", StringComparison.Ordinal) &&
              teamPopoutState.Text.Contains("Running", StringComparison.Ordinal),
            "A pop-out names its own exact worker and current status.");
        teamGraph = Graph("task-a", complete: false); teamMonitorIssue = "fixture-unavailable";
        UpdateTeamPopoutHeader();
        Check(teamPopoutState.Text.Contains("Unavailable", StringComparison.Ordinal),
            "The pop-out reports an incomplete graph without borrowing Main status.");
        teamPopout = null; teamPopoutRunId = null;
        teamPopoutHeading = null; teamPopoutState = null; teamMonitorIssue = null;

        teamInspectionStates["worker-run"] = new(new("task-a", "main-chat"),
            new WorkflowInspectionViewState("worker-run", null, false, [], [], 0, true, [], false));
        ResetTeamForChangedMain();
        Check(teamReadPositions.ContainsKey("worker-run") && teamInspectionStates.ContainsKey("worker-run"),
            "An ordinary Main chat switch retains only local reading and inspection coordinates.");

        checks += await CheckDockAwaitAndReadContractsAsync();
        teamRefreshTimer.Start();
        ConnectionChanged();
        Check(!teamMonitorActive && teamGraph is null && teamTasks.Count == 0 &&
              teamSelectedRunId is null && teamMonitorSurface.Content is null &&
              teamMonitorSurface.Visibility == Visibility.Collapsed && teamSidePane?.Visibility == Visibility.Collapsed &&
              teamPresenterConnection is null && teamPresenterClient is null && !teamRefreshTimer.IsEnabled &&
              teamReadPositions.Count == 0 && teamInspectionStates.Count == 0,
            "Changing the actual service connection detaches the old monitor and clears reader coordinates.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true, teamViewStateChecks = checks,
            evidence = "deterministic unconnected WPF state fixture",
            serviceCommandsExecuted = false, modelUsed = false, windowShown = false
        }));
        return 0;
    }
    private async Task<int> CheckDockAwaitAndReadContractsAsync()
    {
        if (IsConnected || teamPresenters.Count != 0)
            throw new InvalidOperationException("Dock checks require the established offline fixture and no retained presenters.");
        var mainPeer = mainWorkPanel ?? throw new InvalidOperationException("The fixture requires Main.");
        var coordinatorPeer = coordinatorDockPanel ?? throw new InvalidOperationException("The fixture requires Coordinator.");
        var workersPeer = workersDockPanel ?? throw new InvalidOperationException("The fixture requires Workers.");
        var checks = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var connectedField = typeof(ConnectedWindow).GetField("<IsConnected>k__BackingField", flags)!;
        var clientField = typeof(ConnectedWindow).GetField("client", flags)!;
        var oldClient = clientField.GetValue(this); var oldChat = durableConversationId;
        var oldSource = teamSourceConversationId; var oldContent = Content;
        var oldWorkerContent = workersDockPanel!.Content;
        System.Windows.Interop.HwndSource? source = null;
        Task? opening = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = false; var failLoad = false; var heldRun = "coordinator-b";
        CollaborationViewGraph Graph(string task, string suffix) => new(1, task, 1, CollaborationTaskStatus.Running,
            "main-chat", "main", null, "coordinator-" + suffix, "lead", null,
            new("coordinator-" + suffix, "coordinator-" + suffix, "lead", null, null, WorkflowStatus.Running, 1),
            [new("worker-" + suffix, "worker-" + suffix, "helper", null, "coordinator-" + suffix, WorkflowStatus.Running, 1)],
            1, null, true, true, null);
        var graphA = Graph("task-a", "a"); var graphB = Graph("task-b", "b");
        using var handler = new AgentSidebarMetricsHandler((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (request.Operation != "collaboration.view.graph" || request.Arguments.GetProperty("taskId").GetString() != "task-b")
                throw new InvalidOperationException("Unexpected dock fixture owner operation.");
            return Task.FromResult(CommandResult.Ok(graphB));
        });
        using var fixtureClient = new CerebralClient();
        var httpField = typeof(CerebralClient).GetField("http", flags)!;
        ((System.Net.Http.HttpClient)httpField.GetValue(fixtureClient)!).Dispose();
        httpField.SetValue(fixtureClient, new System.Net.Http.HttpClient(handler, disposeHandler: false)
            { BaseAddress = new Uri("http://dock-fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan });
        async Task<CommandResult> Read(CollaborationViewRun coordinate, CommandRequest request, CancellationToken token)
        {
            if (request.Operation == "jobs.workflow.get")
            {
                if (hold && coordinate.RunId == heldRun)
                {
                    entered.TrySetResult(); await release.Task.WaitAsync(token);
                    if (failLoad) throw new InvalidOperationException("Controlled failed task load.");
                }
                var submission = new WorkflowSubmission(coordinate.RunId, coordinate.ConversationId, coordinate.ProfileId,
                    ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion, "Controlled fixture input", null,
                    WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, 64,
                    new(EventActorKind.Service, coordinate.ParentRunId is null ? "cerebral.collaboration" : "cerebral.coordination"))
                    { ParentRunId = coordinate.ParentRunId };
                return CommandResult.Ok(new WorkflowRun { Id = coordinate.RunId, Submission = submission, Revision = 1,
                    Status = WorkflowStatus.Running, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
            }
            if (request.Operation is "jobs.workflow.events.previous" or "jobs.workflow.events")
                return CommandResult.Ok(new { events = Array.Empty<object>(), snapshotSequence = 0L, nextBeforeSequence = (long?)null });
            throw new InvalidOperationException("Unexpected controlled presenter read.");
        }
        WorkflowWorkspace Presenter(CollaborationViewRun coordinate)
        {
            var presenter = new WorkflowWorkspace((request, token) => Read(coordinate, request, token),
                () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
            presenter.UseMonitoringOnly(); return presenter;
        }
        try
        {
            clientField.SetValue(this, fixtureClient); connectedField.SetValue(this, true);
            durableConversationId = teamSourceConversationId = "main-chat";
            workPanelState.Bind(ServiceConnectionIdentity, "main-chat");
            teamTasks = [new("task-a", 1, CollaborationTaskStatus.Running, "coordinator-a", "lead", null,
                "coordinator-a", 1, DateTimeOffset.UnixEpoch),
                new("task-b", 1, CollaborationTaskStatus.Running, "coordinator-b", "lead", null,
                "coordinator-b", 1, DateTimeOffset.UnixEpoch)];
            teamTaskId = "task-a"; teamGraph = graphA; teamMonitorActive = true;
            teamPresenterClient = fixtureClient; teamPresenterConnection = ServiceConnectionIdentity;
            var oldCoordinator = EnsureTeamWorkPanel(graphA.Coordinator!, "task-a");
            var oldWorker = EnsureTeamWorkPanel(graphA.Workers[0], "task-a");
            oldCoordinator.Body.Content = new TextBlock { Text = "Old coordinator A" };
            oldWorker.Body.Content = new TextBlock { Text = "Old worker A" };
            teamSelectedRunId = "worker-a"; teamMonitorSurface = oldWorker.Body; teamMonitorHeader = oldWorker.Header;
            teamMonitorHeading = oldWorker.Heading; teamMonitorState = oldWorker.State; teamPopoutButton = oldWorker.Popout;
            coordinatorDockBody!.Content = oldCoordinator.Body; coordinatorDockBody.Tag = "coordinator-a"; RenderTeamSwitcher();
            var oldCoordinatorHost = coordinatorDockBody;
            var oldWorkerContainer = workersPeer.Content as FrameworkElement ??
                throw new InvalidOperationException("Missing controlled Workers container.");
            var oldWorkerGrid = workerDockGrid ?? throw new InvalidOperationException("Missing controlled Workers grid.");
            var oldWorkerRows = workerDockRows.ToArray();
            var coordinator = Presenter(graphB.Coordinator!); var worker = Presenter(graphB.Workers[0]);
            await coordinator.OpenRunSnapshotAsync("coordinator-b", Lifetime.Token);
            await worker.OpenRunSnapshotAsync("worker-b", Lifetime.Token);
            teamPresenters.Add("coordinator-b", coordinator); teamPresenterScopes.Add("coordinator-b", new("task-b", "main-chat"));
            teamPresenters.Add("worker-b", worker); teamPresenterScopes.Add("worker-b", new("task-b", "main-chat"));
            ShowConversationDock(coordinatorDockPanel!); ShowConversationDock(workersDockPanel!);
            var stagedLayout = (FrameworkElement)Content;
            stagedLayout.Measure(new Size(1280, 840)); stagedLayout.Arrange(new Rect(0, 0, 1280, 840)); stagedLayout.UpdateLayout();
            void VerifyRestoredAPresentations(string phase)
            {
                var task = teamTaskId; var graph = teamGraph; var run = teamSelectedRunId; var active = teamMonitorActive;
                var epoch = conversationDockPresentationEpoch; var navigation = teamNavigationVersion;
                var mainLogical = workPanelState.MainExpanded;
                var runIds = new[] { "coordinator-a", "worker-a", "coordinator-b", "worker-b" };
                var logical = runIds.Select(workPanelState.IsExpanded).ToArray();
                bool SameWorkerRows() => workerDockRows.Count == oldWorkerRows.Length &&
                    workerDockRows.Zip(oldWorkerRows).All(pair => ReferenceEquals(pair.First, pair.Second));
                void LayoutRestored()
                { stagedLayout.Measure(new Size(1280, 840)); stagedLayout.Arrange(new Rect(0, 0, 1280, 840)); stagedLayout.UpdateLayout(); }
                ShowConversationDock(coordinatorPeer); LayoutRestored();
                Check(oldCoordinator.Body.IsDescendantOf(coordinatorPeer) &&
                    ReferenceEquals(teamWorkPanels["coordinator-a"].Body, oldCoordinator.Body) &&
                    ReferenceEquals(coordinatorDockBody, oldCoordinatorHost) &&
                    ReferenceEquals(oldCoordinatorHost.Content, oldCoordinator.Body) && Equals(oldCoordinatorHost.Tag, "coordinator-a") &&
                    oldCoordinator.Body.IsMeasureValid && oldCoordinator.Body.IsArrangeValid &&
                    coordinatorPeer.IsExpanded && !mainPeer.IsExpanded && !workersPeer.IsExpanded &&
                    new[] { mainPeer, coordinatorPeer, workersPeer }.Count(peer => peer.IsExpanded) == 1,
                    phase + ": explicitly selecting and laying out restored A proves the exact Coordinator body is visually mounted in its sole open peer.");
                ShowConversationDock(workersPeer); LayoutRestored();
                Check(ReferenceEquals(workersPeer.Content, oldWorkerContainer) && ReferenceEquals(workerDockGrid, oldWorkerGrid) &&
                    oldWorkerContainer.IsDescendantOf(workersPeer) && oldWorkerGrid.IsDescendantOf(oldWorkerContainer) &&
                    ReferenceEquals(teamWorkPanels["worker-a"].Body, oldWorker.Body) &&
                    SameWorkerRows() && workerDockRows.All(row => row.TaskId == "task-a") &&
                    oldWorkerGrid.IsMeasureValid && oldWorkerGrid.IsArrangeValid &&
                    workersPeer.IsExpanded && !mainPeer.IsExpanded && !coordinatorPeer.IsExpanded &&
                    new[] { mainPeer, coordinatorPeer, workersPeer }.Count(peer => peer.IsExpanded) == 1,
                    phase + ": returning to Workers proves the same retained A grid/container and row objects are visually mounted without inlining its per-run body.");
                Check(teamTaskId == task && ReferenceEquals(teamGraph, graph) && teamSelectedRunId == run && teamMonitorActive == active &&
                    conversationDockPresentationEpoch == epoch && teamNavigationVersion == navigation &&
                    workPanelState.MainExpanded == mainLogical && runIds.Select(workPanelState.IsExpanded).SequenceEqual(logical),
                    phase + ": explicit fixture view inspection preserves accepted task/run/active/logical state and the human presentation epoch.");
            }
            teamMonitorActive = false; teamSelectedRunId = null; workPanelState.SetMainExpanded(true);
            failLoad = true; hold = true; opening = OpenTeamTaskAsync("task-b");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            workersDockPanel!.IsExpanded = true;
            var inactiveCollapseEpoch = conversationDockPresentationEpoch; var inactiveFailure = false;
            release.TrySetResult();
            try { await opening; } catch (InvalidOperationException) { inactiveFailure = true; }
            opening = null; hold = false; failLoad = false;
            Check(inactiveFailure && !teamMonitorActive && teamSelectedRunId is null && teamTaskId == "task-a" &&
                !mainPeer.IsExpanded && workersPeer.IsExpanded && workPanelState.MainExpanded && !workPanelState.IsExpanded("coordinator-b") &&
                conversationDockPresentationEpoch == inactiveCollapseEpoch &&
                ReferenceEquals(coordinatorDockBody, oldCoordinatorHost) &&
                ReferenceEquals(oldCoordinatorHost.Content, oldCoordinator.Body) && Equals(oldCoordinatorHost.Tag, "coordinator-a") &&
                ReferenceEquals(workersPeer.Content, oldWorkerContainer) && ReferenceEquals(workerDockGrid, oldWorkerGrid) &&
                workerDockRows.Count == oldWorkerRows.Length &&
                workerDockRows.Zip(oldWorkerRows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
                workerDockRows.All(row => row.TaskId == "task-a") &&
                ReferenceEquals(teamWorkPanels["coordinator-a"].Body, oldCoordinator.Body) &&
                ReferenceEquals(teamWorkPanels["worker-a"].Body, oldWorker.Body),
                "Failed B loading from an inactive Main preimage restores only logical state and exact A views, preserving human Main collapse. " +
                JsonSerializer.Serialize(new
                {
                    failureCaught = inactiveFailure, activeFalse = !teamMonitorActive, runNull = teamSelectedRunId is null,
                    taskA = teamTaskId == "task-a", mainCollapsed = !mainPeer.IsExpanded, workersExpanded = workersPeer.IsExpanded,
                    logicalMain = workPanelState.MainExpanded, coordinatorBClosed = !workPanelState.IsExpanded("coordinator-b"),
                    epochSame = conversationDockPresentationEpoch == inactiveCollapseEpoch,
                    coordinatorHostSame = ReferenceEquals(coordinatorDockBody, oldCoordinatorHost),
                    coordinatorContentSame = ReferenceEquals(oldCoordinatorHost.Content, oldCoordinator.Body),
                    coordinatorTagSame = Equals(oldCoordinatorHost.Tag, "coordinator-a"),
                    workerContainerSame = ReferenceEquals(workersPeer.Content, oldWorkerContainer),
                    workerGridSame = ReferenceEquals(workerDockGrid, oldWorkerGrid),
                    workerRowCountSame = workerDockRows.Count == oldWorkerRows.Length,
                    workerRowsSame = workerDockRows.Count == oldWorkerRows.Length &&
                        workerDockRows.Zip(oldWorkerRows).All(pair => ReferenceEquals(pair.First, pair.Second)),
                    workerRowsTaskA = workerDockRows.All(row => row.TaskId == "task-a"),
                    coordinatorBodySame = ReferenceEquals(teamWorkPanels["coordinator-a"].Body, oldCoordinator.Body),
                    workerBodySame = ReferenceEquals(teamWorkPanels["worker-a"].Body, oldWorker.Body)
                }));
            VerifyRestoredAPresentations("Inactive rollback");
            changingWorkPanels = true;
            try { mainPeer.IsExpanded = true; }
            finally { changingWorkPanels = false; }
            teamMonitorActive = true; teamSelectedRunId = "worker-a";
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            failLoad = true; hold = true; opening = OpenTeamTaskAsync("task-b");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            workersDockPanel!.IsExpanded = true;
            var rollbackEpoch = conversationDockPresentationEpoch; var failedLoad = false;
            release.TrySetResult();
            try { await opening; } catch (InvalidOperationException) { failedLoad = true; }
            opening = null; hold = false; failLoad = false;
            stagedLayout.UpdateLayout();
            Check(failedLoad && teamTaskId == "task-a" && teamGraph?.TaskId == "task-a" &&
                ReferenceEquals(coordinatorDockBody, oldCoordinatorHost) &&
                ReferenceEquals(oldCoordinatorHost.Content, oldCoordinator.Body) && Equals(oldCoordinatorHost.Tag, "coordinator-a") &&
                ReferenceEquals(workersPeer.Content, oldWorkerContainer) && ReferenceEquals(workerDockGrid, oldWorkerGrid) &&
                workerDockRows.Count == graphA.Workers.Count && workerDockRows.Count == oldWorkerRows.Length &&
                workerDockRows.Zip(oldWorkerRows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
                workerDockRows.All(row => row.TaskId == "task-a") &&
                ReferenceEquals(teamWorkPanels["coordinator-a"].Body, oldCoordinator.Body) &&
                ReferenceEquals(teamWorkPanels["worker-a"].Body, oldWorker.Body) &&
                !coordinatorPeer.IsExpanded && workersPeer.IsExpanded && workPanelState.IsExpanded("worker-a") &&
                conversationDockPresentationEpoch == rollbackEpoch,
                "A failed B load restores the exact A Coordinator host/run tag and Workers container/grid/row identities without reopening the human-collapsed header.");
            VerifyRestoredAPresentations("Active rollback");
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ShowConversationDock(coordinatorPeer);
            hold = true; opening = OpenTeamTaskAsync("task-b");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(teamTaskId == "task-b" && teamGraph?.TaskId == "task-b" && !oldCoordinator.Body.IsDescendantOf(coordinatorDockPanel!) && workerDockRows.Count == graphB.Workers.Count && workerDockRows.All(row => row.TaskId == "task-b"),
                "Task B commit removes both A presentations before its coordinator load settles.");
            workersDockPanel!.IsExpanded = true;
            var collapsedEpoch = conversationDockPresentationEpoch;
            release.TrySetResult(); await opening; opening = null; hold = false;
            Check(!coordinatorDockPanel.IsExpanded && conversationDockPresentationEpoch == collapsedEpoch &&
                teamSelectedRunId == "coordinator-b" && teamPresenters.ContainsKey("coordinator-b") && !mainWorkPanel!.IsExpanded && workersDockPanel!.IsExpanded,
                "The full delayed task/run read retains its result without reopening a header closed during the await.");
            ShowConversationDock(coordinatorDockPanel);
            await OpenTeamRunAsync(graphB.Workers[0]);
            heldRun = "worker-b";
            workspaceRequested = false; compactWorkspaceOpen = false;
            var compactLayout = (FrameworkElement)Content;
            compactLayout.Measure(new Size(720, 540)); compactLayout.Arrange(new Rect(0, 0, 720, 540)); compactLayout.UpdateLayout();
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            hold = true; opening = OpenTeamRunAsync(graphB.Workers[0]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ToggleCompactRail("workspace"); compactLayout.UpdateLayout();
            var workspaceHideEpoch = conversationDockPresentationEpoch;
            release.TrySetResult(); await opening; opening = null; hold = false;
            Check(workspaceRequested && !activityRequested && conversationDockPresentationEpoch == workspaceHideEpoch &&
                !mainWorkPanel!.IsExpanded && workersDockPanel!.IsExpanded && activityColumn!.ActualWidth == 0 && teamPresenters.ContainsKey("worker-b"),
                "Opening compact navigation leaves the selected central Workers peer intact without reserving a plugin-only column.");
            workspaceRequested = false; compactWorkspaceOpen = false; ShowConversationDock(workersDockPanel!);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            hold = true; opening = OpenTeamRunAsync(graphB.Workers[0]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            coordinatorDockPanel!.IsExpanded = true;
            var mainCollapseEpoch = conversationDockPresentationEpoch;
            release.TrySetResult(); await opening; opening = null; hold = false;
            Check(!mainWorkPanel!.IsExpanded && coordinatorDockPanel!.IsExpanded && !workersDockPanel!.IsExpanded &&
                conversationDockPresentationEpoch == mainCollapseEpoch && teamPresenters.ContainsKey("worker-b") && CurrentConversationId == "main-chat",
                "A newer human Coordinator choice survives the full awaited worker inspection/Show completion.");
            changingWorkPanels = true;
            try { mainWorkPanel.IsExpanded = true; }
            finally { changingWorkPanels = false; }
            ShowConversationDock(coordinatorDockPanel); ShowConversationDock(workersDockPanel!);
            var coordinatorPanel = teamWorkPanels["coordinator-b"]; var workerPanel = teamWorkPanels["worker-b"];
            // Controlled physical-host test only: retain the current Workers grid and restore it below.
            var controlledWorker = new DockPanel(); controlledWorker.Children.Add(workerPanel.Body);
            workersDockPanel!.Content = controlledWorker;
            SelectPage("chat");
            var layout = (FrameworkElement)Content; Content = null;
            source = new(new System.Windows.Interop.HwndSourceParameters("Cerebral dock geometry fixture")
                { Width = 1440, Height = 1000, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) })
                { RootVisual = layout };
            layout.Measure(new Size(1440, 1000)); layout.Arrange(new Rect(0, 0, 1440, 1000)); layout.UpdateLayout();
            ShowConversationDock(coordinatorDockPanel!); layout.UpdateLayout();
            Check(TeamPresenterMounted(coordinatorPanel, coordinator) && !TeamPresenterMounted(workerPanel, worker),
                "Only the expanded Coordinator peer admits its exact physically mounted presenter.");
            ShowConversationDock(workersDockPanel!); layout.UpdateLayout();
            Check(!TeamPresenterMounted(coordinatorPanel, coordinator) && TeamPresenterMounted(workerPanel, worker),
                "Selecting Workers replaces the physical read viewport without discarding the retained Coordinator presenter.");
            Check(coordinatorWorkPanel!.ContextMenu is not null && workerDockGrid!.ContextMenu is not null,
                "Current coordinator/worker pop-out controls remain admitted while its independent grid behavior is preserved.");
            teamReadPositions.Clear(); ShowConversationDock(coordinatorDockPanel!); layout.UpdateLayout();
            ObserveTeamReadViewport("task-b", "coordinator-b", coordinator, new("coordinator-b", "coordinator-b", 5, 5, false, 1));
            ShowConversationDock(workersDockPanel!); layout.UpdateLayout();
            ObserveTeamReadViewport("task-b", "worker-b", worker, new("worker-b", "worker-b", 7, 7, false, 1));
            Check(teamReadPositions["coordinator-b"].LastReadMessageSequence == 5 && teamReadPositions["worker-b"].LastReadMessageSequence == 7,
                "Injected owned viewport facts advance each reader only while its actual exclusive peer is selected; this is not active-window visibility evidence.");
            OpenConversationPeer(mainWorkPanel!); layout.UpdateLayout();
            ObserveTeamReadViewport("task-b", "worker-b", worker, new("worker-b", "worker-b", 8, 8, false, 1));
            Check(teamReadPositions["worker-b"].LastReadMessageSequence == 7,
                "A collapsed physical worker host refuses a newer injected read coordinate.");
            ShowConversationDock(coordinatorDockPanel!);
            coordinator.Visibility = Visibility.Collapsed; layout.UpdateLayout();
            ObserveTeamReadViewport("task-b", "coordinator-b", coordinator, new("coordinator-b", "coordinator-b", 9, 9, false, 1));
            Check(teamReadPositions["coordinator-b"].LastReadMessageSequence == 5,
                "A hidden presenter cannot advance its injected read coordinate.");
            coordinator.Visibility = Visibility.Visible;
            var viewportField = typeof(WorkflowWorkspace).GetField("lastReadViewport", flags) ??
                throw new InvalidOperationException("Missing qualified viewport cache.");
            var heldViewport = new WorkflowReadViewport("worker-b", "worker-b", null, null, false, 1);
            OpenConversationPeer(mainWorkPanel!); layout.UpdateLayout();
            viewportField.SetValue(worker, heldViewport);
            ShowConversationDock(workersDockPanel!); layout.UpdateLayout();
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            Check(viewportField.GetValue(worker) is null && workersDockPanel!.IsExpanded &&
                teamReadPositions["worker-b"].LastReadMessageSequence == 7 &&
                new[] { mainWorkPanel!, coordinatorDockPanel!, workersDockPanel! }.Count(panel => panel.IsExpanded) == 1,
                "A same-size real peer reopen executes post-layout read-visibility notification without fabricating an active-window read.");
            source.RootVisual = null; source.Dispose(); source = null; Content = oldContent;
            return checks;
        }
        finally
        {
            release.TrySetResult();
            if (opening is { IsCompleted: false }) await opening.WaitAsync(TimeSpan.FromSeconds(5));
            if (source is not null) { source.RootVisual = null; source.Dispose(); }
            if (Content is null) Content = oldContent;
            workersDockPanel!.Content = oldWorkerContent;
            foreach (var presenter in teamPresenters.Values.ToArray()) presenter.Dispose();
            teamPresenters.Clear(); teamPresenterScopes.Clear(); teamPresenterClient = null; teamPresenterConnection = null;
            connectedField.SetValue(this, false); clientField.SetValue(this, oldClient);
            durableConversationId = oldChat; teamSourceConversationId = oldSource;
        }
    }

}
