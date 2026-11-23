namespace Cerebral.Core;

/// <summary>Shared limits for dividing one model context window between input, reasoning, and visible output.</summary>
public static class AgentTokenLimits
{
    public const int MinimumContextTokens = 512;
    public const int MinimumResponseTokens = 128;
    public const int MaximumResponseTokens = 131_072;
    public const int MinimumReasoningTokens = 128;
    public const int MaximumReasoningTokens = 32_768;
    public const int ContextSafetyTokens = 128;
    public const int MinimumPromptTokens = 128;
    public const int MinimumResponseSharePercent = 5;
    public const int MaximumResponseSharePercent = 50;
    public const int MinimumCompactionReductionPercent = 10;
    public const int MaximumCompactionReductionPercent = 90;
    public const int DefaultCompactionTriggerPercent = 80;
    public const int MinimumCompactionTriggerPercent = 10;
    public const int MaximumCompactionTriggerPercent = 95;

    public static int ValidateCompactionTrigger(int value, string? parameterName = null)
    {
        if (value is < MinimumCompactionTriggerPercent or > MaximumCompactionTriggerPercent)
            throw new ArgumentOutOfRangeException(parameterName ?? nameof(value),
                $"Compaction trigger must be between {MinimumCompactionTriggerPercent} and {MaximumCompactionTriggerPercent} percent.");
        return value;
    }

    public static int ValidateCompactionReduction(int value, string? parameterName = null)
    {
        if (value is < MinimumCompactionReductionPercent or > MaximumCompactionReductionPercent)
            throw new ArgumentOutOfRangeException(parameterName ?? nameof(value),
                $"Compaction reduction must be between {MinimumCompactionReductionPercent} and {MaximumCompactionReductionPercent} percent.");
        return value;
    }

    public static int? ValidateResponseShare(int? value, string? parameterName = null)
    {
        if (value is not null and (< MinimumResponseSharePercent or > MaximumResponseSharePercent))
            throw new ArgumentOutOfRangeException(parameterName ?? nameof(value),
                $"Response balance must be between {MinimumResponseSharePercent} and {MaximumResponseSharePercent} percent.");
        return value;
    }

    public static int MaximumVisibleResponse(int contextTokens, string reasoningMode, int reasoningTokens)
    {
        if (contextTokens < MinimumContextTokens) throw new ArgumentOutOfRangeException(nameof(contextTokens));
        if (reasoningMode is not ("on" or "off")) throw new ArgumentException("Reasoning mode must be on or off.", nameof(reasoningMode));
        var reasoning = reasoningMode == "on" ? reasoningTokens : 0;
        return Math.Min(MaximumResponseTokens,
            contextTokens - reasoning - ContextSafetyTokens - MinimumPromptTokens - 1);
    }

    public static int ResolveResponseShare(AgentProfile profile, int responseSharePercent)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateResponseShare(responseSharePercent, nameof(responseSharePercent));
        var requested = (int)Math.Round((long)profile.ContextTokens * responseSharePercent / 100d,
            MidpointRounding.AwayFromZero);
        var maximum = MaximumVisibleResponse(profile.ContextTokens, profile.ReasoningMode, profile.ReasoningTokens);
        if (maximum < MinimumResponseTokens)
            throw new ArgumentException("The selected context and reasoning reserve leave no reliable response capacity.", nameof(profile));
        return Math.Clamp(requested, MinimumResponseTokens, maximum);
    }
}
