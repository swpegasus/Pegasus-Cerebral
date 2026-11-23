using System.Text.Json;

namespace Cerebral.Conversation;

/// <summary>
/// Plugin-owned source candidate for bounded extra refinement. Decisions are advisory planning data,
/// never an execution permit. The host still owns settings/control admission, receipts, grants,
/// preparations, scheduling, cancellation, resource limits and promotion through the existing harness.
/// This policy is not exported or wired into an accepted Conversation definition.
/// </summary>
public static class ConversationRefinementAssist
{
    public sealed record Settings(int SchemaVersion = 1, bool Enabled = false,
        int ComparisonBudget = 1, string Parallelism = "auto");

    public sealed record Binding(string ConversationId, string RunId, long PlanningRevision,
        string InputSha256, string PermissionsSha256, string ConfirmedReceiptsSha256);

    public sealed record Boundary(Binding Binding, bool SafePlanningBoundary, bool EffectPending,
        bool PreparationActive, bool UnknownEffects, bool Cancelled,
        int RemainingSteps, long RemainingActiveMilliseconds);

    // Current owner admission, not a capability configured by the model or inferred from a role count.
    // Reservations cover one candidate plus its independent check, including preparation/cleanup.
    public sealed record PlanningAllowance(Binding Binding, int RemainingComparisons, int MaximumParallelLanes,
        long ReservedActiveMillisecondsPerComparison,
        long? RemainingInputTokens = null, long? ReservedInputTokensPerComparison = null,
        long? RemainingGeneratedTokens = null, long? ReservedGeneratedTokensPerComparison = null);

    // Per-call collection/serialization safety only. Larger admitted work is submitted in bounded
    // batches while preserving spent-comparison accounting; this is not a connection/product limit.
    public const int MaximumSerializedBatchEntries = 4096;

    public enum SwitchMode { Off, Automatic, Once }
    public sealed record PendingSwitch(string RequestId, Binding Expected, SwitchMode Mode);
    public enum SwitchDisposition { Unchanged, Deferred, Stale, Cancelled, Blocked, Applied, OneShotReady }
    public sealed record SwitchResult(SwitchDisposition Disposition, Settings Active, PendingSwitch? Pending);

    // These fingerprints describe structured failure/check/approach facts, not model prose or byte offsets.
    public sealed record Correction(string FailureSha256, string ObjectiveSha256, string ApproachSha256);
    public enum CorrectionChange { FirstObservation, Repeated, MateriallyChanged }

    public enum Difficulty { None, GenerationIncomplete, MalformedAction, FailedIndependentCheck,
        PermissionDenied, SafetyBoundary, UnknownEffect }
    public sealed record RecoveryWindow(int TotalCorrections, int SameFailureCorrections,
        int ComparisonsStarted, Difficulty Difficulty);

    // Captured by the existing native/adapter admission owner. A configured slot count is not this evidence.
    public sealed record Capacity(string InstanceId, string AdmissionSha256, DateTimeOffset ObservedUtc,
        int AdmittedSlots, int AvailableLeases, int RunnableTasks, int InteractiveReserve,
        bool IndependentPreparationScopes);

    public enum ProbeIsolation { ReadOnly, TaskOwnedFixture, SharedMutable }
    public enum PlanDisposition { Disabled, NotTriggered, Deferred, Blocked, Cancelled, Exhausted, Queued, Compare }
    public sealed record Plan(PlanDisposition Disposition, int Comparisons, int ParallelLanes, string Reason)
    {
        public bool RequiresFreshPreparation => Disposition is PlanDisposition.Compare or PlanDisposition.Queued;
        public bool ReuseConfirmedReceipts => true;
        public bool PermitsEffectReplay => false;
    }

    public sealed record Lane(string ContextId, string PreparationScopeId, string PreparationId,
        string InstanceId, int NativeSlotId, Binding Binding, ProbeIsolation Isolation);

