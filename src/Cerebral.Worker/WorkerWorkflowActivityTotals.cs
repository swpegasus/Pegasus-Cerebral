using Cerebral.Core;
using Cerebral.Contracts.Workflows;
namespace Cerebral.Worker;
public sealed partial class WorkerWorkflows
{
    // Preserve GetOwned for authorization-only callers; attach totals only to the
    // owner-visible workflow read so receipt lookups do not repeat aggregate work.
    public WorkflowRun GetOwnedWithActivityTotals(string id, AgentProfile? actor)
    {
        var run = Store.GetWithActivityTotals(id);
        if (actor is not null && run.Submission.ProfileId != actor.Id)
            throw new UnauthorizedAccessException("This profile does not own the workflow.");
        return run;
    }
}
