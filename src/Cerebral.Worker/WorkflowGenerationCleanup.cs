using System.Runtime.ExceptionServices;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

// One effect owns this tracker. Cleanup uncertainty changes the effect disposition, never the
// provider's terminal facts, accepted input, balance transaction, or authority to prepare again.
internal sealed class WorkflowGenerationCleanup
{
    internal const string CleanupDiagnostic = "Model preparation cleanup could not be confirmed. Inspect this effect before continuing; it was not replayed.";
    internal const string GenerationDiagnostic = "The model adapter did not return a confirmed terminal receipt.";
    private Exception? primaryException, cleanupException;
    private IRegisteredNativeReadinessFailure? readinessFailure, knownReadinessFailure, unconfirmedReadinessFailure;
    private int generationInvoked;

    internal Exception? PrimaryException => Volatile.Read(ref primaryException);
    internal Exception? CleanupException => Volatile.Read(ref cleanupException);
    internal IRegisteredNativeReadinessFailure? ReadinessFailure => Volatile.Read(ref unconfirmedReadinessFailure) ?? Volatile.Read(ref readinessFailure);
    internal bool GenerationInvoked => Volatile.Read(ref generationInvoked) != 0;
    internal bool CleanupUnconfirmed => CleanupException is not null;
    internal void GenerationStarting() => Interlocked.Exchange(ref generationInvoked, 1);

    internal void RecordPrimary(Exception error)
    {
        ObserveCleanup(error);
        Interlocked.CompareExchange(ref primaryException, error, null);
    }

    internal bool ObserveCleanup(Exception error) => AnalyzeCleanup(error, disposing: false);

