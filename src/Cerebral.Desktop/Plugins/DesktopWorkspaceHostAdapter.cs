using System.Windows.Threading;

namespace Cerebral.Desktop.Plugins;

/// <summary>Application callbacks for its existing draft-preserving thread navigation and rename flows.</summary>
public sealed record DesktopWorkspaceCallbacks(
    Func<int, CancellationToken, Task<DesktopConversationList>> ListConversations,
    Func<string, CancellationToken, Task<DesktopOperationResult>> SelectConversation,
    Func<string?, CancellationToken, Task<DesktopOperationResult>> NewConversation,
    Func<DesktopConversationBinding, string, CancellationToken, Task<DesktopOperationResult>> RenameConversation);

public sealed partial class DesktopPluginHostAdapter
{
    private DesktopWorkspaceCallbacks? workspace;
    public bool WorkspaceAvailable => workspace is not null;
    public void ConfigureWorkspace(DesktopWorkspaceCallbacks callbacks)
    {
        DesktopDispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(callbacks);
        if (workspace is not null) throw new InvalidOperationException("The desktop workspace adapter is already configured.");
        workspace = callbacks;
    }

    public Task<DesktopConversationList> ListConversationsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Conversation lists are limited to 1–500 entries.");
        cancellationToken.ThrowIfCancellationRequested();
        async Task<DesktopConversationList> List()
        {
            var callbacks = workspace ?? throw new NotSupportedException("This host has no workspace navigation adapter.");
            var value = await callbacks.ListConversations(limit, cancellationToken);
            if (value.Conversations is null) throw new InvalidOperationException("The workspace returned no conversation list.");
            var bounded = value.Conversations.Take(limit).Select(item => item with
            {
                Title = item.Title.Length > 500 ? item.Title[..500] : item.Title
            }).ToList();
            return new(bounded, value.Truncated || value.Conversations.Count > limit);
        }
        return DesktopDispatcher.CheckAccess() ? List() : DesktopDispatcher.InvokeAsync(List, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    public Task<DesktopOperationResult> SelectConversationAsync(DesktopConversationBinding expectedBinding, string sessionId, CancellationToken cancellationToken = default) =>
        OnDispatcher(async () =>
        {
            if (CheckBinding(expectedBinding) is { } invalid) return invalid;
            if (workspace is null) return Rejected("This host has no workspace navigation adapter.");
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128) return Rejected("Choose a valid conversation ID.");
            return await workspace.SelectConversation(sessionId, cancellationToken);
        }, cancellationToken);

    public Task<DesktopOperationResult> NewConversationAsync(DesktopConversationBinding expectedBinding, string? projectId = null, CancellationToken cancellationToken = default) =>
        OnDispatcher(async () =>
        {
            if (CheckBinding(expectedBinding) is { } invalid) return invalid;
            if (workspace is null) return Rejected("This host has no workspace navigation adapter.");
            if (projectId is not null && (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 128)) return Rejected("Choose a valid project ID.");
            return await workspace.NewConversation(projectId, cancellationToken);
        }, cancellationToken);

    public Task<DesktopOperationResult> RenameConversationAsync(DesktopConversationBinding binding, string title, CancellationToken cancellationToken = default) =>
        OnDispatcher(async () =>
        {
            if (CheckBinding(binding) is { } invalid) return invalid;
            if (workspace is null) return Rejected("This host has no workspace navigation adapter.");
            if (binding.SessionId is null) return Rejected("Save the conversation before renaming it.");
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return Rejected("Conversation titles must contain 1–200 characters.");
            return await workspace.RenameConversation(binding, title.Trim(), cancellationToken);
        }, cancellationToken);
}

internal partial class ScopedDesktopPluginHost
{
    public bool WorkspaceAvailable => !disposed && source.WorkspaceAvailable;
    public async Task<DesktopConversationList> ListConversationsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        return await source.ListConversationsAsync(limit, linked.Token);
    }
    public Task<DesktopOperationResult> SelectConversationAsync(DesktopConversationBinding expectedBinding, string sessionId, CancellationToken cancellationToken = default) =>
        Run(token => source.SelectConversationAsync(expectedBinding, sessionId, token), cancellationToken);
    public Task<DesktopOperationResult> NewConversationAsync(DesktopConversationBinding expectedBinding, string? projectId = null, CancellationToken cancellationToken = default) =>
        Run(token => source.NewConversationAsync(expectedBinding, projectId, token), cancellationToken);
    public Task<DesktopOperationResult> RenameConversationAsync(DesktopConversationBinding binding, string title, CancellationToken cancellationToken = default) =>
        Run(token => source.RenameConversationAsync(binding, title, token), cancellationToken);
}
