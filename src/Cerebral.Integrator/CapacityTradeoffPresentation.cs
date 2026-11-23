using System.Globalization;
using System.Text;
using Cerebral.Core;

namespace Cerebral.Integrator;

// This scope fences this Configuration view; the server's ExpectedCurrent independently
// compares configuration and confers no authority.
internal sealed record CapacityChoiceScope(string ConnectionIdentity, string WorkerId, string ModelId,
    string ModelBinding, long ConnectionEpoch);
internal sealed record SavedCapacitySetting(int ContextTokens, int Slots, bool AutomaticContext, int MaximumContextTokens = 0);
internal sealed record CapacityTradeoffView(CapacityChoiceScope Scope, ModelCapacityPreview Preview,
    DateTimeOffset ReadAt)
{
    internal string SavedText => "Saved · " + CapacityTradeoffPresentation.ConfigurationText(Preview.Saved);
    internal string RequestedText => "Requested · " + CapacityTradeoffPresentation.ConfigurationText(Preview.Requested);
    internal string Details => CapacityTradeoffPresentation.Details(Preview);
    internal bool CanApply => Preview.ApplyEligibility.State == ModelCapacityProtocol.Ready &&
        Preview.AffectedProfilesComplete && !string.IsNullOrWhiteSpace(Preview.ExpectedCurrent);
}

internal static class CapacityTradeoffPresentation
{
    private static bool Text(string? value, int max = 512) => value is not null && value.Length <= max &&
        !value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');
    private static bool Id(string? value, int max = 512) => !string.IsNullOrWhiteSpace(value) && Text(value, max);

    internal static ModelCapacityChoice SavedChoice(ModelCapacityConfiguration value) =>
        value.ContextMode == "fixed" ? new("fixed", value.ParallelRequests, value.ContextTokens) :
        new("automatic", value.ParallelRequests, AutomaticContextCeilingTokens: value.MaximumContextTokens);

    internal static bool ValidChoice(ModelCapacityChoice? value, ModelCapacityBounds bounds) =>
        value is not null && value.ParallelRequests >= bounds.MinimumSlots && value.ParallelRequests <= bounds.MaximumSlots &&
        (value.ContextMode == "fixed" && value.AutomaticContextCeilingTokens is null &&
            value.FixedContextTokens >= bounds.MinimumContextTokens && value.FixedContextTokens <= bounds.MaximumContextTokens ||
         value.ContextMode == "automatic" && value.FixedContextTokens is null &&
            (value.AutomaticContextCeilingTokens == 0 ||
             value.AutomaticContextCeilingTokens >= bounds.MinimumContextTokens &&
             value.AutomaticContextCeilingTokens <= bounds.MaximumContextTokens));

    private static bool ValidConfig(ModelCapacityConfiguration? value, ModelCapacityBounds bounds) =>
        value is not null && value.ContextTokens >= bounds.MinimumContextTokens &&
        value.ContextTokens <= bounds.MaximumContextTokens && value.GpuLayers >= -1 &&
        ValidChoice(SavedChoice(value), bounds) &&
        (value.ContextMode == "automatic" || value.ContextMode == "fixed" && value.MaximumContextTokens == value.ContextTokens);

    private static bool MatchesChoice(ModelCapacityChoice value, ModelCapacityConfiguration config) =>
        value.ContextMode == config.ContextMode && value.ParallelRequests == config.ParallelRequests &&
        (value.ContextMode == "fixed" ? config.ContextTokens == value.FixedContextTokens &&
            config.MaximumContextTokens == value.FixedContextTokens : config.MaximumContextTokens == value.AutomaticContextCeilingTokens);

    private static bool ValidFields(IReadOnlyList<ModelCapacityFieldChange>? fields) =>
        fields is not null && fields.Count <= 64 && fields.All(x => x is not null &&
            Id(x.Field) && Text(x.Before, 2048) && Text(x.After, 2048)) &&
        fields.Select(x => x.Field).Distinct(StringComparer.Ordinal).Count() == fields.Count;

