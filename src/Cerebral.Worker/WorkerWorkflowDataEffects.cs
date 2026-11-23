using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private bool DataSourceCancelled(WorkflowSubmission submission)
    {
        if (submission.DefinitionId != WorkflowDataModule.DefinitionId) return false;
        var host = workflowHost?.Invoke() ?? throw new AdmissionException("The data workflow source host is unavailable.");
        var input = Read<WorkflowDataRunInput>(submission.Input);
        var source = host.Store.Get(input.SourceRequestId);
        if (source.Submission.ProfileId != submission.ProfileId || source.Submission.ProjectId != submission.ProjectId ||
            source.Submission.DefinitionId == WorkflowDataModule.DefinitionId)
            throw new AdmissionException("The data workflow source binding is invalid.");
        return source.CancelRequested || source.Status == WorkflowStatus.Cancelled;
    }
}
