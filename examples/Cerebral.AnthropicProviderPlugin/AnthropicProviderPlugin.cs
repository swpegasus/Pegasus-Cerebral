using System.Text.Json;
using Cerebral.Core;
using Cerebral.HostedProviderProtocol;

namespace Cerebral.AnthropicProviderPlugin;

public sealed class AnthropicProviderPlugin : IServicePlugin
{
    private readonly AnthropicRuntime runtime;
    public AnthropicProviderPlugin() => runtime = new(null, null);
    public AnthropicProviderPlugin(JsonElement profile, HttpMessageHandler handler,
        Func<string, int, CancellationToken, ValueTask<string?>> resolveCredential, Action<JsonElement>? privateFragmentSink = null)
        => runtime = new(ValueContract.Profile(profile), new(handler, resolveCredential, privateFragmentSink));
    public void Initialize(IServicePluginHost host)
    {
        if (host.PluginId != "pegasus.anthropic-provider") throw new InvalidDataException("The provider package identity does not match.");
    }
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [runtime];
    public void Dispose() => runtime.Dispose();
}

internal sealed class AnthropicRuntime(ProviderProfile? profile, ProviderDependencies? dependencies)
    : HostedProviderRuntime(profile, dependencies)
{
    protected override string PackageId => "pegasus.anthropic-provider";
    protected override string ProviderId => "anthropic";
    protected override string Label => "Anthropic";
    protected override string ProtocolId => "anthropic.messages.v1";
    protected override string CountQuality => "providerEstimate";
    protected override Uri CountUri => new("https://api.anthropic.com/v1/messages/count_tokens");
    protected override Uri GenerateUri => new("https://api.anthropic.com/v1/messages");
    protected override void Authenticate(HttpRequestMessage message, string credential)
    {
        message.Headers.Add("x-api-key", credential);
        message.Headers.Add("anthropic-version", "2023-06-01");
    }
    protected override int ParseCount(JsonElement response) => ValueContract.Integer(response, "input_tokens", 0, int.MaxValue);
    protected override RenderedCall Render(JsonElement request, string modelKey)
    {
        var system = new List<object>(); var messages = new List<object>(); var seenMessage = false;
        var output = request.GetProperty("output"); var format = output.GetProperty("format").GetString();
        if (format == "jsonStringCarrier") system.Add(new { type = "text", text = JsonStringCarrier.Guidance(output.GetProperty("schema")) });
        foreach (var message in request.GetProperty("messages").EnumerateArray())
        {
            var role = message.GetProperty("role").GetString(); var text = message.GetProperty("text").GetString();
            if (role == "system")
            {
                if (seenMessage) throw new ProviderFailure("capabilityUnsupported"); // No silent role relocation.
                system.Add(new { type = "text", text });
            }
            else { seenMessage = true; messages.Add(new { role, content = new[] { new { type = "text", text } } }); }
        }
        if (messages.Count == 0) throw new ProviderFailure("capabilityUnsupported");
        var body = new Dictionary<string, object?>
        { ["model"] = modelKey, ["messages"] = messages, ["max_tokens"] = output.GetProperty("maximumTotalOutputTokens").GetInt32(), ["stream"] = true };
        if (system.Count > 0) body["system"] = system;
        var outputConfig = new Dictionary<string, object?>();
        if (format != "text") outputConfig["format"] = new { type = "json_schema",
            schema = format == "jsonStringCarrier" ? JsonStringCarrier.Schema : output.GetProperty("schema") };
        var reasoning = output.GetProperty("reasoning"); var mode = reasoning.GetProperty("mode").GetString();
        if (mode == "off") body["thinking"] = new { type = "disabled" };
        else if (mode == "effort")
        {
            var effort = reasoning.GetProperty("effort").GetString();
            if (effort is not ("low" or "medium" or "high" or "xhigh" or "max")) throw new ProviderFailure("capabilityUnsupported");
            body["thinking"] = new { type = "adaptive" }; outputConfig["effort"] = effort;
        }
        if (outputConfig.Count > 0) body["output_config"] = outputConfig;
        if (output.GetProperty("temperature").ValueKind != JsonValueKind.Null)
        {
            if (output.GetProperty("temperature").GetDouble() > 1 || mode == "effort") throw new ProviderFailure("capabilityUnsupported");
            body["temperature"] = output.GetProperty("temperature");
        }
        var count = body.Where(p => p.Key is "model" or "messages" or "system" or "thinking" or "output_config")
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new(JsonSerializer.SerializeToElement(body), JsonSerializer.SerializeToElement(count));
    }
    protected override async Task ReadStream(Stream stream, VisibleOutput output, StreamResult result, CancellationToken token)
    {
        var started = false; var sawMessageDelta = false; var nextIndex = 0; int? activeIndex = null; string? activeType = null, stopReason = null;
        int? uncachedInput = null;
        await foreach (var frame in BoundedSseReader.ReadAsync(stream, cancellationToken: token))
        {
            var data = ProviderJson.Parse(frame.Data); var type = ValueContract.Text(data, "type", 128);
            if (frame.EventName != "message" && frame.EventName != type) throw new InvalidDataException("The provider event name does not match.");
            switch (type)
            {
                case "ping": break;
                case "message_start":
                    if (started) throw new InvalidDataException("The message start is duplicated.");
                    var message = data.GetProperty("message");
                    if (message.GetProperty("type").GetString() != "message" || message.GetProperty("role").GetString() != "assistant")
                        throw new InvalidDataException("The message identity is invalid.");
                    result.ResponseId = ReceiptText(message, "id") ?? throw new InvalidDataException("The message ID is required.");
                    result.ActualModel = ReceiptText(message, "model") ?? throw new InvalidDataException("The message model is required.");
                    var initial = message.GetProperty("content");
                    if (initial.ValueKind != JsonValueKind.Array || initial.GetArrayLength() != 0) throw new InvalidDataException("Unexpected initial message content.");
                    UpdateUsage(message.GetProperty("usage"), result, ref uncachedInput); started = true; break;
                case "content_block_start":
                    if (!started || sawMessageDelta || activeIndex is not null) throw new InvalidDataException("The content block start is out of order.");
                    var index = ValueContract.Integer(data, "index", 0, 127);
                    if (index != nextIndex++) throw new InvalidDataException("The content block index is out of order.");
                    var block = data.GetProperty("content_block"); activeType = ValueContract.Text(block, "type", 64); activeIndex = index;
                    if (activeType == "text") output.Append(block.GetProperty("text").GetString() ?? throw new InvalidDataException("The block text is invalid."));
                    else if (activeType is not ("thinking" or "redacted_thinking")) throw new ProviderFailure("capabilityUnsupported");
                    break;
                case "content_block_delta":
                    RequireBlock(data, started, activeIndex);
                    var delta = data.GetProperty("delta"); var deltaType = ValueContract.Text(delta, "type", 64);
                    if (deltaType == "text_delta")
                    {
                        if (activeType != "text") throw new InvalidDataException("A text delta targeted a private block.");
                        output.Append(delta.GetProperty("text").GetString() ?? throw new InvalidDataException("The text delta is invalid."));
                    }
                    else if (deltaType is "thinking_delta" or "signature_delta")
                    {
                        if (activeType is not ("thinking" or "redacted_thinking")) throw new InvalidDataException("A private delta targeted a text block.");
                        // Private thinking and signature values are not read, emitted, or retained.
                    }
                    break;
                case "content_block_stop":
                    RequireBlock(data, started, activeIndex); activeIndex = null; activeType = null; break;
                case "message_delta":
                    if (!started || activeIndex is not null) throw new InvalidDataException("The message delta is out of order.");
                    var messageDelta = data.GetProperty("delta");
                    if (messageDelta.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The message delta is invalid.");
                    // Top-level deltas can update cumulative usage before or after the final stop.
                    // Null/omitted stop fields do not erase an observed final reason.
                    if (messageDelta.TryGetProperty("stop_reason", out var stopping) && stopping.ValueKind != JsonValueKind.Null)
                    {
                        var nextReason = ValueContract.Enum(messageDelta, "stop_reason", "end_turn", "max_tokens", "stop_sequence",
                            "tool_use", "pause_turn", "refusal", "model_context_window_exceeded");
                        if (stopReason is not null && stopReason != nextReason) throw new InvalidDataException("The message stop reason changed.");
                        stopReason = nextReason;
                    }
                    UpdateUsage(data.GetProperty("usage"), result, ref uncachedInput); sawMessageDelta = true; break;
                case "message_stop":
                    if (!started || !sawMessageDelta || stopReason is null || activeIndex is not null) throw new InvalidDataException("The message terminal is out of order.");
                    result.Confirmed = true;
                    switch (stopReason)
                    {
                        case "end_turn": result.Outcome = "completed"; result.FailureCode = null; break;
                        case "max_tokens": result.Outcome = "length"; result.FailureCode = "outputLimit"; break;
                        case "refusal": result.Outcome = "refused"; result.FailureCode = "providerRefusal"; break;
                        case "tool_use": result.Outcome = "failed"; result.FailureCode = "capabilityUnsupported"; break;
                        // pause_turn/unknown stops cannot be treated as complete or replayed.
                    }
                    return;
                case "error": return;
                // Unknown future events remain bounded; they cannot establish a terminal.
            }
        }
    }
    private static void RequireBlock(JsonElement value, bool started, int? activeIndex)
    {
        if (!started || activeIndex is null || ValueContract.Integer(value, "index", 0, 127) != activeIndex)
            throw new InvalidDataException("The content block event is out of order.");
    }
    private static void UpdateUsage(JsonElement usage, StreamResult result, ref int? uncachedInput)
    {
        if (usage.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The usage value is invalid.");
        if (usage.TryGetProperty("input_tokens", out _)) uncachedInput = Counter(usage, "input_tokens");
        if (usage.TryGetProperty("output_tokens", out _)) result.OutputTokens = Counter(usage, "output_tokens");
        if (usage.TryGetProperty("cache_read_input_tokens", out _)) result.CacheReadTokens = Counter(usage, "cache_read_input_tokens");
        if (usage.TryGetProperty("cache_creation_input_tokens", out _)) result.CacheWriteTokens = Counter(usage, "cache_creation_input_tokens");
        result.InputTokens = uncachedInput is { } input && result.CacheReadTokens is { } read && result.CacheWriteTokens is { } write
            ? checked(input + read + write) : null;
        // Snapshots are cumulative. Replacing observed counters avoids double-counting each delta.
    }
}
