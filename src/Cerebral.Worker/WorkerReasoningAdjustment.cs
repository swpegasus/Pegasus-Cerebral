using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>One generation's reasoning choice, bounded by both the accepted and current owner profile.</summary>
internal static class WorkerReasoningAdjustment
{
    internal static int Resolve(AgentProfile accepted, AgentProfile current, int? requestedTokens,
        int contextTokens)
    {
        if (accepted.Id != current.Id || accepted.Revision > current.Revision ||
            accepted.ModelId != current.ModelId || accepted.ModelWorkerId != current.ModelWorkerId)
            throw new InvalidOperationException("The accepted reasoning profile or model binding changed. Start a new request.");
        if (!accepted.AllowReasoningAdjustment || !current.AllowReasoningAdjustment)
            throw new InvalidOperationException("This assignment no longer has owner permission to adjust reasoning.");
        if (accepted.ReasoningMode is not ("on" or "off") || current.ReasoningMode is not ("on" or "off"))
            throw new InvalidOperationException("The selected reasoning policy is invalid.");
        var requested = requestedTokens ?? (accepted.ReasoningMode == "on" ? accepted.ReasoningTokens : 0);
        if (requested == 0)
        {
            if (accepted.ReasoningMode == "on" || current.ReasoningMode == "on")
                throw new InvalidOperationException("This owner's reasoning policy requires a positive reserve.");
            return 0;
        }
        if (requested < AgentTokenLimits.MinimumReasoningTokens ||
            requested > AgentTokenLimits.MaximumReasoningTokens)
            throw new InvalidOperationException("The requested reasoning reserve is outside supported limits.");
        var acceptedCeiling = accepted.ReasoningTokenCeiling == 0
            ? accepted.ReasoningTokens : accepted.ReasoningTokenCeiling;
        var currentCeiling = current.ReasoningTokenCeiling == 0
            ? current.ReasoningTokens : current.ReasoningTokenCeiling;
        var contextCeiling = contextTokens - AgentTokenLimits.MinimumResponseTokens -
            AgentTokenLimits.ContextSafetyTokens - AgentTokenLimits.MinimumPromptTokens - 1;
        if (requested > Math.Min(Math.Min(acceptedCeiling, currentCeiling), contextCeiling))
            throw new InvalidOperationException("The requested reasoning reserve exceeds the accepted or current owner ceiling or leaves no reliable response room.");
        return requested;
    }
}
