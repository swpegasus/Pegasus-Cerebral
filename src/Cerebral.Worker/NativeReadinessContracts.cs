namespace Cerebral.Worker;

internal enum RegisteredNativeReadinessStage { Validation, Loading, Capture, Residency, Release }
internal enum RegisteredNativeReadinessCleanup { Active, KnownSettled, KnownResident, RetainedUnknown }

internal sealed record RegisteredNativeReadinessAccountingIdentity(string EntryId,
    ModelOwnershipKind Kind, ModelOwnershipPhase? Phase, bool Retired, bool KnownResident,
    string? Uncertainty = null);

internal sealed record RegisteredNativeReadinessObservation(string AttemptId, string ModelId,
    RegisteredNativeReadinessStage Stage, RegisteredNativeReadinessCleanup Cleanup,
    string AccountingGenerationId, string? RuntimeGenerationId, string? InstanceId,
    string? PinAccountingEntryId, IReadOnlyList<RegisteredNativeReadinessAccountingIdentity> OwnedAccounting,
    bool IdentityCoverageComplete,
    IReadOnlyList<RegisteredNativeReadinessAccountingIdentity>? BlockingAccounting = null)
{
    // Cleanup evidence only. A new accepted request and normal admission are still required.
    internal bool AllowsFreshAcceptedAttempt => IdentityCoverageComplete &&
        Cleanup is RegisteredNativeReadinessCleanup.KnownSettled or RegisteredNativeReadinessCleanup.KnownResident;
}

internal interface IRegisteredNativeReadinessFailure
{
    // Reads only this attempt's sealed set of authoritative ownership handles. Later exact
    // retirement can improve the observation; unrelated current accounting cannot refund it.
    RegisteredNativeReadinessObservation Observation { get; }
    Exception OriginalException { get; }
    Exception? CleanupException { get; }
}

internal sealed class RegisteredNativeReadinessException : IOException, IRegisteredNativeReadinessFailure
{
    internal string Code => "registered_native_readiness_failed";
    private readonly Func<RegisteredNativeReadinessObservation> observe;
    public RegisteredNativeReadinessObservation Observation => observe();
    public Exception OriginalException { get; }
    public Exception? CleanupException { get; }
    internal RegisteredNativeReadinessException(Exception original, RegisteredNativeReadinessObservation observation)
        : this(original, () => observation) { }
    internal RegisteredNativeReadinessException(Exception original, Func<RegisteredNativeReadinessObservation> observe,
        Exception? cleanup = null)
        : base("Registered native readiness failed; inspect its owned cleanup observation.", original)
    { OriginalException = original; this.observe = observe; CleanupException = cleanup; }
}
internal sealed class RegisteredNativeReadinessCanceledException : OperationCanceledException, IRegisteredNativeReadinessFailure
{
    internal string Code => "registered_native_readiness_cancelled";
    private readonly Func<RegisteredNativeReadinessObservation> observe;
    public RegisteredNativeReadinessObservation Observation => observe();
    public Exception OriginalException { get; }
    public Exception? CleanupException { get; }
    internal RegisteredNativeReadinessCanceledException(OperationCanceledException original,
        RegisteredNativeReadinessObservation observation) : this(original, () => observation) { }
    internal RegisteredNativeReadinessCanceledException(OperationCanceledException original,
        Func<RegisteredNativeReadinessObservation> observe, Exception? cleanup = null)
        : base("Registered native readiness was cancelled; inspect its owned cleanup observation.",
            original, original.CancellationToken)
    { OriginalException = original; this.observe = observe; CleanupException = cleanup; }
}

// The cached registration belongs to Engine. This lease owns only the temporary Busy/accounting
// bridge. Core releases it after every borrower transferred to actual preparation or settled.
internal sealed class RegisteredNativeReadinessLease : IAsyncDisposable
{
    internal IRegisteredNativeGenerationService Registration { get; }
    private readonly NativeReadinessOwnershipScope ownership;
    private readonly Lazy<Task> release;
    internal RegisteredNativeReadinessLease(IRegisteredNativeGenerationService registration,
        NativeReadinessOwnershipScope ownership, Action releasePin)
    {
        Registration = registration; this.ownership = ownership;
        release = new(() =>
        {
            ownership.Stage = RegisteredNativeReadinessStage.Release;
            try { releasePin(); return Task.CompletedTask; }
            catch (Exception error) { return Task.FromException(ownership.Wrap(error)); }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }
    internal RegisteredNativeReadinessObservation Observe() => ownership.Observe();
    public ValueTask DisposeAsync() => new(release.Value);
}
