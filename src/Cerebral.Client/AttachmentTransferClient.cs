using System.Collections.ObjectModel;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Core;

namespace Cerebral.Client;

public sealed record AttachmentTransferSnapshot(AttachmentImportBeginRequest Request,
    AttachmentImportStatus? Status, bool Busy, bool Uncertain, string? Error)
{
    public AttachmentReference? Attachment => Status?.State == "complete" ? Status.Attachment : null;
}

public sealed class AttachmentTransferCommandException(string message) : InvalidOperationException(message);

/// <summary>
/// One immutable import request bound to one captured service transport and profile/conversation.
/// Explicit retry reads status first and uses the original request ID. The caller owns the seekable
/// source stream and must prevent its bytes changing while an incomplete upload may be resumed.
/// No file paths or original binary payloads are retained by this adapter.
/// </summary>
public sealed class AttachmentTransferClient
{
    public const int MaximumChunkBytes = 256 * 1024;
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    private const string Prefix = "resources.attachments.import.";
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> command;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AttachmentTransferSnapshot snapshot;

    public AttachmentTransferClient(AttachmentImportBeginRequest request, CerebralClient client)
        : this(request, (input, token) => client.CommandAsync(input.Operation, input.Arguments,
            input.ProfileId, token)) { ArgumentNullException.ThrowIfNull(client); }

