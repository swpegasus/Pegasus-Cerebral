using System.Text.Json;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

// Worker-internal captured state. No prompts, generated text, configuration bodies or credentials.
// Accounting, runtime and scheduler generations have separate scopes; none is a connection fence.
internal sealed record InferenceCaptureSource(string State, string? Reason, DateTimeOffset? ObservedAt,
    string? GenerationId = null, long? Revision = null, long? AgeMilliseconds = null);
internal sealed record InferenceCaptureBudget(int InputTokens, int EffectiveContext, int DesiredResponse,
    int Response, int ReasoningReserve, int SafetyReserve, string CountBasis);
internal sealed record InferenceCaptureTerminal(string Finish, bool CancellationConfirmed, bool ProviderError,
    int? EvaluatedTokens, int? GeneratedTokens, string GeneratedTokenScope, int? ReasoningTokens, DateTimeOffset At);
internal sealed record InferenceCaptureCache(int PreparedInputTokens, int? CachedTokens, int? ProcessedTokens,
    double? PromptMilliseconds);
internal sealed record InferenceExecutionCoordinate(string ExecutionId, string ProfileId, int ProfileRevision,
    string? ConversationId = null, string? RequestId = null, string? RunId = null, string? EffectId = null);
internal sealed record InferenceInboundPreparation(string AccountingGenerationId, string AccountingEntryId, string PreparedLeaseId);
internal sealed record InferenceInvocation(string CorrelationId, string Purpose, InferenceExecutionCoordinate? Execution,
    InferenceInboundPreparation? InboundPreparation = null);

// Async scope carries host provenance only and is frozen into an accounting row at Begin.
// It never enters model requests, remote transport or a shared SDK.
internal static class WorkerInferenceInvocation
{
    private static readonly AsyncLocal<InferenceInvocation?> current = new();
    internal static InferenceInvocation? Current => current.Value;
    internal static IDisposable Enter(InferenceInvocation? value) => new Scope(value);
    internal static IDisposable EnterInboundPreparation(InferenceInboundPreparation value) =>
        new Scope((current.Value ?? new(value.AccountingGenerationId + ":" + value.AccountingEntryId,
            "unattributed", null)) with { InboundPreparation = value });
    private sealed class Scope : IDisposable
    {
        private readonly InferenceInvocation? previous = current.Value;
        private bool disposed;
        internal Scope(InferenceInvocation? value) => current.Value = value;
        public void Dispose() { if (!disposed) { current.Value = previous; disposed = true; } }
    }
}

internal sealed record InferenceCaptureRow(string RowId, string AccountingEntryId, string AccountingGenerationId,
    long AccountingRevision, string? OriginInstallationId, string Kind, string ModelId, string? InstanceId,
    string CorrelationId, string AttemptId, string Purpose, InferenceExecutionCoordinate? Execution,
    string ExecutionAttribution, string? AccountingLeaseId, string? ReservationId, string? LeaseId, int? NativeSlotId, string Phase, bool CancellationRequested,
    string? UncertaintyCode, InferenceCaptureBudget? Budget, InferenceCaptureTerminal? Terminal,
    InferenceCaptureCache? Cache, string Progress, int? ProcessedInputTokens, int? OutputTokens,
    InferenceCaptureSource ProgressSource, string Release, string? ReleaseProof, DateTimeOffset? ReleasedAt,
    DateTimeOffset ObservedAt);
internal sealed record InferenceModelCapture(string ModelId, string InstanceId, string RuntimeGenerationId,
    long RuntimeEpoch, string State, int? RunningLaunchSlotMaximum, int? EffectiveContextTokens,
    string? LaunchBindingSha256, InferenceCaptureSource LaunchSource, int? ManagedAllocatedSlots,
    int? ManagedAvailableSlots, int? ManagedQuarantinedSlots, bool AllocatorCoverageComplete,
    InferenceCaptureSource AllocatorSource, int? ProviderObservedCapacity, InferenceCaptureSource ProviderCapacitySource,
    int? ProviderObservedBusySlots, InferenceCaptureSource ProviderActivitySource);
internal sealed record InferenceProfileExecutionCounts(int ObservedKnownWorkingProfileIdentities,
    int? CompleteWorkingProfileIdentities, int ObservedKnownExecutions, int? CompleteExecutions,
    int? CompleteContextCount, InferenceCaptureSource Source);
internal sealed record InferenceObservationChanged(string Schema, string? OriginInstallationId,
    string ObserverGenerationId, long Revision);
internal sealed record InferenceObservationSnapshot(string Schema, string? IssuingInstallationId,
    string? OwnerConnectionRegistrationFence, string OwnerConnectionRegistrationFenceState,
    string OwnerConnectionRegistrationFenceReason, string ObserverGenerationId, long Revision,
    DateTimeOffset SampledAt, string AccountingGenerationId, bool AccountingCoverageComplete,
    string State, bool Partial, bool Truncated, int MaximumRows, int RetainedCurrentRows,
    string[] Reasons, InferenceProfileExecutionCounts Counts, InferenceCaptureSource ModelCoverage, InferenceModelCapture[] Models,
    InferenceCaptureRow[] Rows, string RemoteState, string EventDeliveryState, bool? ExecutionAdmissionOpen, bool? ModelAdmissionOpen,
    InferenceCaptureSource AdmissionSource, string Semantics);

// Supplemental relationship metadata never enters the frozen v1 DTOs or the shared prepared protocol.
internal sealed record InferenceCaptureLink(string RowId, string State, string? Reason,
    string? RemoteInstallationId, InferenceInboundPreparation? InboundPreparation);
internal sealed record InferenceObservationLinksSnapshot(string Schema, InferenceObservationSnapshot Snapshot,
    InferenceCaptureLink[] Links, bool Partial, bool Truncated, string[] Reasons);

// V3 is a private opt-in annotation. These DTOs contain no digest, key, token or prompt data.
internal sealed record InferenceCacheNativeIdentity(string State, string Scope, string? ExecutableSha256,
    long? ExecutableBytes, int? NativeProcessId, string? NativeStartTicks, string Basis,
    string DependencyIdentityState, string? Reason);
internal sealed record InferenceCacheModel(string ModelId, string InstanceId, string RuntimeGenerationId,
    long RuntimeEpoch, string? LaunchBindingSha256, InferenceCacheNativeIdentity NativeIdentity,
    string State, string? Reason);
internal sealed record InferenceCacheGenerationSource(string ModelId, string InstanceId,
    string RuntimeGenerationId, long RuntimeEpoch, string? LaunchBindingSha256,
    string AccountingGenerationId, string AccountingEntryId, string AttemptId, string LeaseId,
    int NativeSlotId, string Purpose, string ExecutionAttribution, bool NativeStopConfirmed,
    DateTimeOffset ObservedAt);
internal sealed record InferenceCacheGeneration(string RowId, long ActualGenerateSequence,
    string DispatchState, NativePrefixComparison Comparison, NativeCacheProviderEvidence ProviderEvidence,
    InferenceCacheGenerationSource Source);
internal sealed record InferenceNativeDecisionSource(string ModelId, string InstanceId, string RuntimeGenerationId,
    long RuntimeEpoch, string? LaunchBindingSha256, InferenceCacheNativeIdentity NativeIdentity,
    string AccountingGenerationId, string AccountingEntryId, string AttemptId, string LeaseId,
    int NativeSlotId, string Purpose, string ExecutionAttribution);
internal sealed record InferenceNativeDecision(string RowId, long ActualGenerateSequence, string State,
    string TaskJoin, int? IndependentNativeTaskId, NativeCacheDecisionPacket? Packet,
    DateTimeOffset RequestedAt, DateTimeOffset IngestedAt, InferenceNativeDecisionSource Source);
