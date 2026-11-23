using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumProjectedToolOutcomes = 32;
    private const int MaximumSavedReadHandles = 16;
    private const int MaximumSavedReadMetadataBytes = 4 * 1024;

    private static JsonElement ToolReceiptProjection(Input input, HarnessState state)
    {
        // Projection never changes the indexed ledger used by completion, generation plans,
        // native repair ordering, or exact receipt retrieval.
        var indexed = state.ToolReceipts.Select((receipt, index) => (receipt, index)).ToArray();
        var groups = indexed.GroupBy(item => item.receipt.Name, StringComparer.Ordinal)
            .Select(group => new { latest = group.Last(), receiptCount = group.Count() }).ToArray();
        var lastNonSuccess = indexed.LastOrDefault(item =>
            item.receipt.State != nameof(WorkflowEffectState.Succeeded) ||
            item.receipt.Outcome is not (null or nameof(ToolExecutionOutcome.Succeeded)) ||
            item.receipt.ExitCode is not (null or 0));
        var required = input.RequiredSuccessfulTools.Concat(state.CompletionMilestones.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var projected = groups.OrderByDescending(group => required.Contains(group.latest.receipt.Name))
            .ThenByDescending(group => group.latest.receipt.Name == "process.run")
            .ThenByDescending(group => group.latest.receipt.Name == lastNonSuccess.receipt?.Name)
            .ThenByDescending(group => group.latest.index)
            .Take(MaximumProjectedToolOutcomes).ToArray();

        // Count unique displayed receipt records; the diagnostic may already be the latest
        // for its tool. A later invocation's success does not establish repair of this one.
        var displayed = projected.Select(group => group.latest.index).ToHashSet();
        if (lastNonSuccess.receipt is not null) displayed.Add(lastNonSuccess.index);
        // ToolReceipts is this run's ledger, not earlier conversation history. Keep exact
        // saved handles visible even after their live body contributions were compacted away.
        // Only the two verified ordinary read contracts are selected; no prefix/type inference.
        var savedReads = indexed.Where(item =>
                (item.receipt.Name is "file.read" or "file.list") &&
                item.receipt.State == nameof(WorkflowEffectState.Succeeded) &&
                (item.receipt.Outcome is null or nameof(ToolExecutionOutcome.Succeeded)))
            .Reverse().ToArray();
        var visibleReads = savedReads.Take(MaximumSavedReadHandles).ToArray();
        JsonElement savedReadReceipts;
        while (true)
        {
            savedReadReceipts = Element(new
            {
                scope = "current-run", referenceOnly = true,
                totalReceipts = savedReads.Length, displayedReceipts = visibleReads.Length,
                omittedReceipts = savedReads.Length - visibleReads.Length,
                receipts = visibleReads.Select(item => new
                {
                    item.receipt.EffectId, item.receipt.Name, item.receipt.State,
                    item.receipt.Outcome, item.receipt.Truncated
                }).ToArray(),
                observation = "Saved observations only; not current file bytes, new execution or a permission grant."
            });
            // Count the complete index and its property wrapper, including scope, flags and counters.
            if (JsonSerializer.SerializeToUtf8Bytes(new { savedReadReceipts }, Json).Length <= MaximumSavedReadMetadataBytes)
                break;
            if (visibleReads.Length == 0)
                throw new ArgumentException("Saved read-receipt metadata exceeds its bounded projection.");
            // Drop the oldest whole entry. Never shorten an effect identity to fit.
            visibleReads = visibleReads[..^1];
        }
        foreach (var item in visibleReads) displayed.Add(item.index);
        return Element(new
        {
            totalReceipts = indexed.Length,
            distinctTools = groups.Length,
            omittedReceipts = indexed.Length - displayed.Count,
            omittedTools = groups.Length - projected.Length,
            savedReadReceipts,
            latestByTool = projected.Select(group => new
            {
                group.receiptCount,
                latest = group.latest.receipt
            }).ToArray(),
            lastNonSuccess = lastNonSuccess.receipt is null ? null : new
            {
                receipt = lastNonSuccess.receipt,
                isLatestForTool = groups.Single(group =>
                    group.latest.receipt.Name == lastNonSuccess.receipt.Name).latest.index == lastNonSuccess.index
            }
        });
    }
}
