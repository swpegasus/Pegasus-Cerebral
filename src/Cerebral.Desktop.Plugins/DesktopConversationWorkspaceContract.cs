using System.Windows;

namespace Cerebral.Desktop.Plugins;

/// <summary>
/// Small durable coordinate that lets a conversation restore a plugin-owned primary workspace. The plugin keeps
/// domain content and view state in its own service-owned store; Cerebral owns the conversation and composer.
/// </summary>
public sealed record DesktopConversationWorkspaceBinding(
    string ConversationId,
    string PluginId,
    string SurfaceId,
    string ResourceId,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record DesktopConversationWorkspaceContext(
    DesktopConversationSnapshot Conversation,
    DesktopConversationWorkspaceBinding Binding);

/// <summary>A lazily-created primary conversation surface. Each mount must return a new unparented element.</summary>
public sealed record DesktopConversationWorkspace(
    string Id,
    string Title,
    Func<DesktopConversationWorkspaceContext, CancellationToken, Task<FrameworkElement>> CreateViewAsync);

/// <summary>Optional Orchestrator surfaces that replace the center projection while retaining Conversation fallback.</summary>
public interface IDesktopConversationWorkspacePlugin
{
    IReadOnlyList<DesktopConversationWorkspace> ConversationWorkspaces { get; }
}

public sealed record DesktopConversationWorkspaceResult(
    DesktopOperationState State,
    DesktopConversationWorkspaceBinding? Binding = null,
    string? Message = null);

/// <summary>
/// Owner-routed binding operations. The scoped host supplies the calling plugin identity, so a plugin cannot bind a
/// conversation to another package or forge a surface owner.
/// </summary>
public interface IDesktopConversationWorkspaceHost
{
    Task<DesktopConversationWorkspaceResult> BindConversationWorkspaceAsync(
        DesktopConversationBinding expectedBinding,
        string surfaceId,
        string resourceId,
        long expectedRevision = 0,
        CancellationToken cancellationToken = default);

    Task<DesktopConversationWorkspaceResult> ClearConversationWorkspaceAsync(
        DesktopConversationBinding expectedBinding,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
