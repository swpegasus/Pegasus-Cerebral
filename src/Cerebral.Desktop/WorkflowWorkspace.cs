using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Text-free native transcript diagnostics. Event IDs are bounded independently of the card count.</summary>
public sealed record WorkflowTranscriptDisplaySnapshot(int Count, IReadOnlyList<string> EventIds, bool Truncated);

/// <summary>A bounded view over service-owned workflows. It never runs an agent or owns execution.</summary>
public sealed partial class WorkflowWorkspace : Grid, IDisposable
{
    public const int MaximumTranscriptDiagnosticIds = 40;
    internal const int MaximumRetainedTranscriptEvents = 40;
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> command;
    private readonly Func<IReadOnlyList<AgentProfile>> profiles;
    private readonly Action<string, bool> report;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private const string IdleState = "Open saved work or start a workflow provided by an installed module.";
    private readonly ListBox recent = new WorkflowRecentListBox()
    {
        DisplayMemberPath = nameof(WorkflowRunListItem.Label),
        BorderThickness = new Thickness(0),
        Background = Ui.Surface,
        Foreground = Ui.Ink
    };
    private readonly StackPanel transcript = new();
    private readonly StackPanel fields = new();
    private readonly TextBlock state = Ui.Text(IdleState, 12, Ui.Muted);
    private readonly TextBlock heading = Ui.Text("Work", 18);
    private readonly Button stop;
    private readonly Button resume;
    private readonly Button reconcile;
    private readonly Button answer;
    private readonly Button loadEarlier;
    private readonly Button moreRuns;
    private readonly Button newWorkflow;
    private readonly Button retrySubmission;
    private readonly Button refresh;
    private readonly WrapPanel toolbar;
    private readonly StackPanel summaryHost;
    private readonly StackPanel questionForm;
    private Border? hostAttentionView;
    private Func<bool>? hostAttentionCurrent;
    private readonly FrameworkElement guidanceHost;
    private readonly Border answerHost;
    private readonly StackPanel conversationFlow = new();
    private readonly ScrollViewer transcriptScroller;
    private readonly List<CerebralEvent> displayedEvents = [];
    private readonly List<CerebralEvent> recentWindowEvents = [];
    private readonly List<CerebralEvent> retainedTranscriptEvents = [];
    private readonly List<string> renderedTranscriptEventIds = [];
    private int renderedTranscriptCount;
    private readonly Dictionary<string, Func<string>> answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> answerDrafts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer activityClockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WorkflowConversationClient? conversation;
    private WorkflowAnswerRequest? pendingAnswer;
    private WorkflowConversationSubmission? pendingSubmission;
    private bool readingHistory;
    private bool readingOlderRuns;
    private string? renderedVersion;
    private string? questionKey;
    private string? pendingQuestionFocus;
    private long generation;
    private long connectionEpoch;
    private long localFailureRevision;
    private long transcriptRenderRevision;
    private long transcriptInputRevision;
    private bool transcriptRestoring;
    private bool transcriptApplyingRestore;
    private double transcriptRestoreOffset;
    private bool transcriptRestoreFollowEnd;
    private Action? pendingScrollRestore;
    private bool selecting;
    private bool disposed;
    private bool mutationPending;
    private bool releasingHistory;
    private bool historyReleaseArmed;
    private double? historyReleaseOffset;
    private WorkflowRecentPage? recentPage;
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> collapsedActivity = new(StringComparer.Ordinal);
    private readonly List<ActivityView> activityViews = [];
    private readonly bool embedded;
    private bool monitoringOnly;
    public WorkflowConversationSnapshot? Snapshot => conversation?.Snapshot;
    public WorkflowDisplayedFinalResponse? DisplayedFinalResponse { get; private set; }
    /// <summary>Cards committed to the native visual tree, independently of the current service event page.</summary>
    public WorkflowTranscriptDisplaySnapshot DisplayedTranscript => new(renderedTranscriptCount,
        Array.AsReadOnly(renderedTranscriptEventIds.ToArray()), renderedTranscriptCount > renderedTranscriptEventIds.Count);
    internal IReadOnlyList<string> DisplayedEventIds => displayedEvents.Select(item => item.Id).ToArray();
    internal int RecentWindowEventCount => recentWindowEvents.Count;
    internal int RetainedTranscriptEventCount => retainedTranscriptEvents.Count;
    /// <summary>Unsettled Work mutations also fence host connection changes before its transport is replaced.</summary>
    public bool MutationPending => mutationPending || WorkGuidanceUncertain;
    public event EventHandler? SnapshotChanged;
    public event Action<ActivityTurnTarget>? ActivityNavigationRequested;
    public Action? QuestionsResetting { get; set; }
    public Func<TextBox, string, FrameworkElement?>? QuestionTextEntryActions { get; set; }

