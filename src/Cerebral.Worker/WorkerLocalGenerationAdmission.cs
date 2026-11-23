using Cerebral.Contracts.Workflows;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    private static WorkflowLocalGenerationConsumer? FreezeLocalGenerationConsumer(
        WorkflowDefinitionDescriptor definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var declared = definition.LocalGenerationConsumer;
        declared?.Validate();
        return declared is null ? null : new(declared.SchemaVersion, declared.CommandId,
            declared.CommandVersion, declared.Route);
    }

    private void RequireLocalGenerationDefinition(WorkflowLocalGenerationChoice? choice,
        WorkflowDefinitionDescriptor definition)
    {
        if (choice is null) return;
        choice.Validate();
        var declared = FreezeLocalGenerationConsumer(definition);
        if (declared is null || declared.Route != choice.Route ||
            !Registry.TryGetCommand(declared.CommandId, declared.CommandVersion, out _))
            throw new UnauthorizedAccessException("The exact workflow definition does not admit local generation.");
    }

    private void RequireLocalGenerationSnapshot(WorkflowLocalGenerationChoice? choice,
        WorkerExecutionSnapshot.Snapshot accepted, WorkflowDefinitionDescriptor currentDefinition)
    {
        var current = RequireLocalGenerationConsumerSnapshot(accepted, currentDefinition);
        if (choice is null) return;
        RequireLocalGenerationDefinition(choice, currentDefinition);
        var profile = accepted.Profile;
        if (current is null || accepted.Model is null || profile.WorkerId != "local" ||
            profile.ModelWorkerId != "local" || profile.ReasoningMode != "off" ||
            accepted.Model.ModelWorkerId != "local" ||
            accepted.Model.VisionProjectorPath is not null ||
            ModelCapabilityPolicy.Requirements(profile).Contains(ModelCapabilityIds.ImageInput) ||
            choice.ContextTokens is < 512 or > LlamaPreparedGeneration.NativeMaximumContextTokens ||
            choice.ContextTokens > profile.ContextTokens ||
            (accepted.Model.MaximumContextTokens > 0 &&
                choice.ContextTokens > accepted.Model.MaximumContextTokens) ||
            (long)choice.VisibleOutputTokens + AgentTokenLimits.ContextSafetyTokens + 1 > choice.ContextTokens)
            throw new UnauthorizedAccessException("The accepted local text model cannot admit the requested generation envelope.");
    }

    internal static WorkflowLocalGenerationConsumer? RequireLocalGenerationConsumerSnapshot(
        WorkerExecutionSnapshot.Snapshot accepted, WorkflowDefinitionDescriptor currentDefinition)
    {
        var current = FreezeLocalGenerationConsumer(currentDefinition);
        // Both retained edition seven and new permission-bound edition eight keep owner
        // verification separate from local generation.
        if (!WorkerExecutionSnapshot.IsCurrentEdition(accepted.SourceSettingsVersion) ||
            (accepted.OwnerVerification is not null && accepted.LocalGenerationConsumer is not null) ||
            accepted.LocalGenerationConsumer != current)
            throw new InvalidDataException("The local-generation consumer changed after accepted snapshot capture.");
        return current;
    }

    internal void RequireLocalGenerationAccepted(WorkflowSubmission submission)
    {
        var choice = submission.LocalGeneration ??
            throw new UnauthorizedAccessException("This workflow did not accept a local generation choice.");
        var currentDefinition = Registry.GetDefinition(submission.DefinitionId, submission.DefinitionVersion).Descriptor;
        var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
        RequireLocalGenerationSnapshot(choice, snapshot, currentDefinition);
        var definitionProblem = packages.Check(submission, currentDefinition.Id);
        var commandProblem = packages.Check(submission, WorkflowLocalGenerationConsumer.NativeGenerateCommandId);
        if (definitionProblem is not null || commandProblem is not null)
            throw new UnauthorizedAccessException(definitionProblem ?? commandProblem);
    }

    internal void RequireLocalGenerationExecution(WorkflowSubmission submission, ModelExecutionPolicy selected)
    {
        RequireLocalGenerationAccepted(submission);
        var choice = submission.LocalGeneration!;
        var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
        selected.Validate();
        if (selected.Binding.ModelId != snapshot.Model!.Id || selected.Binding.InstanceId is null ||
            choice.ContextTokens < selected.MinimumContextTokens ||
            choice.ContextTokens > selected.MaximumContextTokens ||
            choice.VisibleOutputTokens < selected.MinimumVisibleOutputTokens ||
            choice.VisibleOutputTokens > selected.MaximumVisibleOutputTokens)
            throw new UnauthorizedAccessException("The current loaded policy cannot admit the accepted generation choice.");
    }
}
