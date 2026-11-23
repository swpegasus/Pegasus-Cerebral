using System.Diagnostics;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    private readonly object generationTelemetryGate = new();
    private readonly Dictionary<string, GenerationTelemetryLane> generationTelemetry = new(StringComparer.Ordinal);
    private readonly Queue<GenerationTelemetrySample> recentGenerationTelemetry = new();
    private const int MaximumGenerationTelemetryLanes = 128;

    internal GenerationTelemetrySnapshot GenerationTelemetrySnapshot()
    {
        lock (generationTelemetryGate)
        {
            var now = DateTimeOffset.UtcNow;
            while (recentGenerationTelemetry.TryPeek(out var item) && now - item.At > TimeSpan.FromMinutes(5))
                recentGenerationTelemetry.Dequeue();
            var active = generationTelemetry.Values.Take(MaximumGenerationTelemetryLanes)
                .Select(lane => lane.Snapshot(now)).ToArray();
            return new("cerebral.generation-metrics/1", now, active, recentGenerationTelemetry.ToArray(),
                generationTelemetry.Count > active.Length);
        }
    }

    private GenerationTelemetryLane BeginGenerationTelemetry(PreparedModelMetadata metadata,
        GenerationTelemetryBinding? binding, Action<GenerationTelemetrySample>? observer)
    {
        var lane = new GenerationTelemetryLane(this, metadata, binding, observer);
        lock (generationTelemetryGate) generationTelemetry.Add(lane.Id, lane);
        lane.Publish();
        return lane;
    }

    private sealed class GenerationTelemetryLane(ModelRuntime owner, PreparedModelMetadata metadata,
        GenerationTelemetryBinding? binding, Action<GenerationTelemetrySample>? observer) : IDisposable
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        // Preserve producer order when progress and terminal callbacks race; readers
        // use the feed sequence as a per-lane revision.
        private readonly object publicationGate = new();
        private ProviderGenerationMeasurement? previous;
        private ProviderGenerationMeasurement? lastObserved;
        private GenerationTelemetrySample? latest;
        private long lastPublication = -1;
        private string state = "active";
        private int disposed;

        internal GenerationTelemetrySample Snapshot(DateTimeOffset now)
        {
            var value = latest ?? new("cerebral.generation-metrics/1", Id, metadata.InstanceId, metadata.ModelId,
                metadata.AdapterId, binding, state, "unknown", null, null, null, null, elapsed.ElapsedMilliseconds, now);
            // A delayed/missing frame cannot keep reporting an old sample as a current live rate.
            return value.State == "active" && now - value.At > TimeSpan.FromSeconds(2)
                ? value with { InputTokensPerSecond = null, OutputTokensPerSecond = null } : value;
        }
        internal void Observe(ProviderGenerationMeasurement value)
        {
            lock (owner.generationTelemetryGate)
            {
                if (disposed != 0 || state != "active" || value.InputTokens < lastObserved?.InputTokens || value.OutputTokens < lastObserved?.OutputTokens ||
                    value.InputMilliseconds < lastObserved?.InputMilliseconds || value.OutputMilliseconds < lastObserved?.OutputMilliseconds) return;
                lastObserved = value;
                static double? Rate(int? count, double? time, int? oldCount, double? oldTime)
                {
                    if (count is not { } tokens || time is not > 0) return null;
                    var deltaCount = oldCount is { } before ? tokens - before : tokens;
                    var deltaTime = oldTime is { } beforeMs ? time.Value - beforeMs : time.Value;
                    if (deltaCount < 0 || deltaTime <= 0) return null;
                    var rate = deltaCount * 1000d / deltaTime;
                    return double.IsFinite(rate) && rate is >= 0 and <= 100_000_000 ? rate : null;
                }
                var inputRate = value.Phase == "prefill" ? Rate(value.InputTokens, value.InputMilliseconds,
                    previous?.InputTokens, previous?.InputMilliseconds) : null;
                var outputRate = value.Phase == "output" ? Rate(value.OutputTokens, value.OutputMilliseconds,
                    previous?.OutputTokens, previous?.OutputMilliseconds) : null;
                latest = new("cerebral.generation-metrics/1", Id, metadata.InstanceId, metadata.ModelId,
                    metadata.AdapterId, binding, state, value.Phase, value.InputTokens, value.OutputTokens,
                    inputRate, outputRate, elapsed.ElapsedMilliseconds, DateTimeOffset.UtcNow);
                // Compare over the published interval, rather than reporting a one-token spike.
                if (lastPublication >= 0 && elapsed.ElapsedMilliseconds - lastPublication < 250) return;
                previous = value;
            }
            Publish();
        }
        internal void Finish(ModelGenerationResult? result)
        {
            lock (owner.generationTelemetryGate)
            {
                state = result?.Finish.ToString() ?? "Unknown";
                latest = Snapshot(DateTimeOffset.UtcNow) with { State = state, At = DateTimeOffset.UtcNow,
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds };
                owner.generationTelemetry.Remove(Id);
                owner.recentGenerationTelemetry.Enqueue(latest);
                while (owner.recentGenerationTelemetry.Count > MaximumGenerationTelemetryLanes)
                    owner.recentGenerationTelemetry.Dequeue();
            }
            Publish();
        }
        internal void Publish()
        {
            lock (publicationGate)
            {
                GenerationTelemetrySample value;
                lock (owner.generationTelemetryGate)
                { value = Snapshot(DateTimeOffset.UtcNow); lastPublication = elapsed.ElapsedMilliseconds; }
                try { observer?.Invoke(value); }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.generationTelemetryGate) owner.generationTelemetry.Remove(Id);
        }
    }
}

internal sealed record GenerationTelemetrySnapshot(string Schema, DateTimeOffset At,
    IReadOnlyList<GenerationTelemetrySample> Active, IReadOnlyList<GenerationTelemetrySample> Recent, bool Truncated);
