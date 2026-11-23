using System.Text;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumReceiptReadAttempts = 64;
    private const int MaximumReceiptRequestedBytes = 512 * 1024;

    private sealed record ReceiptPageObservation(string RunId, string LookupEffectId, long RunRevision, WorkflowEffectState State,
        string? ProviderReceiptId, int TotalBytes, int Bytes, int? NextOffset);

    private static bool ConfirmedReceipt(WorkflowEffectState state) =>
        state is WorkflowEffectState.Succeeded or WorkflowEffectState.Failed;

    private static HarnessState RestoreReceiptReadAllowance(HarnessState state, string runId)
    {
        if (state.ReceiptPages is null || state.ReceiptPages.Count > MaximumReceiptReadAttempts)
            throw new ArgumentException("Invalid saved receipt read ledger.");
        var requested = 0;
        foreach (var request in state.ReceiptPages)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.EffectId) || request.EffectId.Length > 128 ||
                request.Offset < 0 || request.ExpectedRevision is < 1 ||
                request.Limit is { } limit && (limit < 4 || limit > WorkflowReceiptPage.MaximumPageBytes))
                throw new ArgumentException("Invalid saved receipt request.");
            requested = checked(requested + (request.Limit ?? PageBytes));
            if (request.Observation is not { } page) continue;
            var end = (long)request.Offset + page.Bytes;
            if (request.Failed || page.RunId != runId || string.IsNullOrWhiteSpace(page.LookupEffectId) || page.LookupEffectId.Length > 128 ||
                page.RunRevision < 1 || !Enum.IsDefined(page.State) ||
                request.ExpectedRevision is { } revision && page.RunRevision != revision ||
                page.Bytes < 0 || page.Bytes > (request.Limit ?? PageBytes) || page.TotalBytes < end ||
                page.NextOffset is { } next && (next != end || next <= request.Offset || next >= page.TotalBytes) ||
                page.NextOffset is null && end != page.TotalBytes)
                throw new ArgumentException("Invalid saved receipt progress or run identity.");
        }
        // Old states retain only requested coordinates. Charge every retained legacy window
        // conservatively at its original 4 KiB limit; never infer successful reads or retry credit.
        if (state.ReceiptReadAccountingVersion == 0)
        {
            if (state.ReceiptPages.Count > MaximumLookups || state.ReceiptReadAttempts != 0 || state.ReceiptReadRequestedBytes != 0 ||
                state.ReceiptPages.Any(request => request.Limit is not null || request.Observation is not null || request.Failed))
                throw new ArgumentException("A receipt accounting marker is missing.");
            if (state.ReceiptPages.Count == 0) return state;
            state = state with { ReceiptReadAccountingVersion = 1,
                ReceiptReadAttempts = state.ReceiptPages.Count, ReceiptReadRequestedBytes = requested };
        }
        if (state.ReceiptReadAccountingVersion != 1 ||
            state.ReceiptReadAttempts < state.ReceiptPages.Count || state.ReceiptReadAttempts > MaximumReceiptReadAttempts ||
            state.ReceiptReadRequestedBytes != requested || requested > MaximumReceiptRequestedBytes)
            throw new ArgumentException("Invalid saved receipt allowance.");
        if (state.PendingReceipt is { } pending)
        {
            if (state.ReceiptPages.Count == 0 || state.ReceiptPages[^1] != pending ||
                state.PendingArguments is not { } arguments ||
                !arguments.TryGetProperty("effectId", out var id) || id.GetString() != pending.EffectId ||
                !arguments.TryGetProperty("offset", out var offset) || offset.GetInt32() != pending.Offset ||
                !arguments.TryGetProperty("limit", out var limit) || limit.GetInt32() != (pending.Limit ?? PageBytes))
                throw new ArgumentException("The pending receipt does not match its admitted coordinate or requested-window charge.");
            var pinned = arguments.TryGetProperty("expectedRevision", out var revision);
            if (pinned != pending.ExpectedRevision.HasValue || pinned && revision.GetInt64() != pending.ExpectedRevision)
                throw new ArgumentException("The pending receipt revision pin changed.");
        }
        return state;
    }

    private static WorkflowStep? AccountReceiptAction(ref HarnessState state, System.Text.Json.JsonElement action)
    {
        if (action.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !action.TryGetProperty("action", out var kind) || kind.ValueKind != System.Text.Json.JsonValueKind.String ||
            kind.GetString() != "receipt") return null;
        if (state.ReceiptReadAttempts >= MaximumReceiptReadAttempts)
            return Finish(state, false, "conversation.receipt_attempts_exhausted: The per-run receipt lookup allowance is exhausted. " +
                "Use the retained evidence or start a fresh bounded request; unrelated tools do not reset this allowance.");
        // Count recognizable receipt proposals before schema/semantic validation. Refusals,
        // malformed fields and no-progress corrections consume attempts, even without host I/O.
        state = state with { ReceiptReadAccountingVersion = 1,
            ReceiptReadAttempts = state.ReceiptReadAttempts + 1 };
        return null;
    }

    private static string? ReceiptReadCorrection(HarnessState state, ReceiptRequest request, out int limit)
    {
        limit = Math.Min(WorkflowReceiptPage.MaximumPageBytes,
            MaximumReceiptRequestedBytes - state.ReceiptReadRequestedBytes);
        if (limit < 4)
            return "conversation.receipt_requested_bytes_exhausted: The 512 KiB per-run requested data-window allowance is exhausted. " +
                "Use the retained evidence or start a fresh request focused on the needed diagnostic. This is not a model token limit.";
        var same = state.ReceiptPages.Where(page => page.EffectId == request.EffectId).ToArray();
        var previous = same.LastOrDefault(page => page.Observation is not null)?.Observation;
        if (previous is null)
            return same.Any(page => page.Offset == request.Offset && page.ExpectedRevision == request.ExpectedRevision)
                ? "conversation.receipt_no_progress: This receipt coordinate was already requested. Use its retained result or a corrected revision."
                : null;
        if (ConfirmedReceipt(previous.State))
        {
            var confirmedStart = Array.FindIndex(same, page => page.Observation is { } observed && ConfirmedReceipt(observed.State));
            if (same.Skip(confirmedStart).Any(page => page.Offset == request.Offset))
                return "conversation.receipt_no_progress: This immutable receipt range was already requested. Use the retained page.";
            if (previous.NextOffset is null)
                return "conversation.receipt_complete: The retained page completed this immutable receipt (nextOffset is null). " +
                    "Use that page; there is no further receipt offset.";
            return previous.NextOffset == request.Offset ? null :
                $"conversation.receipt_offset: Continue this receipt at its advertised nextOffset {previous.NextOffset.Value}.";
        }
        if (request.Offset == 0)
        {
            if (request.ExpectedRevision > previous.RunRevision ||
                request.ExpectedRevision is null && same[^1] is { Failed: true, Offset: > 0 })
                return null;
            return "conversation.receipt_no_progress: Restart an uncertain receipt at zero only for a newer revision, " +
                "or after a failed continuation. An unchanged uncertain result is not new execution evidence.";
        }
        if (previous.NextOffset is null)
            return "conversation.receipt_complete: This uncertain receipt page has no continuation (nextOffset is null). " +
                "Use the retained page, or restart at offset zero only after a newer revision.";
        if (previous.NextOffset != request.Offset)
            return $"conversation.receipt_offset: Continue this uncertain receipt at its advertised nextOffset {previous.NextOffset.Value}.";
        if (request.ExpectedRevision != previous.RunRevision)
            return "conversation.receipt_revision: Continue an uncertain receipt with expectedRevision from its last page. " +
                "If the revision changed, restart at offset zero.";
        return same.Any(page => page.Offset == request.Offset && page.ExpectedRevision == request.ExpectedRevision)
            ? "conversation.receipt_no_progress: This uncertain receipt coordinate was already requested. Do not retry it unchanged."
            : null;
    }

    private static HarnessState ReleaseConsumedReceiptPages(HarnessState state, ReceiptRequest request)
    {
        // A valid forward cursor (or admitted newer uncertain revision) is a model decision
        // consuming this receipt's earlier pages. Retire only those read projections, never
        // another receipt, history input or the actual tool outcome/provenance anchors.
        var ids = state.ReceiptPages.Where(page => page.EffectId == request.EffectId && page.Observation is not null)
            .Select(page => "receipt." + page.Observation!.LookupEffectId).ToHashSet(StringComparer.Ordinal);
        return ids.Count == 0 ? state : ReleaseConsumedReadReferences(state, ids);
    }

    private static HarnessState ObserveReceiptRead(HarnessState state, WorkflowReceiptPage? page, string lookupEffectId)
    {
        var request = state.PendingReceipt ?? throw new ArgumentException("No receipt request is pending.");
        if (state.ReceiptPages.Count == 0 || state.ReceiptPages[^1] != request)
            throw new ArgumentException("The receipt ledger does not match the pending request.");
        var previous = state.ReceiptPages.Take(state.ReceiptPages.Count - 1)
            .LastOrDefault(prior => prior.EffectId == request.EffectId && prior.Observation is not null)?.Observation;
        if (page is not null && previous is not null)
        {
            if (page.RunRevision < previous.RunRevision)
                throw new ArgumentException("The receipt revision moved backwards.");
            if (ConfirmedReceipt(previous.State))
            {
                if (page.State != previous.State || page.ProviderReceiptId != previous.ProviderReceiptId ||
                    page.TotalBytes != previous.TotalBytes)
                    throw new ArgumentException("An immutable receipt changed its outcome, identity or total byte length.");
            }
            else if (request.Offset != 0 && (page.RunRevision != previous.RunRevision ||
                         page.State != previous.State || page.ProviderReceiptId != previous.ProviderReceiptId ||
                         page.TotalBytes != previous.TotalBytes))
                throw new ArgumentException("An uncertain receipt changed during continuation; restart at zero.");
            else if (request.Offset == 0 && page.RunRevision <= previous.RunRevision)
                throw new ArgumentException("The uncertain receipt restart made no revision progress.");
        }
        var observed = request with { Failed = page is null, Observation = page is null ? null :
            new(page.RunId, lookupEffectId, page.RunRevision, page.State, page.ProviderReceiptId,
                page.TotalBytes, Encoding.UTF8.GetByteCount(page.Json), page.NextOffset) };
        return state with { ReceiptPages = [.. state.ReceiptPages.Take(state.ReceiptPages.Count - 1), observed] };
    }
}
