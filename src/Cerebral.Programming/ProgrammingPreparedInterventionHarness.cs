using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

/// <summary>A separate edition; existing Develop and staged policies remain unsupported.</summary>
public sealed class ProgrammingPreparedInterventionHarness : IAgentHarness, IAgentPreparedRejectionSupport
{
    public const string Version = "1.3.22";
    private readonly ProgrammingAgentHarness policy = new();

    public AgentHarnessDescriptor Descriptor { get; } = new ProgrammingAgentHarness().Descriptor with
    {
        Version = Version,
        Description = "Develop one explicit .NET project with verified receipts and consume an exact engine-owned rejection of a never-dispatched prepared command."
    };

    public bool CanRejectPrepared(AgentHarnessSnapshot snapshot) =>
        ProgrammingAgentHarness.CanRejectPreparedForIntervention(snapshot);

    public WorkflowStep Advance(AgentHarnessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ProgrammingAgentHarness.IsPreparedInterventionEdition(snapshot))
            return ProgrammingAgentHarness.PreparedInterventionFailure(snapshot,
                "Prepared intervention requires the exact Develop " + Version + " edition.");
        if (snapshot.Effect?.State == WorkflowEffectState.RejectedBeforeDispatch &&
            snapshot.Signal.Kind != WorkflowSignalKind.PreparedRejected)
            return ProgrammingAgentHarness.PreparedInterventionFailure(snapshot,
                "A rejected prepared command requires its engine-owned rejection signal.");
        return snapshot.Signal.Kind == WorkflowSignalKind.PreparedRejected
            ? ProgrammingAgentHarness.AdvancePreparedIntervention(snapshot) : policy.Advance(snapshot);
    }
}

public sealed partial class ProgrammingAgentHarness
{
    // Avoid depending on static-field initialization order across partial declarations.
    private static readonly JsonSerializerOptions PreparedSignalJson = new(CerebralJson.Options)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static bool IsPreparedInterventionEdition(AgentHarnessSnapshot snapshot) =>
        snapshot.Submission.DefinitionId == Id &&
        snapshot.Submission.DefinitionVersion == ProgrammingPreparedInterventionHarness.Version;

    internal static WorkflowStep PreparedInterventionFailure(AgentHarnessSnapshot snapshot, string reason) =>
        new(snapshot.State.Clone(), new CompleteWorkflow(Element(new { summary = reason }), false, reason));

