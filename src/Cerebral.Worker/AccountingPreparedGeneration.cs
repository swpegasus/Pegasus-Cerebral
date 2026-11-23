using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

internal interface IModelReleaseObservation { bool ReleaseConfirmed { get; } }

internal sealed class AccountingPreparedGeneration : IPreparedModelGeneration, IModelReleaseObservation, IProviderGenerationTelemetry, IBoundGenerationTelemetry
{
    private readonly IPreparedModelGeneration inner;
    private readonly ModelExecutionAccounting.ModelOwnership ownership;
    private readonly bool releaseFromProvider;
    private readonly CancellationTokenRegistration preparationCancellation;
    internal AccountingPreparedGeneration(IPreparedModelGeneration inner, ModelExecutionAccounting.ModelOwnership ownership,
        bool releaseFromProvider = false, CancellationToken cancellationToken = default)
    {
        this.inner = inner; this.ownership = ownership; this.releaseFromProvider = releaseFromProvider;
        ownership.Bind(instanceId: inner.Metadata.InstanceId);
        ownership.Phase(ModelOwnershipPhase.Ready);
        ownership.Prepared(inner.Metadata);
        preparationCancellation = ownership.ObserveCancellation(cancellationToken);
    }

    public void BindTelemetry(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer) =>
        (inner as IBoundGenerationTelemetry)?.BindTelemetry(binding, observer);
    public PreparedModelMetadata Metadata => inner.Metadata;
    public bool ReleaseConfirmed => ownership.IsRetired;
    public void SetTelemetryObserver(Action<ProviderGenerationMeasurement>? observer) =>
        (inner as IProviderGenerationTelemetry)?.SetTelemetryObserver(observer is null ? null : value =>
        { ownership.Progress(value); observer(value); });
    public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
    {
        ownership.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
        // The native allocator's exact return/quarantine callback owns completion, including cancellation.
        using var cancellation = ownership.ObserveCancellation(token);
        try
        {
            var result = await inner.GenerateAsync(onText, token).ConfigureAwait(false);
            ownership.Terminal(result);
            return result;
        }
        catch { ownership.Terminal(null); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        ownership.Phase(ModelOwnershipPhase.Releasing);
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            if (releaseFromProvider)
            {
                if (inner is IModelReleaseObservation observed && observed.ReleaseConfirmed) ownership.Complete();
                else ownership.Unknown("external-provider-release-unobserved");
            }
        }
        catch { ownership.Unknown("prepared-release-failed"); throw; }
        finally { preparationCancellation.Dispose(); }
    }
}
