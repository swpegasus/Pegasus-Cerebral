using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    /// <summary>Deterministic native queue checks; no Worker, model, network or persistent settings.</summary>
    public async Task<int> CheckWorkflowQueueUiAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run queue checks from an active WPF dispatcher callback.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var originalDispatch = queueCommandOverride;
        var checks = 0; var calls = new List<CommandRequest>();
        var now = DateTimeOffset.UnixEpoch;
        var workflow = new WorkflowRunSummary("same-id", "chat", "lead", "Durable request", "pegasus.conversation:chat", WorkflowStatus.Running, 1, now);
        var legacy = new LegacyJobSummary("same-id", "previous-chat", "old-agent", "Previous request", "agent", "running", now, null);
        WorkflowActiveSummaryPage workflows = new([workflow], 100);
        LegacyJobSummaryPage previous = new([legacy], 200);
        var effectState = WorkflowEffectState.Succeeded;
        var throwCancellation = false; var throwQueueAfterCancel = false; var cancelled = false;
        TaskCompletionSource<CommandResult>? slowWorkflow = null;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        WorkflowRun CurrentRun() => new()
        {
            Id = "same-id", Submission = new("same-id", "chat", "lead", "pegasus.conversation:chat", "1.0.0", "VISIBLE",
                "PRIVATE ASSIGNMENT", WorkflowContract.EmptyObject, WorkflowContract.EmptyObject), Revision = 1, Status = WorkflowStatus.Running,
            Effect = new("effect", "cerebral.host:tool", 1, WorkflowContract.EmptyObject, WorkflowReplayPolicy.Never, effectState),
            CreatedAt = now, UpdatedAt = now
        };
        queueCommandOverride = (request, token) =>
        {
            token.ThrowIfCancellationRequested(); calls.Add(request);
            Check(request.ProfileId is null, "Configuration queue commands must use the owner route without a profile impersonation.");
            if (request.Operation is "jobs.workflow.active-summaries" or "queue.active-summaries")
            {
                Check(request.Arguments.GetProperty("limit").GetInt32() == WorkQueuePageSize, "Each active source query must request one bounded summary page.");
                if (throwQueueAfterCancel && cancelled) throw new IOException("Fixture queue refresh failed.");
                if (request.Operation == "queue.active-summaries") return Task.FromResult(CommandResult.Ok(previous));
                if (slowWorkflow is not null) return slowWorkflow.Task.WaitAsync(token);
                return Task.FromResult(CommandResult.Ok(workflows));
            }
            if (request.Operation is "jobs.workflow.cancel" or "jobs.cancel")
            {
                cancelled = true;
                if (throwCancellation) throw new IOException("Fixture transport disconnect.");
                return Task.FromResult(CommandResult.Ok(new { id = request.Arguments.GetProperty("id").GetString() }));
            }
            if (request.Operation == "jobs.workflow.get") return Task.FromResult(CommandResult.Ok(CurrentRun()));
            if (request.Operation is "jobs.workflow.reconcile" or "jobs.reconcile")
                return Task.FromResult(CommandResult.Ok(new { id = "same-id" }));
            throw new InvalidOperationException("Unexpected queue route: " + request.Operation);
        };
        Task? outstanding = null;
        try
        {
            await RefreshQueuePageAsync(0, 0).WaitAsync(deadline.Token);
            var rows = ((IEnumerable<WorkQueueRow>)jobs.ItemsSource).ToList();
            Check(rows.Count == 2 && rows.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() == 2 &&
                rows.Select(x => x.Source).SequenceEqual(["Workflow", "Previous job"]),
                "Identical IDs from the two engines must remain distinct source-labeled selections.");
            Check(AutomationProperties.GetAutomationId(jobs) == "ConfigurationWorkQueue" &&
                moreWorkflows?.IsEnabled == true && moreLegacyJobs?.IsEnabled == true,
                "The actual native grid must expose independent continuation controls.");
            Check(!LegacyWorkDetails(rows.Single(x => x.Origin == WorkSource.Legacy), new AgentJob { Id = "same-id", Prompt = "PRIVATE BACKEND ASSIGNMENT", VisiblePrompt = null }).Contains("PRIVATE BACKEND ASSIGNMENT", StringComparison.Ordinal),
                "Previous-job details must use the visible summary fallback, never the private backend prompt.");
            Check(calls.Count(x => x.Operation is "jobs.workflow.get" or "jobs.get" or "queue.list" or "jobs.list") == 0,
                "Queue refresh must read only summaries, never full execution histories or private assignments.");

            jobs.SelectedItem = rows.Single(x => x.Origin == WorkSource.Legacy);
            await RefreshJobs().WaitAsync(deadline.Token);
            Check(jobs.SelectedItem is WorkQueueRow { Origin: WorkSource.Legacy, Id: "same-id" }, "Refresh must preserve source-qualified selection.");
            await CancelJob().WaitAsync(deadline.Token);
            Check(calls.Last(x => x.Operation.EndsWith(".cancel", StringComparison.Ordinal)).Operation == "jobs.cancel",
                "Stopping a previous job must target only the previous job cancellation route.");
            jobs.SelectedItem = ((IEnumerable<WorkQueueRow>)jobs.ItemsSource).Single(x => x.Origin == WorkSource.Workflow);
            await CancelJob().WaitAsync(deadline.Token);
            Check(calls.Last(x => x.Operation.EndsWith(".cancel", StringComparison.Ordinal)).Operation == "jobs.workflow.cancel",
                "Stopping a durable workflow must target only its workflow cancellation route.");

            var reconciles = calls.Count(x => x.Operation.EndsWith(".reconcile", StringComparison.Ordinal));
            await ReconcileJob().WaitAsync(deadline.Token);
            Check(calls.Count(x => x.Operation.EndsWith(".reconcile", StringComparison.Ordinal)) == reconciles,
                "A normal workflow must not be reconciled or replayed merely because Check outcome was clicked.");
            effectState = WorkflowEffectState.Unknown;
            await ReconcileJob().WaitAsync(deadline.Token);
            Check(calls.Last(x => x.Operation.EndsWith(".reconcile", StringComparison.Ordinal)).Operation == "jobs.workflow.reconcile",
                "An exact current Unknown effect may use the explicit workflow reconcile route.");
            jobs.SelectedItem = ((IEnumerable<WorkQueueRow>)jobs.ItemsSource).Single(x => x.Origin == WorkSource.Legacy);
            await ReconcileJob().WaitAsync(deadline.Token);
            Check(calls.Last(x => x.Operation.EndsWith(".reconcile", StringComparison.Ordinal)).Operation == "jobs.reconcile",
                "A previous job keeps its own connection recovery route.");

            var beforeEvent = calls.Count;
            await OnEventAsync(new(1, "workflow.updated", "same-id", WorkflowContract.EmptyObject, now)).WaitAsync(deadline.Token);
            Check(calls.Count == beforeEvent + 2, "workflow.updated must refresh the two bounded queue projections.");
            var cursorRejected = false;
            try { await RefreshQueuePageAsync(100, 200).WaitAsync(deadline.Token); }
            catch (InvalidDataException) { cursorRejected = true; }
            Check(cursorRejected && workflowAfterRow == 0 && legacyAfterRow == 0 &&
                calls[^2].Arguments.GetProperty("afterRow").GetInt64() == 100 && calls[^1].Arguments.GetProperty("afterRow").GetInt64() == 200,
                "Independent cursors must pass unchanged; a nonadvancing continuation must preserve the prior pages.");
            workflows = new([workflow], 300); previous = new([legacy], 400);
            await RefreshQueuePageAsync(100, 200).WaitAsync(deadline.Token);
            Check(workflowAfterRow == 100 && legacyAfterRow == 200, "Valid pages must atomically adopt independent cursors.");
            var retainedItems = jobs.ItemsSource;
            workflows = new(Enumerable.Range(0, WorkQueuePageSize + 1).Select(i => workflow with { Id = "overflow-" + i }).ToArray(), null);
            var oversizedRejected = false;
            try { await RefreshJobs().WaitAsync(deadline.Token); }
            catch (InvalidDataException) { oversizedRejected = true; }
            Check(oversizedRejected && ReferenceEquals(retainedItems, jobs.ItemsSource), "An oversized page must be rejected without replacing the current native rows.");
            workflows = new([workflow], 300);

            jobs.SelectedItem = ((IEnumerable<WorkQueueRow>)jobs.ItemsSource).Single(x => x.Origin == WorkSource.Workflow);
            throwCancellation = true; var cancelCount = calls.Count(x => x.Operation.EndsWith(".cancel", StringComparison.Ordinal));
            await CancelJob().WaitAsync(deadline.Token);
            Check(calls.Count(x => x.Operation.EndsWith(".cancel", StringComparison.Ordinal)) == cancelCount + 1 &&
                Status.Text.Contains("not confirmed", StringComparison.Ordinal),
                "An uncertain Stop must report the exact existing work and must never automatically retry.");
            throwCancellation = false; cancelled = false; throwQueueAfterCancel = true;
            await CancelJob().WaitAsync(deadline.Token);
            Check(Status.Text.StartsWith("Stop was accepted", StringComparison.Ordinal), "An accepted cancellation must stay accepted when only queue refresh fails.");
            throwQueueAfterCancel = false;

            slowWorkflow = new(TaskCreationOptions.RunContinuationsAsynchronously);
            outstanding = RefreshQueuePageAsync(0, 0);
            Check(!outstanding.IsCompleted, "The stale-response fixture must hold a read across connection invalidation.");
            ConnectionChanged();
            slowWorkflow.SetResult(CommandResult.Ok(new WorkflowActiveSummaryPage([workflow], null)));
            await outstanding.WaitAsync(deadline.Token); outstanding = null;
            Check(jobs.ItemsSource is null && workflowAfterRow == 0 && legacyAfterRow == 0,
                "A response from the previous service must not repopulate the new connection's queue.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, configurationQueueChecks = checks,
                evidence = "deterministic native WPF grid and command delegates", serviceCommandsExecuted = false, networkUsed = false, windowShown = false }));
            return checks;
        }
        finally
        {
            deadline.Cancel();
            slowWorkflow?.TrySetCanceled();
            if (outstanding is not null) { try { await outstanding.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { } }
            queueCommandOverride = originalDispatch;
        }
    }
}