internal sealed record InferenceNativeDecisionDiagnostics(string Schema, InferenceNativeDecision[] Rows,
    int Received, int Malformed, int Conflicting, int UnmatchedOrLate, int StorageOmitted, int ExpiredOrRetired,
    int CaptureFailed, int ReadOmitted, bool Partial, bool Truncated, string[] Reasons);
internal sealed record InferenceCacheDiagnostics(string State, string ObserverGenerationId, long Revision,
    DateTimeOffset SampledAt, InferenceCacheModel[] Models, InferenceCacheGeneration[] Generations,
    bool Partial, bool Truncated, string[] Reasons)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public InferenceNativeDecisionDiagnostics? Decisions { get; init; }
}
internal sealed record InferenceCacheObservationSnapshot(string Schema, InferenceObservationLinksSnapshot Base,
    InferenceCacheDiagnostics Diagnostics);

/// <summary>Bounded passive capture. Snapshot takes only this registry's lock and copies retained DTOs.
/// Source callbacks and notifications never execute under that lock or on the read path.</summary>
internal sealed class InferenceObservationRegistry(string accountingGeneration, bool accountingCoverage,
    int maximumCurrentRows = 4096, int maximumReleasedRows = 128)
{
    internal const int MaximumReturnedRows = 128;
    internal const int MaximumPayloadBytes = 256 * 1024;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, InferenceCaptureRow> rows = new(StringComparer.Ordinal);
    // These are annotations on retained rows, not ownership or release authority.
    private readonly Dictionary<string, InferenceCaptureLink> links = new(StringComparer.Ordinal);
    private bool linkCaptureLost;
    private bool? nativeCacheEnabled;
    private bool nativeCacheInitializationConflict, nativeCacheLost;
    private long nativeCacheRevision = 1;
    private readonly Dictionary<string, NativeCacheOwner> nativeCacheModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeCacheGenerationObservation> nativeCacheGenerations = new(StringComparer.Ordinal);
    private sealed record NativeDecisionEntry(NativeCacheDecisionObservation Requested, NativeCacheDecisionPacket? Packet,
        string State, string TaskJoin, int? IndependentTaskId, DateTimeOffset IngestedAt);
    private readonly Dictionary<(string Entry, long Sequence), NativeDecisionEntry> nativeDecisions = new();
    private int decisionReceived, decisionMalformed, decisionConflicting, decisionUnmatched, decisionOmitted, decisionExpired, decisionFailed;
    private readonly Dictionary<(string Instance, int Slot), (long Sequence, string? Row, string Lease)> nativeCacheSlots = new();
    private readonly Dictionary<string, InferenceModelCapture> models = new(StringComparer.Ordinal);
    private sealed record Accepted(object Token, string Generation, InferenceExecutionCoordinate? Coordinate, string Kind);
    private readonly Dictionary<string, Accepted> executions = new(StringComparer.Ordinal);
    private readonly int currentLimit = maximumCurrentRows is >= 1 and <= 4096 ? maximumCurrentRows : throw new ArgumentOutOfRangeException(nameof(maximumCurrentRows));
    private readonly int releasedLimit = maximumReleasedRows is >= 0 and <= 128 ? maximumReleasedRows : throw new ArgumentOutOfRangeException(nameof(maximumReleasedRows));
    private string generation = Guid.NewGuid().ToString("N");
    private string? issuer;
    private string? schedulerGeneration;
    private DateTimeOffset? schedulerAt;
    private bool lost;
    private bool issuerChanged;
    private bool eventFailed;
    private InferenceCaptureSource modelCoverage = Unavailable("runtime-model-coverage-not-captured");
    private long revision = 1;
    private int notificationPending;
    private Action<InferenceObservationChanged>? notify;

    internal void ConfigureIssuer(string installationId, Action<InferenceObservationChanged>? notification = null) => Capture(() =>
    {
        if (!Identifier(installationId)) { lost = true; return; }
        if (issuer is not null && issuer != installationId)
        { issuerChanged = true; generation = Guid.NewGuid().ToString("N"); lost = true; return; }
        issuer = installationId; notify = notification;
    });

    internal void RuntimeStarted(string runtimeGeneration, long epoch) => Capture(() =>
    {
        if (modelCoverage.GenerationId is { } old && old != runtimeGeneration)
        { lost = true; modelCoverage = Unavailable("multiple-runtime-capture-origins"); return; }
        modelCoverage = new("observed", null, DateTimeOffset.UtcNow, runtimeGeneration, epoch);
    });

    internal string StartScheduler()
    {
        var id = Guid.NewGuid().ToString("N");
        Capture(() =>
        {
            if (schedulerGeneration is not null && executions.Count != 0) lost = true;
            schedulerGeneration = id; schedulerAt = DateTimeOffset.UtcNow;
        });
        return id;
    }

    internal void AcceptExecution(string scheduler, string key, object token,
        InferenceExecutionCoordinate? coordinate, string kind) => Capture(() =>
    {
        if (scheduler != schedulerGeneration) { lost = true; return; }
        if (!Identifier(key) || executions.Count >= 256 && !executions.ContainsKey(key)) { lost = true; return; }
        executions[key] = new(token, scheduler, ValidCoordinate(coordinate) ? coordinate : null, kind); schedulerAt = DateTimeOffset.UtcNow;
    });
    internal void RetireExecution(string scheduler, string key, object token) => Capture(() =>
    {
        if (executions.TryGetValue(key, out var old) && old.Generation == scheduler && ReferenceEquals(old.Token, token))
        { executions.Remove(key); schedulerAt = DateTimeOffset.UtcNow; }
    });

    internal void Accounting(ModelOwnershipEntry value, long sourceRevision, InferenceInvocation? invocation = null) => Capture(() =>
    {
        if (rows.TryGetValue(value.Id, out var old))
        {
            if (sourceRevision <= old.AccountingRevision || old.Release == "confirmed") return;
            rows[value.Id] = old with { AccountingRevision = sourceRevision, InstanceId = value.InstanceId,
                AccountingLeaseId = value.LeaseId, ReservationId = value.ReservationId,
                Phase = value.Phase == ModelOwnershipPhase.Unknown
                    ? value.Uncertainty == "native-slot-quarantined" ? "quarantined" : "unknown"
                    : value.Phase.ToString().ToLowerInvariant(), UncertaintyCode = value.Uncertainty is null ? null : "ownership-unresolved",
                ObservedAt = DateTimeOffset.UtcNow };
            CaptureLink(value, invocation, creating: false);
            return;
        }
        // Never evict current unknown/quarantined ownership to make an optional observer fit.
        if (rows.Values.Count(x => x.Release != "confirmed") >= currentLimit) { lost = true; return; }
        var now = DateTimeOffset.UtcNow;
        var raw = accountingGeneration + ":" + value.Id;
        var accepted = invocation is { } input && Identifier(input.CorrelationId) && ValidCoordinate(input.Execution) ? input : null;
        rows[value.Id] = new(raw, value.Id, accountingGeneration, sourceRevision, issuer, value.Kind.ToString(),
            value.ModelId, value.InstanceId, accepted?.CorrelationId ?? raw, Guid.NewGuid().ToString("N"),
            accepted?.Purpose is "task-generation" or "compaction" ? accepted.Purpose : "unattributed", accepted?.Execution, accepted?.Execution is null ? "unavailable" : "local-host-invocation",
            value.LeaseId, value.ReservationId, null, null, value.Phase.ToString().ToLowerInvariant(), false, null, null, null, null,
            "unknown", null, null, Unavailable("provider-progress-unobserved"), "unobserved", null, null, now);
        CaptureLink(value, invocation, creating: true);
    });

    // Called under the registry gate by Accounting. Optional link loss must not change v1 coverage.
    private void CaptureLink(ModelOwnershipEntry value, InferenceInvocation? invocation, bool creating)
    {
        try
        {
            var row = rows[value.Id];
            if (value.Kind == ModelOwnershipKind.OutboundPreparation)
            {
                var installation = Identifier(value.WorkerInstallationId) ? value.WorkerInstallationId : null;
                links.TryGetValue(value.Id, out var old);
                var changed = old?.Reason == "remote-installation-changed" ||
                    old?.RemoteInstallationId is { } previous && installation != previous;
                var valid = installation is not null && Identifier(value.LeaseId) && !changed;
                links[value.Id] = new(row.RowId, valid ? "observed" : "unavailable",
                    changed ? "remote-installation-changed" : valid ? null : "remote-installation-or-lease-unobserved",
                    changed ? null : installation, null);
            }
            else if (creating && value.Kind == ModelOwnershipKind.LocalPreparation && invocation?.InboundPreparation is { } parent)
            {
                var valid = parent.AccountingGenerationId == accountingGeneration &&
                    Identifier(parent.AccountingEntryId) && Identifier(parent.PreparedLeaseId) &&
                    parent.AccountingEntryId != value.Id && rows.TryGetValue(parent.AccountingEntryId, out var inbound) &&
                    inbound.Kind == nameof(ModelOwnershipKind.InboundReservation) &&
                    inbound.AccountingGenerationId == parent.AccountingGenerationId &&
                    inbound.AccountingLeaseId == parent.PreparedLeaseId && inbound.ModelId == value.ModelId &&
                    inbound.Release != "confirmed";
                links[value.Id] = new(row.RowId, valid ? "observed" : "unavailable",
                    valid ? null : "inbound-parent-unconfirmed", null, valid ? parent : null);
            }
        }
        catch (Exception error) when (Ordinary(error)) { linkCaptureLost = true; }
    }

    internal void Slot(string entry, string leaseId, int slot) => Update(entry, old => old with
        { LeaseId = leaseId, NativeSlotId = slot, Phase = old.Release == "confirmed" || old.Phase is "unknown" or "quarantined" ? old.Phase : "preparing", ObservedAt = DateTimeOffset.UtcNow });
    internal void Prepared(string entry, PreparedModelMetadata metadata) => Update(entry, old => old with
    {
        InstanceId = metadata.InstanceId, Phase = old.Release == "confirmed" ? old.Phase : "ready",
        Budget = new(metadata.InputTokens, metadata.EffectiveContext, metadata.DesiredResponseBudget,
            metadata.ResponseBudget, metadata.ReasoningReserveTokens, metadata.SafetyReserveTokens,
            metadata.CountBasis == LlamaProtocol.CountBasis ? "exact-native-template-token-ids" : "provider-declared-unclassified"),
        ObservedAt = DateTimeOffset.UtcNow
    });
    internal void Cancellation(string entry) => Update(entry, old => old with
    { CancellationRequested = true, Phase = old.Release == "confirmed" || old.Phase is "unknown" or "quarantined" ? old.Phase : "cancelling", ObservedAt = DateTimeOffset.UtcNow });
    internal void Progress(string entry, ProviderGenerationMeasurement sample)
    {
        lock (gate)
        {
            if (!rows.TryGetValue(entry, out var current) || current.Release == "confirmed") return;
            // Numeric frames are optional; record phase changes immediately, otherwise at most 4Hz.
            if (current.Progress == sample.Phase && current.ProgressSource.ObservedAt is { } previous &&
                DateTimeOffset.UtcNow - previous < TimeSpan.FromMilliseconds(250)) return;
        }
        Update(entry, old => old.Release == "confirmed" ? old : old with
        {
            Progress = sample.Phase is "prefill" or "output" ? sample.Phase : "unknown",
            ProcessedInputTokens = sample.InputTokens, OutputTokens = sample.OutputTokens,
            ProgressSource = new("observed", null, DateTimeOffset.UtcNow, accountingGeneration),
            ObservedAt = DateTimeOffset.UtcNow
        });
    }
    internal void Terminal(string entry, ModelGenerationResult? result) => Update(entry, old => old with
    {
        // Native release can precede the wrapper's terminal result. Never resurrect a released row.
        Phase = old.Release == "confirmed" || old.Phase is "unknown" or "quarantined" ? old.Phase : "terminal",
        Terminal = new(result?.Finish.ToString().ToLowerInvariant() ?? "unknown", result?.CancellationConfirmed == true,
            result?.Error is not null || result is null, result?.EvaluatedTokens, result?.GeneratedTokens,
            "provider-unspecified", null, DateTimeOffset.UtcNow),
        Cache = result?.PromptCache is { } cache ? new(cache.PreparedInputTokens, cache.CachedTokens,
            cache.ProcessedPromptTokens, cache.PromptProcessingMilliseconds) : null, ObservedAt = DateTimeOffset.UtcNow
    });
    internal void Released(string entry, long sourceRevision, string proof) => Capture(() =>
    {
        if (!rows.TryGetValue(entry, out var old)) return;
        rows[entry] = old with
        {
            // Confirmed owner retirement resolves ownership uncertainty, independently of provider terminal facts.
            AccountingRevision = Math.Max(sourceRevision, old.AccountingRevision), Phase = "released", UncertaintyCode = null,
            Release = "confirmed", ReleaseProof = proof, ReleasedAt = DateTimeOffset.UtcNow, ObservedAt = DateTimeOffset.UtcNow
        };
    }, trimHistory: true);

    internal void Allocator(string model, string instance, string runtimeGeneration, long epoch,
        int capacity, int available, int quarantined) => Capture(() =>
    {
        if (!models.TryGetValue(instance, out var old))
        {
            if (models.Count >= 64) { lost = true; return; }
            old = new(model, instance, runtimeGeneration, epoch, "captured", null, null, null,
                Unavailable("launch-unobserved"), null, null, null, false, Unavailable("allocator-unobserved"),
                null, Unavailable("provider-capacity-unobserved"), null, Unavailable("provider-activity-unobserved"));
        }
        if (old.RuntimeGenerationId != runtimeGeneration || epoch < old.RuntimeEpoch) return;
        if (modelCoverage.GenerationId == runtimeGeneration)
            modelCoverage = modelCoverage with { ObservedAt = DateTimeOffset.UtcNow, Revision = epoch };
        models[instance] = old with { RuntimeEpoch = epoch, ManagedAllocatedSlots = capacity,
            ManagedAvailableSlots = available, ManagedQuarantinedSlots = quarantined, AllocatorCoverageComplete = true,
            AllocatorSource = new("observed", "individually-sampled-allocator", DateTimeOffset.UtcNow, generation, revision + 1) };
    });
    internal void Launched(string model, string instance, string runtimeGeneration, long epoch, int parallel,
        int? context, string? bindingHash) => Capture(() =>
    {
        if (!models.TryGetValue(instance, out var old)) return;
        var now = DateTimeOffset.UtcNow;
        models[instance] = old with { State = "startup-captured", RunningLaunchSlotMaximum = parallel, EffectiveContextTokens = context,
            LaunchBindingSha256 = bindingHash, LaunchSource = new("captured", bindingHash is null ? "launch-binding-unavailable" : null, now, runtimeGeneration, epoch),
            ProviderObservedCapacity = null, ProviderCapacitySource = Unavailable("native-slot-capacity-not-captured") };
    });
    internal void Stopped(string instance, string runtimeGeneration, long epoch) => Capture(() =>
    {
        models.Remove(instance); modelCoverage = modelCoverage with { ObservedAt = DateTimeOffset.UtcNow, Revision = epoch };
        // Optional annotation cleanup must never escape into Capture's base-coverage failure path.
        try { RemoveNativeCacheInstance(instance); }
        catch (Exception error) when (Ordinary(error)) { nativeCacheLost = true; nativeCacheRevision++; }
    });

    private void Update(string entry, Func<InferenceCaptureRow, InferenceCaptureRow> change) => Capture(() =>
    { if (rows.TryGetValue(entry, out var old)) rows[entry] = change(old); });

    // Late wrapper/cancellation callbacks may follow confirmed-history expiry; they cannot resurrect rows.
    internal void Incomplete() => Capture(() => lost = true);

    // Observation failure cannot alter real admission, token use, return, quarantine or cleanup.
    private void Capture(Action action, bool trimHistory = false)
    {
        try
        {
            lock (gate) { action(); if (trimHistory) TrimReleased(DateTimeOffset.UtcNow); revision++; }
            QueueNotification();
        }
        catch (Exception error) when (Ordinary(error))
        { lock (gate) { lost = true; revision++; } }
    }
    private void TrimReleased(DateTimeOffset now)
    {
        var released = rows.Values.Where(x => x.Release == "confirmed").OrderByDescending(x => x.ReleasedAt).ToArray();
        foreach (var row in released.Where((x, index) => index >= releasedLimit || now - x.ReleasedAt > TimeSpan.FromMinutes(5)))
        {
            rows.Remove(row.AccountingEntryId); links.Remove(row.AccountingEntryId);
            try { if (nativeCacheGenerations.Remove(row.AccountingEntryId)) nativeCacheRevision++; }
            catch (Exception error) when (Ordinary(error)) { nativeCacheLost = true; nativeCacheRevision++; }
            RemoveNativeDecisions(entry: row.AccountingEntryId);
        }
    }

    internal InferenceObservationSnapshot Snapshot(DateTimeOffset? sampledAt = null, int maximumRows = MaximumReturnedRows)
    {
        if (maximumRows is < 1 or > MaximumReturnedRows) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        lock (gate)
        {
            var now = sampledAt ?? DateTimeOffset.UtcNow;
            TrimReleased(now);
            InferenceCaptureSource Age(InferenceCaptureSource source, bool progress = false) =>
                source.ObservedAt is { } observed ? source with { AgeMilliseconds = Math.Max(0, (long)(now - observed).TotalMilliseconds),
                    State = progress && now - observed > TimeSpan.FromSeconds(2) ? "stale" : source.State } : source;
            var selected = rows.Values.OrderBy(x => x.Release == "confirmed").ThenBy(x => x.RowId, StringComparer.Ordinal)
                .Take(maximumRows).Select(x => x with { ProgressSource = Age(x.ProgressSource, true) }).ToArray();
            var truncated = rows.Count > selected.Length;
            InferenceObservationSnapshot Make()
            {
                var reasons = new List<string>();
                if (issuer is null || issuerChanged) reasons.Add("issuing-owner-unavailable");
                if (!accountingCoverage) reasons.Add("accounting-coverage-incomplete");
                if (lost) reasons.Add("capture-incomplete");
                if (schedulerGeneration is null) reasons.Add("scheduler-unobserved");
                if (executions.Values.Any(x => x.Coordinate is null && x.Kind == "local")) reasons.Add("accepted-execution-unprojectable");
                if (truncated) reasons.Add("rows-truncated");
                var partial = reasons.Count != 0;
                var local = executions.Values.Where(x => x.Generation == schedulerGeneration && x.Kind == "local" && x.Coordinate is not null).ToArray();
                var profiles = local.Select(x => x.Coordinate!.ProfileId).Distinct(StringComparer.Ordinal).Count();
                var counts = new InferenceProfileExecutionCounts(profiles, partial ? null : profiles,
                    local.Length, partial ? null : local.Length, null,
                    Age(new(partial ? "partial" : "observed", partial ? "required-coverage-unavailable" : null, schedulerAt, schedulerGeneration)));
                var sampledModels = models.Values.OrderBy(x => x.InstanceId, StringComparer.Ordinal).Select(x => x with
                {
                    AllocatorCoverageComplete = x.AllocatorCoverageComplete && !lost,
                    AllocatorSource = Age(x.AllocatorSource) with { State = lost ? "partial" : x.AllocatorSource.State },
                    LaunchSource = Age(x.LaunchSource), ProviderCapacitySource = Age(x.ProviderCapacitySource),
                    ProviderActivitySource = Age(x.ProviderActivitySource)
                }).ToArray();
                return new("cerebral.inference-ownership/1", issuerChanged ? null : issuer, null, "unavailable",
                    "owner-connection-registration-fence-not-captured", generation, revision, now, accountingGeneration,
                    accountingCoverage, partial ? "partial" : "observed", partial, truncated, maximumRows,
                    rows.Values.Count(x => x.Release != "confirmed"), reasons.ToArray(), counts, Age(modelCoverage) with { State = lost ? "partial" : modelCoverage.State }, sampledModels, selected,
                    "unsupported", eventFailed ? "unavailable" : notify is null ? "unsupported" : "available",
                    null, null, Unavailable("combined-admission-policy-not-captured"),
                    "Issuing local owner only. Captured source ages are independent; allocator counters are not atomic admission or fit proof. " +
                    "Counts cover claimed local native Workflow and legacy AgentJob executions; maintenance and remote dispatch excluded. " +
                    "Profiles, accepted executions, exact leases and reusable native slots are distinct. Context totals unobserved. " +
                    "Terminal is not release or task/build success; returned leases may retain resident prefix KV. Provider activity and per-slot KV bytes unobserved.");
            }
            var result = Make();
            while (JsonSerializer.SerializeToUtf8Bytes(result, WireJson).Length > MaximumPayloadBytes && selected.Length != 0)
            { selected = selected.Take(selected.Length / 2).ToArray(); truncated = true; result = Make(); }
            return result;
        }
    }

    internal InferenceObservationLinksSnapshot SnapshotV2(DateTimeOffset? sampledAt = null, int maximumRows = MaximumReturnedRows)
    {
        if (maximumRows is < 1 or > MaximumReturnedRows) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        lock (gate)
        {
            var now = sampledAt ?? DateTimeOffset.UtcNow;
            var limit = maximumRows;
            while (true)
            {
                // Snapshot is reentrant on this gate. Its revision and the selected annotations
                // are one passive sample; the existing default-v1 selection is never modified.
                var snapshot = Snapshot(now, limit);
                var selected = snapshot.Rows.Where(row => links.ContainsKey(row.AccountingEntryId))
                    .Select(row => links[row.AccountingEntryId]).ToArray();
                var reasons = new List<string>();
                if (snapshot.Partial) reasons.Add("snapshot-partial");
                if (linkCaptureLost) reasons.Add("link-capture-incomplete");
                if (selected.Any(link => link.State != "observed")) reasons.Add("link-unavailable");
                if (snapshot.Truncated) reasons.Add("rows-truncated");
                var result = new InferenceObservationLinksSnapshot("cerebral.inference-ownership/2",
                    snapshot, selected, reasons.Count != 0, snapshot.Truncated, reasons.ToArray());
                if (JsonSerializer.SerializeToUtf8Bytes(result, WireJson).Length <= MaximumPayloadBytes) return result;
                // Reduce the v2 row selection and its annotations together, retaining useful exact
                // pairs and explicit truncation. No retained current ownership is ever evicted.
                if (snapshot.Rows.Length <= 1)
                    throw new InvalidOperationException("inference_link_observation_payload_unavailable");
                limit = Math.Max(1, snapshot.Rows.Length / 2);
            }
        }
    }

    // Initialization is captured once. Cache-only changes neither revise nor notify v1/v2.
    internal void NativeCacheConfigured(bool enabled)
    {
        try
        {
            lock (gate)
            {
                if (nativeCacheInitializationConflict || nativeCacheEnabled == enabled) return;
                if (nativeCacheEnabled is not null)
                {
                    nativeCacheInitializationConflict = true;
                    nativeCacheModels.Clear(); nativeCacheGenerations.Clear(); nativeCacheSlots.Clear();
                }
                else nativeCacheEnabled = enabled;
                nativeCacheRevision++;
            }
        }
        catch (Exception error) when (Ordinary(error)) { NativeCacheCaptureFailed(); }
    }

    internal void NativeCacheCaptureFailed()
    {
        lock (gate) { if (!nativeCacheLost) { nativeCacheLost = true; nativeCacheRevision++; } }
    }

    internal void NativeCacheModel(NativeCacheOwner owner)
    {
        try
        {
            lock (gate)
            {
                if (!NativeCacheActive) return;
                if (!NativeCacheOwnerCurrent(owner)) { NativeCacheCaptureFailed(); return; }
                if (nativeCacheModels.TryGetValue(owner.InstanceId, out var old) && old == owner) return;
                if (!nativeCacheModels.ContainsKey(owner.InstanceId) && nativeCacheModels.Count >= 64)
                { NativeCacheCaptureFailed(); return; }
                RemoveNativeCacheInstance(owner.InstanceId);
                nativeCacheModels[owner.InstanceId] = owner;
                if (owner.ObservationState == "unavailable") NativeCacheCaptureFailed();
                nativeCacheRevision++;
            }
        }
        catch (Exception error) when (Ordinary(error)) { NativeCacheCaptureFailed(); }
    }

    internal NativeCacheBinding? TryNativeCacheBinding(string entry, NativeCacheOwner owner)
    {
        try
        {
            lock (gate)
            {
                if (!NativeCacheActive) return null;
                if (!NativeCacheOwnerCurrent(owner) ||
                    !nativeCacheModels.TryGetValue(owner.InstanceId, out var captured) || captured != owner ||
                    !rows.TryGetValue(entry, out var row) || row.Release == "confirmed" ||
                    row.Kind != nameof(ModelOwnershipKind.LocalPreparation) || row.ModelId != owner.ModelId ||
                    row.InstanceId is not { } instanceId || instanceId != owner.InstanceId || row.AccountingGenerationId != accountingGeneration ||
                    row.LeaseId is not { } leaseId || !Identifier(leaseId) || row.AccountingLeaseId != leaseId ||
                    row.NativeSlotId is not { } slotId || slotId is < 0 or >= 16)
                { NativeCacheCaptureFailed(); return null; }
                return new(row.RowId, row.AccountingGenerationId, row.AccountingEntryId, row.AttemptId,
                    row.ModelId, instanceId, leaseId, slotId, row.Purpose, row.ExecutionAttribution);
            }
        }
        catch (Exception error) when (Ordinary(error)) { NativeCacheCaptureFailed(); return null; }
    }

    internal void NativeCacheGeneration(NativeCacheGenerationObservation observation)
    {
        try
        {
            lock (gate)
            {
                if (!NativeCacheActive) return;
                var owner = observation.Owner;
                if (!NativeCacheOwnerCurrent(owner) ||
                    !nativeCacheModels.TryGetValue(owner.InstanceId, out var captured) || captured != owner ||
                    observation.NativeSlotId is < 0 or >= 16 || !Identifier(observation.LeaseId) ||
                    observation.ActualGenerateSequence is <= 0 or > 9_007_199_254_740_991 ||
                    NativeCacheDispatchRank(observation.DispatchState) < 0 || !NativeCacheFactsValid(observation))
                { NativeCacheCaptureFailed(); return; }
                var binding = observation.Binding;
                if (binding is not null)
                {
                    if (!rows.TryGetValue(binding.AccountingEntryId, out var row)) return; // Trim/stop: no resurrection.
                    if (!NativeCacheRowMatches(row, binding, observation)) { NativeCacheCaptureFailed(); return; }
                }
                var key = (owner.InstanceId, observation.NativeSlotId);
                if (nativeCacheSlots.TryGetValue(key, out var previous))
                {
                    if (observation.ActualGenerateSequence < previous.Sequence) return;
                    if (observation.ActualGenerateSequence == previous.Sequence &&
                        (previous.Row != observation.Binding?.RowId || previous.Lease != observation.LeaseId))
                    { NativeCacheCaptureFailed(); return; }
                }
                // An uncovered send may advance its slot fence; a rejected linked row never can.
                if (binding is null)
                {
                    nativeCacheSlots[key] = (observation.ActualGenerateSequence, null, observation.LeaseId);
                    NativeCacheCaptureFailed(); return;
                }
                if (nativeCacheGenerations.TryGetValue(binding.AccountingEntryId, out var old))
                {
                    if (observation.ActualGenerateSequence < old.ActualGenerateSequence || observation.ObservedAt < old.ObservedAt ||
                        observation.ActualGenerateSequence == old.ActualGenerateSequence &&
                        NativeCacheDispatchRank(observation.DispatchState) < NativeCacheDispatchRank(old.DispatchState)) return;
                    if (old == observation) return;
                    if (observation.ActualGenerateSequence == old.ActualGenerateSequence &&
                        observation.DispatchState == old.DispatchState)
                    { NativeCacheCaptureFailed(); return; } // Same-stage facts were already frozen.
                }
                else if (nativeCacheGenerations.Count >= MaximumReturnedRows)
                { NativeCacheCaptureFailed(); return; }
                nativeCacheSlots[key] = (observation.ActualGenerateSequence, binding.RowId, observation.LeaseId);
                nativeCacheGenerations[binding.AccountingEntryId] = observation;
                nativeCacheRevision++;
            }
        }
        catch (Exception error) when (Ordinary(error)) { NativeCacheCaptureFailed(); }
    }

    // This private sink only records evidence. It cannot acknowledge, stop, release or alter generation facts.
    internal void NativeCacheDecision(NativeCacheDecisionObservation value)
    {
        try
        {
            lock (gate)
            {
                if (!NativeCacheActive) return;
                TrimNativeDecisions(DateTimeOffset.UtcNow);
                if (value.Kind == "event") decisionReceived = Saturate(decisionReceived);
                var b = value.Binding;
                if (value.Kind is not ("requested" or "event" or "task") ||
                    !NativeCacheOwnerCurrent(value.Owner) ||
                    !nativeCacheModels.TryGetValue(value.Owner.InstanceId, out var owner) || owner != value.Owner ||
                    value.ActualGenerateSequence is <= 0 or > 9_007_199_254_740_991 ||
                    value.NativeSlotId is < 0 or >= 16 || value.InputTokens is < 1 or > 1_048_576 ||
                    !rows.TryGetValue(b.AccountingEntryId, out var row) ||
                    !NativeCacheBindingRowMatches(row, b, value.Owner, value.LeaseId, value.NativeSlotId, value.InputTokens))
                { decisionUnmatched = Saturate(decisionUnmatched); nativeCacheRevision++; return; }
                var key = (b.AccountingEntryId, value.ActualGenerateSequence);
                if (!nativeDecisions.TryGetValue(key, out var old))
                {
                    // Only the send-side request marker creates a record. Late packets never resurrect one.
                    if (value.Kind != "requested" || row.Release == "confirmed")
                    { decisionUnmatched = Saturate(decisionUnmatched); nativeCacheRevision++; return; }
                    if (nativeDecisions.Count >= MaximumReturnedRows)
                    { decisionOmitted = Saturate(decisionOmitted); nativeCacheRevision++; return; }
                    nativeDecisions.Add(key, new(value, null, "native-event-not-received", "unobserved", null, value.IngestedAt));
                    nativeCacheRevision++; return;
                }
                var original = old.Requested;
                if (original.Owner != value.Owner || original.Binding != b || original.LeaseId != value.LeaseId ||
                    original.NativeSlotId != value.NativeSlotId || original.InputTokens != value.InputTokens)
                { decisionUnmatched = Saturate(decisionUnmatched); nativeCacheRevision++; return; }
                if (value.Kind == "requested") return;
                if (value.Kind == "task" && !value.TaskConflict && value.IndependentTaskId == old.IndependentTaskId) return;
                var next = old;
                if (value.Kind == "event")
                {
                    if (value.State != "observed" || value.Packet is null)
                    {
                        decisionMalformed = Saturate(decisionMalformed);
                        var state = value.State is "malformed-native-event" or "unsupported-event-schema" or
                            "native-binding-mismatch" or "optional-capture-failed" ? value.State : "malformed-native-event";
                        if (old.Packet is not null) state = "conflicting-native-event";
                        if (old.State == "conflicting-native-event") state = old.State;
                        next = old with { State = state, IngestedAt = value.IngestedAt };
                    }
                    else if (value.Packet.NativeSlotId != value.NativeSlotId || value.Packet.InputTokens != value.InputTokens)
                    {
                        decisionMalformed = Saturate(decisionMalformed);
                        next = old with { State = "native-binding-mismatch", IngestedAt = value.IngestedAt };
                    }
                    else if (old.Packet is null && old.State == "native-event-not-received")
                        next = old with { Packet = value.Packet, State = "observed", IngestedAt = value.IngestedAt };
                    else if (old.Packet != value.Packet)
                        next = old with { State = "conflicting-native-event", IngestedAt = value.IngestedAt };
                    // Equal packets deduplicate. Prior invalid/conflicting evidence is never overwritten by success.
                }
                else
                {
                    if (value.TaskConflict || value.IndependentTaskId is < 0 ||
                        old.IndependentTaskId is not null && value.IndependentTaskId is not null &&
                        old.IndependentTaskId != value.IndependentTaskId)
                        next = old with { TaskJoin = "conflicting-native-task-id", IngestedAt = value.IngestedAt };
                    else if (value.IndependentTaskId is not null)
                        next = old with { IndependentTaskId = value.IndependentTaskId, IngestedAt = value.IngestedAt };
                }
                if (next.TaskJoin != "conflicting-native-task-id")
                    next = next with { TaskJoin = next.Packet is null ? "unobserved" : next.IndependentTaskId is null
                        ? "response-bound/native-task-reported" : next.Packet.NativeTaskId == next.IndependentTaskId
                            ? "existing-ack-slot-status" : "conflicting-native-task-id" };
                if (next.State == "conflicting-native-event" && old.State != next.State ||
                    next.TaskJoin == "conflicting-native-task-id" && old.TaskJoin != next.TaskJoin)
                    decisionConflicting = Saturate(decisionConflicting);
                if (next != old) nativeDecisions[key] = next;
                nativeCacheRevision++;
            }
        }
        catch (Exception error) when (Ordinary(error))
        { lock (gate) { decisionFailed = Saturate(decisionFailed); nativeCacheRevision++; } }
    }
    private static int Saturate(int value) => value < int.MaxValue ? value + 1 : value;
    private void TrimNativeDecisions(DateTimeOffset now)
    {
        foreach (var pair in nativeDecisions.ToArray())
        {
            var value = pair.Value.Requested;
            if (now - value.IngestedAt >= TimeSpan.FromMinutes(5) ||
                !rows.TryGetValue(value.Binding.AccountingEntryId, out var row) || !NativeCacheOwnerCurrent(value.Owner) ||
                !nativeCacheModels.TryGetValue(value.Owner.InstanceId, out var owner) || owner != value.Owner ||
                !NativeCacheBindingRowMatches(row, value.Binding, value.Owner, value.LeaseId, value.NativeSlotId, value.InputTokens))
            { nativeDecisions.Remove(pair.Key); decisionExpired = Saturate(decisionExpired); nativeCacheRevision++; }
        }
    }
    private void RemoveNativeDecisions(string? entry = null, string? instance = null)
    {
        try
        {
            foreach (var pair in nativeDecisions.Where(x => entry is not null && x.Key.Entry == entry ||
                instance is not null && x.Value.Requested.Owner.InstanceId == instance).ToArray())
            { nativeDecisions.Remove(pair.Key); decisionExpired = Saturate(decisionExpired); nativeCacheRevision++; }
        }
        catch (Exception error) when (Ordinary(error)) { decisionFailed = Saturate(decisionFailed); nativeCacheRevision++; }
    }
    private InferenceNativeDecisionDiagnostics? TrySnapshotNativeDecisions(InferenceObservationLinksSnapshot snapshot,
        DateTimeOffset now, int limit)
    {
        try { return SnapshotNativeDecisions(snapshot, now, limit); }
        catch (Exception error) when (Ordinary(error))
        { decisionFailed = Saturate(decisionFailed); nativeCacheRevision++; return null; }
    }
    private InferenceNativeDecisionDiagnostics SnapshotNativeDecisions(InferenceObservationLinksSnapshot snapshot,
        DateTimeOffset now, int limit)
    {
        TrimNativeDecisions(now);
        var returned = snapshot.Snapshot.Rows.Select(x => x.AccountingEntryId).ToHashSet(StringComparer.Ordinal);
        var current = nativeDecisions.Values.OrderBy(x => x.Requested.Binding.RowId, StringComparer.Ordinal)
            .ThenBy(x => x.Requested.ActualGenerateSequence).ToArray();
        var selected = current.Where(x => returned.Contains(x.Requested.Binding.AccountingEntryId)).Take(limit).ToArray();
        var omitted = current.Length - selected.Length;
        var result = selected.Select(value =>
        {
            var request = value.Requested; var owner = request.Owner; var b = request.Binding; var id = owner.Identity;
            var source = new InferenceNativeDecisionSource(owner.ModelId, owner.InstanceId, owner.RuntimeGenerationId,
                owner.RuntimeEpoch, owner.LaunchBindingSha256, new(id.State, id.Scope, id.ExecutableSha256,
                    id.ExecutableBytes, id.NativeProcessId, id.NativeStartTicks?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    id.Basis, id.DependencyIdentityState, id.Reason), b.AccountingGenerationId, b.AccountingEntryId,
                b.AttemptId, b.LeaseId, b.NativeSlotId, b.Purpose, b.ExecutionAttribution);
            return new InferenceNativeDecision(b.RowId, request.ActualGenerateSequence,
                value.TaskJoin == "conflicting-native-task-id" ? "native-task-mismatch" : value.State, value.TaskJoin,
                value.IndependentTaskId, value.Packet, request.IngestedAt, value.IngestedAt, source);
        }).ToArray();
        var reasons = new List<string>();
        if (decisionMalformed != 0) reasons.Add("malformed-native-event");
        if (decisionConflicting != 0) reasons.Add("conflicting-native-event-or-task");
        if (decisionUnmatched != 0) reasons.Add("owner-binding-or-retention-unavailable");
        if (decisionOmitted != 0) reasons.Add("event-limit");
        if (decisionExpired != 0) reasons.Add("owner-binding-or-retention-expired");
        if (decisionFailed != 0) reasons.Add("optional-capture-failed");
        if (omitted != 0) reasons.Add("optional-read-truncated");
        if (result.Length == 0) reasons.Add("native-decision-unobserved");
        if (result.Any(x => x.State != "observed")) reasons.Add("native-event-unavailable");
        if (result.Any(x => x.TaskJoin != "existing-ack-slot-status")) reasons.Add("native-task-unconfirmed-or-conflicting");
        return new("pegasus.native-cache-decisions/1", result, decisionReceived, decisionMalformed, decisionConflicting,
            decisionUnmatched, decisionOmitted, decisionExpired, decisionFailed, omitted, reasons.Count != 0,
            omitted != 0, reasons.ToArray());
    }

    private bool NativeCacheActive => nativeCacheEnabled == true && !nativeCacheInitializationConflict;
    private bool NativeCacheOwnerCurrent(NativeCacheOwner owner) =>
        Identifier(owner.ModelId) && Identifier(owner.InstanceId) && Identifier(owner.RuntimeGenerationId) &&
        owner.RuntimeEpoch >= 0 && (owner.LaunchBindingSha256 is null || NativeCacheSha(owner.LaunchBindingSha256)) &&
        owner.ObservationState is "observed" or "unavailable" && NativeCacheOwnerReason(owner.ObservationReason) &&
        NativeCacheIdentityValid(owner.Identity) && modelCoverage.GenerationId == owner.RuntimeGenerationId &&
        models.TryGetValue(owner.InstanceId, out var model) && model.ModelId == owner.ModelId &&
        model.RuntimeGenerationId == owner.RuntimeGenerationId && model.RuntimeEpoch == owner.RuntimeEpoch &&
        model.LaunchBindingSha256 == owner.LaunchBindingSha256;
    private static bool NativeCacheIdentityValid(NativeExecutableIdentity value) =>
        value.Scope == "main-executable" && value.DependencyIdentityState == "unobserved" &&
        value.State is "observed-main-executable" or "launch-file-only" or "unobserved" or "changed" &&
        value.Basis is "unobserved" or "read-handle-held-through-spawn" && NativeCacheIdentityReason(value.Reason) &&
        (value.ExecutableSha256 is null || NativeCacheSha(value.ExecutableSha256)) &&
        (value.ExecutableBytes is null or > 0 and <= 268_435_456) &&
        (value.NativeProcessId is null or > 0) && (value.NativeStartTicks is null or > 0) &&
        (value.State != "observed-main-executable" || value.ExecutableSha256 is not null &&
            value.ExecutableBytes is not null && value.NativeProcessId is not null && value.NativeStartTicks is not null &&
            value.Basis == "read-handle-held-through-spawn" && value.Reason is null) &&
        (value.State != "launch-file-only" || value.ExecutableSha256 is not null && value.ExecutableBytes is not null &&
            value.NativeProcessId is null && value.NativeStartTicks is null && value.Basis == "read-handle-held-through-spawn") &&
        (value.State != "unobserved" || value.ExecutableSha256 is null && value.ExecutableBytes is null &&
            value.NativeProcessId is null && value.NativeStartTicks is null && value.Basis == "unobserved");
    private static bool NativeCacheOwnerReason(string? value) => value is null or "instance-stopped" or "epoch-unavailable" or
        "observed-instance-cap" or "observation-initialization-unavailable" or "capture-unavailable" or "source-changed";
    private static bool NativeCacheIdentityReason(string? value) => value is null or "unobserved" or "disabled" or "owned-child-not-bound" or
        "owned-child-image-unobserved" or "owned-child-image-mismatch" or "executable-file-observation-unavailable" or "owned-image-byte-binding-unobserved-platform";
    private static bool NativeCacheSha(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static int NativeCacheDispatchRank(string value) => value switch
    { "send-intent" => 0, "acknowledged" => 1, "settled" => 2, "uncertain" => 3, _ => -1 };
    private static bool NativeCacheFactsValid(NativeCacheGenerationObservation value)
    {
        var comparison = value.Comparison; var provider = value.ProviderEvidence;
        if (value.DispatchState == "settled" && !value.NativeStopConfirmed) return false;
        if (comparison.CurrentInputTokens is not (null or >= 1 and <= 1_048_576)) return false;
        if (comparison.State == "observed")
        {
            if (comparison.Reason is not null || !Identifier(comparison.BaselineRowId) || !Identifier(comparison.BaselineLeaseId) ||
                comparison.BaselineGenerateSequence is not { } baselineSequence || baselineSequence is <= 0 or > 9_007_199_254_740_991 ||
                comparison.PreviousInputTokens is not { } previous || previous is < 1 or > 1_048_576 || comparison.CurrentInputTokens is not { } current ||
                comparison.SamePreparedInput is not { } same || comparison.PreviousInputIsPrefixOfCurrent is not { } prefix ||
                comparison.CommonPrefixLowerBoundTokens is not { } lower || comparison.CommonPrefixUpperBoundTokens is not { } upper)
                return false;
            if (baselineSequence >= value.ActualGenerateSequence ||
                comparison.BaselineRowId == value.Binding?.RowId || comparison.BaselineLeaseId == value.LeaseId ||
                lower < 0 || lower > upper || upper > Math.Min(previous, current) ||
                same != (previous == current && prefix) ||
                prefix && (current < previous || lower != previous || upper != previous) ||
                !prefix && current >= previous && upper >= previous) return false;
        }
        else
        {
            if (comparison.State is not ("no-baseline" or "unsupported" or "unavailable") ||
                comparison.BaselineRowId is not null || comparison.BaselineLeaseId is not null ||
                comparison.BaselineGenerateSequence is not null || comparison.PreviousInputTokens is not null ||
                comparison.SamePreparedInput is not null || comparison.PreviousInputIsPrefixOfCurrent is not null ||
                comparison.CommonPrefixLowerBoundTokens is not null || comparison.CommonPrefixUpperBoundTokens is not null)
                return false;
            if (comparison.State == "no-baseline")
            {
                if (comparison.CurrentInputTokens is null || comparison.Reason is not ("no-baseline" or "baseline-expired")) return false;
            }
            else if (comparison.Reason is not ("runtime-epoch-changed" or "epoch-unavailable" or "prior-native-finalization-unavailable" or
                "unsupported-native-use" or "exact-entry-binding-unavailable" or "observation-coverage-unavailable" or "source-changed" or
                "prefix-observation-unavailable" or "prior-native-generation-ineligible" or "diagnostic-publication-unavailable" or
                "unsupported-reasoning-route" or "unsupported-lease-entry" or "native-slot-quarantined" or
                "lease-return-without-native-finalization" or "capture-unavailable" or "instance-stopped" or
                "runtime-epoch-regressed" or "slot-outside-observation" or "known-native-eviction" or "settled-native-input"))
                return false;
        }
        return provider.CachedSource is null or "timings.cache_n" or "usage.prompt_tokens_details.cached_tokens" &&
            provider.ProcessedSource is null or "timings.prompt_n" && provider.RetentionState == "unobserved" &&
            provider.Event is null && provider.ReasonCode is null && provider.Basis is null &&
            (provider.TaskJoin is "unobserved" or "conflicting-native-task-id" && provider.NativeTaskId is null ||
                provider.TaskJoin == "existing-ack-slot-status" && provider.NativeTaskId is >= 0);
    }
    private bool NativeCacheRowMatches(InferenceCaptureRow row, NativeCacheBinding binding, NativeCacheGenerationObservation value) =>
        NativeCacheBindingRowMatches(row, binding, value.Owner, value.LeaseId, value.NativeSlotId, value.Comparison.CurrentInputTokens);
    private bool NativeCacheBindingRowMatches(InferenceCaptureRow row, NativeCacheBinding binding,
        NativeCacheOwner owner, string lease, int slot, int? inputTokens) =>
        row.Kind == nameof(ModelOwnershipKind.LocalPreparation) && row.RowId == binding.RowId &&
        row.AccountingGenerationId == accountingGeneration && row.AccountingGenerationId == binding.AccountingGenerationId &&
        row.AccountingEntryId == binding.AccountingEntryId && row.AttemptId == binding.AttemptId &&
        row.ModelId == binding.ModelId && row.ModelId == owner.ModelId &&
        row.InstanceId == binding.InstanceId && row.InstanceId == owner.InstanceId &&
        row.AccountingLeaseId == binding.LeaseId && row.LeaseId == binding.LeaseId && row.LeaseId == lease &&
        row.NativeSlotId == binding.NativeSlotId && row.NativeSlotId == slot &&
        row.Purpose == binding.Purpose && row.ExecutionAttribution == binding.ExecutionAttribution &&
        (row.Budget is null || inputTokens is null || row.Budget.InputTokens == inputTokens);
    private void RemoveNativeCacheInstance(string instance)
    {
        RemoveNativeDecisions(instance: instance);
        var changed = nativeCacheModels.Remove(instance);
        foreach (var key in nativeCacheGenerations.Where(x => x.Value.Owner.InstanceId == instance).Select(x => x.Key).ToArray())
            changed |= nativeCacheGenerations.Remove(key);
        foreach (var key in nativeCacheSlots.Keys.Where(x => x.Instance == instance).ToArray())
            changed |= nativeCacheSlots.Remove(key);
        if (changed) nativeCacheRevision++;
    }

    internal InferenceCacheObservationSnapshot SnapshotV3(DateTimeOffset? sampledAt = null, int maximumRows = MaximumReturnedRows)
    {
        if (maximumRows is < 1 or > MaximumReturnedRows) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        lock (gate)
        {
            var now = sampledAt ?? DateTimeOffset.UtcNow;
            var limit = maximumRows; var diagnosticLimit = MaximumReturnedRows; var modelLimit = 64;
            var decisionLimit = MaximumReturnedRows; var includeDecisions = NativeCacheActive;
            while (true)
            {
                var snapshot = SnapshotV2(now, limit);
                var candidates = snapshot.Snapshot.Rows.Where(row => nativeCacheGenerations.ContainsKey(row.AccountingEntryId))
                    .Select(row => nativeCacheGenerations[row.AccountingEntryId]).ToArray();
                var selected = candidates
                    .Where(value => value.Binding is { } binding && NativeCacheOwnerCurrent(value.Owner) &&
                        nativeCacheModels.TryGetValue(value.Owner.InstanceId, out var owner) && owner == value.Owner &&
                        rows.TryGetValue(binding.AccountingEntryId, out var row) && NativeCacheRowMatches(row, binding, value))
                    .OrderBy(value => value.Binding!.RowId, StringComparer.Ordinal).ToArray();
                var currentModels = nativeCacheModels.Values.Where(NativeCacheOwnerCurrent)
                    .OrderBy(value => value.InstanceId, StringComparer.Ordinal).ToArray();
                var omittedBinding = candidates.Length != selected.Length || currentModels.Length != nativeCacheModels.Count;
                var truncated = selected.Length > diagnosticLimit || currentModels.Length > modelLimit;
                var generations = selected.Take(diagnosticLimit).Select(value =>
                {
                    var owner = value.Owner; var binding = value.Binding!;
                    return new InferenceCacheGeneration(binding.RowId, value.ActualGenerateSequence, value.DispatchState,
                        value.Comparison, value.ProviderEvidence, new(owner.ModelId, owner.InstanceId,
                            owner.RuntimeGenerationId, owner.RuntimeEpoch, owner.LaunchBindingSha256,
                            binding.AccountingGenerationId, binding.AccountingEntryId, binding.AttemptId, binding.LeaseId,
                            binding.NativeSlotId, binding.Purpose, binding.ExecutionAttribution, value.NativeStopConfirmed, value.ObservedAt));
                }).ToArray();
                var sampledModels = currentModels.Take(modelLimit).Select(value =>
                    {
                        var identity = value.Identity;
                        return new InferenceCacheModel(value.ModelId, value.InstanceId, value.RuntimeGenerationId,
                            value.RuntimeEpoch, value.LaunchBindingSha256, new(identity.State, identity.Scope,
                                identity.ExecutableSha256, identity.ExecutableBytes, identity.NativeProcessId,
                                identity.NativeStartTicks?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                identity.Basis, identity.DependencyIdentityState, identity.Reason), value.ObservationState, value.ObservationReason);
                    }).ToArray();
                var reasons = new List<string>();
                if (nativeCacheEnabled is null) reasons.Add("initialization-unobserved");
                if (nativeCacheInitializationConflict) reasons.Add("initialization-conflict");
                if (nativeCacheLost || omittedBinding) reasons.Add("capture-unavailable");
                if (snapshot.Partial) reasons.Add("base-partial");
                if (truncated) reasons.Add("diagnostics-truncated");
                if (snapshot.Truncated) reasons.Add("base-truncated");
                var state = nativeCacheInitializationConflict || nativeCacheLost || omittedBinding ? "unavailable" : nativeCacheEnabled switch
                { null => "unconfigured", false => "disabled", true => "enabled" };
                var decisions = includeDecisions ? TrySnapshotNativeDecisions(snapshot, now, decisionLimit) : null;
                // Losing the entire optional envelope must remain visible in the parent diagnostics.
                if (NativeCacheActive && decisions is null)
                {
                    reasons.Add(includeDecisions ? "optional-decision-capture-unavailable" : "optional-decision-envelope-omitted");
                    if (!includeDecisions) truncated = true;
                }
                var result = new InferenceCacheObservationSnapshot("cerebral.inference-cache-observation/1", snapshot,
                    new(state, generation, nativeCacheRevision, now, sampledModels, generations,
                        reasons.Count != 0, truncated || snapshot.Truncated, reasons.ToArray())
                    { Decisions = decisions });
                if (JsonSerializer.SerializeToUtf8Bytes(result, WireJson).Length <= MaximumPayloadBytes) return result;
                // Optional rows/envelope yield their budget before any pre-existing generation/model/base coverage.
                if (includeDecisions && decisionLimit > 0) { decisionLimit /= 2; continue; }
                if (includeDecisions) { includeDecisions = false; continue; }
                if (diagnosticLimit > 0) { diagnosticLimit /= 2; continue; }
                if (modelLimit > 0) { modelLimit /= 2; continue; }
                if (snapshot.Snapshot.Rows.Length <= 1)
                    throw new InvalidOperationException("inference_cache_observation_payload_unavailable");
                limit = Math.Max(1, snapshot.Snapshot.Rows.Length / 2);
            }
        }
    }

    internal static bool IsCurrent(InferenceObservationSnapshot snapshot, InferenceObservationChanged invalidation) =>
        snapshot.Schema == "cerebral.inference-ownership/1" &&
        invalidation.Schema == "cerebral.inference-observation.changed/1" && snapshot.IssuingInstallationId is not null &&
        invalidation.ObserverGenerationId == snapshot.ObserverGenerationId &&
        invalidation.OriginInstallationId == snapshot.IssuingInstallationId && invalidation.Revision >= snapshot.Revision;
    private void QueueNotification()
    {
        if (notify is null || Interlocked.Exchange(ref notificationPending, 1) != 0) return;
        // Only one outstanding notification; a coalesced latest revision is sufficient for reread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            InferenceObservationChanged value; Action<InferenceObservationChanged>? target;
            lock (gate) { value = new("cerebral.inference-observation.changed/1", issuer, generation, revision); target = notify; }
            try { target?.Invoke(value); }
            catch (Exception error) when (Ordinary(error)) { lock (gate) eventFailed = true; }
            finally { Interlocked.Exchange(ref notificationPending, 0); }
            bool newer;
            lock (gate) newer = revision != value.Revision;
            if (newer) QueueNotification();
        });
    }
    internal static bool Ordinary(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static bool ValidCoordinate(InferenceExecutionCoordinate? value) => value is not null &&
        Identifier(value.ExecutionId) && Identifier(value.ProfileId) && value.ProfileRevision >= 0 &&
        new[] { value.ConversationId, value.RequestId, value.RunId, value.EffectId }.All(x => x is null || Identifier(x));
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);
    private static InferenceCaptureSource Unavailable(string reason) => new("unavailable", reason, null);
}
