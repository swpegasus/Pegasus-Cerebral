namespace Cerebral.Worker;

internal enum ModelOwnershipKind
{
    LocalLoad, LocalContext, LocalPreparation, LocalMeasurement, ImageVerification, CapabilityLease,
    OutboundPreparation, OutboundMeasurement, OutboundGeneration, RemoteModelChange, InboundReservation, FittingProcess
}

internal enum ModelOwnershipPhase { Queued, Loading, Preparing, Ready, Generating, Releasing, Stopping, Unknown }

internal sealed record ModelOwnershipEntry(string Id, ModelOwnershipKind Kind, ModelOwnershipPhase Phase,
    string ModelId, string? InstanceId, string? ReservationId, string? LeaseId, string? WorkerInstallationId,
    int? ProcessId, bool EffectStarted, string? Uncertainty);

internal sealed record ModelAccountingSnapshot(int SchemaVersion, string ProcessGenerationId, long Revision,
    bool AdmissionOpen, string AdmissionReason, bool CoverageComplete, string Scope,
    int LocalContexts, int LogicalPreparations, int InboundReservations, int QueuedRequests, int ActiveRequests, int UnknownOwnership,
    ModelOwnershipEntry[] Entries);

/// <summary>One explicitly composed runtime's ownership. Observation never invokes providers or repairs state.</summary>
internal sealed class ModelExecutionAccounting(bool admissionOpen = true, bool coverageComplete = false, int capacity = 4096)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StateStore, ModelExecutionAccounting> Owners = new();
    internal static ModelExecutionAccounting For(StateStore store, bool coverageComplete = false, bool initiallyClosed = false) =>
        Owners.GetValue(store, _ => new(admissionOpen: !initiallyClosed, coverageComplete: coverageComplete));
    internal static void Bind(StateStore store, ModelExecutionAccounting accounting)
    {
        if (!ReferenceEquals(Owners.GetValue(store, _ => accounting), accounting))
            throw new InvalidOperationException("model_accounting_owner_already_bound");
    }
    private readonly int maximum = capacity is >= 1 and <= 4096 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object gate = new();
    private readonly Dictionary<string, ModelOwnershipEntry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelOwnershipRole> ownershipRoles = new(StringComparer.Ordinal);
    private bool open = admissionOpen;
    private bool startupReleasePending = !admissionOpen;
    private readonly bool complete = coverageComplete;
    private string reason = admissionOpen ? "ordinary-startup" : "explicit-owner-release-required";
    private long revision = 1;
    internal string ProcessGenerationId { get; } = Guid.NewGuid().ToString("N");
    private InferenceObservationRegistry? observation;
    internal InferenceObservationRegistry Observation => LazyInitializer.EnsureInitialized(ref observation,
        () => new InferenceObservationRegistry(ProcessGenerationId, complete));

    internal ModelOwnership Begin(ModelOwnershipKind kind, string modelId, bool requiresAdmission = true) =>
        Begin(kind, modelId, requiresAdmission, ModelOwnershipRole.Unclassified);

    internal ModelOwnership Begin(ModelOwnershipKind kind, string modelId, bool requiresAdmission, ModelOwnershipRole role)
    {
        if (!Enum.IsDefined(role) || role != ModelOwnershipRole.Unclassified && kind != ModelOwnershipKind.CapabilityLease)
            throw new ArgumentException("model_accounting_role_invalid");
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 128 || modelId.Contains('\0'))
            throw new ArgumentException("model_accounting_identity_invalid");
        using var readiness = NativeReadinessOwnershipScope.BeforeBegin(this, modelId);
        ModelOwnership ownership;
        lock (gate)
        {
            if (requiresAdmission && !open) throw new InvalidOperationException("model_admission_closed");
            if (entries.Count >= maximum) throw new InvalidOperationException("model_accounting_capacity_full");
            var id = Guid.NewGuid().ToString("N");
            entries.Add(id, new(id, kind, ModelOwnershipPhase.Queued, modelId, null, null, null, null, null, false, null));
            if (role != ModelOwnershipRole.Unclassified) ownershipRoles.Add(id, role);
            revision++;
            Observation.Accounting(entries[id], revision, WorkerInferenceInvocation.Current);
            ownership = new(this, id);
        }
        // Collection never calls arbitrary code or takes its lock under the accounting gate.
        readiness?.Register(ownership, kind);
        return ownership;
    }

    internal ModelOwnershipEntry? ReadEntry(string id) { lock (gate) return entries.GetValueOrDefault(id); }

    internal ModelAccountingSnapshot Snapshot()
    {
        lock (gate)
        {
            var values = entries.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
            return new(1, ProcessGenerationId, revision, open, reason, complete,
                "composed current-process ownership; machine and remote-provider totals unobserved",
                values.Count(value => value.Kind == ModelOwnershipKind.LocalContext),
                values.Count(value => value.Kind is ModelOwnershipKind.LocalPreparation or ModelOwnershipKind.OutboundPreparation),
                values.Count(value => value.Kind == ModelOwnershipKind.InboundReservation),
                values.Count(value => value.Phase == ModelOwnershipPhase.Queued),
                values.Count(value => value.EffectStarted && value.Kind is not (ModelOwnershipKind.LocalContext or ModelOwnershipKind.InboundReservation) &&
                    value.Phase is not (ModelOwnershipPhase.Ready or ModelOwnershipPhase.Unknown)),
                values.Count(value => value.Phase == ModelOwnershipPhase.Unknown), values);
        }
    }

    // The base DTO stays unchanged. Purpose is immutable provenance from the exact Begin site.
    internal ModelAccountingRoleCapture SnapshotWithOwnershipRoles()
    {
        lock (gate)
        {
            var snapshot = Snapshot();
            return new(snapshot, ownershipRoles.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new ModelOwnershipRoleBinding(x.Key, x.Value)).ToArray());
        }
    }

    internal void ReleaseStartup(string generation, long expectedRevision)
    {
        lock (gate)
        {
            if (generation != ProcessGenerationId || expectedRevision != revision)
                throw new InvalidOperationException("model_admission_generation_changed");
            if (open) throw new InvalidOperationException("model_admission_already_open");
            if (!startupReleasePending) throw new InvalidOperationException("model_admission_not_startup");
            if (entries.Count != 0) throw new InvalidOperationException("model_ownership_not_empty");
            open = true; startupReleasePending = false; reason = "verified-owner-release"; revision++;
        }
    }

    internal void Close(string admissionReason)
    {
        lock (gate)
        {
            if (!open && reason == admissionReason && !startupReleasePending) return;
            startupReleasePending = false; open = false; reason = admissionReason; revision++;
        }
    }

    private void Change(string id, Func<ModelOwnershipEntry, ModelOwnershipEntry> change)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(id, out var old)) return;
            var next = change(old);
            if (next != old) { entries[id] = next; revision++; Observation.Accounting(next, revision); }
        }
    }

    private void Complete(string id) { lock (gate) { if (entries.Remove(id)) { ownershipRoles.Remove(id); revision++; Observation.Released(id, revision, "accounting-owner-confirmed-retirement"); } } }

    internal void CompleteStoppedInstance(string instanceId)
    {
        lock (gate)
        {
            foreach (var value in entries.Values.Where(value => value.InstanceId == instanceId &&
                value.Kind is ModelOwnershipKind.LocalContext or ModelOwnershipKind.LocalPreparation or ModelOwnershipKind.LocalMeasurement or
                    ModelOwnershipKind.ImageVerification or ModelOwnershipKind.InboundReservation).ToArray())
                if (entries.Remove(value.Id)) { ownershipRoles.Remove(value.Id); revision++; Observation.Released(value.Id, revision, "exact-instance-stopped"); }
        }
    }

    internal sealed class ModelOwnership(ModelExecutionAccounting owner, string id) : IDisposable
    {
        internal string Id { get; } = id;
        internal ModelExecutionAccounting Owner => owner;
        internal void Phase(ModelOwnershipPhase phase, bool effectStarted = false) =>
            owner.Change(Id, value => value.Phase == ModelOwnershipPhase.Unknown ? value :
                value with { Phase = phase, EffectStarted = value.EffectStarted || effectStarted });
        internal void Bind(string? instanceId = null, string? reservationId = null, string? leaseId = null,
            string? workerInstallationId = null, int? processId = null) =>
            owner.Change(Id, value => value with { InstanceId = instanceId ?? value.InstanceId,
                ReservationId = reservationId ?? value.ReservationId, LeaseId = leaseId ?? value.LeaseId,
                WorkerInstallationId = workerInstallationId ?? value.WorkerInstallationId, ProcessId = processId ?? value.ProcessId });
        internal void Unknown(string cause) => owner.Change(Id, value =>
            value with { Phase = ModelOwnershipPhase.Unknown, Uncertainty = cause });
        internal ModelOwnershipEntry? ReadEntry() => owner.ReadEntry(Id);
        internal bool IsRetired { get { lock (owner.gate) return !owner.entries.ContainsKey(Id); } }
        internal bool IsUnknown { get { lock (owner.gate) return owner.entries.TryGetValue(Id, out var entry) && entry.Phase == ModelOwnershipPhase.Unknown; } }
        internal void Complete() => owner.Complete(Id);
        internal void NativeSlot(string leaseId, int slot) => owner.Observation.Slot(Id, leaseId, slot);
        internal void Prepared(Cerebral.Contracts.Models.PreparedModelMetadata metadata) => owner.Observation.Prepared(Id, metadata);
        internal void Cancellation() => owner.Observation.Cancellation(Id);
        internal CancellationTokenRegistration ObserveCancellation(CancellationToken token)
        {
            try { return token.Register(Cancellation); }
            catch (Exception error) when (InferenceObservationRegistry.Ordinary(error))
            { owner.Observation.Incomplete(); return default; }
        }
        internal void Terminal(Cerebral.Contracts.Models.ModelGenerationResult? result) => owner.Observation.Terminal(Id, result);
        internal void Progress(ProviderGenerationMeasurement value) => owner.Observation.Progress(Id, value);
        // Scope exit is never provider completion. Callers must supply their actual release evidence.
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (!owner.entries.TryGetValue(Id, out var entry) || entry.Phase == ModelOwnershipPhase.Unknown) return;
                if (!entry.EffectStarted) { owner.entries.Remove(Id); owner.ownershipRoles.Remove(Id); owner.revision++; owner.Observation.Released(Id, owner.revision, "no-provider-effect"); return; }
                owner.entries[Id] = entry with { Phase = ModelOwnershipPhase.Unknown,
                    Uncertainty = "ownership-scope-ended-without-confirmed-release" };
                owner.revision++;
                owner.Observation.Accounting(owner.entries[Id], owner.revision);
            }
        }
    }
}
