using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private void BindGenerationTelemetry(WorkflowCommandContext context, IPreparedModelGeneration prepared,
        ModelExecutionPolicy? selectedPolicy)
    {
        // Optional observation must never change the prepared effect's result or adoption boundary.
        try
        {
            if (prepared is not IBoundGenerationTelemetry telemetry) return;
            var (snapshot, effective) = Resolve(context.Submission);
            var binding = new GenerationTelemetryBinding(context.Submission.ConversationId, context.Submission.RequestId,
                context.RunId!, context.EffectId, effective.Id, effective.ModelWorkerId);
            if (new[] { binding.ConversationId, binding.RequestId, binding.RunId, binding.EffectId, binding.AgentId, binding.WorkerId }
                .Any(value => !PreviewIdentity(value))) return;
            // Scalar identity failure must not suppress the pre-existing diagnostic event.
            // An empty producer identity is counted as a scalar gap by the bounded feed.
            var producerInstallationId = string.Empty;
            var producerProcessGenerationId = string.Empty;
            try
            {
                producerInstallationId = configuration.Get().InstallationId;
                producerProcessGenerationId = ModelExecutionAccounting.For(store).ProcessGenerationId;
            }
            catch (Exception error) when (IsOrdinary(error)) { }
            // The selected-local command may use a different prepared model than the
            // submission's default binding. Its owned policy, already required on the
            // prepared lease by GeneratePreparedAsync, is the only valid origin source.
            var targetOrigin = selectedPolicy is null
                ? QualifiedGenerationTargetOrigin(snapshot.Model?.Id,
                    snapshot.Model?.ModelWorkerId, snapshot.Model?.ModelWorkerInstallationId,
                    effective.ModelWorkerId, prepared.Metadata.ModelId)
                : QualifiedSelectedLocalGenerationTargetOrigin(selectedPolicy, prepared.Metadata, producerInstallationId);
            telemetry.BindTelemetry(binding, sample =>
            {
                try { diagnosticEvents?.PublishScalarGeneration(producerInstallationId, producerProcessGenerationId, sample, targetOrigin); }
                catch (Exception error) when (IsOrdinary(error)) { }
                try { diagnosticEvents?.Publish("workflow.generation.metrics", context.RunId, sample); }
                catch (Exception error) when (IsOrdinary(error)) { }
            });
        }
        catch (Exception error) when (IsOrdinary(error)) { }
    }

    // A local producer can observe a remote target. Only the accepted model route
    // joined to this prepared model qualifies that distinction; adapter names do not.
    internal static ScalarGenerationTargetOriginV1 QualifiedGenerationTargetOrigin(
        string? acceptedModelId, string? acceptedModelWorkerId, string? acceptedWorkerInstallationId,
        string? effectiveModelWorkerId, string? preparedModelId)
    {
        if (string.IsNullOrWhiteSpace(acceptedModelId) || acceptedModelId != preparedModelId ||
            string.IsNullOrWhiteSpace(acceptedModelWorkerId) || acceptedModelWorkerId != effectiveModelWorkerId)
            return ScalarGenerationTargetOriginV1.Unknown;
        if (acceptedModelWorkerId == "local") return ScalarGenerationTargetOriginV1.Local;
        return string.IsNullOrWhiteSpace(acceptedWorkerInstallationId)
            ? ScalarGenerationTargetOriginV1.Unknown : ScalarGenerationTargetOriginV1.Remote;
    }

    internal static ScalarGenerationTargetOriginV1 QualifiedSelectedLocalGenerationTargetOrigin(
        ModelExecutionPolicy policy, PreparedModelMetadata prepared, string producerInstallationId)
    {
        var selected = policy.Binding;
        return policy.SchemaVersion == ModelExecutionPolicy.CurrentSchemaVersion && selected is not null &&
            !string.IsNullOrWhiteSpace(producerInstallationId) &&
            selected.WorkerInstallationId == producerInstallationId &&
            selected.ModelId == prepared.ModelId && selected.InstanceId == prepared.InstanceId &&
            selected.AdapterId == prepared.AdapterId && selected.AdapterVersion == prepared.AdapterVersion
            ? ScalarGenerationTargetOriginV1.Local : ScalarGenerationTargetOriginV1.Unknown;
    }
}
