namespace Cerebral.Engine.Resources;

/// <summary>Aggregate admission limits. Reducing a limit preserves originals and permits exact idempotent retries.</summary>
public sealed record AttachmentStoreOptions(
    long MaximumStoredBytes = 1024L * 1024 * 1024,
    int MaximumAttachmentsPerConversation = 256);
