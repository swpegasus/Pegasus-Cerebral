using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using Cerebral.Client;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly MainWorkAttention workAttention = new();
    private CompletionOwner? workAttentionOwner;
    private long workAttentionOwnerGeneration;
    private bool workAttentionActivating;
    private ContextMenu? workAttentionMenu;

    internal event Action<WorkAttentionTarget>? WorkAttentionRequested;

    private CompletionOwner WorkAttentionOwner()
    {
        var owner = CurrentCompletionOwner();
        if (!ReferenceEquals(owner, workAttentionOwner))
        {
            workAttentionOwner = owner;
            workAttentionOwnerGeneration++;
            workAttention.ResetView();
        }
        return owner;
    }

    private bool WorkAttentionOwnerCurrent(WorkAttentionTarget target)
    {
        var owner = WorkAttentionOwner();
        return !Lifetime.IsCancellationRequested && IsConnected && CompletionOwnerCurrent(owner) &&
            target.OwnerGeneration == workAttentionOwnerGeneration &&
            target.ConnectionIdentity == owner.Identity;
    }

    private void ObserveWorkAttentionSnapshot()
    {
        if (workAttentionActivating || Lifetime.IsCancellationRequested || !IsConnected || !IsNativeChat ||
            nativeChat?.Snapshot is not { MutationUncertain: false } snapshot ||
            snapshot.ConversationId != durableConversationId) return;
        var run = snapshot.Run;
        if (run is not null && (snapshot.RunId != run.Id || run.ConversationId != snapshot.ConversationId)) return;
        var owner = WorkAttentionOwner();
        if (!CompletionOwnerCurrent(owner) || string.IsNullOrWhiteSpace(owner.Identity)) return;
        var task = teamGraph is { Complete: true } graph && graph.SourceConversationId == snapshot.ConversationId &&
            run is not null && graph.SourceRunId == run.Id ? graph : null;
        var taskActive = task?.TaskStatus is CollaborationTaskStatus.Queued or CollaborationTaskStatus.Running or
            CollaborationTaskStatus.Waiting or CollaborationTaskStatus.Cancelling;
        var background = !IsVisible || !IsActive || WindowState == WindowState.Minimized ||
            mainWorkPanel?.IsExpanded != true || !pages.TryGetValue("chat", out var chatPage) ||
            !ReferenceEquals(Tabs.SelectedItem, chatPage);
        var target = workAttention.Observe(workAttentionOwnerGeneration, owner.Identity,
            snapshot.ConversationId, conversationSelectionVersion, run is null ? null : WorkAttentionRunState.From(run), background,
            completionEligible: !taskActive, taskId: task?.TaskId);
        if (target is not null)
        {
            try { WorkAttentionRequested?.Invoke(target); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { SetStatus("A work notification could not be displayed. Saved work was not changed.", true); }
        }
    }


    internal void ShowWorkAttention(IReadOnlyList<WorkAttentionTarget> targets, Action<WorkAttentionTarget> reviewed)
    {
        Dispatcher.VerifyAccess();
        if (Lifetime.IsCancellationRequested) return;
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(reviewed);
        if (targets.Count > 16) throw new ArgumentException("The pending notification list exceeds its bound.", nameof(targets));
        var captured = targets.ToArray();
        if (workAttentionMenu is { } prior) prior.IsOpen = false;
        var menu = new ContextMenu { Placement = PlacementMode.Bottom,
            PlacementTarget = compactActionsButton is { } actions ? actions : sessionState };
        AutomationProperties.SetAutomationId(menu, "WorkAttentionMenu");
        menu.Items.Add(new MenuItem { Header = "Review notifications", IsEnabled = false });
        if (captured.Length == 0)
            menu.Items.Add(new MenuItem { Header = "No pending work notifications.", IsEnabled = false });
        foreach (var original in captured)
        {
            var label = original.Kind == WorkAttentionKind.Question ? "Question needs input" :
                original.Status == WorkflowStatus.Completed ? "Work completed" : "Work needs review";
            static string Short(string value) => value.Length <= 16 ? value : value[..8] + "…" + value[^4..];
            var item = new MenuItem { Header = label + " · chat " + Short(original.MainConversationId) +
                " · run " + Short(original.RunId),
                ToolTip = "Original chat: " + original.MainConversationId + "\nOriginal run: " + original.RunId +
                    (original.QuestionId is null ? "" : "\nQuestion: " + original.QuestionId + " · revision " + original.QuestionRevision) };
            AutomationProperties.SetAutomationId(item, "WorkAttentionTarget");
            // A balloon click opens this neutral list. Only this explicit item
            // selection chooses its captured original target, never the current chat.
            item.Click += (_, _) => Run(async () =>
            {
                var result = await ActivateWorkAttentionAsync(original);
                if (result.Navigated) reviewed(original);
            });
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => { if (ReferenceEquals(workAttentionMenu, menu)) workAttentionMenu = null; };
        workAttentionMenu = menu;
        menu.IsOpen = true;
    }

    internal async Task<WorkAttentionActivationResult> ActivateWorkAttentionAsync(WorkAttentionTarget target)
    {
        Dispatcher.VerifyAccess();
        if (workAttentionActivating) return new(false, "Another notification is being checked. Current drafts are unchanged.");
        var selection = conversationSelectionVersion;
        var initialConversation = CurrentConversationId;
        var initialRun = nativeChat?.Snapshot?.RunId;
        bool Current() => WorkAttentionOwnerCurrent(target) && conversationSelectionVersion == selection;
        bool SameView() => IsNativeChat && durableConversationId == target.MainConversationId &&
            nativeChat?.Snapshot?.RunId == target.RunId;
        void SafeToSwitch()
        {
            EnsureConversationControlsSettled();
            if (NativeMutationPending || NativeMutationUncertain)
                throw new InvalidOperationException("Resolve the pending Main request before opening this notification. Its draft and request ID were kept.");
            if (SameView()) return;
            EnsureAttachmentNavigationAllowed();
            if (nativeChat?.HasUnsubmittedQuestionDraft == true || loadedQuestions.Any(question => question.Answer is null))
                throw new InvalidOperationException("Finish the current question draft before changing work. This notification has not discarded it.");
        }

        using var activation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        activation.CancelAfter(TimeSpan.FromSeconds(30));
        workAttentionActivating = true;
        WorkAttentionActivationResult result;
        try
        {
            result = await WorkAttentionActivation.ActivateAsync(target, Current, async (original, token) =>
            {
                SafeToSwitch();
                var response = await Client.CommandAsync("jobs.workflow.get", new { id = original.RunId }, cancellationToken: token);
                if (!Current() || !response.Success) return null;
                var run = CerebralJson.Read<WorkflowRun>(response.Data);
                if (original.TaskId is { } taskId)
                {
                    var task = await ReadTeamGraphAsync(taskId, original.MainConversationId, Client, token);
                    if (!Current() || !task.Complete || task.SourceRunId != original.RunId)
                        return null;
                }
                return WorkAttentionRunState.From(run);
            }, async (original, token) =>
            {
                SafeToSwitch();
                if (!Current()) return false;
                // Existing navigation owns composer persistence, attachments, uncertain
                // operations and authenticated dataset admission. Never create a chat.
                var wasEnabled = IsEnabled;
                // Freeze editing only during this user-requested selection. The
                // existing draft store saves the old composer before restoring the target.
                IsEnabled = false;
                try
                {
                    if (!SameView())
                    {
                        var selecting = SelectSession(original.MainConversationId, token);
                        selection = conversationSelectionVersion; // This call owns only its new selection generation.
                        await selecting;
                        if (!Current() || durableConversationId != original.MainConversationId || !IsNativeChat) return false;
                        if (nativeChat!.Snapshot?.RunId != original.RunId)
                        {
                            if (nativeChat.HasUnsubmittedQuestionDraft) return false;
                            await nativeChat.OpenRunSnapshotAsync(original.RunId, token);
                        }
                    }
                    else if (!nativeChat!.HasUnsubmittedQuestionDraft)
                        await nativeChat.InspectRunAsync(original.RunId, token);
                }
                finally { if (!Lifetime.IsCancellationRequested) IsEnabled = wasEnabled; }
                if (!Current() || nativeChat?.Snapshot?.Run is not { } displayed ||
                    WorkAttentionActivation.Refusal(original, WorkAttentionRunState.From(displayed)) is not null)
                    return false;

                SelectPage("chat");
                workPanelState.SetMainExpanded(true);
                if (mainWorkPanel is null) return false;
                changingWorkPanels = true;
                try { workPanelAccordion.Open(mainWorkPanel); }
                finally { changingWorkPanels = false; }
                UpdateWorkPanelRows(); RefreshPageButtons(); UpdateLayout();
                return nativeChat.RevealAttentionTarget(original.RunId, original.RunRevision,
                    original.QuestionId, original.QuestionRevision, original.QuestionFieldId);
            }, activation.Token);
        }
        catch (OperationCanceledException)
        {
            result = new(false, "Notification navigation was cancelled. No request was sent.");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // Transport/provider messages may contain unrelated data. Keep notification
            // feedback short and do not convert an unavailable target to the current chat.
            result = new(false, error is InvalidOperationException && error.Message.StartsWith("Finish the current question", StringComparison.Ordinal)
                ? error.Message
                : error is InvalidOperationException && error.Message.StartsWith("Resolve the pending Main", StringComparison.Ordinal)
                    ? error.Message
                    : "The original notification target is unavailable or cannot be opened safely. Current drafts and attachments were kept.");
        }
        finally
        {
            workAttentionActivating = false;
            if (initialConversation != CurrentConversationId || initialRun != nativeChat?.Snapshot?.RunId)
                workAttention.ResetView();
        }
        SetStatus(result.Message, !result.Navigated);
        return result;
    }
}
