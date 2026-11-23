using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// Persists the small conversation-to-plugin workspace coordinate. Plugin domain state remains outside this record.
/// </summary>
internal sealed class ConversationWorkspaces(StateStore store, WorkerWorkflows? workflows, EventHub events)
{
    private const string Kind = "conversation-workspace";

    public ConversationWorkspaceBinding? Get(string conversationId)
    {
        ValidateText(conversationId, nameof(conversationId), 128);
        EnsureConversation(conversationId);
        return store.Get<ConversationWorkspaceBinding>(Kind, conversationId);
    }

    public ConversationWorkspaceBinding Set(string conversationId, string pluginId, string surfaceId,
        string resourceId, long expectedRevision)
    {
        ValidateText(conversationId, nameof(conversationId), 128);
        ValidateText(pluginId, nameof(pluginId), 128);
        ValidateText(surfaceId, nameof(surfaceId), 64);
        ValidateText(resourceId, nameof(resourceId), 256);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        EnsureConversation(conversationId);
        var saved = store.Locked(() =>
        {
            var current = store.Get<ConversationWorkspaceBinding>(Kind, conversationId);
            var revision = current?.Revision ?? 0;
            if (revision != expectedRevision)
                throw new InvalidOperationException("The conversation workspace changed. Refresh it before replacing the binding.");
            var value = new ConversationWorkspaceBinding(conversationId, pluginId, surfaceId, resourceId,
                checked(revision + 1), DateTimeOffset.UtcNow);
            store.Put(Kind, conversationId, value);
            return value;
        });
        events.Publish("conversation.workspace.updated", null, new { conversationId, revision = saved.Revision });
        return saved;
    }

    public object Clear(string conversationId, long expectedRevision)
    {
        ValidateText(conversationId, nameof(conversationId), 128);
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        EnsureConversation(conversationId);
        store.Locked(() =>
        {
            var current = store.Get<ConversationWorkspaceBinding>(Kind, conversationId)
                ?? throw new InvalidOperationException("This conversation has no workspace binding.");
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("The conversation workspace changed. Refresh it before clearing the binding.");
            store.Delete(Kind, conversationId);
            return true;
        });
        events.Publish("conversation.workspace.updated", null, new { conversationId, revision = expectedRevision + 1, cleared = true });
        return new { conversationId, cleared = true, revision = expectedRevision + 1 };
    }

    private void EnsureConversation(string id)
    {
        if (store.GetSessionMetadata(id) is not null) return;
        if (workflows?.Store.GetConversation(ConversationWorkflowModule.DefinitionId, id) is not null) return;
        throw new ArgumentException("Conversation not found.");
    }

    private static void ValidateText(string value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException($"{name} must be nonempty, contain no control characters, and be at most {maximum} characters.", name);
    }
}
