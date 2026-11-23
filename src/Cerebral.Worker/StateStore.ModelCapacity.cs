using System.Text.Json;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

internal sealed class ModelCapacityUnavailableException(string reason) : IOException(reason);
internal sealed record ModelCapacityStoredProfile(string Id, byte[] Body, AgentProfile Value);
internal sealed record ModelCapacityStoredModel(string Id, byte[] Body, ModelRecord Value);
internal sealed record ModelCapacityStoredState(byte[] SettingsBody, HostSettings Settings,
    ModelCapacityStoredModel Model, IReadOnlyList<ModelCapacityStoredModel> Models,
    IReadOnlyList<ModelCapacityStoredProfile> Profiles, long TotalStoredBytes);
internal sealed record ModelCapacityPreparedRow(string Kind, string Id, JsonElement Value,
    string SerializedBody, int Utf8Bytes);
internal sealed record ModelCapacityPreparedWrites(IReadOnlyList<ModelCapacityPreparedRow> Rows, long TotalStoredBytes);

public sealed partial class StateStore
{
    private const int CapacityMaximumSettingsBytes = 262144;
    private const int CapacityMaximumModelBytes = 2097152;
    private const int CapacityMaximumProfileBytes = 262144;
    private const int CapacityMaximumCatalogBytes = 16777216;
    private const int CapacityMaximumCatalogProfiles = 4096;
    private const int CapacityMaximumCatalogModels = 256;

