using System.Text;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class CompactionReducerTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }

        var owner = new AgentProfile { ContextTokens = 4096, ResponseTokens = 1024, CompactionReductionPercent = 50 };
        var compactor = new AgentProfile { ContextTokens = 4096, ResponseTokens = 700, ReasoningMode = "off", ReasoningTokens = 128 };
        Check(CompactionReducer.TargetResponseTokens(owner, compactor, 3000) == 700,
            "compaction target is capped by the selected compactor response budget");
        var legacy = CerebralJson.Read<AgentProfile>(CerebralJson.Element(new { id = "legacy", name = "Legacy", modelId = "model" }));
        Check(legacy.CompactionTriggerPercent == 80 &&
            CerebralJson.Read<ConfigurationBackupAgent>(CerebralJson.Element(new
                { id = "legacy", name = "Legacy", modelId = "model" })).CompactionTriggerPercent == 80,
            "legacy agent profiles and portable backups inherit the early 80-percent trigger");
        foreach (var trigger in new[] { 10, 80, 95 })
            Check(AgentTokenLimits.ValidateCompactionTrigger(trigger) == trigger,
                "compaction trigger admits its configured inclusive bounds");
        foreach (var trigger in new[] { 0, 9, 96, 101 })
        {
            var triggerRejected = false;
            try { AgentTokenLimits.ValidateCompactionTrigger(trigger); }
            catch (ArgumentOutOfRangeException) { triggerRejected = true; }
            Check(triggerRejected, "invalid configured compaction trigger is rejected");
        }
        Check(legacy.CompactionReductionPercent == 50,
            "profiles saved before reduction targeting inherit the 50 percent default");
        Check(CompactionReducer.TargetResponseTokens(owner, compactor, 600) == 300,
            "default compaction target retains no more than half of the older source tokens");
        owner.CompactionReductionPercent = 75;
        Check(CompactionReducer.TargetResponseTokens(owner, compactor, 1600) == 400,
            "configured context reduction changes the checkpoint response target");
        compactor.ContextTokens = 2048; compactor.ResponseTokens = 1800; compactor.ReasoningMode = "on"; compactor.ReasoningTokens = 512;
        Check(CompactionReducer.TargetResponseTokens(owner, compactor, 10_000) == 1024,
            "compaction honors the selected compactor context and reasoning reserve");

        owner.CompactionReductionPercent = 50;
        compactor = new AgentProfile { ContextTokens = AgentTokenLimits.MinimumContextTokens, ResponseTokens = 128, ReasoningMode = "on", ReasoningTokens = 128 };
        var source = string.Concat(Enumerable.Repeat("segment-\U0001F680-", 32));
        var observed = new StringBuilder();
        var generationFits = true;
        var generated = 0;
        Task<int> Count(IReadOnlyList<ModelMessage> messages, CancellationToken _) =>
            Task.FromResult(messages.Sum(x => x.Content.Length) + messages.Count * 4);
        async Task<string> Summarize(IReadOnlyList<ModelMessage> messages, int maxTokens, CancellationToken cancellationToken)
        {
            var prompt = await Count(messages, cancellationToken);
            generationFits &= prompt + maxTokens + compactor.ReasoningTokens + AgentPrompt.ContextSafetyTokens <= compactor.ContextTokens;
            foreach (var message in messages.Skip(1).Where(x => !x.Content.StartsWith("Existing staged brief:\n", StringComparison.Ordinal) &&
                         !x.Content.StartsWith("Earlier staged brief to reduce before continuing:\n", StringComparison.Ordinal)))
                observed.Append(message.Content);
            generated++;
            return "brief-" + generated + "-" + new string('b', Math.Max(1, Math.Min(100, maxTokens - 12)));
        }

        var reduced = await CompactionReducer.ReduceAsync(compactor, "compact accurately", [source], 128, Count, Summarize, default);
        Check(reduced.Passes > 1 && observed.ToString() == source,
            "one oversized Unicode message is split across bounded compaction passes without dropping source text");
        Check(generationFits && reduced.UsedResponseTokens == 64,
            "rolling compaction lowers only its intermediate output cap when needed and every generation fits the compactor context");
        Check(!string.IsNullOrWhiteSpace(reduced.Checkpoint),
            "staged compaction returns one usable checkpoint after processing all chunks");

        var invalid = new AgentProfile { ContextTokens = 4096, ResponseTokens = 1024, CompactionReductionPercent = 0 };
        var rejected = false;
        try { CompactionReducer.TargetResponseTokens(invalid, compactor, 1000); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "invalid compaction reduction is rejected instead of silently selecting a target");
        Console.WriteLine($"Compaction reducer: {checks} deterministic checks passed; staged generation used a bounded fixture, not a model.");
    }
}
