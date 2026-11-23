using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Engine.Resources;

namespace Cerebral.Worker;

/// <summary>
/// Owner-selected file uploads over existing bounded command frames. Partial bytes are volatile, never logged
/// or put in events. Only the immutable attachment store commits originals. Restart loses partial staging;
/// a stable request ID always reconciles committed imports without replaying them.
/// </summary>
public sealed class WorkerAttachmentTransfers : IDisposable
{
    public const string Prefix = "resources.attachments.";
    public const int MaximumUploads = 4;
    public const long MaximumStagingBytes = 64L * 1024 * 1024;
    public const int MaximumChunkBytes = AttachmentStore.MaximumRangeBytes;
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumBase64Characters = ((MaximumChunkBytes + 2) / 3) * 4;
    // System.Text.Json may represent the '+', '=' and other base64 characters as six-character Unicode
    // escapes in raw JSON. The decoded string is checked separately; this limit bounds only its transport form.
    private const int MaximumEscapedChunkEnvelopeCharacters = MaximumBase64Characters * 6 + 16 * 1024;
    private static readonly string[] Operations =
    [
        Prefix + "import.begin", Prefix + "import.chunk", Prefix + "import.commit",
        Prefix + "import.abort", Prefix + "import.status", Prefix + "describe", Prefix + "read", Prefix + "text",
        Prefix + "archive.inspect", Prefix + "archive.extract", Prefix + "archive.status", Prefix + "archive.text"
    ];
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict, MaxDepth = 8
    };
    private readonly object gate = new();
    private readonly Dictionary<string, Upload> uploads = new(StringComparer.Ordinal);
    private readonly StateStore state;
    private readonly AttachmentStore attachments;
    private readonly AttachmentArtifacts? artifacts;
    private readonly TimeProvider clock;
    private readonly ITimer expiry;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public WorkerAttachmentTransfers(StateStore state, AttachmentStore attachments, TimeProvider? clock = null)
    {
        this.state = state; this.attachments = attachments; this.clock = clock ?? TimeProvider.System;
        expiry = this.clock.CreateTimer(_ => { lock (gate) { if (!disposed) Expire(); } }, null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal WorkerAttachmentTransfers(StateStore state, AttachmentStore attachments, AttachmentArtifacts artifacts,
        TimeProvider? clock = null) : this(state, attachments, clock) => this.artifacts = artifacts;

    public static void Authorize(string operation, AgentProfile? actor, JsonElement arguments)
    {
        if (!Operations.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Unknown attachment command.");
        if (actor is null) return;
        if (operation.StartsWith(Prefix + "import.", StringComparison.Ordinal) && operation != Prefix + "import.status")
            throw new UnauthorizedAccessException("Importing user-selected attachments requires the owner interface, including for administration profiles.");
        if (!arguments.TryGetProperty("profileId", out var profile) || profile.ValueKind != JsonValueKind.String ||
            profile.GetString() != actor.Id)
            throw new UnauthorizedAccessException("An agent can read attachment data only under its own profile and conversation.");
    }

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, AgentProfile? actor,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Authorize(operation, actor, arguments);
        switch (operation)
        {
            case Prefix + "import.begin": return Begin(Read<AttachmentImportBeginRequest>(arguments), actor, cancellationToken);
            case Prefix + "import.chunk":
                if (!arguments.TryGetProperty("bytes", out var encoded) || encoded.ValueKind != JsonValueKind.String ||
                    encoded.GetString()!.Length > MaximumBase64Characters)
                    throw new ArgumentException($"Attachment chunks are limited to {MaximumChunkBytes} bytes.");
                return Chunk(Read<AttachmentImportChunkRequest>(arguments, MaximumEscapedChunkEnvelopeCharacters), actor, cancellationToken);
            case Prefix + "import.commit": return await CommitAsync(Read<AttachmentImportUploadRequest>(arguments), actor, cancellationToken).ConfigureAwait(false);
            case Prefix + "import.abort": return Abort(Read<AttachmentImportUploadRequest>(arguments), actor, cancellationToken);
            case Prefix + "import.status": return Status(Read<AttachmentImportStatusRequest>(arguments), actor, cancellationToken);
            case Prefix + "describe":
            {
                var request = Read<AttachmentDescribeRequest>(arguments);
                return attachments.Describe(Access(request.ProfileId, request.ConversationId, actor), request.Id, request.Revision, cancellationToken);
            }
            case Prefix + "read":
            {
                var request = Read<AttachmentRangeRequest>(arguments);
                return attachments.ReadRange(Access(request.ProfileId, request.ConversationId, actor), request.Id, request.Revision,
                    request.Offset, request.Count, cancellationToken);
            }
            case Prefix + "text":
            {
                var request = Read<AttachmentTextRequest>(arguments);
                var result = attachments.ReadText(Access(request.ProfileId, request.ConversationId, actor), request.Id, request.Revision,
                    request.MaximumUtf8Bytes, cancellationToken);
                // A valid UTF-8 byte bound is not a JSON frame bound: escaping may expand one source byte
                // into six. Measure the actual complete projection and leave one MiB for the pipe envelope.
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, CerebralJson.Options)) > 3 * 1024 * 1024)
                    throw new InvalidOperationException("Complete text exceeds the local transport response limit after JSON encoding. Use exact bounded byte ranges or a text handler with explicit source ranges.");
                return result;
            }
            case Prefix + "archive.inspect":
            {
                var request = Read<AttachmentArchiveRequest>(arguments);
                return await RequireArtifacts().InspectAsync(Access(request.ProfileId, request.ConversationId, actor),
                    request.Id, request.Revision, cancellationToken).ConfigureAwait(false);
            }
            case Prefix + "archive.extract":
            {
                var request = Read<AttachmentArchiveRequest>(arguments);
                return await RequireArtifacts().ExtractAsync(Access(request.ProfileId, request.ConversationId, actor),
                    request.Id, request.Revision, cancellationToken).ConfigureAwait(false);
            }
            case Prefix + "archive.status":
            {
                var request = Read<AttachmentArchiveRequest>(arguments);
                return RequireArtifacts().Status(Access(request.ProfileId, request.ConversationId, actor), request.Id, request.Revision);
            }
            case Prefix + "archive.text":
            {
                var request = Read<AttachmentArchiveTextRequest>(arguments);
                return await RequireArtifacts().ReadTextAsync(Access(request.ProfileId, request.ConversationId, actor),
                    request.Id, request.Revision, request.EntryPath, request.MaximumUtf8Bytes, cancellationToken).ConfigureAwait(false);
            }
            default: throw new ArgumentException("Unknown attachment command.");
        }
    }

    private AttachmentArtifacts RequireArtifacts() => artifacts ??
        throw new InvalidOperationException("Archive processing is unavailable on this Worker.");

    /// <summary>Resolve one owner-imported immutable original under the exact saved profile and conversation.</summary>
    public Cerebral.Contracts.AttachmentReference ResolveImport(string profileId, string conversationId,
        string requestId, CancellationToken cancellationToken = default)
    {
        var access = Access(profileId, conversationId, actor: null); Identifier(requestId);
        return attachments.FindImportReceipt(access, requestId, cancellationToken) ??
            throw new InvalidOperationException("The interface capture import has no committed attachment receipt.");
    }

    private AttachmentImportStatus Begin(AttachmentImportBeginRequest request, AgentProfile? actor, CancellationToken token)
    {
        RequireOwner(actor); var access = Access(request.ProfileId, request.ConversationId, actor);
        Identifier(request.RequestId); AttachmentStore.ValidateImportMetadata(request.OriginalName, request.Length);
        lock (gate)
        {
            Ready(token); Expire();
            var committed = attachments.FindImportReceipt(access, request.RequestId, token);
            if (committed is not null)
            {
                if (committed.OriginalName != request.OriginalName || committed.Length != request.Length)
                    throw new InvalidOperationException("Import request ID already has a committed attachment with different metadata.");
                return Complete(request.RequestId, committed);
            }
            if (uploads.TryGetValue(request.RequestId, out var existing))
            {
                RequireUpload(existing, access, null);
                if (existing.Begin.OriginalName != request.OriginalName || existing.Begin.Length != request.Length)
                    throw new InvalidOperationException("Import request ID is already bound to different metadata.");
                existing.LastActivity = clock.GetUtcNow(); return Snapshot(existing);
            }
            // Reserve declared bytes before allocating. Empty files still consume one transfer slot.
            if (uploads.Count >= MaximumUploads || uploads.Values.Sum(x => x.Begin.Length) > MaximumStagingBytes - request.Length)
                throw new InvalidOperationException("Attachment staging is full. Finish or abort an existing import before starting another.");
            var upload = new Upload(request, access, Guid.NewGuid().ToString("N"), new byte[(int)request.Length], clock.GetUtcNow());
            uploads.Add(request.RequestId, upload);
            return Snapshot(upload);
        }
    }

    private AttachmentImportStatus Chunk(AttachmentImportChunkRequest request, AgentProfile? actor, CancellationToken token)
    {
        RequireOwner(actor); var access = Access(request.ProfileId, request.ConversationId, actor);
        Identifier(request.RequestId); UploadId(request.UploadId);
        if (request.Bytes is null || request.Bytes.Length is <= 0 or > MaximumChunkBytes)
            throw new ArgumentException($"An attachment chunk must contain 1 through {MaximumChunkBytes} bytes.");
        lock (gate)
        {
            Ready(token); Expire();
            var upload = GetUpload(request.RequestId, access, request.UploadId);
            if (upload.Committing) throw new InvalidOperationException("Attachment commit is in progress. Query its stable request status.");
            if (request.Offset < 0 || request.Offset > upload.Begin.Length || request.Bytes.Length > upload.Begin.Length - request.Offset)
                throw new ArgumentOutOfRangeException(nameof(request.Offset), "Chunk is outside the declared original length.");
            if (request.Offset < upload.Received)
            {
                if (request.Bytes.Length > upload.Received - request.Offset ||
                    !upload.Bytes.AsSpan((int)request.Offset, request.Bytes.Length).SequenceEqual(request.Bytes))
                    throw new InvalidOperationException("A repeated attachment chunk does not match retained bytes.");
            }
            else
            {
                if (request.Offset != upload.Received) throw new InvalidOperationException("Attachment chunks must be contiguous. Query status for the next exact offset.");
                request.Bytes.CopyTo(upload.Bytes, (int)request.Offset); upload.Received += request.Bytes.Length;
            }
            upload.LastActivity = clock.GetUtcNow();
            return Snapshot(upload);
        }
    }

    private async Task<AttachmentImportStatus> CommitAsync(AttachmentImportUploadRequest request, AgentProfile? actor, CancellationToken token)
    {
        RequireOwner(actor); var access = Access(request.ProfileId, request.ConversationId, actor);
        Identifier(request.RequestId); UploadId(request.UploadId);
        var committed = attachments.FindImportReceipt(access, request.RequestId, token);
        if (committed is not null) return Complete(request.RequestId, committed);
        Upload upload;
        lock (gate)
        {
            Ready(token); Expire();
            upload = GetUpload(request.RequestId, access, request.UploadId);
            if (upload.Committing) return Snapshot(upload);
            if (upload.Received != upload.Begin.Length) throw new InvalidOperationException("The original file is incomplete. Send the remaining bytes before committing.");
            upload.Committing = true;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            using var source = new MemoryStream(upload.Bytes, writable: false);
            var result = await attachments.ImportAsync(access, request.RequestId, upload.Begin.OriginalName, source, linked.Token).ConfigureAwait(false);
            return Complete(request.RequestId, result);
        }
        finally
        {
            lock (gate)
            {
                // A failed/uncertain commit never leaves a partial buffer reusable as a fresh operation.
                // Query the durable import receipt before beginning the same original again.
                uploads.Remove(request.RequestId);
                Array.Clear(upload.Bytes);
                if (disposed && uploads.Count == 0) lifetime.Dispose();
            }
        }
    }

    private AttachmentImportStatus Abort(AttachmentImportUploadRequest request, AgentProfile? actor, CancellationToken token)
    {
        RequireOwner(actor); var access = Access(request.ProfileId, request.ConversationId, actor);
        Identifier(request.RequestId); UploadId(request.UploadId);
        lock (gate)
        {
            Ready(token); Expire();
            var committed = attachments.FindImportReceipt(access, request.RequestId, token);
            if (committed is not null) return Complete(request.RequestId, committed);
            if (!uploads.TryGetValue(request.RequestId, out var upload)) return Missing(request.RequestId);
            RequireUpload(upload, access, request.UploadId);
            if (upload.Committing) throw new InvalidOperationException("Attachment commit is in progress. Query status; an abort cannot undo a committed original.");
            uploads.Remove(request.RequestId); Array.Clear(upload.Bytes);
            return new(request.RequestId, "aborted", request.UploadId, 0, upload.Begin.Length);
        }
    }

    private AttachmentImportStatus Status(AttachmentImportStatusRequest request, AgentProfile? actor, CancellationToken token)
    {
        var access = Access(request.ProfileId, request.ConversationId, actor); Identifier(request.RequestId);
        lock (gate)
        {
            Ready(token); Expire();
            // Commit persists outside this gate, but cannot remove its committing marker until it
            // acquires the gate. Observe the durable receipt and marker together: never report a
            // transient missing result between SQL commit and staging cleanup.
            var committed = attachments.FindImportReceipt(access, request.RequestId, token);
            if (committed is not null) return Complete(request.RequestId, committed);
            if (!uploads.TryGetValue(request.RequestId, out var upload) || upload.Access != access) return Missing(request.RequestId);
            return Snapshot(upload);
        }
    }

    private AttachmentAccessContext Access(string profileId, string conversationId, AgentProfile? actor)
    {
        Identifier(profileId); Identifier(conversationId);
        if (actor is not null && actor.Id != profileId) throw new UnauthorizedAccessException("This profile cannot access the selected attachment scope.");
        if (state.Get<AgentProfile>("agent", profileId) is null) throw new KeyNotFoundException("Attachment target profile is unavailable.");
        return new(profileId, conversationId);
    }

    private Upload GetUpload(string requestId, AttachmentAccessContext access, string uploadId)
    {
        if (!uploads.TryGetValue(requestId, out var upload))
            throw new KeyNotFoundException("Partial attachment upload is unavailable or expired. Query the import receipt before restarting the original file.");
        RequireUpload(upload, access, uploadId); return upload;
    }

    private static void RequireUpload(Upload upload, AttachmentAccessContext access, string? uploadId)
    {
        if (upload.Access != access || uploadId is not null && upload.Id != uploadId)
            throw new KeyNotFoundException("Partial attachment upload is unavailable in this scope or generation.");
    }

    private static AttachmentImportStatus Complete(string requestId, Cerebral.Contracts.AttachmentReference attachment) =>
        new(requestId, "complete", null, attachment.Length, attachment.Length, attachment);
    private static AttachmentImportStatus Missing(string requestId) => new(requestId, "missing", null, 0, null);
    private static AttachmentImportStatus Snapshot(Upload upload) =>
        new(upload.Begin.RequestId, upload.Committing ? "committing" : "receiving", upload.Id, upload.Received, upload.Begin.Length);
    private static void RequireOwner(AgentProfile? actor)
    {
        if (actor is not null) throw new UnauthorizedAccessException("Attachment import requires the authenticated owner interface.");
    }
    private static void Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Attachment identifiers must contain 1 through 128 characters without whitespace or controls.");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Attachment identifier is not valid Unicode."); }
    }
    private static void UploadId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty) throw new ArgumentException("A current upload generation ID is required.");
    }
    private static T Read<T>(JsonElement input, int maximumEnvelopeCharacters = MaximumBase64Characters + 4096)
    {
        if (input.ValueKind != JsonValueKind.Object || input.GetRawText().Length > maximumEnvelopeCharacters)
            throw new ArgumentException("Attachment command exceeds its bounded object envelope.");
        var names = input.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Attachment command contains duplicate fields.");
        try { return input.Deserialize<T>(Json) ?? throw new ArgumentException("Attachment command cannot be null."); }
        catch (JsonException) { throw new ArgumentException("Attachment command fields do not match the declared contract."); }
    }
    private void Ready(CancellationToken token) { ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested(); }
    private void Expire()
    {
        var before = clock.GetUtcNow() - IdleLifetime;
        foreach (var item in uploads.Where(x => !x.Value.Committing && x.Value.LastActivity <= before).ToArray())
        { uploads.Remove(item.Key); Array.Clear(item.Value.Bytes); }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; expiry.Dispose(); lifetime.Cancel();
            foreach (var upload in uploads.Values.Where(x => !x.Committing)) Array.Clear(upload.Bytes);
            foreach (var id in uploads.Where(x => !x.Value.Committing).Select(x => x.Key).ToArray()) uploads.Remove(id);
            if (uploads.Count == 0) lifetime.Dispose();
        }
        // Committing calls own linked cancellation registrations and clear their buffers in finally.
        // The last committing call disposes the cancellation source after releasing its registration.
    }

    private sealed class Upload(AttachmentImportBeginRequest begin, AttachmentAccessContext access, string id, byte[] bytes, DateTimeOffset now)
    {
        public AttachmentImportBeginRequest Begin { get; } = begin;
        public AttachmentAccessContext Access { get; } = access;
        public string Id { get; } = id;
        public byte[] Bytes { get; } = bytes;
        public long Received { get; set; }
        public bool Committing { get; set; }
        public DateTimeOffset LastActivity { get; set; } = now;
    }
}
