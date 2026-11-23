using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;
namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    /// <summary>Read the run and receipt counters in the same snapshot. This reads only
    /// receipt IDs/states and the existing current-effect marker, never event or tool bodies.
    /// The ledger retains the latest result per effect, including reconciliation.</summary>
    public WorkflowRun GetWithActivityTotals(string id)
    {
        Identifier(id, nameof(id));
        using var connection = journal.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var run = Required(connection, transaction, id);
        var sequence = EventJournal.ReadRevision(connection, transaction, run.Submission.ConversationId);
        using var command = EventJournal.Command(connection, transaction, """
            SELECT effect_id,
                CASE WHEN json_valid(result) THEN
                    CASE WHEN json_type(result)='object' AND json_type(result,'$.state')='text'
                        THEN json_extract(result,'$.state') END END
            FROM workflow_effect_results WHERE run_id=$run
            ORDER BY effect_id LIMIT $maximum;
            """);
        command.Parameters.AddWithValue("$run", id);
        command.Parameters.AddWithValue("$maximum", WorkflowContract.MaximumSteps + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var completed = 0; var failed = 0; var cancelled = 0; var unconfirmed = 0; var blocked = 0;
        string? diagnostic = null;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (seen.Count == WorkflowContract.MaximumSteps) { diagnostic = "receipt-limit"; break; }
                var effectId = reader.GetString(0);
                if (effectId is not { Length: > 0 and <= 128 } || !seen.Add(effectId))
                { diagnostic = "invalid-receipt-metadata"; continue; }
                var state = reader.IsDBNull(1) ? null : reader.GetString(1);
                switch (state)
                {
                    case "Succeeded": completed++; break;
                    case "Failed": failed++; break;
                    case "Cancelled": cancelled++; break;
                    case "Unknown": unconfirmed++; break;
                    default: unconfirmed++; diagnostic = "invalid-receipt-metadata"; break;
                }
            }
        }
        // An explicit owner rejection never dispatches or creates an execution result.
        // Its bounded intervention record remains after the workflow plans a later effect.
        foreach (var intervention in (run.PreparedInterventions ?? []).Where(item => item.Kind == WorkflowPreparedInterventionKind.Reject))
        {
            if (seen.Count == WorkflowContract.MaximumSteps) { diagnostic = "receipt-limit"; break; }
            if (intervention.EffectId is not { Length: > 0 and <= 128 } ||
                intervention.AppliedRevision < 1 || intervention.AppliedRevision > run.Revision ||
                !seen.Add(intervention.EffectId))
            { diagnostic = "invalid-receipt-metadata"; continue; }
            blocked++;
        }
        // A prepared/cancelled-before-dispatch effect has no result row yet. It still
        // represents one recorded call. A result row already owns the same effect ID.
        if (run.Effect is { } current && !seen.Contains(current.Id))
        {
            if (seen.Count == WorkflowContract.MaximumSteps) diagnostic = "receipt-limit";
            else if (current.Id is not { Length: > 0 and <= 128 }) diagnostic = "invalid-current-effect";
            else
            {
                seen.Add(current.Id);
                switch (current.State)
                {
                    case WorkflowEffectState.Cancelled: cancelled++; break;
                    case WorkflowEffectState.Prepared:
                    case WorkflowEffectState.Dispatched:
                    case WorkflowEffectState.Unknown: unconfirmed++; break;
                    case WorkflowEffectState.Succeeded:
                    case WorkflowEffectState.Failed: unconfirmed++; diagnostic = "missing-terminal-receipt"; break;
                    default: unconfirmed++; diagnostic = "invalid-current-effect"; break;
                }
            }
        }
        var totals = new WorkflowActivityTotals(run.Submission.ConversationId, run.Id, run.Revision,
            sequence, seen.Count, completed, failed, cancelled, unconfirmed, diagnostic is null, diagnostic) { Blocked = blocked };
        return run with { ActivityTotals = totals };
    }
}
