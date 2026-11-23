using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private const int WorkQueuePageSize = 40;
    private enum WorkSource { Workflow, Legacy }
    private sealed record WorkQueueRow(WorkSource Origin, string Id, string Title, string ProfileId,
        string Kind, string Status, DateTimeOffset UpdatedAt)
    {
        public string Key => Origin + ":" + Id;
        public string Source => Origin == WorkSource.Workflow ? "Workflow" : "Previous job";
    }
    private readonly TextBlock workQueueStatus = Ui.Text("Reading unfinished work…", 12, Ui.Muted);
    private Button? moreWorkflows;
    private Button? moreLegacyJobs;
    private Button? workDetails, workStop;
    private long workflowAfterRow;
    private long legacyAfterRow;
    private long? workflowNextRow;
    private long? legacyNextRow;
    private long queueConnectionEpoch;
    private long queueRefreshVersion;
    // A diagnostic delegate uses the same request contract without starting a service or writing settings.
    private Func<CommandRequest, CancellationToken, Task<CommandResult>>? queueCommandOverride;

    private FrameworkElement QueueContent()
    {
        AutomationProperties.SetAutomationId(jobs, "ConfigurationWorkQueue");
        jobs.SelectionChanged += (_, _) => RefreshWorkSelectionActions();
        ServiceStatusChanged += (_, _) => RefreshWorkSelectionActions();
        var root = new DockPanel();
        var paging = Ui.Bar();
        moreWorkflows = Ui.Button("More workflows", () => Run(() => RefreshQueuePageAsync(workflowNextRow ?? workflowAfterRow, legacyAfterRow)));
        moreLegacyJobs = Ui.Button("More previous jobs", () => Run(() => RefreshQueuePageAsync(workflowAfterRow, legacyNextRow ?? legacyAfterRow)));
        AutomationProperties.SetAutomationId(moreWorkflows, "QueueMoreWorkflows");
        AutomationProperties.SetAutomationId(moreLegacyJobs, "QueueMoreLegacyJobs");
        moreWorkflows.IsEnabled = moreLegacyJobs.IsEnabled = false;
        paging.Children.Add(Ui.Button("First page", () => Run(() => RefreshQueuePageAsync(0, 0))));
        paging.Children.Add(moreWorkflows); paging.Children.Add(moreLegacyJobs); paging.Children.Add(workQueueStatus);
        DockPanel.SetDock(paging, Dock.Bottom); root.Children.Add(paging); root.Children.Add(jobs);
        return root;
    }

    private void RefreshWorkSelectionActions()
    {
        var selected = IsConnected && jobs.SelectedItem is WorkQueueRow;
        if (workDetails is not null) { workDetails.IsEnabled = selected; workDetails.ToolTip = selected ? null : "Select an unfinished work item to inspect it."; }
        if (workStop is not null) { workStop.IsEnabled = selected; workStop.ToolTip = selected ? null : "Select an unfinished work item to stop it."; }
    }

    private Func<CommandRequest, CancellationToken, Task<CommandResult>> QueueDispatch()
    {
        if (queueCommandOverride is not null) return queueCommandOverride;
        // One selection/action keeps the exact client it began with. A reconnect cannot redirect
        // its second command onto another service after an awaited read.
        var client = Client;
        return (request, token) => client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token);
    }

    private static async Task<T> QueueRead<T>(Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch,
        string operation, object arguments, CancellationToken token)
    {
        var result = await dispatch(new(operation, CerebralJson.Element(arguments)), token);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "The service rejected " + operation + ".");
        return CerebralJson.Read<T>(result.Data);
    }

    private Task RefreshJobs() => RefreshQueuePageAsync(workflowAfterRow, legacyAfterRow);

    private async Task RefreshQueuePageAsync(long workflowCursor, long legacyCursor)
    {
        var version = ++queueRefreshVersion; var connection = queueConnectionEpoch;
        var selected = (jobs.SelectedItem as WorkQueueRow)?.Key;
        var dispatch = QueueDispatch();
        var workflowRead = QueueRead<WorkflowActiveSummaryPage>(dispatch, "jobs.workflow.active-summaries",
            new { limit = WorkQueuePageSize, afterRow = workflowCursor }, Lifetime.Token);
        var legacyRead = QueueRead<LegacyJobSummaryPage>(dispatch, "queue.active-summaries",
            new { limit = WorkQueuePageSize, afterRow = legacyCursor }, Lifetime.Token);
        await Task.WhenAll(workflowRead, legacyRead);
        var workflow = await workflowRead; var legacy = await legacyRead;
        if (version != queueRefreshVersion || connection != queueConnectionEpoch) return;
        ValidateQueuePage(workflow, workflowCursor); ValidateQueuePage(legacy, legacyCursor);
        var rows = workflow.Runs.Select(WorkflowQueueRow).Concat(legacy.Jobs.Select(LegacyQueueRow)).ToList();
        jobs.ItemsSource = rows; jobs.SelectedItem = rows.FirstOrDefault(x => x.Key == selected);
        RefreshWorkSelectionActions();
        workflowAfterRow = workflowCursor; legacyAfterRow = legacyCursor;
        workflowNextRow = workflow.NextAfterRow; legacyNextRow = legacy.NextAfterRow;
        if (moreWorkflows is not null) moreWorkflows.IsEnabled = workflowNextRow is not null;
        if (moreLegacyJobs is not null) moreLegacyJobs.IsEnabled = legacyNextRow is not null;
        workQueueStatus.Text = $"{workflow.Runs.Count} workflows · {legacy.Jobs.Count} previous jobs on these pages" +
            (workflowCursor != 0 || legacyCursor != 0 ? " · First page returns to oldest unfinished work" : "");
    }

    private static WorkQueueRow WorkflowQueueRow(WorkflowRunSummary item) => new(WorkSource.Workflow, item.Id,
        item.Title, item.ProfileId, item.DefinitionId, item.Status.ToString(), item.UpdatedAt);
    private static WorkQueueRow LegacyQueueRow(LegacyJobSummary item) => new(WorkSource.Legacy, item.Id,
        item.Title, item.ProfileId, item.Kind, item.Status, item.UpdatedAt ?? item.CreatedAt);

    private static void ValidateQueuePage(WorkflowActiveSummaryPage page, long cursor)
    {
        if (page.Runs is null || page.Runs.Count > WorkQueuePageSize ||
            page.Runs.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != page.Runs.Count ||
            page.Runs.Any(x => string.IsNullOrWhiteSpace(x.Id) || !Enum.IsDefined(x.Status) ||
                x.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled) ||
            page.NextAfterRow is { } next && (next <= cursor || page.Runs.Count == 0))
            throw new InvalidDataException("The service returned an invalid or oversized active workflow page.");
    }

    private static void ValidateQueuePage(LegacyJobSummaryPage page, long cursor)
    {
        if (page.Jobs is null || page.Jobs.Count > WorkQueuePageSize ||
            page.Jobs.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != page.Jobs.Count ||
            page.Jobs.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Status is not
                ("queued" or "running" or "waiting-human" or "waiting-helper" or "remote-waiting" or "remote-unconfirmed")) ||
            page.NextAfterRow is { } next && (next <= cursor || page.Jobs.Count == 0))
            throw new InvalidDataException("The service returned an invalid or oversized previous-job page.");
    }

    private static string WorkOperation(WorkQueueRow row, string workflow, string legacy) =>
        row.Origin == WorkSource.Workflow ? workflow : legacy;

    private async Task JobDetails()
    {
        var row = Selected<WorkQueueRow>(jobs); var connection = queueConnectionEpoch;
        var dispatch = QueueDispatch();
        string detail;
        if (row.Origin == WorkSource.Workflow)
        {
            var run = await QueueRead<WorkflowRun>(dispatch, "jobs.workflow.get", new { id = row.Id }, Lifetime.Token);
            if (run.Id != row.Id) throw new InvalidDataException("The service returned a different workflow identity.");
            detail = $"Workflow · {run.Status}\n{run.Submission.UserText}\n\nRun: {run.Id}\nConversation: {run.Submission.ConversationId}\nAgent: {run.Submission.ProfileId}\nModule: {run.Submission.DefinitionId}\nRevision: {run.Revision}\n" +
                $"Waiting: {run.WaitingReason}\nError: {run.Error}\nQuestions: {run.Questions?.Fields.Count ?? 0}\n" +
                $"Current command: {run.Effect?.CommandId}\nCommand state: {run.Effect?.State}\nReceipt: {run.Effect?.ProviderReceiptId}";
        }
        else
        {
            var job = await QueueRead<AgentJob>(dispatch, "jobs.get", new { id = row.Id }, Lifetime.Token);
            if (job.Id != row.Id) throw new InvalidDataException("The service returned a different previous-job identity.");
            detail = LegacyWorkDetails(row, job);
        }
        if (connection != queueConnectionEpoch) throw new InvalidOperationException("The connection changed. Inspect the work item from the current service.");
        Ui.ShowText(this, row.Source + " details", detail[..Math.Min(detail.Length, 16_384)]);
    }

    private static string LegacyWorkDetails(WorkQueueRow row, AgentJob job) =>
        $"Previous job · {job.Status}\n{job.VisiblePrompt ?? row.Title}\n\nJob: {job.Id}\nConversation: {job.SessionId}\nAgent: {job.ProfileId}\nKind: {job.Kind}\nWaiting: {job.WaitingReason}\nError: {job.Error}";

    private async Task CancelJob()
    {
        var row = Selected<WorkQueueRow>(jobs); var connection = queueConnectionEpoch;
        var dispatch = QueueDispatch();
        try
        {
            var result = await dispatch(new(WorkOperation(row, "jobs.workflow.cancel", "jobs.cancel"), CerebralJson.Element(new { id = row.Id })), Lifetime.Token);
            if (!result.Success) throw new InvalidOperationException(result.Error ?? "Cancellation was rejected.");
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or TimeoutException)
        {
            SetStatus($"Stop for {row.Source.ToLowerInvariant()} {row.Id} was not confirmed. Refresh or inspect that same ID before retrying. " + error.Message, true);
            return;
        }
        if (connection != queueConnectionEpoch) { SetStatus("Stop was accepted by the previous connection."); return; }
        try { await RefreshJobs(); SetStatus("Stop requested for the selected " + row.Source.ToLowerInvariant() + "."); }
        catch (Exception error) { SetStatus("Stop was accepted; the queue display could not refresh. " + error.Message, true); }
    }

    private async Task ReconcileJob()
    {
        var row = Selected<WorkQueueRow>(jobs); var connection = queueConnectionEpoch;
        var dispatch = QueueDispatch();
        if (row.Origin == WorkSource.Workflow)
        {
            var run = await QueueRead<WorkflowRun>(dispatch, "jobs.workflow.get", new { id = row.Id }, Lifetime.Token);
            if (run.Id != row.Id) throw new InvalidDataException("The service returned a different workflow identity.");
            if (connection != queueConnectionEpoch) throw new InvalidOperationException("Connection changed. Select work from the current service before checking its outcome.");
            if (run.Effect?.State != WorkflowEffectState.Unknown)
            {
                await RefreshJobs();
                SetStatus("This workflow has no uncertain command to reconcile. Questions and paused-work controls are available in Cerebral.");
                return;
            }
        }
        try
        {
            var result = await dispatch(new(WorkOperation(row, "jobs.workflow.reconcile", "jobs.reconcile"), CerebralJson.Element(new { id = row.Id })), Lifetime.Token);
            if (!result.Success) throw new InvalidOperationException(result.Error ?? "Outcome checking was rejected.");
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or TimeoutException)
        {
            SetStatus("Outcome checking was not confirmed. Refresh the same work item before another action. " + error.Message, true); return;
        }
        if (connection != queueConnectionEpoch) { SetStatus("The previous connection accepted the outcome check."); return; }
        try { await RefreshJobs(); SetStatus("Outcome check requested for the selected " + row.Source.ToLowerInvariant() + "."); }
        catch (Exception error) { SetStatus("The outcome check was accepted; the queue display could not refresh. " + error.Message, true); }
    }

    private async Task ShowWorkflowHistory()
    {
        var connection = queueConnectionEpoch;
        var page = await QueueRead<WorkflowRecentPage>(QueueDispatch(), "jobs.workflow.recent", new { limit = WorkQueuePageSize }, Lifetime.Token);
        if (connection != queueConnectionEpoch) return;
        if (page.Runs is null || page.Runs.Count > WorkQueuePageSize) throw new InvalidDataException("The workflow history page exceeded its requested bound.");
        Ui.ShowText(this, "Recent workflow outcomes", string.Join("\n\n", page.Runs.Select(x =>
            $"{x.Status} · {x.Title}\nAgent: {x.ProfileId} · Module: {x.DefinitionId}\nRun: {x.Id}")) +
            (page.NextBeforeRow is null ? "" : "\n\nOlder work remains in Cerebral's paged Work view."));
    }

    protected override void ConnectionChanged()
    {
        InstructionsConnectionChanged();
        queueConnectionEpoch++; queueRefreshVersion++;
        InvalidateModelCapacity();
        CloseDesktopPluginConfiguration();
        workflowAfterRow = legacyAfterRow = 0; workflowNextRow = legacyNextRow = null;
        jobs.ItemsSource = null;
        RefreshWorkSelectionActions();
        if (moreWorkflows is not null) moreWorkflows.IsEnabled = false;
        if (moreLegacyJobs is not null) moreLegacyJobs.IsEnabled = false;
        workQueueStatus.Text = "Connect to read unfinished work.";
    }
}
