using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cerebral.Core;

/// <summary>Shared validation and payload shaping for provider-authored generation plans. This utility never executes a model or sink.</summary>
public static class ToolGenerationPlan
{
    public static void Validate(ToolGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Contains('\0') ||
            Encoding.UTF8.GetByteCount(request.Prompt) > 128 * 1024)
            throw new InvalidDataException("Generated tool guidance must be 1-128 KiB of text without null characters.");
        if (string.IsNullOrEmpty(request.SinkToolName) || request.SinkToolName.Length > 100 ||
            request.SinkToolName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new InvalidDataException("Generated tool sink identifiers allow 1–100 ASCII letters, digits, dots, hyphens and underscores.");
        if (string.IsNullOrWhiteSpace(request.ContentProperty) || request.ContentProperty.Length > 100 ||
            request.ContentProperty.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("Generated tool content property must be a bounded identifier.");
        if (request.MaxTokens is < 64 or > 32_768)
            throw new InvalidDataException("Generated tool response limit must be 64-32,768 tokens.");
        if (request.MaxAttempts is < 1 or > 6)
            throw new InvalidDataException("Generated tool attempts must be between one and six.");
        if (request.SinkArguments.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Generated tool sink arguments must be a JSON object.");
        if (request.SinkArguments.TryGetProperty(request.ContentProperty, out _))
            throw new InvalidDataException("Generated tool sink arguments must not pre-populate the generated content property.");
        if (Encoding.UTF8.GetByteCount(request.SinkArguments.GetRawText()) > 64 * 1024)
            throw new InvalidDataException("Generated tool sink arguments exceed 64 KiB before content is added.");
    }

    public static JsonElement WithContent(ToolGenerationRequest request, string content)
    {
        Validate(request);
        var node = JsonNode.Parse(request.SinkArguments.GetRawText())?.AsObject()
            ?? throw new InvalidDataException("Generated tool sink arguments must be a JSON object.");
        node[request.ContentProperty] = content;
        return CerebralJson.Element(node);
    }

    public static JsonElement ActivityArguments(ToolGenerationRequest request, JsonElement arguments, int characterCount)
    {
        var node = JsonNode.Parse(arguments.GetRawText())?.AsObject()
            ?? throw new InvalidDataException("Generated tool sink arguments must be a JSON object.");
        node[request.ContentProperty] = $"[generated payload omitted from active context: {characterCount:N0} characters]";
        return CerebralJson.Element(node);
    }

    public static int AttemptTokenLimit(ToolGenerationRequest request, int attempt)
    {
        Validate(request);
        if (attempt < 1 || attempt > request.MaxAttempts) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (!request.SinkArguments.TryGetProperty("maximumWords", out var maximum) ||
            maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetInt32(out var maximumWords) || maximumWords < 1)
            return request.MaxTokens;

        // A declared word ceiling is a sink contract, not a target. Leave ordinary generation untouched,
        // but keep bounded prose comfortably below a ceiling before the validated sink has to reject it.
        if (attempt == 1) return request.MaxTokens;
        // The retry prompt assigns a smaller word range. Budget against that range rather than turning the sink's
        // hard ceiling into a tight token ceiling: a completion that is cut off cannot satisfy the range even when
        // it would otherwise have been short enough. The validated sink remains authoritative for the actual words.
        var minimumWords = request.SinkArguments.TryGetProperty("minimumWords", out var minimum) &&
            minimum.ValueKind == JsonValueKind.Number && minimum.TryGetInt32(out var configuredMinimum) && configuredMinimum > 0
                ? configuredMinimum : 1;
        var retryFraction = attempt == 2 ? 0.65 : 0.50;
        var retryTarget = Math.Clamp((int)Math.Floor(maximumWords * retryFraction), minimumWords, maximumWords);
        var retryUpper = Math.Min(maximumWords, Math.Max(minimumWords, retryTarget + 75));
        // English prose generally needs well below two tokens per word. A three-token allowance let small models
        // ignore the smaller retry range and still overrun the validated word ceiling by hundreds of words. Keep a
        // two-token allowance so the model can finish punctuation and names while the sink remains the exact arbiter.
        return Math.Clamp(Math.Min(request.MaxTokens, retryUpper * 2), 64, request.MaxTokens);
    }

    public static string RetryGuidance(ToolGenerationRequest request, string rejection, int attempt = 2)
    {
        if (attempt < 2 || attempt > request.MaxAttempts) throw new ArgumentOutOfRangeException(nameof(attempt));
        rejection = string.IsNullOrWhiteSpace(rejection) ? "The validated sink rejected the payload." : rejection.Trim();
        var ceiling = "";
        if (request.SinkArguments.TryGetProperty("maximumWords", out var maximum) &&
            maximum.ValueKind == JsonValueKind.Number && maximum.TryGetInt32(out var maximumWords) && maximumWords > 0)
        {
            var retryFraction = attempt == 2 ? 0.65 : 0.50;
            var minimumWords = request.SinkArguments.TryGetProperty("minimumWords", out var minimum) &&
                minimum.ValueKind == JsonValueKind.Number && minimum.TryGetInt32(out var configuredMinimum) && configuredMinimum > 0
                    ? configuredMinimum : 1;
            var retryTarget = Math.Clamp((int)Math.Floor(maximumWords * retryFraction), minimumWords, maximumWords);
            var floor = minimumWords > 1 ? $" The complete payload must also contain at least {minimumWords:N0} words." : "";
            var retryUpper = Math.Min(maximumWords, Math.Max(minimumWords, retryTarget + 75));
            ceiling = $" The sink has a hard ceiling of {maximumWords:N0} words.{floor} For this retry, write about {retryTarget:N0} words and stop no later than {retryUpper:N0} words. Treat {retryUpper:N0} as the retry's generation limit, finish the replacement cleanly inside that smaller range, and never approach the sink ceiling.";
        }
        return $"""

            The previous generated payload was rejected by the validated sink:
            {rejection}
            Start the payload again as a fresh complete replacement. Correct the stated failure, materially shorten or restructure it when required, and do not repeat or lightly edit the rejected payload.{ceiling}
            Return only the new payload.
            """;
    }
}

