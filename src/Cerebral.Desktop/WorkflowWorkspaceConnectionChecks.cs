using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Deterministic WPF dispatcher checks with local delegates; no service or network execution.</summary>
public static class WorkflowWorkspaceConnectionChecks
{
    /// <returns>The number of successful assertions; a failed assertion throws.</returns>
    public static async Task<int> VerifyAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF application dispatcher is required.");
        dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run the connection checks from an active WPF dispatcher callback.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var checks = 0; var oldCalls = 0; var currentCalls = 0; var latestCalls = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); checks++; }
        var request = new CommandRequest("fixture.bound-command", CerebralJson.Element(new { }));
        Func<CommandRequest, CancellationToken, Task<CommandResult>> selected = (_, _) =>
        {
            Interlocked.Increment(ref oldCalls);
            Check(dispatcher.CheckAccess(), "Client selection and invocation must occur on the WPF dispatcher.");
            return Task.FromResult(CommandResult.Ok(new { client = "old" }));
        };
        using var workspace = new WorkflowWorkspace((input, token) => selected(input, token), () => Array.Empty<AgentProfile>(), (_, _) => { });
        var oldBinding = workspace.BoundCommand();

        // Block the dispatcher only until the worker has RECEIVED the pending dispatcher task.
        // It never waits for that task here. Reset therefore deterministically precedes its dispatch.
        using (var queued = new ManualResetEventSlim())
        {
            var captured = new TaskCompletionSource<Task<CommandResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launcher = Task.Run(() =>
            {
                try { captured.TrySetResult(oldBinding(request, deadline.Token)); }
                catch (Exception error) { captured.TrySetException(error); }
                finally { queued.Set(); }
            }, deadline.Token);
            Check(queued.Wait(TimeSpan.FromSeconds(3), deadline.Token), "The background call must return a pending dispatcher operation without waiting for the UI thread.");
            Check(oldCalls == 0, "The queued old binding must not invoke the mutable client delegate before UI dispatch.");
            workspace.Reset();
            selected = (_, _) =>
            {
                currentCalls++;
                return Task.FromResult(CommandResult.Ok(new { client = "current" }));
            };
            var pending = await captured.Task.WaitAsync(deadline.Token);
            await launcher.WaitAsync(deadline.Token);
            var rejected = false;
            try { await pending.WaitAsync(deadline.Token); }
            catch (InvalidOperationException error) when (error.Message.Contains("workflow connection changed", StringComparison.Ordinal)) { rejected = true; }
            Check(rejected && oldCalls == 0 && currentCalls == 0,
                "Reset before dispatch must reject the stale binding without invoking either the old or replacement client delegate.");
        }

        var enteredCurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentResult = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentOnDispatcher = false;
        selected = (_, token) =>
        {
            currentCalls++; currentOnDispatcher = dispatcher.CheckAccess(); enteredCurrent.TrySetResult();
            return currentResult.Task.WaitAsync(token);
        };
        var freshBinding = workspace.BoundCommand();
        var freshCall = Task.Run(() => freshBinding(request, deadline.Token), deadline.Token);
        await enteredCurrent.Task.WaitAsync(deadline.Token);
        Check(dispatcher.CheckAccess() && currentOnDispatcher && currentCalls == 1 && !freshCall.IsCompleted,
            "A fresh background binding must atomically select and invoke the current client on the dispatcher before awaiting its result.");
        workspace.Reset();
        selected = (_, _) =>
        {
            latestCalls++;
            Check(dispatcher.CheckAccess(), "The latest client delegate must execute on the same UI dispatcher.");
            return Task.FromResult(CommandResult.Ok(new { client = "latest" }));
        };
        currentResult.SetResult(CommandResult.Ok(new { client = "current" }));
        var completedCurrent = await freshCall.WaitAsync(deadline.Token);
        Check(completedCurrent.Success && completedCurrent.Data.GetProperty("client").GetString() == "current" && latestCalls == 0,
            "A client already invoked before Reset owns its pending result; replacing the selected delegate must not replay or redirect it.");
        var latest = await workspace.BoundCommand()(request, deadline.Token).WaitAsync(deadline.Token);
        Check(latest.Success && latest.Data.GetProperty("client").GetString() == "latest" && latestCalls == 1,
            "A new post-reset binding must use the replacement client exactly once.");

        var failRecent = true; var recentReads = 0; var reports = new List<(string Message, bool Error)>();
        TaskCompletionSource<CommandResult>? delayedRecent = null;
        using var refreshWorkspace = new WorkflowWorkspace((input, _) =>
        {
            Check(dispatcher.CheckAccess() && input.Operation == "jobs.workflow.recent", "A connection refresh must make its expected recent-work query on the dispatcher.");
            recentReads++;
            if (delayedRecent is { } delayed) return delayed.Task;
            return Task.FromResult(failRecent ? CommandResult.Fail("Fixture recent-work query failed.") :
                CommandResult.Ok(new WorkflowRecentPage([
                    new("fixture-run", "fixture-conversation", "fixture-profile", "Recovered work", "fixture.definition",
                        WorkflowStatus.Completed, 1, DateTimeOffset.UnixEpoch)
                ], 1, null)));
        }, () => Array.Empty<AgentProfile>(), (message, error) => reports.Add((message, error)));
        await refreshWorkspace.RefreshFromConnectionAsync().WaitAsync(deadline.Token);
        Check(recentReads == 1 && reports is [{ Message: "Fixture recent-work query failed.", Error: true }],
            "A failed optional workflow query must report its local failure without escaping the connection-health callback.");
        var localState = Find<TextBlock>(refreshWorkspace, "WorkflowState")
            ?? throw new InvalidOperationException("The Work fixture has no public state control.");
        Check(localState.Text == "Fixture recent-work query failed." && ReferenceEquals(localState.Foreground, Ui.ErrorText),
            "The local failed read remains visible as a real error until a successful recovery.");
        failRecent = false;
        await refreshWorkspace.RefreshFromConnectionAsync().WaitAsync(deadline.Token);
        var recent = Find<ListBox>(refreshWorkspace, "WorkflowRecent");
        var recentLabel = recent?.Items.Count == 1 ? recent.Items[0]?.GetType().GetProperty("Label")?.GetValue(recent.Items[0]) as string : null;
        Check(recentReads == 2 && reports.Count == 1 && recentLabel == "Recovered work\nfixture-profile · Completed",
            "A later successful refresh must populate the native list with the workflow title, owning agent and status.");
        Check(localState.Text == "Open saved work or start a workflow provided by an installed module." &&
              ReferenceEquals(localState.Foreground, Ui.Muted),
            "A successful current-owner list refresh clears the idle connection error and error treatment.");

        delayedRecent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingRecovery = refreshWorkspace.RefreshFromConnectionAsync();
        var priorRecovery = delayedRecent;
        delayedRecent = null; failRecent = true;
        await refreshWorkspace.RefreshFromConnectionAsync().WaitAsync(deadline.Token);
        priorRecovery.SetResult(CommandResult.Ok(new WorkflowRecentPage([], 1, null)));
        await pendingRecovery.WaitAsync(deadline.Token);
        Check(localState.Text == "Fixture recent-work query failed." && ReferenceEquals(localState.Foreground, Ui.ErrorText) &&
              reports.Count == 2,
            "An older successful list read cannot erase a newer local failure that arrived while it was pending.");

        delayedRecent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var priorOwnerRead = refreshWorkspace.RefreshFromConnectionAsync();
        var priorOwnerResult = delayedRecent;
        refreshWorkspace.Reset();
        delayedRecent = null;
        priorOwnerResult.SetResult(CommandResult.Ok(new WorkflowRecentPage([
            new("fixture-stale", "fixture-old-owner", "fixture-profile", "Stale owner work", "fixture.definition",
                WorkflowStatus.Completed, 1, DateTimeOffset.UnixEpoch)
        ], 1, null)));
        await priorOwnerRead.WaitAsync(deadline.Token);
        Check(recent is { Items.Count: 0 } && localState.Text == "Reconnect to read saved work.",
            "A pre-reset list result cannot populate the replacement owner or clear its reconnect state.");
        foreach (var transportFailure in new[] { false, true })
        {
            delayedRecent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleFailureRead = refreshWorkspace.RefreshFromConnectionAsync();
            var staleFailureResult = delayedRecent;
            refreshWorkspace.Reset();
            delayedRecent = null;
            if (transportFailure) staleFailureResult.SetException(new IOException("Fixture old-owner transport failure."));
            else staleFailureResult.SetResult(CommandResult.Fail("Fixture old-owner failed reply."));
            await staleFailureRead.WaitAsync(deadline.Token);
            Check(recent is { Items.Count: 0 } && localState.Text == "Reconnect to read saved work." &&
                  ReferenceEquals(localState.Foreground, Ui.Muted) && reports.Count == 2,
                "A pre-reset failed reply or transport exception cannot replace the new owner's state, style or diagnostic.");
        }
        var monitorCalls = 0;
        using (var monitor = new WorkflowWorkspace((_, _) =>
        {
            monitorCalls++;
            return Task.FromResult(CommandResult.Fail("A monitoring mutation reached transport."));
        }, () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true))
        {
            monitor.UseMonitoringOnly();
            monitor.BeginConversation("fixture-monitor");
            try
            {
                await monitor.CancelConversationAsync().WaitAsync(deadline.Token);
                throw new InvalidOperationException("A read-only team presenter accepted a cancellation.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("read-only monitoring", StringComparison.Ordinal))
            { checks++; }
            Check(monitorCalls == 0,
                "A read-only team presenter rejects mutation before invoking its command delegate.");
        }
        var now = DateTimeOffset.UtcNow;
        var inspectionEvents = new CerebralEvent[]
        {
            new("inspection-original", "inspection-chat", "inspection-run", 1, CerebralEventTypes.Message,
                new(EventActorKind.User), [new TextContentPart("Inspect the run.")],
                new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now),
            new("inspection-tool", "inspection-chat", "inspection-run", 2, CerebralEventTypes.ToolCall,
                new(EventActorKind.Agent, "fixture-agent"), [],
                new(ModelInclusion.Never, HumanVisibility.Activity),
                new(EventSourceKind.Service, "cerebral.engine"), now,
                CerebralJson.Element(new ToolCallData("inspection-tool", "cerebral.host:tool",
                    CerebralJson.Element(new { name = "file.read", arguments = new { path = "Example/page.html" } }))))
        };
        var inspectionRun = new WorkflowRun
        {
            Id = "inspection-run", Revision = 1, Status = WorkflowStatus.Completed,
            Submission = new("inspection-run", "inspection-chat", "fixture-agent", "fixture:inspection", "1.0.0",
                "Inspect the run.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            CreatedAt = now, UpdatedAt = now
        };
        var inspectionReads = 0;
        Task<CommandResult> InspectionRead(CommandRequest input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); inspectionReads++;
            return Task.FromResult(input.Operation switch
            {
                "jobs.workflow.get" => CommandResult.Ok(inspectionRun),
                "jobs.workflow.events.previous" => CommandResult.Ok(new
                { events = inspectionEvents, snapshotSequence = 2L, nextBeforeSequence = (long?)null }),
                _ => throw new InvalidOperationException("Unexpected inspection fixture command: " + input.Operation)
            });
        }
        using (var inspected = new WorkflowWorkspace(InspectionRead, () => [], (_, _) => { }, embedded: true))
        using (var reopened = new WorkflowWorkspace(InspectionRead, () => [], (_, _) => { }, embedded: true))
        {
            inspected.UseMonitoringOnly(); reopened.UseMonitoringOnly();
            inspected.BeginConversation("inspection-chat"); reopened.BeginConversation("inspection-chat");
            await inspected.OpenRunSnapshotAsync("inspection-run", deadline.Token);
            var initial = inspected.CaptureInspectionState() ?? throw new InvalidOperationException("The inspected run has no view state.");
            Check(initial.Activities.Count == 1 && initial.Activities[0].Expanded,
                "An exact monitored run exposes one visible activity section for local inspection.");
            var collapsed = initial with
            {
                CollapsedActivityIds = [initial.Activities[0].Key],
                Activities = [initial.Activities[0] with { Expanded = false }]
            };
            await reopened.OpenRunSnapshotAsync("inspection-run", deadline.Token);
            Check(await reopened.RestoreInspectionStateAsync(collapsed, deadline.Token),
                "A reopened exact run restores bounded local inspection state.");
            var restored = reopened.CaptureInspectionState();
            Check(restored is { Activities.Count: 1 } && !restored.Activities[0].Expanded &&
                  restored.CollapsedActivityIds.Contains(initial.Activities[0].Key),
                "Activity expansion remains collapsed after presenter eviction and rehydration.");
            try
            {
                await reopened.RestoreInspectionStateAsync(collapsed with { RunId = "foreign-run" }, deadline.Token);
                throw new InvalidOperationException("Foreign inspection state was accepted.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("does not match", StringComparison.Ordinal))
            { checks++; }
            Check(inspectionReads == 6,
                "State capture and restoration add no reads beyond each presenter's exact-run open and event page.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true, workflowConnectionChecks = checks,
            evidence = "deterministic WPF dispatcher and native control checks with local command delegates",
            serviceCommandsExecuted = false, networkUsed = false, windowShown = false,
            staleDelegateCalls = oldCalls, acceptedCurrentCalls = currentCalls, latestCalls, recentReads
        }));
        return checks;
    }

    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        if (root is T control && AutomationProperties.GetAutomationId(control) == id) return control;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i), id) is { } found) return found;
        return null;
    }
}