    public enum Stage { Preparation, Baseline, Candidate, IndependentCheck, Cleanup }
    public enum Outcome { Succeeded, Failed, Cancelled, Unknown }

    // Token costs are charged to exactly one stage. Peaks must be process/device-wide observations,
    // including overlapping lanes, not a per-lane peak represented as a whole-run measurement.
    // Null means unavailable, never zero. Failed/cancelled stages remain in the ledger.
    public sealed record Cost(string ReceiptId, string EffectId, Binding Binding, Stage Stage, Outcome Outcome,
        long? InputTokens, long? OutputTokens, long? ReasoningTokens, long? WallMilliseconds,
        long? PeakProcessBytes, long? PeakDeviceBytes, bool CleanupConfirmed,
        string? ArtifactSha256 = null, string? ObjectiveSha256 = null);
    public enum Metric { InputTokens, OutputTokens, ReasoningTokens, GeneratedTokens,
        SummedStageWallMilliseconds, WholeRoundElapsedMilliseconds, PeakProcessBytes, PeakDeviceBytes }
    public sealed record RoundTiming(string ReceiptId, Binding Binding, long? ElapsedMilliseconds);
    public sealed record MetricEvidence(bool Available, IReadOnlyList<Metric> Unavailable);

    // Required outcomes/cleanup and optional telemetry are separate. Summed stage time includes
    // overlap; only a separately bound round observation can establish whole-round elapsed time.
    public sealed record Costs(int Stages, int FailedOrCancelledStages, bool RequiredEvidenceComplete,
        long? InputTokens, long? OutputTokens, long? ReasoningTokens, long? SummedStageWallMilliseconds,
        long? WholeRoundElapsedMilliseconds, long? PeakProcessBytes, long? PeakDeviceBytes,
        IReadOnlyList<Metric> UnavailableMetrics)
    {
        public bool TelemetryComplete => UnavailableMetrics.Count == 0;
    }

    // Null means no such configured bound. A specified bound requires a known matching measurement.
    // These limits come from current owner admission, never permanent plugin token ceilings.
    public sealed record Budgets(long? InputTokens = null, long? GeneratedTokens = null,
        long? WholeRoundElapsedMilliseconds = null, long? PeakProcessBytes = null,
        long? PeakDeviceBytes = null, long? OutputTokens = null);

    public sealed record Promotion(Binding Binding, string BaselineSha256, string CandidateSha256,
        string CandidateEffectId, string CandidateContextId, string CheckEffectId, string CheckContextId,
        string ObjectiveSha256, bool IndependentChecksPassed, bool ImprovementObserved,
        IReadOnlyList<string> ExpectedCostReceiptIds);
    public sealed record Qualification(bool EligibleForOwnerPromotion, string Reason, Costs Costs);

