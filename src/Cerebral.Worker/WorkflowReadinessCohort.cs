using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cerebral.Worker;

internal enum WorkflowReadinessCohortState
{
    Uninitialized, Initializing, Ready, Closing, Retired, Failed, Cancelled, Unknown
}

internal sealed record WorkflowReadinessCohortSnapshot(
    WorkflowReadinessCohortState State, bool AdmissionOpen,
    bool InitializationStarted, bool InitializationSettled, int Waiters, int Borrowers,
    bool LeasePresent, bool DisposalStarted, bool DisposalSettled,
    bool CancellationRequested, bool CancellationSettled, bool CleanupUnconfirmed, bool CanReplace,
    string? PrimaryFailureType, string? CleanupFailureType, string? CancellationFailureType);

// One host-selected, frozen-binding attempt. The host selects and reserves under host -> cohort
// lock order; this controller never calls the host. A different attempt needs a different cohort.
internal sealed class WorkflowReadinessCohort<TLease> : IAsyncDisposable where TLease : class, IAsyncDisposable
{
    internal const int MaximumBorrows = 64;
    private readonly object gate = new();
    private readonly Func<CancellationToken, Task<TLease>> factory;
    private readonly Func<Exception, bool>? isCleanupUnconfirmed;
    private readonly CancellationTokenSource operation = new();
    private readonly TaskCompletionSource ready = Signal();
    private readonly TaskCompletionSource initializationDone = Signal();
    private readonly TaskCompletionSource cancellationDone = Signal();
    private readonly TaskCompletionSource<WorkflowReadinessCohortSnapshot> settlement =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration hostRegistration;
    private TLease? lease;
    private Exception? primaryException, cleanupException, cancellationException, initializationException;
    private bool constructed, admissionOpen = true, initializationStarted, initializationSettled;
    private bool retiring, cancellationStarted, cancellationSettled, retirementStarted;
    private bool disposalStarted, disposalSettled, cleanupUnconfirmed, settled;
    private int waiters, borrowers;
    private long version = 1;
    private bool reconciliationBlocked;
    private readonly List<Exception> unconfirmedFailures = [];

    internal WorkflowReadinessCohort(Func<CancellationToken, Task<TLease>> factory,
        CancellationToken hostStopping = default, Func<Exception, bool>? isCleanupUnconfirmed = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.isCleanupUnconfirmed = isCleanupUnconfirmed;
        // A pre-cancelled host token may call CloseAdmission synchronously. Do not finalize or
        // dispose the registration until its actual value has been published by construction.
        hostRegistration = hostStopping.UnsafeRegister(static value =>
            ((WorkflowReadinessCohort<TLease>)value!).CloseAdmission(), this);
        lock (gate) constructed = true;
        DriveRetirement();
    }

    internal Task<WorkflowReadinessCohortSnapshot> Settlement => settlement.Task;
    internal bool CanReplace { get { lock (gate) return settled && !cleanupUnconfirmed; } }
    internal Exception? PrimaryException { get { lock (gate) return primaryException; } }
    internal Exception? CleanupException { get { lock (gate) return cleanupException; } }
    internal Exception? CancellationException { get { lock (gate) return cancellationException; } }
    internal WorkflowReadinessCohortSnapshot Observe() { lock (gate) return SnapshotLocked(); }

    // Only a distinct, still-unprepared accepted request may use this two-phase proof. The host
    // evaluates typed owned observations outside every gate, then rechecks its current cohort and
    // admission under host -> cohort lock order before applying the callback-free confirmation.
    internal SettlementProbe ObserveSettlement()
    {
        lock (gate)
            return new(this, version, SnapshotLocked(), unconfirmedFailures.ToArray(),
                settled && cleanupUnconfirmed && !reconciliationBlocked &&
                initializationSettled && disposalSettled && cancellationSettled &&
                waiters == 0 && borrowers == 0 && unconfirmedFailures.Count != 0);
    }

