namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    /// <summary>Exact retained owner-answer presence, independent of mutable run state or paged model history.</summary>
    public bool HasAcceptedAnswers(string runId)
    {
        Identifier(runId, nameof(runId));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null,
            "SELECT EXISTS(SELECT 1 FROM workflow_answers WHERE run_id=$run);");
        command.Parameters.AddWithValue("$run", runId);
        return (long)command.ExecuteScalar()! != 0;
    }
}