    public WorkflowWorkspace(Func<CommandRequest, CancellationToken, Task<CommandResult>> command,
        Func<IReadOnlyList<AgentProfile>> profiles, Action<string, bool> report, bool embedded = false)
    {
        this.command = command; this.profiles = profiles; this.report = report; this.embedded = embedded;
        Margin = embedded ? new Thickness(0) : new Thickness(12);
        // Embedded Chat shares its host's decorative base; standalone Work owns its surface.
        Background = embedded ? Brushes.Transparent : Ui.Surface;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        newWorkflow = Ui.Button("New workflow", () => Run(StartAsync), true);
        AutomationProperties.SetAutomationId(newWorkflow, "WorkflowNew");
        retrySubmission = Ui.Button("Retry request", () => RunAttentionAction(RetrySubmissionAsync));
        refresh = Ui.Button("Refresh", () => RunAttentionAction(async () =>
        { readingHistory = readingOlderRuns = false; await RefreshFromConnectionAsync(); }));
        AutomationProperties.SetAutomationId(refresh, "WorkflowRefresh");
        AutomationProperties.SetAutomationId(retrySubmission, "WorkflowRetrySubmission");
        toolbar = Ui.Bar(newWorkflow, refresh, retrySubmission);
        moreRuns = Ui.Button("Older work", () => Run(() => RefreshRecentAsync(true)));
        toolbar.Children.Add(moreRuns);
        stop = Ui.Button("Stop", () => RunAttentionAction(() => CancelConversationAsync(lifetime.Token)));
        resume = Ui.Button("Resume", () => RunAttentionAction(() => ChangeAsync(x => x.ResumeAsync(x.Snapshot.Run!.Revision, lifetime.Token), mutation: true)));
        reconcile = Ui.Button("Check outcome", () => Run(() => ChangeAsync(x => x.ReconcileAsync(lifetime.Token), mutation: true)));
        toolbar.Children.Add(stop); toolbar.Children.Add(resume); toolbar.Children.Add(reconcile);
        AutomationProperties.SetAutomationId(stop, "WorkflowStop");
        AutomationProperties.SetAutomationId(resume, "WorkflowResume");
        AutomationProperties.SetAutomationId(reconcile, "WorkflowCheckOutcome");
        Children.Add(toolbar);
        if (embedded) { toolbar.Children.RemoveAt(0); moreRuns.Visibility = Visibility.Collapsed; stop.Visibility = Visibility.Collapsed; }
        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(210), MinWidth = 140 });
        body.ColumnDefinitions.Add(new());
        recent.Margin = new Thickness(0, 0, 12, 0);
        VirtualizingPanel.SetIsVirtualizing(recent, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(recent, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(recent, "WorkflowRecent");
        AutomationProperties.SetName(recent, "Saved workflow runs");
        AutomationProperties.SetAutomationId(moreRuns, "WorkflowRecentOlder");
        AutomationProperties.SetAutomationId(heading, "WorkflowHeading");
        AutomationProperties.SetAutomationId(state, "WorkflowState");
        body.Children.Add(recent);
        if (embedded) { recent.Visibility = Visibility.Collapsed; body.ColumnDefinitions[0].MinWidth = 0; body.ColumnDefinitions[0].Width = new GridLength(0); }
        var center = new Grid(); center.RowDefinitions.Add(new() { Height = GridLength.Auto });
        center.RowDefinitions.Add(new());
        summaryHost = new StackPanel(); summaryHost.Children.Add(heading); summaryHost.Children.Add(state);
        if (embedded) { heading.Visibility = Visibility.Collapsed; state.Visibility = Visibility.Collapsed; }
        loadEarlier = Ui.Button("Load earlier messages", () => Run(LoadEarlierMessagesAsync));
        loadEarlier.HorizontalAlignment = HorizontalAlignment.Center;
        loadEarlier.Margin = new Thickness(0, 5, 0, 8);
        AutomationProperties.SetAutomationId(loadEarlier, "WorkflowLoadEarlier");
        center.Children.Add(summaryHost);
        conversationFlow.Margin = new Thickness(18, 10, 18, 18);
        conversationFlow.Children.Add(transcript);
        _ = CreateGenerationPreview();
        transcriptScroller = new ScrollViewer { Content = conversationFlow, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        ConfigureTranscriptScrollTracking();
        ConfigureInspectionTextTracking();
        ConfigureReadViewportWindow();
        transcriptScroller.SizeChanged += (_, _) =>
        { QueuePendingScrollRestoration(); ObserveReadViewport(); };
        transcriptScroller.ScrollChanged += (_, _) => ObserveReadViewport();
        transcriptScroller.ScrollChanged += (_, change) =>
        {
            if (!readingHistory || !historyReleaseArmed || releasingHistory || change.VerticalChange <= 0 || historyReleaseOffset is not { } boundary ||
                change.VerticalOffset + 1 < boundary) return;
            releasingHistory = true;
            Run(ReleaseEarlierAsync);
        };
        AutomationProperties.SetAutomationId(transcriptScroller, "WorkflowTranscript");
        Grid.SetRow(transcriptScroller, 1); center.Children.Add(transcriptScroller);
        questionForm = new StackPanel();
        answer = Ui.Button("Submit answers", () => Run(AnswerAsync), true);
        AutomationProperties.SetAutomationId(answer, "WorkflowSubmitAnswers");
        answer.HorizontalAlignment = HorizontalAlignment.Left; answer.Margin = new Thickness(0, 12, 0, 12);
        questionForm.Children.Add(fields); questionForm.Children.Add(answer);
        // Questions belong to the same page flow as the conversation. A fixed inner viewport
        // clips labels and competes with the page scroll, especially for longer question sets.
        answerHost = new Border { BorderBrush = Ui.Accent, BorderThickness = new Thickness(0, 2, 0, 0), Padding = new Thickness(0, 16, 0, 0),
            Margin = new Thickness(0, 12, 0, 0), Child = questionForm, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(answerHost, "WorkflowQuestions");
        conversationFlow.Children.Add(answerHost);
        center.RowDefinitions.Add(new() { Height = GridLength.Auto });
        guidanceHost = CreateWorkGuidance(); Grid.SetRow(guidanceHost, 2); center.Children.Add(guidanceHost);
        Grid.SetColumn(center, 1); body.Children.Add(center); Grid.SetRow(body, 1); Children.Add(body);
        recent.SelectionChanged += (_, _) => { if (!selecting && recent.SelectedItem is WorkflowRunListItem item) Run(() => OpenAsync(item.Run)); };
        Loaded += (_, _) => { if (!disposed) { refreshTimer.Start(); activityClockTimer.Start(); Run(RefreshFromConnectionAsync); ObserveReadViewport(); } };
        Unloaded += (_, _) => { refreshTimer.Stop(); activityClockTimer.Stop(); ClearGenerationPreview(); ClearAgentUpdateCards(); };
        refreshTimer.Tick += (_, _) =>
        {
            if (conversation?.Snapshot.Run is { } run &&
                (!run.IsTerminal || embedded && run.DefinitionId == ConversationControlClient.ChatDefinitionId))
                Run(RefreshFromConnectionAsync);
        };
        activityClockTimer.Tick += (_, _) =>
        {
            if (!disposed && conversation?.Snapshot.Run is { IsTerminal: false } run && activityViews.Count > 0)
                RefreshActivityHeaders(run);
        };
        SizeChanged += (_, _) => { if (!embedded) body.ColumnDefinitions[0].Width = new GridLength(ActualWidth < 720 ? 145 : 210); };
        UpdateControls();
    }

    /// <summary>Opt an embedded host into one cached, workspace-owned attention view.
    /// Buttons retain their original admission and retained-request callbacks; the host can only narrow access.</summary>
    public FrameworkElement CreateHostAttentionView(Func<bool> isCurrent)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (!embedded || monitoringOnly) throw new InvalidOperationException("Only an interactive embedded conversation can host attention actions.");
        if (hostAttentionView is not null) return hostAttentionView;
        hostAttentionCurrent = isCurrent;
        foreach (var button in new[] { refresh, retrySubmission, stop, answer })
        {
            button.MinHeight = 24; button.Height = double.NaN;
            button.Padding = new Thickness(8, 1, 8, 1); button.Margin = new Thickness(0, 0, 6, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
        }
        Children.Remove(toolbar);
        toolbar.Children.Remove(refresh); toolbar.Children.Remove(retrySubmission); toolbar.Children.Remove(resume);
        toolbar.Children.Remove(stop);
        summaryHost.Children.Remove(state);
        state.Visibility = Visibility.Visible; state.TextWrapping = TextWrapping.Wrap;
        state.TextTrimming = TextTrimming.None; state.Margin = new Thickness(0, 0, 0, 4);
        var actions = new WrapPanel();
        actions.Children.Add(refresh); actions.Children.Add(retrySubmission);
        var attentionText = new ScrollViewer { Content = state, MaxHeight = 72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(attentionText, "WorkflowAttentionText");
        AutomationProperties.SetName(attentionText, "Complete current conversation diagnostic");
        var content = new StackPanel(); content.Children.Add(attentionText); content.Children.Add(actions);
        hostAttentionView = new Border { Child = content, Background = Ui.ErrorBackground,
            BorderBrush = Ui.ErrorBorder, BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6),
            Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(hostAttentionView, "WorkflowHostAttention");
        AutomationProperties.SetName(hostAttentionView, "Current conversation attention and supported actions");
        // Questions replace Main's outer composer; their existing Stop must travel with the form.
        questionForm.Children.Insert(0, stop);
        UpdateControls();
        return hostAttentionView;
    }

    private void RunAttentionAction(Func<Task> action)
    {
        if (hostAttentionView is null) { Run(action); return; }
        var expected = Snapshot; var expectedGeneration = generation; var expectedConnection = connectionEpoch;
        if (hostAttentionCurrent?.Invoke() != true) return;
        Run(async () =>
        {
            // The gate can wait across a selection or connection change. Never retarget an old click.
            if (expectedGeneration != generation || expectedConnection != connectionEpoch ||
                !ReferenceEquals(expected, Snapshot) || hostAttentionCurrent?.Invoke() != true) return;
            await action();
        });
    }

    private void UpdateHostAttention()
    {
        if (hostAttentionView is null) return;
        var snapshot = Snapshot; var run = snapshot?.Run;
        var unresolved = run?.EffectState is WorkflowEffectState.Unknown or WorkflowEffectState.Dispatched;
        var attention = snapshot?.MutationUncertain == true || run?.Error is not null ||
            run?.Status == WorkflowStatus.Failed || run is { Status: WorkflowStatus.Waiting, Questions: null } ||
            Equals(state.Foreground, Ui.ErrorText);
        hostAttentionView.Visibility = attention ? Visibility.Visible : Visibility.Collapsed;
        hostAttentionView.IsEnabled = !disposed && hostAttentionCurrent?.Invoke() == true;
        refresh.IsEnabled = !disposed && !mutationPending && !WorkGuidanceUncertain;
        retrySubmission.Visibility = snapshot is { MutationUncertain: true, Run: null } &&
            pendingSubmission is { } pending && snapshot.RunId == pending.RequestId &&
            snapshot.LastMutationRequestId == pending.RequestId ? Visibility.Visible : Visibility.Collapsed;
        retrySubmission.IsEnabled = !mutationPending && !WorkGuidanceUncertain && !unresolved;
        // This projection does not expose outstanding child-run dependencies. Waiting alone cannot prove Resume is applicable.
        resume.Visibility = Visibility.Collapsed;
        resume.IsEnabled = false;
        // Unknown is not a resolver capability. Main offers read-only Refresh, never speculative reconciliation.
        reconcile.Visibility = Visibility.Collapsed;
    }

    public void Notify(ServiceEvent item)
    {
        NotifyGenerationPreview(item);
        NotifyGenerationMetrics(item);
        if (item.Type == "workflow.updated" && IsLoaded && (!embedded || conversation?.IsRelevant(item) == true)) Run(RefreshFromConnectionAsync);
    }

    /// <summary>Use this presenter for an exact host-admitted monitoring run. The caller must also bind its command to that run.</summary>
    public void UseMonitoringOnly()
    {
        Dispatcher.VerifyAccess();
        if (conversation is not null || mutationPending)
            throw new InvalidOperationException("Set monitoring mode before opening a run.");
        monitoringOnly = true;
        retrySubmission.Visibility = Visibility.Collapsed;
        guidanceHost.Visibility = Visibility.Collapsed;
        UpdateControls();
    }

    public void Reset()
    {
        RequireSettledWorkGuidance();
        if (mutationPending) throw new InvalidOperationException("Wait for the pending request before resetting its conversation.");
        if (conversation?.Snapshot.MutationUncertain == true)
            throw new InvalidOperationException("Refresh or retry the unconfirmed Work request before changing its connection.");
        generation++; connectionEpoch++; localFailureRevision++; transcriptRenderRevision++; transcriptInputRevision++; workSelectionPending = false;
        ClearImageCards(); ClearAgentUpdateCards(); ResetGenerationPreviewFeed();
        transcriptRestoring = false; transcriptRestoreOffset = 0; transcriptRestoreFollowEnd = true; pendingScrollRestore = null;
        conversation = null; questionKey = null; pendingQuestionFocus = null; pendingAnswer = null; pendingSubmission = null;
        readingHistory = readingOlderRuns = releasingHistory = historyReleaseArmed = false; historyReleaseOffset = null; renderedVersion = null;
        answers.Clear(); answerDrafts.Clear(); expanded.Clear(); collapsedActivity.Clear(); activityViews.Clear(); displayedEvents.Clear(); recentWindowEvents.Clear(); transcript.Children.Clear(); fields.Children.Clear();
        renderedTranscriptEventIds.Clear(); renderedTranscriptCount = 0; retainedTranscriptEvents.Clear();
        DisplayedFinalResponse = null;
        renderedMessageCards.Clear(); renderedReadCards.Clear(); lastReadViewport = null;
        inspectionTextSelection = null;
        selecting = true; recent.ItemsSource = null; selecting = false; recentPage = null;
        QuestionsResetting?.Invoke();
        heading.Text = "Work"; state.Text = "Reconnect to read saved work."; state.Foreground = Ui.Muted;
        AutomationProperties.SetAutomationId(heading, "WorkflowHeading");
        AutomationProperties.SetHelpText(heading, "");
        UpdateControls(); SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BeginConversation(string conversationId)
    {
        if (mutationPending) throw new InvalidOperationException("Wait for this request's acceptance before opening another chat.");
        if (conversation?.Snapshot.MutationUncertain == true)
            throw new InvalidOperationException("Refresh or retry the uncertain request before opening another chat.");
        Reset();
        conversation = new(conversationId, BoundCommand(), WorkflowConversationClient.MaximumPageEvents);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns the captured profile for an accepted or uncertain request; a later UI selection cannot rebind it.</summary>
    public string? ProfileForRequest(string requestId) =>
        pendingSubmission?.RequestId == requestId ? pendingSubmission.ProfileId :
        Snapshot?.Run is { } run && run.Id == requestId ? run.ProfileId : null;

    /// <summary>
    /// Start explicit plugin work in a separate conversation. RequestId is also its conversation ID, so retries
    /// cannot allocate another history. The existing adapter owns the exact pending payload and uncertainty.
    /// </summary>
    public Task<WorkflowConversationSnapshot> SubmitNewWorkflowAsync(WorkflowConversationSubmission submission, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (embedded) throw new InvalidOperationException("Explicit plugin work belongs in the Work view.");
        if (mutationPending) throw new InvalidOperationException("Wait for the pending Work request before submitting another.");
        if (conversation?.Snapshot is { MutationUncertain: true } current && current.RunId != submission.RequestId)
            throw new InvalidOperationException("Refresh or retry the unconfirmed Work request before starting different work.");
        submission = WorkflowConversationClient.FreezeSubmission(submission);
        if (conversation?.Snapshot.RunId != submission.RequestId) BeginConversation(submission.RequestId);
        return SubmitConversationAsync(submission, token);
    }

    public async Task<WorkflowConversationSnapshot> SubmitConversationAsync(WorkflowConversationSubmission submission, CancellationToken token = default)
    {
        var current = conversation ?? throw new InvalidOperationException("Open a conversation before submitting.");
        var expected = generation;
        if (current.Snapshot.MutationUncertain && pendingSubmission?.RequestId != submission.RequestId)
            throw new InvalidOperationException("Refresh or retry the retained request before submitting different work.");
        if (mutationPending) throw new InvalidOperationException("Wait for the pending request before submitting another.");
        submission = WorkflowConversationClient.FreezeSubmission(submission);
        if (!current.Snapshot.MutationUncertain)
        {
            if (current.Snapshot.RunId != submission.RequestId) ClearGenerationPreview();
            pendingSubmission = submission;
        }
        try { return await MutateAsync(() => current.SubmitAsync(submission, token)); }
        finally { if (expected == generation && current == conversation) { Render(); SnapshotChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public async Task CancelConversationAsync(CancellationToken token = default)
    {
        var current = conversation ?? throw new InvalidOperationException("Open a conversation before stopping.");
        var expected = generation;
        try { await MutateAsync(() => current.CancelAsync(token)); }
        finally { if (expected == generation && current == conversation) { Render(); SnapshotChanged?.Invoke(this, EventArgs.Empty); } }
    }

    /// <summary>
    /// Inspect the current uncertain effect once through its registered reconciliation handler. This
    /// never replays the effect and keeps the selected conversation and request identity unchanged.
    /// </summary>
    public async Task<WorkflowConversationSnapshot> ReconcileConversationAsync(CancellationToken token = default)
    {
        var current = conversation ?? throw new InvalidOperationException("Open a conversation before checking its outcome.");
        var expected = generation;
        try { return await MutateAsync(() => current.ReconcileAsync(token)); }
        finally { if (expected == generation && current == conversation) { Render(); SnapshotChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public async Task RefreshAsync()
    {
        var expected = generation;
        if (conversation is { Snapshot.RunId: not null } current)
        {
            if (embedded && !monitoringOnly && current.Snapshot.Run?.DefinitionId == ConversationControlClient.ChatDefinitionId)
            {
                bool CanFollowOnUi() => !disposed && expected == generation && current == conversation &&
                    !readingHistory && !readingOlderRuns && !mutationPending && !current.Snapshot.MutationUncertain;
                bool CanFollow() => Dispatcher.CheckAccess() ? CanFollowOnUi() : Dispatcher.Invoke(CanFollowOnUi);
                await current.RefreshConversationAsync(CanFollow, latestEvents: !readingHistory, token: lifetime.Token);
            }
            else await current.RefreshAsync(latestEvents: !readingHistory, token: lifetime.Token);
            if (expected == generation && current == conversation) Render();
        }
        if (expected == generation && !readingOlderRuns && !embedded) await RefreshRecentAsync(false);
    }

    private async Task RefreshRecentAsync(bool older)
    {
        var expected = generation;
        var expectedFailure = localFailureRevision;
        var arguments = new Dictionary<string, object> { ["limit"] = 40 };
        if (older && recentPage?.NextBeforeRow is { } before)
        { arguments["beforeRow"] = before; arguments["snapshotRow"] = recentPage.SnapshotRow; }
        CommandResult result;
        try { result = await command(new("jobs.workflow.recent", CerebralJson.Element(arguments)), lifetime.Token); }
        catch (Exception) when (disposed || expected != generation) { return; }
        // A previous owner's failed reply is just as stale as its successful reply.
        if (disposed || expected != generation) return;
        if (!result.Success) throw new InvalidOperationException(result.Error);
        var page = CerebralJson.Read<WorkflowRecentPage>(result.Data);
        recentPage = page; readingOlderRuns = older; selecting = true;
        var names = profiles().Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
            .GroupBy(profile => profile.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        var items = page.Runs.Select(run => new WorkflowRunListItem(run,
            run.Title + "\n" + names.GetValueOrDefault(run.ProfileId, run.ProfileId) + " · " + run.Status)).ToArray();
        try { recent.ItemsSource = items; recent.SelectedItem = items.FirstOrDefault(x => x.Run.Id == conversation?.Snapshot.Run?.Id); }
        finally { selecting = false; }
        moreRuns.IsEnabled = page.NextBeforeRow is not null;
        // An idle read can recover an earlier local connection failure. A selected run,
        // pending request or newer failure still owns its status; a list result cannot clear it.
        if (conversation is null && !mutationPending && pendingSubmission is null && expectedFailure == localFailureRevision)
        {
            state.Text = IdleState;
            state.Foreground = Ui.Muted;
        }
    }

    internal Func<CommandRequest, CancellationToken, Task<CommandResult>> BoundCommand()
    {
        Dispatcher.VerifyAccess();
        var expected = connectionEpoch;
        return (request, token) =>
        {
            Task<CommandResult> Dispatch()
            {
                // Connection reset and client lookup are one UI-dispatcher operation. The invoked
                // client then owns this request, even if the window replaces its client during await.
                if (disposed || expected != connectionEpoch)
                    throw new InvalidOperationException("The workflow connection changed. Reopen work from its original service.");
                return command(request, token);
            }
            return Dispatcher.CheckAccess() ? Dispatch() :
                Dispatcher.InvokeAsync(Dispatch, DispatcherPriority.Normal, token).Task.Unwrap();
        };
    }

    public async Task RefreshFromConnectionAsync()
    {
        var expected = generation;
        ClearGenerationPreview();
        try { await RefreshAsync(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (disposed || expected != generation) { }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private void ReportFailure(Exception error)
    {
        if (disposed) return;
        localFailureRevision++;
        if (conversation is not null) Render();
        state.Text = error.Message; state.Foreground = Ui.ErrorText; UpdateHostAttention(); report(error.Message, true);
    }

    private async Task OpenAsync(WorkflowRunSummary item)
    {
        RequireSettledWorkGuidance();
        if (mutationPending) throw new InvalidOperationException("Wait for this request's acceptance before selecting different work.");
        if (conversation?.Snapshot.MutationUncertain == true) throw new InvalidOperationException("Refresh the uncertain request before selecting different work.");
        var expected = ++generation; releasingHistory = false; ClearGenerationPreview(); workSelectionPending = true; UpdateControls();
        try { _ = await OpenKnownRunAsync(item, expected, lifetime.Token); }
        catch (InvalidOperationException) when (expected != generation)
        {
            // A newer native selection owns its display. Semantic callers use the strict API below.
        }
        finally { if (expected == generation) { workSelectionPending = false; UpdateControls(); } }
    }

    private async Task<WorkflowConversationSnapshot> OpenKnownRunAsync(WorkflowRunSummary item, long expected, CancellationToken token)
    {
        var selected = new WorkflowConversationClient(item.ConversationId, BoundCommand(), WorkflowConversationClient.MaximumPageEvents);
        await selected.OpenAsync(item.Id, token);
        await selected.ExpandRecentTranscriptAsync(token: token);
        if (expected != generation || mutationPending || conversation?.Snapshot.MutationUncertain == true)
            throw new InvalidOperationException("The selected work changed while opening this run. Inspect the current selection.");
        if (conversation?.Snapshot.RunId != item.Id)
        {
            transcriptRenderRevision++; transcriptInputRevision++;
            transcriptRestoring = true; transcriptRestoreOffset = 0; transcriptRestoreFollowEnd = true;
            inspectionTextSelection = null;
        }
        if (conversation?.Snapshot.ConversationId != selected.Snapshot.ConversationId ||
            conversation?.Snapshot.Run?.ProfileId != selected.Snapshot.Run?.ProfileId)
            ClearAgentUpdateCards();
        conversation = selected; questionKey = null; pendingAnswer = null; pendingSubmission = null; answerDrafts.Clear(); expanded.Clear();
        readingHistory = releasingHistory = historyReleaseArmed = false; historyReleaseOffset = null;
        recentWindowEvents.Clear(); retainedTranscriptEvents.Clear(); renderedVersion = null;
        Render();
        return selected.Snapshot;
    }

    public async Task OpenRunAsync(string runId) => _ = await OpenRunSnapshotAsync(runId);

    /// <summary>Open an exact run and return only the snapshot adopted by this selection generation.</summary>
    public async Task<WorkflowConversationSnapshot> OpenRunSnapshotAsync(string runId, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireSettledWorkGuidance();
        if (mutationPending) throw new InvalidOperationException("Wait for this request's acceptance before selecting different work.");
        if (conversation?.Snapshot.MutationUncertain == true) throw new InvalidOperationException("Refresh the uncertain request before selecting different work.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        // Reserve selection before the first read, so a slow lookup cannot replace newer work.
        var expected = ++generation; releasingHistory = false; ClearGenerationPreview(); workSelectionPending = true; UpdateControls();
        try
        {
            var response = await BoundCommand()(new("jobs.workflow.get", CerebralJson.Element(new { id = runId })), linked.Token);
            if (!response.Success) throw new InvalidOperationException(response.Error);
            var run = CerebralJson.Read<WorkflowRun>(response.Data);
            if (expected != generation) throw new InvalidOperationException("The selected work changed while reading this run. Inspect the current selection.");
            if (run.Id != runId) throw new InvalidDataException("The service returned another workflow run.");
            return await OpenKnownRunAsync(new(run.Id, run.Submission.ConversationId, run.Submission.ProfileId, run.Submission.UserText,
                run.Submission.DefinitionId, run.Status, run.Revision, run.UpdatedAt), expected, linked.Token);
        }
        finally { if (expected == generation) { workSelectionPending = false; UpdateControls(); } }
    }

    /// <summary>Refresh a previously selected exact run without rebinding a caller to another selection.</summary>
    public async Task<WorkflowConversationSnapshot> InspectRunAsync(string runId, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = conversation;
        if (current?.Snapshot.Run?.Id != runId) throw new InvalidOperationException("Open this exact workflow run before inspecting it.");
        var expected = generation;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await current.RefreshAsync(latestEvents: !readingHistory, token: linked.Token);
        if (expected != generation || current != conversation) throw new InvalidOperationException("The selected work changed during inspection. Reopen the intended run.");
        Render();
        return current.Snapshot;
    }

    /// <summary>Resume the exact selected revision through the same mutation gate as the visible Resume control.</summary>
    public async Task<WorkflowConversationSnapshot> ResumeRunAsync(string runId, long expectedRevision,
        CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = conversation;
        if (current?.Snapshot.Run?.Id != runId)
            throw new InvalidOperationException("Open this exact workflow run before resuming it.");
        await ChangeAsync(x => x.ResumeAsync(expectedRevision, token), mutation: true);
        return current.Snapshot;
    }

    /// <summary>Inspect the exact selected uncertain effect through the visible Check outcome path.</summary>
    public async Task<WorkflowConversationSnapshot> ReconcileRunAsync(string runId,
        CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = conversation;
        if (current?.Snapshot.Run?.Id != runId)
            throw new InvalidOperationException("Open this exact workflow run before reconciling it.");
        await ChangeAsync(x => x.ReconcileAsync(token), mutation: true);
        return current.Snapshot;
    }

    /// <summary>Answers the currently rendered durable question set through the same mutation gate as the native form.</summary>
    public async Task<WorkflowConversationSnapshot> AnswerConversationAsync(string requestId,
        IReadOnlyDictionary<string, string> suppliedAnswers, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 || requestId.Any(char.IsWhiteSpace))
            throw new ArgumentException("A bounded request ID without whitespace is required.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(suppliedAnswers);
        if (suppliedAnswers.Count > 32) throw new ArgumentException("Supply at most 32 answers.", nameof(suppliedAnswers));
        var current = conversation ?? throw new InvalidOperationException("Open a durable conversation first.");
        var run = current.Snapshot.Run ?? throw new InvalidOperationException("Refresh the current run first.");
        var questions = run.Questions ?? throw new InvalidOperationException("The current run has no unanswered questions.");
        var fields = questions.Fields.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, value) in suppliedAnswers)
        {
            if (!fields.ContainsKey(id)) throw new ArgumentException($"Question field '{id}' is not in the current question set.", nameof(suppliedAnswers));
            if (value is null || value.Length > 8192) throw new ArgumentException($"Answer '{id}' exceeds its supported bound.", nameof(suppliedAnswers));
            if (!string.IsNullOrWhiteSpace(value)) answers.Add(id, value);
        }
        foreach (var field in questions.Fields.Where(x => x.Required && !questions.Answers.ContainsKey(x.Id)))
            if (!answers.ContainsKey(field.Id)) throw new ArgumentException($"Answer required question '{field.Id}'.", nameof(suppliedAnswers));
        var request = new WorkflowAnswerRequest(requestId, run.Id, questions.Id, questions.Revision, answers);
        await ChangeAsync(x => x.AnswerAsync(request, token), mutation: true);
        return current.Snapshot;
    }

    /// <summary>Semantic callers must name the exact displayed question; never substitute its newer revision.</summary>
    public Task<WorkflowConversationSnapshot> AnswerRunAsync(WorkflowAnswerRequest request, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        var run = conversation?.Snapshot.Run;
        if (run?.Id != request.RunId)
            throw new InvalidOperationException("This workflow question is stale or belongs to another run. Inspect the exact run before answering.");
        if (run.Questions is null)
            return RetryAnswerRunAsync(request, token);
        var questions = run.Questions;
        if (questions.Id != request.QuestionSetId || questions.Revision != request.ExpectedQuestionRevision)
            throw new InvalidOperationException("This workflow question is stale or belongs to another run. Inspect the exact run before answering.");
        // No await separates the identity guard from the native form's mutation gate.
        return AnswerConversationAsync(request.RequestId, request.Answers, token);
    }

    private async Task<WorkflowConversationSnapshot> RetryAnswerRunAsync(WorkflowAnswerRequest request, CancellationToken token)
    {
        var current = conversation ?? throw new InvalidOperationException("Open a saved workflow first.");
        await ChangeAsync(x => x.RetryAnswerAsync(request, token), mutation: true);
        return current.Snapshot;
    }

    private async Task ChangeAsync(Func<WorkflowConversationClient, Task<WorkflowConversationSnapshot>> change, bool mutation = false)
    {
        var current = conversation ?? throw new InvalidOperationException("Open a saved workflow first.");
        var expected = generation;
        if (mutation) await MutateAsync(() => change(current));
        else await change(current);
        if (current == conversation && expected == generation) Render();
    }

    public async Task LoadEarlierMessagesAsync()
    {
        var current = conversation ?? throw new InvalidOperationException("Open saved work first.");
        if (!current.Snapshot.HasOlder) return;
        var expected = generation;
        bool StillSelected() => !disposed && expected == generation && ReferenceEquals(current, conversation);
        var previousExtent = transcriptScroller.ExtentHeight;
        var previousOffset = transcriptScroller.VerticalOffset;
        var previousHistory = readingHistory;
        var previousArmed = historyReleaseArmed;
        var previousBoundary = historyReleaseOffset;
        historyReleaseArmed = false;
        readingHistory = true;
        try
        {
            await ChangeAsync(x => x.LoadOlderAsync(lifetime.Token));
            if (!StillSelected()) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!StillSelected()) return;
                transcriptScroller.UpdateLayout();
                var addedExtent = Math.Max(0, transcriptScroller.ExtentHeight - previousExtent);
                historyReleaseOffset = (historyReleaseOffset ?? 0) + addedExtent;
                transcriptScroller.ScrollToVerticalOffset(previousOffset + addedExtent);
                transcriptScroller.UpdateLayout();
            }, DispatcherPriority.Loaded, lifetime.Token);
            if (!StillSelected()) return;
            // Rebuilding the visual tree and restoring its anchor both raise ScrollChanged. Arm
            // eviction only after those layout notifications drain so a programmatic restore can
            // never be mistaken for the reader returning to the recent page.
            await Dispatcher.InvokeAsync(() =>
            {
                if (StillSelected()) historyReleaseArmed = true;
            }, DispatcherPriority.ContextIdle, lifetime.Token);
        }
        catch when (!StillSelected()) { /* An obsolete read cannot report into the new selection. */ }
        catch
        {
            readingHistory = previousHistory;
            historyReleaseArmed = previousArmed;
            historyReleaseOffset = previousBoundary;
            throw;
        }
    }

    private async Task ReleaseEarlierAsync()
    {
        var current = conversation;
        var boundary = historyReleaseOffset;
        if (current is null || boundary is null) { releasingHistory = false; return; }
        var expected = generation;
        bool StillSelected() => !disposed && expected == generation && ReferenceEquals(current, conversation);
        var relativeOffset = Math.Max(0, transcriptScroller.VerticalOffset - boundary.Value);
        readingHistory = false;
        historyReleaseArmed = false;
        historyReleaseOffset = null;
        try
        {
            await ChangeAsync(x => x.RefreshAsync(latestEvents: true, token: lifetime.Token));
            if (!StillSelected()) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!StillSelected()) return;
                transcriptScroller.UpdateLayout();
                transcriptScroller.ScrollToVerticalOffset(Math.Min(relativeOffset, transcriptScroller.ScrollableHeight));
            }, DispatcherPriority.Loaded, lifetime.Token);
        }
        catch when (!StillSelected()) { /* The released page belongs to the old selection. */ }
        catch
        {
            readingHistory = true;
            historyReleaseArmed = true;
            historyReleaseOffset = boundary;
            throw;
        }
        finally { if (StillSelected()) releasingHistory = false; }
    }

    private async Task<T> MutateAsync<T>(Func<Task<T>> action, bool guidance = false)
    {
        if (monitoringOnly) throw new InvalidOperationException("This exact team run is open for read-only monitoring.");
        if (!guidance) RequireSettledWorkGuidance();
        if (mutationPending) throw new InvalidOperationException("A request is awaiting acceptance. Wait for its receipt before another action.");
        // Set this synchronously: before transport returns there may be neither a Run nor an
        // uncertainty flag, but the request identity already belongs to this conversation.
        mutationPending = true; UpdateControls(); SnapshotChanged?.Invoke(this, EventArgs.Empty);
        try { return await action(); }
        finally { mutationPending = false; UpdateControls(); SnapshotChanged?.Invoke(this, EventArgs.Empty); }
    }

    private static bool IsTranscriptCard(CerebralEvent item) =>
        item.Projection.Human == HumanVisibility.Transcript &&
        item.Type is not CerebralEventTypes.WorkflowTransition and not "workflow.questions";

    private void RetainTranscriptEvents(WorkflowConversationSnapshot snapshot, WorkflowConversationRun run)
    {
        // Activity volume must not evict accepted conversation cards. This small semantic window is
        // separate from the raw recent page cache; manually loaded history remains temporary.
        if (retainedTranscriptEvents.Count > 0 && retainedTranscriptEvents[0].ConversationId != snapshot.ConversationId)
            retainedTranscriptEvents.Clear();
        IEnumerable<CerebralEvent> candidates = snapshot.Events;
        if (snapshot.OriginalMessage is { } originalMessage) candidates = candidates.Append(originalMessage);
        foreach (var item in candidates.Where(IsTranscriptCard))
            if (retainedTranscriptEvents.All(existing => existing.Id != item.Id)) retainedTranscriptEvents.Add(item);
        retainedTranscriptEvents.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        var original = retainedTranscriptEvents.FirstOrDefault(item => item.TurnId == run.Id &&
            item.Type == CerebralEventTypes.Message && item.Actor.Kind == EventActorKind.User);
        while (retainedTranscriptEvents.Count > MaximumRetainedTranscriptEvents)
        {
            // Pin the exact observed original request for the selected run, then keep recent cards.
            var oldest = retainedTranscriptEvents.FindIndex(item => item.Id != original?.Id);
            retainedTranscriptEvents.RemoveAt(oldest);
        }
    }

    private void Render()
    {
        var snapshot = conversation!.Snapshot;
        var run = snapshot.Run;
        ReconcileGenerationPreview();
        if (run is null) { UpdateControls(); SnapshotChanged?.Invoke(this, EventArgs.Empty); return; }
        if (!readingHistory)
        {
            if (recentWindowEvents.Count > 0 && recentWindowEvents[0].ConversationId != snapshot.ConversationId)
                recentWindowEvents.Clear();
            foreach (var item in snapshot.Events)
                if (recentWindowEvents.All(existing => existing.Id != item.Id)) recentWindowEvents.Add(item);
            recentWindowEvents.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            while (recentWindowEvents.Count > WorkflowConversationClient.MaximumPageEvents * WorkflowConversationClient.MaximumRecentPages)
                recentWindowEvents.RemoveAt(0);
            RetainTranscriptEvents(snapshot, run);
            displayedEvents.Clear();
            displayedEvents.AddRange(recentWindowEvents);
            foreach (var item in retainedTranscriptEvents)
                if (displayedEvents.All(existing => existing.Id != item.Id)) displayedEvents.Add(item);
        }
        else foreach (var item in snapshot.Events)
            if (displayedEvents.All(existing => existing.Id != item.Id)) displayedEvents.Add(item);
        displayedEvents.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        UpdateRunSummary(snapshot, run);
        var questionVersion = run.Questions is null ? "" : $"{run.Questions.Id}:{run.Questions.Revision}";
        var version = $"{run.Id}:{run.Status}:{run.CancelRequested}:{run.WaitingReason}:{run.Error}:{run.EffectState}:" +
            $"{displayedEvents.Count}:{displayedEvents.FirstOrDefault()?.Id}:{displayedEvents.LastOrDefault()?.Id}:{questionVersion}:{snapshot.MutationUncertain}";
        if (version == renderedVersion)
        {
            RefreshActivityHeaders(run);
            UpdateControls();
            return;
        }
        renderedVersion = version;
        var priorOffset = transcriptRestoring ? transcriptRestoreOffset : transcriptScroller.VerticalOffset;
        var followEnd = transcriptRestoring ? transcriptRestoreFollowEnd : transcriptScroller.ScrollableHeight <= priorOffset + 32;
        var scrollInputRevision = transcriptInputRevision;
        transcriptRestoreOffset = priorOffset;
        transcriptRestoreFollowEnd = followEnd;
        transcriptRestoring = !readingHistory;
        var priorActivityPositions = activityViews.ToDictionary(view => view.Key,
            view => new ScrollPosition(view.Restoring ? view.RestoreOffset : view.Scroller.VerticalOffset, view.FollowEnd),
            StringComparer.Ordinal);
        var priorActivityViews = activityViews.ToDictionary(view => view.Key, StringComparer.Ordinal);
        activityViews.Clear();
        activityEntries.Clear();
        var renderRevision = ++transcriptRenderRevision;
        var visibleIds = displayedEvents.Select(x => x.Id).Concat(displayedEvents.Select(x => "activity:" + x.Id)).ToHashSet(StringComparer.Ordinal);
        expanded.IntersectWith(visibleIds);
        if (!snapshot.MutationUncertain) pendingSubmission = null;
        transcript.Children.Clear();
        renderedMessageCards.Clear();
        renderedReadCards.Clear();
        ReconcileImageWindow();
        ReconcileAgentUpdateCardWindow();
        renderedTranscriptEventIds.Clear(); renderedTranscriptCount = 0;
        DisplayedFinalResponse = null;
        transcript.Children.Add(loadEarlier);
        Expander? activity = null; StackPanel? entries = null; List<CerebralEvent>? activityItems = null;
        ActivityView? activityView = null;
        var activityAnchor = "leading";
        string? activityTurn = null;
        var turnActivityViews = new Dictionary<string, ActivityView>(StringComparer.Ordinal);
        var profileNames = profiles().Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
            .GroupBy(profile => profile.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        foreach (var item in displayedEvents)
        {
            if (item.Projection.Human is HumanVisibility.Hidden or HumanVisibility.Sidebar) continue;
            if (item.Projection.Human == HumanVisibility.Activity || item.Type == CerebralEventTypes.WorkflowTransition)
            {
                if (turnActivityViews.TryGetValue(item.TurnId, out var turnView))
                {
                    // Late receipts can interleave with a service continuation. Extend the
                    // already inserted turn view so call/receipt pairing and visual ownership
                    // survive without inserting the same Expander twice.
                    turnView.Items.Add(item);
                    continue;
                }
                if (activityTurn != item.TurnId)
                {
                    // Service continuations contribute activity without a synthetic user card.
                    // Give their accepted turn a stable branch after the preceding response,
                    // rather than appending new work to an earlier turn's viewport and totals.
                    activity = null; entries = null; activityItems = null;
                    activityAnchor = "turn:" + item.TurnId;
                    activityTurn = item.TurnId;
                }
                if (activity is null)
                {
                    var key = "activity:" + activityAnchor;
                    if (priorActivityViews.TryGetValue(key, out var priorView))
                    {
                        activityView = priorView;
                        activityView.Items.Clear();
                        activityView.Restoring = true;
                        activity = activityView.Expander;
                        activityItems = activityView.Items;
                    }
                    else
                    {
                        entries = new StackPanel(); activityItems = [];
                        var activityScroller = new ScrollViewer
                        {
                            Content = entries,
                            MaxHeight = 180,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            CanContentScroll = false,
                            PanningMode = PanningMode.VerticalOnly
                        };
                        AutomationProperties.SetAutomationId(activityScroller, "WorkflowActivityScroller");
                        var activityViewport = new Border
                        {
                            Child = activityScroller,
                            Margin = new Thickness(8, 5, 2, 7),
                            Padding = new Thickness(4),
                            Background = Ui.RaisedSurface,
                            BorderBrush = Ui.Line,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = .18, Color = Colors.Black }
                        };
                        AutomationProperties.SetAutomationId(activityViewport, "WorkflowActivityViewport");
                        activity = new Expander { Header = "Activity", HeaderTemplate = ActivityCaptionTemplate(),
                            HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = activityViewport, Margin = new Thickness(0, 8, 0, 10),
                            IsExpanded = !embedded && !collapsedActivity.Contains(key) };
                        AutomationProperties.SetAutomationId(activity, "WorkflowActivity");
                        activity.Expanded += (_, _) => collapsedActivity.Remove(key); activity.Collapsed += (_, _) => collapsedActivity.Add(key);
                        activityView = new(key, activity, activityScroller, activityItems);
                        if (embedded) activityView.SummaryButton = CreateActivitySummary(activityView);
                        ConfigureActivityScrollTracking(activityView);
                    }
                    activityViews.Add(activityView);
                    turnActivityViews[item.TurnId] = activityView;
                    transcript.Children.Add(activityView.SummaryButton is { } summary ? summary : activity);
                }
                activityItems!.Add(item);
                continue;
            }
            if (!IsTranscriptCard(item)) continue; // Active forms have one input owner below.
            // Late receipts stay with their accepted turn even after its response. A real
            // user card establishes its existing anchor; service turns start their own branch
            // at the first activity event and never synthesize another user prompt.
            if (item.Actor.Kind == EventActorKind.User)
            {
                turnActivityViews.Remove(item.TurnId);
                activity = null; entries = null; activityItems = null;
                activityAnchor = item.Id;
                activityTurn = item.TurnId;
            }
            var card = TranscriptCard(item, profileNames);
            transcript.Children.Add(card);
            if (item.Type == "workflow.result" && item.ConversationId == snapshot.ConversationId &&
                item.TurnId == run.Id && item.Actor.Kind == EventActorKind.Agent)
                DisplayedFinalResponse = WorkflowFinalResponseText.Read(item, snapshot.ConversationId, run.Id, run.ProfileId);
            renderedMessageCards.Add((item.Id, item.Sequence, card));
            if (CountsAsMessage(item.Type)) renderedReadCards.Add((item.Sequence, card));
            if (item.Type == "workflow.result" && item.TurnId == run.Id &&
                item.ConversationId == snapshot.ConversationId && item.Actor.Kind == EventActorKind.Agent)
                PublishPresentationMark(WorkflowPresentationMarkKind.FinalCardMutated,
                    snapshot.ConversationId, run.Id, run.Id, null,
                    item.Actor.Id ?? item.Provenance.AgentId ?? "", item.Id);
            renderedTranscriptCount++;
            if (renderedTranscriptEventIds.Count < MaximumTranscriptDiagnosticIds) renderedTranscriptEventIds.Add(item.Id);
        }
        foreach (var view in activityViews) ReconcileActivityRows(view, run, profileNames);
        MountGenerationPreviewInActivity();
        ReconcileGenerationMetrics();
        var newQuestions = run.Questions is not null && questionVersion != questionKey;
        if (newQuestions) pendingQuestionFocus = questionVersion;
        RenderQuestions();
        UpdateControls();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        ObserveReadViewport();
        // Existing activity rows survive new receipts. Restore the viewport from explicit
        // reader intent, not from offsets temporarily clamped while arranging the transcript.
        var activityRestores = activityViews.Select(view =>
        {
            if (priorActivityPositions.TryGetValue(view.Key, out var prior)) view.FollowEnd = prior.FollowEnd;
            view.RestoreOffset = prior?.Offset ?? 0;
            return (View: view, Prior: prior, InputRevision: view.InputRevision);
        }).ToArray();
        pendingScrollRestore = () =>
        {
            if (disposed || renderRevision != transcriptRenderRevision) return;
            transcriptScroller.UpdateLayout();
            // A native chat can be prepared while its host is still hidden/unarranged. Keep the
            // restore until a real viewport exists instead of consuming it at zero height.
            if (transcriptScroller.ViewportHeight <= 0) return;
            pendingScrollRestore = null;
            if (!readingHistory && scrollInputRevision == transcriptInputRevision)
            {
                transcriptApplyingRestore = true;
                try
                {
                    if (pendingQuestionFocus == questionKey && pendingQuestionFocus is not null && answerHost.Visibility == Visibility.Visible)
                    {
                        transcriptScroller.ScrollToVerticalOffset(answerHost.TranslatePoint(new Point(), conversationFlow).Y);
                        pendingQuestionFocus = null;
                    }
                    else if (followEnd) transcriptScroller.ScrollToEnd();
                    else transcriptScroller.ScrollToVerticalOffset(Math.Clamp(priorOffset, 0, transcriptScroller.ScrollableHeight));
                    transcriptScroller.UpdateLayout();
                }
                finally { transcriptApplyingRestore = false; }
            }
            transcriptRestoring = false;
            foreach (var restore in activityRestores)
            {
                var view = restore.View;
                view.Scroller.UpdateLayout();
                // A real wheel/key/click/selection after rendering supersedes this queued restore.
                if (view.InputRevision != restore.InputRevision) continue;
                if (view.FollowEnd) view.Scroller.ScrollToEnd();
                else view.Scroller.ScrollToVerticalOffset(Math.Clamp(restore.Prior?.Offset ?? 0, 0, view.Scroller.ScrollableHeight));
                view.Scroller.UpdateLayout();
                view.Restoring = false;
            }
        };
        QueuePendingScrollRestoration();
    }

    private void UpdateRunSummary(WorkflowConversationSnapshot snapshot, WorkflowConversationRun run)
    {
        heading.Text = run.UserText.Length > 100 ? run.UserText[..100] + "…" : run.UserText;
        // Exact identities are public automation metadata; visible headings remain readable.
        AutomationProperties.SetAutomationId(heading, "WorkflowSelectedRun-" + run.Id);
        AutomationProperties.SetHelpText(heading, snapshot.ConversationId);
        heading.TextWrapping = TextWrapping.NoWrap; heading.TextTrimming = TextTrimming.CharacterEllipsis;
        state.Text = snapshot.MutationUncertain ? "Outcome not confirmed. Refresh before retrying the same request." :
            run.WaitingReason ?? run.Error ?? run.Status.ToString();
        state.Foreground = run.Status == WorkflowStatus.Failed ? Ui.ErrorText : Ui.Muted;
    }

    private void RefreshActivityHeaders(WorkflowConversationRun run)
    {
        var profileNames = profiles().Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
            .GroupBy(profile => profile.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        var currentView = CurrentGenerationActivity();
        foreach (var view in activityViews)
        {
            var currentIteration = ReferenceEquals(view, currentView);
            foreach (var row in view.Rows.Values)
                row.Expander.Header = ActivityRowCaption(row.Model, run, view.Items, profileNames,
                    currentIteration: currentIteration);
            view.Expander.Header = ActivityHeader(view.Items, run, profileNames,
                olderActivityOmitted: Snapshot?.HasOlder == true, currentIteration: currentIteration) +
                GenerationRateCaption(view, run, currentIteration);
            RefreshActivitySectionHeaders(view, run, currentIteration);
            view.Expander.Foreground = ActivityHeaderBrush(view.Items, run, currentIteration);
        }
    }

    private static string Caption(CerebralEvent item, IReadOnlyDictionary<string, ActivityCall> calls,
        IReadOnlyDictionary<string, string> profileNames)
    {
        var origin = ActivityOriginPrefix(item, profileNames);
        if (TryToolCall(item, out _, out var callLabel)) return origin + callLabel;
        if (item.Type == CerebralEventTypes.ToolResult)
        {
            var result = ToolResult(item);
            var recordedTool = item.Data is { } resultData ? ActivityString(resultData, "toolName") : "";
            var fallbackLabel = recordedTool.Length > 0 && recordedTool != "cerebral.host:tool"
                ? FriendlyToolLabel(recordedTool, default) : "Command";
            var call = result.CallId is not null && calls.TryGetValue(result.CallId, out var matched) ? matched : new(fallbackLabel, ActivityOrigin(item, profileNames));
            var state = ActivityResultState(item);
            return ActivityOriginPrefix(call.Origin) + state + " · " + call.Label + (result.DurationMilliseconds is > 0 ? " · " + FormatCallElapsed(result.DurationMilliseconds.Value) : "");
        }
        if (item.Type == CerebralEventTypes.WorkflowTransition)
        {
            var detail = item.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String ? reason.GetString()?.Replace('.', ' ') : null;
            return origin + (string.IsNullOrWhiteSpace(detail) ? "Service update" : "Service update · " + detail);
        }
        return origin + item.Type.Replace('.', ' ');
    }

    internal static string ActivityHeader(IReadOnlyList<CerebralEvent> items, WorkflowConversationRun run,
        IReadOnlyDictionary<string, string> profileNames, DateTimeOffset? measuredAt = null,
        bool olderActivityOmitted = false, bool currentIteration = true)
    {
        var calls = RecordedActivityCallCount(items);
        var rows = ProjectActivityRows(items);
        var turns = items.Select(item => item.TurnId).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        var turnId = turns.Length == 1 ? turns[0] : null;
        var current = currentIteration && turnId == run.Id;
        var partialWindow = olderActivityOmitted &&
            (turnId is null || !HasRecordedActivityStart(items, turnId));
        // An unmatched receipt is evidence of an unresolved effect, not proof that it is
        // still executing. In particular, owner cancellation can leave a durable Unknown.
        var active = rows.LastOrDefault(row => row.Event.Type == CerebralEventTypes.ToolCall && row.Event.TurnId == run.Id)?.Event;
        var working = currentIteration && active is not null && ActivityRunExecuting(run);
        long measured = 0; int inputTokens = 0; int outputTokens = 0; bool hasInputTokens = false; bool hasOutputTokens = false;
        foreach (var item in items.Where(x => x.Type == CerebralEventTypes.ToolResult))
        {
            var result = ToolResult(item);
            measured += Math.Max(0, result.DurationMilliseconds ?? 0);
            if (GenerationUsage(item) is var usage && usage.HasValue)
            {
                if (usage.Value.Input is { } input) { inputTokens += input; hasInputTokens = true; }
                if (usage.Value.Output is { } output) { outputTokens += output; hasOutputTokens = true; }
                if (result.DurationMilliseconds is null) measured += Math.Max(0, usage.Value.Elapsed);
            }
        }
        if (active is not null)
        {
            // Freeze an unresolved call at the recorded transition out of execution. A
            // later UI refresh must not turn waiting time into elapsed execution time.
            var end = working ? measuredAt ?? DateTimeOffset.UtcNow :
                items.LastOrDefault(x => x.Type == CerebralEventTypes.WorkflowTransition &&
                    x.TurnId == active.TurnId && x.CreatedAt >= active.CreatedAt)?.CreatedAt ?? active.CreatedAt;
            measured += Math.Max(0, (long)(end - active.CreatedAt).TotalMilliseconds);
        }
        // ActiveMilliseconds includes the command's full timeout reservation while it is in
        // flight. That reservation protects the execution budget; it is not elapsed user time.
        // The unmatched call event above supplies the smooth live duration until the measured
        // receipt replaces the reservation.
        var completedFloor = run.EffectState is WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown
            ? Math.Max(0, run.ActiveMilliseconds - run.ReservedExecutionMilliseconds)
            : run.ActiveMilliseconds;
        if (current) measured = Math.Max(measured, completedFloor);
        else if (turnId is not null && RecordedTerminalActiveMilliseconds(items, turnId) is { } recordedFloor)
            measured = Math.Max(measured, recordedFloor);
        var state = ActivityState(items, run, currentIteration);
        var origins = items.Select(item => ActivityOrigin(item, profileNames)).Where(origin => ActivityOriginPrefix(origin).Length > 0)
            .Distinct(StringComparer.Ordinal).Take(3).ToArray();
        var parts = new List<string> { "Activity" };
        if (origins.Length > 0) parts.Add(string.Join(", ", origins));
        parts.Add(state + (measured > 0 ? " " + FormatElapsed(measured) : ""));
        // A bounded journal window is not a whole-run count. Earlier iterations
        // of this same run also keep their own observed values after a refresh.
        const string shown = " shown";
        var totals = current ? MatchingActivityTotals(items, run) : null;
        if (totals is not null)
        {
            parts.Add($"{totals.Calls} call{(totals.Calls == 1 ? "" : "s")} total");
            parts.Add($"{totals.Completed} completed · {totals.Failed} failed · {totals.Cancelled} cancelled · {totals.Unconfirmed} unconfirmed");
            if (totals.Blocked > 0) parts.Add($"{totals.Blocked} blocked");
        }
        else if (current && run.ActivityTotals is not null)
        {
            parts.Add("Run totals updating / unavailable");
        }
        else
        {
            if (calls > 0) parts.Add($"{calls} call{(calls == 1 ? "" : "s")}{shown}");
            parts.Add(RecordedActivityOutcomeCaption(items));
            parts.Add("Whole-run totals unavailable");
        }
        if (hasInputTokens && hasOutputTokens) parts.Add($"{inputTokens:N0} in / {outputTokens:N0} out{shown}");
        else if (hasInputTokens) parts.Add($"{inputTokens:N0} in{shown}");
        else if (hasOutputTokens) parts.Add($"{outputTokens:N0} out{shown}");
        if (partialWindow) parts.Add("Earlier activity omitted");
        return string.Join(" · ", parts);
    }

    internal static WorkflowActivityTotals? MatchingActivityTotals(IReadOnlyList<CerebralEvent> items, WorkflowConversationRun run)
    {
        // A total belongs to one exact current turn and accepted run revision. A
        // rolling page, stale snapshot, or aggregate for another context is not a total.
        if(items.Count==0 || items.Any(item=>item.ConversationId!=run.ConversationId || item.TurnId!=run.Id) ||
            run.ActivityTotals is not {CoverageComplete:true} totals) return null;
        var sequence=items.Max(item=>item.Sequence);
        return totals.IsValidFor(run.ConversationId,run.Id,run.Revision,sequence)?totals:null;
    }

    private static bool HasRecordedActivityStart(IReadOnlyList<CerebralEvent> items, string turnId)
    {
        var first = items.Where(item => item.TurnId == turnId && item.Type == CerebralEventTypes.WorkflowTransition)
            .OrderBy(item => item.Sequence).FirstOrDefault();
        return TryEngineTransition(first, turnId, out var data) &&
               data.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String &&
               reason.GetString() == "submitted" &&
               data.TryGetProperty("activeMilliseconds", out var active) && active.ValueKind == JsonValueKind.Number &&
               active.TryGetInt64(out var milliseconds) && milliseconds == 0;
    }

    private static long? RecordedTerminalActiveMilliseconds(IReadOnlyList<CerebralEvent> items, string turnId)
    {
        // Only the exact final engine transition can supply a durable floor. An earlier terminal-looking
        // payload must not mask a malformed or incomplete tail in the bounded activity window.
        var final = items.Where(item => item.TurnId == turnId && item.Type == CerebralEventTypes.WorkflowTransition)
            .OrderBy(item => item.Sequence).LastOrDefault();
        if (!TryEngineTransition(final, turnId, out var data) ||
            !data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<WorkflowStatus>(status.GetString(), out var parsed) || !Enum.IsDefined(parsed) ||
            parsed is not (WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled) ||
            !data.TryGetProperty("activeMilliseconds", out var active) || active.ValueKind != JsonValueKind.Number ||
            !active.TryGetInt64(out var milliseconds) || milliseconds < 0 ||
            milliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) return null;
        return milliseconds;
    }

    private static bool TryEngineTransition(CerebralEvent? item, string turnId, out JsonElement data)
    {
        data = default;
        if (item is not { Type: CerebralEventTypes.WorkflowTransition,
                Actor: { Kind: EventActorKind.Service, Id: "cerebral.engine" },
                Provenance: { Source: EventSourceKind.Service, SourceId: "cerebral.engine" } } ||
            item.TurnId != turnId || item.Provenance.JobId != turnId ||
            item.Data is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("runId", out var runId) || runId.ValueKind != JsonValueKind.String ||
            runId.GetString() != turnId) return false;
        data = value;
        return true;
    }

    internal static string ActivityState(IReadOnlyList<CerebralEvent> items, WorkflowConversationRun run,
        bool currentIteration = true)
    {
        var latestTurn = items.OrderBy(item => item.Sequence).LastOrDefault()?.TurnId;
        if (latestTurn is not null && latestTurn != run.Id)
        {
            var transition = items.Where(item => item.TurnId == latestTurn && item.Type == CerebralEventTypes.WorkflowTransition)
                .OrderBy(item => item.Sequence).LastOrDefault(item =>
                    item.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("status", out _));
            return transition?.Data is { } recorded && recorded.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String && Enum.TryParse<WorkflowStatus>(status.GetString(), out var parsed) &&
                Enum.IsDefined(parsed) ? parsed.ToString() : "Recorded";
        }
        if (!currentIteration && latestTurn == run.Id)
        {
            var pending = ProjectActivityRows(items).Any(row => row.Event.Type == CerebralEventTypes.ToolCall);
            return pending ? "Awaiting receipt" : "Finished";
        }
        if (run.IsTerminal) return run.Status.ToString();
        if (run.EffectState == WorkflowEffectState.Unknown) return "Waiting · outcome unconfirmed";
        if (run.CancelRequested) return "Cancelling";
        if (ActivityRunExecuting(run))
        {
            var active = ProjectActivityRows(items).LastOrDefault(row => row.Event.Type == CerebralEventTypes.ToolCall && row.Event.TurnId == run.Id);
            if (active is not null) return ActivityRecoveryState(active, items) ?? "Working";
        }
        return run.Status.ToString();
    }

    private static string ActivityOriginPrefix(CerebralEvent item, IReadOnlyDictionary<string, string> profiles) =>
        item.Type is CerebralEventTypes.ToolCall or CerebralEventTypes.ToolResult
            ? RecordedActivityAgent(item) + " · " : ActivityOriginPrefix(ActivityOrigin(item, profiles));

    public static string RecordedActivityAgent(CerebralEvent source)
    {
        // Current profile names, tool names and the selected panel cannot establish
        // the actor bound to a historical dispatch. Receipts have a Tool actor.
        if (source.Actor.Kind != EventActorKind.Agent || string.IsNullOrWhiteSpace(source.Actor.Id) ||
            !string.IsNullOrWhiteSpace(source.Provenance.AgentId) && source.Provenance.AgentId != source.Actor.Id ||
            string.IsNullOrWhiteSpace(source.Actor.DisplayName) || source.Actor.DisplayName.Length > 128 ||
            source.Actor.DisplayName.Any(char.IsControl)) return "Agent not recorded";
        return source.Actor.DisplayName.Trim();
    }

    private static string ActivityOriginPrefix(string origin) =>
        string.IsNullOrWhiteSpace(origin) || origin.Equals("Cerebral", StringComparison.OrdinalIgnoreCase) ||
        origin.Equals("Pegasus Cerebral", StringComparison.OrdinalIgnoreCase) || origin is "System service" or "Tool"
            ? "" : BoundActivitySummary(origin) + " · ";

    private static string ActivityOrigin(CerebralEvent item, IReadOnlyDictionary<string, string> profileNames)
    {
        if (!string.IsNullOrWhiteSpace(item.Actor.DisplayName)) return item.Actor.DisplayName!;
        var agentId = item.Provenance.AgentId ?? (item.Actor.Kind == EventActorKind.Agent ? item.Actor.Id : null);
        if (!string.IsNullOrWhiteSpace(agentId) && profileNames.TryGetValue(agentId, out var profileName) && !string.IsNullOrWhiteSpace(profileName))
            return profileName;
        if (item.Actor.Kind == EventActorKind.Agent) return !string.IsNullOrWhiteSpace(agentId) ? agentId! : "Cerebral";
        if (!string.IsNullOrWhiteSpace(item.Provenance.PluginId)) return item.Provenance.PluginId!;
        return item.Actor.Kind switch
        {
            EventActorKind.User => "You",
            EventActorKind.Plugin => !string.IsNullOrWhiteSpace(item.Actor.Id) ? item.Actor.Id! : "Plug-in",
            EventActorKind.Tool => "Tool",
            EventActorKind.Service or EventActorKind.System => "System service",
            _ => "Cerebral"
        };
    }

    private static string TranscriptSpeaker(CerebralEvent item, IReadOnlyDictionary<string, string> profileNames) =>
        item.Actor.Kind == EventActorKind.User ? "You" : ActivityOrigin(item, profileNames);

    private static bool TryToolCall(CerebralEvent item, out string callId, out string label)
    {
        callId = ""; label = "";
        if (item.Type != CerebralEventTypes.ToolCall || item.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("callId", out var id) || id.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("toolName", out var name) || name.ValueKind != JsonValueKind.String) return false;
        callId = id.GetString()!;
        var tool = name.GetString()!;
        JsonElement arguments = default;
        if (data.TryGetProperty("arguments", out var outer) && outer.ValueKind == JsonValueKind.Object)
        {
            arguments = outer;
            if (tool == "cerebral.host:tool" && outer.TryGetProperty("name", out var nestedName) && nestedName.ValueKind == JsonValueKind.String)
            {
                tool = nestedName.GetString()!;
                if (outer.TryGetProperty("arguments", out var nestedArguments) && nestedArguments.ValueKind == JsonValueKind.Object) arguments = nestedArguments;
            }
        }
        label = FriendlyToolLabel(tool, arguments);
        return true;
    }

    private static string? ToolCallId(CerebralEvent item) => TryToolCall(item, out var id, out _) ? id : null;

    private static string ActivityString(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return "";
        foreach (var field in fields)
            if (value.TryGetProperty(field, out var part) && part.ValueKind == JsonValueKind.String &&
                part.GetString() is { Length: > 0 } text) return text;
        return "";
    }

    private static string BoundActivitySummary(string text)
    {
        var safe = BoundActivityText(text, []);
        safe = string.Join(" ", safe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (safe.Length <= 180) return safe;
        var length = char.IsHighSurrogate(safe[178]) ? 178 : 179;
        return safe[..length] + "…";
    }

    private static string FriendlyToolLabel(string tool, JsonElement arguments)
    {
        var path = ActivityString(arguments, "path", "src", "dst", "projectPath", "manifestPath");
        var leaf = Path.GetFileName(path);
        var suffix = leaf.Length > 0 ? " · " + leaf : "";
        string label;
        if (tool.StartsWith("process.", StringComparison.Ordinal))
        {
            var executable = ActivityString(arguments, "executableId", "toolId");
            var command = arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty("arguments", out var argv) && argv.ValueKind == JsonValueKind.Array
                    ? ActivityArguments(argv) : "";
            label = "Running · " + (executable.Length > 0 ? executable : tool) +
                (command.Length > 0 ? " " + command : "");
        }
        else label = tool switch
        {
            "cerebral.host:generate" => "Generating response",
            "cerebral.host:catalog" => "Discovering tool descriptions" +
                (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array
                    ? " · " + string.Join(", ", names.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Take(8).Select(name => name.GetString())) : ""),
            "cerebral.host:history" => "Reading history",
            "cerebral.host:receipt" => "Reading receipt",
            "cerebral.host:compact" or "context.compact" => "Compacting context",
            "file.write" or "file.edit" or "file.append" => "Editing" + suffix,
            "file.copy" => "Copying" + suffix,
            "file.move" => "Moving" + suffix,
            "file.read" => "Reading" + suffix,
            "file.list" => "Listing files" + suffix,
            "file.search" => "Searching files" + suffix,
            "plugin.scaffold" => "Scaffolding plug-in" + suffix,
            "plugin.verify" => "Verifying plug-in" + suffix,
            "plugin.present" => "Presenting plug-in" + suffix,
            _ when tool.StartsWith("skill", StringComparison.Ordinal) => "Skill · " + tool.Replace('.', ' ') +
                (ActivityString(arguments, "skillName", "skillId", "name", "id") is { Length: > 0 } skill ? " · " + skill : ""),
            _ when tool.Contains("workflow", StringComparison.Ordinal) => "Workflow · " + tool.Replace('.', ' ') +
                (ActivityString(arguments, "definitionId", "workflowId", "name", "id") is { Length: > 0 } workflow ? " · " + workflow : ""),
            _ when tool.StartsWith("mcp.", StringComparison.Ordinal) => "MCP · " + tool[4..] +
                (ActivityString(arguments, "op") is { Length: > 0 } operation ? " · " + operation : "") + suffix,
            _ => tool + suffix
        };
        return BoundActivitySummary(BoundActivityText(label, ActivitySecrets(arguments)));
    }

    private static (string? CallId, bool? Success, long? DurationMilliseconds) ToolResult(CerebralEvent item)
    {
        if (item.Type != CerebralEventTypes.ToolResult || item.Data is not { ValueKind: JsonValueKind.Object } data) return default;
        var id = data.TryGetProperty("callId", out var callId) && callId.ValueKind == JsonValueKind.String ? callId.GetString() : null;
        bool? success = data.TryGetProperty("success", out var succeeded) && succeeded.ValueKind is JsonValueKind.True or JsonValueKind.False ? succeeded.GetBoolean() : null;
        long? duration = data.TryGetProperty("durationMilliseconds", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number &&
            elapsed.TryGetInt64(out var value) ? value : null;
        return (id, success, duration);
    }

    private static (int? Input, int? Output, long Elapsed)? GenerationUsage(CerebralEvent item)
    {
        var command = item.Content.OfType<StructuredContentPart>().FirstOrDefault(x => x.Schema == "workflow.command.result")?.Value;
        if (command is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object) return null;
        data.TryGetProperty("prepared", out var prepared);
        data.TryGetProperty("generation", out var generation);
        int? input = prepared.ValueKind == JsonValueKind.Object && prepared.TryGetProperty("inputTokens", out var inputValue) && inputValue.ValueKind == JsonValueKind.Number &&
            inputValue.TryGetInt32(out var parsedInput) && parsedInput >= 0 ? parsedInput : null;
        int? output = generation.ValueKind == JsonValueKind.Object && generation.TryGetProperty("generatedTokens", out var outputValue) && outputValue.ValueKind == JsonValueKind.Number &&
            outputValue.TryGetInt32(out var parsedOutput) && parsedOutput >= 0 ? parsedOutput : null;
        var elapsed = data.TryGetProperty("elapsedMilliseconds", out var elapsedValue) && elapsedValue.ValueKind == JsonValueKind.Number &&
            elapsedValue.TryGetInt64(out var parsedElapsed) ? parsedElapsed : 0;
        return (input, output, elapsed);
    }

    private static string FormatElapsed(long milliseconds)
    {
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private static string FormatCallElapsed(long milliseconds) => milliseconds switch
    {
        < 1_000 => $"{Math.Max(1, milliseconds)} ms",
        < 60_000 => $"{milliseconds / 1000d:0.0} s",
        _ => FormatElapsed(milliseconds)
    };

    private UIElement EventBody(CerebralEvent item)
    {
        var content = new StackPanel();
        for (var partIndex = 0; partIndex < item.Content.Count; partIndex++)
        {
            var part = item.Content[partIndex];
            if (part is TextContentPart text) content.Children.Add(MarkdownView.Render(text.Text));
            else if (part is StructuredContentPart data)
            {
                if (data.Schema == "workflow.answers" && data.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var value in data.Value.EnumerateObject())
                    {
                        var replyText = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString();
                        var reply = new TextBox { Text = value.Name + ": " + replyText, IsReadOnly = true, BorderThickness = new Thickness(0),
                            Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0, 2, 0, 4) };
                        TextContextMenus.Attach(reply); content.Children.Add(reply);
                    }
                }
                else if (data.Schema == "workflow.result" && data.Value.ValueKind == JsonValueKind.Object &&
                    data.Value.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
                    content.Children.Add(MarkdownView.Render(summary.GetString()!));
                else content.Children.Add(StructuredText(data.Value));
            }
            else if (part is ImageContentPart image) content.Children.Add(ImageCard(item, image.Attachment, "Attached image", "image-" + partIndex));
            else if (part is FileContentPart file) content.Children.Add(AttachmentPresentation.Reference(file.Attachment, "File"));
            else if (part is AudioContentPart audio) content.Children.Add(AttachmentPresentation.Reference(audio.Attachment, "Audio"));
            else content.Children.Add(Ui.Text(part is ResourceContentPart resource ? resource.Resource.ResourceId : "Attachment", 13));
        }
        if (item.Data is { ValueKind: JsonValueKind.Object } details)
        {
            // Event Data often contains internal response-hook/decision correlation. Public
            // chat content comes from typed content parts; never append that envelope as prose.
            var lines = new List<string>();
            foreach (var field in new[] { "summary", "message" }) AddActivityValue(field, details, lines);
            if (lines.Count > 0) content.Children.Add(ActivityDetailBox(BoundActivityText(string.Join("\n", lines), ActivitySecrets(details))));
        }
        return content;
    }

    private static TextBox StructuredText(JsonElement value)
    {
        var current = value.Clone();
        string? plainText = null;
        for (var depth = 0; depth < 4 && current.ValueKind == JsonValueKind.String; depth++)
        {
            var raw = current.GetString() ?? "";
            try
            {
                using var document = JsonDocument.Parse(raw);
                current = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                plainText = raw;
                break;
            }
        }
        // Typed public content may use arbitrary schema fields or encoded scalar strings.
        // Keep that content distinct from the internal event Data envelope, while sharing
        // credential redaction with activity details and bounding the visible projection.
        var display = BoundActivityText(plainText ?? FormatStructuredValue(current), ActivitySecrets(current));
        var box = Ui.Json(display);
        box.IsReadOnly = true;
        box.MinHeight = 30;
        box.MaxHeight = double.PositiveInfinity;
        box.TextWrapping = TextWrapping.Wrap;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SelectableMarkdown.RouteMouseWheelToAncestor(box);
        return box;
    }

    private static string FormatStructuredValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        var text = new StringBuilder();
        AppendStructuredValue(text, value, 0);
        return text.ToString().TrimEnd();
    }

    private static void AppendStructuredValue(StringBuilder text, JsonElement value, int indent)
    {
        static void Pad(StringBuilder target, int amount) => target.Append(' ', amount);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (!value.EnumerateObject().Any()) { text.Append("(empty)"); return; }
                foreach (var property in value.EnumerateObject())
                {
                    Pad(text, indent); text.Append(property.Name).Append(':');
                    if (ActivitySecretKeys.Contains(ActivityKey(property.Name)))
                    { text.AppendLine(" [Redacted]"); continue; }
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        text.AppendLine(); AppendStructuredValue(text, property.Value, indent + 2);
                    }
                    else { text.Append(' '); AppendStructuredValue(text, property.Value, 0); text.AppendLine(); }
                }
                break;
            case JsonValueKind.Array:
                if (!value.EnumerateArray().Any()) { text.Append("(empty)"); return; }
                foreach (var item in value.EnumerateArray())
                {
                    Pad(text, indent); text.Append("- ");
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        text.AppendLine(); AppendStructuredValue(text, item, indent + 2);
                    }
                    else { AppendStructuredValue(text, item, 0); text.AppendLine(); }
                }
                break;
            case JsonValueKind.String: text.Append(value.GetString()); break;
            case JsonValueKind.True: text.Append("true"); break;
            case JsonValueKind.False: text.Append("false"); break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: text.Append("(none)"); break;
            default: text.Append(value.GetRawText()); break;
        }
    }

    private void RenderQuestions()
    {
        var questions = conversation?.Snapshot.Run?.Questions;
        var key = questions is null ? null : questions.Id + ":" + questions.Revision;
        if (key == questionKey) return;
        var sameQuestionSet = questions is not null && questionKey?.StartsWith(questions.Id + ":", StringComparison.Ordinal) == true;
        if (sameQuestionSet) { foreach (var (id, read) in answers) answerDrafts[id] = read(); }
        else { answerDrafts.Clear(); pendingAnswer = null; }
        if (pendingAnswer is { } pending && questions is not null && questions.Revision > pending.ExpectedQuestionRevision &&
            pending.Answers.All(x => questions.Answers.TryGetValue(x.Key, out var value) && value == x.Value)) pendingAnswer = null;
        questionKey = key; QuestionsResetting?.Invoke(); fields.Children.Clear(); answers.Clear();
        if (questions is null) { pendingAnswer = null; pendingQuestionFocus = null; answerDrafts.Clear(); return; }
        var questionHeading = Ui.Text(monitoringOnly ? "Waiting for task input" : "Needs your input", 22);
        questionHeading.FontWeight = FontWeights.SemiBold;
        fields.Children.Add(questionHeading);
        fields.Children.Add(Ui.Text(monitoringOnly
            ? "These questions are shown for inspection. Answer through the accepted collaboration task."
            : "Answer the questions below to continue. Optional questions can be left blank.", 13, Ui.Muted));
        foreach (var field in questions.Fields)
        {
            if (questions.Answers.TryGetValue(field.Id, out var saved))
            {
                var display = field.Multiple && WorkflowQuestionAnswers.TrySelections(field, saved, out var selected) ? string.Join(", ", selected) : saved;
                fields.Children.Add(Ui.Text(field.Label + ": " + display, 13)); continue;
            }
            fields.Children.Add(Ui.Text(field.Label + (field.Required || field.Label.TrimEnd().EndsWith("(optional)", StringComparison.OrdinalIgnoreCase) ? "" : " (optional)"), 13));
            if (field.Description is { } description) fields.Children.Add(MarkdownView.Render(description,
                error: message => report(message, true)));
            var box = Ui.Input(answerDrafts.GetValueOrDefault(field.Id, ""), multiline: field.Choices is null);
            box.MinHeight = field.Choices is null ? 64 : 32;
            box.Margin = new Thickness(0, 4, 0, 10); box.MaxLength = 8192;
            AutomationProperties.SetAutomationId(box, "WorkflowAnswer-" + field.Id);
            TextContextMenus.EnableWritingAssistance(box);
            if (field.Multiple && field.Choices is { Count: > 0 } offered)
            {
                fields.Children.Add(Ui.Text("Select all that apply.", 12, Ui.Muted));
                var group = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
                AutomationProperties.SetAutomationId(group, "WorkflowAnswer-" + field.Id);
                WorkflowQuestionAnswers.TrySelections(field, box.Text, out var selected);
                var boxes = new List<(string Choice, CheckBox Box)>();
                for (var index = 0; index < offered.Count; index++)
                {
                    var choice = offered[index];
                    var check = new CheckBox { Content = Ui.Text(choice, 13), IsChecked = selected.Contains(choice, StringComparer.Ordinal),
                        Margin = new Thickness(0, 3, 0, 3) };
                    AutomationProperties.SetAutomationId(check, "WorkflowAnswer-" + field.Id + "-" + index);
                    AutomationProperties.SetName(check, choice);
                    group.Children.Add(check); boxes.Add((choice, check));
                }
                fields.Children.Add(group);
                answers[field.Id] = () => WorkflowQuestionAnswers.EncodeSelections(field, boxes.Where(item => item.Box.IsChecked == true).Select(item => item.Choice));
            }
            else if (field.Choices is { Count: > 0 })
            {
                fields.Children.Add(Ui.Text("Select one.", 12, Ui.Muted));
                var choices = new ComboBox { ItemsSource = field.Required ? field.Choices : new[] { "" }.Concat(field.Choices).ToArray(),
                    MinWidth = 140, Margin = new Thickness(0, 4, 0, 8),
                    ToolTip = field.Required ? "Choose an answer to continue." : "Leave blank to skip this optional question." };
                choices.SelectedItem = field.Choices.FirstOrDefault(x => x == box.Text);
                AutomationProperties.SetAutomationId(choices, "WorkflowAnswer-" + field.Id);
                fields.Children.Add(choices); answers[field.Id] = () => choices.SelectedItem as string ?? "";
            }
            else
            {
                fields.Children.Add(box); answers[field.Id] = () => box.Text;
                if (QuestionTextEntryActions?.Invoke(box, questions.Id + "." + field.Id) is { } actions) fields.Children.Add(actions);
            }
        }
    }

    private async Task AnswerAsync()
    {
        var current = conversation ?? throw new InvalidOperationException("Open work first.");
        var run = current.Snapshot.Run ?? throw new InvalidOperationException("Refresh work first.");
        var questions = run.Questions ?? throw new InvalidOperationException("There are no unanswered questions.");
        if (questionKey != questions.Id + ":" + questions.Revision) throw new InvalidOperationException("The displayed form is stale. Refresh before answering.");
        var values = answers.ToDictionary(x => x.Key, x => x.Value(), StringComparer.Ordinal);
        foreach (var field in questions.Fields.Where(x => x.Required && !questions.Answers.ContainsKey(x.Id)))
            if (!values.TryGetValue(field.Id, out var text) || string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Answer each required question before continuing.");
        values = values.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(StringComparer.Ordinal);
        if (pendingAnswer is null)
            pendingAnswer = new(Guid.NewGuid().ToString("N"), run.Id, questions.Id, questions.Revision, values);
        else if (!pendingAnswer.Answers.OrderBy(x => x.Key).SequenceEqual(values.OrderBy(x => x.Key)))
            throw new InvalidOperationException("An answer outcome is uncertain. Refresh or retry the unchanged answer before editing it.");
        var request = pendingAnswer;
        try
        {
            await ChangeAsync(x => x.AnswerAsync(request, lifetime.Token), mutation: true);
            pendingAnswer = null;
        }
        catch { if (!current.Snapshot.MutationUncertain) pendingAnswer = null; throw; }
    }

    private void UpdateControls()
    {
        var run = conversation?.Snapshot.Run;
        if (monitoringOnly)
        {
            stop.Visibility = resume.Visibility = reconcile.Visibility = answer.Visibility = Visibility.Collapsed;
            answerHost.Visibility = run?.Questions is null ? Visibility.Collapsed : Visibility.Visible;
            fields.IsEnabled = false;
            newWorkflow.Visibility = retrySubmission.Visibility = guidanceHost.Visibility = Visibility.Collapsed;
            loadEarlier.IsEnabled = conversation?.Snapshot.HasOlder == true;
            loadEarlier.Visibility = loadEarlier.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        stop.IsEnabled = run is { IsTerminal: false } && !mutationPending && !WorkGuidanceUncertain;
        if (embedded) stop.Visibility = run?.Questions is not null ? Visibility.Visible : Visibility.Collapsed;
        resume.Visibility = run is { Status: WorkflowStatus.Waiting, Questions: null } ? Visibility.Visible : Visibility.Collapsed;
        reconcile.Visibility = run?.EffectState == WorkflowEffectState.Unknown ? Visibility.Visible : Visibility.Collapsed;
        resume.IsEnabled = !mutationPending && !WorkGuidanceUncertain && run?.EffectState != WorkflowEffectState.Unknown;
        reconcile.IsEnabled = !mutationPending && !WorkGuidanceUncertain;
        answer.IsEnabled = !mutationPending && !WorkGuidanceUncertain;
        answerHost.Visibility = run?.Questions is null ? Visibility.Collapsed : Visibility.Visible;
        loadEarlier.IsEnabled = conversation?.Snapshot.HasOlder == true;
        loadEarlier.Visibility = conversation?.Snapshot.HasOlder == true ? Visibility.Visible : Visibility.Collapsed;
        recent.IsEnabled = !mutationPending && !WorkGuidanceUncertain;
        newWorkflow.IsEnabled = !mutationPending && !WorkGuidanceUncertain;
        UpdateWorkGuidance();
        UpdateHostAttention();
    }

    private async Task RetrySubmissionAsync()
    {
        var submission = pendingSubmission ?? throw new InvalidOperationException("No unconfirmed submission is retained.");
        var current = conversation ?? throw new InvalidOperationException("The request connection changed.");
        await MutateAsync(() => current.SubmitAsync(submission, lifetime.Token)); pendingSubmission = null; Render();
    }

    private async void Run(Func<Task> action)
    {
        if (disposed) return;
        var entered = false;
        try
        {
            await gate.WaitAsync(lifetime.Token);
            entered = true;
            if (!disposed) await action();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ReportFailure(ex); }
        finally { if (entered) gate.Release(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; generation++; refreshTimer.Stop(); activityClockTimer.Stop(); lifetime.Cancel();
        DetachReadViewportWindow();
        ClearImageCards(); ClearAgentUpdateCards(); ClearGenerationPreview(); UpdateControls(); lifetime.Dispose();
    }
    private sealed record WorkflowRunListItem(WorkflowRunSummary Run, string Label);
    private sealed class WorkflowRecentListBox : ListBox
    {
        internal WorkflowRecentListBox() => SetResourceReference(StyleProperty, typeof(ListBox));

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is ListBoxItem row && item is WorkflowRunListItem entry)
            {
                // Prepare each realized/recycled row without replacing its shared theme style.
                AutomationProperties.SetAutomationId(row, "WorkflowRun-" + entry.Run.Id);
                AutomationProperties.SetName(row, entry.Label);
                AutomationProperties.SetHelpText(row, entry.Run.ConversationId);
            }
        }

        protected override void ClearContainerForItemOverride(DependencyObject element, object item)
        {
            if (element is ListBoxItem row)
            {
                row.ClearValue(AutomationProperties.AutomationIdProperty);
                row.ClearValue(AutomationProperties.NameProperty);
                row.ClearValue(AutomationProperties.HelpTextProperty);
            }
            base.ClearContainerForItemOverride(element, item);
        }
    }
    private sealed record ActivityCall(string Label, string Origin, CerebralEvent? Event = null);
    private sealed record ScrollPosition(double Offset, bool FollowEnd);
    private sealed record ActivityView(string Key, Expander Expander, ScrollViewer Scroller, List<CerebralEvent> Items)
    {
        public bool FollowEnd = true;
        public bool Restoring = true;
        public double RestoreOffset;
        public long InputRevision;
        public bool ReviewHistory;
        public Button? ReviewButton;
        public Button? SummaryButton;
        public Dictionary<string, ActivityRowView> Rows { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ActivitySectionView> Sections { get; } = new(StringComparer.Ordinal);
    }
}