    internal sealed class SettlementProbe
    {
        internal readonly WorkflowReadinessCohort<TLease> Owner;
        internal readonly long Version;
        internal WorkflowReadinessCohortSnapshot Snapshot { get; }
        internal IReadOnlyList<Exception> Failures { get; }
        internal bool Eligible { get; }
        internal SettlementProbe(WorkflowReadinessCohort<TLease> owner, long version,
            WorkflowReadinessCohortSnapshot snapshot, Exception[] failures, bool eligible)
        {
            Owner = owner; Version = version; Snapshot = snapshot;
            Failures = Array.AsReadOnly(failures); Eligible = eligible;
        }
        internal SettlementConfirmation? Evaluate(Func<Exception, bool> confirmsOwnedSettlement)
        {
            ArgumentNullException.ThrowIfNull(confirmsOwnedSettlement);
            if (!Eligible) return null;
            // Foreign getters may throw. A failed proof never changes or erases owned evidence.
            try
            {
                foreach (var failure in Failures)
                    if (!confirmsOwnedSettlement(failure)) return null;
                return new(this);
            }
            catch { return null; }
        }
    }

    internal sealed class SettlementConfirmation
    {
        internal readonly SettlementProbe Probe;
        internal SettlementConfirmation(SettlementProbe probe) => Probe = probe;
    }

    internal bool TryConfirmSettlement(SettlementConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var expected = confirmation.Probe;
        lock (gate)
        {
            if (!ReferenceEquals(expected.Owner, this) || !expected.Eligible ||
                expected.Version != version || !settled || !cleanupUnconfirmed ||
                reconciliationBlocked || !initializationSettled || !disposalSettled ||
                !cancellationSettled || waiters != 0 || borrowers != 0 ||
                expected.Failures.Count != unconfirmedFailures.Count) return false;
            for (var i = 0; i < unconfirmedFailures.Count; i++)
                if (!ReferenceEquals(expected.Failures[i], unconfirmedFailures[i])) return false;
            // The exact temporary lease is now proven released by its owned observation.
            // Keep original exceptions and the first Settlement result as historical evidence.
            lease = null;
            cleanupUnconfirmed = false;
            AdvanceVersionLocked();
            return true;
        }
    }

    // This method is deliberately synchronous and callback-free so the parent can atomically
    // select a cohort and reserve interest under its own gate. Provider work starts in Acquire.
    internal Borrow ReserveBorrow()
    {
        lock (gate)
        {
            if (!admissionOpen) throw new InvalidOperationException("workflow_readiness_cohort_closed");
            if (waiters + borrowers >= MaximumBorrows)
                throw new InvalidOperationException("workflow_readiness_cohort_full");
            var value = new Borrow(this);
            waiters++;
            AdvanceVersionLocked();
            return value;
        }
    }

    internal async Task<Borrow> BorrowAsync(CancellationToken waiterToken = default)
    {
        var value = ReserveBorrow();
        try { await value.AcquireAsync(waiterToken).ConfigureAwait(false); return value; }
        catch
        {
            // Withdrawal is prompt even if an abandoned initializer ignores cancellation.
            // Settlement remains owned and observed by this cohort; no late success is orphaned.
            _ = Release(value, null);
            throw;
        }
    }

    internal void CloseAdmission()
    {
        lock (gate) { admissionOpen = false; retiring = true; AdvanceVersionLocked(); }
        DriveRetirement();
    }

    public async ValueTask DisposeAsync()
    {
        CloseAdmission();
        await Settlement.ConfigureAwait(false);
        ThrowCleanupFailure();
    }

    internal sealed class Borrow : IAsyncDisposable
    {
        internal readonly WorkflowReadinessCohort<TLease> Owner;
        internal readonly CancellationTokenSource Withdrawal = new();
        internal readonly TaskCompletionSource Acquired = Signal(), AcquireFinished = Signal(), Released = Signal();
        internal bool AcquireStarted, ReleaseRequested, InterestReleased, HasLease;
        internal TLease? AcquiredLease;

