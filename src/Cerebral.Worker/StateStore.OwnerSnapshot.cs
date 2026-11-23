using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

internal sealed record OwnerSnapshotRawState(byte[]? ConfigurationBody,
    IReadOnlyList<OwnerSnapshotRawModelRow> Models, bool CatalogComplete, IReadOnlyList<string> Diagnostics);

public sealed partial class StateStore
{
    internal const int MaximumSnapshotConfigurationBytes = 256 * 1024;

    /// <summary>One coordinate-bearing committed read snapshot. No Open/Create, PRAGMA repair, Get/List defaults or writes.</summary>
    private long ownerSnapshotModelRowsRead;
    internal long OwnerSnapshotModelRowsRead => Interlocked.Read(ref ownerSnapshotModelRowsRead);

    internal T WithOwnerSnapshotState<T>(bool includeModels, Func<OwnerSnapshotRawState, T> read,
        CancellationToken token, Action<byte[]?>? requireOwner = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        var entered = false;
        try
        {
            while (!(entered = Monitor.TryEnter(gate, 20))) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(DirectoryPath, "cerebral.db"), Mode = SqliteOpenMode.ReadOnly,
                Pooling = false, DefaultTimeout = 1
            }.ToString());
            connection.Open();
            var before = SnapshotDataVersion(connection);
            using var scope = connection.BeginTransaction(deferred: true);
            byte[]? configuration = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = scope; command.CommandTimeout = 1;
                command.CommandText = """
                    SELECT typeof(body), length(CAST(body AS BLOB)),
                           substr(CAST(body AS BLOB),1,$limit)
                    FROM records WHERE kind='settings' AND id='host'
                    """;
                command.Parameters.AddWithValue("$limit", MaximumSnapshotConfigurationBytes + 1);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetString(0) != "text" || reader.IsDBNull(1) ||
                        reader.GetInt64(1) is < 1 or > MaximumSnapshotConfigurationBytes || reader.IsDBNull(2))
                        throw new OwnerSnapshotUnavailableException("configuration_storage_invalid");
                    configuration = (byte[])reader.GetValue(2);
                    if (reader.Read()) throw new OwnerSnapshotUnavailableException("configuration_coordinate_duplicate");
                }
            }
            requireOwner?.Invoke(configuration); // Identity/rotation refusal precedes every model read.
            token.ThrowIfCancellationRequested();
            var rows = new List<OwnerSnapshotRawModelRow>();
            var issues = new List<string>();
            var complete = true; long bodyBytes = 0;
            if (includeModels)
            {
                // Project one bounded row at a time. The remaining-byte predicate prevents even
                // SQLite's result projection from materializing a second over-budget body.
                for (var visited = 0; visited <= OwnerModelSnapshotCodec.MaximumCatalogRows; visited++)
                {
                    token.ThrowIfCancellationRequested();
                    using var command = connection.CreateCommand();
                    command.Transaction = scope; command.CommandTimeout = 1;
                    command.CommandText = """
                        SELECT typeof(id), length(CAST(id AS BLOB)), substr(CAST(id AS BLOB),1,513),
                               typeof(body), length(CAST(body AS BLOB)),
                               CASE WHEN typeof(body)='text' AND length(CAST(body AS BLOB)) BETWEEN 1 AND $remaining
                                    THEN CAST(body AS BLOB) ELSE NULL END
                        FROM records WHERE kind='model' ORDER BY id COLLATE BINARY LIMIT 1 OFFSET $row
                        """;
                    command.Parameters.AddWithValue("$row", visited);
                    command.Parameters.AddWithValue("$remaining", visited == OwnerModelSnapshotCodec.MaximumCatalogRows
                        ? 0 : OwnerModelSnapshotCodec.MaximumModelBodyBytes - bodyBytes);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) break;
                    Interlocked.Increment(ref ownerSnapshotModelRowsRead);
                    if (visited == OwnerModelSnapshotCodec.MaximumCatalogRows)
                    { complete = false; AddIssue("catalog_row_bound"); break; }
                    string? id = null;
                    if (reader.GetString(0) == "text" && !reader.IsDBNull(1) && reader.GetInt64(1) is >= 1 and <= 512)
                    {
                        try
                        {
                            id = new System.Text.UTF8Encoding(false, true).GetString((byte[])reader.GetValue(2));
                            OwnerModelSnapshotCodec.ValidateIdentifier(id);
                        }
                        catch (Exception error) when (error is System.Text.DecoderFallbackException or InvalidDataException)
                        { id = null; }
                    }
                    if (id is null) { complete = false; AddIssue("invalid_storage_coordinate"); }
                    var validBodyType = reader.GetString(3) == "text" && !reader.IsDBNull(4);
                    var length = validBodyType ? reader.GetInt64(4) : -1;
                    if (validBodyType && length > OwnerModelSnapshotCodec.MaximumModelBodyBytes - bodyBytes)
                    {
                        complete = false; AddIssue("catalog_byte_bound");
                        if (id is not null) rows.Add(new(id, []));
                        break;
                    }
                    if (!validBodyType || length < 1 || reader.IsDBNull(5))
                    {
                        complete = false; AddIssue("invalid_storage_body");
                        if (id is not null) rows.Add(new(id, []));
                        continue;
                    }
                    var body = (byte[])reader.GetValue(5);
                    bodyBytes += length; // Count projected bytes even when this coordinate is invalid.
                    if (body.Length != length) { complete = false; AddIssue("storage_body_changed"); break; }
                    if (id is not null) rows.Add(new(id, body));
                }
            }
            token.ThrowIfCancellationRequested();
            var result = read(new(configuration, rows.AsReadOnly(), complete, issues.AsReadOnly()));
            token.ThrowIfCancellationRequested();
            scope.Commit(); // End the read transaction before checking this connection's external-write fence.
            if (SnapshotDataVersion(connection) != before)
                throw new OwnerSnapshotUnavailableException("retained_snapshot_changed");
            return result;

            void AddIssue(string code) { if (issues.Count < 16 && !issues.Contains(code, StringComparer.Ordinal)) issues.Add(code); }
        }
        catch (SqliteException) { throw new OwnerSnapshotUnavailableException("retained_store_unavailable"); }
        finally { if (entered) Monitor.Exit(gate); }
    }

    private static long SnapshotDataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1; command.CommandText = "PRAGMA data_version;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
