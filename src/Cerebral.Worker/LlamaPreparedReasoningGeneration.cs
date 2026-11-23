using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>One immutable, measured chat request. Adopted callbacks remain terminal-only;
/// an optional public-channel draft is provisional. Provider reasoning is never forwarded.</summary>
internal sealed class LlamaPreparedReasoningGeneration : IPreparedModelGeneration, IProviderGenerationTelemetry, IBoundGenerationDraft
{
    internal static ModelAdapterDescriptor Adapter { get; } = new("llama.cpp.prepared.reasoning.text", "1.0.0",
        Array.AsReadOnly(new[] { "text" }), SupportsReasoning: true);
    private readonly HttpClient http;
    private readonly LlamaSlotPool.Lease slot;
    private readonly Func<bool> exited;
    private readonly bool requireJson;
    private readonly LlamaProtocolTiming timing;
    private readonly CancellationTokenSource disposal = new();
    private readonly object gate = new();
    private readonly GenerationDraftChannel draft = new();
    private JsonElement body;
    private Task<ModelGenerationResult>? generation;
    private bool disposed;
    public PreparedModelMetadata Metadata { get; }
    private Action<ProviderGenerationMeasurement>? telemetryObserver;
    public void SetTelemetryObserver(Action<ProviderGenerationMeasurement>? observer) => telemetryObserver = observer;
    public bool TryBindDraft(GenerationDraftBinding binding, Action<GenerationDraftSample> observer)
    {
        lock (gate)
        {
            if (disposed || generation is not null) return false;
            return draft.TryBind(binding, observer);
        }
    }

    private LlamaPreparedReasoningGeneration(HttpClient http, LlamaSlotPool.Lease slot, Func<bool> exited,
        JsonElement body, bool requireJson, PreparedModelMetadata metadata, LlamaProtocolTiming timing)
    {
        this.http = http; this.slot = slot; this.exited = exited; this.body = body;
        this.requireJson = requireJson; Metadata = metadata; this.timing = timing;
    }

    internal static async Task<IPreparedModelGeneration> PrepareAsync(HttpClient http, LlamaSlotPool.Lease slot,
        string instanceId, ModelGenerationRequest request, IReadOnlyList<string> stopSequences, Func<bool> exited,
        CancellationToken token, LlamaProtocolTiming? timing = null)
    {
        try
        {
            // Snapshot before the first await, including the schema's JsonDocument lifetime.
            var messages = LlamaPreparedGeneration.TextSnapshot(request);
            if (request.ReasoningTokens is < 128 or > 32768)
                throw new ArgumentException("Prepared reasoning requires a bounded reasoning reservation.");
            var schema = request.ResponseSchema?.Clone(); var stops = stopSequences.ToArray();
            if (stops.Length > 128 || stops.Any(stop => stop is null || stop.Length > 4096 || stop.Contains('\0')))
                throw new ArgumentException("Configured model stop sequences exceed their bounds.");
            var values = ReasoningCompletion.CreateRequest(messages, request.ResponseLimit, request.Temperature,
                false, schema, request.ReasoningTokens, stops);
            values["id_slot"] = slot.Id; values["return_progress"] = true; values["n"] = 1;
            values["cache_prompt"] = true;
            values["timings_per_token"] = true;
            values["stream_options"] = new { include_usage = true };
            var frozen = JsonSerializer.SerializeToElement(values, CerebralJson.Options);
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
            preparation.CancelAfter(TimeSpan.FromSeconds(60));
            var status = await LlamaProtocol.SlotAsync(http, slot.Id, preparation.Token);
            if (status.Processing)
            {
                slot.Quarantine("Reserved reasoning slot was already occupied. Explicitly unload and reload the model.");
                throw new IOException("The reserved reasoning slot was unexpectedly occupied; capacity was quarantined.");
            }
            var count = await CountAsync(http, frozen, preparation.Token);
            var metadata = LlamaPreparedGeneration.ResolveBudget(instanceId, Adapter, request, count,
                Math.Min(request.ContextLimit, status.Context),
                "Exact llama.cpp chat input_tokens for the immutable schema/template/body; terminal prompt_tokens must agree.");
            if (frozen.GetProperty("max_tokens").GetInt32() != metadata.CombinedGenerationBudget)
            {
                values["max_tokens"] = metadata.CombinedGenerationBudget;
                frozen = JsonSerializer.SerializeToElement(values, CerebralJson.Options);
                if (await CountAsync(http, frozen, preparation.Token) != count)
                    throw new LlamaProtocolException("Changing the combined generation limit changed the prepared input count; no inference was dispatched.");
            }
            return new LlamaPreparedReasoningGeneration(http, slot, exited, frozen, schema.HasValue, metadata,
                timing ?? LlamaProtocolTiming.Default);
        }
        catch { slot.Dispose(); throw; }
    }