        internal Borrow(WorkflowReadinessCohort<TLease> owner) => Owner = owner;
        internal TLease Lease
        {
            get
            {
                lock (Owner.gate)
                {
                    if (ReleaseRequested || !HasLease || AcquiredLease is null)
                        throw new ObjectDisposedException(nameof(Borrow), "Readiness is not held by this borrower.");
                    return AcquiredLease;
                }
            }
        }
        internal Task AcquireAsync(CancellationToken waiterToken = default) => Owner.Acquire(this, waiterToken);
        // The caller releases only after its real preparation transfer or failed/cancelled
        // preparation cleanup has settled. A release is not proof of native admission.
        internal ValueTask ReleaseAsync(Exception? primaryFailure = null) => new(Owner.Release(this, primaryFailure));
        public ValueTask DisposeAsync() => ReleaseAsync();
    }

    private Task Acquire(Borrow value, CancellationToken waiterToken)
    {
        bool initialize = false;
        lock (gate)
        {
            if (value.ReleaseRequested) throw new ObjectDisposedException(nameof(Borrow));
            if (value.AcquireStarted) return value.Acquired.Task;
            value.AcquireStarted = true;
            AdvanceVersionLocked();
            if (admissionOpen && !waiterToken.IsCancellationRequested && !initializationStarted)
            {
                initializationStarted = true;
                initialize = true;
            }
        }
        _ = AcquireCoreAsync(value, waiterToken);
        if (initialize) _ = InitializeAsync();
        return value.Acquired.Task;
    }

