using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

internal sealed record OwnerWorkflowAbandonRequest(string RequestId, string RunId, long ExpectedRevision,
    string EffectId, string EffectSha256, string Reason);

public sealed partial class JobScheduler
{
    public WorkflowAbandonInspection InspectWorkflowAbandon(string id)
    {
        if (workflows is null) throw new InvalidOperationException("Workflow engine is unavailable.");
        return store.Locked(() =>
        {
            var boundRunIds = workflows.Store.ChildRunIds(id).Prepend(id).ToArray();
            var inspection = workflows.Store.InspectOwnerAbandon(id);
            var live = WorkflowAbandonExecutionRefusal(boundRunIds);
            return live is null ? inspection : inspection with { Eligible = false, RefusalReason = live };
        });
    }

    internal WorkflowAbandonInspection AbandonWorkflow(OwnerWorkflowAbandonRequest request)
    {
        if (workflows is null) throw new InvalidOperationException("Workflow engine is unavailable.");
        return store.Locked(() =>
        {
            workflowStopping.ThrowIfCancellationRequested();
            // Materialize journal-owned identities before entering the Engine write transaction.
            // The shared admission gate remains held; Engine rechecks nonterminal children itself.
            var boundRunIds = workflows.Store.ChildRunIds(request.RunId).Prepend(request.RunId).ToArray();
            var run = workflows.Store.AbandonOwner(request.RequestId, request.RunId, request.ExpectedRevision,
                request.EffectId, request.EffectSha256, request.Reason, current =>
                {
                    if (current.Id != request.RunId)
                        throw new WorkflowConflictException("The abandonment admission identity changed.");
                    if (WorkflowAbandonExecutionRefusal(boundRunIds) is { } refusal)
                        throw new WorkflowConflictException(refusal);
                });
            workflows.Publish(run);
            return workflows.Store.InspectOwnerAbandon(run.Id);
        });
    }

    private string? WorkflowAbandonExecutionRefusal(IReadOnlyList<string> boundRunIds)
    {
        // In-transaction callback: dictionary reads only, with no nested journal transaction.
        foreach (var runId in boundRunIds)
        {
            var key = "workflow:" + runId;
            if (running.ContainsKey(key) || executions.TryGetValue(key, out var task) && !task.IsCompleted)
                return "This workflow or its child still owns live execution or reconciliation; no abandonment was committed.";
        }
        return null;
    }
}
