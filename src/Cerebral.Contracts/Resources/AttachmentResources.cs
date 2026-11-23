namespace Cerebral.Contracts.Resources;

/// <summary>
/// Host-established access scope. Construct this from the authenticated caller and selected conversation;
/// copying these strings from a model request does not establish authorization.
/// </summary>
public sealed record AttachmentAccessContext(string ProfileId, string ConversationId);

/// <summary>An exact byte interval, for host/provider transport only. Never append binary bytes to chat events.</summary>
public sealed record AttachmentRange(AttachmentReference Attachment, long Offset, ReadOnlyMemory<byte> Bytes);

/// <summary>Complete strictly decoded UTF-8 text and its original immutable source coordinate.</summary>
public sealed record AttachmentText(AttachmentReference Attachment, string Text);

/// <summary>One sanitized archive member. Paths are relative display identities, never host paths.</summary>
public sealed record AttachmentArchiveEntry(string Path, long Length, bool Directory);

/// <summary>Bounded inspection of an immutable archive original; no member has been executed or added to model context.</summary>
public sealed record AttachmentArchiveInspection(
    AttachmentReference Attachment, IReadOnlyList<AttachmentArchiveEntry> Entries,
    long ExpandedBytes, bool Truncated = false);

/// <summary>A disposable derivative of one archive revision. The immutable original remains independently retained.</summary>
public sealed record AttachmentArtifactReceipt(
    string AttachmentId, long AttachmentRevision, string ProfileId, string ConversationId,
    string State, IReadOnlyList<AttachmentArchiveEntry> Entries, long ExpandedBytes,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? Error = null);

/// <summary>Strict UTF-8 from one exact extracted member, attributed to its immutable source coordinate.</summary>
public sealed record AttachmentArtifactText(
    string AttachmentId, long AttachmentRevision, string EntryPath, string Text);
