using System.Collections.Concurrent;
using System.Text.Json;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

public sealed record SessionAppendResult(int Sequence, int Revision);

public sealed partial class StateStore
{
    private sealed class StoredSession
    {
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? ProjectId { get; set; }
        public string Id { get; set; } = "";
        public string Title { get; set; } = "New task";
        public string ProfileId { get; set; } = "";
        public int Revision { get; set; }
        public string? Checkpoint { get; set; }
        public int CheckpointThroughSequence { get; set; }
        public int? ResponseSharePercent { get; set; }
        public Dictionary<string, string> RemoteSessionIds { get; set; } = new(StringComparer.Ordinal);

        public static StoredSession From(TaskSession value) => new()
        {
            UpdatedAt = value.UpdatedAt,
            ProjectId = value.ProjectId,
            Id = value.Id,
            Title = value.Title,
            ProfileId = value.ProfileId,
            Revision = value.Revision,
            Checkpoint = value.Checkpoint,
            CheckpointThroughSequence = value.CheckpointThroughSequence,
            ResponseSharePercent = value.ResponseSharePercent,
            RemoteSessionIds = new(value.RemoteSessionIds, StringComparer.Ordinal)
        };

        public TaskSession ToSession() => new()
        {
            UpdatedAt = UpdatedAt,
            ProjectId = ProjectId,
            Id = Id,
            Title = Title,
            ProfileId = ProfileId,
            Revision = Revision,
            Checkpoint = Checkpoint,
            CheckpointThroughSequence = CheckpointThroughSequence,
            ResponseSharePercent = ResponseSharePercent,
            RemoteSessionIds = new(RemoteSessionIds, StringComparer.Ordinal)
        };
    }

    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, object> sessionDispatchGates = new(StringComparer.Ordinal);
    private readonly string connectionString;
    private SqliteConnection? transactionConnection;
    private SqliteTransaction? transaction;
    public string DirectoryPath { get; }

    public StateStore(string directory) : this(directory, deferDiagnosticPruning: false) { }

    // Recovery composition may initialize the existing schema, but pruning evidence waits for
    // the exact owner release. The public constructor retains ordinary startup behavior.
    internal StateStore(string directory, bool deferDiagnosticPruning)
        : this(directory, deferDiagnosticPruning, initializationObserver: null) { }

