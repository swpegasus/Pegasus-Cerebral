namespace Cerebral.Orchestrator;

internal enum InferenceOwnershipReadOutcome { Sample, Unavailable, Unsupported }

internal sealed record InferenceOwnershipPollTicket(long Epoch, object Connection, string Owner,
    long RequestNumber, long VisibilityVersion, long InvalidationVersion);

/// <summary>Dispatcher-owned scheduling policy. Caller supplies a monotonic clock and actual visibility.
/// A reconnect never opens a second request while the old bounded request is still completing.</summary>
internal sealed class InferenceOwnershipPollState : IDisposable
{
    private object? connection;
    private string? owner;
    private long epoch, requestNumber, visibilityVersion, invalidationVersion, notifiedRevision = -1;
    private TimeSpan? lastStart;
    private InferenceOwnershipPollTicket? inFlight;
    private readonly HashSet<string> retiredGenerations = new(StringComparer.Ordinal);
    private bool eligible, disposed;
    internal bool Unsupported { get; private set; }
    internal bool Dirty { get; private set; } = true;
    internal string? ObserverGenerationId { get; private set; }
    internal long Revision { get; private set; } = -1;
    internal bool IsInFlight => inFlight is not null;
    internal bool Eligible => eligible && !disposed;

    internal void Bind(object nextConnection, string installationId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(nextConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        connection = nextConnection; owner = installationId; epoch++;
        Unsupported = false; Dirty = true; ObserverGenerationId = null; Revision = -1;
        notifiedRevision = -1; invalidationVersion++; retiredGenerations.Clear();
        // Keep lastStart and inFlight: connection changes cannot bypass global spacing or overlap.
    }

    internal void Unbind()
    {
        if (disposed) return;
        connection = null; owner = null; epoch++; Unsupported = false; Dirty = true;
        ObserverGenerationId = null; Revision = -1; notifiedRevision = -1;
        invalidationVersion++; retiredGenerations.Clear();
    }

    internal void SetVisibility(bool windowVisible, bool minimized, bool statusVisible)
    {
        var next = !disposed && windowVisible && !minimized && statusVisible;
        if (next == eligible) return;
        eligible = next; visibilityVersion++; Dirty = true; invalidationVersion++;
    }

    internal bool TryBegin(object currentConnection, string installationId, TimeSpan monotonicNow,
        out InferenceOwnershipPollTicket? ticket)
    {
        ticket = null;
        if (!Eligible || Unsupported || inFlight is not null ||
            !ReferenceEquals(connection, currentConnection) || owner != installationId ||
            monotonicNow < TimeSpan.Zero ||
            lastStart is { } previous && monotonicNow - previous < TimeSpan.FromSeconds(1))
            return false;
        lastStart = monotonicNow;
        ticket = inFlight = new(epoch, currentConnection, installationId, ++requestNumber,
            visibilityVersion, invalidationVersion);
        // Dirty events are hints; the same bounded cadence refreshes when an event is lost.
        return true;
    }

    internal bool Complete(InferenceOwnershipPollTicket ticket, InferenceOwnershipReadOutcome outcome,
        string? generation = null, long revision = 0)
    {
        if (!ReferenceEquals(inFlight, ticket)) return false;
        inFlight = null;
        if (disposed || !Eligible || ticket.Epoch != epoch || ticket.VisibilityVersion != visibilityVersion ||
            !ReferenceEquals(ticket.Connection, connection) || ticket.Owner != owner)
            return false;
        if (outcome == InferenceOwnershipReadOutcome.Unsupported)
        {
            Unsupported = true; Dirty = false;
            return true;
        }
        if (outcome == InferenceOwnershipReadOutcome.Unavailable) { Dirty = true; return true; }
        if (!Guid.TryParseExact(generation, "N", out var id) || id == Guid.Empty || revision < 0)
            return false;
        generation = id.ToString("N");
        if (ObserverGenerationId == generation)
        {
            if (revision < Revision) return false;
        }
        else
        {
            // A full read may establish a replacement generation; events never can.
            // Refuse a retired generation and bound history until the connection is rebound.
            if (retiredGenerations.Contains(generation!) || retiredGenerations.Count >= 8) return false;
            if (ObserverGenerationId is { } old) retiredGenerations.Add(old);
            notifiedRevision = -1;
        }
        ObserverGenerationId = generation; Revision = revision;
        Dirty = ticket.InvalidationVersion != invalidationVersion;
        return true;
    }

    internal bool Notify(string generation, long revision)
    {
        if (!Guid.TryParseExact(generation, "N", out var id) || id == Guid.Empty) return false;
        generation = id.ToString("N");
        if (disposed || Unsupported || ObserverGenerationId != generation ||
            revision <= Math.Max(Revision, notifiedRevision)) return false;
        notifiedRevision = revision; Dirty = true; invalidationVersion++;
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; eligible = false; connection = null; owner = null;
        epoch++; visibilityVersion++; invalidationVersion++; Dirty = true;
    }
}
