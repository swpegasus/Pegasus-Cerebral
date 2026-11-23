using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;
using Cerebral.Engine.Workflows;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly AsyncLocal<MainWorkflowDefinitionInputLease?> mainDefinitionInput = new();

    private MainWorkflowDefinitionInputLease CaptureMainDefinitionInput(string requestId, JsonElement input, bool exact)
    {
        var binding = PluginBinding();
        var client = SemanticAttachClient();
        var connection = ServiceConnection;
        var source = knowledgeConnectionVersion;
        var identity = ServiceConnectionIdentity;
        var semanticAuthority = CaptureSemanticAttachAuthority();
        return new(requestId, input, () => !Lifetime.IsCancellationRequested &&
            binding == PluginBinding() && ReferenceEquals(client, SemanticAttachClient()) &&
            ReferenceEquals(connection, ServiceConnection) && source == knowledgeConnectionVersion &&
            identity == ServiceConnectionIdentity && (semanticAuthority?.Invoke() ?? true), exact);
    }

    private void RequireMainDefinitionInputCurrent(string requestId) => mainDefinitionInput.Value?.Require(requestId);

    private void CheckRetainedMainDefinitionInput(WorkflowConversationSubmission request) =>
        mainDefinitionInput.Value?.CheckRetained(request.RequestId, request.Input);

    private void ValidateRetainedMainDefinitionInput(WorkflowConversationSubmission request) =>
        mainDefinitionInput.Value?.ValidateRetained(request.RequestId, request.Input);

    private DesktopOperationResult DefinitionInputComposerOutcome(DesktopOperationResult result)
    {
        var lease = mainDefinitionInput.Value;
        if (lease is null || lease.Applied ||
            result.State is not (DesktopOperationState.Accepted or DesktopOperationState.Completed)) return result;
        // A plug-in may have performed its own action. Refuse only the claim that its result applied
        // this carrier to ordinary chat; never describe that refusal as proof of no effects.
        return new(DesktopOperationState.Rejected,
            "definitionInput was not applied to the selected workflow submission. A plug-in handled or held the message; its own actions may already have occurred.",
            lease.RequestId, result.JobId);
    }

    private WorkflowWorkspace? nativeChat;
    private string? durableConversationId;
    private string durableConversationTitle = "New conversation";
    private ConversationRecentPage? durableConversationPage;
    private readonly Dictionary<string, ConversationSummary> durableConversations = new(StringComparer.Ordinal);
    private long durableNavigationVersion;
    private bool showingLegacyNavigation;
    private bool readingOlderChats;
    private Button? olderChats;
    private string? CurrentConversationId => session?.Id ?? durableConversationId;
    private bool IsNativeChat => session is null && durableConversationId is not null;
    private bool NativeWorkActive => IsNativeChat && nativeChat?.Snapshot?.Run is { IsTerminal: false };
    private bool NativeMutationUncertain => ControlUncertain || (IsNativeChat && nativeChat?.Snapshot?.MutationUncertain == true);
    private bool NativeMutationPending => ControlPending || (IsNativeChat && nativeChat?.MutationPending == true);

    private void EnsureNativeNavigationAllowed()
    {
        EnsureConversationControlsSettled(); EnsureAttachmentNavigationAllowed();
        if (NativeMutationPending) throw new InvalidOperationException("Wait for this request's acceptance before changing chats. Its request ID is retained in the current conversation.");
        if (NativeMutationUncertain) throw new InvalidOperationException("Refresh or retry the unconfirmed request before changing chats. Its request ID is retained in the current conversation.");
    }

    private void InitializeNativeChat(Grid center)
    {
        nativeChat = new(CommandWithCompletionOutcomeAsync,
            () => profiles, ReportNativeCompletionStatus, embedded: true) { Visibility = Visibility.Collapsed };
        nativeChat.QuestionsResetting = ResetQuestionDesktopTextEntries;
        nativeChat.QuestionTextEntryActions = (box, id) => RegisterDesktopTextEntry(box, "workflow-question." + id, "Answer Cerebral's question", question: true);
        nativeChat.SnapshotChanged += (_, _) => { UpdateNativeChatState(); QueueSharedActivityUpdate(); };
        nativeChat.ActivityNavigationRequested += target =>
        { if (IsNativeChat) OpenTurnActivityPage(nativeChat, target); };
        nativeChat.IsVisibleChanged += (_, _) => { if (nativeChat.IsVisible) ObserveComposerFinalResponse(); else SuspendComposerSuggestion(); };
        center.Children.Add(nativeChat);
        Closed += (_, _) => { CloseMainChatTiming(MainChatTimingEnd.Disposed); nativeChat.Dispose(); };
        BeginNativeChat();
    }

    private void BeginNativeChat(FreshChatSelection? selection = null, string? restoredLocalId = null)
    {
        EnsureNativeNavigationAllowed();
        if (restoredLocalId is not null && !Guid.TryParseExact(restoredLocalId, "N", out _))
            throw new InvalidDataException("The local draft identity is invalid.");
        CloseMainChatTiming(MainChatTimingEnd.SelectionChanged);
        ResetMainChatWorkflowChoice();
        durableConversationId = restoredLocalId ?? Guid.NewGuid().ToString("N"); durableConversationTitle = "New conversation";
        loadedConversationProperties = null; loadedPropertiesSource = -1;
        nativePreferencePending = false; nativePreferenceEditGeneration++;
        // Install the chosen owner before BeginConversation can publish any callbacks.
        InstallFreshChatDraft(selection);
        nativeChat?.BeginConversation(durableConversationId);
        if (nativeChat is not null) nativeChat.Visibility = Visibility.Collapsed;
        transcript.Visibility = Visibility.Visible;
    }

    private void ShowNativeChat()
    {
        if (nativeChat is not null) nativeChat.Visibility = Visibility.Visible;
        transcript.Visibility = Visibility.Collapsed;
        questionHost?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        liveDetails.Visibility = Visibility.Collapsed; liveLabel.Text = "";
    }

    private void UpdateNativeChatState()
    {
        if (!IsNativeChat || nativeChat is null) return;
        var snapshot = nativeChat.Snapshot;
        // A new selection is read before it becomes the selected plugin binding. Its intermediate
        // render must never publish results or question state under the previous conversation.
        if (snapshot is null || snapshot.ConversationId != durableConversationId) return;
        if (!snapshot.MutationUncertain && snapshot.Run is { } confirmed)
        {
            MarkInitialChatAccepted(confirmed.Id);
            AcceptAttachmentSelection(confirmed.Id);
        }
        if (!snapshot.MutationUncertain && snapshot.Run is { } receipt && retainedNativeSubmission is { } retained &&
            receipt.Id == retained.RequestId && snapshot.RunId == retained.RequestId &&
            snapshot.ConversationId == retainedNativeConversationId && RetainedNativeOwnerCurrent())
        {
            MarkInitialChatAccepted(retained.RequestId);
            MarkSemanticSubmission(retained.RequestId, "accepted");
            ClearRetainedNativeSubmission();
        }
        if (!snapshot.MutationUncertain && snapshot.Run is { } accepted && pendingSubmission?.RequestId == accepted.Id)
        {
            if (prompt.Text == pendingSubmission.Prompt) prompt.Clear();
            AcceptAttachmentSelection(accepted.Id);
            pendingSubmission = null; RememberComposerDraft(snapshot.ConversationId, prompt.Text);
        }
        if (mainChatTimingProbe is { } observedTiming && snapshot.RunId == observedTiming.RequestId &&
            snapshot.Events.Any(item => item.Type == "workflow.result" && item.TurnId == observedTiming.RequestId &&
                item.ConversationId == snapshot.ConversationId && item.Projection.Human != HumanVisibility.Transcript))
            observedTiming.MarkNonTranscriptResult(snapshot.ConversationId, observedTiming.RequestId!);
        if (mainChatTimingProbe is { } timing && timing.RequestId == snapshot.RunId &&
            snapshot.Run is { } observed && (observed.IsTerminal || observed.CancelRequested))
            QueueMainChatTimingTerminal(snapshot.ConversationId, observed.Id);
        sessionHeading.Text = durableConversationTitle;
        ObserveMainCompletionSnapshot(snapshot);
        if (TryConversationProfile() is null) sessionState.Text = ConversationProfileError;
        if (snapshot.RunId is null && composerDraftSelectionError is { } selectionError)
            sessionState.Text = selectionError;
        if (stopButton is not null) stopButton.IsEnabled = snapshot.Run is { IsTerminal: false } && !stopRequested && !NativeMutationPending;
        if (conversationComposer is not null) conversationComposer.Visibility = snapshot.Run?.Questions is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateMainChatWorkflowChoice();
        RenderAttachmentDrafts();
        desktopPluginHost?.PublishConversationChanged();
        foreach (var item in snapshot.Events.Where(x => x.Type == "workflow.result" && x.Projection.Human == HumanVisibility.Transcript))
        {
            if (!presentedPluginMessages.Add(item.Id)) continue;
            var text = string.Join("\n", item.Content.Select(part => part switch
            {
                TextContentPart plain => plain.Text,
                StructuredContentPart { Value.ValueKind: JsonValueKind.Object } structured when structured.Value.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String => summary.GetString(),
                _ => null
            }).Where(x => x is not null));
            if (pluginMessageBaseline && text.Length > 0)
                desktopPluginHost?.PublishLeadMessageCommitted(new(item.Id, PluginBinding(), "assistant",
                    text[..Math.Min(text.Length, DesktopPluginContract.MaxTextChars)], item.CreatedAt, text.Length > DesktopPluginContract.MaxTextChars));
        }
        ObserveComposerFinalResponse();
        pluginMessageBaseline = true;
    }

    private async Task RefreshDurableConversationsAsync(bool older = false)
    {
        var expectedConnection = knowledgeConnectionVersion;
        var expectedPage = ++durableNavigationVersion;
        var pageLimit = Math.Max(40, recentChatLimit);
        var arguments = new Dictionary<string, object> { ["limit"] = pageLimit, ["generalOnly"] = true };
        if (older && durableConversationPage?.NextBeforeRow is { } before)
        { arguments["beforeRow"] = before; arguments["snapshotRow"] = durableConversationPage.SnapshotRow; }
        // If only the exact-workflow lane has an older cursor, an exhausted Chat lane
        // contributes no new general rows; querying it without a cursor would restart page one.
        var page = older && durableConversationPage is { NextBeforeRow: null } exhaustedChat
            ? new ConversationRecentPage([], exhaustedChat.SnapshotRow, null)
            : await Get<ConversationRecentPage>("conversations.recent", arguments);
        if (expectedConnection != knowledgeConnectionVersion || expectedPage != durableNavigationVersion) return;
        var protectedSummaries = new List<ConversationSummary>();
        long? protectedBefore = null;
        do
        {
            var protectedArguments = new Dictionary<string, object> { ["limit"] = 200, ["snapshotRow"] = page.SnapshotRow };
            if (protectedBefore is { } cursor) protectedArguments["beforeRow"] = cursor;
            var protectedPage = await Get<ConversationRecentPage>("conversations.protected", protectedArguments);
            if (expectedConnection != knowledgeConnectionVersion || expectedPage != durableNavigationVersion) return;
            if (protectedPage.Conversations.Count > 200 || protectedPage.SnapshotRow != page.SnapshotRow ||
                protectedPage.NextBeforeRow is { } next && (next < 1 || protectedBefore is { } prior && next >= prior))
                throw new InvalidDataException("The protected conversation list returned an invalid page.");
            protectedSummaries.AddRange(protectedPage.Conversations);
            protectedBefore = protectedPage.NextBeforeRow;
        }
        while (protectedBefore is not null);
        var exact = await ReadExactWorkflowNavigationAsync(pageLimit, older, expectedPage, expectedConnection);
        if (expectedConnection != knowledgeConnectionVersion || expectedPage != durableNavigationVersion) return;
        showingLegacyNavigation = false; readingOlderChats = older;
        if (page.Conversations.Count > pageLimit) throw new InvalidDataException("The conversation list exceeded its requested page bound.");
        durableConversationPage = page;
        var selected = durableConversationId is not null ? durableConversations.GetValueOrDefault(durableConversationId) : null;
        var selectedExact = exact.Available && exactWorkflowNavigationConnectionIdentity == ServiceConnectionIdentity &&
            durableConversationId is not null ? exactWorkflowConversations.GetValueOrDefault(durableConversationId) : null;
        durableConversations.Clear();
        foreach (var item in page.Conversations.Concat(protectedSummaries).DistinctBy(item => item.Id)) durableConversations.Add(item.Id, item);
        exactWorkflowConversations.Clear();
        foreach (var entry in exact.Recent.Conversations.Concat(exact.Protected))
        {
            if (exactWorkflowConversations.TryGetValue(entry.Summary.Id, out var prior))
            {
                if (prior != entry) throw new InvalidDataException("The exact workflow list returned conflicting saved coordinates.");
                continue;
            }
            if (durableConversations.ContainsKey(entry.Summary.Id))
                throw new InvalidDataException("A saved conversation has both ordinary and exact workflow identities.");
            exactWorkflowConversations.Add(entry.Summary.Id, entry);
            durableConversations.Add(entry.Summary.Id, entry.Summary);
        }
        if (selectedExact is not null && !exactWorkflowConversations.ContainsKey(selectedExact.Summary.Id))
        {
            exactWorkflowConversations.Add(selectedExact.Summary.Id, selectedExact);
            durableConversations.TryAdd(selectedExact.Summary.Id, selectedExact.Summary);
        }
        if (selected is not null) durableConversations.TryAdd(selected.Id, selected);
        if (IsNativeChat && durableConversations.TryGetValue(durableConversationId!, out var selectedTitle))
        { durableConversationTitle = selectedTitle.Title; sessionHeading.Text = selectedTitle.Title; }
        var summaries = durableConversations.Values.Select(x => new TaskSession { Id = x.Id, Title = x.Title, ProfileId = x.ProfileId,
            ProjectId = x.ProjectId, UpdatedAt = x.UpdatedAt }).ToList();
        if (selected is not null && summaries.All(x => x.Id != selected.Id))
            summaries.Add(new() { Id = selected.Id, Title = selected.Title, ProfileId = selected.ProfileId, ProjectId = selected.ProjectId, UpdatedAt = selected.UpdatedAt });
        selecting = true;
        try { conversations.ItemsSource = summaries; conversations.SelectedItem = summaries.FirstOrDefault(x => x.Id == CurrentConversationId); }
        finally { selecting = false; }
        // Rendering combines bounded recent rows with protected summaries; older conversations remain durable.
        await RefreshNavigationAsync(summaries);
    }

    private async Task OpenDurableConversationAsync(ConversationSummary summary, long selection, CancellationToken token)
    {
        RequireSemanticAttachCurrent();
        if (NativeMutationPending) throw new InvalidOperationException("Wait for this request's acceptance before changing chats.");
        if (NativeMutationUncertain)
        {
            if (durableConversationId != summary.Id)
                throw new InvalidOperationException("Refresh or retry the unconfirmed request before changing conversations.");
            // Navigation summaries may still point at the previous turn. Keep the uncertain ID.
            await nativeChat!.RefreshAsync();
            return;
        }
        var exact = exactWorkflowConversations.GetValueOrDefault(summary.Id);
        if (exact is not null)
        {
            var bound = await Client.GetAsync<ExactWorkflowConversation>("conversations.workflow.get", new
            {
                conversationId = summary.Id, acceptedRunId = exact.AcceptedRunId,
                definitionId = exact.DefinitionId, definitionVersion = exact.DefinitionVersion
            }, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (selection != conversationSelectionVersion) return;
            RequireSemanticAttachCurrent();
            ValidateExactWorkflowConversation(bound);
            if (bound.Summary.Id != summary.Id || bound.Summary.ProfileId != summary.ProfileId ||
                bound.Summary.ProjectId != summary.ProjectId || bound.DefinitionId != exact.DefinitionId ||
                bound.DefinitionVersion != exact.DefinitionVersion)
                throw new InvalidDataException("The accepted exact workflow conversation changed ownership or definition.");
            summary = bound.Summary;
            exactWorkflowConversations[summary.Id] = bound;
            durableConversations[summary.Id] = summary;
            exact = bound;
        }
        var changed = durableConversationId != summary.Id;
        if (CurrentConversationId is { } previous && previous != summary.Id) RememberComposerDraft(previous, prompt.Text);
        if (durableConversationId == summary.Id && nativeChat?.Snapshot?.RunId == summary.LatestRunId)
            await nativeChat.RefreshAsync();
        else
        {
            nativeChat!.Reset();
            await nativeChat.OpenRunAsync(summary.LatestRunId);
        }
        token.ThrowIfCancellationRequested();
        if (selection != conversationSelectionVersion) return;
        RequireSemanticAttachCurrent();
        EnsureNativeNavigationAllowed();
        if (nativeChat?.Snapshot is not { } selectedSnapshot || selectedSnapshot.ConversationId != summary.Id || selectedSnapshot.RunId != summary.LatestRunId)
            throw new InvalidDataException("The selected run did not match this conversation. Refresh the conversation list.");
        if (exact is not null && (selectedSnapshot.Run?.Id != summary.LatestRunId ||
            selectedSnapshot.Run.ProfileId != summary.ProfileId ||
            selectedSnapshot.Run.DefinitionId != exact.DefinitionId ||
            selectedSnapshot.Run.DefinitionVersion != exact.DefinitionVersion))
            throw new InvalidDataException("The selected exact workflow run does not match its saved owner coordinates.");
        session = null; durableConversationId = summary.Id; durableConversationTitle = summary.Title;
        draftProjectId = summary.ProjectId; pendingSubmission = null;
        if (changed)
        {
            ResetDesktopConversation();
            nativePreferencePending = false; nativePreferenceEditGeneration++;
        }
        RestoreComposerDraftText(summary.Id);
        selectedNavigationId = navigation.Nodes.FirstOrDefault(x => x.Kind == "conversation" && x.TargetId == summary.Id)?.Id ?? "conversation-" + summary.Id;
        if (changed) selectedResponseSharePercent = ReadSavedChatResponseBalance(summary.Id);
        if (exact is null) await LoadNativePropertiesAsync(summary, selection, token);
        else
        {
            // Ordinary-chat preferences are definition-specific; no exact workflow property is fabricated.
            loadedConversationProperties = null; loadedPropertiesSource = knowledgeConnectionVersion;
            selectedResponseSharePercent = null; nativePreferencePending = false;
        }
        if (selection != conversationSelectionVersion) return;
        RefreshConversationTokenBalance(); ShowNativeChat(); UpdateAgentSummary(); UpdateNativeChatState(); RenderNavigation();
        await RefreshProjectKnowledgeAsync(); await RefreshInterfaceRequests();
        if (changed) ResetTeamForChangedMain();
        else ShowMainTeamView();
        await RefreshTeamForCurrentMainAsync();
    }

    private async Task<DesktopOperationResult> SubmitNativeDesktopInput(DesktopSubmission input, string? assignment, CancellationToken ct)
    {
        RequireMainDefinitionInputCurrent(input.RequestId);
        if (ControlUncertain) return new(DesktopOperationState.Rejected, "Use Retry pending action before submitting another message.", input.RequestId);
        if (session is not null) return new(DesktopOperationState.Rejected, "This is a previous-generation conversation. Start a new chat to continue through the durable conversation engine.", input.RequestId);
        if (NativeMutationPending) return new(DesktopOperationState.Rejected, "A request is awaiting acceptance. Wait for its receipt before another submission.", input.RequestId);
        var workspace = nativeChat;
        if (input.Binding != PluginBinding() || durableConversationId is null || workspace?.Snapshot is not { } snapshot || snapshot.ConversationId != durableConversationId)
            return new(DesktopOperationState.Rejected, "The conversation changed.", input.RequestId);
        RequireSemanticAttachCurrent();
        if (!RetainedNativeOwnerCurrent())
            return new(DesktopOperationState.Rejected, "The retained request belongs to its original connection. Reconnect there before retrying.", input.RequestId);
        EnsureSemanticAttachSubmissionAllowed();
        var workflowIntent = mainChatWorkflowIntent.Value ?? CaptureMainChatWorkflowIntent()
            ?? throw new InvalidOperationException("The current conversation has no confirmed chat option.");
        var previousWorkflowIntent = mainChatWorkflowIntent.Value;
        mainChatWorkflowIntent.Value = workflowIntent;
        try
        {
            RequireMainChatWorkflowIntentCurrent(workflowIntent);
            await ReconcileInterruptedNativeWorkAsync(ct);
            RequireSemanticAttachCurrent();
            RequireMainChatWorkflowIntentCurrent(workflowIntent);
            if (workspace.Snapshot is not { } refreshed || refreshed.ConversationId != durableConversationId)
                return new(DesktopOperationState.Rejected, "The conversation changed while checking its prior work.", input.RequestId);
            snapshot = refreshed;
            if (NativeWorkActive && snapshot.RunId != input.RequestId)
                return new(DesktopOperationState.Rejected, "This conversation is still working. Answer its question, stop it, or wait before sending another message.", input.RequestId);
            WorkflowConversationSubmission request;
            if (retainedNativeSubmission is { } frozen)
            {
                if (!RetainedNativeOwnerCurrent() || frozen.RequestId != input.RequestId || frozen.UserText != input.Text)
                    return new(DesktopOperationState.Rejected, "Retry the retained request on its original connection before submitting different text.", input.RequestId);
                ValidateRetainedMainDefinitionInput(frozen);
                request = frozen;
            }
            else
            {
                var client = SemanticAttachClient(); var connection = ServiceConnection; var identity = ServiceConnectionIdentity;
                var source = knowledgeConnectionVersion;
                var profile = ConversationProfile();
                if (nativePreferencePending && snapshot.Run is not null)
                    await PersistSelectedNativeResponsePreferenceAsync();
                RequireSemanticAttachCurrent();
                RequireMainChatWorkflowIntentCurrent(workflowIntent);
                var root = TryInitialAcceptedRoot(out var acceptedRoot)
                    ? acceptedRoot : await NativeSubmissionRootAsync(profile, snapshot, input, ct);
                RequireSemanticAttachCurrent();
                RequireMainChatWorkflowIntentCurrent(workflowIntent);
                if (input.Binding != PluginBinding() || !ReferenceEquals(client, SemanticAttachClient()) ||
                    !ReferenceEquals(connection, ServiceConnection) || source != knowledgeConnectionVersion || identity != ServiceConnectionIdentity)
                    return new(DesktopOperationState.Rejected, "The conversation or connection changed.", input.RequestId);
                var content = FreezeAttachments(input, profile.Id);
                var inputLease = mainDefinitionInput.Value;
                var definitionInput = inputLease?.Consume(input.RequestId);
                request = WorkflowConversationClient.FreezeSubmission(CreateNativeSubmission(input, profile.Id, root,
                    ConversationProjectId, selectedResponseSharePercent, assignment, content, workflowIntent.DefinitionVersion,
                    definitionInput, workflowIntent.DefinitionId, workflowIntent.ExactAvailability?.Definition.InputSchema));
                inputLease?.ConfirmApplied(input.RequestId);
                retainedNativeSubmission = request; retainedNativeConversationId = durableConversationId;
                retainedNativeClient = client; retainedNativeConnection = connection;
                retainedNativeSource = source; retainedNativeIdentity = identity;
                mainOwnedSubmissionCoordinates = workflowIntent.DefinitionId == ConversationControlClient.ChatDefinitionId
                    ? null : new(durableConversationId!, request.RequestId, ConversationProjectId, root);
                CaptureFreshChatPreparation(request, profile, ConversationProjectId, root,
                    workflowIntent.DefinitionId != ConversationControlClient.ChatDefinitionId);
                ArmMainCompletionSubmission(request.RequestId);
                mainChatTimingProbe?.Bind(durableConversationId!, request.RequestId, profile.Id);
                // Once frozen, this request owns its edition; later callbacks use its canonical owner.
                mainChatWorkflowIntent.Value = CaptureMainChatWorkflowIntent();
                UpdateMainChatWorkflowChoice();
            }
            var selectedId = durableConversationId;
            var draftRevision = pluginDraftRevision;
            var ownsPending = pendingSubmission?.RequestId == input.RequestId;
            RequireSemanticAttachCurrent();
            ShowNativeChat();
            WorkflowConversationSnapshot accepted;
            string? refreshWarning = null;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token);
                EnsureSemanticAttachSubmissionAllowed();
                await EnsureFreshChatAuthorityAsync(request, linked.Token);
                RequireSemanticAttachCurrent();
                EnsureSemanticAttachSubmissionAllowed();
                RequireMainDefinitionInputCurrent(request.RequestId);
                CaptureSemanticSubmission(request);
                MarkInitialChatDispatchAttempt(request);
                mainChatTimingProbe?.MarkSubmitCall(request.RequestId);
                accepted = await workspace.SubmitConversationAsync(request, linked.Token);
                if (!accepted.MutationUncertain && accepted.ConversationId == durableConversationId &&
                    accepted.RunId == request.RequestId && accepted.Run?.Id == request.RequestId)
                    mainChatTimingProbe?.MarkConfirmedAck(request.RequestId);
            }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException or InvalidDataException or ArgumentException or OperationCanceledException or TimeoutException)
            {
                if (ConfirmedNativeSubmission(workspace.Snapshot, input.RequestId) && workspace.Snapshot is { } receipt)
                { accepted = receipt; refreshWarning = "The message was accepted; its display could not refresh. Refresh the saved run. " + error.Message; }
                else if (workspace.Snapshot?.MutationUncertain != true)
                {
                    MarkSemanticSubmission(input.RequestId, "rejected");
                    // A prepared draft has one immutable first RID, including a known refusal.
                    // Retrying may resend only its ordinary frozen submission; changed text needs a new chat.
                    var keepsInitial = KeepInitialChatRequest(request);
                    if (!keepsInitial) ClearRetainedNativeSubmission();
                    UpdateMainChatWorkflowChoice();
                    return new(DesktopOperationState.Rejected, keepsInitial
                        ? "The first request keeps its original ID and message. Retry it unchanged, or start a new chat to select a current profile. " + error.Message
                        : error.Message, input.RequestId);
                }
                else
                {
                    MarkSemanticSubmission(input.RequestId, "uncertain");
                    return new(DesktopOperationState.Uncertain, "Submission was not confirmed. Refresh or use Retry request with its retained ID. " + error.Message, input.RequestId);
                }
            }
            MarkInitialChatAccepted(input.RequestId);
            MarkSemanticSubmission(input.RequestId, "accepted");
            if (selectedId == durableConversationId && input.Binding == PluginBinding() && RetainedNativeOwnerCurrent())
            {
                if (draftRevision == pluginDraftRevision && (ownsPending || prompt.Text == input.Text)) prompt.Clear();
                AcceptAttachmentSelection(input.RequestId);
                pendingSubmission = null; ClearRetainedNativeSubmission(); RememberComposerDraft(selectedId, prompt.Text); CarryDesktopInputModeToSession(selectedId);
                if (durableConversationTitle == "New conversation") durableConversationTitle = string.IsNullOrWhiteSpace(input.Text) ? "Attachments" : input.Text[..Math.Min(70, input.Text.Length)].Replace('\n', ' ');
                SaveChatResponseBalance(); UpdateNativeChatState();
                try
                {
                    if (nativePreferencePending && semanticAttachInvocation.Value is not null)
                        refreshWarning = "The message was accepted. A later chat preference remains pending; confirm it through the ordinary interface.";
                    else if (nativePreferencePending) await PersistSelectedNativeResponsePreferenceAsync();
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or HttpRequestException or TimeoutException or
                    System.Text.Json.JsonException or OperationCanceledException)
                {
                    refreshWarning = "The message was accepted; its response preference could not be confirmed. Reopen Properties to reload it. " + error.Message;
                    SetStatus(refreshWarning, true);
                }
                try { await RefreshDurableConversationsAsync(); }
                catch (Exception error) { SetStatus("The request was accepted; conversation navigation could not refresh: " + error.Message, true); }
                if (CurrentConversationId == selectedId)
                {
                    selectedNavigationId = NavigationIdForConversation(selectedId);
                    RememberCurrentConversationVisit();
                    RenderNavigation();
                }
            }
            return new(DesktopOperationState.Accepted, refreshWarning ?? "Cerebral accepted the message.", input.RequestId, accepted.RunId);
        }
        finally { mainChatWorkflowIntent.Value = previousWorkflowIntent; }
    }

    private static WorkflowConversationSubmission CreateNativeSubmission(DesktopSubmission input, string profileId,
        string? root, string? projectId, int? responseSharePercent, string? assignment, IReadOnlyList<ContentPart>? acceptedContent = null,
        string? definitionVersion = null, JsonElement? definitionInput = null,
        string? definitionId = null, JsonElement? exactSchema = null)
    {
        var id = definitionId ?? ConversationControlClient.ChatDefinitionId;
        JsonElement workflowInput;
        if (id == ConversationControlClient.ChatDefinitionId)
        {
        var arguments = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(root)) arguments["workingDirectory"] = root;
        if (projectId is { } project) arguments["projectId"] = project;
        if (responseSharePercent is { } share) { AgentTokenLimits.ValidateResponseShare(share); arguments["responseSharePercent"] = share; }
            workflowInput = MainWorkflowDefinitionInput.Merge(CerebralJson.Element(arguments), definitionInput);
        }
        else
        {
            if (definitionInput is not { } supplied || exactSchema is not { } schema)
                throw new InvalidOperationException("The selected exact workflow requires its complete confirmed definitionInput.");
            workflowInput = MainWorkflowDefinitionInput.FreezeExact(supplied);
            WorkflowSchema.Validate(workflowInput, schema);
        }
        return new WorkflowConversationSubmission(input.RequestId, profileId, id,
            definitionVersion ?? ConversationControlClient.ChatDefinitionVersion,
            input.Text, workflowInput, assignment, WorkflowContract.MaximumSteps)
            { AcceptedContent = acceptedContent ?? [], OwnerProjectId = id == ConversationControlClient.ChatDefinitionId ? null : projectId };
    }

    private void SaveChatResponseBalance()
    {
        if (IsNativeChat)
            UiPreferenceStore.WriteString("Cerebral.ChatBalance." + durableConversationId, selectedResponseSharePercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }
    private static int? ReadSavedChatResponseBalance(string id) => int.TryParse(UiPreferenceStore.ReadString("Cerebral.ChatBalance." + id), out var value) && value is >= 5 and <= 50 ? value : null;

    private async Task<DesktopOperationResult> StopNativeChatAsync(CancellationToken token)
    {
        var workspace = nativeChat;
        if (workspace?.Snapshot?.RunId is not { } id) return new(DesktopOperationState.Rejected, "This conversation has no accepted work to stop.");
        try { await workspace.CancelConversationAsync(token); return new(DesktopOperationState.Accepted, "Stop requested for this conversation's durable run.", JobId: id); }
        catch (Exception error) when (error is WorkflowConversationCommandException or HttpRequestException or IOException or InvalidDataException or OperationCanceledException or TimeoutException)
        {
            if (ConfirmedNativeStop(workspace.Snapshot, id))
                return new(DesktopOperationState.Accepted, "Stop was accepted; its display could not refresh. Refresh the saved run. " + error.Message, JobId: id);
            if (error is WorkflowConversationCommandException) return new(DesktopOperationState.Rejected, error.Message, JobId: id);
            return new(DesktopOperationState.Uncertain, "Stop could not be confirmed. Refresh this run before another action. " + error.Message, JobId: id);
        }
    }

    private async Task ReconcileInterruptedNativeWorkAsync(CancellationToken token)
    {
        if (!IsNativeChat || nativeChat?.Snapshot?.Run is not { IsTerminal: false, EffectState: WorkflowEffectState.Unknown }) return;
        await nativeChat.ReconcileConversationAsync(token);
    }

    private static bool ConfirmedNativeSubmission(WorkflowConversationSnapshot? snapshot, string requestId) =>
        snapshot is { MutationUncertain: false, Run: { } run } && snapshot.RunId == requestId && run.Id == requestId;
    private static bool ConfirmedNativeStop(WorkflowConversationSnapshot? snapshot, string requestId) =>
        ConfirmedNativeSubmission(snapshot, requestId) && snapshot!.Run is { } run &&
        (run.CancelRequested || run.Status == WorkflowStatus.Cancelled);

}
