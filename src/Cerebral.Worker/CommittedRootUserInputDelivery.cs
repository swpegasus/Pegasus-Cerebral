using System.Threading.Channels;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// Host-owned discovery and same-instance lifetime. EligibleObservers returns the
/// currently enabled and source-profile-scoped package bindings using only body-free
/// coordinates. TryAcquire independently rechecks each exact loaded instance before
/// returning its lease. Neither method invokes package code or sees UserText.
/// </summary>
internal sealed record CommittedRootUserInputObserverTarget(
    string PackageId, string Generation, object InstanceToken);

internal interface ICommittedRootUserInputDeliveryGate
{
    IReadOnlyList<CommittedRootUserInputObserverTarget> EligibleObservers(CommittedRootUserInputSource source);
    ICommittedRootUserInputCallbackLease? TryAcquire(
        CommittedRootUserInputObserverTarget target, CommittedRootUserInputSource source);
}

/// <summary>
/// A lease on the exact loaded callback instance. Its owner must refuse package unload
/// while this lease is live, including after cancellation or timeout.
/// </summary>
internal interface ICommittedRootUserInputCallbackLease : IDisposable
{
    ValueTask InvokeAsync(CommittedRootUserInputObservation observation, CancellationToken token);
}

internal enum CommittedRootUserInputEnqueueResult
{
    Queued,
    Duplicate,
    Ineligible,
    Full,
    Stopped
}

/// <summary>
/// A bounded, best-effort post-commit lane. TryEnqueue performs no package call, wait,
/// journal access, settings read or callback; callers invoke it only after all durable,
/// package, legacy and idle admission gates have returned.
/// </summary>
internal sealed class CommittedRootUserInputDelivery : IAsyncDisposable
{
    internal const int QueueCapacity = 64;
    internal const int MaximumActiveCallbacks = 4;
    internal const int MaximumObserverModules = 32;
    internal const int RecentEventCapacity = 256;
    internal static readonly TimeSpan CallbackDeadline = TimeSpan.FromSeconds(10);

    private readonly Channel<CommittedRootUserInputReceipt> queue = Channel.CreateBounded<CommittedRootUserInputReceipt>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly ICommittedRootUserInputDeliveryGate gate;
    private readonly Action<string, string, string?>? reportCode;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim active = new(MaximumActiveCallbacks);
    private readonly HashSet<(string ConversationId, string EventId)> recent = new();
    private readonly Queue<(string ConversationId, string EventId)> recentOrder = new();
    private readonly object recentGate = new();
    private readonly object callbacksGate = new();
    private readonly HashSet<Task> callbacks = new();
    private readonly TaskCompletionSource<bool> stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task pump;
    private readonly Task settlement;
    private readonly TimeSpan callbackDeadline;
    private Task? cancellation;
    private int stopped;
    private long fullCount;

    internal CommittedRootUserInputDelivery(ICommittedRootUserInputDeliveryGate gate,
        Action<string, string, string?>? reportCode = null, TimeSpan? callbackDeadline = null)
    {
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.reportCode = reportCode;
        this.callbackDeadline = callbackDeadline ?? CallbackDeadline;
        if (this.callbackDeadline <= TimeSpan.Zero || this.callbackDeadline > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(callbackDeadline));
        pump = Task.Run(PumpAsync);
        settlement = SettleAsync();
    }

    internal long FullCount => Interlocked.Read(ref fullCount);
    /// <summary>Completes only after the pump, pending receipts and actual callbacks settle.</summary>
    internal Task Settlement => settlement;

