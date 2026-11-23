using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace Cerebral.Worker;

internal enum StateStoreInitializationPhase { FreshDatabaseReserved, BeforeFreshInitializationCommit }

public sealed partial class StateStore
{
    private const string CreationLeaseFileName = ".state-store-create.lock";

    private FileStream AcquireDatabaseCreationLease()
    {
        var path = Path.Combine(DirectoryPath, CreationLeaseFileName);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (IsSharingConflict(error) && deadline.Elapsed < TimeSpan.FromSeconds(5))
            { Thread.Sleep(20); }
        }
    }

    private static bool IsSharingConflict(IOException error)
    {
        var code = error.HResult & 0xffff;
        return code is 32 or 33 || !OperatingSystem.IsWindows() && code == 11;
    }

    private FileStream? ReserveFreshDatabase()
    {
        var path = Path.Combine(DirectoryPath, "cerebral.db");
        // This preliminary check avoids creating a primary beside orphan state. Only a successful
        // CreateNew supplies provenance; absence, empty tables and bootstrap lock/log files do not.
        if (!StatePathExists(path)) RequireNoPriorOwnerState();
        FileStream reservation;
        try { reservation = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite); }
        catch (IOException error) when (IsAlreadyExistingFile(error) && StatePathExists(path)) { return null; }
        try { RequireNoPriorOwnerState(); return reservation; }
        catch { reservation.Dispose(); throw; }
    }

    private static bool IsAlreadyExistingFile(IOException error)
    {
        var code = error.HResult & 0xffff;
        return code is 80 or 183 || !OperatingSystem.IsWindows() && code == 17;
    }

    private static bool StatePathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void RequireNoPriorOwnerState()
    {
        // Main's current service.lock/local-worker.log and this constructor's creation lease are
        // legitimate bootstrap files. Prior durable owner state and orphan SQLite sidecars are not.
        string[] paths = ["cerebral.db-wal", "cerebral.db-shm", "cerebral.db-journal",
            "engine.db", "engine.db-wal", "engine.db-shm", "engine.db-journal", Path.Combine("Engine", "selection.ini")];
        foreach (var relative in paths)
        {
            var path = Path.Combine(DirectoryPath, relative);
            if (StatePathExists(path))
                throw new IOException($"Fresh state-store creation is unavailable while prior owner state exists: {path}");
        }
    }

    private static void RequireUninitializedDatabase(SqliteCommand command)
    {
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            throw new InvalidDataException("Fresh state-store creation encountered preexisting schema; ownership remains unconfirmed.");
    }

    private static void InitializeFreshRetainedOwnership(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        // A strict insert is part of database birth, never an absence repair or an ownership reset.
        command.CommandText = """
            INSERT INTO records(kind,id,body) VALUES('remote-prepared-routes','quarantine','{"version":1,"entries":[]}');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Short observation/admission linearization without Open(), initialization, or a write transaction.</summary>
    internal T WithModelAccountingState<T>(Func<T> action, CancellationToken token)
    {
        var entered = false;
        try
        {
            while (!(entered = Monitor.TryEnter(gate, 20))) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            return action();
        }
        finally { if (entered) Monitor.Exit(gate); }
    }

    internal byte[]? ReadRemotePreparedOwnershipBody(CancellationToken token) => WithModelAccountingState(() =>
    {
        token.ThrowIfCancellationRequested();
        // Normal mutation may already own a transaction; observe its current row, not a stale second connection.
        using var owned = transactionConnection is null ? new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "cerebral.db"), Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 1
        }.ToString()) : null;
        if (owned is not null) owned.Open();
        var connection = transactionConnection ?? owned!;
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 1;
        command.CommandText = "SELECT typeof(body), length(CAST(body AS BLOB)), substr(CAST(body AS BLOB),1,262145) FROM records WHERE kind='remote-prepared-routes' AND id='quarantine'";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != "text" || reader.IsDBNull(1) || reader.GetInt64(1) is < 1 or > 262144 || reader.IsDBNull(2))
            throw new InvalidDataException("remote_prepared_storage_invalid");
        var bytes = (byte[])reader.GetValue(2);
        if (bytes.Length != reader.GetInt64(1) || reader.Read()) throw new InvalidDataException("remote_prepared_storage_changed");
        token.ThrowIfCancellationRequested();
        return bytes;
    }, token);
}