    internal static bool CanRejectPreparedForIntervention(AgentHarnessSnapshot snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            var (_, state) = MatchPreparedIntervention(snapshot, WorkflowEffectState.Prepared);
            if (snapshot.Effect!.Error is not null) return false;
            _ = PendingOwnerSteering(snapshot, state);
            return true;
        }
        catch (Exception error) when (ProgrammingEvidence.Invalid(error)) { return false; }
    }

    private static HarnessState ReadPreparedState(AgentHarnessSnapshot snapshot)
    {
        if (snapshot.State.ValueKind != JsonValueKind.Object || !snapshot.State.TryGetProperty("phase", out _))
            throw new ArgumentException("The saved prepared-command state is missing.");
        ProgrammingEvidence.Unique(snapshot.State);
        var state = snapshot.State.Deserialize<HarnessState>(PreparedSignalJson)
            ?? throw new ArgumentException("The saved prepared-command state is missing.");
        if (state.Stages is not null || state.Contributions is null || state.Checks is null ||
            state.ReadReceipts is null || state.ReadSignatures is null || state.ReceiptPages is null ||
            state.RepeatedBuildFailures is null || state.ReadProjectionReferences is null || state.ReadHistory is null)
            throw new ArgumentException("The saved Develop continuation is inconsistent.");
        return state;
    }

    // This is a policy capability check, not authorization or proof about the receipt store.
    // Core owns the idle lease, exact revision/hash, receipt absence and dispatch boundary.
    private static (Input Input, HarnessState State) MatchPreparedIntervention(
        AgentHarnessSnapshot snapshot, WorkflowEffectState expectedState)
    {
        if (!IsPreparedInterventionEdition(snapshot) || snapshot.Run.IsTerminal ||
            snapshot.Run.CancelRequested || snapshot.Run.Status == WorkflowStatus.Cancelling ||
            snapshot.Effect is not { } effect || effect.State != expectedState ||
            string.IsNullOrWhiteSpace(effect.Id) || effect.CommandVersion != 1 ||
            effect.Result is not null || effect.ProviderReceiptId is not null ||
            effect.ReservedExecutionMilliseconds != 0 || effect.FailureBudgetKey is not null)
            throw new ArgumentException("The exact never-dispatched Develop command is unavailable.");
        var input = ReadInput(snapshot.Submission.Input);
        var state = ReadPreparedState(snapshot);
        ProgrammingEvidence.Unique(effect.Arguments);
        JsonElement expected;
        switch (state.Phase)
        {
            case "tool" when effect.CommandId == Tool && state.PendingName is not null &&
                state.PendingArguments is { ValueKind: JsonValueKind.Object } arguments:
                expected = Element(new { name = state.PendingName, arguments, workingDirectory = input.Root });
                break;
            case "model" when effect.CommandId == Generate:
            case "compact" when effect.CommandId == Compact:
                expected = GenerationArguments(input, state);
                break;
            case "receipt" when effect.CommandId == Receipt && state.PendingReceipt is { } request &&
                state.ReceiptPages.Count != 0 && state.ReceiptPages[^1] == request:
                expected = ReceiptArguments(request);
                break;
            default:
                throw new ArgumentException("The command does not match its saved pending phase.");
        }
        if (!JsonElement.DeepEquals(effect.Arguments, expected))
            throw new ArgumentException("The exact command arguments differ from the saved pending intent.");
        return (input, state);
    }

    internal static WorkflowStep AdvancePreparedIntervention(AgentHarnessSnapshot snapshot)
    {
        try
        {
            var (input, state) = MatchPreparedIntervention(snapshot, WorkflowEffectState.RejectedBeforeDispatch);
            if (snapshot.Signal.Data.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The prepared-rejection signal is missing.");
            ProgrammingEvidence.Unique(snapshot.Signal.Data);
            var signal = snapshot.Signal.Data.Deserialize<WorkflowPreparedRejectionSignal>(PreparedSignalJson)
                ?? throw new ArgumentException("The prepared-rejection signal is missing.");
            ValidateOwnerText(signal.Reason, required: true);
            var effect = snapshot.Effect!;
            if (signal.EffectId != effect.Id || effect.Error != signal.Reason)
                throw new ArgumentException("The rejection signal does not identify this exact rejected command.");

            var steering = PendingOwnerSteering(snapshot, state);
            if (steering.Count(entry => entry.RequestId == signal.SteeringRequestId) != 1)
                throw new ArgumentException("The rejection has no matching unincorporated owner correction.");
            var records = snapshot.Run.PreparedInterventions;
            if (records is null || records.Count > 16 || records.Any(record => record is null))
                throw new ArgumentException("The prepared-rejection audit is missing or invalid.");
            var matches = records.Where(record => record.RequestId == signal.SteeringRequestId).ToArray();
            // Hash the original prepared tuple. The rejected marker/reason are engine audit,
            // never a synthetic failed result, provider receipt or failure-budget reset.
            var original = effect with { State = WorkflowEffectState.Prepared, Error = null };
            if (matches.Length != 1 || matches[0] is not { Kind: WorkflowPreparedInterventionKind.Reject,
                    BudgetKey: null, Consumed: false } record ||
                record.EffectId != effect.Id || record.EffectSha256 != WorkflowPreparedEffectIdentity.Sha256(original) ||
                record.AppliedRevision < 1 || record.AppliedRevision > snapshot.Run.Revision ||
                !PreparedDigest(record.RequestSha256))
                throw new ArgumentException("The rejected command does not match its exact engine audit tuple.");

            var next = state with
            {
                Phase = "model", PendingName = null, PendingArguments = null, PendingCheck = null,
                PendingReceipt = null, CompactionReason = null,
                // This page was requested, never read. Retain every earlier page observation.
                ReceiptPages = state.Phase == "receipt" ? state.ReceiptPages.Take(state.ReceiptPages.Count - 1).ToArray() : state.ReceiptPages
            };
            return ReconsiderOwnerSteering(input, next, steering);
        }
        catch (Exception error) when (ProgrammingEvidence.Invalid(error))
        {
            return PreparedInterventionFailure(snapshot,
                "The prepared rejection could not be adopted: " + error.Message);
        }
    }

    private static bool PreparedDigest(string? value) => value is { Length: 64 } &&
        value.All(character => char.IsAsciiHexDigit(character) && !char.IsUpper(character));
}
