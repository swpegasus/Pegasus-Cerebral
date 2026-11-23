using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.HostedProviderProtocol;

namespace Cerebral.OpenAiProviderPlugin;

/// <summary>Optional model-facing package. The default instance has no account, keys, or network activation.</summary>
public sealed class OpenAiProviderPlugin : IServicePlugin
{
    private readonly OpenAiRuntime runtime;
    public OpenAiProviderPlugin() => runtime = new(null, null);
    // Explicit injection is for the owned model-host seam and deterministic fixtures, never ambient credentials.
    public OpenAiProviderPlugin(JsonElement profile, HttpMessageHandler handler,
        Func<string, int, CancellationToken, ValueTask<string?>> resolveCredential, Action<JsonElement>? privateFragmentSink = null)
        => runtime = new(ValueContract.Profile(profile), new(handler, resolveCredential, privateFragmentSink));
    public void Initialize(IServicePluginHost host)
    {
        if (host.PluginId != "pegasus.openai-provider") throw new InvalidDataException("The provider package identity does not match.");
    }
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [runtime];
    public void Dispose() => runtime.Dispose();
}

internal sealed class OpenAiRuntime(ProviderProfile? profile, ProviderDependencies? dependencies)
    : HostedProviderRuntime(profile, dependencies)
{
    protected override string PackageId => "pegasus.openai-provider";
    protected override string ProviderId => "openai";
    protected override string Label => "OpenAI";
    protected override string ProtocolId => "openai.responses.v1";
    protected override string CountQuality => "providerReported";
    protected override Uri CountUri => new("https://api.openai.com/v1/responses/input_tokens");
    protected override Uri GenerateUri => new("https://api.openai.com/v1/responses");
    protected override void Authenticate(HttpRequestMessage message, string credential)
        => message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
    protected override int ParseCount(JsonElement response) => ValueContract.Integer(response, "input_tokens", 0, int.MaxValue);
    protected override RenderedCall Render(JsonElement request, string modelKey)
    {
        var output = request.GetProperty("output"); var format = output.GetProperty("format").GetString();
        var input = request.GetProperty("messages").EnumerateArray()
            .Select(m => (object)new { role = m.GetProperty("role").GetString(), content = m.GetProperty("text").GetString() }).ToList();
        if (format == "jsonStringCarrier")
            input.Insert(0, new { role = "system", content = JsonStringCarrier.Guidance(output.GetProperty("schema")) });
        var body = new Dictionary<string, object?>
        {
            ["model"] = modelKey, ["input"] = input, ["stream"] = true, ["store"] = false,
            ["truncation"] = "disabled", ["max_output_tokens"] = output.GetProperty("maximumTotalOutputTokens").GetInt32()
        };
        if (format != "text")
            body["text"] = new { format = new { type = "json_schema", name = "cerebral_value", strict = true,
                schema = format == "jsonStringCarrier" ? JsonStringCarrier.Schema : output.GetProperty("schema") } };
        var reasoning = output.GetProperty("reasoning"); var mode = reasoning.GetProperty("mode").GetString();
        if (mode != "default")
        {
            var effort = mode == "off" ? "none" : reasoning.GetProperty("effort").GetString();
            if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
                throw new ProviderFailure("capabilityUnsupported");
            body["reasoning"] = new { effort };
        }
        if (output.GetProperty("temperature").ValueKind != JsonValueKind.Null) body["temperature"] = output.GetProperty("temperature");
        // Count uses the exact logical input/schema/reasoning; generation-only controls are not count endpoint fields.
        var count = body.Where(p => p.Key is "model" or "input" or "text" or "reasoning" or "truncation")
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new(JsonSerializer.SerializeToElement(body), JsonSerializer.SerializeToElement(count));
    }
    protected override async Task ReadStream(Stream stream, VisibleOutput output, StreamResult result, CancellationToken token)
    {
        var created = false; var lastSequence = -1;
        var channels = new SortedDictionary<(int Output, int Content), StringBuilder>();
        var finished = new HashSet<(int Output, int Content)>();
        await foreach (var frame in BoundedSseReader.ReadAsync(stream, cancellationToken: token))
        {
            var data = ProviderJson.Parse(frame.Data);
            var type = ValueContract.Text(data, "type", 128);
            if (frame.EventName != "message" && frame.EventName != type) throw new InvalidDataException("The provider event name does not match.");
            if (data.TryGetProperty("sequence_number", out var sequence))
            {
                if (!sequence.TryGetInt32(out var number) || number <= lastSequence || number < 0) throw new InvalidDataException("The provider sequence is invalid.");
                lastSequence = number;
            }
            if (type is "response.created" or "response.in_progress")
            {
                var response = data.GetProperty("response"); BindReceipt(response, result);
                if (type == "response.created" && created) throw new InvalidDataException("The response start is duplicated.");
                created = true; continue;
            }
            if (type == "response.output_text.delta")
            {
                if (!created) throw new InvalidDataException("The response did not start.");
                var key = Channel(data); if (finished.Contains(key)) throw new InvalidDataException("The text channel already finished.");
                if (!channels.TryGetValue(key, out var text)) { if (channels.Count == 128) throw new InvalidDataException("Too many output text channels."); channels[key] = text = new(); }
                var delta = data.GetProperty("delta");
                if (delta.ValueKind != JsonValueKind.String) throw new InvalidDataException("The text delta is invalid.");
                var value = delta.GetString()!; output.Append(value); text.Append(value); continue;
            }
            if (type == "response.output_text.done")
            {
                if (!created) throw new InvalidDataException("The response did not start.");
                var key = Channel(data);
                if (!finished.Add(key) || !channels.TryGetValue(key, out var text) || data.GetProperty("text").GetString() != text.ToString())
                    throw new InvalidDataException("The completed text channel does not match its deltas.");
                continue;
            }
            if (type is "response.completed" or "response.incomplete" or "response.failed")
            {
                if (!created) throw new InvalidDataException("The response did not start.");
                var response = data.GetProperty("response"); BindReceipt(response, result);
                var status = ValueContract.Text(response, "status", 32);
                if (status != type["response.".Length..]) throw new InvalidDataException("The terminal event status does not match.");
                if (response.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
                {
                    result.InputTokens = Counter(usage, "input_tokens"); result.OutputTokens = Counter(usage, "output_tokens");
                    if (usage.TryGetProperty("output_tokens_details", out var details) && details.ValueKind != JsonValueKind.Null)
                        result.ReasoningTokens = Counter(details, "reasoning_tokens");
                    if (usage.TryGetProperty("input_tokens_details", out var inputDetails) && inputDetails.ValueKind != JsonValueKind.Null)
                        result.CacheReadTokens = Counter(inputDetails, "cached_tokens");
                }
                if (type == "response.failed")
                { result.Confirmed = true; result.Outcome = "failed"; result.FailureCode = "providerUnavailable"; return; }
                var finalText = new StringBuilder(); var refused = false;
                // Completed/incomplete response objects require output; the known failed path above may omit it.
                if (!response.TryGetProperty("output", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 128)
                    throw new InvalidDataException("The output item list is required and must be bounded.");
                {

                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.GetProperty("type").GetString() != "message") continue; // Includes private reasoning; never surfaced.
                        var content = item.GetProperty("content");
                        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() > 128) throw new InvalidDataException("The output content list is invalid.");
                        foreach (var part in content.EnumerateArray())
                        {
                            var kind = part.GetProperty("type").GetString();
                            if (kind == "output_text")
                            {
                                if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                                    throw new InvalidDataException("The output text must be a non-null string.");
                                finalText.Append(text.GetString());
                            }
                            else if (kind == "refusal") refused = true;
                            else throw new InvalidDataException("The output content kind is unsupported.");
                        }
                    }
                }
                var expected = string.Concat(channels.Values.Select(x => x.ToString()));
                if (type == "response.completed" && !refused && channels.Count > 0 &&
                    (expected != finalText.ToString() || expected != output.Text))
                    throw new InvalidDataException("The terminal text does not match streamed output.");
                if (channels.Count == 0) output.Append(finalText.ToString());
                result.Confirmed = true;
                if (refused) { result.Outcome = "refused"; result.FailureCode = "providerRefusal"; }
                else if (type == "response.completed") { result.Outcome = "completed"; result.FailureCode = null; }
                else
                {
                    var reason = response.GetProperty("incomplete_details").GetProperty("reason").GetString();
                    if (reason == "max_output_tokens") { result.Outcome = "length"; result.FailureCode = "outputLimit"; }
                    else if (reason == "content_filter") { result.Outcome = "refused"; result.FailureCode = "providerRefusal"; }
                }
                return;
            }
            if (type == "error") return; // An error event without a response terminal leaves the effect unknown.
            // Future/private semantic events are bounded by the SSE reader and discarded.
        }
    }
    private static (int, int) Channel(JsonElement value) => (ValueContract.Integer(value, "output_index", 0, 127),
        ValueContract.Integer(value, "content_index", 0, 127));
    private static void BindReceipt(JsonElement response, StreamResult result)
    {
        var id = ReceiptText(response, "id"); var model = ReceiptText(response, "model");
        if (id is null || model is null || result.ResponseId is not null && result.ResponseId != id ||
            result.ActualModel is not null && result.ActualModel != model) throw new InvalidDataException("The response identity changed.");
        result.ResponseId = id; result.ActualModel = model;
    }
}
