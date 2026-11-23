namespace Cerebral.Core;

/// <summary>
/// Optional version-1 service-host facet for bounded, scalar generation observations.
/// It grants no model, workflow, diagnostic-text, or history access. A service package
/// must opt into collection itself; opening a reader does not start an inference.
/// </summary>
public interface IServiceScalarGenerationHostV1
{
    IScalarGenerationSubscriptionV1 OpenScalarGenerationObservations();
}

public interface IScalarGenerationSubscriptionV1 : IDisposable
{
    /// <summary>Records rejected by the producer or lost to this bounded reader's full queue.</summary>
    long DroppedRecords { get; }
    IAsyncEnumerable<ScalarGenerationObservationV1> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>The accepted, prepared model target route; unrelated to the local producer identity.</summary>
public enum ScalarGenerationTargetOriginV1 { Unknown, Local, Remote }

/// <summary>
/// One host-authored numeric sample. The producer identifies this Worker process;
/// model and adapter IDs identify the target and never imply a local physical slot.
/// Provider-observed token counts are progress measurements, not confirmed usage.
/// Rates are provider-phase rates, not estimates from text or wall time.
/// TargetOrigin is qualified from the accepted route joined to the prepared model;
/// Unknown means the adapter must not infer Local or Remote from producer identity.
/// No conversation, project, request, run, effect, agent, or raw content is carried.
/// Sequence is monotonic for this producer feed and acts as a per-lane revision.
/// </summary>
public sealed record ScalarGenerationObservationV1(
    long Sequence, DateTimeOffset ObservedAt,
    string ProducerInstallationId, string ProducerProcessGenerationId,
    string LaneId, string ModelId, string ModelInstanceId, string ModelAdapterId,
    string State, string Phase,
    int? ProviderObservedInputTokens, int? ProviderObservedOutputTokens,
    double? ProviderInputTokensPerSecond, double? ProviderOutputTokensPerSecond,
    long ElapsedMilliseconds, ScalarGenerationTargetOriginV1 TargetOrigin)
{
    public const string Contract = "cerebral.service.scalar-generation/1";
    public const string ProviderTokenScope = "provider-unspecified";
}