    // The internal observer exposes transaction boundaries to file-backed crash/concurrency checks.
    // It cannot provide creation provenance or change the initial ownership payload.
    internal StateStore(string directory, bool deferDiagnosticPruning,
        Action<StateStoreInitializationPhase>? initializationObserver)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "cerebral.db"),
            Pooling = true
        }.ToString();
        // Provisioning can create the database before Main acquires service.lock. Serialize both
        // constructor paths, and retain the create-new file handle until the schema commit.
        using (AcquireDatabaseCreationLease())
        {
            using var reservation = ReserveFreshDatabase();
            var fresh = reservation is not null;
            if (fresh) initializationObserver?.Invoke(StateStoreInitializationPhase.FreshDatabaseReserved);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteNonQuery();
            using var schema = connection.BeginTransaction(deferred: false);
            command.Transaction = schema;
            if (fresh) RequireUninitializedDatabase(command);
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS records(
                    kind TEXT NOT NULL,
                    id TEXT NOT NULL,
                    body TEXT NOT NULL,
                    PRIMARY KEY(kind,id)
                );
                CREATE TABLE IF NOT EXISTS sessions(
                    id TEXT NOT NULL PRIMARY KEY,
                    body TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS session_messages(
                    session_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    body TEXT NOT NULL,
                    PRIMARY KEY(session_id,sequence),
                    FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
                );
                """;
            command.ExecuteNonQuery();
            command.CommandText = "CREATE INDEX IF NOT EXISTS jobs_by_status ON records(json_extract(body,'$.status')) WHERE kind='job';";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE INDEX IF NOT EXISTS activity_by_turn ON records(json_extract(body,'$.sessionId'),json_extract(body,'$.turnSequence'),json_extract(body,'$.sequence')) WHERE kind='activity';";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE INDEX IF NOT EXISTS session_messages_recent ON session_messages(session_id,sequence DESC);";
            command.ExecuteNonQuery();
            if (fresh)
            {
                InitializeFreshRetainedOwnership(connection, schema);
                initializationObserver?.Invoke(StateStoreInitializationPhase.BeforeFreshInitializationCommit);
            }
            schema.Commit();
        }
        // Diagnostic-only schema damage must not prevent access to healthy core state.
        // Preserve the incompatible table for inspection; owner diagnostic reads still fail
        // actionably, while Capture cannot replace a confirmed inference result.
        try { InitializePromptCacheDiagnostics(prune: !deferDiagnosticPruning); }
        catch (Exception error) when (error is SqliteException or IOException) { }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    public T? Get<T>(string kind, string id)
    {
        if (kind == "session" && typeof(T) == typeof(TaskSession))
            return (T?)(object?)WithConnection(connection => ReadSession(connection, id, null, int.MaxValue));

        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT body FROM records WHERE kind=$kind AND id=$id";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() is string body
                ? JsonSerializer.Deserialize<T>(body, CerebralJson.Options)
                : default;
        });
    }

    public TaskSession? GetSession(string id, int? beforeSequence = null, int messageLimit = 200)
    {
        if (messageLimit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(messageLimit));
        if (beforeSequence is <= 0) throw new ArgumentOutOfRangeException(nameof(beforeSequence));
        return WithConnection(connection => ReadSession(connection, id, beforeSequence, messageLimit));
    }

    /// <summary>Reads conversation state without materializing any saved message bodies.</summary>
    public TaskSession? GetSessionMetadata(string id) => WithConnection(connection => ReadSessionMetadata(connection, id));

    /// <summary>Finds a conversation's latest active user turn without reading unrelated message bodies.</summary>
    public int? GetLatestUserRequestSequence(string id, DateTimeOffset? noLaterThan = null) => WithConnection<int?>(connection =>
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence
            FROM session_messages
            WHERE session_id=$id
              AND COALESCE(json_extract(body,'$.excludedFromContext'),0)=0
              AND json_extract(body,'$.role')='user'
              AND json_extract(body,'$.origin')='user'
              AND json_extract(body,'$.toolName') IS NULL
              AND ($at IS NULL OR julianday(json_extract(body,'$.at')) <= julianday($at))
            ORDER BY sequence DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", noLaterThan is null ? DBNull.Value : noLaterThan.Value.UtcDateTime.ToString("O"));
        var result = command.ExecuteScalar();
        return result is long sequence ? checked((int)sequence) : null;
    });

    /// <summary>
    /// Reads the material needed to run one turn. Before the first checkpoint this deliberately returns the complete
    /// active conversation so compaction can make its first faithful checkpoint. Afterward it returns the current
    /// turn, everything newer than the checkpoint, and the latest user requests used by routing and intent checks.
    /// The complete conversation remains available through GetSession paging.
    /// </summary>
    public TaskSession? GetSessionForWork(string id, int startSequence)
    {
        if (startSequence < 1) throw new ArgumentOutOfRangeException(nameof(startSequence));
        return WithConnection(connection =>
        {
            var session = ReadSessionMetadata(connection, id);
            if (session is null) return null;
            if (session.CheckpointThroughSequence <= 0 || string.IsNullOrWhiteSpace(session.Checkpoint))
                return ReadSession(connection, id, null, int.MaxValue);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                WITH active AS (
                    SELECT sequence,body
                    FROM session_messages
                    WHERE session_id=$id
                      AND COALESCE(json_extract(body,'$.excludedFromContext'),0)=0
                ), recent_user AS (
                    SELECT sequence
                    FROM active
                    WHERE json_extract(body,'$.role')='user'
                      AND json_extract(body,'$.origin')='user'
                      AND json_extract(body,'$.toolName') IS NULL
                    ORDER BY sequence DESC
                    LIMIT 8
                )
                SELECT body
                FROM active
                WHERE sequence >= $start
                   OR sequence > $through
                   OR sequence IN (SELECT sequence FROM recent_user)
                ORDER BY sequence
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$start", startSequence);
            command.Parameters.AddWithValue("$through", session.CheckpointThroughSequence);
            ReadMessages(command, session);
            return session;
        });
    }

    /// <summary>Reads only active material newer than the adopted checkpoint for the next compaction pass.</summary>
    public TaskSession? GetSessionForCompaction(string id) => WithConnection(connection =>
    {
        var session = ReadSessionMetadata(connection, id);
        if (session is null) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT body
            FROM session_messages
            WHERE session_id=$id
              AND sequence > $through
              AND COALESCE(json_extract(body,'$.excludedFromContext'),0)=0
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$through", session.CheckpointThroughSequence);
        ReadMessages(command, session);
        return session;
    });

    public List<T> List<T>(string kind)
    {
        if (kind == "session" && typeof(T) == typeof(TaskSession))
            return (List<T>)(object)WithConnection(ListSessions);

        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT body FROM records WHERE kind=$kind ORDER BY rowid DESC";
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read())
            {
                var value = JsonSerializer.Deserialize<T>(reader.GetString(0), CerebralJson.Options);
                if (value is not null) result.Add(value);
            }
            return result;
        });
    }

    public void Put<T>(string kind, string id, T value)
    {
        if (kind == "session" && value is TaskSession session)
        {
            WithConnection(connection =>
            {
                session.UpdatedAt = DateTimeOffset.UtcNow;
                WriteSessionMetadata(connection, session);
                var sequences = new HashSet<int>();
                foreach (var message in session.Messages)
                {
                    if (message.Sequence <= 0 || !sequences.Add(message.Sequence))
                        throw new InvalidDataException("Session messages require positive unique sequence numbers.");
                    UpsertSessionMessage(connection, session.Id, message);
                }
                return true;
            });
            return;
        }

        WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO records(kind,id,body) VALUES($kind,$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(value, CerebralJson.Options));
            command.ExecuteNonQuery();
            return true;
        });
    }

    public void Delete(string kind, string id)
    {
        WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (kind == "session")
            {
                command.CommandText = "DELETE FROM sessions WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
            }
            else
            {
                command.CommandText = "DELETE FROM records WHERE kind=$kind AND id=$id";
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$id", id);
            }
            command.ExecuteNonQuery();
            return true;
        });
    }

    private T WithConnection<T>(Func<SqliteConnection, T> action)
    {
        lock (gate)
        {
            if (transactionConnection is not null) return action(transactionConnection);
            using var connection = Open();
            return action(connection);
        }
    }

    // Establish store -> engine order without opening or borrowing a transaction. Engine decision
    // callbacks must own their complete Locked transaction, including SQLite COMMIT, inside the fence.
    internal T WithTransactionFreeGate<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            if (transactionConnection is not null || transaction is not null)
                throw new InvalidOperationException("Engine decision admission refuses an ambient StateStore transaction.");
            return action();
        }
    }

    public T Locked<T>(Func<T> action)
    {
        lock (gate)
        {
            if (transactionConnection is not null) return action();
            using var connection = Open();
            using var scope = connection.BeginTransaction();
            transactionConnection = connection;
            transaction = scope;
            try
            {
                var result = action();
                scope.Commit();
                return result;
            }
            finally
            {
                transaction = null;
                transactionConnection = null;
            }
        }
    }

    /// <summary>
    /// Orders user input and short durable adoption of model-selected actions for a conversation. Callbacks must
    /// remain synchronous and must not retain a SQLite transaction while external or asynchronous work runs.
    /// </summary>
    public T LinearizedSessions<T>(IEnumerable<string> sessionIds, Func<T> action)
    {
        var keys = sessionIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var gates = keys.Select(x => sessionDispatchGates.GetOrAdd(x, _ => new object())).ToArray();
        foreach (var item in gates) Monitor.Enter(item);
        try { return action(); }
        finally
        {
            for (var index = gates.Length - 1; index >= 0; index--) Monitor.Exit(gates[index]);
        }
    }

    public T LinearizedSession<T>(string sessionId, Func<T> action) => LinearizedSessions([sessionId], action);

    public SessionAppendResult Append(string sessionId, string role, string content, string? toolName = null, string? origin = null)
    {
        return Locked(() =>
        {
            var session = WithConnection(connection => ReadSessionMetadata(connection, sessionId))
                ?? throw new ArgumentException("Session not found.");
            var sequence = WithConnection(connection =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM session_messages WHERE session_id=$id";
                command.Parameters.AddWithValue("$id", sessionId);
                return checked(Convert.ToInt32((long)(command.ExecuteScalar() ?? 1L)));
            });
            var message = new SessionMessage
            {
                Sequence = sequence,
                Role = role,
                Content = content,
                ToolName = toolName,
                Origin = origin ?? (toolName is not null ? "tool" : "user")
            };
            WithConnection(connection =>
            {
                UpsertSessionMessage(connection, sessionId, message);
                session.Revision++;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                WriteSessionMetadata(connection, session);
                return true;
            });
            return new SessionAppendResult(sequence, session.Revision);
        });
    }

    public bool AdoptCheckpoint(string sessionId, int expectedRevision, int throughSequence, string checkpoint)
    {
        return Locked(() =>
        {
            var session = WithConnection(connection => ReadSessionMetadata(connection, sessionId))
                ?? throw new ArgumentException("Session not found.");
            if (session.Revision != expectedRevision) return false;
            session.Checkpoint = checkpoint;
            session.CheckpointThroughSequence = throughSequence;
            session.Revision++;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            WithConnection(connection =>
            {
                WriteSessionMetadata(connection, session);
                return true;
            });
            return true;
        });
    }

    private TaskSession? ReadSession(SqliteConnection connection, string id, int? beforeSequence, int limit)
    {
        var session = ReadSessionMetadata(connection, id);
        if (session is null) return null;

        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*),COALESCE(MIN(sequence),0) FROM session_messages WHERE session_id=$id";
            count.Parameters.AddWithValue("$id", id);
            using var reader = count.ExecuteReader();
            if (reader.Read())
            {
                session.MessageCount = checked((int)reader.GetInt64(0));
                var earliest = checked((int)reader.GetInt64(1));
                session.OldestLoadedSequence = earliest == 0 ? null : earliest;
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (limit == int.MaxValue)
        {
            command.CommandText = "SELECT body FROM session_messages WHERE session_id=$id ORDER BY sequence";
        }
        else
        {
            command.CommandText = """
                SELECT body FROM (
                    SELECT sequence,body
                    FROM session_messages
                    WHERE session_id=$id AND ($before IS NULL OR sequence < $before)
                    ORDER BY sequence DESC
                    LIMIT $limit
                )
                ORDER BY sequence
                """;
            command.Parameters.AddWithValue("$before", beforeSequence is null ? DBNull.Value : beforeSequence.Value);
            command.Parameters.AddWithValue("$limit", limit);
        }
        command.Parameters.AddWithValue("$id", id);
        ReadMessages(command, session);

        var firstLoaded = session.Messages.FirstOrDefault()?.Sequence;
        var earliestStored = session.OldestLoadedSequence;
        session.HasEarlierMessages = firstLoaded is not null && earliestStored is not null && firstLoaded > earliestStored;
        session.OldestLoadedSequence = firstLoaded ?? earliestStored;
        return session;
    }

    private static void ReadMessages(SqliteCommand command, TaskSession session)
    {
        using var messages = command.ExecuteReader();
        while (messages.Read())
        {
            var value = JsonSerializer.Deserialize<SessionMessage>(messages.GetString(0), CerebralJson.Options);
            if (value is not null) session.Messages.Add(value);
        }
    }

    private TaskSession? ReadSessionMetadata(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM sessions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string body
            ? JsonSerializer.Deserialize<StoredSession>(body, CerebralJson.Options)?.ToSession()
            : null;
    }

    private List<TaskSession> ListSessions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM sessions ORDER BY updated_at DESC,rowid DESC";
        using var reader = command.ExecuteReader();
        var result = new List<TaskSession>();
        while (reader.Read())
        {
            var value = JsonSerializer.Deserialize<StoredSession>(reader.GetString(0), CerebralJson.Options)?.ToSession();
            if (value is not null) result.Add(value);
        }
        return result;
    }

    private void WriteSessionMetadata(SqliteConnection connection, TaskSession session)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(id,body,updated_at)
            VALUES($id,$body,$updated)
            ON CONFLICT(id) DO UPDATE SET body=excluded.body,updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(StoredSession.From(session), CerebralJson.Options));
        command.Parameters.AddWithValue("$updated", (session.UpdatedAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void UpsertSessionMessage(SqliteConnection connection, string sessionId, SessionMessage message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_messages(session_id,sequence,body)
            VALUES($session,$sequence,$body)
            ON CONFLICT(session_id,sequence) DO UPDATE SET body=excluded.body
            WHERE body<>excluded.body
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$sequence", message.Sequence);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(message, CerebralJson.Options));
        command.ExecuteNonQuery();
    }
}
