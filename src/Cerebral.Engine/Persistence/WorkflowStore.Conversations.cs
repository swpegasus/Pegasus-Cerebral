using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using System.Text.Json;

namespace Cerebral.Engine.Persistence;

/// <summary>Owner-root navigation coordinates for an exact non-chat workflow.</summary>
public sealed record OwnerWorkflowConversation(ConversationSummary Summary, string DefinitionId,
    string DefinitionVersion, string AcceptedRunId);

public sealed record OwnerWorkflowConversationPage(IReadOnlyList<OwnerWorkflowConversation> Conversations,
    long SnapshotRow, long? NextBeforeRow);

public sealed partial class WorkflowStore
{
    /// <summary>One row per owner conversation, ordered by its latest root turn. Child contexts remain available by exact identity.</summary>
    public ConversationRecentPage ListConversations(string definitionId, int limit = 40, long? beforeRow = null, long? snapshotRow = null) =>
        ReadConversations(definitionId, limit, beforeRow, snapshotRow);

    /// <summary>Project membership and pins supplied by the owner service, independent of the recent-chat page.</summary>
    public ConversationRecentPage ListProtectedConversations(string definitionId, IReadOnlyCollection<string> pinnedIds,
        int limit = 200, long? beforeRow = null, long? snapshotRow = null)
    {
        ValidatePinnedConversationIds(pinnedIds);
        return ReadConversations(definitionId, limit, beforeRow, snapshotRow, protectedIds: pinnedIds);
    }

    public ConversationRecentPage ListGeneralConversations(string definitionId, IReadOnlyCollection<string> pinnedIds,
        int limit = 40, long? beforeRow = null, long? snapshotRow = null)
    {
        ValidatePinnedConversationIds(pinnedIds);
        return ReadConversations(definitionId, limit, beforeRow, snapshotRow, protectedIds: pinnedIds, generalOnly: true);
    }

    private static void ValidatePinnedConversationIds(IReadOnlyCollection<string> pinnedIds)
    {
        ArgumentNullException.ThrowIfNull(pinnedIds);
        if (pinnedIds.Count > 1000) throw new ArgumentException("Pinned conversation lookup exceeded the sidebar bound.");
        foreach (var id in pinnedIds) Identifier(id, nameof(pinnedIds));
    }

    public ConversationSummary? GetConversation(string definitionId, string conversationId)
    {
        Identifier(conversationId, nameof(conversationId));
        return ReadConversations(definitionId, 1, conversationId: conversationId).Conversations.SingleOrDefault();
    }