    private static bool ValidPlan(ModelCapacityPreviewPlan? value, ModelCapacityChoice choice) =>
        value is not null && value.ContextTokens > 0 && value.ParallelRequests == choice.ParallelRequests &&
        value.TotalContextTokens > 0 && value.Automatic == (choice.ContextMode == "automatic") &&
        value.ReservedMemoryMb >= 0 && value.AvailableMemoryMb is not < 0 &&
        value.EstimatedPlacementMb is null && value.EstimatedRemainingMb is null &&
        value.FitState == ModelCapacityProtocol.UnknownFit && !value.GuaranteedNextLoad &&
        Text(value.MemoryPool) && Text(value.Basis, 8192) &&
        value.Limitations is not null && value.Limitations.Count <= 64 && value.Limitations.All(x => Text(x, 4096)) &&
        (value.SelectionSource is ModelCapacityProtocol.FixedRequest or ModelCapacityProtocol.RetainedFallback or
            ModelCapacityProtocol.MemoryEstimate or ModelCapacityProtocol.UnreportedSelection) &&
        (value.SelectionSource != ModelCapacityProtocol.RetainedFallback || value.MeetsRequiredContextTokens is null);

    private static bool ValidBudget(ModelCapacityProfileBudget? budget) => budget is not null &&
        budget.Revision >= 0 && budget.ContextTokens > 0 && budget.ResponseTokens > 0 &&
        budget.ReasoningTokens >= 0 && Id(budget.ReasoningMode);
    internal static bool ValidEffects(IReadOnlyList<ModelCapacityProfileEffect>? effects, string modelId) =>
        effects is not null && effects.Count <= ModelCapacityProtocol.MaximumAffectedProfiles &&
        effects.All(x => x is not null && Id(x.ProfileId) && Text(x.Name) && Id(x.ModelWorkerId) &&
            x.ModelId == modelId && ValidBudget(x.Before) && ValidBudget(x.After) &&
            x.After.Revision >= x.Before.Revision) &&
        effects.Select(x => x.ProfileId).Distinct(StringComparer.Ordinal).Count() == effects.Count;

