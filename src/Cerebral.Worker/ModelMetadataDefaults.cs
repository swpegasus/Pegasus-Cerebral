using System.Text.RegularExpressions;
using Cerebral.Core;

namespace Cerebral.Worker;

internal static partial class ModelMetadataDefaults
{
    // Several older Llama 2 instruction merges ship without tokenizer.chat_template even though
    // their publisher specifies Alpaca. Keep this bounded fallback explicit and editable in Advanced.
    private const string AlpacaTemplate = "{% for message in messages %}{% if message['role'] == 'system' %}{{ message['content'] + '\n\n' }}{% elif message['role'] == 'user' %}{{ 'Below is an instruction that describes a task. Write a response that appropriately completes the request.\n\n### Instruction:\n' + message['content'] + '\n\n### Response:\n' }}{% elif message['role'] == 'assistant' %}{{ message['content'] + '\n\n' }}{% endif %}{% endfor %}";

    public static ModelRecord Apply(ModelRecord model, GgufModelMetadata metadata, bool preserveConfiguredContext, bool preserveDisplayName)
    {
        var notes = new List<string>();
        var identity = metadata.Name + " " + Path.GetFileNameWithoutExtension(model.Path);
        if (!preserveDisplayName && !string.IsNullOrWhiteSpace(metadata.Name)) model.Name = metadata.Name;
        model.Architecture = metadata.Architecture ?? "Unknown";
        model.DeclaredType = metadata.DeclaredType;
        model.Quantization = metadata.Quantization ?? "Unknown";
        model.ParameterCount = metadata.ParameterCount is <= long.MaxValue ? (long?)metadata.ParameterCount : null;
        model.NativeContextTokens = metadata.NativeContextTokens;
        model.RecommendedContextTokens = RecommendContext(metadata.NativeContextTokens);
        model.BlockCount = metadata.BlockCount;
        model.EmbeddingLength = metadata.EmbeddingLength;
        model.AttentionHeadCount = metadata.HeadCount;
        model.AttentionHeadCountKv = metadata.KvHeadCount;
        model.HasEmbeddedChatTemplate = metadata.HasEmbeddedChatTemplate;
        var authoritativePurpose = GgufMetadataInspector.PurposeForDeclaredType(metadata.DeclaredType);
        var detectedPurpose = authoritativePurpose ?? metadata.Purpose;
        model.Purpose = authoritativePurpose is null && IsTextPurpose(detectedPurpose) && RoleplayPattern().IsMatch(identity)
            ? "Creative writing"
            : detectedPurpose;
        model.MetadataSource = metadata.UsesFilenameEstimate ? "gguf + filename estimate"
            : metadata.UsesMetadataEstimate ? "gguf + metadata estimate" : "gguf";

        if (!preserveConfiguredContext) model.ContextTokens = model.RecommendedContextTokens;
        else if (metadata.NativeContextTokens is { } native && model.ContextTokens > native)
            notes.Add($"Configured context ({model.ContextTokens:N0}) exceeds the GGUF native context ({native:N0}); adjust only if the model's publisher documents scaling.");

        if (!metadata.HasEmbeddedChatTemplate && string.IsNullOrWhiteSpace(model.ChatTemplate))
        {
            if (AlpacaFallbackPattern().IsMatch(identity))
            {
                model.ChatTemplate = AlpacaTemplate;
                if (model.StopSequences.Count == 0) model.StopSequences = ["### Instruction:"];
                notes.Add("No embedded chat template was found. Cerebral inferred the publisher's Alpaca format from this model family; it remains editable in Advanced.");
                model.MetadataSource = "gguf + filename estimate";
            }
            else notes.Add("No embedded chat template was found. Choose the publisher's chat template in Advanced before using this for Cerebral.");
        }
        if (metadata.NativeContextTokens is null)
            notes.Add("The GGUF did not declare a native context length; Cerebral kept the conservative 4,096-token recommendation.");
        if (metadata.KvShapeWarning is { } shapeWarning) notes.Add(shapeWarning);
        if (model.Purpose is "Adapter" or "Vision projector" or "Embedding" or "Reranking" or "Audio" or "Vision")
            notes.Add($"Detected purpose '{model.Purpose}' is not supported as a text Cerebral model by the current runtime adapter.");
        model.MetadataWarning = notes.Count == 0 ? null : string.Join(" ", notes);
        ModelCapabilityPolicy.Refresh(model);
        return model;
    }

    public static DiscoveredModel Apply(DiscoveredModel model, GgufModelMetadata metadata)
    {
        model.Name = metadata.Name;
        model.Architecture = metadata.Architecture ?? "Unknown";
        model.DeclaredType = metadata.DeclaredType;
        model.Quantization = metadata.Quantization ?? "Unknown";
        model.NativeContextTokens = metadata.NativeContextTokens;
        var authoritativePurpose = GgufMetadataInspector.PurposeForDeclaredType(metadata.DeclaredType);
        var detectedPurpose = authoritativePurpose ?? metadata.Purpose;
        model.Purpose = authoritativePurpose is null && IsTextPurpose(detectedPurpose) && RoleplayPattern().IsMatch(metadata.Name + " " + Path.GetFileNameWithoutExtension(model.Path))
            ? "Creative writing"
            : detectedPurpose;
        model.HasEmbeddedChatTemplate = metadata.HasEmbeddedChatTemplate;
        if (!metadata.HasEmbeddedChatTemplate)
            model.MetadataWarning = AlpacaFallbackPattern().IsMatch(metadata.Name + " " + Path.GetFileNameWithoutExtension(model.Path))
                ? "No embedded template; Cerebral can apply the recognized Alpaca family fallback on import."
                : "No embedded chat template; publisher guidance is required before Cerebral use.";
        if (metadata.KvShapeWarning is { } shapeWarning)
            model.MetadataWarning = string.IsNullOrWhiteSpace(model.MetadataWarning)
                ? shapeWarning : model.MetadataWarning + " " + shapeWarning;
        return model;
    }

    private static int RecommendContext(int? native)
    {
        var ceiling = Math.Clamp(native ?? 4096, 512, 16384);
        foreach (var tier in new[] { 16384, 8192, 4096, 2048, 1024, 512 })
            if (tier <= ceiling) return tier;
        return 512;
    }

    private static bool IsTextPurpose(string purpose) => purpose is not ("Adapter" or "Vision projector" or "Embedding" or "Reranking" or "Audio" or "Vision");

    [GeneratedRegex(@"\b(?:unholy|utopia)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlpacaFallbackPattern();
    [GeneratedRegex(@"\b(?:roleplay|role[-_ ]?play|rp|story|storytelling|writer|writing|lewd|emaid|utopia)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleplayPattern();
}
