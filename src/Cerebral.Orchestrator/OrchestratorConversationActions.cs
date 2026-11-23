using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    protected override void ValidateConnectionChange()
    {
        EnsureConversationControlsSettled(); EnsureAttachmentNavigationAllowed();
        if (nativeChat?.MutationPending == true || workflowWorkspace?.MutationPending == true)
            throw new InvalidOperationException("A request is awaiting acceptance. Wait for its receipt before switching service connections.");
        if (nativeChat?.Snapshot?.MutationUncertain == true || workflowWorkspace?.Snapshot?.MutationUncertain == true)
            throw new InvalidOperationException("A request is unconfirmed. Retry its retained request on the current connection before switching services.");
    }

    protected override void ConnectionChanged()
    {
        ValidateConnectionChange();
        CloseMainChatTiming(MainChatTimingEnd.ConnectionChanged);
        ResetSystemInferenceOwnership();
        // Invalidate an in-flight selection before an old service response can publish into the new connection.
        conversationSelectionVersion++;
        ResetTeamForChangedMain(ownerChanged: true);
        conversationHistory.Clear();
        durableNavigationVersion++;
        workflowWorkspace?.Reset();
        nativeChat?.Reset(); durableConversations.Clear(); durableConversationPage = null;
        exactWorkflowConversations.Clear(); exactWorkflowConversationPage = null;
        exactWorkflowNavigationConnectionIdentity = null;
        showingLegacyNavigation = readingOlderChats = showingAllLoadedChats = false;
        ResetTurnActivity();
        var initialSourceComposer = RetainInitialSourceComposerForConnection();
        if (!initialSourceComposer && CurrentConversationId is { } prior) RememberComposerDraft(prior, prompt.Text);
        session = null; BeginNativeChat(); ResetDesktopConversation(); leadProfile = null; profiles.Clear(); configuredModels.Clear(); activeJobs.Clear();
        pendingSubmission = null; liveJobId = null; loadedQuestions.Clear(); loadedQuestionCount = 0; ResetProjectKnowledge();
        conversations.ItemsSource = null; navigation = new(); navigationSessions.Clear();
        selectedNavigationId = CurrentConversationId is { } current ? NavigationIdForConversation(current) : null;
        filePath.Clear(); fileContent.Clear(); fileListing.Clear(); fileEntries.ItemsSource = null;
        openFileHash = null; openFilePath = null; openFileProfileId = null; openFileTruncated = true;
        toolList.ItemsSource = null; toolArguments.Text = "{}"; toolOutput.Clear();
        liveOutput.Clear(); liveDetails.Visibility = Visibility.Collapsed; liveLabel.Text = "";
        sessionHeading.Text = "New conversation"; ResetQuestionDesktopTextEntries(); questionPanel.Children.Clear(); interfaceRequests.Children.Clear();
        EnsureActivitySession(); RenderActivity(); RenderWelcome(); RenderNavigation(); UpdateSessionState();
        RememberCurrentConversationVisit();
        agentSummary.Text = "Connecting to Cerebral…";
    }

    // Stop remains available while a normal command is awaiting a result.
    private async void RequestConversationStop()
    {
        if (IsNativeChat)
        {
            if (stopRequested) return;
            stopRequested = true; UpdateNativeChatState();
            try { var stopped = await StopNativeChatAsync(Lifetime.Token); SetStatus(stopped.Message ?? stopped.State.ToString(), stopped.State != Cerebral.Desktop.Plugins.DesktopOperationState.Accepted); }
            catch (Exception error) { SetStatus(error.Message, true); }
            finally { stopRequested = false; UpdateNativeChatState(); }
            return;
        }
        if (stopRequested || session is null) return;
        var id = session.Id; stopRequested = true;
        if (stopButton is not null) stopButton.IsEnabled = false;
        try
        {
            SetStatus("Stopping this conversation's work…");
            var result = await Command("sessions.stop", new { id });
            await RefreshJobs();
            if (session?.Id == id)
            {
                var stoppedSession = await Get<TaskSession>("sessions.get", new { id });
                if (session?.Id != id) return;
                session = stoppedSession;
                await RefreshActivityAsync(); RenderSession(); await RefreshQuestions();
                liveOutput.Clear(); liveDetails.Visibility = Visibility.Collapsed; liveLabel.Text = "";
            }
            var pending = result.Data.TryGetProperty("pendingApplications", out var pendingApps) &&
                (pendingApps.ValueKind == System.Text.Json.JsonValueKind.Array ? pendingApps.GetArrayLength() > 0 :
                 pendingApps.ValueKind == System.Text.Json.JsonValueKind.Number && pendingApps.TryGetInt32(out var count) && count > 0);
            SetStatus(pending ? "Job cancellation requested. Some application stops are pending; see the interface requests." : "Stop requested for this conversation. Helpers and owned commands are included.");
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus("Stop failed: " + ex.Message, true); }
        finally { stopRequested = false; UpdateSessionState(); }
    }

}
