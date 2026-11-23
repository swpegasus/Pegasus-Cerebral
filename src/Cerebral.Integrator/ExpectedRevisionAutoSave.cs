namespace Cerebral.Integrator;

internal enum ExpectedRevisionAutoSavePhase
{
    Clean,
    Waiting,
    Saving,
    Saved,
    Invalid,
    Conflict,
    Failed
}

internal sealed record ExpectedRevisionAutoSaveStatus(ExpectedRevisionAutoSavePhase Phase, string Message);

internal sealed class SettingsRevisionConflictException(string message) : InvalidOperationException(message);
internal sealed class SettingsDraftInvalidException(string message) : ArgumentException(message);

/// <summary>
/// Serializes a revisioned settings draft behind a short debounce. The controller owns the last
/// acknowledged snapshot; draft text remains owned by the view and is never replaced after a
/// validation error or conflict.
/// </summary>
internal sealed class ExpectedRevisionAutoSave<T> : IAsyncDisposable where T : class
{
    private readonly Func<T, T> buildCandidate;
    private readonly Func<T, CancellationToken, Task<T>> save;
    private readonly Action<T> adopt;
    private readonly Action<ExpectedRevisionAutoSaveStatus> report;
    private readonly TimeSpan delay;
    private readonly Func<TimeSpan, CancellationToken, Task> wait;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim serial = new(1, 1);
    private CancellationTokenSource? pendingDelay;
    private T baseline;
    private long changedVersion;
    private long savedVersion;
    private bool conflict;
    private bool unconfirmed;
    private bool disposed;

    public ExpectedRevisionAutoSave(T baseline, Func<T, T> buildCandidate,
        Func<T, CancellationToken, Task<T>> save, Action<T> adopt,
        Action<ExpectedRevisionAutoSaveStatus> report, TimeSpan? delay = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        this.baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        this.buildCandidate = buildCandidate ?? throw new ArgumentNullException(nameof(buildCandidate));
        this.save = save ?? throw new ArgumentNullException(nameof(save));
        this.adopt = adopt ?? throw new ArgumentNullException(nameof(adopt));
        this.report = report ?? throw new ArgumentNullException(nameof(report));
        this.delay = delay ?? TimeSpan.FromMilliseconds(450);
        if (this.delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        this.wait = wait ?? Task.Delay;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetimeToken = lifetime.Token;
        report(new(ExpectedRevisionAutoSavePhase.Clean, "Changes save automatically."));
    }

    public void Changed()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref changedVersion);
        if (unconfirmed)
        {
            ReportUnconfirmed();
            return;
        }
        if (conflict)
        {
            report(new(ExpectedRevisionAutoSavePhase.Conflict,
                "The service settings changed elsewhere. This draft was not applied; close and reopen these settings to edit the current revision."));
            return;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        var prior = Interlocked.Exchange(ref pendingDelay, source);
        CancelDelay(prior);
        report(new(ExpectedRevisionAutoSavePhase.Waiting, "Waiting briefly before saving…"));
        _ = RunAfterDelayAsync(source);
    }

    public async Task<bool> FlushAsync()
    {
        ThrowIfDisposed();
        CancelDelay(Interlocked.Exchange(ref pendingDelay, null));
        if (unconfirmed) return false;
        if (Volatile.Read(ref changedVersion) == Volatile.Read(ref savedVersion)) return true;
        if (conflict) return false;
        return await SaveLatestAsync(lifetimeToken);
    }

    private async Task RunAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await wait(delay, source.Token);
            if (!source.IsCancellationRequested) await SaveLatestAsync(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error)
        {
            // The normal save path converts failures to status. This is a final guard against an
            // asynchronous UI callback becoming an unobserved exception.
            if (!disposed) report(new(ExpectedRevisionAutoSavePhase.Failed, "Automatic save failed: " + error.Message));
        }
        finally
        {
            Interlocked.CompareExchange(ref pendingDelay, null, source);
            source.Dispose();
        }
    }

    private async Task<bool> SaveLatestAsync(CancellationToken cancellationToken)
    {
        try { await serial.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (ObjectDisposedException) when (disposed) { return false; }
        try
        {
            if (disposed || cancellationToken.IsCancellationRequested || lifetimeToken.IsCancellationRequested || conflict || unconfirmed) return false;
            var version = Volatile.Read(ref changedVersion);
            if (version <= Volatile.Read(ref savedVersion)) return true;

            T candidate;
            try { candidate = buildCandidate(baseline); }
            catch (ArgumentException error)
            {
                report(new(ExpectedRevisionAutoSavePhase.Invalid, error.Message));
                return false;
            }
            catch (FormatException error)
            {
                report(new(ExpectedRevisionAutoSavePhase.Invalid, error.Message));
                return false;
            }
            catch (OverflowException error)
            {
                report(new(ExpectedRevisionAutoSavePhase.Invalid, error.Message));
                return false;
            }

            report(new(ExpectedRevisionAutoSavePhase.Saving, "Saving configuration…"));
            // A superseded delay or queued save may stop before dispatch. Once dispatched,
            // a newer keystroke/Flush must not cancel observation of its acknowledgement.
            if (cancellationToken.IsCancellationRequested || lifetimeToken.IsCancellationRequested) return false;
            T saved;
            try
            {
                saved = await save(candidate, lifetimeToken);
            }
            catch (SettingsRevisionConflictException error)
            {
                conflict = true;
                report(new(ExpectedRevisionAutoSavePhase.Conflict, error.Message));
                return false;
            }
            catch (SettingsDraftInvalidException error)
            {
                report(new(ExpectedRevisionAutoSavePhase.Invalid, error.Message));
                return false;
            }
            catch (Exception error)
            {
                // The delegate was entered. Cancellation/transport failure cannot prove
                // that the service did not commit. Keep the draft, never resend it blindly.
                unconfirmed = true;
                if (!disposed) ReportUnconfirmed(error.Message);
                return false;
            }

            // Advance the acknowledged baseline before view callbacks. A view refresh
            // failure does not turn a known service acknowledgement into an unknown save.
            baseline = saved;
            Volatile.Write(ref savedVersion, version);
            if (!disposed)
            {
                adopt(saved);
                report(Volatile.Read(ref changedVersion) == version
                    ? new(ExpectedRevisionAutoSavePhase.Saved, "Saved.")
                    : new(ExpectedRevisionAutoSavePhase.Waiting, "A newer draft is waiting to save…"));
            }

            // A newer edit may have arrived while this save was in flight. Its own debounce task
            // will follow; FlushAsync also observes the still-dirty version and saves it directly.
            return Volatile.Read(ref savedVersion) == Volatile.Read(ref changedVersion);
        }
        finally { serial.Release(); }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void ReportUnconfirmed(string? detail = null) => report(new(ExpectedRevisionAutoSavePhase.Failed,
        "Save outcome is unconfirmed. This draft has not been resent. Read the saved settings and reopen this editor before another save." +
        (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail)));

    private static void CancelDelay(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        // A completed delay owns disposal; cancellation racing its cleanup is harmless.
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        CancelDelay(Interlocked.Exchange(ref pendingDelay, null));
        await serial.WaitAsync();
        serial.Release();
        serial.Dispose();
        lifetime.Dispose();
    }
}