    /// <summary>Resolve a complete retained owner tuple. A caller-supplied definition is only a filter.</summary>
    public OwnerWorkflowConversation GetOwnerWorkflowConversation(string conversationId, string acceptedRunId,
        string definitionId, string definitionVersion)
    {
        Identifier(conversationId, nameof(conversationId)); Identifier(acceptedRunId, nameof(acceptedRunId));
        Identifier(definitionId, nameof(definitionId), maximum: 256);
        Identifier(definitionVersion, nameof(definitionVersion), maximum: 100);
        // Navigation needs only the accepted identity. Keep private workflow state and
        // context in SQLite; loading a WorkflowRun here can be arbitrarily expensive.
        string profileId;
        string? projectId;
        using (var connection = journal.Open())
        using (var command = EventJournal.Command(connection, null, """
            SELECT profile_id,json_extract(submission,'$.projectId')
            FROM workflow_runs
            WHERE id=$run AND conversation_id=$conversation
                AND json_extract(submission,'$.definitionId')=$definition
                AND json_extract(submission,'$.definitionVersion')=$version
                AND json_extract(submission,'$.parentRunId') IS NULL
                AND (json_extract(submission,'$.caller.kind') IS NULL
                    OR json_extract(submission,'$.caller.kind')='User');
            """))
        {
            command.Parameters.AddWithValue("$run", acceptedRunId);
            command.Parameters.AddWithValue("$conversation", conversationId);
            command.Parameters.AddWithValue("$definition", definitionId);
            command.Parameters.AddWithValue("$version", definitionVersion);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("Owner workflow conversation was not found.");
            profileId = reader.GetString(0);
            projectId = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        var entry = ReadOwnerWorkflowConversations(1, conversationId: conversationId).Conversations.SingleOrDefault();
        if (entry is null || entry.DefinitionId != definitionId || entry.DefinitionVersion != definitionVersion ||
            entry.Summary.ProfileId != profileId || entry.Summary.ProjectId != projectId)
            throw new KeyNotFoundException("Owner workflow conversation was not found.");
        if (entry.AcceptedRunId != acceptedRunId)
            throw new KeyNotFoundException("The selected workflow run is no longer the latest owner turn. Refresh the conversation list and open the current run.");
        return entry;
    }

    /// <summary>Bounded non-chat owner-root discovery; chat navigation retains its existing filter.</summary>
    public OwnerWorkflowConversationPage ListOwnerWorkflowConversations(int limit = 40, long? beforeRow = null,
        long? snapshotRow = null, IReadOnlyCollection<string>? protectedIds = null, bool generalOnly = false,
        string? excludedDefinitionId = null, string? conversationId = null) =>
        ReadOwnerWorkflowConversations(limit, beforeRow, snapshotRow, conversationId: conversationId, protectedIds: protectedIds,
            generalOnly: generalOnly, excludedDefinitionId: excludedDefinitionId);

    private OwnerWorkflowConversationPage ReadOwnerWorkflowConversations(int limit, long? beforeRow = null,
        long? snapshotRow = null, string? conversationId = null, IReadOnlyCollection<string>? protectedIds = null,
        bool generalOnly = false, string? excludedDefinitionId = null)
    {
        if (limit is < 1 or > 200 || beforeRow is < 1 || snapshotRow is < 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (conversationId is not null) Identifier(conversationId, nameof(conversationId));
        if (protectedIds is not null) ValidatePinnedConversationIds(protectedIds);
        if (excludedDefinitionId is not null) Identifier(excludedDefinitionId, nameof(excludedDefinitionId), maximum: 256);
        using var connection = journal.Open();
        using var snapshot = EventJournal.Command(connection, null, "SELECT COALESCE(MAX(rowid),0) FROM workflow_runs;");
        var current = (long)snapshot.ExecuteScalar()!;
        var bound = snapshotRow ?? current;
        if (bound > current) throw new ArgumentException("The requested conversation snapshot is unavailable.");
        using var command = EventJournal.Command(connection, null, $$"""
            WITH owner_turns AS (
                SELECT rowid,conversation_id,
                    json_extract(submission,'$.definitionId') AS definition_id,
                    json_extract(submission,'$.definitionVersion') AS definition_version
                FROM workflow_runs
                WHERE rowid <= $snapshot
                    AND json_extract(submission,'$.parentRunId') IS NULL
                    AND (json_extract(submission,'$.caller.kind') IS NULL
                        OR json_extract(submission,'$.caller.kind')='User')
                    {{(conversationId is null ? "" : "AND conversation_id=$conversation")}}
            ), turns AS (
                SELECT conversation_id,MIN(rowid) AS first_row,MAX(rowid) AS last_row,
                    MIN(definition_id) AS first_definition,MAX(definition_id) AS last_definition,
                    MIN(definition_version) AS first_version,MAX(definition_version) AS last_version
                FROM owner_turns GROUP BY conversation_id
            )
            SELECT turns.last_row,latest.conversation_id,latest.profile_id,latest.id,latest.status,
                json_extract(latest.payload,'$.updatedAt'),
                COALESCE(titles.title,
                    CASE WHEN length(trim(replace(replace(substr(json_extract(first.submission,'$.userText'),1,160),char(13),' '),char(10),' ')))=0
                        THEN CASE WHEN json_array_length(json_extract(first.submission,'$.acceptedContent'))>0
                            THEN 'Attachments' ELSE 'Untitled conversation' END
                        ELSE substr(json_extract(first.submission,'$.userText'),1,160) END),
                bindings.project_id,COALESCE(titles.revision,0),
                turns.first_definition,turns.last_definition,turns.first_version,turns.last_version
            FROM turns JOIN workflow_runs latest ON latest.rowid=turns.last_row
                JOIN workflow_runs first ON first.rowid=turns.first_row
                JOIN workflow_conversations bindings ON bindings.conversation_id=turns.conversation_id
                LEFT JOIN workflow_conversation_titles titles ON titles.conversation_id=turns.conversation_id
            WHERE ($before IS NULL OR turns.last_row < $before)
                AND (turns.first_definition <> $chat OR turns.last_definition <> $chat)
                AND ($excluded IS NULL OR turns.first_definition <> $excluded OR turns.last_definition <> $excluded)
                {{(protectedIds is null ? "" : generalOnly ? "AND bindings.project_id IS NULL AND turns.conversation_id NOT IN (SELECT value FROM json_each($pins))" : "AND (bindings.project_id IS NOT NULL OR turns.conversation_id IN (SELECT value FROM json_each($pins)))")}}
            ORDER BY turns.last_row DESC LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$snapshot", bound);
        command.Parameters.AddWithValue("$chat", ConversationWorkflowContract.DefinitionId);
        command.Parameters.AddWithValue("$excluded", excludedDefinitionId is null ? DBNull.Value : excludedDefinitionId);
        command.Parameters.AddWithValue("$before", beforeRow is { } before ? before : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        if (conversationId is not null) command.Parameters.AddWithValue("$conversation", conversationId);
        if (protectedIds is not null) command.Parameters.AddWithValue("$pins", JsonSerializer.Serialize(protectedIds));
        using var reader = command.ExecuteReader();
        var rows = new List<OwnerWorkflowConversation>(limit);
        long last = 0;
        var more = false;
        while (reader.Read())
        {
            if (reader.GetString(9) != reader.GetString(10) || reader.GetString(11) != reader.GetString(12))
                throw new InvalidDataException("A conversation has mixed owner workflow definitions or editions.");
            if (rows.Count == limit) { more = true; break; }
            last = reader.GetInt64(0);
            var summary = new ConversationSummary(reader.GetString(1), reader.GetString(6).Replace('\r', ' ').Replace('\n', ' '),
                reader.GetString(2), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(3),
                Enum.Parse<WorkflowStatus>(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture))
                { TitleRevision = reader.GetInt64(8) };
            rows.Add(new(summary, reader.GetString(9), reader.GetString(11), summary.LatestRunId));
        }
        return new(rows.AsReadOnly(), bound, more ? last : null);
    }

    public bool HasConversationForProject(string definitionId, string projectId)
    {
        Identifier(projectId, nameof(projectId));
        return ReadConversations(definitionId, 1, projectId: projectId).Conversations.Count != 0;
    }

    private ConversationRecentPage ReadConversations(string definitionId, int limit,
        long? beforeRow = null, long? snapshotRow = null, string? conversationId = null, string? projectId = null,
        IReadOnlyCollection<string>? protectedIds = null, bool generalOnly = false)
    {
        Identifier(definitionId, nameof(definitionId));
        if (limit is < 1 or > 200 || beforeRow is < 1 || snapshotRow is < 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = journal.Open();
        using var snapshot = EventJournal.Command(connection, null, "SELECT COALESCE(MAX(rowid),0) FROM workflow_runs;");
        var current = (long)snapshot.ExecuteScalar()!;
        var bound = snapshotRow ?? current;
        if (bound > current) throw new ArgumentException("The requested conversation snapshot is unavailable.");
        // Filter discovery to root turns before MIN/MAX, so titles and latest-turn ordering use
        // the same owner rows. Exact reads intentionally retain child contexts for agent inspection.
        // The remaining JSON projections are small display fields; assignments/model state stay in SQL.
        using var command = EventJournal.Command(connection, null, $$"""
            WITH turns AS (
                SELECT conversation_id,MIN(rowid) AS first_row,MAX(rowid) AS last_row
                FROM workflow_runs WHERE rowid <= $snapshot AND json_extract(submission,'$.definitionId')=$definition
                    {{(conversationId is null ? "" : "AND conversation_id=$conversation")}}
                    {{(conversationId is null ? "AND json_extract(submission,'$.parentRunId') IS NULL" : "")}}
                GROUP BY conversation_id
            ), summaries AS (
                SELECT turns.last_row,latest.conversation_id,latest.profile_id,latest.id,latest.status,
                    json_extract(latest.payload,'$.updatedAt') AS updated,
                    COALESCE(titles.title,
                        CASE WHEN length(trim(replace(replace(substr(json_extract(first.submission,'$.userText'),1,160),char(13),' '),char(10),' ')))=0
                            THEN CASE WHEN json_array_length(json_extract(first.submission,'$.acceptedContent'))>0
                                THEN 'Attachments' ELSE 'Untitled conversation' END
                            ELSE substr(json_extract(first.submission,'$.userText'),1,160) END) AS title,
                    bindings.project_id AS project,
                    COALESCE(titles.revision,0) AS title_revision
                FROM turns JOIN workflow_runs latest ON latest.rowid=turns.last_row
                    JOIN workflow_runs first ON first.rowid=turns.first_row
                    JOIN workflow_conversations bindings ON bindings.conversation_id=latest.conversation_id
                    LEFT JOIN workflow_conversation_titles titles ON titles.conversation_id=latest.conversation_id
            )
            SELECT * FROM summaries
            WHERE ($before IS NULL OR last_row < $before) {{(projectId is null ? "" : "AND project=$project")}}
                {{(protectedIds is null ? "" : generalOnly ? "AND project IS NULL AND conversation_id NOT IN (SELECT value FROM json_each($pins))" : "AND (project IS NOT NULL OR conversation_id IN (SELECT value FROM json_each($pins)))")}}
            ORDER BY last_row DESC LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$definition", definitionId);
        command.Parameters.AddWithValue("$snapshot", bound);
        command.Parameters.AddWithValue("$before", beforeRow is { } before ? before : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        if (conversationId is not null) command.Parameters.AddWithValue("$conversation", conversationId);
        if (projectId is not null) command.Parameters.AddWithValue("$project", projectId);
        if (protectedIds is not null) command.Parameters.AddWithValue("$pins", JsonSerializer.Serialize(protectedIds));
        using var reader = command.ExecuteReader();
        var rows = new List<ConversationSummary>(limit); long last = 0; var more = false;
        while (reader.Read())
        {
            if (rows.Count == limit) { more = true; break; }
            last = reader.GetInt64(0);
            rows.Add(new(reader.GetString(1), reader.GetString(6).Replace('\r', ' ').Replace('\n', ' '),
                reader.GetString(2), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(3), Enum.Parse<WorkflowStatus>(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture))
                { TitleRevision = reader.GetInt64(8) });
        }
        return new(rows.AsReadOnly(), bound, more ? last : null);
    }
}
