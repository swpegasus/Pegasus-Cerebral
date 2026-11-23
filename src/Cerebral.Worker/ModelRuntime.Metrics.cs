using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    private long confirmedInputTokens;
    private long confirmedOutputTokens;
    private int confirmedInputComplete = 1;
    private int confirmedOutputComplete = 1;

    public ModelUsageTotals UsageTotals() => new(
        Interlocked.Read(ref confirmedInputTokens),
        Interlocked.Read(ref confirmedOutputTokens))
    {
        ConfirmedInputComplete = Volatile.Read(ref confirmedInputComplete) != 0,
        ConfirmedOutputComplete = Volatile.Read(ref confirmedOutputComplete) != 0
    };

    internal IReadOnlyList<int> ResourceProcessIds() => instances.Values.Select(instance =>
    {
        try { return instance.Process.HasExited ? 0 : instance.Process.Id; }
        catch (InvalidOperationException) { return 0; }
    }).Where(id => id > 0).Distinct().ToArray();

    private IPreparedModelGeneration TrackUsage(IPreparedModelGeneration prepared) =>
        prepared is IUsageTrackedGeneration ? prepared :
            LocalModelExecutionPolicy.Forward(new UsageTrackingGeneration(this, prepared), prepared);

    /// <summary>Shares this Worker's process-lifetime meter with a paired adapter without changing
    /// the adapter's preparation, capability, one-use, cancellation, or disposal contracts.</summary>
    internal IModelRuntimeAdapter TrackExternalUsage(IModelRuntimeAdapter runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (ReferenceEquals(runtime, this) || runtime is IUsageTrackedRuntime) return runtime;
        // Resource resolution precedes provider dispatch and returns the inner preparation unchanged.
        // Only this sealed wrapper over an adapter tracked by this owner can skip redundant ownership.
        if (runtime is LlamaResourceModelAdapter resource &&
            resource.DelegatedRuntime is UsageTrackingAdapter tracked && ReferenceEquals(tracked.Owner, this))
            return runtime;
        return runtime is IMultimodalModelRuntimeAdapter multimodal
            ? new MultimodalUsageTrackingAdapter(this, runtime, multimodal)
            : new UsageTrackingAdapter(this, runtime);
    }

    private void RecordUsage(ModelGenerationResult result)
    {
        if (result.EvaluatedTokens is { } input and >= 0)
            Interlocked.Add(ref confirmedInputTokens, input);
        else
            Volatile.Write(ref confirmedInputComplete, 0);
        if (result.GeneratedTokens is { } output and >= 0)
            Interlocked.Add(ref confirmedOutputTokens, output);
        else
            Volatile.Write(ref confirmedOutputComplete, 0);
    }

    private void MarkUsageIncomplete()
    {
        Volatile.Write(ref confirmedInputComplete, 0);
        Volatile.Write(ref confirmedOutputComplete, 0);
    }

    private interface IUsageTrackedRuntime { }
    private interface IUsageTrackedGeneration { }

    private class UsageTrackingAdapter(ModelRuntime owner, IModelRuntimeAdapter inner)
        : IModelRuntimeAdapter, IUsageTrackedRuntime
    {
        public ModelRuntime Owner => owner;
        public ModelAdapterDescriptor Descriptor => inner.Descriptor;

        protected IPreparedModelGeneration Track(IPreparedModelGeneration prepared) => owner.TrackUsage(prepared);

        protected async Task<IPreparedModelGeneration> PrepareTrackedAsync(ModelGenerationRequest request,
            Func<Task<IPreparedModelGeneration>> prepare)
        {
            var alreadyOwned = inner is ModelRuntime native && ReferenceEquals(native.Accounting, owner.Accounting) ||
                inner is RemoteModelRuntimeAdapter remote && ReferenceEquals(remote.Accounting, owner.Accounting);
            if (alreadyOwned) return Track(await prepare().ConfigureAwait(false));
            var ownership = owner.Accounting.Begin(ModelOwnershipKind.OutboundPreparation, request.ModelId);
            ownership.Phase(ModelOwnershipPhase.Preparing, effectStarted: true);
            try
            {
                var prepared = await prepare().ConfigureAwait(false);
                // Transparent resource adapters can return this owner's already-accounted preparation.
                // Preserve its one-use meter, telemetry binding and exact ownership instead of wrapping twice.
                if (prepared is UsageTrackingGeneration tracked && ReferenceEquals(tracked.Owner, owner))
                {
                    ownership.Complete();
                    return prepared;
                }
                var accounted = new AccountingPreparedGeneration(prepared, ownership, releaseFromProvider: true);
                return prepared is IUsageTrackedGeneration ? accounted : Track(accounted);
            }
            catch (ModelContextLimitException) { ownership.Complete(); throw; }
            catch { ownership.Unknown("external-provider-preparation-unconfirmed"); throw; }
        }

        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken) =>
            PrepareTrackedAsync(request, () => inner.PrepareAsync(request, cancellationToken));
    }

    private sealed class MultimodalUsageTrackingAdapter(ModelRuntime owner, IModelRuntimeAdapter inner,
        IMultimodalModelRuntimeAdapter multimodal) : UsageTrackingAdapter(owner, inner), IMultimodalModelRuntimeAdapter
    {
        public Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
            IReadOnlyList<RemotePreparedImageInput> images, CancellationToken cancellationToken) =>
            PrepareTrackedAsync(request, () => multimodal.PrepareMultimodalAsync(request, images, cancellationToken));
    }

    private sealed class UsageTrackingGeneration(ModelRuntime owner, IPreparedModelGeneration inner)
        : IPreparedModelGeneration, IUsageTrackedGeneration, IBoundGenerationTelemetry, IModelReleaseObservation
    {
        private int used;
        private GenerationTelemetryBinding? telemetryBinding;
        private Action<GenerationTelemetrySample>? telemetryObserver;
        public void BindTelemetry(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer)
        { telemetryBinding = binding; telemetryObserver = observer; }
        public ModelRuntime Owner => owner;
        public PreparedModelMetadata Metadata => inner.Metadata;
        public bool ReleaseConfirmed => inner is IModelReleaseObservation observed && observed.ReleaseConfirmed;

        public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref used, 1, 0) != 0)
                throw new InvalidOperationException("A prepared generation is one-use and cannot be replayed.");
            using var telemetry = owner.BeginGenerationTelemetry(Metadata, telemetryBinding, telemetryObserver);
            var provider = inner as IProviderGenerationTelemetry;
            provider?.SetTelemetryObserver(telemetry.Observe);
            try
            {
                var result = await inner.GenerateAsync(onText, cancellationToken).ConfigureAwait(false);
                owner.RecordUsage(result);
                telemetry.Finish(result);
                return result;
            }
            catch
            {
                telemetry.Finish(null);
                // Once generation was handed to an adapter, a thrown terminal path cannot prove
                // that the provider consumed no tokens. Keep the confirmed counts as lower bounds.
                owner.MarkUsageIncomplete();
                throw;
            }
            finally { provider?.SetTelemetryObserver(null); }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
