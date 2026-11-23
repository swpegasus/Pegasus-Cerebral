namespace Cerebral.Auditing;

public sealed record SelectionDecision(string Status, string EffectiveTier, string? AuditWorkerId, string? BackendId,
    long? EstimatedCompleteMonoMs, int? RequiredMemoryMiB, IReadOnlyList<string> ReasonCodes, int ExaminedBackends);

public static class AuditorSelector
{
    public static SelectionDecision Select(AuditRequest request, AuditorPolicy policy, IReadOnlyList<WorkerSnapshot> workers,
        IReadOnlyList<AuditBackendSnapshot> backends, long nowMonoMs)
    {
        request = AuditRequest.Parse(request.ToJson());
        policy = AuditorPolicy.Parse(policy.ToJson());
        workers = workers.Select(item => WorkerSnapshot.Parse(item.ToJson())).ToArray();
        backends = backends.Select(item => AuditBackendSnapshot.Parse(item.ToJson())).ToArray();
        if (nowMonoMs < 0) throw new AuditorContractException("now_mono_ms must be nonnegative.");
        if (workers.Select(item => item.WorkerId).Distinct(StringComparer.Ordinal).Count() != workers.Count)
            throw new AuditorContractException("duplicate worker_id.");
        if (backends.Select(item => item.BackendId).Distinct(StringComparer.Ordinal).Count() != backends.Count)
            throw new AuditorContractException("duplicate backend_id.");
        var workerById = workers.ToDictionary(item => item.WorkerId, StringComparer.Ordinal);
        var requiredTier = Rank(request.MinimumTier) >= Rank(policy.RiskMinimumTier[request.RiskClass])
            ? request.MinimumTier : policy.RiskMinimumTier[request.RiskClass];
        if (policy.SynchronousRisks.Contains(request.RiskClass, StringComparer.Ordinal) && request.ExecutionMode != "synchronous")
            return Empty("rejected", requiredTier, ["synchronous_mode_required"], backends.Count);
        if (nowMonoMs >= request.DeadlineMonoMs)
            return Empty("unavailable", requiredTier, ["deadline_elapsed"], backends.Count);

        var candidates = new List<Candidate>();
        var transientBlocked = false;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var requiredKinds = request.CheckKinds.Values.ToHashSet(StringComparer.Ordinal);
        var maximumOptions = request.DecisionRequest.Questions.Max(item => item.Options.Count);
        foreach (var backend in backends)
        {
            if (!workerById.TryGetValue(backend.HostWorkerId, out var worker)) { reasons.Add("backend_host_unknown"); continue; }
            if (!backend.Enabled || !worker.Enabled) { reasons.Add("disabled"); continue; }
            if (!worker.InstalledBackendIds.Contains(backend.BackendId, StringComparer.Ordinal)) { reasons.Add("backend_not_installed"); continue; }
            if (Rank(backend.AssuranceTier) < Rank(requiredTier)) { reasons.Add("assurance_tier_too_low"); continue; }
            if (!backend.ExecutionModes.Contains(request.ExecutionMode, StringComparer.Ordinal)) { reasons.Add("execution_mode_unsupported"); continue; }
            if (!requiredKinds.IsSubsetOf(backend.AuditKinds)) { reasons.Add("audit_kind_unsupported"); continue; }
            if (request.EstimatedInputTokens > backend.MaxInputTokens || maximumOptions > backend.MaxOptions) { reasons.Add("input_limit_exceeded"); continue; }
            if (!worker.DataZones.Contains(request.DataZone, StringComparer.Ordinal) || !backend.DataZones.Contains(request.DataZone, StringComparer.Ordinal))
            { reasons.Add("data_zone_mismatch"); continue; }
            if (worker.WorkerId != request.AgentWorkerId &&
                (!request.AllowCrossWorker || !policy.CrossWorkerRisks.Contains(request.RiskClass, StringComparer.Ordinal)))
            { reasons.Add("cross_worker_not_allowed"); continue; }
            var memory = EffectiveMemory(worker, backend);
            var usable = Math.Max(0, worker.MemoryAvailableMiB - worker.ReservedAgentMemoryMiB);
            if (usable < memory + policy.MinimumMemoryHeadroomMiB)
            { transientBlocked = true; reasons.Add("insufficient_memory"); continue; }
            if (backend.QueueDepth >= backend.QueueCapacity)
            { transientBlocked = true; reasons.Add("audit_capacity_exhausted"); continue; }
            var resident = worker.ResidentBackendIds.Contains(backend.BackendId, StringComparer.Ordinal);
            var coldStart = resident ? 0 : backend.ColdStartMs;
            var queueUnits = backend.QueueDepth + (worker.ActiveAudits >= worker.AuditSlots ? 1 : 0);
            var queueBatches = (queueUnits + worker.AuditSlots - 1) / worker.AuditSlots;
            long estimated;
            try
            {
                estimated = checked(nowMonoMs + coldStart + (long)queueBatches * backend.P95LatencyMs + backend.P95LatencyMs +
                                    (worker.WorkerId == request.AgentWorkerId ? policy.AgentColocationPenaltyMs : 0));
            }
            catch (OverflowException) { throw new AuditorContractException("estimated completion time exceeds the supported range."); }
            if (estimated > request.DeadlineMonoMs) { reasons.Add("deadline_not_reachable"); continue; }
            candidates.Add(new(worker, backend, estimated, memory, resident));
        }
        if (candidates.Count == 0)
        {
            if (policy.NoSilentFallbackRisks.Contains(request.RiskClass, StringComparer.Ordinal)) reasons.Add("no_silent_fallback");
            return Empty(transientBlocked ? "wait" : "unavailable", requiredTier,
                reasons.Count == 0 ? ["no_backends_declared"] : reasons.Order(StringComparer.Ordinal).ToArray(), backends.Count);
        }
        var selected = candidates.OrderBy(item => item.Estimated)
            .ThenBy(item => item.Resident ? 0 : 1).ThenBy(item => Rank(item.Backend.AssuranceTier) - Rank(requiredTier))
            .ThenBy(item => item.Worker.WorkerId, StringComparer.Ordinal).ThenBy(item => item.Backend.BackendId, StringComparer.Ordinal).First();
        var selectedReasons = new List<string> { "selected" };
        if (selected.Resident) selectedReasons.Add("resident_backend");
        else if (selected.Backend.SharedBackboneModelIdentity is not null &&
                 selected.Worker.ActiveModelIdentities.Contains(selected.Backend.SharedBackboneModelIdentity, StringComparer.Ordinal))
            selectedReasons.Add("shared_backbone");
        if (selected.Worker.WorkerId != request.AgentWorkerId) selectedReasons.Add("cross_worker");
        return new("selected", requiredTier, selected.Worker.WorkerId, selected.Backend.BackendId,
            selected.Estimated, selected.Memory, selectedReasons, backends.Count);
    }

    private static int EffectiveMemory(WorkerSnapshot worker, AuditBackendSnapshot backend)
    {
        if (worker.ResidentBackendIds.Contains(backend.BackendId, StringComparer.Ordinal)) return backend.WorkingMemoryMiB;
        if (backend.SharedBackboneModelIdentity is not null &&
            worker.ActiveModelIdentities.Contains(backend.SharedBackboneModelIdentity, StringComparer.Ordinal))
            return backend.SharedBackboneIncrementalMiB + backend.WorkingMemoryMiB;
        return backend.ResidentMemoryMiB + backend.WorkingMemoryMiB;
    }

    private static int Rank(string tier) => Array.IndexOf(AuditorSchema.AssuranceTiers, tier) + 1;
    private static SelectionDecision Empty(string status, string tier, IReadOnlyList<string> reasons, int examined) =>
        new(status, tier, null, null, null, null, reasons, examined);
    private sealed record Candidate(WorkerSnapshot Worker, AuditBackendSnapshot Backend, long Estimated, int Memory, bool Resident);
}
