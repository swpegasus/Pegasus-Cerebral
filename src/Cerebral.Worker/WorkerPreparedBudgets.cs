using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Private host budget narrowing; accepted reasoning mode and provider contracts remain frozen.</summary>
internal static class WorkerPreparedBudgets
{
    internal static int ReasoningTokens(int acceptedTokens, AgentBudgetResolution live, int contextTokens)
    {
        if (acceptedTokens == 0) return 0;
        return Resolve(contextTokens, AgentTokenLimits.MinimumResponseTokens,
            Math.Min(acceptedTokens, live.ReasoningTokens)).ReasoningTokens;
    }

    private static AgentBudgetResolution Resolve(int capacity, int responseTokens, int reasoningTokens) =>
        AgentBudgetPolicy.Resolve(new AgentProfile
        {
            FollowsModelBudgets = false, ContextTokens = capacity, ResponseTokens = responseTokens,
            ReasoningMode = reasoningTokens == 0 ? "off" : "on",
            ReasoningTokens = reasoningTokens == 0 ? AgentTokenLimits.MinimumReasoningTokens : reasoningTokens
        }, new ModelRecord { ContextTokens = capacity });

    private static ModelGenerationRequest AtContext(ModelGenerationRequest request, int contextTokens,
        ModelExecutionPolicy? selected = null)
    {
        var capacity = Math.Min(request.ContextLimit, contextTokens);
        if (selected is not null)
        {
            // Native preparation measures exact input again. Retain the requested visible
            // allocation as desired; only its actual fit may shrink when explicitly allowed.
            var narrowed = request with { ContextLimit = capacity };
            narrowed.Validate(selected);
            return narrowed;
        }
        var budget = Resolve(capacity, request.ResponseLimit, request.ReasoningTokens);
        return request with
        {
            ContextLimit = capacity,
            ResponseLimit = Math.Min(request.ResponseLimit, budget.ResponseTokens),
            ReasoningTokens = request.ReasoningTokens == 0 ? 0 : Math.Min(request.ReasoningTokens, budget.ReasoningTokens)
        };
    }

    internal static IModelRuntimeAdapter ForPreparation(IModelRuntimeAdapter adapter) => new CapacityAdapter(adapter);

    private sealed class CapacityAdapter(IModelRuntimeAdapter inner) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => inner.Descriptor;

        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            var selected = (inner as IModelExecutionPolicyFacet)?.ExecutionPolicy;
            if (selected is not null) request.Validate(selected);
            IPreparedModelGeneration? prepared = null;
            ModelContextLimitException? limited = null;
            PreparedModelMetadata metadata;
            try
            {
                prepared = await inner.PrepareAsync(request, token).ConfigureAwait(false);
                metadata = prepared.Metadata;
            }
            catch (ModelContextLimitException error)
            {
                limited = error;
                metadata = error.Metadata;
            }

            if (metadata.EffectiveContext >= request.ContextLimit)
            {
                if (prepared is not null) return prepared;
                throw limited!;
            }
            // A load or remote fit can shrink after host admission. Release the first measured
            // lease before repeating preparation once, changing only budgets on the already
            // composed immutable request. Never modify a counted body or dispatch inference here.
            if (prepared is not null) await prepared.DisposeAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var narrowed = AtContext(request, metadata.EffectiveContext, selected);
            var repeated = await inner.PrepareAsync(narrowed, token).ConfigureAwait(false);
            if (selected is not null) LocalModelExecutionPolicy.Require(repeated, selected);
            return repeated;
        }
    }
}