    internal static CapacityTradeoffView Project(CapacityChoiceScope scope, ModelCapacityPreview value, DateTimeOffset now)
    {
        var bounds = value.SupportedBounds;
        if (value.Version != ModelCapacityProtocol.Version || value.ModelId != scope.ModelId ||
            bounds is null || bounds.BoundKind != ModelCapacityProtocol.CompatibilityBounds ||
            bounds.MinimumSlots < 1 || bounds.MaximumSlots < bounds.MinimumSlots ||
            bounds.MinimumContextTokens < 1 || bounds.MaximumContextTokens < bounds.MinimumContextTokens ||
            !ValidConfig(value.Saved, bounds) || !ValidConfig(value.Requested, bounds) ||
            !ValidChoice(value.Choice, bounds) || !MatchesChoice(value.Choice, value.Requested) ||
            value.Saved.GpuLayers != value.Requested.GpuLayers ||
            value.Saved.VisionProjectorConfigured != value.Requested.VisionProjectorConfigured ||
            !ValidFields(value.FieldChanges) || !ValidPlan(value.Planned, value.Choice) ||
            value.Evidence is null || value.Evidence.Kind != ModelCapacityProtocol.Estimate ||
            value.Evidence.Scope != ModelCapacityProtocol.EstimateScope ||
            value.Evidence.Applicability != ModelCapacityProtocol.CurrentConfiguration ||
            !ValidEffects(value.AffectedProfiles, scope.ModelId) || value.Scheduler is null ||
            value.Scheduler.MaxConcurrentJobs < 1 || value.Scheduler.ChangesScheduler ||
            value.ApplyEligibility is null || value.ApplyEligibility.State is not
                (ModelCapacityProtocol.Ready or ModelCapacityProtocol.Busy or ModelCapacityProtocol.Unavailable) ||
            !Text(value.ApplyEligibility.Reason, 8192) ||
            value.ExpectedCurrent is not null && !Id(value.ExpectedCurrent, 32768))
            throw new System.IO.InvalidDataException("Capacity preview has an unsupported or inconsistent contract. Refresh or update this Worker.");
        if (value.Effective is { } effective &&
            (effective.State != "present" || !Id(effective.InstanceId) ||
             effective.BindingState is not ("captured" or "unavailable") ||
             effective.ContextState is not ("measured" or "unobserved") ||
             effective.ContextSource is not ("startup_log" or "launch_arguments") ||
             effective.ConfiguredContextTokens is <= 0 || effective.MeasuredContextTokens is <= 0 ||
             effective.ParallelRequests is <= 0 ||
             effective.ContextState == "unobserved" && effective.MeasuredContextTokens is not null))
            throw new System.IO.InvalidDataException("Effective capacity state is unsupported. No loaded allocation is inferred.");

        if (value.Candidates is null || value.Candidates.Count > 256)
            throw new System.IO.InvalidDataException("The backend suggestion list exceeds its presentation bound.");
        ModelCapacityCandidate? previous = null;
        var seen = new HashSet<ModelCapacityChoice>();
        // Capacity-v1 suggestions are explicit fixed alternatives, including when the
        // selected preview is automatic. Their mode/field changes remain reviewable.
        foreach (var candidate in value.Candidates)
        {
            if (candidate is null || !ValidChoice(candidate.Choice, bounds) || !ValidConfig(candidate.Requested, bounds) ||
                !MatchesChoice(candidate.Choice, candidate.Requested) || !ValidFields(candidate.FieldChanges) ||
                !ValidPlan(candidate.Planned, candidate.Choice) ||
                candidate.Choice.ContextMode != "fixed" || !seen.Add(candidate.Choice) ||
                candidate.Requested.GpuLayers != value.Saved.GpuLayers ||
                candidate.Requested.VisionProjectorConfigured != value.Saved.VisionProjectorConfigured ||
                candidate.Planned.SelectionSource is ModelCapacityProtocol.RetainedFallback or ModelCapacityProtocol.UnreportedSelection ||
                previous is not null && (candidate.Choice.ParallelRequests <= previous.Choice.ParallelRequests ||
                    candidate.Planned.ContextTokens > previous.Planned.ContextTokens))
                throw new System.IO.InvalidDataException("The backend suggestion frontier is inconsistent. It has not been sorted or repaired.");
            previous = candidate;
        }
        // Empty/partial candidates do not invalidate the separately selected manual preview.
        var copy = value with
        {
            FieldChanges = Array.AsReadOnly(value.FieldChanges.ToArray()),
            AffectedProfiles = Array.AsReadOnly(value.AffectedProfiles.ToArray()),
            Planned = value.Planned with { Limitations = Array.AsReadOnly(value.Planned.Limitations.ToArray()) },
            Candidates = Array.AsReadOnly(value.Candidates.Select(x => x with
            {
                FieldChanges = Array.AsReadOnly(x.FieldChanges.ToArray()),
                Planned = x.Planned with { Limitations = Array.AsReadOnly(x.Planned.Limitations.ToArray()) }
            }).ToArray())
        };
        return new(scope, copy, now);
    }

    internal static void ValidateResult(ModelCapacityApplyResult value, ModelCapacityPreview expected)
    {
        var applied = value.State == ModelCapacityProtocol.Applied;
        if (value.Version != ModelCapacityProtocol.Version || value.ModelId != expected.ModelId ||
            value.State is not (ModelCapacityProtocol.Applied or ModelCapacityProtocol.Stale or ModelCapacityProtocol.Busy or
                ModelCapacityProtocol.Invalid or ModelCapacityProtocol.Unavailable) ||
            value.MutationCommitted != applied || !Text(value.Reason, 8192) ||
            !ValidEffects(value.AffectedProfiles, expected.ModelId) || value.Warnings is null ||
            value.Warnings.Count > 64 || value.Warnings.Any(x => !Text(x, 8192)) ||
            applied && (value.Saved != expected.Requested || !value.AffectedProfiles.SequenceEqual(expected.AffectedProfiles)) ||
            !applied && (value.Saved is not null || value.AffectedProfiles.Count != 0 || value.Warnings.Count != 0))
            throw new System.IO.InvalidDataException("The save acknowledgement is inconsistent. Its outcome must be checked by readback.");
    }

    internal static string ConfigurationText(ModelCapacityConfiguration value) =>
        value.ContextMode == "fixed"
            ? $"{N(value.ContextTokens)} fixed context tokens per slot; matching ceiling {N(value.MaximumContextTokens)}; {N(value.ParallelRequests)} inference slots"
            : $"automatic context; ceiling {(value.MaximumContextTokens == 0 ? "native limit" : N(value.MaximumContextTokens))}; {N(value.ParallelRequests)} inference slots; last/fallback {N(value.ContextTokens)} tokens (next load may differ)";

