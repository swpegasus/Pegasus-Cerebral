using System.Globalization;
using System.Text.Json;

namespace Cerebral.Core;

/// <summary>Pure capacity-choice preparation. No store, metadata refresh, runtime action or hardware probe.</summary>
public static class ModelCapacityChoices
{
    public static void ValidateChoice(ModelRecord model, ModelCapacityChoice choice)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.ParallelRequests is < 1 or > 16)
            throw new ArgumentException("Inference slots must be between 1 and 16 (current protocol compatibility bounds).");
        var maximum = NativeMaximum(model);
        switch (choice.ContextMode)
        {
            case "fixed":
                if (choice.FixedContextTokens is not { } fixedTokens || fixedTokens < 512 || fixedTokens > maximum ||
                    choice.AutomaticContextCeilingTokens is not null)
                    throw new ArgumentException("Fixed mode requires an in-range fixed context and no automatic ceiling alternative.");
                break;
            case "automatic":
                if (choice.FixedContextTokens is not null || choice.AutomaticContextCeilingTokens is not { } ceiling ||
                    ceiling < 0 || ceiling > maximum || ceiling is > 0 and < 512)
                    throw new ArgumentException("Automatic mode requires an explicit ceiling (zero means native) and no fixed alternative.");
                break;
            default:
                throw new ArgumentException("Context mode must be fixed or automatic.");
        }
    }

    public static ModelRecord ApplyChoice(ModelRecord model, ModelCapacityChoice choice)
    {
        ValidateChoice(model, choice);
        var copy = JsonSerializer.Deserialize<ModelRecord>(JsonSerializer.Serialize(model, CerebralJson.Options), CerebralJson.Options)
            ?? throw new InvalidDataException("Model configuration could not be copied.");
        copy.ParallelRequests = choice.ParallelRequests;
        copy.AutomaticContextSizing = choice.ContextMode == "automatic";
        if (copy.AutomaticContextSizing)
        {
            copy.MaximumContextTokens = choice.AutomaticContextCeilingTokens!.Value;
            var maximum = copy.MaximumContextTokens == 0 ? NativeMaximum(copy) : copy.MaximumContextTokens;
            // This is a saved fallback, not the forecast returned by Plan or native fitting.
            copy.ContextTokens = Math.Clamp(copy.ContextTokens, 512, maximum);
        }
        else
        {
            copy.ContextTokens = choice.FixedContextTokens!.Value;
            // Both the existing planner and fitter cap fixed sizing by this field too.
            // Disclose this capacity-field change instead of claiming the old ceiling is inactive.
            copy.MaximumContextTokens = copy.ContextTokens;
        }
        return copy;
    }

    public static ModelCapacityConfiguration Configuration(ModelRecord model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new(model.AutomaticContextSizing ? "automatic" : "fixed", model.ContextTokens,
            model.MaximumContextTokens, model.ParallelRequests, model.GpuLayers,
            !string.IsNullOrWhiteSpace(model.VisionProjectorPath));
    }

    public static ModelCapacityBounds Bounds(ModelRecord model) =>
        new(1, 16, 512, NativeMaximum(model), ModelCapacityProtocol.CompatibilityBounds, null);

    public static IReadOnlyList<ModelCapacityFieldChange> Changes(ModelRecord before, ModelRecord after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var result = new List<ModelCapacityFieldChange>();
        Add("automaticContextSizing", before.AutomaticContextSizing ? "true" : "false", after.AutomaticContextSizing ? "true" : "false");
        Add("contextTokens", Number(before.ContextTokens), Number(after.ContextTokens));
        Add("maximumContextTokens", Number(before.MaximumContextTokens), Number(after.MaximumContextTokens));
        Add("parallelRequests", Number(before.ParallelRequests), Number(after.ParallelRequests));
        return result.AsReadOnly();

        void Add(string field, string oldValue, string newValue)
        { if (oldValue != newValue) result.Add(new(field, oldValue, newValue)); }
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    public static ModelCapacityPreviewPlan PreviewPlan(ModelRecord candidate, ResourceInfo? resources,
        long reservedMemoryMb, DateTimeOffset? sampledAt, int? requiredContext)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (reservedMemoryMb < 0) throw new ArgumentException("Reserved memory cannot be negative.");
        if (candidate.ContextTokens < 512 || candidate.ContextTokens > NativeMaximum(candidate) ||
            candidate.ParallelRequests is < 1 or > 16 ||
            candidate.MaximumContextTokens < 0 || candidate.MaximumContextTokens > NativeMaximum(candidate) ||
            candidate.MaximumContextTokens is > 0 and < 512)
            throw new ArgumentException("Capacity settings are outside the advertised compatibility bounds.");
        if (!candidate.AutomaticContextSizing && candidate.MaximumContextTokens > 0 &&
            candidate.ContextTokens > candidate.MaximumContextTokens)
            throw new ArgumentException("Fixed context exceeds the capacity ceiling; prepare an explicit fixed choice first.");
        if (requiredContext is { } required && (required < 512 || required > 1_048_576))
            throw new ArgumentException("Required context must be between 512 and 1048576 tokens.");
        var plan = ModelCapacity.Plan(candidate, resources ?? new ResourceInfo(0, null, []), reservedMemoryMb);
        var limitations = new List<string>
        {
            "Generic F16 attention-cache and runtime-buffer estimates are not measured allocation or a hardware maximum.",
            "The selected memory pool is a planning sample, not exact layer placement or a sum of multiple GPUs.",
            "Native fitting, partial offload, other allocations and the next load can change the result."
        };
        if (resources is null || plan.AvailableMemoryMb is null)
            limitations.Add("Live free-memory telemetry is unavailable; fit and remaining memory are unknown.");
        if (!ModelCapacity.Estimate(candidate).UsesGgufShape)
            limitations.Add("Model shape metadata is incomplete; full placement is unknown.");
        limitations.Add("The generic attention-cache shape does not establish complete architecture or recurrent-state accounting.");
        if (!string.IsNullOrWhiteSpace(candidate.VisionProjectorPath))
            limitations.Add("Projector weights, image encoding and image-workload memory are not included.");
        // No complete-placement contract exists for the generic estimator. Keep totals unknown
        // for every architecture instead of maintaining model-family policy in this Core helper.
        long? placement = null;
        long? remaining = null;
        var basis = plan.SelectionSource == ModelCapacityProtocol.RetainedFallback
            ? "The planner retained the last/fallback context; no memory-derived context suggestion is available."
            : candidate.AutomaticContextSizing
                ? "Automatic context estimate within the selected ceiling. Normal next-load fitting may choose a different context."
                : "Estimate for the explicit fixed context and inference-slot request. Loading success and GPU residency are unmeasured.";
        return new(plan.ContextTokens, plan.ParallelRequests, (long)plan.ContextTokens * plan.ParallelRequests,
            candidate.AutomaticContextSizing, requiredContext,
            requiredContext is { } needed && plan.SelectionSource != ModelCapacityProtocol.RetainedFallback
                ? plan.ContextTokens >= needed : null,
            plan.AvailableMemoryMb, plan.ReservedMemoryMb, placement, remaining, plan.MemoryPool,
            basis, resources is null ? null : sampledAt, ModelCapacityProtocol.UnknownFit, limitations.AsReadOnly(), false)
        { SelectionSource = plan.SelectionSource };
    }

    /// <summary>
    /// A bounded prefix of actual memory-derived context estimates from one resource snapshot.
    /// Stop when the existing planner retains a fallback instead of inventing or clipping a fit.
    /// Each alternative is an explicit fixed choice, with any automatic-mode change disclosed.
    /// Omission never narrows manual configuration bounds or proves a hardware maximum.
    /// </summary>
    public static IReadOnlyList<ModelCapacityCandidate> Frontier(ModelRecord model, ModelCapacityChoice selected,
        ResourceInfo? resources, long reservedMemoryMb, DateTimeOffset? sampledAt, int? requiredContext)
    {
        ValidateChoice(model, selected);
        var result = new List<ModelCapacityCandidate>(16);
        int? precedingContext = null;
        for (var slots = 1; slots <= 16; slots++)
        {
            var sizing = ApplyChoice(model, selected with { ParallelRequests = slots });
            sizing.AutomaticContextSizing = true;
            var suggestion = PreviewPlan(sizing, resources, reservedMemoryMb, sampledAt, requiredContext);
            if (suggestion.SelectionSource != ModelCapacityProtocol.MemoryEstimate ||
                precedingContext is { } preceding && suggestion.ContextTokens > preceding)
                break;
            var choice = new ModelCapacityChoice("fixed", slots, suggestion.ContextTokens);
            var candidate = ApplyChoice(model, choice);
            var planned = PreviewPlan(candidate, resources, reservedMemoryMb, sampledAt, requiredContext) with
            {
                SelectionSource = ModelCapacityProtocol.MemoryEstimate,
                Basis = "Explicit fixed alternative derived from a generic context-cache estimate. Complete placement and fit are unknown.",
                Limitations = Array.AsReadOnly(suggestion.Limitations.Append(
                    selected.ContextMode == "automatic"
                        ? "This alternative changes automatic sizing to an explicit fixed context, as listed in FieldChanges."
                        : "This alternative stays within the selected fixed-context upper bound.").ToArray())
            };
            precedingContext = suggestion.ContextTokens;
            result.Add(new(choice, Configuration(candidate), Changes(model, candidate), planned));
        }
        return result.AsReadOnly();
    }

    private static int NativeMaximum(ModelRecord model) =>
        Math.Clamp(model.NativeContextTokens ?? 1_048_576, 512, 1_048_576);
}
