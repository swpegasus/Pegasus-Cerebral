using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class JobScheduler
{
    private readonly InferenceObservationRegistry inferenceObservation = ModelExecutionAccounting.For(store).Observation;
    private readonly string inferenceSchedulerGeneration = ModelExecutionAccounting.For(store).Observation.StartScheduler();

    private void ObserveAcceptedWorkflow(string key, CancellationTokenSource token, WorkflowSubmission submission)
    {
        InferenceExecutionCoordinate? coordinate = null;
        try
        {
            var profile = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Profile;
            if (key == "workflow:" + submission.RequestId && profile.Id == submission.ProfileId && profile.WorkerId == "local")
                coordinate = new(key, profile.Id, profile.Revision, submission.ConversationId, submission.RequestId);
        }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { }
        inferenceObservation.AcceptExecution(inferenceSchedulerGeneration, key, token, coordinate, "local");
    }
    private void ObserveAcceptedJob(AgentJob job, CancellationTokenSource token)
    {
        var kind = job.WorkerId != "local" ? "remote-dispatch" : job.Kind == "compact" ? "maintenance" : "local";
        InferenceExecutionCoordinate? coordinate = null;
        try
        {
            if (job.Profile.Id == job.ProfileId)
                coordinate = new(job.Id, job.Profile.Id, job.Profile.Revision);
        }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { }
        inferenceObservation.AcceptExecution(inferenceSchedulerGeneration, job.Id, token, coordinate, kind);
    }
    private void ObserveRetiredExecution(string key, CancellationTokenSource token) =>
        inferenceObservation.RetireExecution(inferenceSchedulerGeneration, key, token);
}
