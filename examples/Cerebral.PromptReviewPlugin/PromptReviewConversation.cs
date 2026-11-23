using System.IO;
using System.Text.Json;
namespace Cerebral.PromptReviewPlugin;

internal sealed record PromptReviewReply(string Prompt, string Message)
{
    internal static JsonElement Schema { get; } = JsonDocument.Parse("""
        {"type":"object","properties":{"prompt":{"type":"string","minLength":1,"maxLength":16384},"message":{"type":"string","minLength":1,"maxLength":4096}},"required":["prompt","message"],"additionalProperties":false}
        """).RootElement.Clone();
    internal static PromptReviewReply Parse(string text)
    {
        if (text.Length > 24576) throw new InvalidDataException("The clarifier response exceeded its draft limit.");
        using var json = JsonDocument.Parse(text);
        var value = json.RootElement;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The clarifier returned an incomplete draft response.");
        var draft = prompt.GetString()!; var reply = message.GetString()!;
        if (string.IsNullOrWhiteSpace(draft) || draft.Length > 16384 || string.IsNullOrWhiteSpace(reply) || reply.Length > 4096)
            throw new InvalidDataException("The clarifier returned an empty or oversized draft response.");
        return new(draft, reply);
    }
}
internal sealed record PromptReviewExchange(string Speaker, string Text);
internal sealed class PromptReviewConversation
{
    private readonly List<PromptReviewExchange> messages = [];
    internal IReadOnlyList<PromptReviewExchange> Messages => messages;
    internal void Add(string speaker, string text)
    {
        if (text.Length > 4096) throw new InvalidDataException("Keep each refinement reply under 4,096 characters.");
        messages.Add(new(speaker, text));
        // Current complete draft and original request remain separate, even when older discussion rolls off.
        while (messages.Count > 8 || messages.Sum(item => item.Text.Length) > 12288) messages.RemoveAt(0);
    }
    internal string DisplayText() => ContextText(messages);
    internal static string ContextText(IReadOnlyList<PromptReviewExchange> values) =>
        string.Join("\n\n", values.Select(value => value.Speaker + ": " + value.Text));
    internal static void RequireBoundedInput(string original, string? current, string? feedback)
    {
        if (original.Length > 16384 || current?.Length > 16384 || feedback?.Length > 4096)
            throw new InvalidDataException("This refinement draft exceeds its bounded input size. Shorten the draft or correction before continuing.");
    }
}
