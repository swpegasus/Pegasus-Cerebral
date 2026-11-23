using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    public ConversationCompactionReceipt RequestConversationCompaction(ConversationCompactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId));
        Identifier(request.DefinitionId, nameof(request.DefinitionId), maximum: 256);
        Identifier(request.ConversationId, nameof(request.ConversationId));
        if (request.ExpectedConversationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedConversationRevision));

        return journal.Transact((connection, transaction) =>
        {
            if (ReadCompactionRequest(connection, transaction, request.RequestId) is { } prior)
            {
                if (prior.ConversationId != request.ConversationId ||
                    prior.RequestedAtRevision != request.ExpectedConversationRevision)
                    throw Conflict("Request ID already belongs to a different conversation compaction request.");
                return prior;
            }

            using (var owner = EventJournal.Command(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM workflow_conversations WHERE conversation_id=$conversation);"))
            {
                owner.Parameters.AddWithValue("$conversation", request.ConversationId);
                if ((long)owner.ExecuteScalar()! == 0)
                    throw new KeyNotFoundException("Conversation was not found.");
            }
            using (var active = EventJournal.Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM workflow_runs WHERE conversation_id=$conversation
                    AND status NOT IN ('Completed','Failed','Cancelled'));
                """))
            {
                active.Parameters.AddWithValue("$conversation", request.ConversationId);
                if ((long)active.ExecuteScalar()! != 0)
                    throw Conflict("Finish, answer or stop the conversation's active work before requesting compaction.");
            }

            var revision = EventJournal.ReadRevision(connection, transaction, request.ConversationId);
            if (revision != request.ExpectedConversationRevision)
                throw new JournalRevisionException(request.ExpectedConversationRevision, revision);
            using (var pending = EventJournal.Command(connection, transaction, """
                SELECT request_id FROM conversation_compaction_requests
                WHERE conversation_id=$conversation AND consumed_by_run_id IS NULL;
                """))
            {
                pending.Parameters.AddWithValue("$conversation", request.ConversationId);
                if (pending.ExecuteScalar() is string)
                    throw Conflict("This conversation already has a pending one-time compaction request.");
            }

            var at = DateTimeOffset.UtcNow;
            using (var insert = EventJournal.Command(connection, transaction, """
                INSERT INTO conversation_compaction_requests
                    (request_id,conversation_id,definition_id,expected_revision,requested_at)
                VALUES($id,$conversation,$definition,$revision,$at);
                """))
            {
                insert.Parameters.AddWithValue("$id", request.RequestId);
                insert.Parameters.AddWithValue("$conversation", request.ConversationId);
                insert.Parameters.AddWithValue("$definition", request.DefinitionId);
                insert.Parameters.AddWithValue("$revision", request.ExpectedConversationRevision);
                insert.Parameters.AddWithValue("$at", at.ToString("O"));
                insert.ExecuteNonQuery();
            }

            var item = new CerebralEvent(request.RequestId, request.ConversationId, request.RequestId, 0,
                CerebralEventTypes.ContextCompactionRequested, new(EventActorKind.User, "owner", "You"), [],
                new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.User, "owner"), at,
                Element(new { request.RequestId, request.ExpectedConversationRevision, oneTime = true }));
            EventJournal.AppendInTransaction(connection, transaction, item, revision);
            return new(request.RequestId, request.ConversationId, request.ExpectedConversationRevision, at);
        });
    }

    public ConversationCompactionReceipt? GetPendingConversationCompaction(string conversationId)
    {
        Identifier(conversationId, nameof(conversationId));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT request_id,conversation_id,expected_revision,requested_at,consumed_by_run_id
            FROM conversation_compaction_requests
            WHERE conversation_id=$conversation AND consumed_by_run_id IS NULL;
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCompactionReceipt(reader) : null;
    }

    private static ConversationCompactionReceipt? ReadCompactionRequest(SqliteConnection connection,
        SqliteTransaction transaction, string requestId)
    {
        using var command = EventJournal.Command(connection, transaction, """
            SELECT request_id,conversation_id,expected_revision,requested_at,consumed_by_run_id
            FROM conversation_compaction_requests WHERE request_id=$id;
            """);
        command.Parameters.AddWithValue("$id", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCompactionReceipt(reader) : null;
    }

    private static ConversationCompactionReceipt ReadCompactionReceipt(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
        DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(4) ? null : reader.GetString(4));

    private static void ConsumeConversationCompaction(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowSubmission submission, DateTimeOffset consumedAt)
    {
        if (submission.ConversationCompactionRequestId is not { } requestId) return;
        using var update = EventJournal.Command(connection, transaction, """
            UPDATE conversation_compaction_requests
            SET consumed_by_run_id=$run,consumed_at=$at
            WHERE request_id=$id AND conversation_id=$conversation AND definition_id=$definition
                AND consumed_by_run_id IS NULL;
            """);
        update.Parameters.AddWithValue("$run", submission.RequestId);
        update.Parameters.AddWithValue("$at", consumedAt.ToString("O"));
        update.Parameters.AddWithValue("$id", requestId);
        update.Parameters.AddWithValue("$conversation", submission.ConversationId);
        update.Parameters.AddWithValue("$definition", submission.DefinitionId);
        if (update.ExecuteNonQuery() != 1)
            throw Conflict("The pending conversation compaction request changed before this turn was accepted.");
    }
}
