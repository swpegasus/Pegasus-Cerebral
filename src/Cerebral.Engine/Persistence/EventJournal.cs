using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed record JournalAppendResult(CerebralEvent Event, bool AlreadyCommitted);

public sealed record JournalRetentionLimits(int MaximumRollingEvents = 2_000,
    long MaximumRollingBytes = 64L * 1024 * 1024, int MaximumAgeDays = 30);

public sealed record JournalRetentionGap(long PrunedEvents, long PrunedBytes,
    long FirstPrunedSequence, long LastPrunedSequence, DateTimeOffset LastPrunedAt);

public sealed record JournalRetentionStatus(string ConversationId, long PrunedEvents, long PrunedBytes,
    long? FirstPrunedSequence, long? LastPrunedSequence, DateTimeOffset? LastPrunedAt,
    JournalRetentionLimits? LastLimits)
{
    public JournalRetentionGap? Gap => PrunedEvents > 0 && FirstPrunedSequence is { } first &&
        LastPrunedSequence is { } last && LastPrunedAt is { } at
        ? new(PrunedEvents, PrunedBytes, first, last, at)
        : null;
}

public sealed record JournalPruneResult(JournalRetentionStatus Retention, long RemovedEvents,
    long RemovedBytes, long RetainedRollingEvents, long RetainedRollingBytes,
    long PinnedRollingEvents, bool QuotaSatisfied);

/// <summary>Continue with NextAfterSequence and the same SnapshotSequence. New events belong to a new snapshot.</summary>
public sealed record JournalPage(IReadOnlyList<CerebralEvent> Events, long SnapshotSequence,
    long? NextAfterSequence, JournalRetentionGap? RetentionGap = null);

/// <summary>Events are chronological; continue toward older history with NextBeforeSequence and the same snapshot.</summary>
public sealed record JournalPreviousPage(IReadOnlyList<CerebralEvent> Events, long SnapshotSequence,
    long? NextBeforeSequence, JournalRetentionGap? RetentionGap = null);

public sealed class JournalSourceUnavailableException(string message, JournalRetentionGap? retentionGap)
    : KeyNotFoundException(message)
{
    /// <summary>When present, rolling detail in this snapshot was pruned; a missing ID is otherwise simply absent.</summary>
    public JournalRetentionGap? RetentionGap { get; } = retentionGap;
}

public sealed class JournalRevisionException(long expected, long actual)
    : InvalidOperationException($"Conversation changed: expected revision {expected}, actual {actual}.")
{
    public long Expected { get; } = expected;
    public long Actual { get; } = actual;
}

/// <summary>
/// Bounded canonical event storage. This database does not replay effects and is deliberately separate from
/// the legacy session database. Callers supply accepted, authorized events; possession of this API grants no tools.
/// </summary>
public sealed class EventJournal
{
    // Version 9 marks conversation communication policy. Older hosts must refuse this state
    // instead of admitting communication without the persisted policy. Mark only on mutation.
    public const int SchemaVersion = 9;
    public const int MaximumPageEvents = 200;
    public const int MaximumPruneEvents = 10_000;
    // Leave envelope/metadata space within the local pipe's 4 MiB frame bound.
    public const int MaximumPageBytes = 3 * 1024 * 1024;
    public const int MaximumEventBytes = CerebralEventContract.MaximumSerializedUtf8Bytes;
    private const int ApplicationId = 0x50434556; // PCEV: reject a different database instead of adopting it.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string connectionString;

    /// <summary>Validate the matching shared contract before a host opens owner state or initializes plugins.
    /// Assembly versions alone cannot identify this additive development checkpoint shape.</summary>
    public static void ValidateCheckpointContract()
    {
        if (typeof(Cerebral.Contracts.Context.ContextCheckpoint).GetProperty("SourceLocators") is null)
            throw new InvalidOperationException("This host requires the checkpoint source-map contract. Update the host persistence assembly and Cerebral.Contracts together.");
    }