    public AttachmentTransferClient(AttachmentImportBeginRequest request,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> command)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(command);
        Identifier(request.RequestId); Identifier(request.ProfileId); Identifier(request.ConversationId);
        if (string.IsNullOrWhiteSpace(request.OriginalName) || request.OriginalName.Length > 255 ||
            request.OriginalName.Any(c => char.IsControl(c) || c is '/' or '\\' or ':') ||
            request.Length is < 0 or > MaximumFileBytes)
            throw new ArgumentException("Use a bounded file display name and a declared length of at most 16 MiB.");
        this.command = command; snapshot = new(request, null, false, false, null);
    }

    public AttachmentTransferSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event EventHandler? Changed;

    /// <summary>Read-only reconciliation. Missing does not disprove a previously uncertain mutation.</summary>
    public Task<AttachmentTransferSnapshot> RefreshAsync(CancellationToken token = default) =>
        Serialized(async () => { await RefreshCore(token).ConfigureAwait(false); }, token);

    /// <summary>
    /// Explicitly begin/resume the selected original. A completed receipt is resolved before source access,
    /// so an already committed file never needs reopening. Incomplete uploads require the same immutable bytes.
    /// Each command is sent once; transport errors retain identity for explicit reconciliation.
    /// </summary>
    public Task<AttachmentTransferSnapshot> UploadAsync(Stream? source, CancellationToken token = default) =>
        Serialized(async () =>
        {
            await RefreshCore(token).ConfigureAwait(false);
            if (snapshot.Attachment is not null || snapshot.Status?.State == "committing") return;
            var request = snapshot.Request;
            if (source is null || !source.CanRead || !source.CanSeek || source.Length != request.Length)
                throw new InvalidOperationException("The original selected file is required and must retain its declared length. Reconcile before selecting a different file.");
            if (snapshot.Status?.State == "missing")
                await Mutate("begin", request, token).ConfigureAwait(false);
            if (snapshot.Attachment is not null || snapshot.Status?.State == "committing") return;
            var status = snapshot.Status ?? throw new InvalidDataException("Upload begin returned no state.");
            if (status.State != "receiving" || status.UploadId is null)
                throw new InvalidDataException("Upload is not ready to receive original bytes.");
            source.Position = status.ReceivedBytes;
            var buffer = new byte[MaximumChunkBytes];
            try
            {
                while (source.Position < request.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var offset = source.Position;
                    var size = (int)Math.Min(buffer.Length, request.Length - offset);
                    await source.ReadExactlyAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false);
                    var sent = buffer.AsSpan(0, size).ToArray();
                    try
                    {
                        await Mutate("chunk", new AttachmentImportChunkRequest(request.RequestId, request.ProfileId,
                            request.ConversationId, status.UploadId, offset, sent), token).ConfigureAwait(false);
                    }
                    finally { Array.Clear(sent); }
                    if (snapshot.Status is not { State: "receiving" } received || received.UploadId != status.UploadId ||
                        received.ReceivedBytes != offset + size)
                        throw new InvalidDataException("Chunk receipt does not confirm the exact sent interval.");
                }
            }
            finally { Array.Clear(buffer); }
            if (source.Length != request.Length) throw new InvalidOperationException("The selected source length changed during upload.");
            await Mutate("commit", new AttachmentImportUploadRequest(request.RequestId, request.ProfileId,
                request.ConversationId, status.UploadId), token).ConfigureAwait(false);
        }, token);

    /// <summary>Remove only partial staging. A committed original is returned unchanged for the caller to deselect.</summary>
    public Task<AttachmentTransferSnapshot> AbortAsync(CancellationToken token = default) =>
        Serialized(async () =>
        {
            await RefreshCore(token).ConfigureAwait(false);
            if (snapshot.Attachment is not null) return;
            if (snapshot.Status?.State == "missing")
            {
                if (snapshot.Uncertain)
                    throw new InvalidOperationException("The original operation remains unconfirmed. Keep its request ID and query this service again before discarding it.");
                return;
            }
            if (snapshot.Status is not { State: "receiving", UploadId: { } uploadId })
                throw new InvalidOperationException("Attachment commit is still in progress. Refresh its receipt before removing the draft reference.");
            var request = snapshot.Request;
            await Mutate("abort", new AttachmentImportUploadRequest(request.RequestId, request.ProfileId,
                request.ConversationId, uploadId), token).ConfigureAwait(false);
        }, token);

    private async Task RefreshCore(CancellationToken token)
    {
        var request = snapshot.Request;
        var status = await Send("status", new AttachmentImportStatusRequest(request.RequestId, request.ProfileId,
            request.ConversationId), token).ConfigureAwait(false);
        var remainsUncertain = status.State is "committing" || status.State == "missing" && snapshot.Uncertain;
        Publish(snapshot with { Status = status, Uncertain = remainsUncertain, Error = null });
    }

    private async Task Mutate(string operation, object input, CancellationToken token)
    {
        var wasUncertain = snapshot.Uncertain;
        Publish(snapshot with { Uncertain = true });
        try
        {
            var status = await Send(operation, input, token).ConfigureAwait(false);
            if (input is AttachmentImportChunkRequest chunk &&
                (status.State != "receiving" || status.UploadId != chunk.UploadId || status.ReceivedBytes != chunk.Offset + chunk.Bytes.Length) ||
                operation == "begin" && status.State is not ("receiving" or "complete") ||
                operation == "commit" && status.State is not ("committing" or "complete") ||
                operation == "abort" && status.State is not ("aborted" or "missing" or "complete"))
                throw new InvalidDataException("Attachment receipt does not confirm the requested operation and exact interval.");
            Publish(snapshot with { Status = status, Uncertain = status.State == "committing", Error = null });
        }
        catch (AttachmentTransferCommandException)
        {
            // An explicit retry rejection cannot erase uncertainty about an earlier lost receipt.
            Publish(snapshot with { Uncertain = wasUncertain });
            throw;
        }
    }

    private async Task<AttachmentImportStatus> Send(string operation, object input, CancellationToken token)
    {
        var result = await command(new(Prefix + operation, CerebralJson.Element(input)), token).ConfigureAwait(false);
        if (!result.Success) throw new AttachmentTransferCommandException(result.Error ?? "Attachment service rejected the request.");
        AttachmentImportStatus status;
        try { status = CerebralJson.Read<AttachmentImportStatus>(result.Data); }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidDataException("Attachment service returned an invalid receipt.", error); }
        return Validate(status);
    }

    private AttachmentImportStatus Validate(AttachmentImportStatus status)
    {
        var request = snapshot.Request;
        if (status.RequestId != request.RequestId || status.State is not ("missing" or "receiving" or "committing" or "complete" or "aborted") ||
            status.ReceivedBytes < 0 || status.ReceivedBytes > request.Length || status.Length is { } length && length != request.Length)
            throw new InvalidDataException("Attachment receipt does not match the selected request and length.");
        if (status.State is "receiving" or "committing")
        {
            if (!Guid.TryParseExact(status.UploadId, "N", out var id) || id == Guid.Empty ||
                status.Length != request.Length || status.Attachment is not null ||
                status.State == "committing" && status.ReceivedBytes != request.Length)
                throw new InvalidDataException("Attachment staging receipt is incomplete or inconsistent.");
        }
        if (status.State == "missing" && (status.ReceivedBytes != 0 || status.Length is not null || status.UploadId is not null))
            throw new InvalidDataException("Missing attachment receipt contains inconsistent staging data.");
        if (snapshot.Attachment is not null && status.State != "complete")
            throw new InvalidDataException("The previously confirmed attachment receipt is no longer available on this connection.");
        if (status.State == "complete")
        {
            var reference = status.Attachment;
            if (reference is null || !Guid.TryParseExact(reference.Id, "N", out var id) || id == Guid.Empty || reference.Revision < 1 ||
                reference.Length != request.Length || reference.OriginalName != request.OriginalName ||
                status.ReceivedBytes != request.Length || status.Length != request.Length || status.UploadId is not null ||
                string.IsNullOrWhiteSpace(reference.MediaType) || reference.MediaType.Length > 256 ||
                reference.Metadata is { Count: > 64 })
                throw new InvalidDataException("Committed attachment receipt does not identify the selected immutable original.");
            if (reference.Metadata is { } metadata && metadata.Any(x => string.IsNullOrEmpty(x.Key) || x.Key.Length > 128 || x.Value is null || x.Value.Length > 4096))
                throw new InvalidDataException("Attachment receipt metadata exceeds its bounds.");
            if (snapshot.Attachment is { } prior && (prior.Id != reference.Id || prior.Revision != reference.Revision ||
                !JsonElement.DeepEquals(CerebralJson.Element(prior), CerebralJson.Element(reference))))
                throw new InvalidDataException("Attachment receipt changed its previously confirmed original.");
            status = status with { Attachment = reference with
            {
                Metadata = reference.Metadata is null ? null : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(reference.Metadata, StringComparer.Ordinal))
            } };
        }
        else if (status.Attachment is not null) throw new InvalidDataException("An incomplete upload cannot expose an accepted attachment.");
        return status;
    }

    private async Task<AttachmentTransferSnapshot> Serialized(Func<Task> action, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("This attachment is already being transferred or reconciled.");
        Publish(snapshot with { Busy = true, Error = null });
        try { await action().ConfigureAwait(false); }
        catch (Exception error)
        {
            var message = error.Message;
            Publish(snapshot with { Error = message.Length <= 2048 ? message : message[..2048] });
            throw;
        }
        finally { Publish(snapshot with { Busy = false }); gate.Release(); }
        return snapshot;
    }

    private void Publish(AttachmentTransferSnapshot value) { Volatile.Write(ref snapshot, value); Changed?.Invoke(this, EventArgs.Empty); }
    private static void Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Attachment request and scope identifiers must be bounded and contain no whitespace.");
    }
}
