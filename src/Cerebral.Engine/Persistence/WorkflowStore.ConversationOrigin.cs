using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    /// <summary>
    /// Reads one exact original owner submission without loading the conversation's event history.
    /// Service work, delegated children and owner steering of those runs cannot establish this binding.
    /// </summary>
    public WorkflowSubmission? ReadOriginalConversationSubmission(string definitionId, string conversationId)
    {
        Identifier(definitionId, nameof(definitionId), maximum: 256);
        Identifier(conversationId, nameof(conversationId));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT submission FROM workflow_runs
            WHERE conversation_id=$conversation
                AND json_extract(submission,'$.definitionId')=$definition
                AND json_extract(submission,'$.parentRunId') IS NULL
                AND (json_extract(submission,'$.caller.kind') IS NULL
                    OR json_extract(submission,'$.caller.kind')='User')
            ORDER BY rowid ASC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$definition", definitionId);
        if (command.ExecuteScalar() is not string json) return null;
        var submission = Deserialize<WorkflowSubmission>(json);
        if (submission.ConversationId != conversationId || submission.DefinitionId != definitionId ||
            submission.ParentRunId is not null ||
            submission.Caller is { Kind: not EventActorKind.User })
            throw new InvalidDataException("The original conversation submission has conflicting saved coordinates.");
        return Freeze(submission);
    }
}
