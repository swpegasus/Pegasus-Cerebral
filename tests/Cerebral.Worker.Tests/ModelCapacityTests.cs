using Cerebral.Core;

namespace Cerebral.Worker.Tests;

internal static class ModelCapacityTests
{
    public static void Run()
    {
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++;
            Console.WriteLine("PASS: " + name);
        }

        var model = new ModelRecord
        {
            SizeBytes = 4_683_073_536,
            ContextTokens = 16_384,
            ParallelRequests = 1,
            BlockCount = 28,
            EmbeddingLength = 3_584,
            AttentionHeadCount = 28,
            AttentionHeadCountKv = 4
        };
        var one = ModelCapacity.Estimate(model);
        Check(one.TotalContextTokens == 16_384 && one.KvCacheMb == 896, "capacity derives the f16 KV cache from GGUF shape");
        Check(one.RuntimeReserveMb == 512 && one.FullPlacementMb == one.WeightsMb + 896 + 512, "capacity labels a conservative complete planning estimate");

        model.ParallelRequests = 2;
        var two = ModelCapacity.Estimate(model);
        Check(two.TotalContextTokens == 32_768 && two.KvCacheMb == 1_792 && two.WeightsMb == one.WeightsMb,
            "parallel slots multiply context cache without duplicating model weights");

        var incomplete = ModelCapacity.Estimate(new ModelRecord { SizeBytes = 1L << 30, ContextTokens = 4_096, ParallelRequests = 1 });
        Check(incomplete.KvCacheMb is null && incomplete.FullPlacementMb is null && incomplete.MinimumWorkingSetMb == 1_536,
            "missing GGUF shape returns an explicitly incomplete minimum estimate");

        var qwen = new ModelRecord
        {
            SizeBytes = 6_577_841_376,
            ContextTokens = 16_384,
            RecommendedContextTokens = 16_384,
            NativeContextTokens = 262_144,
            AutomaticContextSizing = true,
            ParallelRequests = 1,
            BlockCount = 32,
            EmbeddingLength = 4_096,
            AttentionHeadCount = 16,
            AttentionHeadCountKv = 4
        };
        var resources = new ResourceInfo(200, 24_000, [new GpuInfo("RTX fixture", 16_303, 15_000, false)]);
        var automatic = ModelCapacity.Plan(qwen, resources, 1_024);
        Check(automatic.ContextTokens == 57_344 && automatic.Automatic && automatic.Changed &&
            automatic.EstimatedRemainingMb is >= 0 and < 128 && automatic.MemoryPool.Contains("RTX fixture", StringComparison.Ordinal),
            "automatic sizing consumes safe VRAM in bounded token steps while retaining configured headroom");

        qwen.ParallelRequests = 2;
        var parallel = ModelCapacity.Plan(qwen, resources, 1_024);
        Check(parallel.ContextTokens == 28_672 && parallel.EstimatedPlacementMb == automatic.EstimatedPlacementMb,
            "automatic sizing divides the same context-cache budget across explicit parallel request slots");

        qwen.ParallelRequests = 1;
        qwen.MaximumContextTokens = 32_768;
        var capped = ModelCapacity.Plan(qwen, resources, 1_024);
        Check(capped.ContextTokens == 32_768 && capped.MaximumContextTokens == 32_768 && capped.EstimatedRemainingMb > 3_000,
            "an automatic model ceiling leaves unused memory available for other models and applications");

        qwen.AutomaticContextSizing = false;
        qwen.ContextTokens = 20_000;
        var fixedPlan = ModelCapacity.Plan(qwen, resources, 1_024);
        Check(!fixedPlan.Automatic && fixedPlan.ContextTokens == 20_000 && !fixedPlan.Changed &&
            fixedPlan.AvailableMemoryMb == 15_000 && fixedPlan.MemoryPool.Contains("RTX fixture", StringComparison.Ordinal),
            "fixed sizing preserves the exact configured context while retaining measured headroom");

        var fixedUnavailable = ModelCapacity.Plan(qwen,
            new ResourceInfo(100, null, [new GpuInfo("Unavailable", null, null, false, "fixture")]), 1_024);
        Check(fixedUnavailable.ContextTokens == 20_000 && fixedUnavailable.AvailableMemoryMb is null,
            "fixed sizing keeps unavailable telemetry distinct from an exhausted memory pool");

        var fixedUnified = ModelCapacity.Plan(qwen,
            new ResourceInfo(100, 12_000, [new GpuInfo("GB10 fixture", 128_000, 20_000, true)]), 2_000);
        Check(fixedUnified.ContextTokens == 20_000 && fixedUnified.AvailableMemoryMb == 12_000,
            "fixed sizing observes unified memory once without resizing the context");

        qwen.AutomaticContextSizing = true;
        qwen.MaximumContextTokens = 0;
        var unavailable = ModelCapacity.Plan(qwen, new ResourceInfo(100, null, [new GpuInfo("Unavailable", null, null, false, "fixture")]), 1_024);
        Check(unavailable.ContextTokens == 20_000 && unavailable.AvailableMemoryMb is null && !unavailable.Changed,
            "missing live memory telemetry keeps the last safe context instead of inventing capacity");

        var unified = ModelCapacity.Plan(qwen, new ResourceInfo(100, 12_000, [new GpuInfo("GB10 fixture", 128_000, 20_000, true)]), 2_000);
        Check(unified.AvailableMemoryMb == 12_000 && unified.MemoryPool.Contains("unified", StringComparison.Ordinal),
            "unified-memory planning counts the shared system and GPU pool once");

        Console.WriteLine($"RESULT: {assertions} model-capacity checks passed. These validate planning math; runtime diagnostics separately report measured memory before and after load.");
    }
}