    public EventJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ValidateCheckpointContract();
        if (!Path.IsPathFullyQualified(databasePath)) throw new ArgumentException("Use an absolute journal path.", nameof(databasePath));
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var application = Scalar(connection, transaction, "PRAGMA application_id;");
        var version = Scalar(connection, transaction, "PRAGMA user_version;");
        if (application == 0 && version == 0 && Scalar(connection, transaction,
                "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';") == 0)
        {
            using var create = Command(connection, transaction, $"""
                CREATE TABLE event_streams (conversation_id TEXT PRIMARY KEY, revision INTEGER NOT NULL);
                CREATE TABLE conversation_events (
                    conversation_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    event_id TEXT NOT NULL UNIQUE,
                    payload TEXT NOT NULL,
                    PRIMARY KEY (conversation_id, sequence));
                PRAGMA application_id = {ApplicationId};
                PRAGMA user_version = 1;
                """);
            create.ExecuteNonQuery();
            version = 1;
            application = ApplicationId;
        }
        if (application != ApplicationId || version is < 1 or > SchemaVersion)
            throw new InvalidOperationException($"Unsupported event journal identity/version ({application}/{version}). Select the Cerebral event database with schema {SchemaVersion}.");
        if (version == 1)
        {
            using var upgrade = Command(connection, transaction, """
                CREATE TABLE workflow_conversations (conversation_id TEXT PRIMARY KEY, profile_id TEXT NOT NULL);
                CREATE TABLE workflow_runs (
                    id TEXT PRIMARY KEY, request_id TEXT NOT NULL UNIQUE,
                    conversation_id TEXT NOT NULL, profile_id TEXT NOT NULL,
                    status TEXT NOT NULL, revision INTEGER NOT NULL,
                    submission TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX workflow_runs_status ON workflow_runs(status, id);
                CREATE TABLE workflow_answers (
                    request_id TEXT PRIMARY KEY, run_id TEXT NOT NULL,
                    request TEXT NOT NULL, response TEXT NOT NULL);
                CREATE TABLE workflow_effect_results (
                    effect_id TEXT PRIMARY KEY, run_id TEXT NOT NULL, result TEXT NOT NULL);
                PRAGMA user_version = 2;
                """);
            upgrade.ExecuteNonQuery();
            version = 2;
        }
        if (version == 2)
        {
            using var upgrade = Command(connection, transaction, """
                CREATE TABLE conversation_compaction_requests (
                    request_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    definition_id TEXT NOT NULL,
                    expected_revision INTEGER NOT NULL,
                    requested_at TEXT NOT NULL,
                    consumed_by_run_id TEXT NULL,
                    consumed_at TEXT NULL);
                CREATE UNIQUE INDEX conversation_compaction_pending
                    ON conversation_compaction_requests(conversation_id)
                    WHERE consumed_by_run_id IS NULL;
                PRAGMA user_version = 3;
                """);
            upgrade.ExecuteNonQuery();
            version = 3;
        }
        if (version == 3)
        {
            using var upgrade = Command(connection, transaction, """
                CREATE TABLE conversation_context_checkpoints (
                    conversation_id TEXT PRIMARY KEY,
                    source_revision INTEGER NOT NULL,
                    run_id TEXT NOT NULL,
                    effect_id TEXT NOT NULL UNIQUE,
                    checkpoint TEXT NOT NULL,
                    adopted_at TEXT NOT NULL);
                PRAGMA user_version = 4;
                """);
            upgrade.ExecuteNonQuery();
            version = 4;
        }
        if (version == 4)
        {
            using var upgrade = Command(connection, transaction, """
                CREATE TABLE event_retention (
                    conversation_id TEXT PRIMARY KEY,
                    pruned_events INTEGER NOT NULL,
                    pruned_bytes INTEGER NOT NULL,
                    first_pruned_sequence INTEGER NOT NULL,
                    last_pruned_sequence INTEGER NOT NULL,
                    last_pruned_at TEXT NOT NULL,
                    maximum_rolling_events INTEGER NOT NULL,
                    maximum_rolling_bytes INTEGER NOT NULL,
                    maximum_age_days INTEGER NOT NULL);
                PRAGMA user_version = 5;
                """);
            upgrade.ExecuteNonQuery();
            version = 5;
        }
        if (version == 5)
        {
            using var upgrade = Command(connection, transaction, """
                ALTER TABLE workflow_conversations ADD COLUMN project_id TEXT NULL;
                CREATE TABLE collaboration_project_grants (
                    id TEXT PRIMARY KEY,
                    source_project_id TEXT NOT NULL,
                    target_project_id TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    payload TEXT NOT NULL);
                CREATE INDEX collaboration_project_grants_route
                    ON collaboration_project_grants(source_project_id,target_project_id);
                CREATE TABLE collaboration_tasks (
                    id TEXT PRIMARY KEY,
                    request_id TEXT NOT NULL UNIQUE,
                    attempt_id TEXT NOT NULL UNIQUE,
                    source_conversation_id TEXT NOT NULL,
                    target_conversation_id TEXT NOT NULL,
                    source_project_id TEXT NULL,
                    target_project_id TEXT NULL,
                    target_run_id TEXT NULL UNIQUE,
                    status TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    payload TEXT NOT NULL);
                CREATE INDEX collaboration_tasks_status ON collaboration_tasks(status,id);
                CREATE INDEX collaboration_tasks_source ON collaboration_tasks(source_conversation_id,id);
                CREATE INDEX collaboration_tasks_target ON collaboration_tasks(target_conversation_id,id);
                CREATE TABLE collaboration_events (
                    task_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    payload TEXT NOT NULL,
                    PRIMARY KEY(task_id,sequence));
                PRAGMA user_version = 6;
                """);
            upgrade.ExecuteNonQuery();
            version = 6;
        }
        using (var index = Command(connection, transaction,
            "CREATE INDEX IF NOT EXISTS workflow_runs_conversation_status ON workflow_runs(conversation_id,status);" +
            "CREATE INDEX IF NOT EXISTS workflow_effect_results_run_id ON workflow_effect_results(run_id);"))
            index.ExecuteNonQuery();
        WorkflowStore.InitializeConversationRenames(connection, transaction);
        ConversationCommunicationStore.Initialize(connection, transaction);
        CollaborationStore.InitializeOwnedCoordinators(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Append one event atomically. Repeat the original ID and payload after a lost response. A replay succeeds
    /// even if expectedRevision is stale; reusing an ID for different content or a different conversation fails.
    /// </summary>
    public JournalAppendResult Append(CerebralEvent item, long expectedRevision, CancellationToken cancellationToken = default)
    {
        return Transact((connection, transaction) => AppendInTransaction(connection, transaction, item, expectedRevision), cancellationToken);
    }

    internal static void RequireCheckpointSourceMapFormat(SqliteConnection connection, SqliteTransaction transaction)
    {
        var version = Scalar(connection, transaction, "PRAGMA user_version;");
        if (version is not (6 or 7 or 8 or 9))
            throw new InvalidOperationException("The journal format changed before checkpoint adoption.");
        if (version is 7 or 8 or 9) return;
        // The format marker shares the adoption transaction: rejected/cancelled adoption
        // rolls it back with the checkpoint, effect receipt and exact-read event. Old hosts
        // reject this version on reopen instead of losing locators or failing mid-workflow.
        using var mark = Command(connection, transaction, "PRAGMA user_version = 7;");
        mark.ExecuteNonQuery();
    }

    /// <summary>Lazy, narrow format marker and global owner request-ID ledger. A refusal rolls both back.</summary>
    internal static void RequirePreparedInterventionFormat(SqliteConnection connection, SqliteTransaction transaction)
    {
        var version = Scalar(connection, transaction, "PRAGMA user_version;");
        if (version is 8 or 9) return;
        if (version is not (6 or 7))
            throw new InvalidOperationException("The event journal cannot record prepared intervention at this format.");
        using var mark = Command(connection, transaction, """
            CREATE TABLE workflow_prepared_requests (
                request_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                request_sha256 TEXT NOT NULL);
            PRAGMA user_version = 8;
            """);
        mark.ExecuteNonQuery();
    }

    internal static void RequireConversationCommunicationFormat(SqliteConnection connection, SqliteTransaction transaction)
    {
        var version = Scalar(connection, transaction, "PRAGMA user_version;");
        if (version == 9) return;
        if (version is not (6 or 7 or 8))
            throw new InvalidOperationException("The event journal cannot record conversation communication policy at this format.");
        // Version 9 includes the preceding lazy format's table, even if this is the first feature mutation.
        // Both feature initialization and marking roll back with a rejected/cancelled policy transaction.
        RequirePreparedInterventionFormat(connection, transaction);
        using var mark = Command(connection, transaction, "PRAGMA user_version = 9;");
        mark.ExecuteNonQuery();
    }

    // Workflows and their evidence use this same transaction owner. No event mirror is committed separately.
    internal T Transact<T>(Func<SqliteConnection, SqliteTransaction, T> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = operation(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return result;
    }

    internal static JournalAppendResult AppendInTransaction(SqliteConnection connection, SqliteTransaction transaction,
        CerebralEvent item, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        if (item.Sequence != 0) throw new ArgumentException("Submit an uncommitted event with sequence zero.", nameof(item));
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (item.Projection.Retention == RetentionPolicy.Ephemeral)
            throw new ArgumentException("Ephemeral progress must not be appended to the durable journal.", nameof(item));
        var submitted = Serialize(item);
        // Freeze caller-owned content collections and JsonElements before entering the transaction.
        var frozen = Deserialize(submitted);
        using var lookup = Command(connection, transaction, "SELECT payload FROM conversation_events WHERE event_id = $id;");
        lookup.Parameters.AddWithValue("$id", item.Id);
        if (lookup.ExecuteScalar() is string existingJson)
        {
            var existing = Deserialize(existingJson);
            if (!JsonNode.DeepEquals(JsonNode.Parse(Serialize(existing with { Sequence = 0 })), JsonNode.Parse(submitted)))
                throw new InvalidOperationException("Event ID already belongs to different content. Use a new ID for new work.");
            return new(existing, true);
        }
        var revision = ReadRevision(connection, transaction, item.ConversationId);
        if (revision != expectedRevision) throw new JournalRevisionException(expectedRevision, revision);
        var committed = frozen with { Sequence = checked(revision + 1) };
        using var insert = Command(connection, transaction, """
            INSERT INTO conversation_events (conversation_id, sequence, event_id, payload) VALUES ($conversation, $sequence, $id, $payload);
            INSERT INTO event_streams (conversation_id, revision) VALUES ($conversation, $sequence)
                ON CONFLICT(conversation_id) DO UPDATE SET revision = excluded.revision;
            """);
        insert.Parameters.AddWithValue("$conversation", committed.ConversationId);
        insert.Parameters.AddWithValue("$sequence", committed.Sequence);
        insert.Parameters.AddWithValue("$id", committed.Id);
        insert.Parameters.AddWithValue("$payload", Serialize(committed));
        insert.ExecuteNonQuery();
        return new(committed, false);
    }

    public long GetRevision(string conversationId)
    {
        ValidateConversation(conversationId);
        using var connection = Open();
        return ReadRevision(connection, null, conversationId);
    }

    /// <summary>Enumerates conversation identities in stable bounded pages without reading event payloads.</summary>
    public IReadOnlyList<string> ReadConversationIds(string? afterConversationId = null, int limit = 64,
        CancellationToken cancellationToken = default)
    {
        if (afterConversationId is not null) ValidateConversation(afterConversationId);
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT conversation_id
            FROM event_streams
            WHERE $after IS NULL OR conversation_id > $after COLLATE BINARY
            ORDER BY conversation_id COLLATE BINARY
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$after", afterConversationId is null ? DBNull.Value : afterConversationId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<string>(limit);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(reader.GetString(0));
        }
        return result.AsReadOnly();
    }

    /// <summary>Reads a bounded forward page; payload bytes as well as count limit memory use.</summary>
    public JournalPage ReadPage(string conversationId, long afterSequence = 0, int limit = 100,
        long? snapshotSequence = null, CancellationToken cancellationToken = default)
    {
        var page = ReadWindow(conversationId, afterSequence, limit, snapshotSequence, false, cancellationToken);
        return new(page.Events, page.Snapshot, page.Next, page.RetentionGap);
    }

    /// <summary>Reads recent history without materializing all earlier events. A null cursor starts at the snapshot end.</summary>
    public JournalPreviousPage ReadPreviousPage(string conversationId, long? beforeSequence = null, int limit = 100,
        long? snapshotSequence = null, CancellationToken cancellationToken = default)
    {
        var page = ReadWindow(conversationId, beforeSequence, limit, snapshotSequence, true, cancellationToken);
        return new(page.Events, page.Snapshot, page.Next, page.RetentionGap);
    }

    /// <summary>
    /// Removes only explicitly rolling, non-model-bearing diagnostic events for one conversation. Active or
    /// unresolved workflow evidence stays pinned. Durable accepted input, model context, questions, effects,
    /// receipts, checkpoints and domain data are outside this cleanup contract.
    /// </summary>
    public JournalPruneResult PruneRolling(string conversationId, JournalRetentionLimits limits,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateConversation(conversationId);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumRollingEvents is < 1 or > 1_000_000 ||
            limits.MaximumRollingBytes is < 1 or > 16L * 1024 * 1024 * 1024 ||
            limits.MaximumAgeDays is < 1 or > 3650)
            throw new ArgumentException("Invalid rolling event retention limits.", nameof(limits));
        var at = now.ToUniversalTime();
        var cutoff = at.AddDays(-limits.MaximumAgeDays);
        var analysis = AnalyzeRolling(conversationId, limits, cutoff, cancellationToken);
        if (analysis.Candidates.Count == 0) return Result(analysis.Advisory, limits, 0, 0);

        var applied = ApplyRollingPlan(conversationId, limits, at, cutoff, analysis, cancellationToken);
        // Once deletion and its retention marker commit atomically, cancellation no longer changes the outcome.
        // The advisory post-commit snapshot therefore completes without turning a committed prune into a reported cancellation.
        var current = ReadRollingAdvisory(conversationId, cutoff,
            applied.Applied ? CancellationToken.None : cancellationToken);
        return Result(current, limits, applied.RemovedEvents, applied.RemovedBytes);
    }

