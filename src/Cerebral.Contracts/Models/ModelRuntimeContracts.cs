using System.Text.Json;

namespace Cerebral.Contracts.Models;

public sealed record ModelInputMessage(string Role, IReadOnlyList<ContentPart> Content);

/// <summary>Provider-neutral input. ResponseSharePercent caps output within the effective context;
/// it never removes input. Binary parts require an adapter that explicitly supports their kind.</summary>
public sealed record ModelGenerationRequest(
    string ModelId,
    IReadOnlyList<ModelInputMessage> Messages,
    int ContextLimit,
    int ResponseLimit,
    int? ResponseSharePercent = null,
    double Temperature = 0.2,
    JsonElement? ResponseSchema = null)
{
    /// <summary>Treat ResponseLimit as a preferred ceiling when the complete input leaves less
    /// capacity. Report desired and actual budgets separately; never omit input. Callers still
    /// enforce their minimum useful response before dispatching the prepared lease.</summary>
    public bool AllowResponseClamping { get; init; }

    /// <summary>Zero selects ordinary generation. A nonzero reservation requires an adapter
    /// that explicitly supports prepared reasoning. The provider's hard generation ceiling
    /// includes this reservation and the desired visible allocation; they are not separate
    /// hard output caps. Private reasoning is never returned as visible content.</summary>
    public int ReasoningTokens { get; init; }

    /// <summary>Legacy validation remains the default for every v1 and unselected request.</summary>
    public void Validate() => ValidateCore(512, 1_048_576, 1, 131_072, policyTextOnly: false);

    /// <summary>Pure shape validation against a host-selected policy. The policy value alone
    /// cannot authorize execution; the loaded adapter must verify its own selection again.</summary>
    public void Validate(ModelExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (ModelId != policy.Binding.ModelId)
            throw new ArgumentException("The generation request does not match the selected model policy.", nameof(ModelId));
        if (ResponseSharePercent is not null || ReasoningTokens != 0)
            throw new ArgumentException("The selected local text policy requires an explicit response limit and reasoning off.");
        ValidateCore(policy.MinimumContextTokens, policy.MaximumContextTokens,
            policy.MinimumVisibleOutputTokens, policy.MaximumVisibleOutputTokens, policyTextOnly: true);
    }

    private void ValidateCore(int minimumContext, int maximumContext,
        int minimumResponse, int maximumResponse, bool policyTextOnly)
    {
        ContractValidation.Identifier(ModelId, nameof(ModelId), 128);
        if (Messages is null || Messages.Count is < 1 or > 1000)
            throw new ArgumentException("Generation requires between 1 and 1000 messages.", nameof(Messages));
        if (ContextLimit < minimumContext || ContextLimit > maximumContext) throw new ArgumentOutOfRangeException(nameof(ContextLimit));
        if (ResponseLimit < minimumResponse || ResponseLimit > maximumResponse) throw new ArgumentOutOfRangeException(nameof(ResponseLimit));
        if (ResponseSharePercent is < 5 or > 50) throw new ArgumentOutOfRangeException(nameof(ResponseSharePercent));
        if (ReasoningTokens != 0 && ReasoningTokens is (< 128 or > 32768))
            throw new ArgumentOutOfRangeException(nameof(ReasoningTokens));
        if (!double.IsFinite(Temperature) || Temperature is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(Temperature));
        if (ResponseSchema is { ValueKind: not JsonValueKind.Object })
            throw new ArgumentException("Response schema must be an object.", nameof(ResponseSchema));
        if (ResponseSchema is { } schema && schema.GetRawText().Length > 262_144)
            throw new ArgumentException("Response schema exceeds the size limit.", nameof(ResponseSchema));
        var characters = 0;
        foreach (var message in Messages)
        {
            if (message is null || message.Role is not ("system" or "user" or "assistant"))
                throw new ArgumentException("A model message requires a supported role.", nameof(Messages));
            if (message.Content is null || message.Content.Count is < 1 or > 256)
                throw new ArgumentException("A model message requires bounded content parts.", nameof(Messages));
            for (var index = 0; index < message.Content.Count; index++)
            {
                var part = message.Content[index] ?? throw new ArgumentException("A content part cannot be null.", nameof(Messages));
                part.Validate(index);
                if (policyTextOnly && part is not TextContentPart)
                    throw new ArgumentException("The selected local text policy requires text-only content.", nameof(Messages));
                if (part is TextContentPart text) characters = checked(characters + text.Text.Length);
            }
        }
        if (characters > 1_048_576) throw new ArgumentException("Model input exceeds the text size limit.", nameof(Messages));
    }
}

