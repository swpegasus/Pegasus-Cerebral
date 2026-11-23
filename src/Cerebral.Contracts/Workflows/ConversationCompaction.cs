namespace Cerebral.Contracts.Workflows;

/// <summary>
/// Owner request to compact retained context once before the next ordinary Chat turn. The request
/// is service-owned and never appears as user-authored transcript content.
/// </summary>
public sealed record ConversationCompactionRequest(string RequestId, string DefinitionId,
    string ConversationId, long ExpectedConversationRevision);

/// <summary>A durable request receipt. ConsumedByRunId is set only after the next turn is accepted.</summary>
public sealed record ConversationCompactionReceipt(string RequestId, string ConversationId,
    long RequestedAtRevision, DateTimeOffset RequestedAt, string? ConsumedByRunId = null);
