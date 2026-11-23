using System.Text.Json;
using System.Windows.Threading;

namespace Cerebral.Desktop.Plugins;

public sealed partial class DesktopPluginHostAdapter
{
    internal Task<DesktopConversationWorkspaceResult> BindConversationWorkspaceAsync(string pluginId,
        DesktopConversationBinding expectedBinding, string surfaceId, string resourceId, long expectedRevision,
        CancellationToken cancellationToken) => OnWorkspaceDispatcher(async () =>
    {
        if (CheckBinding(expectedBinding) is { } invalid)
            return new(invalid.State, null, invalid.Message);
        if (expectedBinding.SessionId is null)
            return new(DesktopOperationState.Rejected, null, "Save or open the conversation before selecting a plugin workspace.");
        if (!ValidCoordinate(pluginId, 128) || !ValidCoordinate(surfaceId, 64) || !ValidCoordinate(resourceId, 256) || expectedRevision < 0)
            return new(DesktopOperationState.Rejected, null, "The conversation workspace coordinate is invalid or too large.");
        var response = await ExecuteServiceCommandAsync("conversations.workspace.set", new
        {
            conversationId = expectedBinding.SessionId, pluginId, surfaceId, resourceId, expectedRevision
        }, cancellationToken);
        return WorkspaceResult(response);
    }, cancellationToken);

    internal Task<DesktopConversationWorkspaceResult> ClearConversationWorkspaceAsync(string pluginId,
        DesktopConversationBinding expectedBinding, long expectedRevision, CancellationToken cancellationToken) =>
        OnWorkspaceDispatcher(async () =>
        {
            if (CheckBinding(expectedBinding) is { } invalid)
                return new(invalid.State, null, invalid.Message);
            if (expectedBinding.SessionId is null || expectedRevision < 1)
                return new(DesktopOperationState.Rejected, null, "Open a bound conversation and refresh its workspace before clearing it.");
            var current = await ExecuteServiceCommandAsync("conversations.workspace.get",
                new { conversationId = expectedBinding.SessionId }, cancellationToken);
            if (!current.Success) return new(DesktopOperationState.Failed, null, current.Error);
            var binding = DeserializeBinding(current.Data);
            if (binding is null || binding.PluginId != pluginId)
                return new(DesktopOperationState.Rejected, binding, "Only the plugin that owns this workspace can clear it.");
            var response = await ExecuteServiceCommandAsync("conversations.workspace.clear", new
            {
                conversationId = expectedBinding.SessionId, expectedRevision
            }, cancellationToken);
            return response.Success
                ? new(DesktopOperationState.Completed, null, "Conversation workspace cleared.")
                : new(DesktopOperationState.Failed, null, response.Error);
        }, cancellationToken);

    private static DesktopConversationWorkspaceResult WorkspaceResult(DesktopServiceCommandResult response)
    {
        if (!response.Success) return new(DesktopOperationState.Failed, null, response.Error);
        var binding = DeserializeBinding(response.Data);
        return binding is null
            ? new(DesktopOperationState.Uncertain, null, "The service accepted the workspace binding but returned no coordinate.")
            : new(DesktopOperationState.Completed, binding, "Conversation workspace selected.");
    }

    private static DesktopConversationWorkspaceBinding? DeserializeBinding(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : value.Deserialize<DesktopConversationWorkspaceBinding>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { PropertyNameCaseInsensitive = true });

    private static bool ValidCoordinate(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private Task<DesktopConversationWorkspaceResult> OnWorkspaceDispatcher(
        Func<Task<DesktopConversationWorkspaceResult>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return DesktopDispatcher.CheckAccess() ? action() :
            DesktopDispatcher.InvokeAsync(action, DispatcherPriority.Normal, token).Task.Unwrap();
    }
}

internal partial class ScopedDesktopPluginHost : IDesktopConversationWorkspaceHost
{
    public Task<DesktopConversationWorkspaceResult> BindConversationWorkspaceAsync(
        DesktopConversationBinding expectedBinding, string surfaceId, string resourceId, long expectedRevision = 0,
        CancellationToken cancellationToken = default)
    {
        Check();
        return RunWorkspace(token => source.BindConversationWorkspaceAsync(id, expectedBinding, surfaceId,
            resourceId, expectedRevision, token), cancellationToken);
    }

    public Task<DesktopConversationWorkspaceResult> ClearConversationWorkspaceAsync(
        DesktopConversationBinding expectedBinding, long expectedRevision, CancellationToken cancellationToken = default)
    {
        Check();
        return RunWorkspace(token => source.ClearConversationWorkspaceAsync(id, expectedBinding, expectedRevision, token),
            cancellationToken);
    }

    private async Task<DesktopConversationWorkspaceResult> RunWorkspace(
        Func<CancellationToken, Task<DesktopConversationWorkspaceResult>> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        return await action(linked.Token);
    }
}
