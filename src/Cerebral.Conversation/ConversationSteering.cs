using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private WorkflowStep AdvanceAcceptedSnapshot(AgentHarnessSnapshot snapshot)
    {
        var state = useContextContributions
            ? new HarnessState { ContextContributions = new(1, []),
                ToolContextProjections = useToolContextProjections ? new(1, snapshot.Run.Id, []) : null } : new HarnessState();
        if (useToolCatalogPages) state = state with { ToolCatalogPage = new(2, snapshot.Run.Id) };
        if (useLocalGeneration && snapshot.Submission.LocalGeneration is { } localChoice)
            state = state with { LocalGenerationV2 = new(1, snapshot.Run.Id, localChoice with { }) };
        state = state with { ReadRecovery = new(1, snapshot.Run.Id, MaximumActionCorrections),
            FailedBuildRecovery = new(1, snapshot.Run.Id), RequiredReadDisplays = new(1, snapshot.Run.Id, [], AutomaticPaging: true) };
        try
        {
            if (useContextContributions)
            {
                if (snapshot.Submission.DefinitionId != Descriptor.Id || snapshot.Submission.DefinitionVersion != Descriptor.Version)
                    throw new ArgumentException("The saved definition does not match this context contribution edition.");
                ValidateContextContributionStateEnvelope(snapshot.State);
            }
            else ValidatePersonalityStateEnvelope(snapshot.State);
            var restored = snapshot.State.TryGetProperty("phase", out _)
                ? snapshot.State.Deserialize<HarnessState>(Json) ?? throw new ArgumentException("The saved conversation state is missing.")
                : state;
            ValidateToolContextStateEnvelope(snapshot.State);
            if (useToolContextProjections != (restored.ToolContextProjections is not null) ||
                restored.ToolContextProjections is { } receiptState && receiptState.RunId != snapshot.Run.Id)
                throw new ArgumentException("The saved receipt projection state belongs to another edition or run.");
            ValidateToolContextProjectionState(restored);
            ValidateToolCatalogPageState(restored, snapshot.Run.Id, useToolCatalogPages);
            ValidateLocalGenerationState(restored, snapshot, useLocalGeneration);
            ValidateReasoningAdjustmentState(restored, snapshot);
            ValidateTaskBudgetState(restored, snapshot);
            ValidateBoundState(restored);
            ValidateAnsweredFormProvenance(restored, snapshot.Run.Id);
            ValidateActionCorrections(restored);
            ValidateFailedBuildRecovery(restored, snapshot.Run.Id);
            ValidateRetainedReadRecovery(restored, snapshot.Run.Id);
            ValidateRequiredReadDisplays(restored, snapshot.Run.Id);
            restored = RestoreReceiptReadAllowance(restored, snapshot.Run.Id);
            if (useContextContributions)
            {
                if (restored.ContextContributions is null)
                    throw new ArgumentException("The saved context contribution edition marker is missing.");
                if (restored.Personality is not null || restored.Phase == "personality")
                    throw new ArgumentException("The generic edition cannot restore legacy context attempts.");
                ValidateContextContributionState(restored);
            }
            else
            {
                if (restored.ContextContributions is not null)
                    throw new ArgumentException("The saved context contribution state belongs to another edition.");
                ValidatePersonalityState(restored);
            }
            state = PruneAnsweredFormProvenance(restored, snapshot.Run.Id);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return Finish(state, false, "The saved conversation state could not be restored: " + error.Message);
        }
        try
        {
            var input = ReadInput(snapshot.Submission.Input);
            ValidateLocalGenerationInput(input, state);
            var hostAddedRequirements = HostAddedRequirements(snapshot, input, out var automaticPresentation);
            input = input with
            {
                UseWorkflowProgress = useWorkflowProgress,
                MainSourceRunId = IsMainConversation(snapshot.Submission) ? snapshot.Run.Id : null,
                UseCoordinatorConversation = snapshot.Submission.DefinitionId == Id &&
                    snapshot.Submission.ParentRunId is null &&
                    snapshot.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.collaboration" },
                UseRepeatedSuccessGuard = useRepeatedSuccessGuard,
                UseOwnerVerificationCompletion = useOwnerVerificationCompletion,
                OwnerVerificationRequirement = useOwnerVerificationCompletion
                    ? ReadOwnerVerificationRequirement(snapshot) : null,
                ForceCompaction = snapshot.Submission.ConversationCompactionRequestId is not null,
                HostAddedRequiredSuccessfulTools = hostAddedRequirements,
                AutomaticPluginTestPresentationId = automaticPresentation,
                AutomaticReviewHandoffEligible = snapshot.Run.Steering.Count == 0 && snapshot.Questions is null,
                HostPluginTestMountIds = HostPluginTestMounts(snapshot, input, automaticPresentation)
            };
            ValidateMainConversationState(input, state);
            if (snapshot.Run.CancelRequested || snapshot.Run.Status is WorkflowStatus.Cancelled or WorkflowStatus.Cancelling)
                return AdvanceCore(snapshot, input, state);
            if (state.SteeringCount < 0 || state.SteeringCount > snapshot.Run.Steering.Count ||
                snapshot.Run.Steering.Where((entry, index) => entry.Sequence != index + 1).Any())
                return Finish(state, false, "The saved steering sequence is inconsistent. No further effect was requested.");
            var pending = snapshot.Run.Steering.Skip(state.SteeringCount).ToArray();
            if (snapshot.Signal.Kind == WorkflowSignalKind.PreparedRejected)
                return ReceivePreparedRejection(snapshot, input, state, pending);
            if (pending.Length == 0) return AdvanceCore(snapshot, input, state);

            // A raw payload generated for superseded input has no authority to reach its sink.
            if (state.Phase == "generated-model" && snapshot.Signal.Kind == WorkflowSignalKind.EffectResult &&
                snapshot.Effect is { CommandId: Generate, CommandVersion: 1, State: WorkflowEffectState.Succeeded, Result: { } } rawEffect)
            {
                MatchPending(state, rawEffect);
                var next = AppendSteering(state with { PendingGeneration = null, PendingArguments = null }, pending);
                next = Append(next, "superseded." + rawEffect.Id, "superseded-generation", "user",
                    "New direction arrived during raw generation. Its saved payload was not written; respond to the updated request.",
                    ContextRequirement.Reducible, rawEffect.Id);
                return RequestModel(input, next);
            }

            // A generation already in flight may describe an action for superseded input.
            // Its receipt remains saved, but no action from that response may be dispatched.
            if (state.Phase == "model" && snapshot.Signal.Kind == WorkflowSignalKind.EffectResult &&
                snapshot.Effect is { CommandId: Generate, State: WorkflowEffectState.Succeeded, Result: { } result } effect &&
                ConversationEffectVersion(state, effect))
            {
                state = ConsumePendingNormalGeneration(state, effect);
                var data = result.Deserialize<GenerationData>(Json);
                if (data is null || data.Context is not { Status: ContextProjectionStatus.Ready } context ||
                    context.ContextRevision != state.ContextRevision || context.ExclusionRevision != state.ExclusionRevision ||
                    (!data.OutputOmitted && data.Generation?.Text is null))
                    return Finish(state, false, "The superseded generation did not confirm its saved context projection.");
                var next = AppendSteering(state, pending);
                var receipt = data.OutputOmitted
                    ? "The previous generation output was omitted by the host, and no partial action was executed."
                    : "The previous generation receipt is " + effect.Id + "; none of its proposed actions was executed.";
                next = Append(next, "superseded." + effect.Id, "superseded-generation", "user",
                    "New direction arrived during generation. " + receipt + " Respond to the updated request.",
                    ContextRequirement.Reducible, effect.Id);
                return RequestModel(input, next);
            }

            // First adopt the actual command/compaction outcome against its original revision.
            // Steering must not invalidate a genuine receipt or bypass a failed effect.
            var step = AdvanceCore(snapshot, input, state);
            if (step.Next is CompleteWorkflow) return step;
            var adopted = step.State.Deserialize<HarnessState>(Json)
                ?? throw new ArgumentException("The next conversation state is missing.");
            ValidateBoundState(adopted);
            adopted = AppendSteering(adopted, pending);
            if (step.Next is InvokeWorkflowCommand command)
            {
                // AdvanceCore created this command for dispatch after the current receipt was adopted.
                // Rebase that new request only; an in-flight personality effect keeps its original window.
                if (command.CommandId == ToolContextProject && adopted.PendingToolContextProjection is not null)
                    return RequestToolContextProjection(adopted);
                if (adopted.Phase == "context-contribution" && adopted.ContextContributions is not null)
                    return RebaseUndispatchedContextContribution(adopted);
                if (command.CommandId == PersonalityProvider && command.CommandVersion == PersonalityProviderVersion)
                    return RebaseUndispatchedPersonality(adopted);
                if (command.CommandId == Generate) return RequestModel(input, adopted);
                if (command.CommandId == Compact)
                    return RequestCompaction(input, adopted, adopted.CompactionReason ?? "New direction arrived before compaction.");
            }
            if (step.Next is AskWorkflowQuestions) return RequestModel(input, adopted);
            return step with { State = Element(adopted) };
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return Finish(state, false, "The latest direction could not be adopted: " + error.Message);
        }
    }

    private static IReadOnlyList<string> HostAddedRequirements(AgentHarnessSnapshot snapshot, Input input,
        out string? automaticPluginTestPresentationId)
    {
        automaticPluginTestPresentationId = null;
        var names = snapshot.Submission.HostAddedRequiredSuccessfulTools;
        var validInstalledQa = OrderedSubset(names,
            ["interface.status", "plugin.controls"]);
        var validRepair = OrderedSubset(names,
            ["interface.status", "process.run", "plugin.verify", "plugin.present"]);
        if (names is null || names.Count > 4 || names.Distinct(StringComparer.Ordinal).Count() != names.Count ||
            !(validInstalledQa || validRepair))
            throw new ArgumentException("The host-added automatic plugin-test requirements are invalid.");
        if (names.Any(name => !input.RequiredSuccessfulTools.Contains(name, StringComparer.Ordinal)))
            throw new ArgumentException("A host-added completion requirement is absent from the admitted combined requirements.");
        if (names.Count == 0)
        {
            // Original obligations may already contain the complete installed-test or
            // repair-before-install shape. Recognize only the exact service-owned reviewed
            // identity; preserve every original requirement and infer no installation/consent.
            var installedShape = input.RequiredSuccessfulTools.Contains("interface.status", StringComparer.Ordinal) &&
                input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal);
            var repairShape = new[] { "interface.status", "process.run", "plugin.verify", "plugin.present" }
                .All(name => input.RequiredSuccessfulTools.Contains(name, StringComparer.Ordinal));
            if (snapshot.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" } &&
                (installedShape || repairShape) &&
                PluginControlReferences.TryResolveReviewedPresentationId(snapshot.Run.Id, out var presentation))
                automaticPluginTestPresentationId = presentation;
            return [];
        }
        if (snapshot.Submission.Caller is not
            { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" } ||
            !PluginControlReferences.TryResolveReviewedPresentationId(snapshot.Run.Id,
                out automaticPluginTestPresentationId))
            throw new ArgumentException("Host-added plugin-test requirements require their exact service-owned automatic QA run.");
        return names.ToArray();
    }

    private static IReadOnlyList<string> HostPluginTestMounts(AgentHarnessSnapshot snapshot, Input input, string? presentation)
    {
        var mounts = snapshot.Submission.HostPluginTestMountIds;
        if (mounts is null || mounts.Count > WorkflowContract.MaximumPluginTestMounts ||
            mounts.Distinct(StringComparer.Ordinal).Count() != mounts.Count ||
            mounts.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 129 || id.Any(char.IsControl)))
            throw new ArgumentException("The host-approved plugin test mounts are invalid.");
        if (mounts.Count != 0 && (presentation is null ||
            snapshot.Submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" } ||
            !input.RequiredSuccessfulTools.Contains("plugin.controls", StringComparer.Ordinal)))
            throw new ArgumentException("Approved plugin test mounts require their exact service-owned installed QA run.");
        return mounts.ToArray();
    }

    private static bool OrderedSubset(IReadOnlyList<string>? names, IReadOnlyList<string> allowed)
    {
        if (names is null || names.Count > allowed.Count) return false;
        var next = 0;
        foreach (var name in names)
        {
            while (next < allowed.Count && allowed[next] != name) next++;
            if (next == allowed.Count) return false;
            next++;
        }
        return true;
    }

    private static HarnessState AppendSteering(HarnessState state, IReadOnlyList<WorkflowSteeringEntry> entries)
    {
        foreach (var entry in entries)
        {
            // Separate categories keep every unprocessed owner direction exact and required;
            // optional context planning cannot silently discard a correction.
            var category = "steering-" + entry.Sequence;
            state = Append(state, "steering." + entry.RequestId, category, "user", entry.Text,
                ContextRequirement.Required, null, alreadyBounded: true);
            if (entry.AssignmentText is { Length: > 0 } assignment)
                state = Append(state, "steering-assignment." + entry.RequestId, category + "-assignment", "user",
                    assignment, ContextRequirement.Required, null, alreadyBounded: true);
            state = state with { SteeringCount = entry.Sequence };
        }
        return state;
    }
}
