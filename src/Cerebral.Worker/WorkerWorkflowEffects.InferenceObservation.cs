using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private static IDisposable EnterInferenceObservation(WorkflowCommandContext context, string purpose)
    {
        InferenceInvocation? invocation = null;
        try
        {
            var profile = WorkerExecutionSnapshot.Read(context.Submission.SettingsSnapshot).Profile;
            var coordinate = new InferenceExecutionCoordinate("workflow:" + context.Submission.RequestId,
                profile.Id, profile.Revision, context.Submission.ConversationId,
                context.Submission.RequestId, context.RunId, context.EffectId);
            var identity = JsonSerializer.Serialize(new[] { coordinate.ExecutionId, context.RunId, context.EffectId });
            invocation = new("effect-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
                purpose, coordinate);
        }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { }
        return WorkerInferenceInvocation.Enter(invocation);
    }
}
