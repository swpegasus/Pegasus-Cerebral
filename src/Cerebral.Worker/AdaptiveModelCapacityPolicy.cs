using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

internal sealed record AdaptiveCapacityCandidate(int ParallelSlots, ModelCapacityPlan Plan,
    string Evidence, bool RuntimeActionRequired);
internal sealed record ConcurrentCapacityObservation(string ModelId, string InstanceId, string BindingSha256,
    int RequestedSlots, int PeakOverlappingGenerations, IReadOnlyList<int> PerSlotContextTokens,
    IReadOnlyList<double> PerSlotTokensPerSecond, int ConfirmedTerminals, int ReleasedLeases,
    long? MeasuredPoolAvailableAfterMb, bool HasUnknownOutcome, string EvidenceLevel);
internal sealed record AdaptiveCapacityDecision(string State, int RequestedTargetSlots, int? ObservedSafeSlots,
    int RequiredPerSlotContextTokens, IReadOnlyList<AdaptiveCapacityCandidate> Candidates, string Detail);

/// <summary>Plans incremental trials; never treats an estimate as capacity or changes a running model.</summary>
internal static class AdaptiveModelCapacityPolicy
{
    internal static AdaptiveCapacityDecision Plan(ModelRecord model, ResourceInfo resources, long reservedMemoryMb,
        int requestedTargetSlots, int requiredPerSlotContextTokens, string expectedInstanceId,
        string expectedBindingSha256, IReadOnlyList<ConcurrentCapacityObservation>? observations = null)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(resources);
        if (requestedTargetSlots is < 1 or > 16 || requiredPerSlotContextTokens is < 512 or > 1_048_576 ||
            reservedMemoryMb < 0 || string.IsNullOrWhiteSpace(expectedInstanceId) ||
            string.IsNullOrWhiteSpace(expectedBindingSha256) || expectedBindingSha256.Length != 64 || !expectedBindingSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Capacity planning requires bounded target, context, reserve and exact runtime pins.");
        if (observations is { Count: > 64 }) throw new ArgumentException("Capacity observations exceed their bounded history.");
        var candidates = new List<AdaptiveCapacityCandidate>();
        for (var slots = 1; slots <= requestedTargetSlots; slots++)
        {
            // Existing Core estimator owns GGUF/unified-pool policy. Clone only to vary a trial;
            // the current model, its configured ceiling and runtime allocation stay unchanged.
            var trial = JsonSerializer.Deserialize<ModelRecord>(JsonSerializer.Serialize(model, CerebralJson.Options), CerebralJson.Options)!;
            trial.ParallelRequests = slots;
            var plan = ModelCapacity.Plan(trial, resources, reservedMemoryMb);
            candidates.Add(new(slots, plan, "estimated; requires actual simultaneous-generation trial", true));
        }
        int? safe = null;
        foreach (var observation in observations ?? [])
        {
            if (observation.ModelId != model.Id || observation.InstanceId != expectedInstanceId ||
                observation.BindingSha256 != expectedBindingSha256 || observation.EvidenceLevel != "real-runtime" ||
                observation.RequestedSlots is < 1 || observation.RequestedSlots > requestedTargetSlots ||
                observation.HasUnknownOutcome || observation.PeakOverlappingGenerations != observation.RequestedSlots ||
                observation.ConfirmedTerminals != observation.RequestedSlots || observation.ReleasedLeases != observation.RequestedSlots ||
                observation.PerSlotContextTokens is null || observation.PerSlotTokensPerSecond is null ||
                observation.PerSlotContextTokens.Count != observation.RequestedSlots ||
                observation.PerSlotTokensPerSecond.Count != observation.RequestedSlots ||
                observation.PerSlotContextTokens.Any(x => x < requiredPerSlotContextTokens || x > Ceiling(model)) ||
                observation.PerSlotTokensPerSecond.Any(x => !double.IsFinite(x) || x <= 0) ||
                observation.MeasuredPoolAvailableAfterMb is not { } available || available < reservedMemoryMb)
                continue;
            safe = Math.Max(safe ?? 0, observation.RequestedSlots);
        }
        return new(safe is null ? "trial_required" : "observed_safe_for_exact_pins",
            requestedTargetSlots, safe, requiredPerSlotContextTokens, candidates.AsReadOnly(),
            safe is null
                ? "The target is unverified. Run one, then two, then the next bounded slot count with real overlap, per-slot context, throughput, measured shared-pool headroom and exact cleanup."
                : "Only the recorded workload at these exact runtime/binding pins is observed safe. This is not a hardware maximum or permission to resize/reload.");
    }

    private static int Ceiling(ModelRecord model)
    {
        var native = Math.Clamp(model.NativeContextTokens ?? 1_048_576, 512, 1_048_576);
        return model.MaximumContextTokens > 0 ? Math.Clamp(model.MaximumContextTokens, 512, native) : native;
    }
}
