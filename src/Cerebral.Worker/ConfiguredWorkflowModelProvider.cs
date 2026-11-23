using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker;

// Selection performs no loading or measurement. Preparation joins Engine's existing
// exact-binding readiness path and borrows its cached registration; Core never owns it.
internal sealed class ConfiguredWorkflowModelProvider : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, ConfiguredWorkflowModelCoordinator> models = new(StringComparer.Ordinal);
    private readonly Func<RemotePreparedModelBinding, CancellationToken, Task<IWorkflowReadinessLease>> acquire;
    private readonly Func<RemotePreparedModelBinding, AdapterBinding, CancellationToken, Task> validate;
    private bool closed;
    private Task? disposal;

    public ConfiguredWorkflowModelProvider(ModelRuntime runtime)
        : this(async (expected, token) =>
        {
            try
            {
                return new EngineReadinessLease(await runtime.AcquireReadyRegisteredNativeTextAsync(expected, token).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not IRegisteredNativeReadinessFailure)
            {
                throw new WorkflowReadinessCleanupException(error, error);
            }
        }, async (expected, binding, token) =>
        {
            await runtime.WithOwnerRuntimeSnapshotAsync(expected.Id, observed =>
            {
                ConfiguredWorkflowModelCoordinator.RequireLoadedWorkflowBinding(expected, observed, binding);
                return true;
            }, token).ConfigureAwait(false);
        })
    { ArgumentNullException.ThrowIfNull(runtime); }

    internal ConfiguredWorkflowModelProvider(
        Func<RemotePreparedModelBinding, CancellationToken, Task<IWorkflowReadinessLease>> acquire,
        Func<RemotePreparedModelBinding, AdapterBinding, CancellationToken, Task> validate)
    {
        this.acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        this.validate = validate ?? throw new ArgumentNullException(nameof(validate));
    }

    internal IModelRuntimeAdapter ForAccepted(AgentProfile profile, WorkerExecutionSnapshot.ModelBinding accepted)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(accepted);
        var mode = profile.ReasoningMode;
        if (profile.WorkerId != "local" || profile.ModelWorkerId != "local" ||
            accepted.ModelWorkerId != "local" || accepted.ModelWorkerInstallationId is not null ||
            accepted.Id != profile.ModelId)
            throw new RemoteModelProtocolException("The accepted local model and worker identity do not match.");
        if (mode is not ("off" or "on") || accepted.VisionProjectorPath is not null ||
            ModelCapabilityPolicy.Requirements(profile).Contains(ModelCapabilityIds.ImageInput))
            throw new ModelCapabilityReadinessException("Configured registered workflows require an accepted off/on text binding.");
        var expected = WorkerExecutionSnapshot.RemoteBinding(accepted);
        expected.Validate();
        expected = expected with { StopSequences = Array.AsReadOnly(expected.StopSequences.ToArray()) };
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(ConfiguredWorkflowModelProvider));
            if (!models.TryGetValue(expected.Id, out var coordinator))
            {
                coordinator = new(expected.Id, acquire, validate);
                models.Add(expected.Id, coordinator);
            }
            if (profile.AllowReasoningAdjustment)
                return new AdaptiveWorkflowAdapter(coordinator.ForAccepted(expected, false),
                    coordinator.ForAccepted(expected, true));
            return coordinator.ForAccepted(expected, mode == "on");
        }
    }

    // The accepted profile opts in to both text facets. The host still validates each
    // generation choice against the frozen and current owner ceilings before preparation.
    private sealed class AdaptiveWorkflowAdapter(IModelRuntimeAdapter withoutReasoning,
        IModelRuntimeAdapter withReasoning) : IConfiguredLocalWorkflowAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = withReasoning.Descriptor;

        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            (request.ReasoningTokens == 0 ? withoutReasoning : withReasoning).PrepareAsync(request, token);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? start = null;
        Task task;
        ConfiguredWorkflowModelCoordinator[] owners;
        lock (gate)
        {
            closed = true;
            owners = models.Values.ToArray();
            if (disposal is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                disposal = start.Task;
            }
            task = disposal;
        }
        if (start is not null) _ = DisposeCoreAsync(start, owners);
        return new(task);
    }

    private static async Task DisposeCoreAsync(TaskCompletionSource done, ConfiguredWorkflowModelCoordinator[] owners)
    {
        try
        {
            await Task.WhenAll(owners.Select(owner => owner.DisposeAsync().AsTask())).ConfigureAwait(false);
            done.TrySetResult();
        }
        catch (Exception error) { done.TrySetException(error); }
    }

    private sealed class EngineReadinessLease(RegisteredNativeReadinessLease inner) : IWorkflowReadinessLease
    {
        public IRegisteredNativeGenerationService Registration => inner.Registration;
        public async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is not IRegisteredNativeReadinessFailure)
            { throw new WorkflowReadinessCleanupException(error, error); }
        }
    }
}
