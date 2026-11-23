using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed partial class CollaborationStore
{
    // Ownership is separate from revocable permission to contact an independent chat.
    // This host-only binding never writes a communication grant or adopts an existing context.
    internal static void InitializeOwnedCoordinators(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = EventJournal.Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS conversation_owned_coordinators (
                main_conversation_id TEXT PRIMARY KEY NOT NULL,
                coordinator_conversation_id TEXT NOT NULL UNIQUE,
                profile_id TEXT NOT NULL,
                project_id TEXT NULL,
                created_by_run_id TEXT NOT NULL,
                CHECK(main_conversation_id <> coordinator_conversation_id));
            """);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Register only the host's fresh coordinator reservation for a live owner Main run.
    /// The host must first authenticate the exact source tool effect and reserve the context.
    /// There is deliberately no pipe operation that accepts this ownership declaration.
    /// </summary>
    public void RegisterOwnedCoordinator(string sourceRunId, string targetConversationId, string targetProfileId)
    {
        Identifier(sourceRunId, nameof(sourceRunId));
        Identifier(targetConversationId, nameof(targetConversationId));
        Identifier(targetProfileId, nameof(targetProfileId));
        journal.Transact((connection, transaction) =>
        {
            var source = ReadOwnedCoordinatorSource(connection, transaction, sourceRunId) ??
                throw new UnauthorizedAccessException("The owned coordinator source run is unavailable.");
            var submission = source.Submission;
            if (!IsOwnerMain(source) || source.IsTerminal || source.CancelRequested ||
                submission.ConversationId == targetConversationId || submission.ProfileId != targetProfileId ||
                IsOwnedCoordinatorConversation(connection, transaction, submission.ConversationId))
                throw new UnauthorizedAccessException("A fresh coordinator requires a live owner Main with the same profile and project.");

            using (var prior = EventJournal.Command(connection, transaction, """
                SELECT main_conversation_id,coordinator_conversation_id,profile_id,project_id
                FROM conversation_owned_coordinators
                WHERE main_conversation_id=$main OR coordinator_conversation_id=$target;
                """))
            {
                prior.Parameters.AddWithValue("$main", submission.ConversationId);
                prior.Parameters.AddWithValue("$target", targetConversationId);
                using var reader = prior.ExecuteReader();
                if (reader.Read())
                {
                    var matches = reader.GetString(0) == submission.ConversationId &&
                        reader.GetString(1) == targetConversationId && reader.GetString(2) == targetProfileId &&
                        (reader.IsDBNull(3) ? null : reader.GetString(3)) == submission.ProjectId;
                    if (!matches || reader.Read())
                        throw new UnauthorizedAccessException("The coordinator reservation belongs to a different Main or binding.");
                    return true;
                }
            }

            // Check all journal identities in the same write transaction as registration.
            // A matching name/profile or a prior communication grant cannot establish ownership.
            using (var history = EventJournal.Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM workflow_conversations WHERE conversation_id=$target)
                    OR EXISTS(SELECT 1 FROM event_streams WHERE conversation_id=$target)
                    OR EXISTS(SELECT 1 FROM conversation_events WHERE conversation_id=$target)
                    OR EXISTS(SELECT 1 FROM workflow_runs WHERE conversation_id=$target)
                    OR EXISTS(SELECT 1 FROM collaboration_tasks
                        WHERE source_conversation_id=$target OR target_conversation_id=$target)
                    OR EXISTS(SELECT 1 FROM conversation_owned_coordinators WHERE main_conversation_id=$target);
                """))
            {
                history.Parameters.AddWithValue("$target", targetConversationId);
                if ((long)history.ExecuteScalar()! != 0)
                    throw new UnauthorizedAccessException("An existing or independently linked chat cannot become an owned coordinator.");
            }
            using var insert = EventJournal.Command(connection, transaction, """
                INSERT INTO conversation_owned_coordinators
                    (main_conversation_id,coordinator_conversation_id,profile_id,project_id,created_by_run_id)
                VALUES($main,$target,$profile,$project,$run);
                """);
            insert.Parameters.AddWithValue("$main", submission.ConversationId);
            insert.Parameters.AddWithValue("$target", targetConversationId);
            insert.Parameters.AddWithValue("$profile", targetProfileId);
            insert.Parameters.AddWithValue("$project", (object?)submission.ProjectId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$run", sourceRunId);
            insert.ExecuteNonQuery();
            return true;
        });
    }

    internal static bool IsOwnedCoordinatorConversation(SqliteConnection connection,
        SqliteTransaction transaction, string conversationId)
    {
        using var command = EventJournal.Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM conversation_owned_coordinators
                WHERE coordinator_conversation_id=$conversation);
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        return (long)command.ExecuteScalar()! != 0;
    }

    internal static bool IsOwnedCoordinatorRoute(SqliteConnection connection,
        SqliteTransaction transaction, CollaborationTask task)
    {
        if (task.SourceRunId is not { } sourceRunId || task.ProjectGrantId is not null) return false;
        var source = ReadOwnedCoordinatorSource(connection, transaction, sourceRunId);
        if (source is null || !IsOwnerMain(source) ||
            source.Submission.ConversationId != task.Request.SourceConversationId ||
            source.Submission.ProfileId != task.SourceProfileId ||
            source.Submission.ProjectId != task.SourceProjectId || task.SourceProjectId != task.TargetProjectId ||
            task.SourceProfileId != task.TargetProfileId)
            return false;
        using var command = EventJournal.Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM conversation_owned_coordinators
                WHERE main_conversation_id=$main AND coordinator_conversation_id=$target
                    AND profile_id=$profile AND project_id IS $project);
            """);
        command.Parameters.AddWithValue("$main", task.Request.SourceConversationId);
        command.Parameters.AddWithValue("$target", task.Request.TargetConversationId);
        command.Parameters.AddWithValue("$profile", task.TargetProfileId);
        command.Parameters.AddWithValue("$project", (object?)task.TargetProjectId ?? DBNull.Value);
        return (long)command.ExecuteScalar()! != 0;
    }

    private static WorkflowRun? ReadOwnedCoordinatorSource(SqliteConnection connection,
        SqliteTransaction transaction, string sourceRunId)
    {
        using var command = EventJournal.Command(connection, transaction,
            "SELECT payload FROM workflow_runs WHERE id=$run;");
        command.Parameters.AddWithValue("$run", sourceRunId);
        return command.ExecuteScalar() is string payload ? Deserialize<WorkflowRun>(payload) : null;
    }

    private static bool IsOwnerMain(WorkflowRun run) =>
        run.Submission.ParentRunId is null &&
        (run.Submission.Caller is null or { Kind: EventActorKind.User }) &&
        run.Submission.DefinitionId == ConversationWorkflowContract.DefinitionId &&
        // Only these owner-Main editions share the qualified owned-coordination source contract.
        // The coordinator target retains its separate exact built-in workflow binding.
        run.Submission.DefinitionVersion is ConversationWorkflowContract.DefinitionVersion or
            "1.0.2-generic-context.1" or "1.0.3-tool-context.1";
}
