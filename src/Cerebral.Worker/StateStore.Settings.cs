using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class StateStore
{
    private const int MaximumSettingsRecordBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding SettingsRecordUtf8 = new(false, true);

    internal int ReadWorkflowIdleExpiryDays() => WithConnection(connection =>
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE
                WHEN NOT json_valid(body) THEN 'invalid'
                WHEN json_type(body,'$.workflowIdleExpiryDays') IS NULL THEN NULL
                WHEN json_type(body,'$.workflowIdleExpiryDays')='integer'
                    THEN json_extract(body,'$.workflowIdleExpiryDays')
                ELSE 'invalid'
            END
            FROM records WHERE kind='settings' AND id='host';
            """;
        return command.ExecuteScalar() switch
        {
            null or DBNull => HostSettings.DefaultWorkflowIdleExpiryDays,
            long days when days is >= 0 and <= HostSettings.MaximumWorkflowIdleExpiryDays => (int)days,
            _ => 0
        };
    });

    /// <summary>
    /// Reads only the declared settings records. Missing is distinct from corrupt saved data;
    /// the caller owns envelope versions, facet semantics and opaque-value policy.
    /// </summary>
    internal JsonElement? ReadSettingsRecord(string kind, string id, int maximumUtf8Bytes)
    {
        if (kind is not ("plugin-settings" or "interface-layout" or "configuration-preset") &&
            !(kind == "settings" && id == "appearance-preset"))
            throw new ArgumentException("Settings record kind or identity is unsupported.");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 ||
            !string.Equals(id, id.Trim(), StringComparison.Ordinal) || id.Any(char.IsControl))
            throw new ArgumentException("Settings record identity is invalid.");
        if (maximumUtf8Bytes is < 1 or > MaximumSettingsRecordBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes), "Settings record bound is invalid.");

        return WithConnection<JsonElement?>(connection =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 1;
            // CASE keeps an oversized or non-text body out of the returned row. Length is
            // measured in UTF-8 bytes, not SQLite's text-character count.
            command.CommandText = """
                SELECT typeof(body), length(CAST(body AS BLOB)),
                       CASE WHEN typeof(body)='text' AND length(CAST(body AS BLOB)) BETWEEN 1 AND $maximum
                            THEN CAST(body AS BLOB) ELSE NULL END
                FROM records WHERE kind=$kind AND id=$id
                """;
            command.Parameters.AddWithValue("$maximum", maximumUtf8Bytes);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            if (reader.GetString(0) != "text" || reader.IsDBNull(1) ||
                reader.GetInt64(1) is < 1 || reader.GetInt64(1) > maximumUtf8Bytes ||
                reader.IsDBNull(2) || reader.GetValue(2) is not byte[] bytes ||
                bytes.LongLength != reader.GetInt64(1) || reader.Read())
                throw new InvalidDataException("Saved settings record is invalid or exceeds its byte bound.");

            try
            {
                _ = SettingsRecordUtf8.GetCharCount(bytes);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    MaxDepth = 32,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
                ValidateSettingsRecordJson(document.RootElement, envelope: true);
                return document.RootElement.Clone();
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or
                EncoderFallbackException or InvalidOperationException)
            {
                // Do not expose opaque values or parser paths in the public diagnostic.
                throw new InvalidDataException("Saved settings record contains invalid JSON.");
            }
        });
    }

    /// <summary>Lists bounded settings coordinates without materializing saved document bodies.</summary>
    internal string[] ListSettingsRecordIds(string kind, int maximum)
    {
        if (kind is not ("plugin-settings" or "interface-layout" or "configuration-preset" or "settings"))
            throw new ArgumentException("Settings record kind is unsupported.");
        if (maximum is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(maximum), "Settings record count bound is invalid.");
        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 1;
            command.CommandText = """
                SELECT id FROM records
                WHERE kind=$kind AND ($kind<>'settings' OR id='appearance-preset')
                ORDER BY id LIMIT $limit
                """;
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$limit", maximum + 1);
            using var reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.GetValue(0) is not string id ||
                    string.IsNullOrWhiteSpace(id) || id.Length > 256 ||
                    !string.Equals(id, id.Trim(), StringComparison.Ordinal) || id.Any(char.IsControl))
                    throw new InvalidDataException("Saved settings record identity is invalid.");
                ids.Add(id);
                if (ids.Count > maximum)
                    throw new InvalidDataException("Saved settings record count exceeds its bound.");
            }
            return ids.ToArray();
        });
    }

    private const int MaximumSettingsAgentBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions SettingsAgentJson = new(Cerebral.Core.CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false, MaxDepth = 32,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal Cerebral.Core.AgentProfile? GetSettingsAgentProfile(string id)
    {
        ValidateSettingsAgentId(id);
        return WithConnection(connection => ReadSettingsAgentProfile(connection, id, null));
    }

    internal Cerebral.Core.AgentProfile[] SettingsAgentProfiles() => WithConnection(connection =>
    {
        var rows = new List<(string Id, long Length)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandTimeout = 1;
            command.CommandText = "SELECT id,typeof(body),length(CAST(body AS BLOB)) FROM records WHERE kind='agent' ORDER BY id LIMIT 129";
            using var reader = command.ExecuteReader();
            long total = 0;
            while (reader.Read())
            {
                if (reader.GetValue(0) is not string id || reader.GetString(1) != "text" ||
                    reader.IsDBNull(2) || reader.GetInt64(2) is < 1 or > MaximumSettingsAgentBytes)
                    throw new InvalidDataException("Saved agent settings record is invalid.");
                ValidateSettingsAgentId(id, saved: true);
                total += reader.GetInt64(2);
                if (rows.Count == 128 || total > MaximumSettingsAgentBytes)
                    throw new InvalidDataException("Saved agent settings exceed their bounded capture.");
                rows.Add((id, reader.GetInt64(2)));
            }
        }
        return rows.Select(row => ReadSettingsAgentProfile(connection, row.Id, row.Length) ??
            throw new InvalidDataException("Saved agent settings changed during capture.")).ToArray();
    });

    private Cerebral.Core.AgentProfile? ReadSettingsAgentProfile(Microsoft.Data.Sqlite.SqliteConnection connection,
        string id, long? expectedLength)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandTimeout = 1;
        command.CommandText = """
            SELECT typeof(body),length(CAST(body AS BLOB)),
                   CASE WHEN typeof(body)='text' AND length(CAST(body AS BLOB)) BETWEEN 1 AND $maximum
                        THEN CAST(body AS BLOB) ELSE NULL END
            FROM records WHERE kind='agent' AND id=$id
            """;
        command.Parameters.AddWithValue("$maximum", MaximumSettingsAgentBytes); command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != "text" || reader.IsDBNull(1) ||
            reader.GetInt64(1) is < 1 or > MaximumSettingsAgentBytes || reader.IsDBNull(2) ||
            reader.GetValue(2) is not byte[] bytes || bytes.LongLength != reader.GetInt64(1) ||
            expectedLength is { } length && length != bytes.LongLength || reader.Read())
            throw new InvalidDataException("Saved agent settings record is invalid or changed.");
        try
        {
            _ = SettingsRecordUtf8.GetCharCount(bytes);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            ValidateSettingsRecordJson(json.RootElement, envelope: true);
            var profile = json.RootElement.Deserialize<Cerebral.Core.AgentProfile>(SettingsAgentJson) ??
                throw new InvalidDataException("Saved agent settings record is invalid.");
            if (profile.Id != id || profile.Revision < 0 || profile.PluginSettings is null || profile.PluginSettings.Count > 32)
                throw new InvalidDataException("Saved agent settings identity or revision is invalid.");
            foreach (var item in profile.PluginSettings)
            {
                ValidateSettingsAgentId(item.Key, saved: true);
                Configuration.ValidatePluginSettingsValue(item.Value);
            }
            profile.PluginSettings = profile.PluginSettings.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal);
            return profile;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("Saved agent settings record is invalid.");
        }
    }

    private static void ValidateSettingsAgentId(string id, bool saved = false)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
        {
            if (saved) throw new InvalidDataException("Saved agent settings identity is invalid.");
            throw new ArgumentException("Agent settings identity is invalid.");
        }
    }

    private static void ValidateSettingsRecordJson(JsonElement value, bool envelope, bool opaque = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(opaque ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                _ = SettingsRecordUtf8.GetByteCount(property.Name);
                if (!names.Add(property.Name) || envelope && property.Name.Contains('\0'))
                    throw new InvalidDataException("Saved settings record contains invalid or duplicate fields.");
                ValidateSettingsRecordJson(property.Value, envelope: false,
                    opaque: opaque || SettingsPreferenceJson.IsOpaqueProperty(property));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                ValidateSettingsRecordJson(item, envelope: false, opaque: opaque);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            _ = SettingsRecordUtf8.GetByteCount(value.GetString()!);
        }
    }
}

// Typed DTO fields retain exact alias/unknown-field admission. Only canonical opaque value
// fields and legacy plugin-ID maps switch key semantics; the whole subtree stays opaque.
internal static class SettingsPreferenceJson
{
    internal static bool IsOpaqueProperty(JsonProperty property) =>
        property.Name is "values" or "portableValues" ||
        property.Name == "pluginSettings" && property.Value.ValueKind == JsonValueKind.Object;
}
