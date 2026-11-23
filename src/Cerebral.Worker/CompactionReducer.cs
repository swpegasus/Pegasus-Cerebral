using Cerebral.Core;

namespace Cerebral.Worker;

public sealed record CompactionReductionResult(string Checkpoint, int Passes, int RequestedResponseTokens, int UsedResponseTokens);

/// <summary>
/// Reduces saved conversation material through bounded rolling passes. Source blocks are split only for model input;
/// the original session messages remain unchanged in the service store.
/// </summary>
public static class CompactionReducer
{
    private const int MinimumStageResponseTokens = 64;

    public static int TargetResponseTokens(AgentProfile owner, AgentProfile compactor, int sourceTokens)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(compactor);
        if (sourceTokens < 1) throw new ArgumentOutOfRangeException(nameof(sourceTokens));
        var reduction = AgentTokenLimits.ValidateCompactionReduction(owner.CompactionReductionPercent,
            nameof(owner.CompactionReductionPercent));
        var retainedPercent = 100 - reduction;
        var sourceTarget = Math.Max(AgentTokenLimits.MinimumResponseTokens,
            (int)Math.Ceiling((long)sourceTokens * retainedPercent / 100d));
        var ownerTarget = Math.Max(AgentTokenLimits.MinimumResponseTokens,
            (int)Math.Ceiling((long)owner.ContextTokens * retainedPercent / 100d));
        var compactorCapacity = Math.Min(compactor.ResponseTokens,
            AgentTokenLimits.MaximumVisibleResponse(compactor.ContextTokens, compactor.ReasoningMode, compactor.ReasoningTokens));
        if (compactorCapacity < AgentTokenLimits.MinimumResponseTokens)
            throw new InvalidOperationException("The selected compactor's context and reasoning reserve leave no reliable checkpoint response capacity.");
        return Math.Max(AgentTokenLimits.MinimumResponseTokens,
            Math.Min(Math.Min(sourceTarget, ownerTarget), compactorCapacity));
    }

    public static async Task<CompactionReductionResult> ReduceAsync(AgentProfile compactor, string instruction,
        IEnumerable<string> sourceBlocks, int requestedResponseTokens,
        Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens,
        Func<IReadOnlyList<ModelMessage>, int, CancellationToken, Task<string>> summarize,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(compactor);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        ArgumentNullException.ThrowIfNull(sourceBlocks);
        ArgumentNullException.ThrowIfNull(countTokens);
        ArgumentNullException.ThrowIfNull(summarize);
        if (requestedResponseTokens is < AgentTokenLimits.MinimumResponseTokens || requestedResponseTokens > compactor.ResponseTokens)
            throw new ArgumentOutOfRangeException(nameof(requestedResponseTokens), "The checkpoint response target must fit the selected compactor's configured response budget.");

        var pending = new LinkedList<string>(sourceBlocks.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (pending.Count == 0) throw new InvalidOperationException("There is no saved material to compact.");
        var totalCharacters = pending.Sum(x => (long)x.Length);
        var maximumPasses = (int)Math.Clamp(totalCharacters + pending.Count * 4L + 32, 64, 8192);
        var summary = "";
        var activeResponseTokens = requestedResponseTokens;
        var passes = 0;

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var batch = new List<string>();
            while (pending.First is { } first)
            {
                var block = first.Value;
                if (await FitsAsync(compactor, instruction, summary, batch.Append(block), activeResponseTokens, countTokens, token))
                {
                    batch.Add(block);
                    pending.RemoveFirst();
                    continue;
                }

                var prefixLength = await LargestFittingPrefixAsync(compactor, instruction, summary, batch, block,
                    activeResponseTokens, countTokens, token);
                if (prefixLength > 0)
                {
                    batch.Add(block[..prefixLength]);
                    if (prefixLength == block.Length) pending.RemoveFirst();
                    else first.Value = block[prefixLength..];
                }
                break;
            }

            if (batch.Count == 0)
            {
                if (summary.Length == 0)
                    throw new InvalidOperationException("The selected compactor's context, instructions, reasoning reserve and response budget leave no room for staged source input. Increase its context or lower its response/reasoning budget.");
                var smaller = Math.Max(MinimumStageResponseTokens, activeResponseTokens / 2);
                if (smaller == activeResponseTokens)
                    throw new InvalidOperationException("The selected compactor cannot combine its staged checkpoint with more source material. Increase its context or lower its response/reasoning budget.");
                var shrinkMessages = BuildMessages(instruction, "", ["Earlier staged brief to reduce before continuing:\n" + summary]);
                if (!await FitsMessagesAsync(compactor, shrinkMessages, smaller, countTokens, token))
                    throw new InvalidOperationException("The selected compactor cannot reprocess its own staged checkpoint. Increase its context or shorten its compactor instructions.");
                summary = await GenerateAsync(shrinkMessages, smaller);
                activeResponseTokens = smaller;
                continue;
            }

            var messages = BuildMessages(instruction, summary, batch);
            summary = await GenerateAsync(messages, activeResponseTokens);
        }

        return new(summary, passes, requestedResponseTokens, activeResponseTokens);

        async Task<string> GenerateAsync(IReadOnlyList<ModelMessage> messages, int maxTokens)
        {
            if (++passes > maximumPasses)
                throw new InvalidOperationException("Compaction exceeded its bounded staged-pass limit. Select a compactor with more context or reduce the saved source set.");
            var result = (await summarize(messages, maxTokens, token)).Trim();
            if (result.Length == 0) throw new InvalidDataException("Compactor returned an empty checkpoint.");
            return result;
        }
    }

    private static async Task<int> LargestFittingPrefixAsync(AgentProfile profile, string instruction, string summary,
        IReadOnlyList<string> batch, string block, int responseTokens,
        Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens, CancellationToken token)
    {
        var low = 1;
        var high = block.Length;
        var best = 0;
        while (low <= high)
        {
            var rawMiddle = low + (high - low) / 2;
            var middle = SafeBoundary(block, rawMiddle);
            if (middle < low) { low = rawMiddle + 1; continue; }
            if (await FitsAsync(profile, instruction, summary, batch.Append(block[..middle]), responseTokens, countTokens, token))
            {
                best = middle;
                low = rawMiddle + 1;
            }
            else high = rawMiddle - 1;
        }
        return SafeBoundary(block, best);
    }

    private static int SafeBoundary(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        if (index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index])) index--;
        return index;
    }

    private static Task<bool> FitsAsync(AgentProfile profile, string instruction, string summary,
        IEnumerable<string> blocks, int responseTokens,
        Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens, CancellationToken token) =>
        FitsMessagesAsync(profile, BuildMessages(instruction, summary, blocks), responseTokens, countTokens, token);

    private static async Task<bool> FitsMessagesAsync(AgentProfile profile, IReadOnlyList<ModelMessage> messages,
        int responseTokens, Func<IReadOnlyList<ModelMessage>, CancellationToken, Task<int>> countTokens, CancellationToken token)
    {
        var promptTokens = await countTokens(messages, token);
        var reasoningTokens = profile.ReasoningMode == "on" ? profile.ReasoningTokens : 0;
        return (long)promptTokens + responseTokens + reasoningTokens + AgentPrompt.ContextSafetyTokens <= profile.ContextTokens;
    }

    private static List<ModelMessage> BuildMessages(string instruction, string summary, IEnumerable<string> blocks)
    {
        var messages = new List<ModelMessage> { new("system", instruction) };
        if (!string.IsNullOrWhiteSpace(summary)) messages.Add(new("user", "Existing staged brief:\n" + summary));
        messages.AddRange(blocks.Select(x => new ModelMessage("user", x)));
        return messages;
    }
}
