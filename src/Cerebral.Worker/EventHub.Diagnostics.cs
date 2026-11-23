using System.Collections.Concurrent;
using System.Threading.Channels;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class EventHub
{
    public const int DiagnosticQueueCapacity = 128;
    public const int MaximumDiagnosticTextCharacters = 2048;
    private long diagnosticSequence;
    private readonly object diagnosticGate = new();
    private readonly ConcurrentDictionary<Guid, DiagnosticSubscription> diagnostics = new();
    public bool HasDiagnosticSubscribers => !diagnostics.IsEmpty;
    public IEngineDiagnosticSubscription SubscribeDiagnostics()
    {
        lock (diagnosticGate)
        {
        if (diagnostics.Count >= 16) throw new InvalidOperationException("Engine diagnostic subscriber capacity is full.");
        var id = Guid.NewGuid();
        var lease = new DiagnosticSubscription(() => diagnostics.TryRemove(id, out _));
        if (!diagnostics.TryAdd(id, lease)) throw new InvalidOperationException("Diagnostic subscription identity collision.");
        return lease;
        }
    }

    public void PublishDiagnostic(EngineDiagnosticRecord value)
    {
        if (!HasDiagnosticSubscribers) return;
        // No plugin callback or disk I/O occurs on the engine thread.
        try
        {
            if (string.IsNullOrWhiteSpace(value.ChatId) || value.ChatId.Length > 128 ||
                value.RequestId.Length > 128 || value.EffectId.Length > 128 || value.AgentId.Length > 128 ||
                value.WorkerId.Length > 128 || value.ModelId.Length > 128 || value.Phase.Length > 64 ||
                value.RunId?.Length > 128 || value.Outcome?.Length > 256) return;
            var bounded = value with
            {
                Sequence = Interlocked.Increment(ref diagnosticSequence), At = DateTimeOffset.UtcNow,
                Text = value.Text is { Length: > MaximumDiagnosticTextCharacters } text ? text[..MaximumDiagnosticTextCharacters] : value.Text,
                TextTruncated = value.TextTruncated || value.Text?.Length > MaximumDiagnosticTextCharacters
            };
            foreach (var lease in diagnostics.Values) lease.Publish(bounded);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }

    private sealed class DiagnosticSubscription(Action detach) : IEngineDiagnosticSubscription
    {
        private readonly Channel<EngineDiagnosticRecord> channel = Channel.CreateBounded<EngineDiagnosticRecord>(
            new BoundedChannelOptions(DiagnosticQueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private long dropped;
        private int disposed;
        private int reading;
        public long DroppedRecords => Interlocked.Read(ref dropped);
        internal void Publish(EngineDiagnosticRecord value)
        {
            if (Volatile.Read(ref disposed) == 0 && !channel.Writer.TryWrite(value)) Interlocked.Increment(ref dropped);
        }
        public async IAsyncEnumerable<EngineDiagnosticRecord> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref reading, 1) != 0)
                throw new InvalidOperationException("A diagnostic subscription permits one consumer.");
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            detach(); channel.Writer.TryComplete();
        }
    }
}
