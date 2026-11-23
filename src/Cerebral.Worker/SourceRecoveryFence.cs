namespace Cerebral.Worker;

/// <summary>
/// A process-owned recovery barrier. Persisted marker edits, timers, client disconnects and
/// successful inspection cannot release it. Only the one accepted owner resume can do so.
/// </summary>
internal sealed class SourceRecoveryFence : IDisposable
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<SourceRecoveryResumeTicket> request =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SourceRecoveryResumeTicket? accepted;
    private string state = "inspecting";
    private string? failure;
    private bool disposed;

    internal bool IsOpen { get { lock (gate) return state == "open"; } }
    internal string State { get { lock (gate) return state; } }
    internal string? Failure { get { lock (gate) return failure; } }

    internal Task WaitForReleaseAsync(CancellationToken token) => release.Task.WaitAsync(token);
    internal Task<SourceRecoveryResumeTicket> WaitForRequestAsync(CancellationToken token) => request.Task.WaitAsync(token);

    internal SourceRecoveryResumeTicket AcceptResume(CancellationToken caller)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            caller.ThrowIfCancellationRequested();
            if (state != "inspecting") throw new InvalidOperationException("source_recovery_resume_already_requested");
            var ticket = new SourceRecoveryResumeTicket(caller);
            accepted = ticket;
            state = "composition-requested";
            ticket.Cancellation = caller.Register(() => Fail(ticket, "source_recovery_resume_cancelled"));
            // Cancellation can run synchronously during registration; never resurrect that failed request.
            if (state != "composition-requested") throw new OperationCanceledException(caller);
            request.TrySetResult(ticket);
            return ticket;
        }
    }

    internal void BeginValidation(SourceRecoveryResumeTicket ticket)
    {
        lock (gate)
        {
            RequireTicket(ticket);
            ticket.Caller.ThrowIfCancellationRequested();
            if (state != "composition-requested") throw new InvalidOperationException("source_recovery_resume_unconfirmed");
            state = "validating";
        }
    }

    internal void Release(SourceRecoveryResumeTicket ticket, SourceRecoveryReleaseEvidence evidence,
        CancellationToken validationToken = default)
    {
        lock (gate)
        {
            RequireTicket(ticket);
            ticket.Caller.ThrowIfCancellationRequested();
            validationToken.ThrowIfCancellationRequested();
            if (state != "validating" || !evidence.Valid)
            {
                if (state != "open") { state = "failed"; failure = "source_recovery_validation_failed"; }
                throw new InvalidOperationException("source_recovery_release_validation_failed");
            }
            state = "open";
            release.TrySetResult();
        }
    }

    internal void Fail(SourceRecoveryResumeTicket ticket, string code)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(accepted, ticket) || state == "open") return;
            state = "failed";
            failure = code is "source_recovery_resume_cancelled" or "source_recovery_validation_failed" or
                "source_recovery_initialization_failed" or "source_recovery_resume_unconfirmed"
                ? code : "source_recovery_validation_failed";
        }
    }

    private void RequireTicket(SourceRecoveryResumeTicket ticket)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(accepted, ticket)) throw new InvalidOperationException("source_recovery_request_identity_changed");
    }

    public void Dispose()
    {
        SourceRecoveryResumeTicket? ticket;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            state = "stopped";
            ticket = accepted;
        }
        // A registration can be running Fail under gate; dispose outside the same lock.
        ticket?.Cancellation.Dispose();
    }
}

internal sealed class SourceRecoveryResumeTicket(CancellationToken caller)
{
    internal CancellationToken Caller { get; } = caller;
    internal CancellationTokenRegistration Cancellation;
}

/// <summary>Derived only by the private host from observed runtime and exact persisted checks.</summary>
internal sealed record SourceRecoveryReleaseEvidence(string HoldId, long Generation, long Revision,
    long EngineEpoch, string HoldState, bool EnginePaused, bool OriginalPriorEnginePaused,
    bool EngineHealthy, bool EnginePending, int EngineEvaluations, int UnreleasedContexts,
    int CapturedWorkCount, bool BaselineRowsMatched, bool PassiveInitializersVerified, bool HostStarted)
{
    internal bool Valid =>
        HoldId == SourceRecoveryManifest.HoldId && Generation == 1 && Revision == 4 &&
        EngineEpoch == 5 && HoldState == "resumed" && !EnginePaused && !OriginalPriorEnginePaused &&
        EngineHealthy && !EnginePending && EngineEvaluations == 0 && UnreleasedContexts == 0 &&
        CapturedWorkCount == 0 && BaselineRowsMatched && PassiveInitializersVerified && HostStarted;
}