    // Walk only expected aggregation/ownership wrappers. This bounds diagnostic work, preserves
    // the first actual primary object, and never invents a registry/preparation observation.
    private bool AnalyzeCleanup(Exception error, bool disposing)
    {
        var pending = new Stack<(Exception Error, int Depth, bool CleanupBranch)>();
        var primarySeen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var cleanupSeen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var visited = 0;
        pending.Push((error, 0, disposing));
        var truncated = false;
        while (pending.Count != 0 && visited < 32)
        {
            var (current, depth, cleanupBranch) = pending.Pop();
            if (!(cleanupBranch ? cleanupSeen : primarySeen).Add(current)) continue;
            visited++;
            if (depth > 8) { truncated = true; continue; }
            if (current is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count > 16) truncated = true;
                foreach (var inner in aggregate.InnerExceptions.Take(16).Reverse())
                    pending.Push((inner, depth + 1, cleanupBranch));
                continue;
            }
            if (current is PreparationCleanupException prepared)
            {
                pending.Push((prepared.InnerException!, depth + 1, true));
                continue;
            }
            if (current is WorkflowReadinessCleanupException workflow)
            {
                RecordUnconfirmed(workflow);
                if (workflow.CleanupFailure is { } cleanup)
                    pending.Push((cleanup, depth + 1, true));
                if (workflow.PrimaryFailure is { } primary) pending.Push((primary, depth + 1, false));
                continue;
            }
            if (current is IRegisteredNativeReadinessFailure readiness)
            {
                Interlocked.CompareExchange(ref readinessFailure, readiness, null);
                try
                {
                    // KnownResident covers this exact readiness branch only. It cannot whitelist
                    // an unclassified cleanup sibling elsewhere in an aggregate/preparation.
                    if (readiness.Observation.AllowsFreshAcceptedAttempt)
                        Interlocked.CompareExchange(ref knownReadinessFailure, readiness, null);
                    else
                    {
                        Interlocked.CompareExchange(ref unconfirmedReadinessFailure, readiness, null);
                        RecordUnconfirmed(current);
                    }
                }
                catch (Exception projectionError)
                {
                    Interlocked.CompareExchange(ref unconfirmedReadinessFailure, readiness, null);
                    RecordUnconfirmed(new WorkflowReadinessCleanupException(current, projectionError));
                }
                // A failing observation getter cannot erase immutable original/secondary
                // failure provenance which is still available on this exact typed failure.
                Exception? original = null, secondary = null;
                try { original = readiness.OriginalException; secondary = readiness.CleanupException; }
                catch (Exception projectionError)
                { RecordUnconfirmed(new WorkflowReadinessCleanupException(current, projectionError)); }
                if (secondary is not null) pending.Push((secondary, depth + 1, true));
                if (original is not null) pending.Push((original, depth + 1, false));
                continue;
            }
            if (current is RegisteredWorkflowCleanupException registered)
            {
                if (registered.GenerationInvoked) GenerationStarting();
                RecordUnconfirmed(current);
                if (registered.InnerException is { } inner) pending.Push((inner, depth + 1, false));
                continue;
            }
            if (cleanupBranch) RecordUnconfirmed(current);
            else Interlocked.CompareExchange(ref primaryException, current, null);
        }
        if (pending.Count != 0) truncated = true;
        if (truncated)
            RecordUnconfirmed(new IOException("Cleanup exception traversal exceeded its bounded ownership projection."));
        return CleanupUnconfirmed;
    }

    private void RecordUnconfirmed(Exception error) =>
        Interlocked.CompareExchange(ref cleanupException, error, null);

    private void RecordCleanup(Exception error) => AnalyzeCleanup(error, disposing: true);

    internal WorkflowCommandResult Apply(WorkflowCommandResult result)
    {
        var unknown = CleanupUnconfirmed;
        var known = Volatile.Read(ref knownReadinessFailure) is not null;
        if (!unknown && !known) return result;
        var diagnostic = unknown ? CleanupDiagnostic :
            "Model readiness cleanup completed with a known failure. Inspect this effect before continuing.";
        var error = result.Error;
        if (string.IsNullOrEmpty(error)) error = diagnostic;
        else if (!error.EndsWith(diagnostic, StringComparison.Ordinal) &&
            error.Length <= 4096 - 1 - diagnostic.Length) error += " " + diagnostic;
        var state = unknown || result.State == WorkflowEffectState.Unknown ? WorkflowEffectState.Unknown :
            PrimaryException is OperationCanceledException || result.State == WorkflowEffectState.Cancelled
                ? WorkflowEffectState.Cancelled : WorkflowEffectState.Failed;
        return result with { State = state, Error = error };
    }

    internal WorkflowCommandResult DisposeReadiness(IDisposable? readiness, WorkflowCommandResult result)
    {
        try { readiness?.Dispose(); }
        catch (Exception error) when (Ordinary(error)) { RecordCleanup(error); }
        return Apply(result);
    }

    internal IModelRuntimeAdapter Track(IModelRuntimeAdapter adapter) => new Adapter(this, adapter);

    internal async Task<WorkflowCommandResult> RunPreparedAsync(IPreparedModelGeneration prepared,
        Func<Task<WorkflowCommandResult>> action)
    {
        WorkflowCommandResult? result = null;
        ExceptionDispatchInfo? primary = null;
        try { result = await action().ConfigureAwait(false); }
        catch (Exception error) when (Ordinary(error))
        {
            RecordPrimary(error);
            primary = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            try { await prepared.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (Ordinary(error))
            {
                if (!ObserveCleanup(error)) RecordCleanup(error);
            }
        }
        // The caller classifies the original failure after cleanup. Its tracker still carries any
        // separate cleanup failure, so cancellation or preparation catches cannot erase it.
        primary?.Throw();
        return Apply(result!);
    }

    private sealed class Adapter(WorkflowGenerationCleanup owner, IModelRuntimeAdapter inner) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => inner.Descriptor;
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            try
            {
                var prepared = await inner.PrepareAsync(request, token).ConfigureAwait(false);
                IPreparedModelGeneration wrapped = prepared is IRegisteredNativeWorkflowPreparation observed
                    ? new ObservedPreparation(owner, observed) : new Preparation(owner, prepared);
                return LocalModelExecutionPolicy.Forward(wrapped, prepared);
            }
            catch (Exception error) when (Ordinary(error)) { owner.ObserveCleanup(error); throw; }
        }
    }

    private class Preparation(WorkflowGenerationCleanup owner, IPreparedModelGeneration inner)
        : IPreparedModelGeneration, IBoundGenerationTelemetry, IBoundGenerationDraft
    {
        private readonly object gate = new();
        private Task? disposal;
        public PreparedModelMetadata Metadata => inner.Metadata;
        public void BindTelemetry(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer) =>
            (inner as IBoundGenerationTelemetry)?.BindTelemetry(binding, observer);
        public bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer) =>
            (inner as IBoundGenerationDraft)?.TryBindDraft(binding, observer) ?? false;
        public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            owner.GenerationStarting();
            try { return await inner.GenerateAsync(onText, token).ConfigureAwait(false); }
            catch (Exception error) when (Ordinary(error)) { owner.RecordPrimary(error); throw; }
        }
        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? start = null;
            Task task;
            lock (gate)
            {
                if (disposal is null)
                {
                    start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    disposal = start.Task;
                }
                task = disposal;
            }
            // Planner catch paths may dispose an unsuccessful probe a second time. Share the actual
            // cleanup attempt, including its failure; never repeat native disposal or retry preparation.
            if (start is not null) _ = DisposeCoreAsync(start);
            return new(task);
        }
        private async Task DisposeCoreAsync(TaskCompletionSource completion)
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception error) when (Ordinary(error))
            {
                owner.RecordCleanup(error);
                // Keep cleanup distinguishable from cancellation and model-capacity control flow.
                completion.TrySetException(new PreparationCleanupException(error));
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }

    private sealed class ObservedPreparation : Preparation, IRegisteredNativeWorkflowPreparation
    {
        private readonly IRegisteredNativeWorkflowPreparation observed;
        internal ObservedPreparation(WorkflowGenerationCleanup owner, IRegisteredNativeWorkflowPreparation inner)
            : base(owner, inner) => observed = inner;
        public RegisteredWorkflowPreparationObservation Observe() => observed.Observe();
    }

    private sealed class PreparationCleanupException(Exception error) : IOException(CleanupDiagnostic, error);
    private static bool Ordinary(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
