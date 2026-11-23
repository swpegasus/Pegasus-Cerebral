namespace Cerebral.Desktop.Plugins;

public sealed record DesktopConversationSummary(string SessionId, string Title, string? ProjectId);
public sealed record DesktopConversationList(IReadOnlyList<DesktopConversationSummary> Conversations, bool Truncated = false);

/// <summary>Optional thread navigation and metadata surface. Cast the supplied host and check WorkspaceAvailable.</summary>
public interface IDesktopWorkspaceHost
{
    bool WorkspaceAvailable { get; }
    Task<DesktopConversationList> ListConversationsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<DesktopOperationResult> SelectConversationAsync(DesktopConversationBinding expectedBinding, string sessionId, CancellationToken cancellationToken = default);
    Task<DesktopOperationResult> NewConversationAsync(DesktopConversationBinding expectedBinding, string? projectId = null, CancellationToken cancellationToken = default);
    Task<DesktopOperationResult> RenameConversationAsync(DesktopConversationBinding binding, string title, CancellationToken cancellationToken = default);
}
