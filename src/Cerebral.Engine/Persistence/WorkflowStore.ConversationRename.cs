using System.Text;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    internal static void InitializeConversationRenames(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = EventJournal.Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS workflow_conversation_titles (
                conversation_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision > 0));
            CREATE TABLE IF NOT EXISTS workflow_conversation_renames (
                request_id TEXT PRIMARY KEY,
                request TEXT NOT NULL,
                response TEXT NOT NULL);
            """);
        command.ExecuteNonQuery();
    }

    /// <summary>Updates only the human title. The caller owns installation-owner authorization.
    /// A retry returns its original receipt even after later renames, without reverting the latest title.</summary>
    public ConversationRenameResult RenameConversation(ConversationRenameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId));
        Identifier(request.DefinitionId, nameof(request.DefinitionId), maximum: 256);
        Identifier(request.ConversationId, nameof(request.ConversationId));
        if (request.ExpectedTitleRevision < 0) throw new ArgumentOutOfRangeException(nameof(request.ExpectedTitleRevision));
        ValidateConversationTitle(request.Title);
        return journal.Transact((connection, transaction) =>
        {
            using (var receipt = EventJournal.Command(connection, transaction,
                "SELECT request,response FROM workflow_conversation_renames WHERE request_id=$id;"))
            {
                receipt.Parameters.AddWithValue("$id", request.RequestId);
                using var reader = receipt.ExecuteReader();
                if (reader.Read())
                {
                    var storedRequest = reader.GetString(0);
                    if (ReadConversationRenameOrigin(storedRequest).Tagged ||
                        !Equivalent(Deserialize<ConversationRenameRequest>(storedRequest), request))
                        throw Conflict("Rename request ID already belongs to different input.");
                    return Deserialize<ConversationRenameResult>(reader.GetString(1));
                }
            }
            using (var existing = EventJournal.Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM workflow_runs
                    WHERE conversation_id=$conversation AND json_extract(submission,'$.definitionId')=$definition);
                """))
            {
                existing.Parameters.AddWithValue("$conversation", request.ConversationId);
                existing.Parameters.AddWithValue("$definition", request.DefinitionId);
                if ((long)existing.ExecuteScalar()! == 0)
                    throw new KeyNotFoundException("Conversation was not found for this workflow definition.");
            }
            long revision;
            using (var current = EventJournal.Command(connection, transaction,
                "SELECT revision FROM workflow_conversation_titles WHERE conversation_id=$conversation;"))
            {
                current.Parameters.AddWithValue("$conversation", request.ConversationId);
                revision = current.ExecuteScalar() is long value ? value : 0;
            }
            if (revision != request.ExpectedTitleRevision)
                throw Conflict("Conversation title changed. Refresh its title revision before renaming.");
            var result = WriteConversationTitle(connection, transaction, request, revision);
            using (var receipt = EventJournal.Command(connection, transaction,
                "INSERT INTO workflow_conversation_renames(request_id,request,response) VALUES($id,$request,$response);"))
            {
                receipt.Parameters.AddWithValue("$id", request.RequestId);
                receipt.Parameters.AddWithValue("$request", Serialize(request));
                receipt.Parameters.AddWithValue("$response", Serialize(result));
                receipt.ExecuteNonQuery();
            }
            return result;
        });
    }

    private static ConversationRenameResult WriteConversationTitle(SqliteConnection connection, SqliteTransaction transaction,
        ConversationRenameRequest request, long revision)
    {
        var result = new ConversationRenameResult(request.ConversationId, request.Title, checked(revision + 1));
        using (var update = EventJournal.Command(connection, transaction, """
            INSERT INTO workflow_conversation_titles(conversation_id,title,revision)
            VALUES($conversation,$title,$revision)
            ON CONFLICT(conversation_id) DO UPDATE SET title=excluded.title,revision=excluded.revision;
            """))
        {
            update.Parameters.AddWithValue("$conversation", result.ConversationId);
            update.Parameters.AddWithValue("$title", result.Title);
            update.Parameters.AddWithValue("$revision", result.TitleRevision);
            update.ExecuteNonQuery();
        }
        return result;
    }

    private static void ValidateConversationTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 2 * ConversationRenameContract.MaximumTitleScalars ||
            title.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new ArgumentException("Conversation title must be nonblank single-line text within 160 Unicode characters.");
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(title) > ConversationRenameContract.MaximumTitleUtf8Bytes)
                throw new ArgumentException("Conversation title exceeds its UTF-8 size bound.");
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("Conversation title must contain valid Unicode text.", nameof(title), error);
        }
        var count = 0;
        foreach (var _ in title.EnumerateRunes())
            if (++count > ConversationRenameContract.MaximumTitleScalars)
                throw new ArgumentException("Conversation title exceeds 160 Unicode characters.");
    }
}