public sealed record ModelAdapterDescriptor(
    string AdapterId, string Version, IReadOnlyList<string> ContentCapabilities, bool SupportsReasoning = false);

/// <summary>Exact preparation for one loaded instance, not an estimate based on characters or bytes.</summary>
public sealed record PreparedModelMetadata(
    string InstanceId, string AdapterId, string AdapterVersion, string ModelId,
    int InputTokens, int EffectiveContext, int ResponseBudget, string CountBasis)
{
    /// <summary>Requested allocation before reducing it to the remaining context capacity.</summary>
    public int DesiredResponseBudget { get; init; } = ResponseBudget;
    public int SafetyReserveTokens { get; init; }
    public int ReasoningReserveTokens { get; init; }
    /// <summary>Hard provider ceiling for reasoning plus visible completion tokens.</summary>
    public int CombinedGenerationBudget => checked(ResponseBudget + ReasoningReserveTokens);
    public bool IsBudgetClamped => ResponseBudget < DesiredResponseBudget;
    public int UnallocatedTokens => Math.Max(0, EffectiveContext - InputTokens - ResponseBudget - ReasoningReserveTokens - SafetyReserveTokens);
}

/// <summary>The adapter measured the fully rendered input using its actual tokenizer, but could not
/// reserve a valid response. No inference was dispatched. This is distinct from provider failures.</summary>
public sealed class ModelContextLimitException : ArgumentException
{
    public PreparedModelMetadata Metadata { get; }
    public string CountDiagnostic { get; }

    public ModelContextLimitException(PreparedModelMetadata metadata, string? countDiagnostic = null)
        : base(countDiagnostic ?? Describe(metadata))
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        CountDiagnostic = Message;
    }

    private static string Describe(PreparedModelMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return $"Prepared input ({metadata.InputTokens} tokens), requested response ({metadata.DesiredResponseBudget} tokens), " +
            $"reasoning reservation ({metadata.ReasoningReserveTokens} tokens), and safety reserve ({metadata.SafetyReserveTokens} tokens) do not fit the effective context " +
            $"({metadata.EffectiveContext} tokens). No inference was dispatched.";
    }
}

public enum ModelGenerationFinish { Stop, Length, Cancelled, Unknown }

/// <summary>Optional provider-reported prompt-cache telemetry. The fully prepared input still counts
/// against the context budget. Token counts do not measure cache memory in RAM or VRAM.</summary>
public sealed record PromptCacheMetrics(
    int PreparedInputTokens, int? CachedTokens = null, int? ProcessedPromptTokens = null,
    double? PromptProcessingMilliseconds = null, string? Diagnostic = null)
{
    public double? ReuseRatio => CachedTokens is { } cached && PreparedInputTokens > 0
        ? (double)cached / PreparedInputTokens : null;
}

/// <summary>Unknown includes interrupted or malformed streams and unconfirmed cancellation.
/// Text can be partial; only Stop is an ordinary completed response. Missing usage remains null.</summary>
public sealed record ModelGenerationResult(
    ModelGenerationFinish Finish, string Text, int? GeneratedTokens = null, int? EvaluatedTokens = null,
    bool CancellationConfirmed = false, string? Error = null)
{
    /// <summary>Null means the provider did not report telemetry; Diagnostic marks unavailable data.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PromptCacheMetrics? PromptCache { get; init; }
}

public interface IModelRuntimeAdapter
{
    ModelAdapterDescriptor Descriptor { get; }
    Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken);
}

/// <summary>A one-use preparation that holds model capacity. Dispose unused preparations promptly.
/// A cancellation request is not confirmation that inference stopped; consult the returned outcome.</summary>
public interface IPreparedModelGeneration : IAsyncDisposable
{
    PreparedModelMetadata Metadata { get; }
    Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken);
}
