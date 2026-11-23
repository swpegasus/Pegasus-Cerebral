namespace Cerebral.Core;

/// <summary>
/// A planning estimate for one loaded model instance. It is intentionally separate from live
/// telemetry: llama.cpp may offload only part of the model, and other processes also use the GPU.
/// </summary>
public sealed record ModelCapacityEstimate(
    long WeightsMb,
    long? KvCacheMb,
    long RuntimeReserveMb,
    long MinimumWorkingSetMb,
    long? FullPlacementMb,
    long TotalContextTokens,
    bool UsesGgufShape);

/// <summary>
/// The service-owned context choice for one model load. Available memory is a live observation;
/// placement and remaining memory are planning estimates until llama.cpp has loaded the model.
/// </summary>
public sealed record ModelCapacityPlan(
    int ContextTokens,
    int ParallelRequests,
    int MaximumContextTokens,
    bool Automatic,
    bool Changed,
    long? AvailableMemoryMb,
    long ReservedMemoryMb,
    long? EstimatedPlacementMb,
    long? EstimatedRemainingMb,
    string MemoryPool,
    string Basis,
    long? MeasuredAvailableAfterLoadMb = null,
    bool? ReservedMemorySatisfied = null)
{
    /// <summary>How context was selected: unreported, fixed_request, retained_fallback or memory_estimate.</summary>
    public string SelectionSource { get; init; } = "unreported";
}

public static class ModelCapacity
{
    private const long Mebibyte = 1024 * 1024;

    public static ModelCapacityEstimate Estimate(ModelRecord model) =>
        Estimate(model, model.ContextTokens, model.ParallelRequests);

    public static ModelCapacityEstimate Estimate(ModelRecord model, int contextTokens, int parallelRequests)
    {
        ArgumentNullException.ThrowIfNull(model);
        var weights = model.SizeBytes > 0 ? DivideRoundUp(model.SizeBytes, Mebibyte) : 0;
        // This is a deliberately conservative fallback for the compute/runtime buffers. A future
        // llama-fit-params result can replace it, but the UI must never present it as measured use.
        var runtime = Math.Max(512L, (long)Math.Ceiling(Math.Max(0L, model.SizeBytes) * 0.05d / Mebibyte));
        var requests = Math.Clamp(parallelRequests, 1, 16);
        var context = Math.Max(0L, contextTokens) * requests;
        long? kv = null;

        if (context > 0 && KvCacheBytesPerToken(model) is { } bytesPerToken)
        {
            // llama.cpp defaults to f16 K and V caches. Each token stores one K and one V value
            // for every KV-head element in every transformer block.
            var bytes = context * bytesPerToken;
            if (double.IsFinite(bytes) && bytes > 0 && bytes <= long.MaxValue)
                kv = (long)Math.Ceiling(bytes / Mebibyte);
        }

        var minimum = SaturatingAdd(weights, runtime);
        long? full = kv is { } cache ? SaturatingAdd(minimum, cache) : null;
        return new(weights, kv, runtime, minimum, full, context, kv.HasValue);
    }

    public static ModelCapacityPlan Plan(ModelRecord model, ResourceInfo resources, long reservedMemoryMb)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(resources);
        reservedMemoryMb = Math.Max(0, reservedMemoryMb);
        var requests = Math.Clamp(model.ParallelRequests, 1, 16);
        var native = Math.Clamp(model.NativeContextTokens ?? 1_048_576, 512, 1_048_576);
        var maximum = model.MaximumContextTokens > 0
            ? Math.Clamp(model.MaximumContextTokens, 512, native)
            : native;
        var current = Math.Clamp(model.ContextTokens, 512, maximum);

        var pool = SelectMemoryPool(model, resources);
        if (!model.AutomaticContextSizing)
            // Fixed sizing still reports measured headroom; it does not make telemetry unavailable.
            return BuildPlan(model, current, requests, current, false,
                pool.FreeMb is { } measured ? Math.Max(0, measured) : null, reservedMemoryMb, pool.Name,
                "Fixed context sizing is selected; Cerebral will use the configured context exactly.", "fixed_request");

        if (pool.FreeMb is null)
            return BuildPlan(model, current, requests, maximum, true, null, reservedMemoryMb, pool.Name,
                "Live memory telemetry is unavailable, so Cerebral kept the last safe context setting.");

