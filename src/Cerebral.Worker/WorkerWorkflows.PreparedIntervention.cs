using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    public WorkflowRun RejectPrepared(WorkflowPreparedRejectRequest request) =>
        Publish(Runtime.RejectPrepared(request));

    public WorkflowRun PermitPreparedOnce(WorkflowPreparedPermitRequest request) =>
        Publish(Runtime.PermitPreparedOnce(request));
}
