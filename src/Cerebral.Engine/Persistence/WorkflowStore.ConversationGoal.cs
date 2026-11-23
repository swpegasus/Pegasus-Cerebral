using Cerebral.Contracts;
using System.Globalization;

namespace Cerebral.Engine.Persistence;

/// <summary>Display-only projection of exact accepted user messages, never a synthesized objective.</summary>
public sealed record ConversationGoalEntry(string EventId, string RunId, long Sequence, string Text,
    bool Truncated, bool Steering, DateTimeOffset CreatedAt);
public sealed record ConversationGoalSnapshot(string ConversationId, long Revision,
    ConversationGoalEntry Original, IReadOnlyList<ConversationGoalEntry> LaterInstructions, bool HasOlderInstructions);

public sealed partial class WorkflowStore
{
    public ConversationGoalSnapshot ReadConversationGoal(string definitionId, string conversationId)
    {
        Identifier(definitionId, nameof(definitionId), maximum: 256);
        Identifier(conversationId, nameof(conversationId));
        using var connection = journal.Open();
        // Display reads share a snapshot without reserving SQLite's single writer.
        using var transaction = connection.BeginTransaction(deferred: true);
        var revision = EventJournal.ReadRevision(connection, transaction, conversationId);
        List<ConversationGoalEntry> Read(bool first, long after)
        {
            using var command = EventJournal.Command(connection, transaction, $$"""
                SELECT event_id,json_extract(e.payload,'$.turnId'),e.sequence,
                    substr(COALESCE(json_extract(e.payload,'$.content[0].text'),''),1,4096),
                    length(COALESCE(json_extract(e.payload,'$.content[0].text'),''))>4096,
                    json_extract(e.payload,'$.data.steeringRequestId') IS NOT NULL,
                    json_extract(e.payload,'$.createdAt')
                FROM conversation_events e JOIN workflow_runs r ON r.id=json_extract(e.payload,'$.turnId')
                WHERE e.conversation_id=$conversation AND e.sequence<=$revision AND e.sequence>$after
                    AND json_extract(e.payload,'$.type')=$message
                    AND json_extract(e.payload,'$.actor.kind')='User'
                    AND json_extract(r.submission,'$.definitionId')=$definition
                    AND json_extract(r.submission,'$.parentRunId') IS NULL
                ORDER BY e.sequence {{(first ? "ASC" : "DESC")}} LIMIT {{(first ? 1 : 9)}};
                """);
            command.Parameters.AddWithValue("$conversation", conversationId);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$message", CerebralEventTypes.Message);
            command.Parameters.AddWithValue("$definition", definitionId);
            using var reader = command.ExecuteReader();
            var result = new List<ConversationGoalEntry>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    reader.GetString(3), reader.GetInt64(4) != 0, reader.GetInt64(5) != 0,
                    DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
            return result;
        }
        var original = Read(true, 0).SingleOrDefault()
            ?? throw new KeyNotFoundException("No original accepted user request exists for this conversation.");
        var latest = Read(false, original.Sequence);
        var more = latest.Count > 8;
        if (more) latest.RemoveAt(8);
        latest.Reverse();
        transaction.Commit();
        return new(conversationId, revision, original, latest.AsReadOnly(), more);
    }
}
