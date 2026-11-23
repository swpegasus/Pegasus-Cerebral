using System.IO;
using System.Net.Http;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private ConversationControlClient? conversationControl;
    private CerebralClient? conversationControlTransport;
    private DesktopConversationBinding? conversationControlBinding;
    private long conversationControlDraftRevision;
    private string? conversationControlDraftText;
    private long conversationControlSource;
    private bool conversationControlPreparing;
    private Func<string, ConversationControlClient>? conversationControlFactory;
    private Func<string, string?>? conversationRenameEditor;
    private bool ControlPending => conversationControlPreparing || conversationControl?.Snapshot.DispatchPending == true;
    private bool ControlUncertain => conversationControl?.Snapshot.Uncertain == true;

    private void EnsureConversationControlsSettled()
    {
        if (ControlPending) throw new InvalidOperationException("Wait for the conversation action's acceptance before changing chats or connections.");
        if (ControlUncertain) throw new InvalidOperationException("Use Retry pending action before changing chats or connections. The unconfirmed request ID and original input are retained.");
    }

    private ConversationControlClient BeginConversationControl(string conversationId, string? draftText = null)
    {
        if (conversationControl is not null) throw new InvalidOperationException("Resolve the retained conversation action first.");
        conversationControlBinding = PluginBinding(); conversationControlSource = knowledgeConnectionVersion;
        conversationControlDraftRevision = pluginDraftRevision; conversationControlDraftText = draftText;
        if (conversationControlFactory is { } factory) conversationControl = factory(conversationId);
        else
        {
            // This independent client retains its endpoint/instance even if the ordinary UI reconnects.
            var captured = CreateIndependentClient(); conversationControlTransport = captured;
            conversationControl = new(conversationId, (request, token) =>
                captured.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token));
        }
        return conversationControl;
    }

    private bool IsCurrentConversationControl => conversationControlBinding == PluginBinding() &&
        conversationControlSource == knowledgeConnectionVersion;

    private void ReleaseConversationControl()
    {
        if (conversationControl?.Snapshot is { DispatchPending: true } or { Uncertain: true }) return;
        conversationControl = null; conversationControlBinding = null; conversationControlDraftText = null;
        conversationControlTransport?.Dispose(); conversationControlTransport = null;
    }

    private async Task<DesktopOperationResult> SteerNativeDesktopInput(DesktopSubmission input, CancellationToken token)
    {
        if (input.Binding != PluginBinding() || !IsNativeChat)
            return new(DesktopOperationState.Rejected, "The conversation changed.", input.RequestId);
        if (conversationControl?.Snapshot is { Uncertain: true, Steering: { } retained })
        {
            if (retained.RequestId != input.RequestId || retained.Text != input.Text || !IsCurrentConversationControl)
                return new(DesktopOperationState.Rejected, "Use Retry pending action to resend the original steering request unchanged.", input.RequestId);
            return await ExecuteNativeSteering(conversationControl, retained, token);
        }
        if (NativeMutationPending || NativeMutationUncertain)
            return new(DesktopOperationState.Rejected, "Wait for or resolve the pending conversation request first.", input.RequestId);
        if (nativeChat?.Snapshot?.Run is not { IsTerminal: false } run || run.CancelRequested)
            return new(DesktopOperationState.Rejected, "There is no active conversation run to steer.", input.RequestId);
        var control = BeginConversationControl(durableConversationId!, input.Text);
        return await ExecuteNativeSteering(control, new(input.RequestId, run.Id, input.Text), token);
    }

    private async Task<DesktopOperationResult> ExecuteNativeSteering(ConversationControlClient control, WorkflowSteeringRequest request, CancellationToken token)
    {
        if (control != conversationControl || !IsCurrentConversationControl)
            throw new InvalidOperationException("The retained action belongs to its original conversation and service.");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime.Token);
            var receiptTask = control.SteerAsync(request, linked.Token);
            UpdateNativeChatState();
            var receipt = await receiptTask;
            if (IsCurrentConversationControl)
            {
                // A user can continue typing while transport awaits a receipt. Never clear that newer draft.
                if (conversationControlDraftRevision == pluginDraftRevision && prompt.Text == conversationControlDraftText) prompt.Clear();
                if (pendingSteering?.RequestId == receipt.RequestId) pendingSteering = null;
            }
            return new(DesktopOperationState.Accepted, "Steering accepted for the next safe step; it has not necessarily executed yet.", receipt.RequestId, receipt.RunId);
        }
        catch (WorkflowConversationCommandException error)
        {
            return new(control.Snapshot.Uncertain ? DesktopOperationState.Uncertain : DesktopOperationState.Rejected,
                control.Snapshot.Uncertain ? "The retry was rejected, but the original steering outcome is still unconfirmed. Its request is retained. " + error.Message : error.Message,
                request.RequestId, request.RunId);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            return new(control.Snapshot.Uncertain ? DesktopOperationState.Uncertain : DesktopOperationState.Rejected,
                control.Snapshot.Uncertain ? "Steering acceptance is unconfirmed. Use Retry pending action; its exact input and request ID are retained. " + error.Message : error.Message,
                request.RequestId, request.RunId);
        }
        finally { ReleaseConversationControl(); UpdateNativeChatState(); }
    }

    private async Task RetryConversationControlAsync()
    {
        var control = conversationControl ?? throw new InvalidOperationException("There is no pending conversation action.");
        if (!control.Snapshot.Uncertain || !IsCurrentConversationControl)
            throw new InvalidOperationException("The unconfirmed action belongs to its original conversation and service.");
        if (control.Snapshot.Steering is { } steering)
        {
            var result = await ExecuteNativeSteering(control, steering, Lifetime.Token);
            SetStatus(result.Message ?? result.State.ToString(), result.State != DesktopOperationState.Accepted);
        }
        else if (control.Snapshot.Rename is { } rename) await ExecuteNativeRename(control, rename, Lifetime.Token);
        else if (control.Snapshot.Compaction is { } compaction) await ExecuteNativeCompaction(control, compaction, Lifetime.Token);
    }

    private async Task RenameDurableConversationAsync(string conversationId)
    {
        if (exactWorkflowConversations.ContainsKey(conversationId))
            throw new InvalidOperationException("Rename is unavailable for an exact workflow conversation; its saved title is unchanged.");
        if (ControlUncertain)
        {
            if (conversationControl?.Snapshot.Rename?.ConversationId != conversationId)
                throw new InvalidOperationException("Retry the retained conversation action before renaming a different chat.");
            await RetryConversationControlAsync(); return;
        }
        await RenameDurableConversationCoreAsync(conversationId, null, Lifetime.Token);
    }

    private async Task<DesktopOperationResult> RenameDurableConversationFromWorkspaceAsync(
        DesktopConversationBinding binding, string title, CancellationToken token)
    {
        var conversationId = binding.SessionId;
        if (binding != PluginBinding() || conversationId is null)
            return new(DesktopOperationState.Rejected, "The conversation changed. Review the current conversation before renaming it.");
        if (ControlUncertain)
            return new(DesktopOperationState.Uncertain, "Resolve the retained conversation action with Retry pending action. Its original request ID and title are unchanged.");
        if (ControlPending || NativeMutationPending || NativeMutationUncertain)
            return new(DesktopOperationState.Rejected, "Wait for or resolve the pending conversation request before renaming.");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime.Token);
            return await RenameDurableConversationCoreAsync(conversationId, title, linked.Token);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or
            HttpRequestException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            // A read or binding failure before dispatch is safe to retry as a fresh user action.
            return new(DesktopOperationState.Rejected, "The name was not submitted: " + error.Message);
        }
    }

    // Both the native menu and plugin callback share one CAS read and one retained mutation path.
    private async Task<DesktopOperationResult> RenameDurableConversationCoreAsync(
        string conversationId, string? requestedTitle, CancellationToken token)
    {
        if (exactWorkflowConversations.ContainsKey(conversationId))
            throw new InvalidOperationException("Rename is unavailable for an exact workflow conversation; its saved title is unchanged.");
        EnsureNativeNavigationAllowed();
        var control = BeginConversationControl(conversationId);
        conversationControlPreparing = true;
        try
        {
            // Read the latest title CAS revision before opening the editor. The captured client and
            // binding remain fixed through both awaits and the modal dialog's dispatcher pump.
            var captured = conversationControlTransport;
            var saved = captured is not null
                ? await captured.GetAsync<ConversationSummary>("conversations.get", new { id = conversationId }, cancellationToken: token)
                : durableConversations.GetValueOrDefault(conversationId) ?? throw new InvalidOperationException("Open the saved conversation before renaming.");
            if (saved.Id != conversationId || !IsCurrentConversationControl)
                throw new InvalidDataException("The conversation or service changed before its title could be edited.");
            durableConversations[conversationId] = saved;
            string? title = requestedTitle;
            if (title is null && conversationRenameEditor is { } editor) title = editor(saved.Title);
            else if (title is null)
            {
                var form = new FieldsDialog(this, "Rename conversation", "Change the sidebar title. Saved messages and the original request remain unchanged.") { Height = 280 };
                form.Add("title", "Name", saved.Title);
                title = form.ShowDialog() == true ? form.Value("title").Trim() : null;
            }
            if (title is null) return new(DesktopOperationState.Rejected, "Rename cancelled.");
            if (!IsCurrentConversationControl) throw new InvalidOperationException("The conversation changed while the name editor was open.");
            conversationControlPreparing = false;
            return await ExecuteNativeRename(control, new(Guid.NewGuid().ToString("N"), ConversationControlClient.ChatDefinitionId,
                saved.Id, saved.TitleRevision, title), token);
        }
        finally { conversationControlPreparing = false; ReleaseConversationControl(); UpdateNativeChatState(); }
    }

    private async Task<DesktopOperationResult> ExecuteNativeRename(ConversationControlClient control, ConversationRenameRequest request, CancellationToken token)
    {
        if (control != conversationControl || !IsCurrentConversationControl)
            throw new InvalidOperationException("The retained action belongs to its original conversation and service.");
        try
        {
            var receiptTask = control.RenameAsync(request, token); UpdateNativeChatState();
            var receipt = await receiptTask;
            if (IsCurrentConversationControl)
            {
                ApplyConversationRename(receipt);
                SetStatus("Conversation renamed.");
            }
            return new(DesktopOperationState.Completed, "Conversation renamed.", request.RequestId);
        }
        catch (WorkflowConversationCommandException error)
        {
            var uncertain = control.Snapshot.Uncertain;
            SetStatus(control.Snapshot.Uncertain
                ? "The retry was rejected, but the original rename outcome is still unconfirmed. Its request is retained. " + error.Message
                : "The name was not changed: " + error.Message + " Open Rename again to review its latest title.", true);
            return new(uncertain ? DesktopOperationState.Uncertain : DesktopOperationState.Rejected,
                uncertain ? "The original rename outcome is still unconfirmed. Use Retry pending action." : error.Message,
                request.RequestId);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            var uncertain = control.Snapshot.Uncertain;
            SetStatus(control.Snapshot.Uncertain
                ? "Rename acceptance is unconfirmed. Use Retry pending action; the original title and revision are retained. " + error.Message
                : error.Message, true);
            return new(uncertain ? DesktopOperationState.Uncertain : DesktopOperationState.Rejected,
                uncertain ? "Rename acceptance is unconfirmed. Use Retry pending action." : error.Message,
                request.RequestId);
        }
        finally { ReleaseConversationControl(); UpdateNativeChatState(); }
    }

    private async Task RequestDurableConversationCompactionAsync()
    {
        if (durableConversationId is { } exactId && exactWorkflowConversations.ContainsKey(exactId))
            throw new InvalidOperationException("Ordinary-chat compaction is unavailable for an exact workflow conversation.");
        if (ControlUncertain)
        {
            if (conversationControl?.Snapshot.Compaction?.ConversationId != durableConversationId)
                throw new InvalidOperationException("Retry the retained conversation action before requesting another compaction.");
            await RetryConversationControlAsync(); return;
        }
        if (!IsNativeChat || durableConversationId is null || nativeChat is null)
            throw new InvalidOperationException("Open a saved durable conversation first.");
        if (NativeMutationPending)
            throw new InvalidOperationException("Wait for the pending conversation action before requesting compaction.");
        await nativeChat.RefreshFromConnectionAsync();
        if (NativeWorkActive)
            throw new InvalidOperationException("Finish, answer or stop the current conversation work before requesting compaction.");
        var snapshot = nativeChat.Snapshot ?? throw new InvalidOperationException("The saved conversation is unavailable.");
        if (snapshot.Events.Count == 0 && snapshot.SnapshotSequence == 0)
            throw new InvalidOperationException("Send at least one message before requesting conversation compaction.");
        var control = BeginConversationControl(durableConversationId);
        await ExecuteNativeCompaction(control, new(Guid.NewGuid().ToString("N"), ConversationControlClient.ChatDefinitionId,
            durableConversationId, snapshot.SnapshotSequence), Lifetime.Token);
    }

    private async Task ExecuteNativeCompaction(ConversationControlClient control,
        ConversationCompactionRequest request, CancellationToken token)
    {
        if (control != conversationControl || !IsCurrentConversationControl)
            throw new InvalidOperationException("The retained action belongs to its original conversation and service.");
        try
        {
            var receiptTask = control.RequestCompactionAsync(request, token); UpdateNativeChatState();
            await receiptTask;
            if (IsCurrentConversationControl)
                SetStatus("One measured compaction will run before this conversation's next real message. No prompt was added to the transcript.");
        }
        catch (WorkflowConversationCommandException error)
        {
            SetStatus(control.Snapshot.Uncertain
                ? "The retry was rejected, but the original compaction request is still unconfirmed. Use Retry pending action. " + error.Message
                : "Compaction was not requested: " + error.Message, true);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            SetStatus(control.Snapshot.Uncertain
                ? "Compaction request acceptance is unconfirmed. Use Retry pending action; the exact request is retained. " + error.Message
                : error.Message, true);
        }
        finally { ReleaseConversationControl(); UpdateNativeChatState(); }
    }

    private async Task RefreshConversationTitleAsync(string conversationId)
    {
        if (!durableConversations.ContainsKey(conversationId) && (!IsNativeChat || CurrentConversationId != conversationId)) return;
        var source = knowledgeConnectionVersion; var captured = Client;
        var saved = await captured.GetAsync<ConversationSummary>("conversations.get", new { id = conversationId }, cancellationToken: Lifetime.Token);
        if (source != knowledgeConnectionVersion || saved.Id != conversationId) return;
        ApplyConversationRename(new(saved.Id, saved.Title, saved.TitleRevision));
    }

    private void ApplyConversationRename(ConversationRenameResult receipt)
    {
        if (durableConversations.TryGetValue(receipt.ConversationId, out var current))
        {
            if (current.TitleRevision > receipt.TitleRevision) return;
            durableConversations[receipt.ConversationId] = current with { Title = receipt.Title, TitleRevision = receipt.TitleRevision };
        }
        // Discard an earlier navigation query that may still carry the pre-rename title.
        durableNavigationVersion++;
        foreach (var item in navigationSessions.Where(x => x.Id == receipt.ConversationId)) item.Title = receipt.Title;
        conversations.Items.Refresh();
        foreach (var item in navigation.Nodes.Where(x => x.Kind == "conversation" && x.TargetId == receipt.ConversationId)) item.Title = receipt.Title;
        if (CurrentConversationId == receipt.ConversationId)
        {
            durableConversationTitle = receipt.Title; sessionHeading.Text = receipt.Title;
            desktopPluginHost?.PublishConversationChanged();
        }
        RenderNavigation();
    }
}