    private static async Task<int> CountAsync(HttpClient http, JsonElement body, CancellationToken token)
    {
        using var result = await LlamaProtocol.PostAsync(http, "v1/chat/completions/input_tokens", body, token);
        var root = result.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("object", out var kind) ||
            kind.ValueKind != JsonValueKind.String || kind.GetString() != "response.input_tokens" ||
            LlamaProtocol.ReadCount(root, "input_tokens") is not { } count || count is < 1 or > 1_048_576)
            throw new LlamaProtocolException("The runtime did not return an exact bounded chat input count.");
        return count;
    }

    public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken)
    {
        TaskCompletionSource<ModelGenerationResult> done;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (generation is not null) throw new InvalidOperationException("A prepared generation can be used only once.");
            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = done.Task;
            draft.Start();
        }
        _ = CompleteGenerationAsync(done, onText, cancellationToken);
        return done.Task;
    }

    private async Task CompleteGenerationAsync(TaskCompletionSource<ModelGenerationResult> done,
        Action<string>? onText, CancellationToken token)
    {
        try { done.TrySetResult(await GenerateCoreAsync(onText, token).ConfigureAwait(false)); }
        catch (OperationCanceledException error)
        { draft.Complete(accepted: false); done.TrySetCanceled(error.CancellationToken); }
        catch (Exception error)
        { draft.Complete(accepted: false); done.TrySetException(error); }
    }

    private async Task<ModelGenerationResult> GenerateCoreAsync(Action<string>? onText, CancellationToken token)
    {
        using var intent = CancellationTokenSource.CreateLinkedTokenSource(token, disposal.Token);
        using var transport = new CancellationTokenSource();
        var acknowledged = 0; var sent = false; var rejected = false;
        var visible = new StringBuilder();
        ModelGenerationResult result = new(ModelGenerationFinish.Unknown, "", Error: "Reasoning stream ended without a terminal receipt and usage.");
        using var cancel = intent.Token.Register(() =>
        {
            // /slots can overtake queued inference. Only an actual completion frame establishes
            // admission; headers or pre-admission idle status cannot release this exclusive lease.
            if (Volatile.Read(ref acknowledged) != 0) transport.Cancel();
            else transport.CancelAfter(timing.AcceptanceAfterCancellation);
        });
        try
        {
            intent.Token.ThrowIfCancellationRequested();
            if (exited()) throw new IOException("The prepared model instance is no longer running.");
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
                { Content = JsonContent.Create(body, options: CerebralJson.Options) };
            intent.Token.ThrowIfCancellationRequested(); sent = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transport.Token);
            if (!response.IsSuccessStatusCode)
            {
                using var error = await LlamaProtocol.ReadJsonAsync(response.Content, 65_536, transport.Token);
                rejected = error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out _);
                result = result with { Error = "Model reasoning request failed with HTTP " + (int)response.StatusCode + ". Verify template/schema compatibility." };
            }
            else
            {
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    throw new LlamaProtocolException("Model did not return a reasoning inference event stream.");
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(transport.Token));
                var lines = new LlamaProtocol.BoundedLines(reader);
                string? completionId = null, finish = null;
                int? input = null, generated = null;
                PromptCacheMetrics? promptCache = null;
                var terminal = false; var usageReceived = false; long streamCharacters = 0;
                while (await lines.ReadAsync(transport.Token) is { } line)
                {
                    streamCharacters += line.Length;
                    if (streamCharacters > LlamaProtocol.MaximumPreparationCharacters)
                        throw new LlamaProtocolException("Reasoning stream exceeded its bounded protocol size.");
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line[5..].TrimStart();
                    if (data == "[DONE]") break;
                    using var frame = JsonDocument.Parse(data); var root = frame.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                        throw new LlamaProtocolException("Reasoning inference returned an invalid or error frame.");
                    UniqueFields(root, optionalTelemetry: "timings");
                    if (!root.TryGetProperty("id", out var identity) || identity.ValueKind != JsonValueKind.String ||
                        identity.GetString() is not { Length: > 0 and <= 256 } candidate ||
                        !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                        choices.GetArrayLength() > 1)
                        throw new LlamaProtocolException("Reasoning frame did not identify one bounded completion.");
                    if (completionId is not null && completionId != candidate)
                        throw new LlamaProtocolException("Reasoning stream changed completion identity.");
                    completionId = candidate;
                    ProviderGenerationTelemetry.Observe(telemetryObserver, root);
                    if (choices.GetArrayLength() == 0)
                    {
                        if (!terminal || usageReceived || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                            throw new LlamaProtocolException("Reasoning usage did not follow exactly one terminal receipt.");
                        UniqueFields(usage, optionalTelemetry: "prompt_tokens_details");
                        input = LlamaProtocol.ReadCount(usage, "prompt_tokens");
                        generated = LlamaProtocol.ReadCount(usage, "completion_tokens");
                        var total = LlamaProtocol.ReadCount(usage, "total_tokens");
                        if (input is null || generated is null || total is null || (long)input + generated != total)
                            throw new LlamaProtocolException("Reasoning terminal usage was missing or inconsistent.");
                        usageReceived = true;
                        promptCache = LlamaProtocol.MergePromptCache(promptCache,
                            LlamaProtocol.ReadPromptCache(root, Metadata.InputTokens), Metadata.InputTokens);
                        result = new(ModelGenerationFinish.Unknown, visible.ToString(), generated, input,
                            Error: "Reasoning terminal output has not yet been adopted.") { PromptCache = promptCache };
                        continue;
                    }
                    if (terminal) throw new LlamaProtocolException("Reasoning stream emitted another choice after completion.");
                    var choice = choices[0];
                    if (choice.ValueKind != JsonValueKind.Object) throw new LlamaProtocolException("Reasoning stream returned an invalid choice.");
                    UniqueFields(choice);
                    if (!choice.TryGetProperty("index", out var index) || !index.TryGetInt32(out var ordinal) || ordinal != 0 ||
                        !choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                        throw new LlamaProtocolException("Reasoning stream did not identify the selected single choice.");
                    UniqueFields(delta);
                    if (delta.TryGetProperty("tool_calls", out _))
                        throw new LlamaProtocolException("Reasoning stream returned provider tool calls instead of the requested visible action.");
                    string? draftFragment = null;
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                    {
                        if (content.ValueKind != JsonValueKind.String) throw new LlamaProtocolException("Reasoning visible content was invalid.");
                        var fragment = content.GetString()!;
                        if (visible.Length + fragment.Length > LlamaProtocol.MaximumFrameCharacters)
                            throw new LlamaProtocolException("Reasoning visible output exceeded its size limit.");
                        visible.Append(fragment); draftFragment = fragment;
                    }
                    // delta.reasoning_content is neither buffered, forwarded nor retained.
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                    {
                        if (reason.ValueKind != JsonValueKind.String) throw new LlamaProtocolException("Reasoning finish receipt was invalid.");
                        finish = reason.GetString(); terminal = true;
                        promptCache = LlamaProtocol.MergePromptCache(promptCache,
                            LlamaProtocol.ReadPromptCache(root, Metadata.InputTokens), Metadata.InputTokens);
                    }
                    Volatile.Write(ref acknowledged, 1);
                    if (intent.IsCancellationRequested && !terminal) { transport.Cancel(); transport.Token.ThrowIfCancellationRequested(); }
                    // Only the validated selected public channel reaches the optional parser tap.
                    if (!intent.IsCancellationRequested && draftFragment is not null) draft.Append(draftFragment);
                }
                if (terminal && usageReceived)
                {
                    var outcome = finish switch { "stop" => ModelGenerationFinish.Stop, "length" => ModelGenerationFinish.Length, _ => ModelGenerationFinish.Unknown };
                    result = new(outcome, visible.ToString(), generated, input,
                        Error: outcome == ModelGenerationFinish.Unknown ? "Reasoning returned an unsupported finish reason." : null)
                        { PromptCache = promptCache };
                    if (input != Metadata.InputTokens)
                        result = result with { Finish = ModelGenerationFinish.Unknown, Error = "Reasoning input count changed between preparation and generation; output was not adopted." };
                    else if (generated > Metadata.CombinedGenerationBudget)
                        result = result with { Finish = ModelGenerationFinish.Unknown, Error = "Reasoning output exceeded the prepared combined generation cap; output was not adopted." };
                    else if (outcome == ModelGenerationFinish.Stop) ValidateVisible(visible.ToString(), requireJson);
                }
            }
        }
        catch (OperationCanceledException) when (intent.IsCancellationRequested)
        { result = result with { Finish = ModelGenerationFinish.Cancelled, Text = "", CancellationConfirmed = false, Error = null }; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            result = result with { Finish = ModelGenerationFinish.Unknown, Text = "", Error = error is LlamaProtocolException
                ? error.Message : "Reasoning protocol failed (" + error.GetType().Name + "); output was not adopted." };
        }
        finally
        {
            transport.Cancel();
            var confirmed = !sent || exited() || ((Volatile.Read(ref acknowledged) != 0 || rejected) &&
                await LlamaProtocol.ConfirmIdleAsync(http, slot.Id, exited, timing));
            if (!confirmed)
            {
                const string reason = "Reasoning stop could not be confirmed. This slot remains quarantined; explicitly unload and reload the model once other work has stopped.";
                slot.Quarantine(reason);
                result = result with { Finish = ModelGenerationFinish.Unknown, CancellationConfirmed = false,
                    Error = string.IsNullOrWhiteSpace(result.Error) ? reason : result.Error + " " + reason };
            }
            else if (intent.IsCancellationRequested)
                result = result with { Finish = ModelGenerationFinish.Cancelled, CancellationConfirmed = true, Error = null };
            slot.Dispose(); body = default;
        }
        // The adopted callback still waits for confirmed idle; drafts never adopt an action.
        if (result.Finish == ModelGenerationFinish.Stop && intent.IsCancellationRequested)
            result = result with { Finish = ModelGenerationFinish.Cancelled, CancellationConfirmed = true, Error = null };
        // A broken parser/template could have put private reasoning in the apparent content channel.
        // Non-adopted reasoning responses therefore retain usage and diagnostics, never partial text.
        if (result.Finish != ModelGenerationFinish.Stop || result.Error is not null) result = result with { Text = "" };
        draft.Complete(result.Finish == ModelGenerationFinish.Stop && result.Error is null);
        if (result.Finish == ModelGenerationFinish.Stop && result.Error is null) onText?.Invoke(result.Text);
        return result;
    }

    private static void UniqueFields(JsonElement value, string? optionalTelemetry = null)
    {
        // Optional telemetry duplicates are rejected by the cache parser and become unavailable.
        // Identity, choice, content and required token-usage fields remain strictly unique.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!names.Add(field.Name) && field.Name != optionalTelemetry)
                throw new LlamaProtocolException("Reasoning frame contained duplicated fields.");
    }

    private static void ValidateVisible(string text, bool requireJson)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new LlamaProtocolException("Reasoning completed without visible content; verify template separation.");
        if (text.Contains("<think>", StringComparison.OrdinalIgnoreCase) || text.Contains("</think>", StringComparison.OrdinalIgnoreCase))
            throw new LlamaProtocolException("Reasoning returned unseparated tagged content; output was not adopted.");
        if (requireJson)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                // JSON escaping must not hide tagged reasoning inside an otherwise valid action.
                if (HasTaggedContent(document.RootElement))
                    throw new LlamaProtocolException("Reasoning returned tagged content inside its structured action; output was not adopted.");
            }
            catch (JsonException) { throw new LlamaProtocolException("Reasoning did not return one structured JSON object; output was not adopted."); }
        }
    }

    private static bool HasTaggedContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => HasTags(value.GetString()!),
        JsonValueKind.Array => value.EnumerateArray().Any(HasTaggedContent),
        JsonValueKind.Object => value.EnumerateObject().Any(field => HasTags(field.Name) || HasTaggedContent(field.Value)),
        _ => false
    };

    private static bool HasTags(string value) => value.Contains("<think>", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("</think>", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        Task<ModelGenerationResult>? active;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; disposal.Cancel(); active = generation;
        }
        draft.Complete(accepted: false);
        try { if (active is not null) await active; }
        finally { slot.Dispose(); body = default; disposal.Dispose(); }
    }
}
