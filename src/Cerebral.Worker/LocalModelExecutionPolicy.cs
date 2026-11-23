using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

internal interface ILocalModelPolicySelection
{
    Task<ILocalModelPolicyScope> SelectAsync(CancellationToken token);
}

internal interface ILocalModelPolicyScope : IAsyncDisposable
{
    ModelExecutionPolicy Policy { get; }
    IModelRuntimeAdapter Adapter { get; }
    Task RequireCurrentAsync(CancellationToken token);
}

/// <summary>Worker-private joins for the selected loaded handler. A policy value alone cannot
/// open this path; it must arrive through an owned selector and survive every preparation.</summary>
internal static class LocalModelExecutionPolicy
{
    internal static IModelRuntimeAdapter Bind(IModelRuntimeAdapter inner, ModelExecutionPolicy policy) =>
        new PolicyAdapterView(inner, policy);

    private sealed class PolicyAdapterView(IModelRuntimeAdapter inner, ModelExecutionPolicy policy)
        : IModelRuntimeAdapter, IModelExecutionPolicyFacet
    {
        public ModelExecutionPolicy ExecutionPolicy { get; } = policy;
        public ModelAdapterDescriptor Descriptor => inner.Descriptor;
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            request.Validate(ExecutionPolicy);
            var prepared = await inner.PrepareAsync(request, token).ConfigureAwait(false);
            try { Require(prepared, ExecutionPolicy); return prepared; }
            catch (Exception primary)
            {
                try { await prepared.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new WorkflowReadinessCleanupException(primary, cleanup); }
                throw;
            }
        }
    }

    internal static ModelExecutionPolicy Require(IModelRuntimeAdapter adapter, ModelExecutionPolicy expected)
    {
        if (adapter is not IModelExecutionPolicyFacet facet || facet.ExecutionPolicy != expected)
            throw new InvalidOperationException("selected_local_policy_adapter_changed");
        return expected;
    }

    internal static ModelExecutionPolicy Require(IPreparedModelGeneration prepared, ModelExecutionPolicy expected)
    {
        if (prepared is not IModelExecutionPolicyFacet facet || facet.ExecutionPolicy != expected)
            throw new InvalidOperationException("selected_local_policy_preparation_changed");
        return expected;
    }

    internal static IPreparedModelGeneration Forward(IPreparedModelGeneration wrapped,
        IPreparedModelGeneration source)
    {
        if (source is not IModelExecutionPolicyFacet facet) return wrapped;
        var policy = facet.ExecutionPolicy;
        policy.Validate();
        return wrapped is IRegisteredNativeWorkflowPreparation observed
            ? new ObservedPolicyPreparedView(observed, policy)
            : new PolicyPreparedView(wrapped, policy);
    }

    internal static void ValidateMetadata(ModelExecutionPolicy policy, ModelGenerationRequest request,
        PreparedModelMetadata metadata, bool contextLimited)
    {
        policy.Validate(); request.Validate(policy);
        RemotePreparedProtocol.ValidateMetadata(metadata, request, contextLimited);
        if (metadata.InstanceId != policy.Binding.InstanceId ||
            metadata.ModelId != policy.Binding.ModelId || metadata.AdapterId != policy.Binding.AdapterId ||
            metadata.AdapterVersion != policy.Binding.AdapterVersion ||
            metadata.InputTokens < 1 ||
            metadata.DesiredResponseBudget != request.ResponseLimit ||
            metadata.ReasoningReserveTokens != 0 ||
            metadata.SafetyReserveTokens != AgentTokenLimits.ContextSafetyTokens ||
            !contextLimited && (long)metadata.InputTokens + metadata.ResponseBudget +
                metadata.SafetyReserveTokens > metadata.EffectiveContext ||
            !contextLimited && !request.AllowResponseClamping && metadata.ResponseBudget != request.ResponseLimit)
            throw new InvalidDataException("The selected local policy prepared metadata is invalid.");
    }

    private class PolicyPreparedView(IPreparedModelGeneration inner, ModelExecutionPolicy policy)
        : IPreparedModelGeneration, IModelExecutionPolicyFacet, IBoundGenerationTelemetry,
            IBoundGenerationDraft, IProviderGenerationTelemetry, IModelReleaseObservation
    {
        public ModelExecutionPolicy ExecutionPolicy { get; } = policy;
        public PreparedModelMetadata Metadata => inner.Metadata;
        public bool ReleaseConfirmed => inner is IModelReleaseObservation released && released.ReleaseConfirmed;
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token) =>
            inner.GenerateAsync(onText, token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void BindTelemetry(GenerationTelemetryBinding binding, Action<GenerationTelemetrySample> observer) =>
            (inner as IBoundGenerationTelemetry)?.BindTelemetry(binding, observer);
        public bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer) =>
            (inner as IBoundGenerationDraft)?.TryBindDraft(binding, observer) ?? false;
        public void SetTelemetryObserver(Action<ProviderGenerationMeasurement>? observer) =>
            (inner as IProviderGenerationTelemetry)?.SetTelemetryObserver(observer);
    }

    private sealed class ObservedPolicyPreparedView(IRegisteredNativeWorkflowPreparation inner,
        ModelExecutionPolicy policy) : PolicyPreparedView(inner, policy), IRegisteredNativeWorkflowPreparation
    {
        public RegisteredWorkflowPreparationObservation Observe() => inner.Observe();
    }
}
