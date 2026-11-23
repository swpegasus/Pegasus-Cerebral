namespace Cerebral.Desktop.Plugins;

/// <summary>Balances host-managed callback scopes. Retirement closes admission immediately,
/// but cleanup waits for the existing callbacks; it never aborts native code or promises ALC collection.</summary>
internal sealed class DesktopPluginInvocationLifetime
{
    private readonly object gate = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool retiring;
    private int active;

    internal int ActiveCount { get { lock (gate) return active; } }

    internal IDisposable Enter(IDisposable observation)
    {
        try
        {
            lock (gate)
            {
                if (retiring) throw new InvalidOperationException("The native plugin instance is stopping; no new callback was started.");
                active = checked(active + 1);
            }
            return new Invocation(this, observation);
        }
        catch { observation.Dispose(); throw; }
    }

    internal void RequireActive()
    {
        lock (gate)
            if (retiring) throw new InvalidOperationException("The native plugin instance stopped while its callback was running; discard the stale result.");
    }

    internal Task Retire()
    {
        lock (gate)
        {
            retiring = true;
            if (active == 0) drained.TrySetResult();
            return drained.Task;
        }
    }

    private void Leave()
    {
        lock (gate)
        {
            if (active <= 0) throw new InvalidOperationException("Native callback scopes were released out of balance.");
            active--;
            if (retiring && active == 0) drained.TrySetResult();
        }
    }

    private sealed class Invocation(DesktopPluginInvocationLifetime owner, IDisposable observation) : IDisposable
    {
        private DesktopPluginInvocationLifetime? current = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref current, null);
            if (value is null) return;
            try { observation.Dispose(); }
            finally { value.Leave(); }
        }
    }
}