    // Preview opens a read-only transaction, never Configuration.Get/Open or a migration path.
    // Apply borrows the current Locked transaction, so comparison and all updates share its fence.
    internal T WithModelCapacityState<T>(string modelId, Func<ModelCapacityStoredState, T> read,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(read);
        var entered = false;
        try
        {
            while (!(entered = Monitor.TryEnter(gate, 20))) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (transactionConnection is not null)
                return read(Capture(transactionConnection, transaction));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(DirectoryPath, "cerebral.db"), Mode = SqliteOpenMode.ReadOnly,
                Pooling = false, DefaultTimeout = 1
            }.ToString());
            connection.Open();
            var version = SnapshotDataVersion(connection);
            using var scope = connection.BeginTransaction(deferred: true);
            var result = read(Capture(connection, scope));
            token.ThrowIfCancellationRequested();
            scope.Commit();
            if (SnapshotDataVersion(connection) != version)
                throw new ModelCapacityUnavailableException("capacity_store_changed");
            return result;
        }
        finally { if (entered) Monitor.Exit(gate); }

        ModelCapacityStoredState Capture(SqliteConnection connection, SqliteTransaction? scope)
        {
            try { return ReadModelCapacityState(connection, scope, modelId, token); }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { throw new ModelCapacityUnavailableException("capacity_storage_invalid"); }
        }
    }

    private static ModelCapacityStoredState ReadModelCapacityState(SqliteConnection connection,
        SqliteTransaction? scope, string modelId, CancellationToken token)
    {
        long remaining = CapacityMaximumCatalogBytes;
        byte[] ReadBody(SqliteDataReader row, int typeColumn, int lengthColumn, int bodyColumn, int maximum, Type rowType)
        {
            if (row.GetString(typeColumn) != "text" || row.IsDBNull(lengthColumn) || row.IsDBNull(bodyColumn))
                throw new ModelCapacityUnavailableException("capacity_storage_body");
            var length = row.GetInt64(lengthColumn);
            if (length < 1 || length > maximum || length > remaining)
                throw new ModelCapacityUnavailableException("capacity_storage_byte_bound");
            var body = (byte[])row.GetValue(bodyColumn);
            if (body.Length != length) throw new ModelCapacityUnavailableException("capacity_storage_body");
            remaining -= length;
            ValidateCapacityJson(body, rowType.GetProperties().Select(property =>
                CerebralJson.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name));
            return body;
        }
        SqliteCommand Command(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = scope; command.CommandTimeout = 1; command.CommandText = sql;
            return command;
        }
        byte[] settingsBody;
        using (var command = Command("SELECT typeof(body),length(CAST(body AS BLOB)),substr(CAST(body AS BLOB),1,$limit) FROM records WHERE kind='settings' AND id='host'"))
        {
            command.Parameters.AddWithValue("$limit", CapacityMaximumSettingsBytes + 1);
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new ModelCapacityUnavailableException("capacity_settings_missing");
            settingsBody = ReadBody(row, 0, 1, 2, CapacityMaximumSettingsBytes, typeof(HostSettings));
        }
        var settings = JsonSerializer.Deserialize<HostSettings>(settingsBody, CerebralJson.Options)
            ?? throw new ModelCapacityUnavailableException("capacity_settings_invalid");
        if (settings.Revision < 0 || settings.MaxConcurrentJobs is < 1 or > 16 || settings.ReservedMemoryMb < 0)
            throw new ModelCapacityUnavailableException("capacity_settings_invalid");

        // Read every bounded catalog row and deserialize using the same case-insensitive options
        // as ordinary profile reads. SQL json_extract alone cannot prove affected membership.
        List<(string Id, byte[] Body)> ReadCatalog(string kind, int rows, int maximum)
        {
            var result = new List<(string, byte[])>();
            using var command = Command("SELECT typeof(id),length(CAST(id AS BLOB)),substr(CAST(id AS BLOB),1,513),typeof(body),length(CAST(body AS BLOB)),CASE WHEN length(CAST(body AS BLOB)) BETWEEN 1 AND $bodyLimit THEN CAST(body AS BLOB) ELSE NULL END FROM records WHERE kind=$kind ORDER BY id COLLATE BINARY LIMIT $rows");
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$bodyLimit", Math.Min(maximum, remaining));
            command.Parameters.AddWithValue("$rows", rows + 1);
            using var row = command.ExecuteReader();
            while (row.Read())
            {
                token.ThrowIfCancellationRequested();
                if (result.Count == rows) throw new ModelCapacityUnavailableException("capacity_catalog_row_bound");
                if (row.GetString(0) != "text" || row.IsDBNull(1) || row.GetInt64(1) is < 1 or > 512 || row.IsDBNull(2))
                    throw new ModelCapacityUnavailableException("capacity_storage_coordinate");
                var id = new System.Text.UTF8Encoding(false, true).GetString((byte[])row.GetValue(2));
                Configuration.ValidateId(id);
                result.Add((id, ReadBody(row, 3, 4, 5, maximum, kind == "model" ? typeof(ModelRecord) : typeof(AgentProfile))));
            }
            return result;
        }
        var models = ReadCatalog("model", CapacityMaximumCatalogModels, CapacityMaximumModelBytes)
            .Select(row => new ModelCapacityStoredModel(row.Id, row.Body,
                JsonSerializer.Deserialize<ModelRecord>(row.Body, CerebralJson.Options)
                ?? throw new ModelCapacityUnavailableException("capacity_model_invalid"))).ToArray();
        if (models.Any(x => x.Id != x.Value.Id)) throw new ModelCapacityUnavailableException("capacity_model_coordinate");
        var model = models.SingleOrDefault(x => x.Id == modelId)
            ?? throw new ModelCapacityUnavailableException("capacity_model_missing");
        var profiles = new List<ModelCapacityStoredProfile>();
        foreach (var row in ReadCatalog("agent", CapacityMaximumCatalogProfiles, CapacityMaximumProfileBytes))
        {
            var profile = JsonSerializer.Deserialize<AgentProfile>(row.Body, CerebralJson.Options)
                ?? throw new ModelCapacityUnavailableException("capacity_profile_invalid");
            if (profile.Id != row.Id) throw new ModelCapacityUnavailableException("capacity_profile_coordinate");
            if (profile.ModelWorkerId != "local" || profile.ModelId != modelId) continue;
            if (profiles.Count == ModelCapacityProtocol.MaximumAffectedProfiles)
                throw new ModelCapacityUnavailableException("capacity_affected_profile_bound");
            profiles.Add(new(row.Id, row.Body, profile));
        }
        token.ThrowIfCancellationRequested();
        return new(settingsBody, settings, model, models, profiles.AsReadOnly(),
            CapacityMaximumCatalogBytes - remaining);
    }

    // Use the exact ordinary Put<JsonElement> serializer before any row is written. JsonElement
    // clones are immutable and Put uses the same value/options/generic overload, so its stored
    // string is exactly SerializedBody. Unknown values may expand (escaping/indentation), and
    // even a revision digit can grow: both per-row and complete post-save catalog limits apply.
    internal static ModelCapacityPreparedWrites PrepareCapacityWrites(ModelCapacityStoredState state,
        IReadOnlyDictionary<string, JsonElement> modelChanges,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> profileChanges)
    {
        if (profileChanges.Count != state.Profiles.Count ||
            state.Profiles.Any(profile => !profileChanges.ContainsKey(profile.Id)))
            throw new ModelCapacityUnavailableException("capacity_prepared_profile_membership");
        var rows = new List<ModelCapacityPreparedRow>(state.Profiles.Count + 1);
        var total = state.TotalStoredBytes;
        var replacedBytes = state.Profiles.Sum(profile => (long)profile.Body.Length) + state.Model.Body.Length;
        if (total < state.SettingsBody.Length + replacedBytes || total > CapacityMaximumCatalogBytes)
            throw new ModelCapacityUnavailableException("capacity_prepared_catalog_bound");
        void Add(string kind, string id, byte[] before, IReadOnlyDictionary<string, JsonElement> changes, int maximum)
        {
            var value = PatchCapacityFields(before, changes);
            var body = JsonSerializer.Serialize(value, CerebralJson.Options);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(body);
            if (bytes > maximum)
                throw new ModelCapacityUnavailableException("capacity_prepared_row_bound");
            var rowType = kind == "model" ? typeof(ModelRecord) : typeof(AgentProfile);
            ValidateCapacityJson(System.Text.Encoding.UTF8.GetBytes(body), rowType.GetProperties().Select(property =>
                CerebralJson.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name));
            total = checked(total - before.Length + bytes);
            rows.Add(new(kind, id, value, body, bytes));
        }
        Add("model", state.Model.Id, state.Model.Body, modelChanges, CapacityMaximumModelBytes);
        foreach (var profile in state.Profiles)
            Add("agent", profile.Id, profile.Body, profileChanges[profile.Id], CapacityMaximumProfileBytes);
        if (total > CapacityMaximumCatalogBytes)
            throw new ModelCapacityUnavailableException("capacity_prepared_catalog_bound");
        return new(rows.AsReadOnly(), total);
    }

    private static void ValidateCapacityJson(byte[] body, IEnumerable<string>? typedRootNames = null)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ModelCapacityUnavailableException("capacity_storage_shape");
        var typedNames = new HashSet<string>(typedRootNames ?? [], StringComparer.OrdinalIgnoreCase);
        var nodes = 0;
        void Visit(JsonElement value, bool root = false)
        {
            if (++nodes > 65536) throw new ModelCapacityUnavailableException("capacity_storage_shape");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var typedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name) || root && typedNames.Contains(property.Name) && !typedKeys.Add(property.Name))
                        throw new ModelCapacityUnavailableException("capacity_storage_duplicate");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Visit(item);
        }
        Visit(document.RootElement, root: true);
    }

    // Patch only named fields in raw JSON; unknown persisted members and unrelated metadata
    // survive. Serialization may change whitespace, never the untouched JSON values.
    internal static JsonElement PatchCapacityFields(byte[] body, IReadOnlyDictionary<string, JsonElement> changes)
    {
        ValidateCapacityJson(body, changes.Keys);
        using var original = JsonDocument.Parse(body);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in original.RootElement.EnumerateObject())
            {
                var key = changes.Keys.FirstOrDefault(x => x.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
                if (key is null) property.WriteTo(writer);
                else { writer.WritePropertyName(key); changes[key].WriteTo(writer); written.Add(key); }
            }
            foreach (var change in changes)
                if (!written.Contains(change.Key)) { writer.WritePropertyName(change.Key); change.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        using var result = JsonDocument.Parse(buffer.ToArray());
        return result.RootElement.Clone();
    }
}
