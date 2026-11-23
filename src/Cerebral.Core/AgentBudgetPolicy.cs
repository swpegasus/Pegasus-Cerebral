namespace Cerebral.Core;

/// <summary>The effective token settings for one agent assignment on a selected model.</summary>
public sealed record AgentBudgetResolution(
    int ContextTokens,
    int ResponseTokens,
    string ReasoningMode,
    int ReasoningTokens);

/// <summary>
/// Resolves model-derived or agent-specific token settings without changing either saved record.
/// Model bindings and token-budget inheritance are deliberately independent choices.
/// </summary>
public static class AgentBudgetPolicy
{
    public static AgentBudgetResolution Resolve(
        AgentProfile profile,
        ModelRecord model,
        int? recommendedContextTokens = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(model);
        if (model.ContextTokens < AgentTokenLimits.MinimumContextTokens)
            throw new ArgumentException($"The model context limit must be at least {AgentTokenLimits.MinimumContextTokens} tokens.", nameof(model));

        int context;
        int response;
        if (profile.FollowsModelBudgets)
        {
            var ownerPolicy = profile.TaskBudgetPolicy;
            if (ownerPolicy is not null &&
                (ownerPolicy.SchemaVersion != 1 ||
                 ownerPolicy.MaxContextTokens is null && ownerPolicy.MaxResponseTokens is null ||
                 ownerPolicy.MaxContextTokens is { } contextCeiling && contextCeiling < profile.ContextTokens ||
                 ownerPolicy.MaxResponseTokens is { } responseCeiling && responseCeiling < profile.ResponseTokens ||
                 ownerPolicy.MaxResponseTokens is { } requestedOutput && requestedOutput >
                     AgentTokenLimits.MaximumVisibleResponse(ownerPolicy.MaxContextTokens ?? profile.ContextTokens,
                         profile.ReasoningMode, profile.ReasoningMode == "on" ?
                             profile.ReasoningTokenCeiling == 0 ? profile.ReasoningTokens : profile.ReasoningTokenCeiling : 0)))
                throw new ArgumentException("The owner task budget policy is invalid or leaves no input and response headroom.", nameof(profile));

            // Follow the selected/current context. Metadata recommendations remain import hints;
            // a confirmed fit may change model capacity, but an owner ceiling still bounds the assignment.
            var selectedContext = recommendedContextTokens ?? model.ContextTokens;
            if (ownerPolicy?.MaxContextTokens is { } maximumContext)
                selectedContext = Math.Min(selectedContext, maximumContext);
            context = Math.Clamp(selectedContext, AgentTokenLimits.MinimumContextTokens, model.ContextTokens);
            response = context / 4;
            if (ownerPolicy?.MaxResponseTokens is { } ownerResponseCeiling)
                response = Math.Min(response, ownerResponseCeiling);
        }
        else
        {
            context = Math.Clamp(profile.ContextTokens, AgentTokenLimits.MinimumContextTokens, model.ContextTokens);
            response = profile.ResponseTokens;
        }

        var reasoningMode = profile.ReasoningMode;
        var reasoningTokens = Math.Clamp(profile.ReasoningTokens, AgentTokenLimits.MinimumReasoningTokens, AgentTokenLimits.MaximumReasoningTokens);
        if (reasoningMode == "on")
        {
            var maximumReasoning = context - AgentTokenLimits.MinimumResponseTokens - AgentTokenLimits.ContextSafetyTokens - AgentTokenLimits.MinimumPromptTokens - 1;
            if (maximumReasoning < AgentTokenLimits.MinimumReasoningTokens)
                throw new ArgumentException("The model context is too small for both reasoning and a reliable visible response.", nameof(model));
            reasoningTokens = Math.Min(reasoningTokens, maximumReasoning);
        }

        var maximumResponse = AgentTokenLimits.MaximumVisibleResponse(context, reasoningMode, reasoningTokens);
        if (maximumResponse < AgentTokenLimits.MinimumResponseTokens)
            throw new ArgumentException("The model context and reasoning reserve leave no reliable visible response.", nameof(model));
        response = Math.Clamp(response, AgentTokenLimits.MinimumResponseTokens, maximumResponse);

        return new(context, response, reasoningMode, reasoningTokens);
    }
}
