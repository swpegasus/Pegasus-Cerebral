using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Resources;

/// <summary>
/// Immutable originals in the service's canonical SQLite journal, never in a build directory or transcript.
/// The host selects the journal's durable path and establishes the access scope. This API is not authentication.
/// Admission is bounded per original and in aggregate; no retention policy silently deletes source material.
/// </summary>
public sealed class AttachmentStore
{
    public const int MaximumAttachmentBytes = 16 * 1024 * 1024;
    public const int MaximumTextBytes = 1024 * 1024;
    public const int MaximumRangeBytes = 256 * 1024;
    public const int MaximumOriginalNameBytes = 255;
    public const int MaximumImageDimension = AttachmentAdmission.MaximumImageDimension;
    public const long MaximumImagePixels = AttachmentAdmission.MaximumImagePixels;
    private const int MaximumMetadataBytes = 2048;
    private const string Columns = "id,revision,media_type,original_name,byte_length,metadata";
    private readonly EventJournal journal;
    private readonly AttachmentStoreOptions options;

    public AttachmentStore(EventJournal journal, AttachmentStoreOptions? options = null)
    {
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.options = options ?? new AttachmentStoreOptions();
        if (this.options.MaximumStoredBytes < 0 || this.options.MaximumAttachmentsPerConversation < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Attachment capacity limits cannot be negative.");
        journal.Transact((connection, transaction) =>
        {
            using var create = EventJournal.Command(connection, transaction, """
                CREATE TABLE IF NOT EXISTS resource_attachments (
                    id TEXT PRIMARY KEY, revision INTEGER NOT NULL CHECK(revision=1),
                    request_id TEXT NOT NULL UNIQUE,
                    conversation_id TEXT NOT NULL, profile_id TEXT NOT NULL,
                    media_type TEXT NOT NULL, original_name TEXT NOT NULL,
                    byte_length INTEGER NOT NULL CHECK(byte_length>=0 AND byte_length<=16777216),
                    metadata TEXT NOT NULL CHECK(length(CAST(metadata AS BLOB))<=2048),
                    content BLOB NOT NULL CHECK(typeof(content)='blob' AND length(content)=byte_length),
                    created_utc TEXT NOT NULL);
                """);
            create.ExecuteNonQuery();
            return true;
        });
    }

    /// <summary>Validate declared import metadata before reserving transport storage or allocating its buffer.</summary>
    public static void ValidateImportMetadata(string originalName, long length)
    {
        ValidateOriginalName(originalName);
        var maximum = AttachmentAdmission.MaximumBytes(originalName);
        if (length < 0 || length > maximum)
            throw new ArgumentOutOfRangeException(nameof(length), $"Attachment length must be between 0 and {maximum} bytes for this type.");
    }

    /// <summary>
    /// Imports the remaining bytes of a user-selected stream. The caller retains stream ownership.
    /// Retry the same request ID, scope, name, and exact bytes after uncertainty; a changed retry is rejected.
    /// No partial attachment or conversation reservation is committed when admission/read/cancellation fails.
    /// </summary>
    public async Task<AttachmentReference> ImportAsync(AttachmentAccessContext access, string requestId,
        string originalName, Stream source, CancellationToken cancellationToken = default)
    {
        ValidateAccess(access); Identifier(requestId, nameof(requestId)); ValidateOriginalName(originalName);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The selected file stream must be readable.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        var maximum = AttachmentAdmission.MaximumBytes(originalName);
        var bytes = await ReadBoundedAsync(source, maximum, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = AttachmentAdmission.Inspect(originalName, bytes);
            var metadata = JsonSerializer.Serialize(inspection.Metadata);
            var fresh = new AttachmentReference(Guid.NewGuid().ToString("N"), 1, inspection.MediaType,
                originalName, bytes.LongLength, inspection.Metadata);
            return journal.Transact((connection, transaction) =>
            {
                using var prior = EventJournal.Command(connection, transaction, $"""
                    SELECT {Columns},profile_id,conversation_id,content=$content
                    FROM resource_attachments WHERE request_id=$request;
                    """);
                prior.Parameters.AddWithValue("$request", requestId);
                prior.Parameters.Add("$content", SqliteType.Blob).Value = bytes;
                using (var reader = prior.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var retained = Reference(reader);
                        if (reader.GetString(6) != access.ProfileId || reader.GetString(7) != access.ConversationId ||
                            retained.OriginalName != originalName || retained.MediaType != inspection.MediaType ||
                            retained.Length != bytes.LongLength || !SameMetadata(retained.Metadata, inspection.Metadata) ||
                            reader.GetInt64(8) != 1)
                            throw new InvalidOperationException("Attachment import request ID is already bound to different input. Use the original request to reconcile.");
                        return retained;
                    }
                }
                using (var usage = EventJournal.Command(connection, transaction, """
                    SELECT COALESCE(SUM(byte_length),0),
                        COUNT(CASE WHEN conversation_id=$conversation THEN 1 END)
                    FROM resource_attachments;
                    """))
                {
                    usage.Parameters.AddWithValue("$conversation", access.ConversationId);
                    using var reader = usage.ExecuteReader();
                    if (!reader.Read()) throw new InvalidDataException("Attachment storage usage could not be read.");
                    if (reader.GetInt64(0) > options.MaximumStoredBytes - bytes.LongLength ||
                        reader.GetInt64(1) >= options.MaximumAttachmentsPerConversation)
                        throw new InvalidOperationException("Attachment storage capacity is reached. Increase the configured storage/conversation limit or remove attachments through an explicit lifecycle operation.");
                }
                WorkflowStore.BindConversation(connection, transaction, access.ConversationId, access.ProfileId, null);
                using var insert = EventJournal.Command(connection, transaction, """
                    INSERT INTO resource_attachments
                      (id,revision,request_id,conversation_id,profile_id,media_type,original_name,byte_length,metadata,content,created_utc)
                    VALUES($id,1,$request,$conversation,$profile,$media,$name,$length,$metadata,$content,$created);
                    """);
                insert.Parameters.AddWithValue("$id", fresh.Id); insert.Parameters.AddWithValue("$request", requestId);
                Scope(insert, access);
                insert.Parameters.AddWithValue("$media", fresh.MediaType); insert.Parameters.AddWithValue("$name", originalName);
                insert.Parameters.AddWithValue("$length", bytes.LongLength); insert.Parameters.AddWithValue("$metadata", metadata);
                insert.Parameters.Add("$content", SqliteType.Blob).Value = bytes;
                insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
                return fresh;
            }, cancellationToken);
        }
        finally { Array.Clear(bytes); }
    }

