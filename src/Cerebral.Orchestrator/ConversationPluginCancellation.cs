namespace Cerebral.Orchestrator;

/// <summary>Cancellation exposed to native plugin callbacks must not throw into the owner or timer.</summary>
internal sealed class ConversationPluginCancellation : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource source = new();
    private readonly List<CancellationTokenRegistration> registrations = [];
    private readonly Timer? timer;
    private bool disposed;
    private int disposeRequested;
    private Exception? cancellationFailure;
    internal CancellationToken Token => source.Token;
    internal bool IsCancellationRequested => source.IsCancellationRequested;
    internal Exception? CancellationFailure => Volatile.Read(ref cancellationFailure);

    internal ConversationPluginCancellation(TimeSpan? timeout, params CancellationToken[] parents)
    {
        foreach (var parent in parents) registrations.Add(parent.Register(Cancel));
        if (timeout is { } duration)
            timer = new Timer(_ => Cancel(), null, duration, Timeout.InfiniteTimeSpan);
    }

    internal void Cancel()
    {
        lock (gate)
        {
            if (disposed) return;
            CancelCore();
        }
    }

    private void CancelCore()
    {
        try { source.Cancel(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { Volatile.Write(ref cancellationFailure, error); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeRequested, 1) != 0) return;
        // Remove parent callbacks outside the source lock: a concurrent callback may need that lock.
        foreach (var registration in registrations) registration.Dispose();
        timer?.Dispose();
        lock (gate)
        {
            CancelCore(); disposed = true; source.Dispose();
        }
    }
}
