using System.Globalization;
using System.Text;
using Cerebral.Contracts.Collaboration;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed class ConversationCommunicationRevisionException(long expected, long actual)
    : InvalidOperationException($"Communication permission changed: expected revision {expected}, actual {actual}.")
{
    public long Expected { get; } = expected;
    public long Actual { get; } = actual;
}

/// <summary>One persisted, revocable permission per conversation pair. There is no executor or resource grant here.
/// The host owns installation-owner authorization, exact caller-chat binding and resolved conversation identities.
/// Every delivery must check current permission; a queued request or an old Allowed receipt is not authorization.</summary>
public sealed class ConversationCommunicationStore(EventJournal journal)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = EventJournal.Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS conversation_communication_permissions (
                first_id TEXT NOT NULL,
                second_id TEXT NOT NULL,
                allowed INTEGER NOT NULL CHECK(allowed IN (0,1)),
                revision INTEGER NOT NULL CHECK(revision > 0),
                updated_at TEXT NOT NULL,
                pending_request_id TEXT NULL UNIQUE,
                pending_source_id TEXT NULL,
                pending_requested_at TEXT NULL,
                PRIMARY KEY(first_id,second_id),
                CHECK(first_id <> second_id),
                CHECK((pending_request_id IS NULL AND pending_source_id IS NULL AND pending_requested_at IS NULL)
                    OR (allowed=0 AND pending_request_id IS NOT NULL AND pending_source_id IS NOT NULL
                        AND pending_requested_at IS NOT NULL AND pending_source_id IN (first_id,second_id))));
            CREATE INDEX IF NOT EXISTS conversation_communication_pending
                ON conversation_communication_permissions(first_id,second_id) WHERE pending_request_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS conversation_communication_requests (
                request_id TEXT PRIMARY KEY NOT NULL,
                source_id TEXT NOT NULL,
                target_id TEXT NOT NULL,
                expected_revision INTEGER NOT NULL CHECK(expected_revision >= 0),
                CHECK(source_id <> target_id));
            """);
        command.ExecuteNonQuery();
    }

    public ConversationCommunicationPermission Get(string firstConversationId, string secondConversationId,
        CancellationToken cancellationToken = default)
    {
        var pair = Pair(firstConversationId, secondConversationId);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = journal.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var current = Read(connection, transaction, pair);
        if (current.Revision > 0) RequireConversations(connection, transaction, pair);
        return current;
    }

    /// <summary>A repeated exact request ID returns current state even after later decisions/revocation.
    /// The durable ID ledger never reopens an old request or replays its prior Allowed result.</summary>
    public ConversationCommunicationPermission Request(ConversationCommunicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId));
        var pair = Pair(request.SourceConversationId, request.TargetConversationId);
        Revision(request.ExpectedRevision);
        return journal.Transact((connection, transaction) =>
        {
            RequireConversations(connection, transaction, pair);
            var current = Read(connection, transaction, pair);
            using (var prior = EventJournal.Command(connection, transaction, """
                SELECT source_id,target_id,expected_revision FROM conversation_communication_requests WHERE request_id=$id;
                """))
            {
                prior.Parameters.AddWithValue("$id", request.RequestId);
                using var reader = prior.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetString(0) != request.SourceConversationId || reader.GetString(1) != request.TargetConversationId ||
                        reader.GetInt64(2) != request.ExpectedRevision)
                        throw new InvalidOperationException("Request ID already belongs to different communication request content.");
                    return current;
                }
            }
            Expected(current, request.ExpectedRevision);
            if (current.PendingRequest is not null)
                throw new InvalidOperationException("This conversation pair already has a pending communication request.");
            if (!current.Allowed)
            {
                using var count = EventJournal.Command(connection, transaction,
                    "SELECT COUNT(*) FROM conversation_communication_permissions WHERE pending_request_id IS NOT NULL;");
                if (Convert.ToInt64(count.ExecuteScalar()) >= ConversationCommunicationContract.MaximumPendingRequests)
                    throw new InvalidOperationException("The pending communication request limit is reached. Resolve existing requests first.");
            }
            EventJournal.RequireConversationCommunicationFormat(connection, transaction);
            using (var insert = EventJournal.Command(connection, transaction, """
                INSERT INTO conversation_communication_requests(request_id,source_id,target_id,expected_revision)
                VALUES($id,$source,$target,$revision);
                """))
            {
                insert.Parameters.AddWithValue("$id", request.RequestId);
                insert.Parameters.AddWithValue("$source", request.SourceConversationId);
                insert.Parameters.AddWithValue("$target", request.TargetConversationId);
                insert.Parameters.AddWithValue("$revision", request.ExpectedRevision);
                insert.ExecuteNonQuery();
            }
            // Remember even a request made while allowed, so its retry cannot reopen the pair after revoke.
            if (current.Allowed) return current;
            var now = DateTimeOffset.UtcNow;
            var updated = current with
            {
                Revision = checked(current.Revision + 1), UpdatedAt = now,
                PendingRequest = new(request.RequestId, request.SourceConversationId, now)
            };
            Write(connection, transaction, updated);
            return updated;
        }, cancellationToken);
    }

    /// <summary>Owner-only CAS allow/deny/revoke. Any successful decision clears the pending request.
    /// Repeating a stale decision rejects, including a previously successful allow after revocation.</summary>
    public ConversationCommunicationPermission SetAllowed(ConversationCommunicationSetAllowedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Pair);
        var pair = Pair(request.Pair.FirstConversationId, request.Pair.SecondConversationId);
        Revision(request.ExpectedRevision);
        if (request.PendingRequestId is not null) Identifier(request.PendingRequestId, nameof(request.PendingRequestId));
        return journal.Transact((connection, transaction) =>
        {
            var current = Read(connection, transaction, pair);
            Expected(current, request.ExpectedRevision);
            if (request.PendingRequestId is not null && current.PendingRequest?.RequestId != request.PendingRequestId)
                throw new InvalidOperationException("The pending communication request changed before the owner decision.");
            if (request.Allowed) RequireConversations(connection, transaction, pair);
            EventJournal.RequireConversationCommunicationFormat(connection, transaction);
            var updated = current with
            {
                Allowed = request.Allowed, Revision = checked(current.Revision + 1),
                PendingRequest = null, UpdatedAt = DateTimeOffset.UtcNow
            };
            Write(connection, transaction, updated);
            return updated;
        }, cancellationToken);
    }

    /// <summary>Bounded owner state. Host authorization also governs exposure of this pair directory.</summary>
    public ConversationCommunicationPage List(ConversationCommunicationPair? afterPair = null, int limit = 64,
        bool pendingOnly = false, string? conversationId = null, CancellationToken cancellationToken = default)
    {
        PageLimit(limit);
        var after = afterPair is null ? null : Pair(afterPair.FirstConversationId, afterPair.SecondConversationId);
        if (conversationId is not null) Identifier(conversationId, nameof(conversationId));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT first_id,second_id,allowed,revision,updated_at,pending_request_id,pending_source_id,pending_requested_at
            FROM conversation_communication_permissions
            WHERE ($pending=0 OR pending_request_id IS NOT NULL)
                AND ($conversation IS NULL OR first_id=$conversation OR second_id=$conversation)
                AND ($first IS NULL OR first_id > $first OR (first_id=$first AND second_id > $second))
            ORDER BY first_id,second_id LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$pending", pendingOnly ? 1 : 0);
        command.Parameters.AddWithValue("$conversation", (object?)conversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$first", (object?)after?.FirstConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$second", (object?)after?.SecondConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = command.ExecuteReader();
        var items = new List<ConversationCommunicationPermission>(limit);
        var more = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == limit) { more = true; break; }
            items.Add(ReadRow(reader));
        }
        return new(items.ToArray(), more ? items[^1].Pair : null);
    }

    /// <summary>Registered conversation coordinates only. Does not read events, run payloads, titles or context.
    /// The host must authorize this metadata view; directory membership supplies no caller or owner proof.</summary>
    public ConversationCommunicationDirectoryPage ReadDirectory(string? afterConversationId = null, int limit = 64,
        CancellationToken cancellationToken = default)
    {
        PageLimit(limit);
        if (afterConversationId is not null) Identifier(afterConversationId, nameof(afterConversationId));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT conversation_id,profile_id,project_id FROM workflow_conversations
            WHERE $after IS NULL OR conversation_id > $after ORDER BY conversation_id LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$after", (object?)afterConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = command.ExecuteReader();
        var items = new List<ConversationCommunicationDirectoryEntry>(limit);
        var more = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == limit) { more = true; break; }
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return new(items.ToArray(), more ? items[^1].ConversationId : null);
    }

    private static ConversationCommunicationPermission Read(SqliteConnection connection, SqliteTransaction? transaction,
        ConversationCommunicationPair pair)
    {
        using var command = EventJournal.Command(connection, transaction, """
            SELECT first_id,second_id,allowed,revision,updated_at,pending_request_id,pending_source_id,pending_requested_at
            FROM conversation_communication_permissions WHERE first_id=$first AND second_id=$second;
            """);
        command.Parameters.AddWithValue("$first", pair.FirstConversationId);
        command.Parameters.AddWithValue("$second", pair.SecondConversationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : new(pair, false, 0);
    }

    private static void RequireConversations(SqliteConnection connection, SqliteTransaction transaction,
        ConversationCommunicationPair pair)
    {
        using var command = EventJournal.Command(connection, transaction, """
            SELECT conversation_id,profile_id FROM workflow_conversations WHERE conversation_id IN ($first,$second);
            """);
        command.Parameters.AddWithValue("$first", pair.FirstConversationId);
        command.Parameters.AddWithValue("$second", pair.SecondConversationId);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            Identifier(reader.GetString(0), "registered conversation ID");
            Identifier(reader.GetString(1), "registered profile ID");
            count++;
        }
        if (count != 2) throw new InvalidOperationException("Both conversations require registered identity metadata before communication.");
    }

    private static ConversationCommunicationPermission ReadRow(SqliteDataReader reader) => new(
        new(reader.GetString(0), reader.GetString(1)), reader.GetInt64(2) == 1, reader.GetInt64(3),
        reader.IsDBNull(5) ? null : new(reader.GetString(5), reader.GetString(6), ParseTime(reader.GetString(7))),
        ParseTime(reader.GetString(4)));

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, ConversationCommunicationPermission state)
    {
        using var command = EventJournal.Command(connection, transaction, """
            INSERT INTO conversation_communication_permissions(first_id,second_id,allowed,revision,updated_at,
                pending_request_id,pending_source_id,pending_requested_at)
            VALUES($first,$second,$allowed,$revision,$at,$pending,$source,$requested)
            ON CONFLICT(first_id,second_id) DO UPDATE SET allowed=excluded.allowed,revision=excluded.revision,
                updated_at=excluded.updated_at,pending_request_id=excluded.pending_request_id,
                pending_source_id=excluded.pending_source_id,pending_requested_at=excluded.pending_requested_at;
            """);
        command.Parameters.AddWithValue("$first", state.Pair.FirstConversationId);
        command.Parameters.AddWithValue("$second", state.Pair.SecondConversationId);
        command.Parameters.AddWithValue("$allowed", state.Allowed ? 1 : 0);
        command.Parameters.AddWithValue("$revision", state.Revision);
        command.Parameters.AddWithValue("$at", state.UpdatedAt!.Value.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$pending", (object?)state.PendingRequest?.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)state.PendingRequest?.SourceConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$requested", (object?)state.PendingRequest?.RequestedAt.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static ConversationCommunicationPair Pair(string first, string second)
    {
        Identifier(first, nameof(first)); Identifier(second, nameof(second));
        var comparison = StringComparer.Ordinal.Compare(first, second);
        if (comparison == 0) throw new ArgumentException("Communication requires two different conversations.");
        return comparison < 0 ? new(first, second) : new(second, first);
    }

    private static void Identifier(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > 128 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("Identifiers must be at most 128 characters without whitespace or control characters.", name);
        _ = StrictUtf8.GetByteCount(value);
    }

    private static void Revision(long revision)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
    }

    private static void PageLimit(int limit)
    {
        if (limit is < 1 or > ConversationCommunicationContract.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private static void Expected(ConversationCommunicationPermission current, long expected)
    {
        if (current.Revision != expected) throw new ConversationCommunicationRevisionException(expected, current.Revision);
    }

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
}
