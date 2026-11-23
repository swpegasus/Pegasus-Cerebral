using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

// Like the existing numeric telemetry scope, but captured BEFORE accounting/usage wrappers.
// Retain only a binder and immutable metadata. Ambiguous capture fails closed, even if two
// preparations happen to have equal counts; it must never bind another preparation's stream.
internal sealed class RegisteredWorkflowDraftScope : IDisposable
{
    private static readonly AsyncLocal<RegisteredWorkflowDraftScope?> current = new();
    private readonly RegisteredWorkflowDraftScope? previous;
    private readonly object gate = new();
    private PreparedModelMetadata? metadata;
    private Func<GenerationDraftBinding, Action<GenerationDraftSample>, bool>? binder;
    private bool captured, detached;
    private int disposed;

    private RegisteredWorkflowDraftScope() { previous = current.Value; current.Value = this; }
    internal static RegisteredWorkflowDraftScope Enter() => new();
    internal static void Capture(IPreparedModelGeneration prepared)
    {
        var scope = current.Value;
        if (scope is null || prepared is not IBoundGenerationDraft source) return;
        try
        {
            var measured = prepared.Metadata;
            lock (scope.gate)
            {
                if (scope.detached || scope.disposed != 0) return;
                if (scope.captured)
                {
                    scope.metadata = null; scope.binder = null;
                    return;
                }
                scope.captured = true;
                scope.metadata = measured;
                scope.binder = source.TryBindDraft;
            }
        }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error))
        {
            lock (scope.gate)
            {
                scope.captured = true; scope.metadata = null; scope.binder = null;
            }
        }
    }
    internal RegisteredWorkflowDraftForwarder? Detach(PreparedModelMetadata validated)
    {
        lock (gate)
        {
            if (detached || disposed != 0) return null;
            detached = true;
            var forwarder = metadata == validated && binder is not null
                ? new RegisteredWorkflowDraftForwarder(binder) : null;
            metadata = null; binder = null;
            return forwarder;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (ReferenceEquals(current.Value, this)) current.Value = previous;
        lock (gate) { detached = true; metadata = null; binder = null; }
    }
}

internal sealed class RegisteredWorkflowDraftForwarder(
    Func<GenerationDraftBinding, Action<GenerationDraftSample>, bool> bind) : IDisposable
{
    private readonly object gate = new();
    private Func<GenerationDraftBinding, Action<GenerationDraftSample>, bool>? binder = bind;
    private int observing;
    private bool disposed;

    internal bool TryBind(GenerationDraftBinding binding, Action<GenerationDraftSample> observer)
    {
        lock (gate)
        {
            var target = binder; binder = null;
            if (disposed || target is null || observer is null) return false;
            try
            {
                // No source callback is permitted before GenerateAsync. This gate additionally
                // suppresses a defective binder that throws after binding.
                var accepted = target(binding, sample =>
                {
                    // Disposal drains admitted callbacks before it returns, including when
                    // provider release failed and the provider can still call this delegate.
                    lock (gate)
                    {
                        if (!disposed && observing != 0) observer(sample);
                    }
                });
                if (accepted) observing = 1;
                return accepted;
            }
            catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { return false; }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true; binder = null;
            observing = 0;
        }
    }
}
