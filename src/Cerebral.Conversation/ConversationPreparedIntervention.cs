using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

// Candidate-only addition. Older factories retain their constructors, descriptors and behavior.
public sealed partial class ConversationAgentHarness : IAgentPreparedRejectionSupport
{
    public const string PreparedInterventionVersion = "1.0.4-prepared-intervention.1";
    private readonly bool usePreparedIntervention;
    private enum PreparedInterventionEdition { Enabled }

    private ConversationAgentHarness(PreparedInterventionEdition edition) : this(true, false, true)
    {
        usePreparedIntervention = edition == PreparedInterventionEdition.Enabled;
        Descriptor = Descriptor with { Version = PreparedInterventionVersion };
    }

    /// <summary>Explicit opt-in only; accepted packages and application defaults are unchanged.</summary>
    public static ConversationAgentHarness CreatePreparedInterventionEdition() =>
        new(PreparedInterventionEdition.Enabled);

    public bool CanRejectPrepared(AgentHarnessSnapshot snapshot)
    {
        if (HasTerminalCapacityMarker(snapshot.State) || !usePreparedIntervention || !MatchesPreparedEdition(snapshot) ||
            snapshot.Run.Status != WorkflowStatus.Waiting || snapshot.Run.CancelRequested ||
            snapshot.Effect is not { State: WorkflowEffectState.Prepared, Result: null,
                Error: null, ProviderReceiptId: null, ReservedExecutionMilliseconds: 0, FailureBudgetKey: null } effect)
            return false;
        try
        {
            // This admission is also used for a later permit. Inspect its CURRENT pending
            // effect; do not require it to equal an earlier rejected effect or request.
            var state = RestorePreparedInterventionState(snapshot);
            return MatchesPendingPreparedTool(state, effect);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or
            InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private bool MatchesPreparedEdition(AgentHarnessSnapshot snapshot) =>
        snapshot.Submission.DefinitionId == Id &&
        snapshot.Submission.DefinitionVersion == Descriptor.Version &&
        Descriptor.Version is PreparedInterventionVersion or RepeatPreparedVersion or OwnerVerifiedPreparedVersion;

    private static HarnessState RestorePreparedInterventionState(AgentHarnessSnapshot snapshot)
    {
        ValidateContextContributionStateEnvelope(snapshot.State);
        ValidateToolContextStateEnvelope(snapshot.State);
        var state = snapshot.State.Deserialize<HarnessState>(Json)
            ?? throw new ArgumentException("The prepared conversation state is missing.");
        if (state.ContextContributions is null || state.ToolContextProjections?.RunId != snapshot.Run.Id)
            throw new ArgumentException("The prepared conversation edition or run does not match.");
        ValidateContextContributionState(state);
        ValidateToolContextProjectionState(state);
        ValidateBoundState(state);
        ValidateAnsweredFormProvenance(state, snapshot.Run.Id);
        ValidateActionCorrections(state);
        return state;
    }

    private static bool MatchesPendingPreparedTool(HarnessState state, WorkflowEffect effect)
    {
        if (effect.CommandId != Tool || effect.CommandVersion != 1 ||
            state.Phase is not ("tool" or "generated-sink") ||
            state.PendingToolContextProjection is not null || state.PendingArguments is not { } arguments ||
            state.PendingName is not { Length: > 0 and <= 256 } name ||
            !JsonElement.DeepEquals(arguments, effect.Arguments) ||
            arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("name", out var tool) || tool.ValueKind != JsonValueKind.String ||
            tool.GetString() != name)
            return false;
        if (state.PendingPluginTargetAction is not { } deferred) return true;
        // The deferred proposal belongs only to this exact plugin.projects target inspection.
        // A different or schema-preparation chain is not a supported prepared tool boundary.
        return state.Phase == "tool" && name == "plugin.projects" && !deferred.PreparingSchema &&
            PendingToolArguments(state) is { } inspection &&
            TryText(inspection, "targetPath", MaximumBindingPathCharacters, out var requested) &&
            TryCanonicalAbsolutePath(requested, out var canonical) &&
            PathEquals(canonical, deferred.TargetPath);
    }

    private WorkflowStep ReceivePreparedRejection(AgentHarnessSnapshot snapshot, Input input,
        HarnessState state, IReadOnlyList<WorkflowSteeringEntry> pendingSteering)
    {
        if (!usePreparedIntervention || !MatchesPreparedEdition(snapshot))
            return Finish(state, false, "This conversation edition does not support prepared owner rejection.");
        if (snapshot.Effect is not { State: WorkflowEffectState.RejectedBeforeDispatch, Result: null,
                ProviderReceiptId: null, ReservedExecutionMilliseconds: 0, FailureBudgetKey: null } effect ||
            !MatchesPendingPreparedTool(state, effect))
            return Finish(state, false, "The owner rejection does not match the pending undispatched tool intent.");

        var signal = ReadPreparedRejectionSignal(snapshot.Signal.Data);
        if (signal.EffectId != effect.Id || signal.Reason != effect.Error ||
            !pendingSteering.Any(entry => entry.RequestId == signal.SteeringRequestId))
            return Finish(state, false, "The prepared rejection has no matching owner steering and effect.");

        var audit = snapshot.Run.PreparedInterventions?.Where(entry =>
            entry.Kind == WorkflowPreparedInterventionKind.Reject &&
            entry.RequestId == signal.SteeringRequestId && entry.EffectId == effect.Id).ToArray() ?? [];
        // The audit hashes the original Prepared intent. Reconstruct only its identity;
        // this never creates a result, receipt, dispatch or synthetic completed effect.
        var original = effect with { State = WorkflowEffectState.Prepared, Error = null };
        if (audit.Length != 1 || audit[0].AppliedRevision < 1 ||
            audit[0].AppliedRevision > snapshot.Run.Revision ||
            audit[0].EffectSha256 != WorkflowPreparedEffectIdentity.Sha256(original))
            return Finish(state, false, "The prepared owner rejection has no matching durable intent audit.");

        var sourceId = "prepared-owner-rejection." + signal.SteeringRequestId;
        if (state.Contributions.Any(source => source.Id == sourceId))
            return Finish(state, false, "The prepared owner rejection has already been adopted.");

        // Retain provider receipts, failure history, policy counters and unrelated deferred
        // state. Only this exact pending intent (including its generated sink payload) clears.
        var next = state with { PendingArguments = null, PendingName = null, PendingGeneration = null };
        var deferredAudit = "";
        if (state.PendingPluginTargetAction is { } deferred)
        {
            // MatchesPendingPreparedTool already joined this chain to the rejected inspection.
            // It must not resume authoring after its inspection was deliberately withdrawn.
            next = next with { PendingPluginTargetAction = null };
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(deferred.Arguments.GetRawText()))).ToLowerInvariant();
            deferredAudit = "The dependent target proposal " + deferred.Name + " for " + deferred.TargetPath +
                " (arguments fingerprint " + fingerprint + ") was also retired without execution. " +
                "Choose it again explicitly after reviewing current state.\n";
        }
        next = AppendSteering(next, pendingSteering);
        next = Append(next, sourceId,
            "owner-prepared-rejection." + audit[0].AppliedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), "user",
            "The owner rejected prepared tool intent " + effect.Id + " (" + state.PendingName +
            ") before dispatch. This intent was not executed and has no tool or provider result.\n" +
            "Owner request: " + signal.SteeringRequestId + "; rejected intent fingerprint: " + audit[0].EffectSha256 + ".\n" +
            "Owner rejection reason: " + signal.Reason + "\n" + deferredAudit +
            "Review the owner direction and current evidence, then choose a new decision. " +
            "Do not treat the rejected intent as completed work.",
            ContextRequirement.Required, null);
        return RequestModel(input, next);
    }

    private static WorkflowPreparedRejectionSignal ReadPreparedRejectionSignal(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The prepared rejection signal is not an object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Add(property.Name) ||
                property.Name is not ("effectId" or "reason" or "steeringRequestId"))
                throw new ArgumentException("The prepared rejection signal has invalid fields.");
        if (fields.Count != 3)
            throw new ArgumentException("The prepared rejection signal has missing fields.");
        var signal = value.Deserialize<WorkflowPreparedRejectionSignal>(Json)
            ?? throw new ArgumentException("The prepared rejection signal is missing.");
        if (!ValidEffectId(signal.EffectId) || !ValidEffectId(signal.SteeringRequestId) ||
            string.IsNullOrWhiteSpace(signal.Reason) || signal.Reason.Length > 3072 ||
            signal.Reason.Contains('\0') || Encoding.UTF8.GetByteCount(signal.Reason) > 4096)
            throw new ArgumentException("The prepared rejection signal exceeds its bounded contract.");
        return signal;
    }
}
