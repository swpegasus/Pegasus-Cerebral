using System.Globalization;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    /// <summary>Queue display must not deserialize persisted model contexts or effect payloads.</summary>
    public WorkflowActiveSummaryPage ListActiveSummaries(int limit = 40, long afterRow = 0)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterRow < 0) throw new ArgumentOutOfRangeException(nameof(afterRow));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT rowid,id,conversation_id,profile_id,status,revision,
                substr(json_extract(submission,'$.userText'),1,160),
                json_extract(submission,'$.definitionId'),json_extract(payload,'$.updatedAt')
            FROM workflow_runs WHERE status NOT IN ('Completed','Failed','Cancelled') AND rowid > $after
            ORDER BY rowid LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$after", afterRow);
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = command.ExecuteReader();
        var rows = new List<WorkflowRunSummary>(limit); long last = 0; var more = false;
        while (reader.Read())
        {
            if (rows.Count == limit) { more = true; break; }
            last = reader.GetInt64(0);
            var title = reader.GetString(6).Replace('\r', ' ').Replace('\n', ' ');
            if (title.Length > 160) title = title[..(char.IsHighSurrogate(title[159]) ? 159 : 160)];
            rows.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), title,
                reader.GetString(7), Enum.Parse<WorkflowStatus>(reader.GetString(4)), reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        }
        return new(rows.AsReadOnly(), more ? last : null);
    }
}
