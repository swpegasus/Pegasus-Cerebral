using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Cerebral.Client;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record LocalDraftVisitState(FreshChatSelection Selection, int? ResponseSharePercent);
    private readonly ConversationVisitHistory<LocalDraftVisitState> conversationHistory = new();

    // A default-profile first send has no explicit path-preparation record. Its exact
    // accepted run still promotes the history visit without changing path-authority state.
    internal static bool HasConfirmedNativeRunForHistory(WorkflowConversationSnapshot? snapshot, string? conversationId,
        bool currentBinding) =>
        currentBinding && snapshot is { MutationUncertain: false, ConversationId: { } savedId,
            RunId: { } runId, Run: { } run } &&
        savedId == conversationId && run.Id == runId;

    private bool HasConfirmedCurrentNativeRun =>
        HasConfirmedNativeRunForHistory(nativeChat?.Snapshot, durableConversationId,
            freshChatDraft is not { } draft || FreshChatDraftBindingCurrent(draft));

    private bool IsUnsentLocalConversation => session is null && durableConversationId is not null &&
        !freshChatInitialAccepted && !HasConfirmedCurrentNativeRun &&
        freshChatDraft is { } draft && FreshChatDraftBindingCurrent(draft);

    private void RememberCurrentConversationVisit()
    {
        if (!IsConnected || CurrentConversationId is not { } id || string.IsNullOrWhiteSpace(ServiceConnectionIdentity)) return;
        if (session is null && !IsUnsentLocalConversation && !freshChatInitialAccepted &&
            !HasConfirmedCurrentNativeRun &&
            !durableConversations.ContainsKey(id)) return;
        if (composerDraftProtectedSelection is not null && prompt.Text.Length == 0) return;
        if (IsUnsentLocalConversation && (freshChatPreparation is not null || freshChatInitialDispatchAttempted)) return;
        LocalDraftVisitState? local = null;
        if (IsUnsentLocalConversation && freshChatDraft is { } draft)
        {
            local = new(new(draft.Selection.ProfileId, draft.Selection.ProfileRevision,
                draft.Selection.Grants.ToArray()), selectedResponseSharePercent);
            // Existing draft storage retains the complete composer text under the same local ID.
            RememberComposerDraft(id, prompt.Text);
        }
        conversationHistory.Record(id, ConversationProjectId, ServiceConnectionIdentity,
            local is not null, session is not null, local);
        UpdateNavigationActions();
    }

    private static bool ConfirmedConversationAbsent(Exception error, bool legacy) =>
        error is InvalidOperationException && (error.Message == "Conversation was not found." ||
            legacy && error.Message == "Session not found.");

    private enum ConversationHistoryNavigationState { Moved, Boundary, Unavailable, ConnectionChanged, Stale }
    private readonly record struct ConversationHistoryNavigationResult(ConversationHistoryNavigationState State,
        int SkippedUnavailable = 0);

    private async Task NavigateConversationHistoryAsync(bool back)
    {
        _ = await NavigateConversationHistoryCoreAsync(back);
    }

    private async Task<ConversationHistoryNavigationResult> NavigateConversationHistoryCoreAsync(bool back)
    {
        EnsureNativeNavigationAllowed();
        if (composerDraftProtectedSelection is not null && prompt.Text.Length == 0)
            throw new InvalidOperationException("Resolve the protected draft selection before using chat history.");
        if (IsUnsentLocalConversation && (freshChatPreparation is not null || freshChatInitialDispatchAttempted))
            throw new InvalidOperationException("This first request has retained preparation. Resolve or retry it before using chat history.");
        RequireSemanticAttachCurrent();
        RememberCurrentConversationVisit();
        var prior = conversationHistory.Current;
        var identity = ServiceConnectionIdentity;
        var expectedSelection = conversationSelectionVersion;
        var revision = conversationHistory.Revision;
        var skippedUnavailable = 0;
        foreach (var visit in conversationHistory.Candidates(back))
        {
            if (identity != ServiceConnectionIdentity || expectedSelection != conversationSelectionVersion)
                return new(ConversationHistoryNavigationState.Stale, skippedUnavailable);
            if (visit.ConnectionIdentity != identity)
            {
                SetStatus("Chat history belongs to an earlier connection. Open a chat from the sidebar.", true);
                return new(ConversationHistoryNavigationState.ConnectionChanged, skippedUnavailable);
            }
            try
            {
                if (visit.IsLocalDraft)
                {
                    RestoreLocalConversationVisit(visit);
                }
                else
                    await SelectSession(visit.ConversationId, Lifetime.Token, recordHistory: false,
                        historyLegacyRoute: visit.IsLegacy);
                if (identity != ServiceConnectionIdentity || CurrentConversationId != visit.ConversationId ||
                    conversationSelectionVersion != expectedSelection + 1)
                    return new(ConversationHistoryNavigationState.Stale, skippedUnavailable);
                if (!conversationHistory.MoveTo(visit.Sequence, revision))
                    return new(ConversationHistoryNavigationState.Stale, skippedUnavailable);
                showingLegacyNavigation = visit.IsLegacy;
                selectedNavigationId = NavigationIdForConversation(visit.ConversationId);
                RenderNavigation();
                return new(ConversationHistoryNavigationState.Moved, skippedUnavailable);
            }
            catch (Exception error)
            {
                // A failed read can advance SelectSession's version before it opens a target.
                // Rebind an untouched, never-dispatched local draft under its original ID.
                if (prior is { IsLocalDraft: true, LocalState: { } local } &&
                    CurrentConversationId == prior.ConversationId && session is null &&
                    identity == ServiceConnectionIdentity && conversationSelectionVersion == expectedSelection + 1 &&
                    !NativeMutationPending && !NativeMutationUncertain && freshChatPreparation is null &&
                    !freshChatInitialDispatchAttempted)
                {
                    try
                    {
                        NewConversationInProject(prior.ProjectId, local.Selection, prior.ConversationId,
                            recordHistory: false, responseSharePercent: local.ResponseSharePercent);
                        // A semantic attempt may continue to another candidate after this exact
                        // local repair. Keep its captured selection aligned with the restored ID.
                        AdvanceSemanticAttachNavigation(conversationSelectionVersion);
                        expectedSelection = conversationSelectionVersion;
                    }
                    catch (Exception repair) { throw new AggregateException(error, repair); }
                }
                if (!ConfirmedConversationAbsent(error, visit.IsLegacy) || CurrentConversationId == visit.ConversationId) throw;
                // Only an exact owner lookup saying the identity is gone removes a visit.
                if (!conversationHistory.RemoveUnavailable(visit.Sequence, revision))
                    return new(ConversationHistoryNavigationState.Stale, skippedUnavailable);
                skippedUnavailable++;
                revision = conversationHistory.Revision;
                expectedSelection = conversationSelectionVersion;
                UpdateNavigationActions();
            }
        }
        SetStatus("No available chat remains in that direction.");
        return new(skippedUnavailable == 0 ? ConversationHistoryNavigationState.Boundary :
            ConversationHistoryNavigationState.Unavailable, skippedUnavailable);
    }

    private void RestoreLocalConversationVisit(ConversationVisitHistory<LocalDraftVisitState>.Visit visit)
    {
        if (!visit.IsLocalDraft || visit.LocalState is not { } state)
            throw new InvalidOperationException("The local draft visit is incomplete.");
        NewConversationInProject(visit.ProjectId, state.Selection, visit.ConversationId,
            recordHistory: false, responseSharePercent: state.ResponseSharePercent);
    }

    private bool TryHandleConversationHistoryMouse(MouseButtonEventArgs e)
    {
        var back = e.ChangedButton == MouseButton.XButton1;
        if (!back && e.ChangedButton != MouseButton.XButton2) return false;
        if (e.OriginalSource is not DependencyObject source || !CanRouteConversationHistoryMouse(source, e.ChangedButton)) return false;
        e.Handled = true;
        Run(() => NavigateConversationHistoryAsync(back));
        return true;
    }

    private bool CanRouteConversationHistoryMouse(DependencyObject source, MouseButton button)
    {
        if (button is not (MouseButton.XButton1 or MouseButton.XButton2) ||
            FindVisualAncestor<HwndHost>(source) is not null || HasPluginBoundary(source) ||
            IsWithin(source, desktopWorkspaceTrees) || IsWithin(source, desktopPluginAccessories) ||
            IsWithin(source, desktopPluginActions) || activityRail is not null && IsWithin(source, activityRail) ||
            desktopPluginSidebarTabs.Any(tab => IsWithin(source, tab))) return false;
        return button == MouseButton.XButton1 ? conversationHistory.CanBack : conversationHistory.CanForward;
    }

    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null;
             current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static bool HasPluginBoundary(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null;
             current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
            if (current is FrameworkElement element &&
                System.Windows.Automation.AutomationProperties.GetAutomationId(element) is { } id &&
                (id.StartsWith("Plugin", StringComparison.Ordinal) ||
                 id.StartsWith("DesktopSidebarTab.", StringComparison.Ordinal))) return true;
        return false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null;
             current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
            if (current is T found) return found;
        return null;
    }
}