    private async Task AcquireCoreAsync(Borrow value, CancellationToken waiterToken)
    {
        var succeeded = false;
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(
                new[] { waiterToken, operation.Token, value.Withdrawal.Token });
            waiting.Token.ThrowIfCancellationRequested();
            lock (gate)
                if (!admissionOpen || value.ReleaseRequested)
                    throw new InvalidOperationException("workflow_readiness_cohort_closed");
            await ready.Task.WaitAsync(waiting.Token).ConfigureAwait(false);
            waiting.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!admissionOpen || value.ReleaseRequested || lease is null)
                    throw new InvalidOperationException("workflow_readiness_cohort_closed");
                if (waiting.IsCancellationRequested) throw new OperationCanceledException(waiting.Token);
                value.AcquiredLease = lease;
                value.HasLease = true;
                waiters--;
                borrowers++;
                AdvanceVersionLocked();
            }
            succeeded = true;
            value.Acquired.TrySetResult();
        }
        catch (Exception error)
        {
            // Retirement cancellation cannot replace an already-committed failure from this
            // same initializer. Keep the exact owned exception, including cancellation subtypes.
            Exception observed = error;
            lock (gate)
                if (initializationSettled && ready.Task.IsFaulted && initializationException is not null)
                    observed = initializationException;
            value.Acquired.TrySetException(observed);
            _ = value.Acquired.Task.Exception;
        }
        finally
        {
            value.AcquireFinished.TrySetResult();
            if (!succeeded) _ = Release(value, null);
        }
    }

    private async Task InitializeAsync()
    {
        TLease? returned = null;
        Exception? failure = null;
        bool unknown = false;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            returned = await factory(operation.Token).ConfigureAwait(false);
            if (returned is null) throw new InvalidOperationException("workflow_readiness_factory_returned_null");
        }
        catch (Exception error) { failure = error; unknown = ClassifyUnconfirmed(error); }
        lock (gate)
        {
            lease = returned;
            initializationSettled = true;
            initializationException = failure;
            primaryException ??= failure;
            cleanupUnconfirmed |= unknown;
            if (unknown) { TrackUnconfirmedLocked(failure!); admissionOpen = false; retiring = true; }
            AdvanceVersionLocked();
        }
        if (failure is null) ready.TrySetResult();
        else { ready.TrySetException(failure); _ = ready.Task.Exception; }
        initializationDone.TrySetResult();
        DriveRetirement();
    }

    private Task Release(Borrow value, Exception? primaryFailure)
    {
        lock (gate)
        {
            if (value.ReleaseRequested) return value.Released.Task;
            value.ReleaseRequested = true;
            AdvanceVersionLocked();
        }
        _ = ReleaseCoreAsync(value, primaryFailure);
        return value.Released.Task;
    }

    private async Task ReleaseCoreAsync(Borrow value, Exception? primaryFailure)
    {
        if (primaryFailure is not null)
        {
            var unknown = ClassifyUnconfirmed(primaryFailure);
            lock (gate)
            {
                primaryException ??= primaryFailure; cleanupUnconfirmed |= unknown;
                if (unknown) { TrackUnconfirmedLocked(primaryFailure); admissionOpen = false; retiring = true; }
                AdvanceVersionLocked();
            }
        }
        try { await value.Withdrawal.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { RecordCancellationFailure(error); }
        if (value.AcquireStarted) await value.AcquireFinished.Task.ConfigureAwait(false);
        // Keep interest until its cancellation owner is actually cleaned up; another release
        // must not publish zero ownership and settle the cohort while this cleanup is pending.
        try { value.Withdrawal.Dispose(); }
        catch (Exception error) { RecordCleanupFailure(error); }
        bool last;
        lock (gate)
        {
            if (!value.InterestReleased)
            {
                value.InterestReleased = true;
                if (value.HasLease) borrowers--; else waiters--;
                value.AcquiredLease = null;
                AdvanceVersionLocked();
            }
            last = waiters + borrowers == 0;
            if (last) { admissionOpen = false; retiring = true; }
        }
        DriveRetirement();
        try
        {
            if (last)
            {
                await Settlement.ConfigureAwait(false);
                ThrowCleanupFailure();
            }
            value.Released.TrySetResult();
        }
        catch (Exception error)
        {
            value.Released.TrySetException(error);
            _ = value.Released.Task.Exception;
        }
    }

    private void DriveRetirement()
    {
        bool cancel = false, finish = false, noInitialization = false;
        lock (gate)
        {
            if (!constructed || !retiring) return;
            if (!cancellationStarted) { cancellationStarted = true; cancel = true; AdvanceVersionLocked(); }
            if (waiters + borrowers == 0 && !retirementStarted)
            {
                retirementStarted = true;
                AdvanceVersionLocked();
                finish = true;
                if (!initializationStarted)
                {
                    initializationSettled = true;
                    noInitialization = true;
                }
            }
        }
        // Every operation capable of running foreign code is outside the control gate.
        if (cancel) _ = CancelOperationAsync();
        if (noInitialization) initializationDone.TrySetResult();
        if (finish) _ = FinishRetirementAsync();
    }

    private async Task CancelOperationAsync()
    {
        try { await operation.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { RecordCancellationFailure(error); }
        finally
        {
            lock (gate) { cancellationSettled = true; AdvanceVersionLocked(); }
            cancellationDone.TrySetResult();
        }
    }

    private async Task FinishRetirementAsync()
    {
        await cancellationDone.Task.ConfigureAwait(false);
        await initializationDone.Task.ConfigureAwait(false);
        TLease? owned;
        lock (gate) { owned = lease; disposalStarted = owned is not null; }
        var disposed = owned is null;
        if (owned is not null)
        {
            try { await owned.DisposeAsync().ConfigureAwait(false); disposed = true; }
            catch (Exception error)
            {
                lock (gate) { cleanupException ??= error; AdvanceVersionLocked(); }
                // A throwing Dispose is unconfirmed unless the supplied typed classifier
                // proves release. Keep its exact error even when release is confirmed.
                var unknown = isCleanupUnconfirmed is null || ClassifyUnconfirmed(error);
                lock (gate)
                {
                    cleanupUnconfirmed |= unknown;
                    if (unknown) TrackUnconfirmedLocked(error);
                    AdvanceVersionLocked();
                }
                disposed = !unknown;
            }
        }
        // Callback delivery and the initializer are actually settled before disposing either
        // cancellation owner. Unconfirmed lease disposal retains the lease reference.
        try { hostRegistration.Dispose(); }
        catch (Exception error) { RecordCleanupFailure(error); }
        try { operation.Dispose(); }
        catch (Exception error) { RecordCleanupFailure(error); }
        WorkflowReadinessCohortSnapshot snapshot;
        lock (gate)
        {
            if (disposed) lease = null;
            disposalSettled = true;
            settled = true;
            AdvanceVersionLocked();
            snapshot = SnapshotLocked();
        }
        settlement.TrySetResult(snapshot);
    }

    private bool ClassifyUnconfirmed(Exception error)
    {
        if (isCleanupUnconfirmed is null) return false;
        // The composition supplies its typed Engine cleanup classifier. No name/message-based
        // inference is performed here; classifier failure itself cannot permit a fresh attempt.
        try { return isCleanupUnconfirmed(error); }
        catch (Exception failure) { RecordCleanupFailure(failure); return true; }
    }

    private void RecordCleanupFailure(Exception error)
    {
        lock (gate)
        {
            cleanupException ??= error; cleanupUnconfirmed = true; reconciliationBlocked = true;
            admissionOpen = false; retiring = true; TrackUnconfirmedLocked(error); AdvanceVersionLocked();
        }
    }
    private void RecordCancellationFailure(Exception error)
    {
        lock (gate)
        {
            cancellationException ??= error; cleanupUnconfirmed = true; reconciliationBlocked = true;
            admissionOpen = false; retiring = true; TrackUnconfirmedLocked(error); AdvanceVersionLocked();
        }
    }

    private void TrackUnconfirmedLocked(Exception error)
    {
        foreach (var existing in unconfirmedFailures)
            if (ReferenceEquals(existing, error)) return;
        // Unknown closes admission: at most the bounded outstanding borrowers, initializer and
        // lease disposal can add causes. Unexpected overflow permanently blocks reconciliation.
        if (unconfirmedFailures.Count >= MaximumBorrows + 2) reconciliationBlocked = true;
        else unconfirmedFailures.Add(error);
    }

    private void AdvanceVersionLocked()
    {
        if (version == long.MaxValue) reconciliationBlocked = true;
        else version++;
    }

    private void ThrowCleanupFailure()
    {
        Exception? failure;
        lock (gate) failure = cleanupException ?? cancellationException;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private WorkflowReadinessCohortSnapshot SnapshotLocked()
    {
        var state = cleanupUnconfirmed ? WorkflowReadinessCohortState.Unknown :
            settled ? primaryException is OperationCanceledException ? WorkflowReadinessCohortState.Cancelled :
                primaryException is not null || cleanupException is not null ?
                    WorkflowReadinessCohortState.Failed : WorkflowReadinessCohortState.Retired :
            retiring ? WorkflowReadinessCohortState.Closing :
            !initializationStarted ? WorkflowReadinessCohortState.Uninitialized :
            !initializationSettled ? WorkflowReadinessCohortState.Initializing :
            lease is not null ? WorkflowReadinessCohortState.Ready :
            primaryException is OperationCanceledException ? WorkflowReadinessCohortState.Cancelled :
                WorkflowReadinessCohortState.Failed;
        return new(state, admissionOpen, initializationStarted, initializationSettled, waiters, borrowers,
            lease is not null, disposalStarted, disposalSettled, cancellationStarted,
            !cancellationStarted || cancellationSettled, cleanupUnconfirmed, settled && !cleanupUnconfirmed,
            FailureType(primaryException), FailureType(cleanupException), FailureType(cancellationException));
    }

    private static string? FailureType(Exception? error)
    {
        if (error is null) return null;
        var name = error.GetType().Name;
        if (name.Length > 128) name = name[..128];
        return string.Create(name.Length, name, static (span, value) =>
        {
            for (var i = 0; i < value.Length; i++)
                span[i] = char.IsAsciiLetterOrDigit(value[i]) || value[i] is '_' or '.' ? value[i] : '_';
        });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