        var available = Math.Max(0, pool.FreeMb.Value);
        var capacity = Estimate(model, current, requests);
        if (!capacity.UsesGgufShape)
            return BuildPlan(model, current, requests, maximum, true, available, reservedMemoryMb, pool.Name,
                "The GGUF does not expose enough model shape metadata to size its context cache safely; Cerebral kept the last setting.");

        var usable = Math.Max(0, available - reservedMemoryMb);
        if (usable <= capacity.MinimumWorkingSetMb)
            return BuildPlan(model, current, requests, maximum, true, available, reservedMemoryMb, pool.Name,
                "A complete GPU placement does not fit beyond the configured reserve. Cerebral kept the last context and lets llama.cpp fit model layers without claiming more context is safe.");

        var bytesPerToken = KvCacheBytesPerToken(model);
        if (bytesPerToken is null)
            return BuildPlan(model, current, requests, maximum, true, available, reservedMemoryMb, pool.Name,
                "Context-cache cost could not be derived from the GGUF; Cerebral kept the last setting.");

        var maxTotalTokens = (long)Math.Floor((usable - capacity.MinimumWorkingSetMb) * (double)Mebibyte / bytesPerToken.Value);
        var selected = Math.Min((long)maximum, maxTotalTokens / requests);
        selected = RoundContextDown(selected);
        if (selected < 512)
            return BuildPlan(model, current, requests, maximum, true, available, reservedMemoryMb, pool.Name,
                "The measured pool cannot support a 512-token full placement beyond the reserve. Cerebral kept the last setting and relies on llama.cpp layer fitting.");

        var chosen = (int)selected;
        return BuildPlan(model, chosen, requests, maximum, true, available, reservedMemoryMb, pool.Name,
            $"Cerebral selected the largest 1,024-token context step that preserves {reservedMemoryMb:N0} MiB of configured headroom for {requests:N0} inference slot{(requests == 1 ? "" : "s")}.", "memory_estimate");
    }

    private static (string Name, long? FreeMb) SelectMemoryPool(ModelRecord model, ResourceInfo resources)
    {
        if (model.GpuLayers == 0)
            return ("System memory", resources.SystemAvailableMb);

        var gpu = resources.Gpus
            .Where(x => x.Error is null && x.FreeMb is not null)
            .OrderByDescending(x => x.FreeMb)
            .FirstOrDefault();
        if (gpu is null) return ("GPU memory", null);
        if (!gpu.Unified) return (gpu.Name + " GPU memory", gpu.FreeMb);

        var shared = resources.SystemAvailableMb is { } system
            ? Math.Min(gpu.FreeMb!.Value, system)
            : gpu.FreeMb;
        return (gpu.Name + " unified memory", shared);
    }

    private static ModelCapacityPlan BuildPlan(ModelRecord model, int contextTokens, int requests, int maximum,
        bool automatic, long? available, long reserved, string pool, string basis, string selectionSource = "retained_fallback")
    {
        var estimate = Estimate(model, contextTokens, requests);
        long? remaining = available is { } free && estimate.FullPlacementMb is { } placement
            ? free - placement - reserved
            : null;
        return new(contextTokens, requests, maximum, automatic, contextTokens != model.ContextTokens,
            available, reserved, estimate.FullPlacementMb, remaining, pool, basis) { SelectionSource = selectionSource };
    }

    private static long RoundContextDown(long tokens)
    {
        if (tokens < 512) return 0;
        if (tokens < 1_024) return 512;
        return Math.Min(1_048_576, tokens / 1_024 * 1_024);
    }

    private static double? KvCacheBytesPerToken(ModelRecord model)
    {
        if (model.BlockCount is not > 0 || model.EmbeddingLength is not > 0 ||
            model.AttentionHeadCount is not > 0 || model.AttentionHeadCountKv is not > 0)
            return null;
        var kvWidth = model.EmbeddingLength.Value * (double)model.AttentionHeadCountKv.Value / model.AttentionHeadCount.Value;
        var value = model.BlockCount.Value * kvWidth * 4d;
        return double.IsFinite(value) && value > 0 ? value : null;
    }

    private static long DivideRoundUp(long value, long divisor) => value <= 0 ? 0 : value / divisor + (value % divisor == 0 ? 0 : 1);
    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}
