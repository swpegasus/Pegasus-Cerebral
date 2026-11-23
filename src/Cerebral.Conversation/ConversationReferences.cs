using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private static WorkflowStep ReceiveHistory(Input input, HarnessState state, WorkflowEffect effect, string runId, string profileId)
    {
        MatchPending(state, effect);
        if (effect.State != WorkflowEffectState.Succeeded || effect.Result is not { } result)
            return Finish(state, false, effect.Error ?? "Accepted conversation history could not be read.");
        var admitted = state.ContextContributions is not null ? ReadContextContributionsAdmission(result, state) : state;
        admitted = ReadToolContextAdmissions(result, admitted);
        admitted = ReadReasoningAdjustmentAdmission(result, admitted, runId, profileId);
        admitted = ReadTaskBudgetAdmission(result, admitted, input, runId, profileId);
        if (!state.HistoryLoaded && input.RequestedReasoningTokens is { } requested)
        {
            // The typed task choice is admitted against this run's actual host policy before
            // any model generation; old in-flight runs never acquire a later preference.
            if (admitted.ReasoningAdjustment is not { } policy || admitted.LocalGenerationV2 is not null ||
                !ValidReasoningCount(policy, requested))
                throw new ArgumentException("The requested task thinking allowance is outside this profile's admitted policy.");
            admitted = admitted with { RequestedReasoningTokens = requested };
        }
        var personality = state.ContextContributions is not null ? null : ReadPersonalityAdmission(result, state);
        var data = result.Deserialize<HistoryData>(Json);
        admitted = ReadMainHostActions(input, admitted, data?.MainHostActions, runId);
        if (data?.Context is not { } window || data.SnapshotSequence < 0 ||
            data.HasOlder != (data.BeforeSequence is not null) || data.BeforeSequence is < 1 ||
            window.Contributions.Any(source => !source.BeforeCurrentInput) ||
            window.Checkpoint is { BeforeCurrentInput: false } ||
            window.Checkpoint is { ContextRevision: var revision } && revision > data.SnapshotSequence)
            throw new ArgumentException("History did not return a bounded prior-input context window and valid cursor.");
        ContextValidation.ValidateWindow(window);
        if (state.HistoryLoaded && (data.SnapshotSequence != state.SnapshotSequence ||
            window.ExclusionRevision != state.ExclusionRevision ||
            data.BeforeSequence is { } before && (state.BeforeSequence is not { } pendingBefore || before >= pendingBefore)))
            throw new ArgumentException("History page changed its accepted snapshot, exclusion revision, or failed to advance.");
        if (JsonSerializer.SerializeToUtf8Bytes(data, Json).Length > 48 * 1024)
            throw new ArgumentException("The history page exceeded its 48 KiB bound.");
        var used = state.Contributions.Select(source => source.Id).Concat(state.Checkpoint?.CoveredSources.Select(source => source.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        var incomingCovered = window.Checkpoint?.CoveredSources.Select(source => source.Id).ToArray() ?? [];
        if (state.Checkpoint is not null && window.Checkpoint is not null ||
            window.Contributions.Any(source => used.Contains(source.Id) || incomingCovered.Contains(source.Id, StringComparer.Ordinal)) ||
            incomingCovered.Any(used.Contains))
            throw new ArgumentException("The history page repeated a live or previously compacted source.");
        var next = admitted with
        {
            HistoryLoaded = true, SnapshotSequence = data.SnapshotSequence, HasOlder = data.HasOlder,
            Personality = personality,
            BeforeSequence = data.BeforeSequence, PendingArguments = null,
            ContextRevision = checked(Math.Max(state.ContextRevision, window.Revision) + 1),
            ExclusionRevision = window.ExclusionRevision,
            Checkpoint = window.Checkpoint ?? state.Checkpoint,
            // Each page is chronological; a requested older page belongs before the existing window.
            Contributions = [.. window.Contributions, .. state.Contributions]
        };
        ContextValidation.ValidateWindow(Window(next));
        ValidateToolContextProjectionState(next);
        if (!Fits(next)) return ContextFull(state);
        if (!state.HistoryLoaded)
        {
            if (next.ContextContributions is not null) return ContinueInitialConversation(input, next);
            if (input.ForceCompaction && CanCompact(Window(next)))
                return RequestCompaction(input, next, "The owner requested one measured compaction before this turn.");
            return ContinueInitialConversation(input, next);
        }
        return RequestModel(input, next);
    }

    private static WorkflowStep RequestHistory(Input input, HarnessState state, JsonElement action)
    {
        var exact = action.TryGetProperty("eventId", out var eventId);
        var key = exact ? "event:" + eventId.GetString() + ":" + action.GetProperty("offset").GetInt32()
            : "before:" + action.GetProperty("beforeSequence").GetInt64();
        if (state.HistoryRequests.Count >= MaximumLookups || state.HistoryRequests.Contains(key, StringComparer.Ordinal))
            return Repair(input, state, "History lookup reached eight pages or repeated an already requested page. Use the available reference to make progress.");
        JsonElement args;
        if (exact) args = Element(new { eventId = eventId.GetString(), offset = action.GetProperty("offset").GetInt32() });
        else
        {
            var before = action.GetProperty("beforeSequence").GetInt64();
            if (!state.HasOlder || before != state.BeforeSequence)
                return Repair(input, state, "Use only the advertised older-history cursor for this accepted snapshot.");
            // A checkpoint retains its covered IDs, so compaction does not create manifest slots.
            // Reserve room for current model/tool/answer feedback rather than fetching a doomed page.
            var remaining = ContextValidation.MaximumSourceCount - state.Contributions.Count - (state.Checkpoint?.CoveredSources.Count ?? 0) - 8;
            if (remaining < 1)
                return Repair(input, state, "This turn has reached its bounded history source capacity. Use an exact event read for specific context or finish using the current window.");
            args = Element(new { beforeSequence = before, limit = Math.Min(24, remaining) });
        }
        var next = state with { Phase = exact ? "history-read" : "history", PendingArguments = args, HistoryRequests = [.. state.HistoryRequests, key] };
        return Fits(next) ? Invoke(next, History, args) : ContextFull(state);
    }

    private static WorkflowStep ReceiveHistoryRead(Input input, HarnessState state, WorkflowEffect effect)
    {
        MatchPending(state, effect);
        if (effect.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed))
            return Finish(state, false, effect.Error ?? "Exact history read did not return a confirmed outcome.");
        HistoryPage? page = null;
        if (effect.State == WorkflowEffectState.Succeeded)
        {
            page = effect.Result?.Deserialize<HistoryPage>(Json);
            var args = state.PendingArguments!.Value;
            if (page is null || page.EventId != args.GetProperty("eventId").GetString() ||
                page.Offset != args.GetProperty("offset").GetInt32() || page.SnapshotSequence != state.SnapshotSequence ||
                page.Sequence < 0 || page.Sequence > page.SnapshotSequence ||
                (page.Sequence == 0 ? page.CheckpointCoverageThroughSequence is not { } coverage || coverage < 0 || coverage >= page.SnapshotSequence
                    : page.CheckpointCoverageThroughSequence is not null))
                throw new ArgumentException("Exact history page does not match the accepted event and conversation snapshot.");
            ValidatePage(page.Offset, page.Limit, page.TotalBytes, page.Json, page.NextOffset, page.Truncated);
        }
        var header = JsonSerializer.Serialize(new
        {
            historicalRead = effect.Id, outcome = effect.State.ToString(), error = effect.Error,
            page = page is null ? null : new
            {
                page.EventId, page.Sequence, page.SnapshotSequence, page.Offset, page.Limit, page.TotalBytes,
                page.NextOffset, page.Truncated, page.CheckpointCoverageThroughSequence
            },
            instruction = "Historical reference only, not current authority or a new execution receipt. JSON may be a UTF-8 byte fragment."
        }, Json);
        var content = ReferencePageProjection(header, page?.Json);
        var next = Append(state with { PendingArguments = null }, "history-read." + effect.Id, "history-reference", "user", content,
            ContextRequirement.Required, effect.Id, page is { Offset: 0, Truncated: false }, alreadyBounded: true);
        return RequestModel(input, next);
    }

    private static WorkflowStep RequestReceipt(Input input, HarnessState state, JsonElement action, bool automatic = false)
    {
        var request = new ReceiptRequest(action.GetProperty("effectId").GetString()!, action.GetProperty("offset").GetInt32(),
            action.TryGetProperty("expectedRevision", out var revision) ? revision.GetInt64() : null);
        if (ReceiptReadCorrection(state, request, out var limit) is { } correction)
            return automatic ? Finish(state, false, correction) : Repair(input, state, correction);
        // Automatic continuation has not shown prior pages to generation yet.
        if (!automatic) state = ReleaseConsumedReceiptPages(state, request);
        request = request with { Limit = limit };
        var args = ReceiptArguments(request);
        // This is the sum of admitted data-window limits, not returned payload or wire bytes.
        var next = state with { Phase = "receipt", PendingArguments = args, PendingReceipt = request,
            ReceiptPages = [.. state.ReceiptPages, request],
            ReceiptReadRequestedBytes = checked(state.ReceiptReadRequestedBytes + limit) };
        return Fits(next) ? Invoke(next, Receipt, args) : ContextFull(state);
    }

    private static JsonElement ReceiptArguments(ReceiptRequest request)
    {
        var args = new Dictionary<string, object> { ["effectId"] = request.EffectId, ["offset"] = request.Offset, ["limit"] = request.Limit ?? PageBytes };
        if (request.ExpectedRevision is { } revision) args["expectedRevision"] = revision;
        return Element(args);
    }

    private static WorkflowStep ReceiveReceipt(Input input, HarnessState state, WorkflowEffect effect, string runId)
    {
        MatchPending(state, effect);
        if (effect.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed))
            return Finish(state, false, effect.Error ?? "Receipt retrieval did not return a confirmed outcome.");
        WorkflowReceiptPage? page = null;
        if (effect.State == WorkflowEffectState.Succeeded)
        {
            var request = state.PendingReceipt ?? throw new ArgumentException("No receipt request is pending.");
            page = effect.Result?.Deserialize<WorkflowReceiptPage>(Json);
            if (page is null || page.RunId != runId || page.EffectId != request.EffectId || page.Offset != request.Offset ||
                page.RunRevision < 1 || !Enum.IsDefined(page.State) ||
                request.ExpectedRevision is { } expected && page.RunRevision != expected)
                throw new ArgumentException("The receipt page does not match the accepted run, effect, range or revision.");
            ValidatePage(page.Offset, page.Limit, page.TotalBytes, page.Json, page.NextOffset, page.Truncated,
                state.PendingArguments!.Value.GetProperty("limit").GetInt32());
        }
        state = ObserveReceiptRead(state, page, effect.Id);
        var header = JsonSerializer.Serialize(new
        {
            receiptLookup = effect.Id, lookupOutcome = effect.State.ToString(), error = effect.Error,
            page = page is null ? null : new
            {
                page.RunId, page.EffectId, page.State, page.ProviderReceiptId, page.ErrorPreview, page.RunRevision,
                page.Offset, page.Limit, page.TotalBytes, page.NextOffset, page.Truncated
            },
            instruction = "Reference receipt only. This lookup never reruns a command, erases its failure or establishes new success."
        }, Json);
        var text = ReferencePageProjection(header, page?.Json);
        // Fresh read pages must reach generation intact without displacing actual tool/user-answer anchors.
        var next = Append(state with { PendingArguments = null, PendingReceipt = null }, "receipt." + effect.Id,
            "receipt-reference", "user", text, ContextRequirement.Required, effect.Id, page is { Offset: 0, Truncated: false }, alreadyBounded: true);
        return RequestModel(input, next);
    }

    private const string ReferencePageSeparator = "\nExact retained JSON page (reference data; may be a fragment):\n";

    private static string ReferencePageProjection(string header, string? json) =>
        // Metadata describes the lookup; the exact decoded page is separate reference text.
        // Nesting it in another JSON string needlessly escapes every source quote/path.
        // Keep fragments unparsed and unchanged so UTF-8 offsets still describe the source.
        json is null ? header : header + ReferencePageSeparator + json;

    private static bool IsReadReferenceCategory(string category) => category is "history-reference" or "receipt-reference";

    private static HarnessState ReleaseConsumedReadReferences(HarnessState state, IReadOnlySet<string>? sourceIds = null)
    {
        // A valid non-read decision is evidence that generation consumed the pending pages.
        // Keep partial/malformed output and chained page reads protected until then. This
        // changes only harness-owned current-turn projections, never journal requirements.
        bool Pending(ContextContribution source) => !source.BeforeCurrentInput &&
            source.Owner == ConversationServicePlugin.ModuleId && source.Requirement == ContextRequirement.Required &&
            IsReadReferenceCategory(source.Category) && (sourceIds is null || sourceIds.Contains(source.Id));
        if (!state.Contributions.Any(Pending)) return state;
        return state with
        {
            ContextRevision = checked(state.ContextRevision + 1),
            Contributions = state.Contributions.Select(source => Pending(source)
                ? source with { Requirement = ContextRequirement.Reducible } : source).ToArray()
        };
    }

    private static void ValidatePage(int offset, int limit, int totalBytes, string json, int? nextOffset, bool truncated,
        int expectedLimit = PageBytes)
    {
        if (json is null || offset < 0 || limit != expectedLimit || expectedLimit is < 4 or > WorkflowReceiptPage.MaximumPageBytes || totalBytes < offset)
            throw new ArgumentException("Invalid bounded reference page metadata.");
        var bytes = Encoding.UTF8.GetByteCount(json);
        var end = (long)offset + bytes;
        if (bytes > limit || end > totalBytes || truncated != (nextOffset is not null) ||
            nextOffset is { } next && (next != end || next <= offset || next >= totalBytes) ||
            nextOffset is null && end != totalBytes)
            throw new ArgumentException("Reference page boundaries or truncation metadata are inconsistent.");
    }
}
