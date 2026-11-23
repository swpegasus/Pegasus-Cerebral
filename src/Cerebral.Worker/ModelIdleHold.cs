namespace Cerebral.Worker;

/// <summary>Monotonic inactivity, distinct from model inference duration and wall-clock display time.</summary>
internal sealed class ModelIdleHold(TimeProvider clock)
{
    private readonly object gate = new();
    private long lastActivity = clock.GetTimestamp();
    private DateTimeOffset lastUsed = clock.GetUtcNow();
    private bool workWasActive;
    private long revision;

    public DateTimeOffset LastUsed { get { lock (gate) return lastUsed; } }
    public void Touch()
    {
        lock (gate) { lastActivity = clock.GetTimestamp(); lastUsed = clock.GetUtcNow(); revision++; }
    }
    public (long Revision, long Milliseconds) Observe(bool active)
    {
        lock (gate)
        {
            // Begin a complete grace period after a tool/helper/human wait ends, even when the
            // last inference happened much earlier. Ten-second scan granularity can delay release.
            if (active || workWasActive) Touch();
            workWasActive = active;
            return (revision, Math.Max(0, (long)clock.GetElapsedTime(lastActivity).TotalMilliseconds));
        }
    }
}
