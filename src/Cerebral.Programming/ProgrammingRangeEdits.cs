using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private const string RangeEdit = "file.edit-range";

    // All confirmed source mutations have the same verification/context consequences.
    private static bool IsSourceMutation(string name) => name is "file.write" or "file.edit" or RangeEdit;

    private static void ValidateRangeEditArguments(JsonElement arguments)
    {
        ProgrammingMutationProof.ValidateRangeArguments(arguments);
        // The Tools boundary alone resolves physical lines, encoding, EOF and byte-level no-op
        // admission against the pinned file. This pure policy never rereads mutable source.
    }

    private static bool RangeOutcome(WorkflowEffect effect, ToolData? data, ToolExecutionOutcome outcome) =>
        data is { FollowUpRequired: false, Truncated: false, Evidence: { Partial: false } evidence } &&
        evidence.Outcome == outcome &&
        !string.IsNullOrWhiteSpace(evidence.ProviderId) && evidence.ProviderId.Length <= 256 &&
        !evidence.ProviderId.Any(char.IsControl) &&
        (evidence.RequestId is null || string.Equals(evidence.RequestId, effect.Id, StringComparison.Ordinal));

    private static bool KnownRejectedRangeEdit(WorkflowEffect effect, ToolData? data) =>
        effect.State == WorkflowEffectState.Failed && RangeOutcome(effect, data, ToolExecutionOutcome.Rejected);

    private static WorkflowStep UnconfirmedRangeEdit(HarnessState state, WorkflowEffect effect) =>
        Finish(state, false, "The file.edit-range outcome is unconfirmed. Inspect its exact receipt " +
            effect.Id + " before any further mutation; no edit credit or automatic replay was granted.");

    private static bool ConfirmsRangeEdit(WorkflowEffect effect, ToolData? data, JsonElement arguments)
        => data is not null && ProgrammingMutationProof.Read(RangeEdit, arguments, effect.State, effect.Id,
            data.Content, data.Truncated, data.FollowUpRequired, data.Evidence) is not null;

    private static bool IsRangeHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}

