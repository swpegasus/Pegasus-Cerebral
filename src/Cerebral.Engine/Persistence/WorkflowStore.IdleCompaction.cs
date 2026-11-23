using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

/// <summary>A host-only prepared candidate. Until Submit adopts it, it has no durable authority.</summary>
public sealed record ConversationIdleCheckpoint(string CompletedRunId, long CompletedRunRevision,
    long HeadRevision, long SourceRevision, string SettingsFingerprint, ContextWindow Window,
    ContextCheckpoint Checkpoint, ConversationContextCheckpoint? Baseline, JsonElement Evidence);

public sealed partial class WorkflowStore
{
    /// <summary>One exact saved host-generation envelope, not a scan or replay of the conversation.</summary>
    public JsonElement? ReadLastConversationGeneration(string runId, long throughRevision)
    {
        Identifier(runId, nameof(runId));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT json_extract(e.payload,'$.data.arguments')
            FROM conversation_events e JOIN workflow_runs r ON r.id=$run
            WHERE e.conversation_id=r.conversation_id AND e.sequence<= $revision
                AND e.sequence>json_extract(r.submission,'$.conversationSequence')
                AND json_extract(e.payload,'$.turnId')=$run
                AND json_extract(e.payload,'$.type')=$type
                AND json_extract(e.payload,'$.provenance.sourceId')='cerebral.engine'
                AND json_extract(e.payload,'$.data.toolName')='cerebral.host:generate'
            ORDER BY e.sequence DESC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$revision", throughRevision);
        command.Parameters.AddWithValue("$type", CerebralEventTypes.ToolCall);
        if (command.ExecuteScalar() is not string value) return null;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static bool TryAdoptIdleCheckpoint(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowSubmission submission, ConversationIdleCheckpoint candidate, string settingsFingerprint)
    {
        // Every failure is a cache miss: the authoritative existing context still admits normally.
        if (submission.ConversationCompactionRequestId is not null ||
            candidate.SettingsFingerprint != settingsFingerprint ||
            EventJournal.ReadRevision(connection, transaction, submission.ConversationId) != candidate.HeadRevision)
            return false;
        var completed = Read(connection, transaction, candidate.CompletedRunId);
        if (completed is null || completed.Status != WorkflowStatus.Completed || completed.Questions is not null ||
            completed.Revision != candidate.CompletedRunRevision || completed.Submission.ConversationId != submission.ConversationId ||
            completed.Submission.ProfileId != submission.ProfileId || completed.Submission.DefinitionId != submission.DefinitionId ||
            completed.Submission.ParentRunId is not null || candidate.SourceRevision != completed.Submission.ConversationSequence)
            return false;
        using (var latest = EventJournal.Command(connection, transaction,
            "SELECT id FROM workflow_runs WHERE conversation_id=$conversation ORDER BY rowid DESC LIMIT 1;"))
        {
            latest.Parameters.AddWithValue("$conversation", submission.ConversationId);
            if (latest.ExecuteScalar() as string != completed.Id) return false;
        }
        using (var baseline = EventJournal.Command(connection, transaction, """
            SELECT source_revision,checkpoint FROM conversation_context_checkpoints WHERE conversation_id=$conversation;
            """))
        {
            baseline.Parameters.AddWithValue("$conversation", submission.ConversationId);
            using var reader = baseline.ExecuteReader();
            if (reader.Read())
            {
                if (candidate.Baseline is null || reader.GetInt64(0) != candidate.Baseline.SourceRevision ||
                    reader.GetString(1) != Serialize(candidate.Baseline.Checkpoint)) return false;
            }
            else if (candidate.Baseline is not null) return false;
        }

        try { ContextValidation.ValidateCandidate(candidate.Window, candidate.Checkpoint, candidate.Checkpoint.CompactionEffectId); }
        catch (ArgumentException) { return false; }
        if (!candidate.Checkpoint.BeforeCurrentInput || candidate.Checkpoint.ContextRevision != candidate.HeadRevision ||
            candidate.Checkpoint.ExclusionRevision != (candidate.Baseline?.Checkpoint.ExclusionRevision ?? 0))
            return false;
        foreach (var source in candidate.Checkpoint.CoveredSources)
        {
            if (candidate.Baseline is { } prior && source == new ContextSourceReference(prior.Checkpoint.Id, prior.Checkpoint.ContextRevision))
                continue;
            using var exact = EventJournal.Command(connection, transaction, """
                SELECT sequence,json_extract(payload,'$.projection.model')
                FROM conversation_events WHERE conversation_id=$conversation AND event_id=$id;
                """);
            exact.Parameters.AddWithValue("$conversation", submission.ConversationId);
            exact.Parameters.AddWithValue("$id", source.Id);
            using var reader = exact.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != source.Revision || source.Revision > candidate.SourceRevision ||
                reader.GetString(1) is "Never" or "OnDemand") return false;
        }

        EventJournal.RequireCheckpointSourceMapFormat(connection, transaction);
        var at = DateTimeOffset.UtcNow;
        // The adoption receipt supplies an exact-read handle for the prepared summary/manifest/usage.
        // It is committed with the next accepted input, without rewriting the completed run.
        var receipt = new CerebralEvent(candidate.Checkpoint.Id, submission.ConversationId, submission.RequestId, 0,
            "context.idle-compaction", new(EventActorKind.Service, "cerebral.engine"), [],
            new(ModelInclusion.OnDemand, HumanVisibility.Activity), new(EventSourceKind.Service, "cerebral.engine"),
            at, candidate.Evidence.Clone());
        EventJournal.AppendInTransaction(connection, transaction, receipt, candidate.HeadRevision);
        using var save = EventJournal.Command(connection, transaction, """
            INSERT INTO conversation_context_checkpoints
                (conversation_id,source_revision,run_id,effect_id,checkpoint,adopted_at)
            VALUES($conversation,$revision,$run,$effect,$checkpoint,$at)
            ON CONFLICT(conversation_id) DO UPDATE SET
                source_revision=excluded.source_revision,run_id=excluded.run_id,effect_id=excluded.effect_id,
                checkpoint=excluded.checkpoint,adopted_at=excluded.adopted_at;
            """);
        save.Parameters.AddWithValue("$conversation", submission.ConversationId);
        save.Parameters.AddWithValue("$revision", candidate.SourceRevision);
        save.Parameters.AddWithValue("$run", submission.RequestId);
        save.Parameters.AddWithValue("$effect", candidate.Checkpoint.CompactionEffectId);
        save.Parameters.AddWithValue("$checkpoint", Serialize(candidate.Checkpoint));
        save.Parameters.AddWithValue("$at", at.ToString("O"));
        save.ExecuteNonQuery();
        return true;
    }
}