    internal CommittedRootUserInputEnqueueResult TryEnqueue(CommittedRootUserInputReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.HasCoherentRootUserEventBinding) return CommittedRootUserInputEnqueueResult.Ineligible;
        if (Volatile.Read(ref stopped) != 0) return CommittedRootUserInputEnqueueResult.Stopped;
        // The event is the key, not a caller-supplied request replay or a profile label.
        var key = (receipt.ConversationId, receipt.EventId);
        lock (recentGate)
        {
            if (recent.Contains(key)) return CommittedRootUserInputEnqueueResult.Duplicate;
            if (Volatile.Read(ref stopped) != 0) return CommittedRootUserInputEnqueueResult.Stopped;
            if (!queue.Writer.TryWrite(receipt))
            {
                Interlocked.Increment(ref fullCount);
                return CommittedRootUserInputEnqueueResult.Full;
            }
            recent.Add(key);
            recentOrder.Enqueue(key);
            while (recentOrder.Count > RecentEventCapacity)
                recent.Remove(recentOrder.Dequeue());
        }
        return CommittedRootUserInputEnqueueResult.Queued;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var receipt in queue.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                var source = Source(receipt);
                CommittedRootUserInputObserverTarget[] targets;
                try
                {
                    var eligible = gate.EligibleObservers(source)
                        ?? throw new InvalidDataException("Observer discovery returned no list.");
                    if (eligible.Count > MaximumObserverModules)
                        throw new InvalidDataException("Observer discovery exceeds its bound.");
                    targets = eligible.ToArray();
                    if (targets.Any(target => target is null || string.IsNullOrWhiteSpace(target.PackageId) ||
                            string.IsNullOrWhiteSpace(target.Generation) || target.InstanceToken is null) ||
                        targets.Select(target => target.PackageId).Distinct(StringComparer.Ordinal).Count() != targets.Length)
                        throw new InvalidDataException("Observer discovery has invalid or duplicate package bindings.");
                }
                catch (Exception)
                {
                    Report("delivery-discovery-failed", receipt.RequestId);
                    continue;
                }
                foreach (var target in targets)
                {
                    await active.WaitAsync(stopping.Token).ConfigureAwait(false);
                    // Each target acquires only its own package lease after capacity is
                    // available. One blocked callback never retains another package.
                    var work = RunAsync(receipt, source, target);
                    lock (callbacksGate) callbacks.Add(work);
                    _ = work.ContinueWith(completed =>
                    {
                        // Observe any unexpected task fault and remove only after actual
                        // completion; the settlement snapshot is taken after this pump exits.
                        _ = completed.Exception;
                        lock (callbacksGate) callbacks.Remove(completed);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception) { Report("delivery-pump-failed", ""); }
        finally { RequestStop(); }
    }

    private static CommittedRootUserInputSource Source(CommittedRootUserInputReceipt receipt) =>
        new(receipt.RequestId, receipt.ConversationId, receipt.ProfileId, receipt.DefinitionId,
            receipt.DefinitionVersion, receipt.ProjectId, receipt.EventId, receipt.EventSequence);

    private async Task RunAsync(CommittedRootUserInputReceipt receipt,
        CommittedRootUserInputSource source, CommittedRootUserInputObserverTarget target)
    {
        // Yield immediately so even a slow host-owned scope read cannot occupy the
        // single queue reader. No plugin body or callback runs on the submit path.
        await Task.Yield();
        ICommittedRootUserInputCallbackLease? lease = null;
        CancellationTokenSource? deadline = null;
        Task? callback = null;
        try
        {
            if (stopping.IsCancellationRequested) return;
            // Recheck this target immediately before callback. A null lease means it
            // became disabled, out of scope, retired or absent after discovery.
            lease = gate.TryAcquire(target, source);
            if (lease is null) return;
            var invocationDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            deadline = invocationDeadline;
            using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            // Validate and create every fallible deadline primitive before invoking
            // package code. An invalid TimeSpan can never release a started callback.
            var timeout = Task.Delay(callbackDeadline, timerStop.Token);
            var acquired = lease;
            callback = Task.Run(async () =>
            {
                invocationDeadline.Token.ThrowIfCancellationRequested();
                await acquired.InvokeAsync(new(source, receipt.UserText), invocationDeadline.Token).ConfigureAwait(false);
            });
            var elapsed = await Task.WhenAny(callback, timeout).ConfigureAwait(false);
            if (elapsed != callback)
            {
                // Cancellation requests cooperation. The lease and active capacity remain
                // held until the actual callback task completes, even if it ignores this.
                try { invocationDeadline.Cancel(); }
                catch (AggregateException) { Report("delivery-cancellation-failed", receipt.RequestId, target.PackageId); }
                Report(stopping.IsCancellationRequested ? "delivery-stopping" : "delivery-timeout", receipt.RequestId, target.PackageId);
            }
            else timerStop.Cancel();
        }
        catch (Exception) { Report("delivery-host-failed", receipt.RequestId, target.PackageId); }
        finally
        {
            // Even an unexpected host/timer fault after Task.Run cannot retire this
            // package or release active capacity before the callback actually ends.
            if (callback is not null)
            {
                try { await callback.ConfigureAwait(false); }
                catch (OperationCanceledException) when (deadline?.IsCancellationRequested == true) { }
                catch (Exception) { Report("delivery-callback-failed", receipt.RequestId, target.PackageId); }
            }
            deadline?.Dispose();
            try { lease?.Dispose(); }
            catch (Exception) { Report("delivery-lease-release-failed", receipt.RequestId, target.PackageId); }
            active.Release();
        }
    }

    private void Report(string code, string requestId, string? observerId = null)
    {
        // Diagnostic sinks are optional and must never alter the accepted workflow result.
        try { reportCode?.Invoke(code, requestId, observerId); }
        catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        RequestStop();
        // Stop is prompt. Settlement is the separate, retained proof of actual drain;
        // a caller must not infer package retirement from this return.
        return ValueTask.CompletedTask;
    }

    private void RequestStop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        queue.Writer.TryComplete();
        cancellation = CancelStopAsync();
        stopRequested.TrySetResult(true);
    }

    private async Task CancelStopAsync()
    {
        try { await stopping.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { }
    }

    private async Task SettleAsync()
    {
        await stopRequested.Task.ConfigureAwait(false);
        try { await pump.ConfigureAwait(false); }
        catch (Exception) { Report("delivery-pump-failed", ""); }
        // The sole reader has stopped. Drop all still-queued UserText references.
        while (queue.Reader.TryRead(out _)) { }
        Task[] running;
        lock (callbacksGate) running = callbacks.ToArray();
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { Report("delivery-callback-task-failed", ""); }
        if (cancellation is { } cancel)
        {
            try { await cancel.ConfigureAwait(false); }
            catch (Exception) { Report("delivery-cancellation-failed", ""); }
        }
        lock (recentGate) { recent.Clear(); recentOrder.Clear(); }
        active.Dispose();
        stopping.Dispose();
    }
}
