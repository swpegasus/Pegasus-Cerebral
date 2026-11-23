using System.Runtime.CompilerServices;
using Cerebral.Core;

namespace Cerebral.MetricsPlugin.Service;

/// <summary>Consumes only the versioned scalar facet. A source owns one lazy-opened
/// subscription; it cannot dispatch inference, read correlation or inspect a provider.</summary>
internal sealed class ScalarGenerationMetricsSource : IMetricsSource
{
    private readonly IScalarGenerationSubscriptionV1 subscription;
    private readonly Func<DateTimeOffset> clock;
    private long rejected;
    private int reading, disposed;
    internal ScalarGenerationMetricsSource(IServiceScalarGenerationHostV1 host, Func<DateTimeOffset>? clock = null)
    { subscription = host.OpenScalarGenerationObservations(); this.clock = clock ?? (() => DateTimeOffset.UtcNow); }
    public string State => Volatile.Read(ref disposed) == 0 ? "available" : "unavailable";
    public string Reason => Volatile.Read(ref disposed) == 0 ? "instrumented-generation-scalars-only" : "metrics-source-detached";
    public long DroppedRecords
    {
        get
        {
            var producer = subscription.DroppedRecords;
            var local = Interlocked.Read(ref rejected);
            if (producer < 0) return long.MaxValue; // Invalid source coverage is a gap, never a zero.
            return producer > long.MaxValue - local ? long.MaxValue : producer + local;
        }
    }
    public async IAsyncEnumerable<MetricObservation> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0)
            throw new InvalidOperationException("A scalar metrics source permits one consumer.");
        await foreach (var sample in subscription.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref disposed) != 0) yield break;
            if (TryMap(sample, clock(), out var row)) yield return row!;
            else Interlocked.Increment(ref rejected);
        }
    }
    internal static bool TryMap(ScalarGenerationObservationV1 sample, DateTimeOffset now, out MetricObservation? row)
    {
        row = null;
        if (sample is null || sample.Sequence <= 0 || sample.ElapsedMilliseconds < 0 ||
            !Id(sample.ProducerInstallationId) || !Id(sample.ProducerProcessGenerationId) ||
            !Id(sample.LaneId) || !Id(sample.ModelId) || !Id(sample.ModelInstanceId) || !Id(sample.ModelAdapterId) ||
            sample.State is not ("active" or "Stop" or "Length" or "Cancelled" or "Unknown") ||
            sample.Phase is not ("unknown" or "prefill" or "output") ||
            !Enum.IsDefined(sample.TargetOrigin)) return false;
        var state = sample.State switch
        {
            "active" => MetricState.Active,
            "Stop" or "Length" => MetricState.Completed,
            "Cancelled" => MetricState.Cancelled,
            _ => MetricState.Unknown
        };
        var origin = sample.TargetOrigin switch
        {
            ScalarGenerationTargetOriginV1.Local => MetricOrigin.Local,
            ScalarGenerationTargetOriginV1.Remote => MetricOrigin.Remote,
            _ => MetricOrigin.Unknown
        };
        var mapped = new MetricObservation
        {
            SchemaVersion = 2, InstallationId = sample.ProducerInstallationId,
            SourceGeneration = sample.ProducerProcessGenerationId, EntityId = sample.LaneId,
            Revision = sample.Sequence, ObservedAt = sample.ObservedAt,
            Kind = MetricKind.Generation, State = state, Origin = origin,
            Basis = MetricBasis.ProviderTelemetry, Partial = true,
            ModelId = sample.ModelId, OutputTokenScope = MetricTokenScope.ProviderUnspecified,
            ProviderObservedInputTokens = sample.ProviderObservedInputTokens,
            ProviderObservedOutputTokens = sample.ProviderObservedOutputTokens,
            InputTokensPerSecond = sample.ProviderInputTokensPerSecond,
            OutputTokensPerSecond = sample.ProviderOutputTokensPerSecond,
            GenerationHostMilliseconds = sample.ElapsedMilliseconds
        };
        // No substring, hash, normalization or inference of target identity. Confirmed
        // usage, processed/cache/context/budget counters and correlation remain absent.
        if (!MetricsValidation.Observation(mapped, now)) return false;
        row = mapped; return true;
    }
    private static bool Id(string? value) => value?.Length <= 128 && MetricsValidation.Identifier(value, false);
    public void Dispose()
    { if (Interlocked.Exchange(ref disposed, 1) == 0) subscription.Dispose(); }
}