    internal static string Details(ModelCapacityPreview value)
    {
        var text = new StringBuilder();
        text.AppendLine("Requested · " + ConfigurationText(value.Requested));
        var selection = value.Planned.SelectionSource switch
        {
            ModelCapacityProtocol.FixedRequest => "explicit fixed request",
            ModelCapacityProtocol.MemoryEstimate => "memory-derived estimate",
            ModelCapacityProtocol.RetainedFallback => "retained fallback; no derived-fit or minimum-context credit",
            _ => "selection provenance unreported"
        };
        text.AppendLine($"Planned · {N(value.Planned.ContextTokens)} tokens per slot; {selection}. Never guaranteed at next load.");
        text.AppendLine("Evidence · estimate for this configuration. Full placement, remaining memory, fit and image headroom are unknown.");
        text.AppendLine($"Resource sample · available {(value.Planned.AvailableMemoryMb is { } mb ? N(mb) + " MiB" : "unknown")}; reserve {N(value.Planned.ReservedMemoryMb)} MiB; pool {value.Planned.MemoryPool}; sampled {value.Planned.ResourceSampledAt?.ToString("O") ?? "unknown"}.");
        foreach (var limitation in value.Planned.Limitations) text.AppendLine("Limitation · " + limitation);
        text.AppendLine(value.Effective is not { } actual ? "Effective · unloaded or unobserved; no running allocation established." :
            $"Effective observation · present instance {actual.InstanceId}; binding {actual.BindingState}; configured context {Maybe(actual.ConfiguredContextTokens)}; measured context {Maybe(actual.MeasuredContextTokens)} ({actual.ContextState}, {actual.ContextSource}); slots {Maybe(actual.ParallelRequests)}. Presence is not readiness or workload qualification.");
        text.AppendLine($"Scheduler · current job ceiling {N(value.Scheduler.MaxConcurrentJobs)}; unchanged and independent of native inference slots.");
        text.AppendLine($"Manual compatibility bounds · slots {N(value.SupportedBounds.MinimumSlots)}–{N(value.SupportedBounds.MaximumSlots)}; context {N(value.SupportedBounds.MinimumContextTokens)}–{N(value.SupportedBounds.MaximumContextTokens)}. These are protocol bounds, not measured hardware limits.");
        text.AppendLine(value.CandidatesComplete ? "Backend suggestions · complete for this preview." :
            "Backend suggestions · incomplete or unavailable. Omitted slot counts remain manual choices within the compatibility bounds.");
        text.AppendLine($"Model features · GPU layers {value.Saved.GpuLayers} preserved; image projector {(value.Saved.VisionProjectorConfigured ? "configured" : "not configured")} preserved. This does not establish image readiness.");
        text.AppendLine("Exact saved-field changes:");
        foreach (var field in value.FieldChanges) text.AppendLine($"  {field.Field}: {field.Before} → {field.After}");
        if (value.FieldChanges.Count == 0) text.AppendLine("  None.");
        text.AppendLine($"Affected profiles · {value.AffectedProfiles.Count}; review {(value.AffectedProfilesComplete ? "complete" : "INCOMPLETE — Apply disabled")}.");
        foreach (var profile in value.AffectedProfiles)
        {
            text.AppendLine($"  {profile.Name} [{profile.ProfileId}] · model {profile.ModelWorkerId}/{profile.ModelId}; follows model budgets {profile.FollowsModelBudgets}");
            text.AppendLine($"    revision {profile.Before.Revision} → {profile.After.Revision}; context {profile.Before.ContextTokens} → {profile.After.ContextTokens}; response {profile.Before.ResponseTokens} → {profile.After.ResponseTokens}; reasoning {profile.Before.ReasoningMode}/{profile.Before.ReasoningTokens} → {profile.After.ReasoningMode}/{profile.After.ReasoningTokens}");
        }
        text.AppendLine($"Apply · {value.ApplyEligibility.State}: {value.ApplyEligibility.Reason}");
        text.Append("An applied result saves settings for the next load. No model load/unload, active-context truncation, or scheduler change is requested.");
        return text.ToString();
    }
    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string Maybe(int? value) => value is { } n ? N(n) : "unknown";
}