    public static Settings ReadSettings(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new();
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("settings_object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name is not
                ("schemaVersion" or "enabled" or "comparisonBudget" or "parallelism"))
                throw Invalid("settings_fields");
        var version = 1; var count = 1; var enabled = false; var parallelism = "auto";
        if (value.TryGetProperty("schemaVersion", out var v) &&
            (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version))) throw Invalid("settings_type");
        if (value.TryGetProperty("comparisonBudget", out var n) &&
            (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out count))) throw Invalid("settings_type");
        if (value.TryGetProperty("enabled", out var e))
        {
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("settings_type");
            enabled = e.GetBoolean();
        }
        if (value.TryGetProperty("parallelism", out var p))
        {
            if (p.ValueKind != JsonValueKind.String) throw Invalid("settings_type");
            parallelism = p.GetString()!;
        }
        var settings = new Settings(version, enabled, count, parallelism);
        Validate(settings);
        return settings;
    }

    /// <summary>Pass only the already owner-admitted plugin settings portions. A chat override is explicit.</summary>
    public static Settings ResolveSettings(JsonElement profile, JsonElement? chatOverride = null) =>
        ReadSettings(chatOverride is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } chat
            ? chat : profile);

    /// <summary>
    /// Pending switches come from an admitted human control, not generated tool arguments or Cortex.
    /// Exact binding/CAS and one-shot request consumption belong to the existing owner controller.
    /// </summary>
    public static SwitchResult ResolveSwitch(Settings active, PendingSwitch? pending, Boundary boundary)
    {
        Validate(active);
        if (pending is null) return new(SwitchDisposition.Unchanged, active, null);
        Validate(boundary); Validate(pending.Expected); Identifier(pending.RequestId);
        if (!Enum.IsDefined(pending.Mode)) throw Invalid("switch_mode");
        if (pending.Expected != boundary.Binding) return new(SwitchDisposition.Stale, active, pending);
        if (boundary.Cancelled) return new(SwitchDisposition.Cancelled, active, pending);
        if (boundary.UnknownEffects) return new(SwitchDisposition.Blocked, active, pending);
        if (!Safe(boundary)) return new(SwitchDisposition.Deferred, active, pending);
        return pending.Mode == SwitchMode.Once
            ? new(SwitchDisposition.OneShotReady, active, null)
            : new(SwitchDisposition.Applied, active with { Enabled = pending.Mode == SwitchMode.Automatic }, null);
    }

    public static CorrectionChange CompareCorrection(Correction? previous, Correction current)
    {
        Validate(current);
        if (previous is null) return CorrectionChange.FirstObservation;
        Validate(previous);
        return previous == current ? CorrectionChange.Repeated : CorrectionChange.MateriallyChanged;
    }

    public static Plan CreatePlan(Settings settings, Boundary boundary, RecoveryWindow recovery,
        ProbeIsolation isolation, Capacity? capacity, DateTimeOffset now, PlanningAllowance allowance,
        bool oneShotReady = false)
    {
        Validate(settings);
        // Off is an exact no-request path, independent of optional owner/capacity/recovery observations.
        if (!settings.Enabled && !oneShotReady) return new(PlanDisposition.Disabled, 0, 0, "off");
        Validate(boundary); Validate(allowance);
        if (allowance.Binding != boundary.Binding) return new(PlanDisposition.Blocked, 0, 0, "stale_owner_allowance");
        if (!Enum.IsDefined(isolation)) throw Invalid("probe_isolation");
        if (recovery.TotalCorrections is < 0 or > 8 || recovery.SameFailureCorrections is < 0 or > 2 ||
            recovery.SameFailureCorrections > recovery.TotalCorrections || recovery.ComparisonsStarted < 0 ||
            !Enum.IsDefined(recovery.Difficulty))
            throw Invalid("recovery_window");
        if (boundary.Cancelled) return new(PlanDisposition.Cancelled, 0, 0, "cancelled");
        if (boundary.UnknownEffects) return new(PlanDisposition.Blocked, 0, 0, "unknown_effect_no_replay");
        if (recovery.Difficulty is Difficulty.PermissionDenied or Difficulty.SafetyBoundary or Difficulty.UnknownEffect)
            return new(PlanDisposition.Blocked, 0, 0, "difficulty_is_an_authority_or_effect_boundary");
        if (!Safe(boundary)) return new(PlanDisposition.Deferred, 0, 0, "next_safe_planning_boundary");
        if (isolation == ProbeIsolation.SharedMutable)
            return new(PlanDisposition.Blocked, 0, 0, "comparison_requires_read_only_or_owned_fixture");
        // Refinement cannot replenish the existing two-identical/eight-total correction window.
        if (recovery.TotalCorrections >= 8 || recovery.SameFailureCorrections >= 2)
            return new(PlanDisposition.Exhausted, 0, 0, "existing_correction_limit");
        if (recovery.Difficulty == Difficulty.None && !oneShotReady)
            return new(PlanDisposition.NotTriggered, 0, 0, "no_observed_difficulty");
        var count = Math.Min(settings.ComparisonBudget - recovery.ComparisonsStarted, allowance.RemainingComparisons);
        // Each comparison needs a fresh candidate and independent check. Reuse the confirmed baseline.
        count = Math.Min(count, boundary.RemainingSteps / 2);
        count = (int)Math.Min(count, boundary.RemainingActiveMilliseconds / allowance.ReservedActiveMillisecondsPerComparison);
        foreach (var (remaining, reserved) in new[] {
            (allowance.RemainingInputTokens, allowance.ReservedInputTokensPerComparison),
            (allowance.RemainingGeneratedTokens, allowance.ReservedGeneratedTokensPerComparison) })
        {
            if (remaining is not null && reserved is null)
                return new(PlanDisposition.Blocked, 0, 0, "required_cost_reservation_unavailable");
            if (remaining is not null && reserved > 0)
                count = (int)Math.Min(count, remaining.Value / reserved.Value);
        }
        if (count <= 0)
            return new(PlanDisposition.Exhausted, 0, 0, "comparison_or_host_budget_exhausted");
        if (allowance.MaximumParallelLanes == 0)
            return new(PlanDisposition.Queued, count, 0, "wait_for_owner_lane_allowance");
        if (capacity is null)
            return new(PlanDisposition.Compare, count, 1, "serial_fresh_admission_required");
        Validate(capacity);
        // These are planning observations only. Every actual preparation still needs fresh owner admission.
        if (capacity.ObservedUtc > now || now - capacity.ObservedUtc > TimeSpan.FromSeconds(5))
            return new(PlanDisposition.Compare, count, 1, "stale_capacity_serial_fresh_admission_required");
        var available = Math.Min(allowance.MaximumParallelLanes, Math.Min(capacity.RunnableTasks,
            Math.Max(0, capacity.AvailableLeases - capacity.InteractiveReserve)));
        if (available == 0) return new(PlanDisposition.Queued, count, 0, "wait_for_owner_admission");
        var parallel = capacity.IndependentPreparationScopes ? Math.Min(count, available) : 1;
        return new(PlanDisposition.Compare, count, parallel, parallel == 1 ? "serial" : "auto_from_admitted_capacity");
    }

    /// <summary>Validate actual admitted lane observations before parallel dispatch; never manufacture leases.</summary>
    public static void ValidateLanes(Binding expected, IReadOnlyList<Lane> lanes, bool parallel,
        PlanningAllowance allowance)
    {
        Validate(expected); Validate(allowance);
        if (allowance.Binding != expected) throw Invalid("stale_owner_allowance");
        if (lanes is null || lanes.Count < 1 || lanes.Count > allowance.RemainingComparisons ||
            parallel && lanes.Count > allowance.MaximumParallelLanes) throw Invalid("lane_owner_allowance");
        if (lanes.Count > MaximumSerializedBatchEntries) throw Invalid("lane_serialization_batch_bound");
        var captured = lanes.ToArray();
        foreach (var lane in captured)
        {
            if (lane is null) throw Invalid("lane_missing");
            Identifier(lane.ContextId); Identifier(lane.PreparationScopeId);
            Identifier(lane.PreparationId); Identifier(lane.InstanceId); Validate(lane.Binding);
            if (lane.Binding != expected || lane.NativeSlotId < 0 || !Enum.IsDefined(lane.Isolation) ||
                lane.Isolation == ProbeIsolation.SharedMutable) throw Invalid("lane_binding_or_isolation");
        }
        if (captured.Select(l => l.ContextId).Distinct(StringComparer.Ordinal).Count() != captured.Length ||
            captured.Select(l => l.PreparationScopeId).Distinct(StringComparer.Ordinal).Count() != captured.Length ||
            captured.Select(l => l.PreparationId).Distinct(StringComparer.Ordinal).Count() != captured.Length ||
            parallel && captured.Select(l => (l.InstanceId, l.NativeSlotId)).Distinct().Count() != captured.Length)
            throw Invalid("lane_reuse");
    }

    public static Costs SummarizeCosts(Binding expected, IReadOnlyList<Cost> stages, RoundTiming? timing = null)
    {
        Validate(expected);
        if (stages is null || stages.Count < 1 || stages.Count > MaximumSerializedBatchEntries)
            throw Invalid("cost_serialization_batch_bound");
        var captured = stages.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cost in captured)
        {
            if (cost is null) throw Invalid("cost_missing");
            Identifier(cost.ReceiptId); Identifier(cost.EffectId); Validate(cost.Binding);
            if (cost.Binding != expected || !seen.Add(cost.ReceiptId) ||
                !Enum.IsDefined(cost.Stage) || !Enum.IsDefined(cost.Outcome))
                throw Invalid("cost_binding");
            if (cost.ArtifactSha256 is not null) Hash(cost.ArtifactSha256);
            if (cost.ObjectiveSha256 is not null) Hash(cost.ObjectiveSha256);
            foreach (var number in new[] { cost.InputTokens, cost.OutputTokens, cost.ReasoningTokens,
                cost.WallMilliseconds, cost.PeakProcessBytes, cost.PeakDeviceBytes })
                if (number < 0) throw Invalid("cost_negative");
        }
        if (timing is not null)
        {
            Identifier(timing.ReceiptId); Validate(timing.Binding);
            if (timing.Binding != expected || timing.ElapsedMilliseconds < 0) throw Invalid("round_timing_binding");
        }
        long? Sum(Func<Cost, long?> read) => captured.Any(c => read(c) is null)
            ? null : captured.Aggregate(0L, (sum, c) => checked(sum + read(c)!.Value));
        long? Peak(Func<Cost, long?> read) => captured.Any(c => read(c) is null)
            ? null : captured.Max(c => read(c)!.Value);
        var input = Sum(c => c.InputTokens); var output = Sum(c => c.OutputTokens);
        var reasoning = Sum(c => c.ReasoningTokens); var stageTime = Sum(c => c.WallMilliseconds);
        var process = Peak(c => c.PeakProcessBytes); var device = Peak(c => c.PeakDeviceBytes);
        var measured = new[] { (Metric.InputTokens, input), (Metric.OutputTokens, output),
            (Metric.ReasoningTokens, reasoning), (Metric.GeneratedTokens, checked(output + reasoning)),
            (Metric.SummedStageWallMilliseconds, stageTime), (Metric.WholeRoundElapsedMilliseconds, timing?.ElapsedMilliseconds),
            (Metric.PeakProcessBytes, process), (Metric.PeakDeviceBytes, device) };
        var missing = measured.Where(pair => pair.Item2 is null).Select(pair => pair.Item1).ToArray();
        return new(captured.Length, captured.Count(c => c.Outcome is Outcome.Failed or Outcome.Cancelled),
            captured.All(c => c.Outcome != Outcome.Unknown && c.CleanupConfirmed),
            input, output, reasoning, stageTime, timing?.ElapsedMilliseconds, process, device,
            Array.AsReadOnly(missing));
    }

    /// <summary>Metric availability alone never establishes comparative savings or quality improvement.</summary>
    public static MetricEvidence CheckMetrics(Costs costs, IReadOnlyList<Metric> requested)
    {
        if (requested is null || requested.Count > Enum.GetValues<Metric>().Length ||
            requested.Any(metric => !Enum.IsDefined(metric)) || requested.Distinct().Count() != requested.Count)
            throw Invalid("metric_requirements");
        var missing = requested.Where(metric => Measurement(costs, metric) is null).ToArray();
        return new(missing.Length == 0, Array.AsReadOnly(missing));
    }

    /// <summary>
    /// Qualifies a proposal only. The owner must authenticate every referenced receipt, include all stages,
    /// recheck input/permissions/limits/cancellation, then pass any chosen action through normal admission.
    /// A model self-score, lesson, fresh prompt or setting is insufficient promotion evidence.
    /// </summary>
    public static Qualification QualifyPromotion(Boundary boundary, Promotion promotion, IReadOnlyList<Cost> stages,
        Budgets? budgets = null, RoundTiming? timing = null,
        IReadOnlyList<Metric>? requiredEfficiencyMetrics = null)
    {
        Validate(boundary); Validate(promotion.Binding);
        Hash(promotion.BaselineSha256); Hash(promotion.CandidateSha256); Hash(promotion.ObjectiveSha256);
        Identifier(promotion.CandidateEffectId); Identifier(promotion.CheckEffectId);
        Identifier(promotion.CandidateContextId); Identifier(promotion.CheckContextId);
        var captured = stages.ToArray();
        var costs = SummarizeCosts(boundary.Binding, captured, timing);
        Qualification No(string reason) => new(false, reason, costs);
        // The owner supplies the closed round receipt manifest, including failed/cancelled work.
        // A hand-picked list of cheap successful stages cannot establish the whole refinement cost.
        var expectedCosts = promotion.ExpectedCostReceiptIds?.ToArray() ?? throw Invalid("cost_manifest");
        if (expectedCosts.Length < 1 || expectedCosts.Length > MaximumSerializedBatchEntries)
            throw Invalid("cost_manifest_serialization_batch_bound");
        foreach (var id in expectedCosts) Identifier(id);
        if (expectedCosts.Distinct(StringComparer.Ordinal).Count() != expectedCosts.Length)
            throw Invalid("cost_manifest");
        if (!expectedCosts.ToHashSet(StringComparer.Ordinal).SetEquals(captured.Select(c => c.ReceiptId)))
            return No("all_stage_cost_manifest_incomplete");
        if (promotion.Binding != boundary.Binding) return No("stale_promotion");
        if (boundary.Cancelled || boundary.UnknownEffects || !Safe(boundary)) return No("unsafe_boundary");
        if (boundary.RemainingSteps <= 0 || boundary.RemainingActiveMilliseconds <= 0) return No("host_budget_exhausted");
        if (promotion.CandidateEffectId == promotion.CheckEffectId ||
            promotion.CandidateContextId == promotion.CheckContextId) return No("check_not_independent");
        if (!promotion.IndependentChecksPassed || !promotion.ImprovementObserved ||
            promotion.BaselineSha256 == promotion.CandidateSha256) return No("improvement_not_qualified");
        if (!costs.RequiredEvidenceComplete) return No("required_outcome_or_cleanup_unconfirmed");
        // Optional unavailable telemetry is retained as null and reported, not a global promotion gate.
        // An explicitly required efficiency metric or configured owner bound must still be measurable.
        if (requiredEfficiencyMetrics is not null && !CheckMetrics(costs, requiredEfficiencyMetrics).Available)
            return No("required_efficiency_metric_unavailable");
        if (budgets is not null)
        {
            var limits = new[] { (Metric.InputTokens, budgets.InputTokens),
                (Metric.GeneratedTokens, budgets.GeneratedTokens),
                (Metric.WholeRoundElapsedMilliseconds, budgets.WholeRoundElapsedMilliseconds),
                (Metric.PeakProcessBytes, budgets.PeakProcessBytes),
                (Metric.PeakDeviceBytes, budgets.PeakDeviceBytes), (Metric.OutputTokens, budgets.OutputTokens) };
            if (limits.Any(pair => pair.Item2 < 0)) throw Invalid("owner_budget");
            foreach (var (metric, limit) in limits.Where(pair => pair.Item2 is not null))
            {
                var observed = Measurement(costs, metric);
                if (observed is null) return No("required_budget_metric_unavailable");
                if (observed > limit) return No("owner_cost_budget_exceeded");
            }
        }
        if (!captured.Any(c => c.Stage == Stage.Baseline && c.Outcome == Outcome.Succeeded &&
                c.ArtifactSha256 == promotion.BaselineSha256) ||
            !captured.Any(c => c.EffectId == promotion.CandidateEffectId && c.Stage == Stage.Candidate &&
                c.Outcome == Outcome.Succeeded && c.ArtifactSha256 == promotion.CandidateSha256) ||
            !captured.Any(c => c.EffectId == promotion.CheckEffectId && c.Stage == Stage.IndependentCheck &&
                c.Outcome == Outcome.Succeeded && c.ArtifactSha256 == promotion.CandidateSha256 &&
                c.ObjectiveSha256 == promotion.ObjectiveSha256) ||
            !captured.Any(c => c.Stage == Stage.Preparation) || !captured.Any(c => c.Stage == Stage.Cleanup))
            return No("stage_receipt_missing");
        return new(true, "eligible_for_owner_review_only", costs);
    }

    private static long? Measurement(Costs costs, Metric metric) => metric switch
    {
        Metric.InputTokens => costs.InputTokens,
        Metric.OutputTokens => costs.OutputTokens,
        Metric.ReasoningTokens => costs.ReasoningTokens,
        Metric.GeneratedTokens => checked(costs.OutputTokens + costs.ReasoningTokens),
        Metric.SummedStageWallMilliseconds => costs.SummedStageWallMilliseconds,
        Metric.WholeRoundElapsedMilliseconds => costs.WholeRoundElapsedMilliseconds,
        Metric.PeakProcessBytes => costs.PeakProcessBytes,
        Metric.PeakDeviceBytes => costs.PeakDeviceBytes,
        _ => throw Invalid("metric")
    };

    private static void Validate(PlanningAllowance allowance)
    {
        if (allowance is null) throw Invalid("planning_allowance_missing");
        Validate(allowance.Binding);
        if (allowance.RemainingComparisons < 0 || allowance.MaximumParallelLanes < 0 ||
            allowance.ReservedActiveMillisecondsPerComparison <= 0 ||
            new[] { allowance.RemainingInputTokens, allowance.ReservedInputTokensPerComparison,
                allowance.RemainingGeneratedTokens, allowance.ReservedGeneratedTokensPerComparison }.Any(n => n < 0))
            throw Invalid("planning_allowance");
    }

    private static bool Safe(Boundary boundary) =>
        boundary.SafePlanningBoundary && !boundary.EffectPending && !boundary.PreparationActive;
    private static void Validate(Settings settings)
    {
        if (settings.SchemaVersion != 1 || settings.ComparisonBudget < 1 || settings.Parallelism != "auto")
            throw Invalid("settings_version_or_bounds");
    }
    private static void Validate(Binding binding)
    {
        Identifier(binding.ConversationId); Identifier(binding.RunId);
        if (binding.PlanningRevision < 0) throw Invalid("planning_revision");
        Hash(binding.InputSha256); Hash(binding.PermissionsSha256); Hash(binding.ConfirmedReceiptsSha256);
    }
    private static void Validate(Boundary boundary)
    {
        Validate(boundary.Binding);
        if (boundary.RemainingSteps < 0 || boundary.RemainingActiveMilliseconds < 0) throw Invalid("host_budget");
    }
    private static void Validate(Correction value)
    { Hash(value.FailureSha256); Hash(value.ObjectiveSha256); Hash(value.ApproachSha256); }
    private static void Validate(Capacity value)
    {
        Identifier(value.InstanceId); Hash(value.AdmissionSha256);
        if (value.AdmittedSlots < 0 || value.AvailableLeases < 0 || value.AvailableLeases > value.AdmittedSlots ||
            value.RunnableTasks < 0 || value.InteractiveReserve < 0)
            throw Invalid("capacity_observation");
    }
    private static void Hash(string value)
    {
        if (value is not { Length: 64 } || value.AsSpan().ContainsAnyExcept("0123456789abcdef"))
            throw Invalid("sha256");
    }
    private static void Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':')))
            throw Invalid("identifier");
    }
    private static ArgumentException Invalid(string code) => new("refinement_assist_" + code);
}
