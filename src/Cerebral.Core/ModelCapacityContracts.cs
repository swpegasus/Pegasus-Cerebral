namespace Cerebral.Core;

/// <summary>Version 1 normal configuration operations. ExpectedCurrent compares current state; it grants no authority.</summary>
public static class ModelCapacityProtocol
{
    public const int Version = 1;
    public const string PreviewOperation = "models.capacity.preview";
    public const string ApplyOperation = "models.capacity.apply";
    public const int MaximumAffectedProfiles = 512;

    // Eligibility is independent of estimate/measurement evidence. Ready means structurally
    // applicable at this snapshot; Apply still rechecks currentness and all busy guards.
    public const string Ready = "ready";
    public const string Busy = "busy";
    public const string Unavailable = "unavailable";
    public const string Applied = "applied";
    public const string Stale = "stale";
    public const string Invalid = "invalid";
    public const string Estimate = "estimate";
    public const string EstimateScope = "generic_capacity_planner";
    public const string CurrentConfiguration = "current_configuration";
    public const string UnknownFit = "unknown";
    public const string CompatibilityBounds = "protocol_compatibility";
    public const string FixedRequest = "fixed_request";
    public const string RetainedFallback = "retained_fallback";
    public const string MemoryEstimate = "memory_estimate";
    public const string UnreportedSelection = "unreported";
}

/// <summary>
/// Fixed sets the exact context and its matching capacity ceiling. Automatic sets an explicit ceiling
/// (zero selects the native limit); its saved ContextTokens remains a bounded last/fallback value,
/// never the planner's promise of the next allocation. The inactive alternative must be null.
/// </summary>
public sealed record ModelCapacityChoice(string ContextMode, int ParallelRequests,
    int? FixedContextTokens = null, int? AutomaticContextCeilingTokens = null);

public sealed record ModelCapacityPreviewRequest(int Version, string ModelId, ModelCapacityChoice? Choice = null,
    int? RequiredPerSlotContextTokens = null);
public sealed record ModelCapacityApplyRequest(int Version, string ModelId, ModelCapacityChoice Choice,
    string ExpectedCurrent, int? RequiredPerSlotContextTokens = null);

/// <summary>ContextTokens is fixed context in fixed mode, and last/fallback context in automatic mode.</summary>
public sealed record ModelCapacityConfiguration(string ContextMode, int ContextTokens,
    int MaximumContextTokens, int ParallelRequests, int GpuLayers, bool VisionProjectorConfigured);
public sealed record ModelCapacityFieldChange(string Field, string Before, string After);
public sealed record ModelCapacityBounds(int MinimumSlots, int MaximumSlots, int MinimumContextTokens,
    int MaximumContextTokens, string BoundKind, int? MeasuredHardwareMaximumSlots = null);

/// <summary>Always an estimate. Missing telemetry and incomplete placement terms remain unknown.</summary>
public sealed record ModelCapacityPreviewPlan(int ContextTokens, int ParallelRequests,
    long TotalContextTokens, bool Automatic, int? RequiredPerSlotContextTokens, bool? MeetsRequiredContextTokens,
    long? AvailableMemoryMb, long ReservedMemoryMb, long? EstimatedPlacementMb, long? EstimatedRemainingMb,
    string MemoryPool, string Basis, DateTimeOffset? ResourceSampledAt, string FitState,
    IReadOnlyList<string> Limitations, bool GuaranteedNextLoad = false)
{
    /// <summary>Selection provenance, not fit: fixed_request, retained_fallback, memory_estimate or unreported.</summary>
    public string SelectionSource { get; init; } = ModelCapacityProtocol.UnreportedSelection;
}
public sealed record ModelCapacityEvidence(string Kind, string Scope, string Applicability);

/// <summary>Lightweight backend estimate item; only the selected preview has profile effects and a token.
/// The candidate set may be incomplete. Omitted slots remain valid manual choices, not proved unsupported.</summary>
public sealed record ModelCapacityCandidate(ModelCapacityChoice Choice, ModelCapacityConfiguration Requested,
    IReadOnlyList<ModelCapacityFieldChange> FieldChanges, ModelCapacityPreviewPlan Planned);

/// <summary>A coherent observation of this instance. Configured and measured context are distinct.</summary>
// Effective is null when no coherent present instance is observed. Present observations use
// State=present; BindingState=captured|unavailable; ContextState=measured|unobserved;
// ContextSource=startup_log|launch_arguments. A measured context is not workload qualification.
public sealed record ModelCapacityEffective(string State, string InstanceId, string BindingState,
    int? ConfiguredContextTokens, int? MeasuredContextTokens, int? ParallelRequests,
    string ContextState, string ContextSource);

public sealed record ModelCapacityProfileBudget(int Revision, int ContextTokens, int ResponseTokens,
    string ReasoningMode, int ReasoningTokens);
public sealed record ModelCapacityProfileEffect(string ProfileId, string Name, string ModelWorkerId,
    string ModelId, bool FollowsModelBudgets, ModelCapacityProfileBudget Before, ModelCapacityProfileBudget After);
public sealed record ModelCapacityScheduler(int MaxConcurrentJobs, bool ChangesScheduler = false);
/// <summary>State: ready, busy or unavailable. Invalid request shape/version fails normal command validation.</summary>
public sealed record ModelCapacityApplyEligibility(string State, string Reason);

public sealed record ModelCapacityPreview(int Version, string ModelId,
    ModelCapacityConfiguration Saved, ModelCapacityChoice Choice, ModelCapacityConfiguration Requested,
    IReadOnlyList<ModelCapacityFieldChange> FieldChanges, ModelCapacityPreviewPlan Planned,
    ModelCapacityEffective? Effective, ModelCapacityEvidence Evidence, ModelCapacityBounds SupportedBounds,
    IReadOnlyList<ModelCapacityProfileEffect> AffectedProfiles, bool AffectedProfilesComplete,
    ModelCapacityScheduler Scheduler, string? ExpectedCurrent, ModelCapacityApplyEligibility ApplyEligibility,
    IReadOnlyList<ModelCapacityCandidate> Candidates, bool CandidatesComplete);

/// <summary>
/// State is applied (MutationCommitted=true), or stale, busy, invalid, unavailable (false).
/// Applied means the store transaction committed, even if a later notification has a warning.
/// A transport failure has no response and must stay unconfirmed until readback; never auto-retry.
/// </summary>
public sealed record ModelCapacityApplyResult(int Version, string ModelId, string State,
    bool MutationCommitted, string Reason, ModelCapacityConfiguration? Saved,
    IReadOnlyList<ModelCapacityProfileEffect> AffectedProfiles, IReadOnlyList<string> Warnings);