    /// <summary>
    /// Reconcile an uncertain import without reopening the source or writing anything.
    /// A null result means no receipt is visible in this exact host-established scope.
    /// </summary>
    public AttachmentReference? FindImportReceipt(AttachmentAccessContext access, string requestId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access); Identifier(requestId, nameof(requestId));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = journal.Open();
        using var command = Select(connection, access, requestId, Columns, byRequest: true);
        using var reader = command.ExecuteReader();
        var reference = reader.Read() ? Reference(reader) : null;
        cancellationToken.ThrowIfCancellationRequested();
        return reference;
    }

    /// <summary>Read immutable public metadata only; missing and foreign-scope IDs share the same failure.</summary>
    public AttachmentReference Describe(AttachmentAccessContext access, string id, long revision,
        CancellationToken cancellationToken = default)
    {
        ValidateCoordinate(access, id, revision);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = journal.Open();
        using var command = Select(connection, access, id, Columns);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw Unavailable();
        var reference = Reference(reader);
        RequireRevision(reference, revision);
        cancellationToken.ThrowIfCancellationRequested();
        return reference;
    }

    /// <summary>Do not let caller-supplied MIME, length, display name, or image metadata override stored admission.</summary>
    public AttachmentReference ValidateReference(AttachmentAccessContext access, AttachmentReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var retained = Describe(access, reference.Id, reference.Revision, cancellationToken);
        if (retained.MediaType != reference.MediaType || retained.OriginalName != reference.OriginalName ||
            retained.Length != reference.Length || !SameMetadata(retained.Metadata, reference.Metadata))
            throw new InvalidDataException("Attachment metadata does not match its immutable stored reference. Refresh the attachment.");
        return retained;
    }

    /// <summary>Reads exactly count bytes or fails. EOF never silently shortens a requested interval.</summary>
    public AttachmentRange ReadRange(AttachmentAccessContext access, string id, long revision, long offset, int count,
        CancellationToken cancellationToken = default)
    {
        ValidateCoordinate(access, id, revision);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0 || count > MaximumRangeBytes) throw new ArgumentOutOfRangeException(nameof(count),
            $"Read at most {MaximumRangeBytes} attachment bytes per interval.");
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = journal.Open();
        using var command = Select(connection, access, id, Columns + """
            ,CASE WHEN $offset<=byte_length AND $count<=byte_length-$offset
              THEN CASE WHEN $count=0 THEN zeroblob(0) ELSE substr(content,$offset+1,$count) END END
            """);
        command.Parameters.AddWithValue("$offset", offset); command.Parameters.AddWithValue("$count", count);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw Unavailable();
        var reference = Reference(reader); RequireRevision(reference, revision);
        if (offset > reference.Length || count > reference.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count), "Requested attachment interval extends beyond the original bytes.");
        if (reader.IsDBNull(6)) throw new InvalidDataException("Stored attachment bytes could not be resolved.");
        var bytes = (byte[])reader.GetValue(6);
        if (bytes.Length != count) throw new InvalidDataException("Stored attachment length does not match its metadata.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(reference, offset, bytes);
    }

    /// <summary>Return the entire text only when it fits the explicit byte budget. Never truncate required source text.</summary>
    public AttachmentText ReadText(AttachmentAccessContext access, string id, long revision, int maximumUtf8Bytes = MaximumTextBytes,
        CancellationToken cancellationToken = default)
    {
        ValidateCoordinate(access, id, revision);
        if (maximumUtf8Bytes < 0 || maximumUtf8Bytes > MaximumTextBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = journal.Open();
        using var command = Select(connection, access, id, Columns + ",CASE WHEN byte_length<=$maximum THEN content END");
        command.Parameters.AddWithValue("$maximum", maximumUtf8Bytes);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw Unavailable();
        var reference = Reference(reader); RequireRevision(reference, revision);
        if (!AttachmentAdmission.IsText(reference.MediaType))
            throw new NotSupportedException("This attachment is not admitted UTF-8 text. Resolve it through a compatible image, archive, or file handler.");
        if (reference.Length > maximumUtf8Bytes)
            throw new InvalidOperationException($"Complete text needs {reference.Length} UTF-8 bytes; requested limit is {maximumUtf8Bytes}. Increase the limit or use a handler with explicit source ranges.");
        if (reader.IsDBNull(6)) throw new InvalidDataException("Stored text bytes could not be resolved.");
        var bytes = (byte[])reader.GetValue(6);
        if (bytes.LongLength != reference.Length) throw new InvalidDataException("Stored attachment length does not match its metadata.");
        string text;
        try { text = AttachmentAdmission.StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Stored text is not valid UTF-8.", ex); }
        if (text.Contains('\0')) throw new InvalidDataException("Stored text contains an inadmissible NUL character.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(reference, text);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken cancellationToken)
    {
        using var accumulator = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(buffer.Length, maximum - accumulator.Length + 1);
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0) return accumulator.ToArray();
                if (read > maximum - accumulator.Length)
                    throw new InvalidDataException($"Attachment exceeds the {maximum}-byte limit for its type.");
                accumulator.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            if (accumulator.TryGetBuffer(out var held)) held.AsSpan().Clear();
        }
    }

    private static SqliteCommand Select(SqliteConnection connection, AttachmentAccessContext access, string id, string projection, bool byRequest = false)
    {
        var keyColumn = byRequest ? "request_id" : "id";
        var command = EventJournal.Command(connection, null, $"""
            SELECT {projection} FROM resource_attachments
            WHERE {keyColumn}=$id AND profile_id=$profile AND conversation_id=$conversation
              AND EXISTS(SELECT 1 FROM workflow_conversations w
                WHERE w.conversation_id=$conversation AND w.profile_id=$profile);
            """);
        command.Parameters.AddWithValue("$id", id); Scope(command, access);
        return command;
    }

    private static void Scope(SqliteCommand command, AttachmentAccessContext access)
    {
        command.Parameters.AddWithValue("$profile", access.ProfileId);
        command.Parameters.AddWithValue("$conversation", access.ConversationId);
    }

    private static AttachmentReference Reference(SqliteDataReader reader)
    {
        var metadataJson = reader.GetString(5);
        if (AttachmentAdmission.StrictUtf8.GetByteCount(metadataJson) > MaximumMetadataBytes)
            throw new InvalidDataException("Stored attachment metadata exceeds its limit.");
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        var reference = new AttachmentReference(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt64(4), metadata is null ? null : new ReadOnlyDictionary<string, string>(metadata));
        if (reference.Revision != 1 || reference.Length < 0 || reference.Length > MaximumAttachmentBytes)
            throw new InvalidDataException("Stored attachment coordinate or length is invalid.");
        ValidateOriginalName(reference.OriginalName);
        if (reference.MediaType != "image/png" && reference.MediaType != "image/jpeg" &&
            reference.MediaType != "application/octet-stream" && !AttachmentAdmission.IsText(reference.MediaType) &&
            !AttachmentAdmission.IsArchive(reference.MediaType))
            throw new InvalidDataException("Stored attachment media type is not admitted.");
        return reference;
    }

    private static bool SameMetadata(IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? second) =>
        first is null ? second is null : second is not null && first.Count == second.Count &&
            first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static void RequireRevision(AttachmentReference reference, long requested)
    {
        if (reference.Revision != requested)
            throw new InvalidOperationException("Attachment revision does not match the immutable original. Refresh its reference.");
    }

    private static KeyNotFoundException Unavailable() => new("Attachment is unavailable in this conversation and profile.");

    private static void ValidateCoordinate(AttachmentAccessContext access, string id, long revision)
    {
        ValidateAccess(access);
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Attachment ID must be an opaque GUID coordinate.", nameof(id));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
    }

    private static void ValidateAccess(AttachmentAccessContext access)
    {
        ArgumentNullException.ThrowIfNull(access);
        Identifier(access.ProfileId, nameof(access.ProfileId)); Identifier(access.ConversationId, nameof(access.ConversationId));
    }

    private static void Identifier(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Use a nonempty identifier of at most 128 characters without whitespace or control characters.", name);
        try { _ = AttachmentAdmission.StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException ex) { throw new ArgumentException("Identifier is not valid Unicode.", name, ex); }
    }

    private static void ValidateOriginalName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*') ||
            value.EndsWith(' ') || value.EndsWith('.') || value.Contains('\u2028') || value.Contains('\u2029'))
            throw new ArgumentException("OriginalName must be a file display name, without directories, control characters, or path syntax.", nameof(value));
        try
        {
            if (AttachmentAdmission.StrictUtf8.GetByteCount(value) > MaximumOriginalNameBytes)
                throw new ArgumentException($"File display name exceeds {MaximumOriginalNameBytes} UTF-8 bytes.", nameof(value));
        }
        catch (EncoderFallbackException ex) { throw new ArgumentException("File display name is not valid Unicode.", nameof(value), ex); }
    }
}
