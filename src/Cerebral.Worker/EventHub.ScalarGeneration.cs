using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class EventHub
{
    internal const int ScalarGenerationQueueCapacity = 128;
    private readonly ConcurrentDictionary<Guid, ScalarGenerationSubscription> scalarGenerationReaders = new();
    private readonly object scalarGenerationGate = new();
    private long scalarGenerationSequence;
    internal int ScalarGenerationSubscriberCount => scalarGenerationReaders.Count;

    internal IScalarGenerationSubscriptionV1 SubscribeScalarGeneration()
    {
        lock (scalarGenerationGate)
        {
            if (scalarGenerationReaders.Count >= 16)
                throw new InvalidOperationException("The scalar generation subscription limit was reached.");
            var id = Guid.NewGuid();
            var reader = new ScalarGenerationSubscription(() => scalarGenerationReaders.TryRemove(id, out _));
            if (!scalarGenerationReaders.TryAdd(id, reader))
                throw new InvalidOperationException("Scalar generation subscription identity collision.");
            return reader;
        }
    }

    /// <summary>Never calls a package, waits for a reader, stores content, or changes generation outcome.</summary>
    internal void PublishScalarGeneration(string installationId, string processGenerationId,
        GenerationTelemetrySample sample, ScalarGenerationTargetOriginV1 targetOrigin = ScalarGenerationTargetOriginV1.Unknown)
    {
        if (scalarGenerationReaders.IsEmpty) return;
        try
        {
            var readers = scalarGenerationReaders.Values;
            if (!ValidScalarGeneration(installationId, processGenerationId, sample, targetOrigin))
            {
                foreach (var reader in readers) reader.Drop();
                return;
            }
            var observation = new ScalarGenerationObservationV1(
                Interlocked.Increment(ref scalarGenerationSequence), sample.At,
                installationId, processGenerationId, sample.LaneId, sample.ModelId,
                sample.InstanceId, sample.AdapterId, sample.State, sample.Phase,
                sample.InputTokens, sample.OutputTokens, sample.InputTokensPerSecond,
                sample.OutputTokensPerSecond, sample.ElapsedMilliseconds, targetOrigin);
            // Projection deliberately never reads sample.Binding. That object can carry
            // chat, request, run, effect, agent, and model-worker correlation.
            foreach (var reader in readers) reader.Publish(observation);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            foreach (var reader in scalarGenerationReaders.Values) reader.Drop();
        }
    }

    private static bool ValidScalarGeneration(string installationId, string processGenerationId,
        GenerationTelemetrySample value, ScalarGenerationTargetOriginV1 targetOrigin)
    {
        static bool Id(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 128 &&
            !text.Any(char.IsControl);
        static bool Rate(double? rate) => rate is null || double.IsFinite(rate.Value) && rate is >= 0 and <= 100_000_000;
        return Enum.IsDefined(targetOrigin) && value.Schema == "cerebral.generation-metrics/1" &&
            Id(installationId) && Guid.TryParseExact(processGenerationId, "N", out _) &&
            Guid.TryParseExact(value.LaneId, "N", out _) && Id(value.ModelId) &&
            Id(value.InstanceId) && Id(value.AdapterId) &&
            value.State is "active" or "Stop" or "Length" or "Cancelled" or "Unknown" &&
            value.Phase is "unknown" or "prefill" or "output" &&
            value.At != default && value.InputTokens is null or >= 0 &&
            value.OutputTokens is null or >= 0 && Rate(value.InputTokensPerSecond) &&
            Rate(value.OutputTokensPerSecond) && value.ElapsedMilliseconds >= 0;
    }

    private sealed class ScalarGenerationSubscription(Action detach) : IScalarGenerationSubscriptionV1
    {
        private readonly Channel<ScalarGenerationObservationV1> channel =
            Channel.CreateBounded<ScalarGenerationObservationV1>(new BoundedChannelOptions(ScalarGenerationQueueCapacity)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private long dropped;
        private int disposed;
        private int reading;
        public long DroppedRecords => Interlocked.Read(ref dropped);
        internal void Drop()
        {
            if (Volatile.Read(ref disposed) == 0) Interlocked.Increment(ref dropped);
        }
        internal void Publish(ScalarGenerationObservationV1 value)
        {
            if (Volatile.Read(ref disposed) == 0 && !channel.Writer.TryWrite(value)) Drop();
        }
        public async IAsyncEnumerable<ScalarGenerationObservationV1> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref reading, 1) != 0)
                throw new InvalidOperationException("A scalar generation subscription permits one consumer.");
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            detach();
            channel.Writer.TryComplete();
        }
    }
}