    [Flags]
    private enum RollingPruneReason { None = 0, Count = 1, Bytes = 2, Age = 4 }

    private sealed record RollingPruneCandidate(long Sequence, long Bytes, RollingPruneReason Reasons);
    private sealed record RollingRetentionMarker(long PrunedEvents, long PrunedBytes,
        long? LastPrunedSequence, string? LastPrunedAt);
    private sealed record RollingStatistics(long Events, long Bytes, long PinnedEvents, long ExpiredEvents);
    private sealed record RollingAdvisory(JournalRetentionStatus Retention, RollingStatistics Statistics);
    private sealed record RollingAnalysis(IReadOnlyList<RollingPruneCandidate> Candidates, long StreamRevision,
        RollingRetentionMarker RetentionMarker, RollingAdvisory Advisory);
    private sealed record RollingApplyResult(bool Applied, long RemovedEvents, long RemovedBytes);

    private RollingAnalysis AnalyzeRolling(string conversationId, JournalRetentionLimits limits, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var revision = ReadRevision(connection, transaction, conversationId);
        var marker = ReadRetentionMarker(connection, transaction, conversationId);
        var candidates = new List<RollingPruneCandidate>();
        using (var select = Command(connection, transaction, """
            WITH rolling AS (
                SELECT event.sequence,
                       length(CAST(event.payload AS BLOB)) AS bytes,
                       json_extract(event.payload,'$.createdAt') AS occurred_at,
                       ROW_NUMBER() OVER (ORDER BY event.sequence DESC) AS recent_rank,
                       SUM(length(CAST(event.payload AS BLOB))) OVER (
                           ORDER BY event.sequence DESC ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS recent_bytes,
                       (lower(COALESCE(json_extract(event.payload,'$.projection.model'),'')) NOT IN ('never','ondemand')
                        OR EXISTS(
                           SELECT 1 FROM workflow_runs AS run
                           WHERE run.id=json_extract(event.payload,'$.data.runId')
                             AND run.conversation_id=event.conversation_id
                             AND (lower(run.status) NOT IN ('completed','failed','cancelled')
                                  OR lower(COALESCE(json_extract(run.payload,'$.effect.state'),''))
                                     IN ('prepared','dispatched','unknown'))
                       )) AS pinned
                FROM conversation_events AS event
                WHERE event.conversation_id=$conversation
                  AND lower(COALESCE(json_extract(event.payload,'$.projection.retention'),''))='rolling'
            )
            SELECT sequence,bytes,
                   CASE WHEN recent_rank>$maximum_events THEN 1 ELSE 0 END,
                   CASE WHEN recent_bytes>$maximum_bytes THEN 1 ELSE 0 END,
                   CASE WHEN julianday(occurred_at)<julianday($cutoff) THEN 1 ELSE 0 END
            FROM rolling
            WHERE pinned=0 AND (recent_rank>$maximum_events OR recent_bytes>$maximum_bytes
                OR julianday(occurred_at)<julianday($cutoff))
            ORDER BY sequence LIMIT $maximum_prune;
            """))
        {
            select.Parameters.AddWithValue("$conversation", conversationId);
            select.Parameters.AddWithValue("$maximum_events", limits.MaximumRollingEvents);
            select.Parameters.AddWithValue("$maximum_bytes", limits.MaximumRollingBytes);
            select.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            select.Parameters.AddWithValue("$maximum_prune", MaximumPruneEvents);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reasons = (reader.GetInt64(2) != 0 ? RollingPruneReason.Count : RollingPruneReason.None) |
                              (reader.GetInt64(3) != 0 ? RollingPruneReason.Bytes : RollingPruneReason.None) |
                              (reader.GetInt64(4) != 0 ? RollingPruneReason.Age : RollingPruneReason.None);
                if (reasons == RollingPruneReason.None)
                    throw new InvalidDataException("Rolling cleanup selected an event without a retention reason.");
                candidates.Add(new(reader.GetInt64(0), reader.GetInt64(1), reasons));
            }
        }
        var statistics = ReadRollingStatistics(connection, transaction, conversationId, cutoff);
        var status = ReadRetentionStatus(connection, transaction, conversationId) ?? EmptyRetention(conversationId);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(candidates.AsReadOnly(), revision, marker, new(status, statistics));
    }

    private RollingApplyResult ApplyRollingPlan(string conversationId, JournalRetentionLimits limits,
        DateTimeOffset at, DateTimeOffset cutoff, RollingAnalysis analysis, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var planJson = JsonSerializer.Serialize(analysis.Candidates.Select(candidate => new
        {
            sequence = candidate.Sequence,
            bytes = candidate.Bytes,
            reasons = (int)candidate.Reasons
        }), Json);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (ReadRevision(connection, transaction, conversationId) != analysis.StreamRevision ||
            ReadRetentionMarker(connection, transaction, conversationId) != analysis.RetentionMarker)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(false, 0, 0);
        }

        // Appends change the stream revision and prior prunes change the monotonic retention marker. With both
        // unchanged, count/byte rank and the recorded reasons remain stable. Recheck immutable payload facts and
        // current workflow pin state here so a newly active or unresolved run can never lose its evidence.
        long validCandidates;
        using (var validate = Command(connection, transaction, """
            WITH planned AS (
                SELECT CAST(json_extract(value,'$.sequence') AS INTEGER) AS sequence,
                       CAST(json_extract(value,'$.bytes') AS INTEGER) AS bytes,
                       CAST(json_extract(value,'$.reasons') AS INTEGER) AS reasons
                FROM json_each($plan)
            )
            SELECT COUNT(*)
            FROM planned
            JOIN conversation_events AS event
              ON event.conversation_id=$conversation AND event.sequence=planned.sequence
            WHERE planned.reasons BETWEEN 1 AND 7
              AND planned.bytes=length(CAST(event.payload AS BLOB))
              AND lower(COALESCE(json_extract(event.payload,'$.projection.retention'),''))='rolling'
              AND lower(COALESCE(json_extract(event.payload,'$.projection.model'),'')) IN ('never','ondemand')
              AND ((planned.reasons & 4)=0
                   OR julianday(json_extract(event.payload,'$.createdAt'))<julianday($cutoff))
              AND NOT EXISTS(
                  SELECT 1 FROM workflow_runs AS run
                  WHERE run.id=json_extract(event.payload,'$.data.runId')
                    AND run.conversation_id=event.conversation_id
                    AND (lower(run.status) NOT IN ('completed','failed','cancelled')
                         OR lower(COALESCE(json_extract(run.payload,'$.effect.state'),''))
                            IN ('prepared','dispatched','unknown')));
            """))
        {
            validate.Parameters.AddWithValue("$plan", planJson);
            validate.Parameters.AddWithValue("$conversation", conversationId);
            validate.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            validCandidates = Convert.ToInt64(validate.ExecuteScalar());
        }
        if (validCandidates != analysis.Candidates.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(false, 0, 0);
        }
        cancellationToken.ThrowIfCancellationRequested();

        using (var remove = Command(connection, transaction, """
            WITH planned AS (
                SELECT CAST(json_extract(value,'$.sequence') AS INTEGER) AS sequence
                FROM json_each($plan)
            )
            DELETE FROM conversation_events
            WHERE conversation_id=$conversation
              AND sequence IN (SELECT sequence FROM planned);
            """))
        {
            remove.Parameters.AddWithValue("$plan", planJson);
            remove.Parameters.AddWithValue("$conversation", conversationId);
            if (remove.ExecuteNonQuery() != analysis.Candidates.Count)
                throw new InvalidOperationException("Rolling event cleanup changed unexpectedly.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var removedBytes = analysis.Candidates.Sum(candidate => candidate.Bytes);
        using (var retain = Command(connection, transaction, """
            INSERT INTO event_retention(conversation_id,pruned_events,pruned_bytes,
                first_pruned_sequence,last_pruned_sequence,last_pruned_at,
                maximum_rolling_events,maximum_rolling_bytes,maximum_age_days)
            VALUES($conversation,$events,$bytes,$first,$last,$at,$maximum_events,$maximum_bytes,$maximum_days)
            ON CONFLICT(conversation_id) DO UPDATE SET
                pruned_events=event_retention.pruned_events+excluded.pruned_events,
                pruned_bytes=event_retention.pruned_bytes+excluded.pruned_bytes,
                first_pruned_sequence=MIN(event_retention.first_pruned_sequence,excluded.first_pruned_sequence),
                last_pruned_sequence=MAX(event_retention.last_pruned_sequence,excluded.last_pruned_sequence),
                last_pruned_at=excluded.last_pruned_at,
                maximum_rolling_events=excluded.maximum_rolling_events,
                maximum_rolling_bytes=excluded.maximum_rolling_bytes,
                maximum_age_days=excluded.maximum_age_days;
            """))
        {
            retain.Parameters.AddWithValue("$conversation", conversationId);
            retain.Parameters.AddWithValue("$events", analysis.Candidates.Count);
            retain.Parameters.AddWithValue("$bytes", removedBytes);
            retain.Parameters.AddWithValue("$first", analysis.Candidates[0].Sequence);
            retain.Parameters.AddWithValue("$last", analysis.Candidates[^1].Sequence);
            retain.Parameters.AddWithValue("$at", at.ToString("O"));
            retain.Parameters.AddWithValue("$maximum_events", limits.MaximumRollingEvents);
            retain.Parameters.AddWithValue("$maximum_bytes", limits.MaximumRollingBytes);
            retain.Parameters.AddWithValue("$maximum_days", limits.MaximumAgeDays);
            retain.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(true, analysis.Candidates.Count, removedBytes);
    }

    private static RollingRetentionMarker ReadRetentionMarker(SqliteConnection connection,
        SqliteTransaction transaction, string conversationId)
    {
        using var command = Command(connection, transaction, """
            SELECT pruned_events,pruned_bytes,last_pruned_sequence,last_pruned_at
            FROM event_retention WHERE conversation_id=$conversation;
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3))
            : new(0, 0, null, null);
    }

    private static RollingStatistics ReadRollingStatistics(SqliteConnection connection, SqliteTransaction transaction,
        string conversationId, DateTimeOffset cutoff)
    {
        using var retained = Command(connection, transaction, """
            SELECT COUNT(*),COALESCE(SUM(length(CAST(event.payload AS BLOB))),0),
                   COALESCE(SUM(CASE WHEN
                       lower(COALESCE(json_extract(event.payload,'$.projection.model'),'')) NOT IN ('never','ondemand')
                       OR EXISTS(
                       SELECT 1 FROM workflow_runs AS run
                       WHERE run.id=json_extract(event.payload,'$.data.runId')
                         AND run.conversation_id=event.conversation_id
                         AND (lower(run.status) NOT IN ('completed','failed','cancelled')
                              OR lower(COALESCE(json_extract(run.payload,'$.effect.state'),''))
                                 IN ('prepared','dispatched','unknown'))
                   ) THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN julianday(json_extract(event.payload,'$.createdAt'))<julianday($cutoff)
                       THEN 1 ELSE 0 END),0)
            FROM conversation_events AS event
            WHERE event.conversation_id=$conversation
              AND lower(COALESCE(json_extract(event.payload,'$.projection.retention'),''))='rolling';
            """);
        retained.Parameters.AddWithValue("$conversation", conversationId);
        retained.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        using var reader = retained.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Rolling retention statistics could not be read.");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private RollingAdvisory ReadRollingAdvisory(string conversationId, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var statistics = ReadRollingStatistics(connection, transaction, conversationId, cutoff);
        var status = ReadRetentionStatus(connection, transaction, conversationId) ?? EmptyRetention(conversationId);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(status, statistics);
    }

    private static JournalPruneResult Result(RollingAdvisory advisory, JournalRetentionLimits limits,
        long removedEvents, long removedBytes) =>
        new(advisory.Retention, removedEvents, removedBytes, advisory.Statistics.Events, advisory.Statistics.Bytes,
            advisory.Statistics.PinnedEvents,
            advisory.Statistics.Events <= limits.MaximumRollingEvents &&
            advisory.Statistics.Bytes <= limits.MaximumRollingBytes && advisory.Statistics.ExpiredEvents == 0);

    public JournalRetentionStatus GetRetentionStatus(string conversationId)
    {
        ValidateConversation(conversationId);
        using var connection = Open();
        return ReadRetentionStatus(connection, null, conversationId) ?? EmptyRetention(conversationId);
    }

    /// <summary>Exact indexed lookup confined to one conversation and an already accepted snapshot.
    /// Missing, foreign and newer events remain unavailable; a retention gap separately reports that rolling detail
    /// in the snapshot was pruned. Projection permission belongs to the caller.</summary>
    public CerebralEvent ReadExact(string conversationId, string eventId, long snapshotSequence,
        CancellationToken cancellationToken = default)
    {
        ValidateConversation(conversationId); ValidateConversation(eventId);
        if (snapshotSequence < 0) throw new ArgumentOutOfRangeException(nameof(snapshotSequence));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (snapshotSequence > ReadRevision(connection, transaction, conversationId))
            throw new ArgumentOutOfRangeException(nameof(snapshotSequence));
        using var command = Command(connection, transaction, """
            SELECT length(CAST(payload AS BLOB)),
                CASE WHEN length(CAST(payload AS BLOB)) <= $maximum THEN payload END
            FROM conversation_events
            WHERE event_id=$event AND conversation_id=$conversation AND sequence <= $snapshot;
            """);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$snapshot", snapshotSequence);
        command.Parameters.AddWithValue("$maximum", MaximumEventBytes);
        string? payload = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetInt64(0) > MaximumEventBytes || reader.IsDBNull(1))
                    throw new InvalidDataException("Stored conversation source exceeds the supported size.");
                payload = reader.GetString(1);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (payload is not null) return Deserialize(payload);
        var gap = ReadRetentionStatus(connection, transaction, conversationId)?.Gap;
        throw new JournalSourceUnavailableException(gap is null
            ? "Conversation source does not exist in this accepted snapshot."
            : "Conversation source is unavailable; rolling detail in this snapshot was pruned. Inspect the retention gap before retrying.", gap);
    }

    private (IReadOnlyList<CerebralEvent> Events, long Snapshot, long? Next, JournalRetentionGap? RetentionGap) ReadWindow(string conversationId,
        long? cursor, int limit, long? snapshotSequence, bool previous, CancellationToken cancellationToken)
    {
        ValidateConversation(conversationId);
        if (cursor < 0 || previous && cursor == 0) throw new ArgumentOutOfRangeException(nameof(cursor));
        if (limit is < 1 or > MaximumPageEvents) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var revision = ReadRevision(connection, transaction, conversationId);
        var snapshot = snapshotSequence ?? revision;
        if (snapshot < 0 || snapshot > revision || cursor > snapshot && (!previous || cursor - 1 != snapshot))
            throw new ArgumentOutOfRangeException(nameof(snapshotSequence), "The cursor must refer to an existing conversation snapshot.");
        // Both query shapes use the conversation/sequence primary key; no history-wide projection is built.
        using var select = Command(connection, transaction, previous ? """
            SELECT payload FROM conversation_events
            WHERE conversation_id = $conversation AND ($cursor IS NULL OR sequence < $cursor) AND sequence <= $snapshot
            ORDER BY sequence DESC LIMIT $limit;
            """ : """
            SELECT payload FROM conversation_events
            WHERE conversation_id = $conversation AND sequence > $cursor AND sequence <= $snapshot
            ORDER BY sequence LIMIT $limit;
            """);
        select.Parameters.AddWithValue("$conversation", conversationId);
        select.Parameters.AddWithValue("$cursor", cursor is { } value ? value : DBNull.Value);
        select.Parameters.AddWithValue("$snapshot", snapshot);
        select.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = select.ExecuteReader();
        var items = new List<CerebralEvent>(limit);
        var bytes = 0;
        var more = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = reader.GetString(0);
            var length = Encoding.UTF8.GetByteCount(payload);
            if (length > MaximumEventBytes) throw new InvalidDataException("Stored event exceeds the supported size.");
            if (items.Count == limit || bytes + length > MaximumPageBytes) { more = true; break; }
            items.Add(Deserialize(payload));
            bytes += length;
        }
        var next = more ? items[^1].Sequence : (long?)null;
        if (previous) items.Reverse();
        var gap = ReadRetentionStatus(connection, transaction, conversationId)?.Gap;
        return (items.AsReadOnly(), snapshot, next, gap);
    }

    private static JournalRetentionStatus? ReadRetentionStatus(SqliteConnection connection,
        SqliteTransaction? transaction, string conversationId)
    {
        using var command = Command(connection, transaction, """
            SELECT pruned_events,pruned_bytes,first_pruned_sequence,last_pruned_sequence,last_pruned_at,
                   maximum_rolling_events,maximum_rolling_bytes,maximum_age_days
            FROM event_retention WHERE conversation_id=$conversation;
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(conversationId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            new(reader.GetInt32(5), reader.GetInt64(6), reader.GetInt32(7)));
    }

    private static JournalRetentionStatus EmptyRetention(string conversationId) =>
        new(conversationId, 0, 0, null, null, null, null);

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    internal static long ReadRevision(SqliteConnection connection, SqliteTransaction? transaction, string conversationId)
    {
        using var command = Command(connection, transaction, "SELECT revision FROM event_streams WHERE conversation_id = $id;");
        command.Parameters.AddWithValue("$id", conversationId);
        return command.ExecuteScalar() is long revision ? revision : 0;
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command;
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Serialize(CerebralEvent item)
    {
        var json = JsonSerializer.Serialize(item, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumEventBytes) throw new ArgumentException("Event exceeds the 2 MiB journal limit. Store large content as a resource reference.", nameof(item));
        return json;
    }

    private static CerebralEvent Deserialize(string json)
    {
        var item = JsonSerializer.Deserialize<CerebralEvent>(json, Json) ?? throw new InvalidDataException("Stored event is empty.");
        item.Validate(); return item;
    }

    private static void ValidateConversation(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 128 || id.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("Invalid conversation ID.", nameof(id));
    }
}
