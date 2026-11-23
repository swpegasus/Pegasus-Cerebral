using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class StateStore
{
    /// <summary>Bounded rotating read of unfinished plugin follow-up intents; never hydrates completed history.</summary>
    internal IReadOnlyList<InterfaceRequest> ListPluginTestContinuations(string? afterId, int limit)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT body FROM records
                WHERE kind='interface' AND json_extract(body,'$.pluginTests.status') IN ('pending','admitted')
                ORDER BY CASE WHEN $after IS NULL OR id > $after THEN 0 ELSE 1 END,id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$after", afterId is null ? DBNull.Value : afterId);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var results = new List<InterfaceRequest>(limit);
            while (reader.Read())
                results.Add(JsonSerializer.Deserialize<InterfaceRequest>(reader.GetString(0), CerebralJson.Options)
                    ?? throw new InvalidDataException("A retained interface continuation is invalid."));
            return results;
        });
    }
}
