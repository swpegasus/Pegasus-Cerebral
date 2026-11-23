namespace Cerebral.Worker;

// A bounded per-attempt identity collector, not a second accounting store. Begin registers
// authoritative handles after releasing accounting's lock. Snapshots copy the handle list
// before reading accounting, so neither direction nests the two locks.
internal sealed class NativeReadinessOwnershipScope : IDisposable
{
    private static readonly AsyncLocal<NativeReadinessOwnershipScope?> Current = new();
    private readonly NativeReadinessOwnershipScope? previous;
    private readonly ModelExecutionAccounting accounting;
    private readonly object gate = new();
    private readonly int maximum;
    private readonly List<(ModelExecutionAccounting.ModelOwnership Handle, ModelOwnershipKind Kind)> owned = [];
    private readonly Dictionary<string, ModelOwnershipKind> blockers = new(StringComparer.Ordinal);
    private readonly HashSet<string> resident = new(StringComparer.Ordinal);
    private bool sealedScope, coverage = true;
    private int begins;
    private string? runtimeId, instanceId, pinId;
    internal Exception? PrimaryFailure { get; private set; }
    internal static void CaptureLoaderFailure(ModelExecutionAccounting? accounting, string modelId, Exception error)
    {
        var scope = Current.Value;
        if (scope is null || !ReferenceEquals(scope.accounting, accounting) || scope.ModelId != modelId) return;
        lock (scope.gate) { if (!scope.sealedScope) scope.PrimaryFailure ??= error; }
    }
    internal static void BlockedBy(ModelExecutionAccounting accounting, ModelOwnershipEntry entry)
    {
        var scope = Current.Value;
        if (scope is null || !ReferenceEquals(scope.accounting, accounting) || scope.ModelId != entry.ModelId) return;
        lock (scope.gate)
        {
            if (scope.owned.Any(value => value.Handle.Id == entry.Id)) return;
            if (scope.blockers.Count < scope.maximum) scope.blockers.TryAdd(entry.Id, entry.Kind);
            else scope.coverage = false;
        }
    }
    internal Exception? CleanupFailure { get; set; }
    internal string AttemptId { get; } = Guid.NewGuid().ToString("N");
    internal string ModelId { get; }
    internal RegisteredNativeReadinessStage Stage { get; set; }

    internal NativeReadinessOwnershipScope(ModelExecutionAccounting accounting, string modelId, int maximum = 64)
    {
        if (maximum is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximum));
        this.accounting = accounting; ModelId = modelId; this.maximum = maximum;
        previous = Current.Value; Current.Value = this;
    }

    internal static BeginTicket? BeforeBegin(ModelExecutionAccounting accounting, string modelId)
    {
        var scope = Current.Value;
        if (scope is null || !ReferenceEquals(scope.accounting, accounting) || scope.ModelId != modelId) return null;
        lock (scope.gate)
        {
            // An inherited background context cannot start new owned work after finalization.
            // This refusal occurs BEFORE accounting inserts an entry. An in-progress Begin
            // at seal time instead makes coverage permanently incomplete.
            if (scope.sealedScope) throw new InvalidOperationException("registered_native_readiness_scope_closed");
            scope.begins++;
            return new(scope);
        }
    }

    internal sealed class BeginTicket(NativeReadinessOwnershipScope scope) : IDisposable
    {
        private bool disposed;
        internal void Register(ModelExecutionAccounting.ModelOwnership handle, ModelOwnershipKind kind)
        {
            lock (scope.gate)
            {
                if (scope.owned.Count < scope.maximum) scope.owned.Add((handle, kind));
                else scope.coverage = false;
            }
        }
        public void Dispose()
        {
            lock (scope.gate)
            {
                if (disposed) return;
                disposed = true; scope.begins--;
                // A failed Begin creates no handle. Register is deliberately nonthrowing for
                // saturation; that cannot strand an inserted row or mask a loader exception.
            }
        }
    }

    internal void ConfirmResident(ModelExecutionAccounting.ModelOwnership? handle)
    { if (handle is not null) lock (gate) resident.Add(handle.Id); }
    internal void Captured(string runtime, string instance, string pin)
    { lock (gate) { runtimeId = runtime; instanceId = instance; pinId = pin; } }
    internal void Seal()
    { lock (gate) { if (begins != 0) coverage = false; sealedScope = true; } }

    internal RegisteredNativeReadinessObservation Observe()
    {
        (ModelExecutionAccounting.ModelOwnership Handle, ModelOwnershipKind Kind)[] handles;
        KeyValuePair<string, ModelOwnershipKind>[] dependencies;
        HashSet<string> known;
        bool complete;
        string? runtime, instance, pin;
        lock (gate)
        {
            dependencies = blockers.ToArray();
            handles = owned.ToArray(); known = new(resident, StringComparer.Ordinal);
            complete = sealedScope && coverage && begins == 0;
            runtime = runtimeId; instance = instanceId; pin = pinId;
        }
        var identities = handles.Select(value =>
        {
            var entry = value.Handle.ReadEntry();
            return new RegisteredNativeReadinessAccountingIdentity(value.Handle.Id, value.Kind,
                entry?.Phase, entry is null, entry?.Phase == ModelOwnershipPhase.Ready && known.Contains(value.Handle.Id), entry?.Uncertainty);
        }).ToArray();
        var blocking = dependencies.Select(value =>
        {
            var entry = accounting.ReadEntry(value.Key);
            return new RegisteredNativeReadinessAccountingIdentity(value.Key, value.Value,
                entry?.Phase, entry is null, false, entry?.Uncertainty);
        }).ToArray();
        var activePin = identities.Any(value => value.EntryId == pin && !value.Retired &&
            value.Phase == ModelOwnershipPhase.Ready);
        var cleanup = !complete || blocking.Any(value => !value.Retired) ? RegisteredNativeReadinessCleanup.RetainedUnknown :
            activePin ? RegisteredNativeReadinessCleanup.Active :
            identities.All(value => value.Retired) ? RegisteredNativeReadinessCleanup.KnownSettled :
            identities.All(value => value.Retired || value.KnownResident) ? RegisteredNativeReadinessCleanup.KnownResident :
            RegisteredNativeReadinessCleanup.RetainedUnknown;
        return new(AttemptId, ModelId, Stage, cleanup, accounting.ProcessGenerationId,
            runtime, instance, pin, Array.AsReadOnly(identities), complete, Array.AsReadOnly(blocking));
    }

    internal Exception Wrap(Exception error)
    {
        Seal();
        var original = PrimaryFailure ?? error;
        var cleanup = CleanupFailure ?? (!ReferenceEquals(original, error) ? error : null);
        return original is OperationCanceledException cancelled
            ? new RegisteredNativeReadinessCanceledException(cancelled, Observe, cleanup)
            : new RegisteredNativeReadinessException(original, Observe, cleanup);
    }
    public void Dispose()
    {
        Seal();
        if (ReferenceEquals(Current.Value, this)) Current.Value = previous;
    }
}
