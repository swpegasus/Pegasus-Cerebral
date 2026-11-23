using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private void ConfigureDesktopWorkspace()
    {
        desktopPluginHost!.ConfigureWorkspace(new DesktopWorkspaceCallbacks(
            (limit, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var saved = (conversations.ItemsSource as IEnumerable<TaskSession> ?? []).ToList();
                var ordered = saved.OrderByDescending(x => x.UpdatedAt).Take(limit).Select(x => new DesktopConversationSummary(x.Id, x.Title, x.ProjectId)).ToList();
                return Task.FromResult(new DesktopConversationList(ordered, saved.Count > limit));
            },
            async (id, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (NativeMutationPending || NativeMutationUncertain && CurrentConversationId != id)
                    return new DesktopOperationResult(DesktopOperationState.Rejected, "Wait for request acceptance, or refresh its unconfirmed outcome, before changing conversations.");
                var expectedSelection = conversationSelectionVersion + 1;
                await SelectSession(id, ct);
                if (conversationSelectionVersion != expectedSelection || CurrentConversationId != id)
                    return new DesktopOperationResult(DesktopOperationState.Rejected, "A newer conversation selection replaced this request.");
                var selectedBinding = PluginBinding();
                await RefreshSessions();
                if (selectedBinding == PluginBinding() && !ct.IsCancellationRequested) SelectPage("chat");
                return new DesktopOperationResult(DesktopOperationState.Completed);
            },
            (projectId, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (NativeMutationPending || NativeMutationUncertain)
                    return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Wait for request acceptance, or refresh its unconfirmed outcome, before starting a new conversation."));
                if (projectId is not null && !EffectiveNavigation().Any(x => x.Id == projectId && x.Kind == "project"))
                    return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Select an existing workspace project."));
                NewConversationInProject(projectId); SelectPage("chat");
                return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
            },
            async (binding, title, ct) =>
            {
                if (binding.SessionId is null) return new DesktopOperationResult(DesktopOperationState.Rejected, "Save a conversation before renaming it.");
                if (durableConversations.ContainsKey(binding.SessionId) || binding.SessionId == durableConversationId)
                    return await RenameDurableConversationFromWorkspaceAsync(binding, title, ct);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token);
                CommandResult result;
                try
                {
                    result = await Client.CommandAsync("sessions.rename", new { id = binding.SessionId, title }, cancellationToken: linked.Token);
                    if (!result.Success) return new DesktopOperationResult(DesktopOperationState.Rejected, result.Error);
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or OperationCanceledException)
                { return new DesktopOperationResult(DesktopOperationState.Uncertain, "The rename could not be confirmed. " + ex.Message); }
                try
                {
                    var renamed = CerebralJson.Read<TaskSession>(result.Data);
                    if (binding == PluginBinding() && session is not null) { session.Title = renamed.Title; sessionHeading.Text = renamed.Title; }
                    await RefreshSessions(); desktopPluginHost.PublishConversationChanged();
                }
                catch (Exception ex) { SetStatus("The conversation was renamed, but the display could not refresh: " + ex.Message, true); }
                return new DesktopOperationResult(DesktopOperationState.Completed, "Conversation renamed.");
            }));
    }
}
