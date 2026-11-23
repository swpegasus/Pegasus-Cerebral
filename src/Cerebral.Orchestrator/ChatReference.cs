using System.IO;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

/// <summary>Local saved-chat references identify a chat inside the current authenticated dataset; they grant no access.</summary>
internal static class ChatReference
{
    private const string Prefix = "cerebral://";

    internal static string? Create(string id) =>
        TrySavedGuid(id, out var guid) ? Prefix + guid.ToString("D") : null;

    internal static bool IsReference(string target) => target.StartsWith("cerebral:", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 128 || target.Any(char.IsControl))
            throw new ArgumentException("Provide an exact saved chat ID or a cerebral://GUID reference.");
        if (!IsReference(target)) return target; // Opaque native IDs remain exact; names never become lookup keys.
        if (!target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
            target.Length != Prefix.Length + 36 || !Guid.TryParseExact(target[Prefix.Length..], "D", out var guid))
            throw new ArgumentException("Chat references use cerebral:// followed by one GUID in standard hyphenated format.");
        return guid.ToString("N");
    }

    internal static async Task<ConversationSummary> ReadSavedAsync(string target,
        Func<string, CancellationToken, Task<ConversationSummary>> read, Func<bool> current, CancellationToken token)
    {
        var id = NormalizeTarget(target);
        token.ThrowIfCancellationRequested();
        if (!current()) throw new InvalidOperationException("The chat connection changed. Reopen the reference on the current connection.");
        var saved = await read(id, token);
        token.ThrowIfCancellationRequested();
        if (!current()) throw new InvalidOperationException("The chat connection changed while resolving the reference.");
        if (saved is null || saved.Id != id || string.IsNullOrWhiteSpace(saved.LatestRunId) ||
            string.IsNullOrWhiteSpace(saved.Title))
            throw new InvalidDataException("The service did not return this exact saved chat. No chat was opened or created.");
        return saved;
    }

    private static bool TrySavedGuid(string id, out Guid guid)
    {
        guid = default;
        return id.Length == 32 && Guid.TryParseExact(id, "N", out guid) ||
            id.Length == 36 && Guid.TryParseExact(id, "D", out guid);
    }
}

public sealed partial class OrchestratorWindow
{
    private async Task OpenSavedChatReferenceAsync(string target, CancellationToken cancellationToken)
    {
        var id = ChatReference.NormalizeTarget(target);
        if (!ChatReference.IsReference(target))
        {
            await SelectSession(id, cancellationToken); // Preserve native opaque IDs and existing previous-generation routing.
            return;
        }
        EnsureAttachmentNavigationAllowed(); EnsureConversationControlsSettled();
        if (!IsConnected) throw new InvalidOperationException("Connect to the dataset that contains this saved chat before opening its reference.");
        var connection = ServiceConnection;
        var identity = ServiceConnectionIdentity;
        var source = knowledgeConnectionVersion;
        var selection = conversationSelectionVersion;
        bool Current() => !Lifetime.IsCancellationRequested && IsConnected &&
            ReferenceEquals(connection, ServiceConnection) && identity == ServiceConnectionIdentity &&
            source == knowledgeConnectionVersion && selection == conversationSelectionVersion;
        using var captured = CreateIndependentClient();
        using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token);
        readLifetime.CancelAfter(TimeSpan.FromSeconds(30));
        var saved = await ChatReference.ReadSavedAsync(target,
            (savedId, token) => captured.GetAsync<ConversationSummary>("conversations.get", new { id = savedId }, cancellationToken: token),
            Current, readLifetime.Token);
        // Resolve first, then reuse the existing selection path. An absent reference never creates a conversation.
        durableConversations[saved.Id] = saved;
        await SelectSession(saved.Id, cancellationToken);
        if (!IsConnected || !ReferenceEquals(connection, ServiceConnection) || identity != ServiceConnectionIdentity ||
            source != knowledgeConnectionVersion || CurrentConversationId != saved.Id)
            throw new InvalidOperationException("The resolved chat could not be confirmed on the current connection. Refresh before retrying.");
    }
}
