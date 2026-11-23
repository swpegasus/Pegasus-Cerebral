using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.MetricsPlugin.Service;

/// <summary>Optional Worker-resident collector. Nothing here can dispatch a model or change
/// resource admission. The default adapter reports the real missing-host-facet state.</summary>
public sealed class MetricsServicePlugin : IServicePlugin, IServiceCommandExtension
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Func<IMetricsSource>? sourceFactory;
    private readonly Func<string, MetricsHistory> historyFactory;
    private MetricsHistory? history;
    private Capture? capture;
    private Task maintenance = Task.CompletedTask;
    private int disposalStarted;
    private volatile bool disposed;
    // Live intent is independent of stored preferences: a failed opt-out save must
    // still suppress replacement after the old source finally retires.
    private bool captureRequested;
    private string sourceState = "unavailable", sourceReason = "reviewed-metrics-host-facet-not-connected";
    private bool captureActive;
    private long cleanupFaults;
    private sealed class Capture(IMetricsSource source, CancellationTokenSource stop)
    {
        internal IMetricsSource Source { get; } = source;
        internal CancellationTokenSource Stop { get; } = stop;
        internal Task Pump { get; set; } = Task.CompletedTask;
        internal bool Retiring { get; set; }
        internal bool ReadFault { get; set; }
        internal bool DisposeFault { get; set; }
        internal bool CoverageFault { get; set; }
    }

    public MetricsServicePlugin() : this(null, root => new(root, journalVersion: 2)) { }
    internal MetricsServicePlugin(Func<IMetricsSource>? sourceFactory, Func<string, MetricsHistory>? historyFactory = null)
    { this.sourceFactory = sourceFactory; this.historyFactory = historyFactory ?? (root => new(root)); }
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [this];
    public IReadOnlyList<string> Operations => ["metrics-history.status", "metrics-history.query",
        "metrics-history.settings.get", "metrics-history.settings.set"];

    public void Initialize(IServicePluginHost host)
    {
        lifecycle.Wait();
        try
        {
            if (disposed || history is not null || host.PluginId != MetricsContract.PluginId)
                throw new InvalidOperationException("Metrics plugin identity/lifecycle mismatch.");
            // Discovery does not open a subscription. Capture intent alone owns the
            // lease; disposing/unloading the package retires it through the same barrier.
            sourceFactory ??= host is IServiceScalarGenerationHostV1 scalarHost
                ? () => new ScalarGenerationMetricsSource(scalarHost) : () => new PendingMetricsSource();
            history = historyFactory(host.DataDirectory);
            captureRequested = history.Settings.Values.CaptureEnabled;
            // The service owns collection independently of desktop lifetime. Only the
            // reviewed source may supply real observations.
            if (captureRequested) StartCapture();
            maintenance = MaintainAsync(lifetime.Token);
        }
        finally { lifecycle.Release(); }
    }

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = history ?? throw new InvalidOperationException("Metrics plugin is not initialized.");
            if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Metrics arguments require an object.");
            // Surface queue/rejection loss even when no valid row follows it. A
            // source read failure remains primary; this never restarts its reader.
            if (capture is { } currentCapture)
            {
                try { current.SourceGap(currentCapture.Source.DroppedRecords); }
                catch (Exception e) when (Ordinary(e)) { currentCapture.ReadFault = true; current.SourceGap(0, true); }
            }
            object result;
            switch (operation)
            {
                case "metrics-history.status":
                    Empty(arguments); result = new MetricsStatus("cerebral.metrics-history.status/2", current.Settings,
                        current.Coverage(), sourceState, sourceReason, captureActive, Interlocked.Read(ref cleanupFaults)); break;
                case "metrics-history.settings.get": Empty(arguments); result = current.Settings; break;
                case "metrics-history.query":
                    var query = arguments.Deserialize(MetricsContract.TypeInfo<MetricsQuery>()) ?? throw new ArgumentException("Missing metrics query.");
                    result = current.Query(query, cancellationToken); break;
                case "metrics-history.settings.set":
                    var change = arguments.Deserialize(MetricsContract.TypeInfo<MetricsSettingsChange>()) ?? throw new ArgumentException("Missing settings change.");
                    MetricsValidation.Preferences(change.Values);
                    if (!change.Values.CaptureEnabled)
                    {
                        captureRequested = false; // Applies even when the subsequent save is rejected or uncertain.
                        await StopCaptureAsync().ConfigureAwait(false);
                    }
                    result = current.SaveSettings(change, cancellationToken);
                    if (change.Values.CaptureEnabled) { captureRequested = true; StartCapture(); }
                    break;
                default: throw new ArgumentException("Unknown metrics history operation.");
            }
            // Framework-owned JSON crosses the collectible service boundary, not plugin CLR DTOs.
            return result switch
            {
                MetricsStatus value => JsonSerializer.SerializeToElement(value, MetricsContract.TypeInfo<MetricsStatus>()),
                MetricsSettings value => JsonSerializer.SerializeToElement(value, MetricsContract.TypeInfo<MetricsSettings>()),
                MetricsQueryResult value => JsonSerializer.SerializeToElement(value, MetricsContract.TypeInfo<MetricsQueryResult>()),
                _ => throw new InvalidOperationException("Unsupported metrics command response type.")
            };
        }
        finally { lifecycle.Release(); }
    }

    // Session admission/retirement state is serialized by lifecycle. The pump owns
    // its captured session and never clears a successor's activity or source fields.
    private void StartCapture()
    {
        if (disposed || !captureRequested || history is null) return;
        if (capture is { } previous)
        {
            if (!previous.Pump.IsCompleted)
            {
                if (previous.Retiring) { sourceState = "retiring"; sourceReason = "metrics-source-detach-pending"; }
                return;
            }
            FinishCapture(previous);
        }
        IMetricsSource? opened = null;
        try
        {
            opened = sourceFactory!(); sourceState = opened.State; sourceReason = opened.Reason;
            if (sourceState != "available") { DisposeSource(opened, null); return; }
            var session = new Capture(opened, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
            capture = session; captureActive = true; history.SourceStarted();
            session.Pump = Task.Run(() => PumpAsync(session));
            // Fulfill a pending re-enable only after actual retirement. An owner
            // need not repeat its already confirmed settings command.
            _ = session.Pump.ContinueWith(_ => ReconcileRetirementAsync(session), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }
        catch (Exception e) when (Ordinary(e))
        {
            if (opened is not null) DisposeSource(opened, null);
            sourceState = "unavailable"; sourceReason = "metrics-source-open-failed"; captureActive = false;
        }
    }
    private async Task PumpAsync(Capture session)
    {
        var token = session.Stop.Token;
        try
        {
            await foreach (var row in session.Source.ReadAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                history!.SourceObserved(); history.SourceGap(session.Source.DroppedRecords);
                history.Append(row, token);
            }
            if (!token.IsCancellationRequested) ObserveGap(session);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (Ordinary(e)) { session.ReadFault = true; ObserveGap(session); }
        finally
        {
            try { DisposeSource(session.Source, session); }
            finally { SnapshotSourceCoverage(session); }
        }
    }
    private void ObserveGap(Capture session)
    {
        try { history?.SourceGap(session.Source.DroppedRecords, true); }
        catch (Exception e) when (Ordinary(e)) { session.ReadFault = true; }
    }
    private void SnapshotSourceCoverage(Capture session)
    {
        try { history?.SourceGap(session.Source.DroppedRecords); }
        catch (Exception error) when (Ordinary(error))
        {
            session.CoverageFault = true;
            try { history?.SourceGap(0, true); }
            catch (Exception cleanup) when (Ordinary(cleanup)) { Interlocked.Increment(ref cleanupFaults); }
        }
    }
    private void DisposeSource(IMetricsSource source, Capture? session)
    {
        try { source.Dispose(); }
        catch (Exception e) when (Ordinary(e))
        { if (session is not null) session.DisposeFault = true; Interlocked.Increment(ref cleanupFaults); }
    }
    private void Cancel(CancellationTokenSource stop)
    {
        try { stop.Cancel(); }
        catch (Exception e) when (Ordinary(e)) { Interlocked.Increment(ref cleanupFaults); }
    }
    private void RequestStop(Capture session)
    {
        session.Retiring = true;
        SnapshotSourceCoverage(session);
        try { Cancel(session.Stop); }
        finally { DisposeSource(session.Source, session); }
    }
    private async Task StopCaptureAsync()
    {
        if (capture is not { } session) return;
        try
        {
            RequestStop(session);
            try { await session.Pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { sourceState = "retiring"; sourceReason = "metrics-source-detach-pending"; }
            catch (Exception e) when (Ordinary(e)) { session.ReadFault = true; }
        }
        finally
        {
            captureActive = false;
            if (session.Pump.IsCompleted) FinishCapture(session);
        }
    }
    private void FinishCapture(Capture session)
    {
        if (!ReferenceEquals(capture, session) || !session.Pump.IsCompleted) return;
        try { session.Stop.Dispose(); }
        catch (Exception e) when (Ordinary(e)) { Interlocked.Increment(ref cleanupFaults); }
        finally
        {
            capture = null; captureActive = false; sourceState = "unavailable";
            sourceReason = session.ReadFault ? "metrics-source-read-failed" : session.DisposeFault
                ? "metrics-source-dispose-failed" : session.CoverageFault ? "metrics-source-coverage-unavailable"
                : session.Retiring ? "metrics-capture-stopped" : "metrics-source-ended";
        }
    }
    private async Task ReconcileRetirementAsync(Capture session)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(capture, session)) return;
            var replace = session.Retiring && captureRequested && !disposed;
            FinishCapture(session);
            if (replace) StartCapture(); // Natural EOF/failure never starts an automatic retry loop.
        }
        finally { lifecycle.Release(); }
    }
    private async Task MaintainAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) history?.Maintain(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (Ordinary(e))
        { try { history?.SourceGap(0, true); } catch (Exception failure) when (Ordinary(failure)) { } }
    }
    private static void Empty(JsonElement arguments)
    { if (arguments.EnumerateObject().Any()) throw new ArgumentException("This metrics operation takes no arguments."); }
    private static bool Ordinary(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0) return;
        disposed = true;
        try { Cancel(lifetime); }
        finally { _ = DisposeOwnedAsync(); }
    }
    private async Task DisposeOwnedAsync()
    {
        Capture? retiring;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            captureRequested = false; captureActive = false; retiring = capture;
            if (retiring is not null) RequestStop(retiring);
        }
        finally { lifecycle.Release(); }
        try
        {
            // A throwing detach cannot skip registering this actual-task barrier.
            // An unretired reader/writer retains the store lock until it really exits.
            await Task.WhenAll(retiring?.Pump ?? Task.CompletedTask, maintenance).ConfigureAwait(false);
        }
        catch (Exception e) when (Ordinary(e)) { Interlocked.Increment(ref cleanupFaults); }
        finally
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try { if (retiring is not null) FinishCapture(retiring); }
            finally { lifecycle.Release(); }
            try { history?.Dispose(); }
            catch (Exception e) when (Ordinary(e)) { Interlocked.Increment(ref cleanupFaults); }
            finally { lifetime.Dispose(); }
        }
    }
}
