using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class StateStore
{
    // Completed receipts stay addressable for reconnect and idempotency. They never enter these queue queries.
    private const string ActiveJobPredicate = "json_extract(body,'$.status') IN ('queued','running','waiting-human','waiting-helper','remote-waiting','remote-unconfirmed')";

    public List<AgentJob> ActiveJobs() => ReadJobs(ActiveJobPredicate, null, null);

    /// <summary>Projects queue display fields in SQLite without loading prompts, profile snapshots or results.</summary>
    public LegacyJobSummaryPage ActiveJobSummaries(int limit = 40, long afterRow = 0)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterRow < 0) throw new ArgumentOutOfRangeException(nameof(afterRow));
        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT rowid,id,COALESCE(json_extract(body,'$.sessionId'),''),COALESCE(json_extract(body,'$.profileId'),''),
                    substr(COALESCE(NULLIF(json_extract(body,'$.visiblePrompt'),''),
                        (SELECT NULLIF(json_extract(s.body,'$.title'),'') FROM sessions s WHERE s.id=json_extract(r.body,'$.sessionId')),
                        'Background work'),1,160),
                    COALESCE(json_extract(body,'$.kind'),'agent'),json_extract(body,'$.status'),json_extract(body,'$.createdAt')
                FROM records r WHERE kind='job'
                """ + " AND " + ActiveJobPredicate + " AND rowid > $after ORDER BY rowid LIMIT $limit;";
            command.Parameters.AddWithValue("$after", afterRow);
            command.Parameters.AddWithValue("$limit", limit + 1);
            using var reader = command.ExecuteReader();
            var rows = new List<LegacyJobSummary>(limit); long last = 0; var more = false;
            while (reader.Read())
            {
                if (rows.Count == limit) { more = true; break; }
                last = reader.GetInt64(0);
                var title = reader.GetString(4).Replace('\r', ' ').Replace('\n', ' ');
                if (title.Length > 160) title = title[..(char.IsHighSurrogate(title[159]) ? 159 : 160)];
                rows.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), title,
                    reader.GetString(5), reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture), null));
            }
            return new LegacyJobSummaryPage(rows.AsReadOnly(), more ? last : null);
        });
    }

    public int ActiveJobCount() => WithConnection(connection =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM records WHERE kind='job' AND " + ActiveJobPredicate;
        return checked((int)(long)command.ExecuteScalar()!);
    });

    public List<AgentJob> JobHistory(int limit, int days)
    {
        if (limit is < 1 or > 10000 || days is < 1 or > 3650) throw new ArgumentException("History display limits must be 1-10000 jobs and 1-3650 days.");
        return ReadJobs("NOT (" + ActiveJobPredicate + ") AND julianday(COALESCE(json_extract(body,'$.finishedAt'),json_extract(body,'$.createdAt'))) >= julianday($since)",
            limit, DateTimeOffset.UtcNow.AddDays(-days));
    }

    private List<AgentJob> ReadJobs(string predicate, int? limit, DateTimeOffset? since) => WithConnection(connection =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT body FROM records WHERE kind='job' AND " + predicate +
            " ORDER BY julianday(COALESCE(json_extract(body,'$.finishedAt'),json_extract(body,'$.createdAt'))) DESC, id" + (limit is null ? "" : " LIMIT $limit");
        if (limit is not null) command.Parameters.AddWithValue("$limit", limit.Value);
        if (since is not null) command.Parameters.AddWithValue("$since", since.Value.ToString("O"));
        using var reader = command.ExecuteReader(); var result = new List<AgentJob>();
        while (reader.Read()) { var value = JsonSerializer.Deserialize<AgentJob>(reader.GetString(0), CerebralJson.Options); if (value is not null) result.Add(value); }
        return result;
    });
}
