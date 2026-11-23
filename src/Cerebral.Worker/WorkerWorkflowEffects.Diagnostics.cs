using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private EngineDiagnosticRecord? CreateObservation(WorkflowCommandContext context, PreparedModelMetadata? prepared = null,
        AgentProfile? observedProfile = null)
    {
        if (diagnosticEvents?.HasDiagnosticSubscribers != true) return null;
        try
        {
            var profile = observedProfile ?? WorkerExecutionSnapshot.Read(context.Submission.SettingsSnapshot).Profile;
            return new(0, DateTimeOffset.UtcNow, context.Submission.ConversationId,
                context.Submission.RequestId, context.RunId, context.EffectId, profile.Id, profile.ModelWorkerId,
                profile.ModelId, "", InputTokens: prepared?.InputTokens, ResponseTokens: prepared?.ResponseBudget,
                ReasoningTokens: prepared?.ReasoningReserveTokens, ContextTokens: prepared?.EffectiveContext);
        }
        catch (Exception error) when (IsOrdinary(error)) { return null; }
    }
    private void Observe(WorkflowCommandContext context, string phase, string? outcome = null) =>
        Observe(CreateObservation(context), phase, outcome: outcome);

    private void Observe(EngineDiagnosticRecord? observation, string phase, string? text = null,
        ModelGenerationResult? generation = null, long? elapsed = null,
        string? outcome = null)
    {
        if (observation is null || diagnosticEvents?.HasDiagnosticSubscribers != true) return;
        try
        {
            diagnosticEvents.PublishDiagnostic(observation with { Phase = phase, Text = text,
                GeneratedTokens = generation?.GeneratedTokens, ElapsedMilliseconds = elapsed,
                Outcome = outcome ?? generation?.Finish.ToString() });
        }
        catch (Exception error) when (IsOrdinary(error)) { }
    }
}
