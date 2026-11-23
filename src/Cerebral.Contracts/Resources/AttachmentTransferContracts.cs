namespace Cerebral.Contracts.Resources;

public sealed record AttachmentImportBeginRequest(
    string RequestId, string ProfileId, string ConversationId, string OriginalName, long Length);

/// <summary>Bytes are bounded transport data, never a message, event, receipt payload, or model input.</summary>
public sealed record AttachmentImportChunkRequest(
    string RequestId, string ProfileId, string ConversationId, string UploadId, long Offset, byte[] Bytes);

public sealed record AttachmentImportUploadRequest(
    string RequestId, string ProfileId, string ConversationId, string UploadId);

public sealed record AttachmentImportStatusRequest(string RequestId, string ProfileId, string ConversationId);

/// <summary>State is receiving, committing, complete, missing, or aborted; only complete carries an immutable original.</summary>
public sealed record AttachmentImportStatus(
    string RequestId, string State, string? UploadId, long ReceivedBytes, long? Length, AttachmentReference? Attachment = null);

public sealed record AttachmentDescribeRequest(string ProfileId, string ConversationId, string Id, long Revision);
public sealed record AttachmentRangeRequest(string ProfileId, string ConversationId, string Id, long Revision, long Offset, int Count);
public sealed record AttachmentTextRequest(string ProfileId, string ConversationId, string Id, long Revision, int MaximumUtf8Bytes = 1024 * 1024);

public sealed record AttachmentArchiveRequest(string ProfileId, string ConversationId, string Id, long Revision);
public sealed record AttachmentArchiveTextRequest(
    string ProfileId, string ConversationId, string Id, long Revision, string EntryPath,
    int MaximumUtf8Bytes = 1024 * 1024);
