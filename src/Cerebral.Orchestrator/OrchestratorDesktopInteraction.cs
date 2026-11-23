using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private string pluginGeneration = Guid.NewGuid().ToString("N");
    private long pluginDraftRevision;
    private DesktopPluginHostAdapter? desktopPluginHost;
    private OrchestratorPluginOwnerScope? desktopPluginOwner;
    private bool changingDesktopPluginOwner;
    private int desktopPluginMountDepth;
    private bool desktopPluginOwnerRefreshPending;
    private bool desktopPluginDisposalRequested;
    private bool disposingDesktopPlugins;
    private bool revokingDesktopTextEntryTargets;
    private readonly SemaphoreSlim desktopInputGate = new(1, 1);
    private readonly TabControl desktopSidebarTabs = new();
    private readonly List<TabItem> desktopPluginSidebarTabs = [];
    private readonly WrapPanel desktopPluginActions = new() { Visibility = Visibility.Collapsed };
    private readonly WrapPanel desktopPluginAccessories = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel desktopWorkspaceTrees = new() { Visibility = Visibility.Collapsed };
    private readonly HashSet<string> presentedPluginMessages = new(StringComparer.Ordinal);
    private bool pluginMessageBaseline;
    private (string JobId, string Text, string RequestId)? pendingSteering;
    private DesktopConversationBinding PluginBinding() => new(pluginGeneration, CurrentConversationId);
    private DesktopConversationSnapshot PluginConversation() => new(PluginBinding(), session?.Title ?? durableConversationTitle, ConversationProjectId,
        EffectiveNavigation().FirstOrDefault(x => x.Id == ConversationProjectId)?.Path, IsNativeChat ? NativeWorkActive : CurrentJob() is not null);

    private OrchestratorPluginOwnerState DesktopPluginOwnerState() =>
        new(IsConnected ? Client : null, ServiceConnectionIdentity, knowledgeConnectionVersion, IsConnected);

    private void CreateDesktopPluginHost()
    {
        var owner = new OrchestratorPluginOwnerScope(DesktopPluginOwnerState(), () => Dispatcher.CheckAccess()
            ? DesktopPluginOwnerState() : Dispatcher.Invoke(DesktopPluginOwnerState), Lifetime.Token);
        desktopPluginOwner = owner;
        desktopPluginHost = new(Dispatcher, PluginConversation, () => new(PluginBinding(), pluginDraftRevision, prompt.Text),
            ApplyPluginDraftWithReviewLease,
            (input, ct) => WithDesktopInputGate(input, ct, SubmitDesktopInput), (input, ct) => WithDesktopInputGate(input, ct, SteerDesktopInput), StopDesktopConversation,
            text => SetStatus(text),
            pluginDataRoot: desktopPluginDataRoot,
            serviceCommand: owner.Dispatch, dismissInputGuidance: ClearDesktopInputGuidance,
             submitWorkflow: (pluginId, input, ct) => WithDesktopInputGate(
                  new(input.Binding, input.RequestId, input.VisibleText), ct,
                  (_, token) => SubmitDesktopWorkflow(pluginId, input, token)), hasSubmissionContent: HasFrozenAttachments,
              responseBalance: PluginResponseBalance,
              setResponseBalance: SetPluginResponseBalanceAsync,
              productPaths: DesktopProductPathsSnapshot);
        desktopPluginHost.ConfigureSettingsNavigation(PluginSettingsNavigationAvailability, OpenPluginSettingsAsync);
        var host = desktopPluginHost;
        desktopPluginHost.ConfigureDefaultTextEntry(() =>
        {
            if (Lifetime.IsCancellationRequested || desktopPluginDisposalRequested || desktopPluginOwnerRefreshPending ||
                revokingDesktopTextEntryTargets || !ReferenceEquals(desktopPluginHost, host) ||
                !ReferenceEquals(desktopPluginOwner, owner) || owner.ObserveMatchesOwner() != true ||
                desktopPlugins is null) return null;
            var mount = desktopTextEntryMounts.FirstOrDefault(value =>
                !value.Question && string.Equals(value.Id, "composer", StringComparison.Ordinal));
            var target = mount?.Target;
            return mount is not null && !mount.Lifetime.IsCancellationRequested &&
                target?.IsAvailableForPlugin == true ? target : null;
        }, supportsDraftPresentation: true);
        ConfigureDesktopWorkspace();
    }

    private void InitializeDesktopInteraction()
    {
        prompt.TextChanged += (_, _) => pluginDraftRevision++;
        InitializeDesktopPlugins();
        ServiceStatusChanged += DesktopPluginOwnerChanged;
        Closed += (_, _) =>
        {
            composerDraftPresentation?.Dispose();
            ServiceStatusChanged -= DesktopPluginOwnerChanged;
            try { DisposeDesktopPlugins(); }
            catch (Exception error)
            {
                try
                {
                    var kind = error.GetType().Name;
                    Console.Error.WriteLine("Desktop plugin cleanup failed while closing (" +
                        kind[..Math.Min(kind.Length, 128)] + ").");
                }
                catch { /* An unavailable diagnostic writer must not interrupt close cleanup. */ }
            }
            finally { conversationControlTransport?.Dispose(); }
        };
    }

    private void DesktopPluginOwnerChanged(object? sender, EventArgs args)
    {
        if (Lifetime.IsCancellationRequested || changingDesktopPluginOwner) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => DesktopPluginOwnerChanged(sender, args));
            return;
        }
        InvalidateComposerSuggestion();
        try
        {
            if (EnsureDesktopPluginOwner() && desktopPluginsStarted && IsConnected) ReloadDesktopPlugins();
        }
        catch (Exception error) { ReportDesktopPluginRefreshError(error); }
    }

    private bool EnsureDesktopPluginOwner()
    {
        Dispatcher.VerifyAccess();
        if (Lifetime.IsCancellationRequested || desktopPluginDisposalRequested || changingDesktopPluginOwner || desktopPluginOwner?.MatchesOwner() == true) return false;
        if (desktopPluginMountDepth != 0)
        {
            desktopPluginOwner?.Retire();
            desktopPluginOwnerRefreshPending = true;
            RevokeDesktopTextEntryTargets();
            return false;
        }
        try
        {
            changingDesktopPluginOwner = true;
            // Retire before unmounting: teardown callbacks cannot acquire a replacement owner.
            DisposeDesktopPlugins(preservePersistentTextEntries: true);
            if (Lifetime.IsCancellationRequested || desktopPluginDisposalRequested) return false;
            CreateDesktopPluginRuntime();
            return !desktopPluginDisposalRequested && desktopPluginOwner?.ObserveMatchesOwner() == true;
        }
        finally { changingDesktopPluginOwner = false; }
    }

    private void ReportDesktopPluginRefreshError(Exception error)
    {
        var wasChanging = changingDesktopPluginOwner;
        try
        {
            changingDesktopPluginOwner = true;
            SetStatus("Desktop plugins could not refresh: " + error.Message, true);
        }
        finally { changingDesktopPluginOwner = wasChanging; }
    }

    private DesktopProductPaths DesktopProductPathsSnapshot()
    {
        return DesktopInstancePaths.Current.ProductPaths(projectsDirectory, modelDirectory, toolsDirectory, sharedDirectory);
    }

    private void ResetDesktopConversation()
    {
        InvalidateComposerSuggestion(); observedComposerResponse = null; composerSuggestionResponse = null;
        CloseMainChatTiming(MainChatTimingEnd.SelectionChanged);
        pluginGeneration = Guid.NewGuid().ToString("N"); presentedPluginMessages.Clear();
        pluginMessageBaseline = IsNativeChat && nativeChat?.Snapshot?.RunId is null; pendingSteering = null;
        RestoreDesktopInputMode();
        if (attachmentDrafts.Count == 0) attachmentSelections.Clear();
        RenderAttachmentDrafts();
        desktopPluginHost?.PublishConversationChanged();
        _ = RefreshConversationWorkspaceAsync();
    }

    private DesktopMessageSnapshot PluginMessage(SessionMessage item) => new(item.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), PluginBinding(), item.Role,
        item.Content.Length <= DesktopPluginContract.MaxTextChars ? item.Content : item.Content[..DesktopPluginContract.MaxTextChars], item.At, item.Content.Length > DesktopPluginContract.MaxTextChars);
    private void PresentDesktopMessage(SessionMessage item)
    {
        if (item.Role == "assistant" && !item.Content.StartsWith("Question: ", StringComparison.Ordinal) && presentedPluginMessages.Add(item.Sequence.ToString()) && pluginMessageBaseline)
            desktopPluginHost?.PublishLeadMessageCommitted(PluginMessage(item));
    }

    private async Task<DesktopOperationResult> WithDesktopInputGate(DesktopSubmission input, CancellationToken ct, Func<DesktopSubmission, CancellationToken, Task<DesktopOperationResult>> action)
    {
        if (!await desktopInputGate.WaitAsync(0, ct)) return new(DesktopOperationState.Rejected, "Another input is being submitted. Wait for its receipt.", input.RequestId);
        try { return await action(input, ct); }
        finally { desktopInputGate.Release(); }
    }

    private async Task<DesktopOperationResult> SubmitDesktopInput(DesktopSubmission input, CancellationToken ct)
        => await SubmitDesktopInput(input, null, null, ct);

    private Task<DesktopOperationResult> SubmitDesktopWorkflow(string pluginId, DesktopWorkflowSubmission input, CancellationToken ct) =>
        input.Target is null
            ? SubmitDesktopInput(new(input.Binding, input.RequestId, input.VisibleText), input.Context, pluginId, ct)
            : SubmitExplicitDesktopWorkflow(input, ct);

    private async Task<DesktopOperationResult> SubmitDesktopInput(DesktopSubmission input, string? workflowContext, string? workflowSource, CancellationToken ct)
        => await SubmitNativeDesktopInput(input, workflowContext, ct);

    private async Task<DesktopOperationResult> SteerDesktopInput(DesktopSubmission input, CancellationToken ct)
    {
        if (input.Binding != PluginBinding()) return new(DesktopOperationState.Rejected, "The conversation changed.", input.RequestId);
        if (IsNativeChat) return await SteerNativeDesktopInput(input, ct);
        var current = CurrentJob(); if (current is null) return new(DesktopOperationState.Rejected, "There is no active assignment.", input.RequestId);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token);
            var result = await Client.CommandAsync("jobs.steer", new { id = current.Id, prompt = input.Text, requestId = input.RequestId }, cancellationToken: linked.Token);
            if (!result.Success) return new(DesktopOperationState.Rejected, result.Error, input.RequestId, current.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or OperationCanceledException)
        { return new(DesktopOperationState.Uncertain, "Steering could not be confirmed. Retry with the same request ID. " + ex.Message, input.RequestId, current.Id); }
        if (pendingSteering?.RequestId == input.RequestId) pendingSteering = null;
        if (input.Binding == PluginBinding())
        {
            if (prompt.Text == input.Text) prompt.Clear();
            try { var fresh = await Get<TaskSession>("sessions.get", new { id = current.SessionId }); if (input.Binding == PluginBinding()) { session = fresh; RenderSession(); await RefreshActivityAsync(); } }
            catch (Exception ex) { SetStatus("Steering was accepted, but the display could not refresh: " + ex.Message, true); }
        }
        return new(DesktopOperationState.Accepted, "Steering saved for the next safe step.", input.RequestId, current.Id);
    }

    private async Task<DesktopOperationResult> StopDesktopConversation(DesktopConversationBinding binding, CancellationToken ct)
    {
        if (binding != PluginBinding() || binding.SessionId is null) return new(DesktopOperationState.Rejected, "Open the current conversation before stopping it.");
        if (IsNativeChat) return await StopNativeChatAsync(ct);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token);
            var result = await Client.CommandAsync("sessions.stop", new { id = binding.SessionId }, cancellationToken: linked.Token);
            if (!result.Success) return new(DesktopOperationState.Failed, result.Error);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or OperationCanceledException)
        { return new(DesktopOperationState.Uncertain, "The stop request could not be confirmed. " + ex.Message); }
        try { await RefreshJobs(); }
        catch (Exception ex) { SetStatus("Stop was accepted, but the display could not refresh: " + ex.Message, true); }
        return new(DesktopOperationState.Accepted, "Stop requested, including helpers and owned commands. Remaining stops are shown in Activity.");
    }
}
