using Cerebral.Contracts.Workflows;
using Cerebral.Contracts.Context;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // Only fresh runs opt in. A missing marker on an older saved run preserves its completion contract.
    private sealed record RequiredReadDisplayState(int SchemaVersion, string RunId, IReadOnlyList<string> ClippedEffectIds,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        bool AutomaticPaging = false);

    private static void ValidateRequiredReadDisplays(HarnessState state, string runId)
    {
        if (state.RequiredReadDisplays is not { } displays) return;
        if (displays.SchemaVersion != 1 || displays.RunId != runId || displays.ClippedEffectIds is null ||
            displays.ClippedEffectIds.Count > state.ToolReceipts.Count ||
            displays.ClippedEffectIds.Distinct(StringComparer.Ordinal).Count() != displays.ClippedEffectIds.Count ||
            displays.ClippedEffectIds.Any(id => string.IsNullOrWhiteSpace(id) ||
                !state.ToolReceipts.Any(receipt => receipt.EffectId == id && receipt.Name == "file.read" &&
                    receipt.State == nameof(WorkflowEffectState.Succeeded) && receipt.Outcome is null or "Succeeded")))
            throw new ArgumentException("Invalid saved required-read display state or effect identity.");
    }

    private static string? RequiredReadDisplayDiagnostic(HarnessState state)
    {
        if (state.RequiredReadDisplays is not { } displays) return null;
        foreach (var effectId in displays.ClippedEffectIds)
        {
            var offset = RequiredReadReceiptOffset(state, displays.RunId, effectId);
            if (offset is null) continue;
            return "conversation.required_read_display_incomplete: A required file.read succeeded, but its returned payload " +
                "was clipped before model display. Read the existing receipt for effectId " + effectId +
                " with offset " + offset.Value + " using the receipt action and continue its advertised nextOffset until complete. " +
                "This covers that effect's returned payload; it does not require reading the whole file. " +
                "Another successful file.read cannot clear this pending evidence. Use blocked if the evidence cannot be completed.";
        }
        return null;
    }

    private static WorkflowStep? ContinueRequiredReadDisplay(Input input, HarnessState state)
    {
        if (state.RequiredReadDisplays is not { AutomaticPaging: true } displays) return null;
        foreach (var effectId in displays.ClippedEffectIds)
        {
            if (RequiredReadReceiptOffset(state, displays.RunId, effectId) is not { } offset) continue;
            // A real failed lookup returns to the model with its evidence; never replay it automatically.
            if (state.ReceiptPages.LastOrDefault(request => request.EffectId == effectId) is { Failed: true }) return null;
            var action = Element(new { action = "receipt", effectId, offset });
            if (AccountReceiptAction(ref state, action) is { } exhausted) return exhausted;
            // Use the existing run-owned, immutable receipt route and its byte/attempt/state bounds.
            // Keep all pages required until a generation can consume them together.
            return RequestReceipt(input, state, action, automatic: true);
        }
        return null;
    }

    private static bool RequiredReadDisplayProjectionIncluded(HarnessState state, ContextProjection projection)
    {
        if (state.RequiredReadDisplays is not { AutomaticPaging: true } displays) return true;
        var sources = state.ReceiptPages.Where(request => displays.ClippedEffectIds.Contains(request.EffectId, StringComparer.Ordinal) &&
                request.Observation is not null)
            .Select(request => "receipt." + request.Observation!.LookupEffectId).ToHashSet(StringComparer.Ordinal);
        // Fetching is not delivery. Before accepting a model decision, require the host's exact
        // generation projection to confirm these still-required receipt pages and revisions.
        return state.Contributions.Where(source => sources.Contains(source.Id) && source.Requirement == ContextRequirement.Required)
            .All(source => projection.IncludedSources is not null && projection.IncludedSources.Any(included =>
                included.Id == source.Id && included.Revision == source.SourceRevision));
    }

    // Receipt pages are admitted with exact UTF-8 boundaries and appended intact as required model input.
    // Credit only a contiguous, confirmed sequence belonging to this original effect and run.
    private static int? RequiredReadReceiptOffset(HarnessState state, string runId, string effectId)
    {
        var offset = 0;
        ReceiptPageObservation? identity = null;
        foreach (var request in state.ReceiptPages.Where(request => request.EffectId == effectId))
        {
            if (request.Failed || request.Observation is not { } page) continue;
            if (page.RunId != runId || page.State != WorkflowEffectState.Succeeded || request.Offset != offset)
                return offset;
            if (identity is not null && (page.ProviderReceiptId != identity.ProviderReceiptId || page.TotalBytes != identity.TotalBytes))
                return offset;
            identity ??= page;
            var end = (long)offset + page.Bytes;
            if (page.NextOffset is null)
                return end == page.TotalBytes ? null : offset;
            if (end != page.NextOffset || end <= offset || end >= page.TotalBytes) return offset;
            offset = page.NextOffset.Value;
        }
        return offset;
    }
}
