namespace Cerebral.Contracts.Collaboration;

public static class ConversationCommunicationContract
{
    public const string Id = "pegasus.cerebral.conversation-communication";
    public const int Version = 1;
    public const int MaximumPageSize = 128;
    public const int MaximumPendingRequests = 256;
}

/// <summary>The store returns an ordinally sorted pair. Permission is symmetric and defaults to denied.</summary>
public sealed record ConversationCommunicationPair(string FirstConversationId, string SecondConversationId);

public sealed record ConversationCommunicationPendingRequest(
    string RequestId, string SourceConversationId, DateTimeOffset RequestedAt);

/// <summary>Permission to communicate only. This grants no history, context, project, resource, file or tool access.</summary>
public sealed record ConversationCommunicationPermission(
    ConversationCommunicationPair Pair, bool Allowed, long Revision,
    ConversationCommunicationPendingRequest? PendingRequest = null, DateTimeOffset? UpdatedAt = null);

/// <summary>The host must bind SourceConversationId to the exact authenticated caller chat.
/// A profile ID is not caller-chat proof. Requesting cannot allow communication.</summary>
public sealed record ConversationCommunicationRequest(
    string RequestId, string SourceConversationId, string TargetConversationId, long ExpectedRevision);

/// <summary>Installation-owner operation. The host must authorize the owner before calling the store.
/// When deciding a displayed request, include its exact PendingRequestId as well as the displayed revision.</summary>
public sealed record ConversationCommunicationSetAllowedRequest(
    ConversationCommunicationPair Pair, long ExpectedRevision, bool Allowed, string? PendingRequestId = null);

/// <summary>Keyset page of current state, not an immutable snapshot. Re-read and use CAS before a decision.</summary>
public sealed record ConversationCommunicationPage(
    IReadOnlyList<ConversationCommunicationPermission> Permissions, ConversationCommunicationPair? NextAfterPair);

/// <summary>Navigation coordinates only; no title derived from messages, transcript, workflow payload or context.</summary>
public sealed record ConversationCommunicationDirectoryEntry(string ConversationId, string ProfileId, string? ProjectId);

public sealed record ConversationCommunicationDirectoryPage(
    IReadOnlyList<ConversationCommunicationDirectoryEntry> Conversations, string? NextAfterConversationId);
