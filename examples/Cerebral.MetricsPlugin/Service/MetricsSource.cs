namespace Cerebral.MetricsPlugin.Service;

/// <summary>Private adapter boundary, not a new SDK interface. A future adapter consumes only
/// Core's reviewed optional facet. No raw engine-debug stream, owner dispatcher or DB handle.</summary>
internal interface IMetricsSource : IDisposable
{
    string State { get; }
    string Reason { get; }
    long DroppedRecords { get; }
    IAsyncEnumerable<MetricObservation> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class PendingMetricsSource : IMetricsSource
{
    public string State => "unavailable";
    public string Reason => "reviewed-metrics-host-facet-not-connected";
    public long DroppedRecords => 0;
    public async IAsyncEnumerable<MetricObservation> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
    public void Dispose() { }
}
