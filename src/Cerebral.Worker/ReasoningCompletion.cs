using System.Text;
using System.Text.Json;

namespace Cerebral.Worker;

/// <summary>llama.cpp's template-aware chat endpoint separates private reasoning from structured actions.</summary>
public static class ReasoningCompletion
{
    public static Dictionary<string, object?> CreateRequest(IReadOnlyList<ModelMessage> messages, int maxTokens,
        double temperature, bool json, JsonElement? schema, int reasoningTokens, IReadOnlyList<string> stops)
    {
        if (reasoningTokens is < 128 or > 32768) throw new ArgumentException("Reasoning budget must be between 128 and 32768 tokens.");
        if (schema is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Response schema must be a JSON object.");
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages, ["max_tokens"] = checked(maxTokens + reasoningTokens),
            ["temperature"] = temperature, ["stream"] = true,
            ["chat_template_kwargs"] = new { enable_thinking = true },
            ["reasoning_format"] = "deepseek", ["reasoning_budget_tokens"] = reasoningTokens
        };
        if (stops.Count > 0) body["stop"] = stops;
        if (schema.HasValue) body["response_format"] = new { type = "json_schema", json_schema = new { schema = schema.Value } };
        else if (json) body["response_format"] = new { type = "json_object" };
        return body;
    }

    public static async Task<string> ReadAsync(TextReader reader, bool requireJson, Action<string>? onContent, CancellationToken token)
    {
        var result = new StringBuilder(); string? finishReason = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(token);
            if (line is null) break;
            if (line.Length > 2_000_000) throw new IOException("Model stream frame exceeded the output bound.");
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].TrimStart(); if (data == "[DONE]") break;
            JsonDocument frame;
            try { frame = JsonDocument.Parse(data); }
            catch (JsonException) { throw new IOException("Model returned an invalid chat stream frame; partial output was not adopted."); }
            using (frame)
            {
                var root = frame.RootElement;
                // Never include the raw response in errors: it may contain private reasoning.
                if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) throw new IOException("Model chat request failed; partial output was not adopted.");
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (choice.ValueKind != JsonValueKind.Object) throw new IOException("Model returned an invalid chat choice.");
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String) finishReason = finish.GetString();
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    result.Append(content.GetString());
                    if (result.Length > 2_000_000) throw new IOException("Model response exceeded the output bound.");
                }
                // delta.reasoning_content is intentionally neither buffered nor forwarded.
                if (finishReason is not null) break;
            }
        }
        if (finishReason == "length") throw new IOException("Model reached the combined reasoning/response token limit. Increase the configured budget; partial output was not adopted.");
        if (finishReason != "stop") throw new IOException("Model chat stream ended without a successful completion marker. Partial output was not adopted.");
        var text = result.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new IOException("Model completed without visible content; verify this model supports reasoning separation and structured output.");
        if (requireJson)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            }
            catch (JsonException) { throw new IOException("Model did not return one structured JSON object. Check reasoning/template compatibility; partial output was not adopted."); }
        }
        else if (text.Contains("<think>", StringComparison.OrdinalIgnoreCase) || text.Contains("</think>", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Model returned unseparated reasoning. Disable reasoning or use a compatible model/template.");
        // Publish only adopted visible content, never partially parsed reasoning or malformed actions.
        onContent?.Invoke(text); return text;
    }
}
