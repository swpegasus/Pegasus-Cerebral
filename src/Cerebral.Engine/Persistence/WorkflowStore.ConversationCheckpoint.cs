using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed record ConversationContextCheckpoint(long SourceRevision, ContextCheckpoint Checkpoint);

public sealed partial class WorkflowStore
{
    public ConversationContextCheckpoint? GetConversationCheckpoint(string conversationId, long throughRevision)
    {
        Identifier(conversationId, nameof(conversationId));
        if (throughRevision < 0) throw new ArgumentOutOfRangeException(nameof(throughRevision));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT source_revision,checkpoint FROM conversation_context_checkpoints
            WHERE conversation_id=$conversation AND source_revision <= $revision;
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$revision", throughRevision);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), Deserialize<ContextCheckpoint>(reader.GetString(1))) : null;
    }

    /// <summary>Legacy-only exact read. Coverage is not an append sequence. A real adoption event,
    /// including one outside the accepted snapshot or in another conversation, forbids this fallback.</summary>
    public ConversationContextCheckpoint? GetLegacyCheckpointSource(string conversationId, string checkpointId,
        long beforeRevision)
    {
        Identifier(conversationId, nameof(conversationId));
        Identifier(checkpointId, nameof(checkpointId));
        if (beforeRevision < 0) throw new ArgumentOutOfRangeException(nameof(beforeRevision));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT source_revision,CASE WHEN length(CAST(checkpoint AS BLOB)) <= $maximum THEN checkpoint END
            FROM conversation_context_checkpoints
            WHERE conversation_id=$conversation AND effect_id=$id AND source_revision < $revision
                AND NOT EXISTS (SELECT 1 FROM conversation_events WHERE event_id=$id);
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$id", checkpointId);
        command.Parameters.AddWithValue("$revision", beforeRevision);
        command.Parameters.AddWithValue("$maximum", ContextValidation.MaximumCheckpointBytes);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.IsDBNull(1)) throw new InvalidDataException("Retained legacy checkpoint exceeds the supported size.");
        var checkpoint = Deserialize<ContextCheckpoint>(reader.GetString(1));
        ContextValidation.ValidateWindow(new(checkpoint.ContextRevision, checkpoint.ExclusionRevision, [], checkpoint));
        if (checkpoint.Id != checkpointId || checkpoint.ContextRevision > beforeRevision || checkpoint.SourceLocators.Count != 0) return null;
        return new(reader.GetInt64(0), checkpoint);
    }

    private static void CommitConversationCheckpoint(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun run, WorkflowEffect effect, WorkflowCommandResult result)
    {
        if (result.ConversationCheckpoint is not { } checkpoint) return;
        if (result.State != WorkflowEffectState.Succeeded)
            throw new ArgumentException("Only a successful confirmed effect can adopt a conversation checkpoint.", nameof(result));
        ContextValidation.ValidateWindow(new(checkpoint.ContextRevision, checkpoint.ExclusionRevision, [], checkpoint));
        if (!checkpoint.BeforeCurrentInput || checkpoint.Id != effect.Id || checkpoint.CompactionEffectId != effect.Id)
            throw new ArgumentException("Conversation checkpoint identity or placement is invalid.", nameof(result));

        EventJournal.RequireCheckpointSourceMapFormat(connection, transaction);
        var payload = Serialize(checkpoint);
        var sourceRevision = run.Submission.ConversationSequence;
        using (var current = EventJournal.Command(connection, transaction, """
            SELECT source_revision,checkpoint,run_id FROM conversation_context_checkpoints WHERE conversation_id=$conversation;
            """))
        {
            current.Parameters.AddWithValue("$conversation", run.Submission.ConversationId);
            using var reader = current.ExecuteReader();
            if (reader.Read())
            {
                var revision = reader.GetInt64(0);
                if (revision > sourceRevision)
                    throw Conflict("A newer conversation checkpoint is already retained.");
                if (revision == sourceRevision)
                {
                    if (reader.GetString(1) == payload) return;
                    var previous = Deserialize<ContextCheckpoint>(reader.GetString(1));
                    // Admission fixes the conversation boundary for the entire run. Later compactions
                    // may refine that baseline only through its exact retained checkpoint chain.
                    if (reader.GetString(2) != run.Id || checkpoint.ContextRevision <= previous.ContextRevision ||
                        checkpoint.ExclusionRevision != previous.ExclusionRevision ||
                        !checkpoint.CoveredSources.Contains(new(previous.Id, previous.ContextRevision)))
                        throw Conflict("A checkpoint refinement must belong to the same run, advance its context revision, and cover the exact retained checkpoint without changing exclusions.");
                }
            }
        }

        var at = DateTimeOffset.UtcNow;
        // Commit an exact later-turn read coordinate with the baseline and effect result. The current
        // journal head is independent of the admission-fixed coverage cutoff. Duplicate completion is
        // handled before this method; an ID collision rolls back the entire transaction.
        var receipt = new CerebralEvent(checkpoint.Id, run.Submission.ConversationId, run.Id, 0,
            "context.compaction", new(EventActorKind.Service, "cerebral.engine"), [],
            new(ModelInclusion.OnDemand, HumanVisibility.Activity), new(EventSourceKind.Service, "cerebral.engine"),
            at, Element(new { kind = "conversation-checkpoint", coverageThroughSequence = sourceRevision, checkpoint }));
        EventJournal.AppendInTransaction(connection, transaction, receipt,
            EventJournal.ReadRevision(connection, transaction, receipt.ConversationId));

        using var save = EventJournal.Command(connection, transaction, """
            INSERT INTO conversation_context_checkpoints
                (conversation_id,source_revision,run_id,effect_id,checkpoint,adopted_at)
            VALUES($conversation,$revision,$run,$effect,$checkpoint,$at)
            ON CONFLICT(conversation_id) DO UPDATE SET
                source_revision=excluded.source_revision,run_id=excluded.run_id,effect_id=excluded.effect_id,
                checkpoint=excluded.checkpoint,adopted_at=excluded.adopted_at;
            """);
        save.Parameters.AddWithValue("$conversation", run.Submission.ConversationId);
        save.Parameters.AddWithValue("$revision", sourceRevision);
        save.Parameters.AddWithValue("$run", run.Id);
        save.Parameters.AddWithValue("$effect", effect.Id);
        save.Parameters.AddWithValue("$checkpoint", payload);
        save.Parameters.AddWithValue("$at", at.ToString("O"));
        save.ExecuteNonQuery();
    }
}
