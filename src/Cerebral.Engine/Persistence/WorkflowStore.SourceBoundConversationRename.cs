using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    private const string SourceBoundRenameOrigin = "cerebral.source-bound-conversation-rename/1";
    private sealed record SourceBoundRenameStoredRequest(string Origin, SourceBoundConversationRenameGuard Guard);

    /// <summary>Reads the exact first root User input in one snapshot; never synthesizes a later source.</summary>
    public CommittedRootUserInputReceipt? ReadFirstCommittedRootUserInput(string conversationId)
    {
        Identifier(conversationId, nameof(conversationId));
        using var connection = journal.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        string? eventId;
        using (var first = EventJournal.Command(connection, transaction, """
            SELECT e.event_id FROM conversation_events e
            LEFT JOIN workflow_runs r ON r.id=json_extract(e.payload,'$.turnId')
                AND r.conversation_id=e.conversation_id
            WHERE e.conversation_id=$conversation
                AND json_extract(e.payload,'$.type')=$message
                AND json_extract(e.payload,'$.actor.kind')='User'
                AND (r.id IS NULL OR json_extract(r.submission,'$.parentRunId') IS NULL)
            ORDER BY e.sequence LIMIT 1;
            """))
        {
            first.Parameters.AddWithValue("$conversation", conversationId);
            first.Parameters.AddWithValue("$message", CerebralEventTypes.Message);
            eventId = first.ExecuteScalar() as string;
        }
        var source = eventId is null ? null : ReadVerifiedFirstRootUserInput(connection, transaction, conversationId, eventId);
        transaction.Commit();
        return source;
    }

    /// <summary>Applies one exact source guard and title CAS, or records a known immutable skip.</summary>
    public SourceBoundConditionalRenameResult RenameConversationFromSource(SourceBoundConversationRenameGuard guard)
    {
        ValidateSourceBoundRenameGuard(guard);
        return journal.Transact((connection, transaction) =>
        {
            var known = ReadGuardedRenameReceipt(connection, transaction, guard, out var found);
            if (found) return known ?? new(SourceBoundConditionalRenameDisposition.Skipped, SkipCode: "rename-request-conflict");
            SourceBoundConditionalRenameResult Skip(string code) =>
                StoreGuardedRenameReceipt(connection, transaction, guard,
                    new(SourceBoundConditionalRenameDisposition.Skipped, SkipCode: code));

            if (guard.Rename.ConversationId != guard.SourceConversationId ||
                guard.Rename.DefinitionId != guard.SourceDefinitionId ||
                guard.Rename.ExpectedTitleRevision != guard.CapturedTitleRevision)
                return Skip("source-mismatch");
            if (guard.CapturedTitleRevision != 0) return Skip("title-not-default");
            var work = Read(connection, transaction, guard.WorkRequestId);
            if (work is null) return Skip("work-missing");
            if (work.Id != guard.WorkRequestId || work.Submission.RequestId != guard.WorkRequestId ||
                work.Submission.ConversationId != guard.WorkConversationId ||
                work.Submission.ParentRunId is not null || work.Submission.Caller?.Kind != EventActorKind.Service ||
                work.Submission.ProjectId != guard.SourceProjectId)
                return Skip("work-mismatch");
            var source = ReadVerifiedFirstRootUserInput(connection, transaction, guard.SourceConversationId, guard.SourceEventId);
            if (source is null) return Skip("source-mismatch");
            if (source.RequestId != guard.SourceRequestId || source.ConversationId != guard.SourceConversationId ||
                source.EventId != guard.SourceEventId || source.EventTurnId != guard.SourceRequestId ||
                source.EventSequence != guard.SourceEventSequence || source.DefinitionId != guard.SourceDefinitionId ||
                source.DefinitionVersion != guard.SourceDefinitionVersion ||
                !string.Equals(HashSourceInput(source.UserText), guard.SourceInputSha256, StringComparison.Ordinal))
                return Skip("source-mismatch");
            if (source.ProjectId != guard.SourceProjectId) return Skip("source-project-changed");

            // This is the exact default-title projection used by GetConversation for this
            // definition/CID, including prior Service/child history and CR/LF display folding.
            using var current = EventJournal.Command(connection, transaction, """
                SELECT bindings.profile_id,bindings.project_id,COALESCE(titles.revision,0),
                    COALESCE(titles.title,
                        CASE WHEN length(trim(replace(replace(substr(json_extract(first.submission,'$.userText'),1,160),char(13),' '),char(10),' ')))=0
                            THEN CASE WHEN json_array_length(json_extract(first.submission,'$.acceptedContent'))>0
                                THEN 'Attachments' ELSE 'Untitled conversation' END
                            ELSE substr(json_extract(first.submission,'$.userText'),1,160) END)
                FROM workflow_conversations bindings
                JOIN workflow_runs first ON first.rowid=(
                    SELECT MIN(rowid) FROM workflow_runs WHERE conversation_id=$conversation
                        AND json_extract(submission,'$.definitionId')=$definition)
                LEFT JOIN workflow_conversation_titles titles ON titles.conversation_id=bindings.conversation_id
                WHERE bindings.conversation_id=$conversation;
                """);
            current.Parameters.AddWithValue("$conversation", guard.SourceConversationId);
            current.Parameters.AddWithValue("$definition", guard.SourceDefinitionId);
            string profile = "";
            string? project = null;
            string title = "";
            long revision = 0;
            bool exists;
            using (var reader = current.ExecuteReader())
            {
                exists = reader.Read();
                if (exists)
                {
                    profile = reader.GetString(0);
                    project = reader.IsDBNull(1) ? null : reader.GetString(1);
                    revision = reader.GetInt64(2);
                    title = reader.GetString(3).Replace('\r', ' ').Replace('\n', ' ');
                }
            }
            if (!exists) return Skip("source-missing");
            if (profile != source.ProfileId) return Skip("source-mismatch");
            if (project != guard.SourceProjectId) return Skip("source-project-changed");
            if (revision != guard.CapturedTitleRevision || !string.Equals(title, guard.CapturedSourceTitle, StringComparison.Ordinal))
                return Skip("title-changed");

            var renamed = WriteConversationTitle(connection, transaction, guard.Rename, revision);
            return StoreGuardedRenameReceipt(connection, transaction, guard,
                new(SourceBoundConditionalRenameDisposition.Applied, renamed));
        });
    }

    /// <summary>Exact receipt-only reconciliation. Absence/conflict is unknown; no title/source recheck or mutation.</summary>
    public SourceBoundConditionalRenameResult? ReadSourceBoundConversationRenameReceipt(SourceBoundConversationRenameGuard guard)
    {
        ValidateSourceBoundRenameGuard(guard);
        using var connection = journal.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var known = ReadGuardedRenameReceipt(connection, transaction, guard, out _);
        transaction.Commit();
        return known;
    }

    private static CommittedRootUserInputReceipt? ReadVerifiedFirstRootUserInput(SqliteConnection connection,
        SqliteTransaction transaction, string conversationId, string eventId)
    {
        string? payload;
        long sequence;
        using (var lookup = EventJournal.Command(connection, transaction,
            "SELECT payload,sequence FROM conversation_events WHERE conversation_id=$conversation AND event_id=$event;"))
        {
            lookup.Parameters.AddWithValue("$conversation", conversationId);
            lookup.Parameters.AddWithValue("$event", eventId);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read()) return null;
            payload = reader.GetString(0);
            sequence = reader.GetInt64(1);
        }
        if (Encoding.UTF8.GetByteCount(payload) > EventJournal.MaximumEventBytes)
            throw new InvalidDataException("Stored source event exceeds the canonical bound.");
        var item = JsonSerializer.Deserialize<CerebralEvent>(payload, Json)
            ?? throw new InvalidDataException("Stored source event is empty.");
        item.Validate();
        if (item.Id != eventId || item.ConversationId != conversationId || item.Sequence != sequence)
            throw new InvalidDataException("Stored source event coordinates conflict with their journal row.");
        if (item.Type != CerebralEventTypes.Message || item.Actor.Kind != EventActorKind.User ||
            item.Provenance.Source != EventSourceKind.User || item.ParentEventId is not null) return null;
        var run = Read(connection, transaction, item.TurnId);
        if (run is null) return null;
        var accepted = run.Submission;
        if (run.Id != item.TurnId || accepted.RequestId != run.Id || accepted.ConversationId != conversationId ||
            accepted.ParentRunId is not null || accepted.Caller is { Kind: not EventActorKind.User } ||
            item.Provenance.JobId != run.Id || item.CorrelationId != run.Id ||
            accepted.ConversationSequence < 0 || accepted.ConversationSequence == long.MaxValue ||
            item.Sequence != accepted.ConversationSequence + 1 ||
            item.Content.Count == 0 || item.Content[0] is not TextContentPart text ||
            !string.Equals(text.Text, accepted.UserText, StringComparison.Ordinal)) return null;
        // Root844's conversation-wide predicate counts unbound User history and excludes
        // only positively joined same-conversation children. Coherence is not firstness.
        if (HasEarlierRootUserMessage(connection, transaction, conversationId, item.Sequence - 1)) return null;
        return new(accepted.RequestId, conversationId, accepted.ProfileId, accepted.DefinitionId,
            accepted.DefinitionVersion, accepted.ProjectId, accepted.ConversationSequence, item.Id, item.TurnId,
            item.Sequence, item.Type, item.Actor.Kind, item.Provenance.Source, accepted.ParentRunId, accepted.UserText);
    }

    private static string HashSourceInput(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void ValidateSourceBoundRenameGuard(SourceBoundConversationRenameGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(guard.Rename);
        Identifier(guard.Rename.RequestId, nameof(guard.Rename.RequestId));
        Identifier(guard.Rename.DefinitionId, nameof(guard.Rename.DefinitionId), maximum: 256);
        Identifier(guard.Rename.ConversationId, nameof(guard.Rename.ConversationId));
        Identifier(guard.WorkRequestId, nameof(guard.WorkRequestId));
        Identifier(guard.WorkConversationId, nameof(guard.WorkConversationId));
        Identifier(guard.SourceConversationId, nameof(guard.SourceConversationId));
        Identifier(guard.SourceRequestId, nameof(guard.SourceRequestId));
        Identifier(guard.SourceEventId, nameof(guard.SourceEventId));
        Identifier(guard.SourceDefinitionId, nameof(guard.SourceDefinitionId), maximum: 256);
        Identifier(guard.SourceDefinitionVersion, nameof(guard.SourceDefinitionVersion));
        if (guard.SourceProjectId is not null) Identifier(guard.SourceProjectId, nameof(guard.SourceProjectId));
        ArgumentNullException.ThrowIfNull(guard.CapturedSourceTitle);
        Text(guard.CapturedSourceTitle, nameof(guard.CapturedSourceTitle), 2 * ConversationRenameContract.MaximumTitleScalars);
        if (guard.SourceEventSequence < 1 || guard.CapturedTitleRevision < 0 || guard.Rename.ExpectedTitleRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(guard));
        if (guard.SourceInputSha256 is not { Length: 64 } ||
            guard.SourceInputSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Source input hash must be lowercase SHA-256.", nameof(guard));
        ValidateConversationTitle(guard.Rename.Title);
    }

    private static (bool Tagged, string? Origin) ReadConversationRenameOrigin(string request)
    {
        if (Encoding.UTF8.GetByteCount(request) > MaximumRunBytes)
            throw new InvalidDataException("Stored rename request exceeds the existing storage bound.");
        using var document = JsonDocument.Parse(request);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Stored rename request is not an object.");
        if (!document.RootElement.TryGetProperty("origin", out var origin)) return (false, null);
        if (origin.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Stored rename origin is not a string.");
        return (true, origin.GetString());
    }

    private static SourceBoundConditionalRenameResult? ReadGuardedRenameReceipt(SqliteConnection connection,
        SqliteTransaction transaction, SourceBoundConversationRenameGuard guard, out bool found)
    {
        using var receipt = EventJournal.Command(connection, transaction,
            "SELECT request,response FROM workflow_conversation_renames WHERE request_id=$id;");
        receipt.Parameters.AddWithValue("$id", guard.Rename.RequestId);
        using var reader = receipt.ExecuteReader();
        found = reader.Read();
        if (!found) return null;
        var request = reader.GetString(0);
        var origin = ReadConversationRenameOrigin(request);
        if (!origin.Tagged || origin.Origin != SourceBoundRenameOrigin) return null;
        var stored = Deserialize<SourceBoundRenameStoredRequest>(request);
        if (stored.Guard is null) throw new InvalidDataException("Stored guarded rename has no guard.");
        if (!Equivalent(stored.Guard, guard)) return null;
        var result = Deserialize<SourceBoundConditionalRenameResult>(reader.GetString(1));
        if (result.Disposition == SourceBoundConditionalRenameDisposition.Applied && result.SkipCode is null &&
            result.Receipt is { } renamed && renamed.ConversationId == guard.Rename.ConversationId &&
            renamed.Title == guard.Rename.Title && guard.Rename.ExpectedTitleRevision < long.MaxValue &&
            renamed.TitleRevision == guard.Rename.ExpectedTitleRevision + 1)
            return result with { Disposition = SourceBoundConditionalRenameDisposition.AlreadyApplied };
        if (result.Disposition == SourceBoundConditionalRenameDisposition.Skipped && result.Receipt is null &&
            result.SkipCode is "source-mismatch" or "work-missing" or "work-mismatch" or "source-project-changed" or
                "title-not-default" or "source-missing" or "title-changed") return result;
        throw new InvalidDataException("Stored guarded rename result is inconsistent.");
    }

    private static SourceBoundConditionalRenameResult StoreGuardedRenameReceipt(SqliteConnection connection,
        SqliteTransaction transaction, SourceBoundConversationRenameGuard guard, SourceBoundConditionalRenameResult result)
    {
        using var receipt = EventJournal.Command(connection, transaction,
            "INSERT INTO workflow_conversation_renames(request_id,request,response) VALUES($id,$request,$response);");
        receipt.Parameters.AddWithValue("$id", guard.Rename.RequestId);
        receipt.Parameters.AddWithValue("$request", Serialize(new SourceBoundRenameStoredRequest(SourceBoundRenameOrigin, guard)));
        receipt.Parameters.AddWithValue("$response", Serialize(result));
        receipt.ExecuteNonQuery();
        return result;
    }
}
