using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

internal sealed class LlamaProtocolException(string message) : IOException(message);

/// <summary>One allocator is shared by every caller of a loaded llama-server, including legacy chat.
/// An uncertain stop consumes its slot until that instance is explicitly stopped; it is never reused optimistically.</summary>
internal sealed class LlamaSlotPool
{
    private readonly InferenceSlotArbiter available;
    private readonly ConcurrentDictionary<int, string> quarantined = new();
    private readonly int capacity;
    private Action? poolObservation;
    internal void ObserveChanges(Action observer) => poolObservation = observer;
    private void ObservePoolSafely()
    {
        try { poolObservation?.Invoke(); }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { }
    }
    private readonly object nativeCacheGate = new();
    private NativeCacheObservation? nativeCache;
    private NativeCacheOwner? nativeCacheOwner;
    private bool nativeCacheClosed;
    internal NativeCacheOwner? NativeCacheOwner
    {
        get
        {
            try
            {
                NativeCacheObservation? observation; NativeCacheOwner? captured;
                lock (nativeCacheGate) { observation = nativeCache; captured = nativeCacheOwner; }
                return observation?.Owner ?? captured;
            }
            catch (Exception e) when (NativeCacheObservation.Ordinary(e))
            { return nativeCacheOwner is { } owner ? owner with { ObservationState = "unavailable", ObservationReason = "capture-unavailable" } : null; }
        }
    }
    internal void ConfigureNativeCache(NativeCacheOwner owner, Func<long> currentEpoch)
    {
        try
        {
            lock (nativeCacheGate)
            {
                if (nativeCacheOwner is not null) return;
                if (nativeCacheClosed)
                { nativeCacheOwner = owner with { ObservationState = "unavailable", ObservationReason = "instance-stopped" }; return; }
                nativeCache = NativeCacheObservation.TryCreate(owner, capacity, currentEpoch, out var reason);
                nativeCacheOwner = nativeCache is null
                    ? owner with { ObservationState = "unavailable", ObservationReason = reason } : owner;
            }
        }
        catch (Exception e) when (NativeCacheObservation.Ordinary(e)) { }
    }
    internal void StopNativeCache()
    {
        try
        {
            NativeCacheObservation? observation;
            lock (nativeCacheGate) { nativeCacheClosed = true; observation = nativeCache; }
            observation?.Dispose();
        }
        catch (Exception e) when (NativeCacheObservation.Ordinary(e)) { }
    }

    public LlamaSlotPool(int count)
    {
        if (count is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(count));
        capacity = count;
        available = new(count);
    }
    internal int Capacity => capacity;
    public int AvailableCount => available.AvailableCount;
    public IReadOnlyDictionary<int, string> Quarantined => new Dictionary<int, string>(quarantined);
    public async Task<Lease> AcquireAsync(Action released, CancellationToken token, Action<string>? quarantine = null,
        bool nativeCacheCompatible = false)
    {
        try
        {
            var slot = WorkerLogicalContextAffinity.Current is { } context
                ? await available.AcquireForContextAsync(WorkerInferencePriority.Current, token, context)
                : await available.AcquireAsync(WorkerInferencePriority.Current, token);
            var lease = new Lease(this, slot, released, quarantine, nativeCacheCompatible);
            if (!nativeCacheCompatible) lease.InvalidateNativeCache("unsupported-lease-entry");
            ObservePoolSafely();
            return lease;
        }
        catch (ChannelClosedException) when (quarantined.Count == capacity)
        { throw new IOException("All model slots have unconfirmed stops. Explicitly unload and reload the model before starting more work."); }
    }
    public void Complete() { StopNativeCache(); available.Complete(); ObservePoolSafely(); }

    internal sealed class Lease(LlamaSlotPool owner, int id, Action released, Action<string>? quarantine = null,
        bool nativeCacheCompatible = false) : IDisposable
    {
        private readonly object leaseGate = new();
        private bool disposed, isQuarantined;
        public int Id => id;
        internal string LeaseId { get; } = Guid.NewGuid().ToString("N");
        private NativeCacheBinding? nativeBinding;
        private Action<NativeCacheGenerationObservation>? nativePublish;
        private Action<NativeCacheDecisionObservation>? nativeDecisionPublish;
        internal bool NativeDecisionEnabled => nativeCacheCompatible && !IsQuarantined &&
            nativeBinding is { Purpose: "task-generation", ExecutionAttribution: "local-host-invocation" } && nativeDecisionPublish is not null;
        private int nativeFinalized;
        private string? nativeInvalidationReason;
        internal void BindNativeCache(NativeCacheBinding binding, Action<NativeCacheGenerationObservation> publish,
            Action<NativeCacheDecisionObservation>? decisionPublish = null)
        {
            try
            {
                nativeBinding = null; nativePublish = null; nativeDecisionPublish = null;
                var context = owner.NativeCacheOwner;
                if (!nativeCacheCompatible || context is null || binding.ModelId != context.ModelId ||
                    binding.InstanceId != context.InstanceId || binding.LeaseId != LeaseId || binding.NativeSlotId != id)
                { InvalidateNativeCache("exact-entry-binding-unavailable"); return; }
                nativeBinding = binding; nativePublish = publish; nativeDecisionPublish = decisionPublish;
            }
            catch (Exception e) when (NativeCacheObservation.Ordinary(e))
            { InvalidateNativeCache("capture-unavailable"); }
        }
        internal void InvalidateNativeCache(string reason)
        {
            try
            {
                nativeInvalidationReason = NativeCacheObservation.ReasonCode(reason);
                owner.nativeCache?.Invalidate(id, nativeInvalidationReason);
            }
            catch (Exception e) when (NativeCacheObservation.Ordinary(e)) { }
        }
        internal NativeCacheGeneration? BeginNativeCache(JsonElement prompt, PreparedModelMetadata metadata,
            bool supportedText, CancellationToken token)
        {
            try
            {
                var context = owner.NativeCacheOwner;
                return owner.nativeCache?.Begin(id, LeaseId, nativeBinding, nativePublish, prompt, metadata.InputTokens,
                    nativeCacheCompatible && supportedText && !IsQuarantined && context is not null &&
                    metadata.ModelId == context.ModelId && metadata.InstanceId == context.InstanceId, token, nativeDecisionPublish);
            }
            catch (Exception e) when (NativeCacheObservation.Ordinary(e))
            { InvalidateNativeCache("capture-unavailable"); return null; }
        }
        internal void CompleteNativeCache(NativeCacheGeneration? attempt, ModelGenerationResult result, bool sent,
            bool acknowledged, bool confirmedIdle, bool live, bool cancelled, NativeCacheProviderEvidence evidence)
        {
            Interlocked.Exchange(ref nativeFinalized, 1);
            try
            {
                if (attempt is null) { InvalidateNativeCache("prior-native-finalization-unavailable"); return; }
                owner.nativeCache?.Complete(attempt, result, sent, acknowledged,
                    confirmedIdle && !IsQuarantined, live, cancelled, evidence);
            }
            catch (Exception e) when (NativeCacheObservation.Ordinary(e))
            { InvalidateNativeCache("prior-native-finalization-unavailable"); }
        }
        public bool IsQuarantined { get { lock (leaseGate) return isQuarantined; } }
        public void Quarantine(string reason)
        {
            lock (leaseGate)
            {
                // A stale callback cannot poison a slot already returned to another borrower.
                if (disposed || isQuarantined) return;
                isQuarantined = true;
                owner.available.InvalidateContext(id);
            }
            InvalidateNativeCache("native-slot-quarantined");
            try { quarantine?.Invoke(reason); }
            finally
            {
                owner.quarantined.TryAdd(id, reason);
                if (owner.quarantined.Count == owner.capacity) owner.available.Complete();
                owner.ObservePoolSafely();
            }
        }
        public void Dispose()
        {
            bool quarantined;
            lock (leaseGate)
            {
                if (disposed) return;
                disposed = true; quarantined = isQuarantined;
            }
            if (quarantined) { InvalidateNativeCache("native-slot-quarantined"); return; }
            if (Volatile.Read(ref nativeFinalized) == 0) InvalidateNativeCache(nativeInvalidationReason ?? "lease-return-without-native-finalization");
            owner.available.Return(id);
            try { released(); }
            finally { owner.ObservePoolSafely(); }
        }
    }
}

internal sealed record LlamaProtocolTiming(TimeSpan AcceptanceAfterCancellation, TimeSpan StopConfirmation, TimeSpan PollInterval)
{
    public static LlamaProtocolTiming Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100));
}

internal static class LlamaProtocol
{
    internal const int MaximumFrameCharacters = 2_000_000;
    internal const int MaximumPreparationCharacters = 16_777_216;
    internal const string CountBasis = "Exact llama.cpp template token IDs (add_special=true, parse_special=true); identical IDs sent to completion.";

    internal static async Task<JsonDocument> ReadJsonAsync(HttpContent content, int maximumCharacters, CancellationToken token)
    {
        using var reader = new StreamReader(await content.ReadAsStreamAsync(token));
        var result = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, token);
            if (count == 0) break;
            if (result.Length + count > maximumCharacters) throw new IOException("Model protocol response exceeded its size limit.");
            result.Append(buffer, 0, count);
        }
        return JsonDocument.Parse(result.ToString());
    }

    internal static async Task<JsonDocument> PostAsync(HttpClient http, string path, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: CerebralJson.Options) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new LlamaProtocolException($"Model {path} failed with HTTP {(int)response.StatusCode}.");
        return await ReadJsonAsync(response.Content, MaximumPreparationCharacters, token);
    }

    internal static async Task<(int Context, bool Processing, int? TaskId)> SlotAsync(HttpClient http, int id, CancellationToken token)
    {
        using var response = await http.GetAsync("slots", HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new LlamaProtocolException("Model slot diagnostics are unavailable. Use a llama-server build with /slots enabled.");
        using var document = await ReadJsonAsync(response.Content, 262_144, token);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("Model returned invalid slot diagnostics.");
        var matches = document.RootElement.EnumerateArray().Where(slot => slot.TryGetProperty("id", out var value) && value.TryGetInt32(out var candidate) && candidate == id).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("n_ctx", out var context) || !context.TryGetInt32(out var count) || count is < 512 or > LlamaPreparedGeneration.NativeMaximumContextTokens ||
            !matches[0].TryGetProperty("is_processing", out var processing) || processing.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new IOException("Model did not report the reserved slot and its effective context.");
        int? taskId = matches[0].TryGetProperty("id_task", out var task) && task.ValueKind == JsonValueKind.Number && task.TryGetInt32(out var parsed) ? parsed : null;
        return (count, processing.GetBoolean(), taskId);
    }

    // GET /slots is high priority and can overtake a queued completion. Call only after this
    // request was acknowledged or explicitly rejected, never to infer pre-admission cancellation.
    internal static async Task<bool> ConfirmIdleAsync(HttpClient http, int id, Func<bool> exited, LlamaProtocolTiming timing)
    {
        using var cleanup = new CancellationTokenSource(timing.StopConfirmation);
        try
        {
            while (!cleanup.IsCancellationRequested)
            {
                if (exited()) return true;
                if (!(await SlotAsync(http, id, cleanup.Token)).Processing) return true;
                await Task.Delay(timing.PollInterval, cleanup.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or ObjectDisposedException) { }
        return exited();
    }

    internal static int? ReadCount(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetInt32(out var count) || count < 0) throw new IOException("Model returned invalid token usage.");
        return count;
    }

    private const string InvalidPromptCacheDiagnostic =
        "Provider prompt-cache telemetry was malformed or inconsistent with the fully prepared input.";

    // Optional diagnostics cannot change output adoption, token budgets, or slot ownership.
    // Read only explicit provider measurements; absence is never an inferred zero or cache hit.
    internal static PromptCacheMetrics? ReadPromptCache(JsonElement root, int preparedInputTokens)
    {
        try
        {
            int? cached = null, processed = null;
            double? milliseconds = null;
            if (OptionalCacheField(root, "timings", out var timings))
            {
                if (timings.ValueKind != JsonValueKind.Object) return UnavailablePromptCache(preparedInputTokens);
                cached = OptionalCacheCount(timings, "cache_n");
                processed = OptionalCacheCount(timings, "prompt_n");
                if (OptionalCacheField(timings, "prompt_ms", out var elapsed))
                {
                    if (elapsed.ValueKind != JsonValueKind.Number || !elapsed.TryGetDouble(out var value))
                        return UnavailablePromptCache(preparedInputTokens);
                    milliseconds = value;
                }
            }
            if (OptionalCacheField(root, "usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
            {
                if (usage.ValueKind != JsonValueKind.Object) return UnavailablePromptCache(preparedInputTokens);
                if (OptionalCacheField(usage, "prompt_tokens_details", out var details))
                {
                    if (details.ValueKind != JsonValueKind.Object) return UnavailablePromptCache(preparedInputTokens);
                    var usageCached = OptionalCacheCount(details, "cached_tokens");
                    if (cached is not null && usageCached is not null && cached != usageCached)
                        return UnavailablePromptCache(preparedInputTokens);
                    cached ??= usageCached;
                }
            }
            return ValidatePromptCache(new(preparedInputTokens, cached, processed, milliseconds), preparedInputTokens);
        }
        catch (InvalidDataException) { return UnavailablePromptCache(preparedInputTokens); }
    }

    internal static PromptCacheMetrics? MergePromptCache(PromptCacheMetrics? first, PromptCacheMetrics? next, int preparedInputTokens)
    {
        first = ValidatePromptCache(first, preparedInputTokens);
        next = ValidatePromptCache(next, preparedInputTokens);
        if (first is null) return next;
        if (next is null) return first;
        if (first.Diagnostic is not null || next.Diagnostic is not null ||
            first.CachedTokens is not null && next.CachedTokens is not null && first.CachedTokens != next.CachedTokens ||
            first.ProcessedPromptTokens is not null && next.ProcessedPromptTokens is not null && first.ProcessedPromptTokens != next.ProcessedPromptTokens ||
            first.PromptProcessingMilliseconds is not null && next.PromptProcessingMilliseconds is not null &&
                first.PromptProcessingMilliseconds != next.PromptProcessingMilliseconds)
            return UnavailablePromptCache(preparedInputTokens);
        return ValidatePromptCache(new(preparedInputTokens, first.CachedTokens ?? next.CachedTokens,
            first.ProcessedPromptTokens ?? next.ProcessedPromptTokens,
            first.PromptProcessingMilliseconds ?? next.PromptProcessingMilliseconds), preparedInputTokens);
    }

    internal static PromptCacheMetrics? ValidatePromptCache(PromptCacheMetrics? metrics, int preparedInputTokens)
    {
        if (metrics is null) return null;
        if (preparedInputTokens < 0 || metrics.PreparedInputTokens != preparedInputTokens ||
            metrics.CachedTokens is < 0 || metrics.CachedTokens > preparedInputTokens ||
            metrics.ProcessedPromptTokens is < 0 || metrics.ProcessedPromptTokens > preparedInputTokens ||
            metrics.CachedTokens is { } cached && metrics.ProcessedPromptTokens is { } processed && (long)cached + processed != preparedInputTokens ||
            metrics.PromptProcessingMilliseconds is { } elapsed && (!double.IsFinite(elapsed) || elapsed < 0) ||
            metrics.Diagnostic is not null)
            return UnavailablePromptCache(preparedInputTokens);
        return metrics.CachedTokens is null && metrics.ProcessedPromptTokens is null && metrics.PromptProcessingMilliseconds is null
            ? null : metrics;
    }

    private static PromptCacheMetrics UnavailablePromptCache(int preparedInputTokens) =>
        new(preparedInputTokens, Diagnostic: InvalidPromptCacheDiagnostic);

    private static bool OptionalCacheField(JsonElement value, string name, out JsonElement field)
    {
        field = default;
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var found = false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (found) throw new InvalidDataException();
            field = property.Value; found = true;
        }
        return found;
    }

    private static int? OptionalCacheCount(JsonElement value, string name)
    {
        if (!OptionalCacheField(value, name, out var field)) return null;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var count)) throw new InvalidDataException();
        return count;
    }

    internal static string DescribeFrame(JsonElement root)
    {
        // Fixed protocol field names and JSON kinds only; never echo response text or unknown keys.
        if (root.ValueKind != JsonValueKind.Object) return root.ValueKind.ToString();
        string[] known = ["content", "stop", "stop_type", "id_slot", "index", "tokens", "tokens_predicted", "tokens_evaluated", "timings", "prompt_progress", "choices", "id", "error"];
        return string.Join(", ", known.Where(name => root.TryGetProperty(name, out _)).Select(name => name + ":" + root.GetProperty(name).ValueKind));
    }

    internal sealed class BoundedLines(StreamReader reader)
    {
        private readonly char[] buffer = new char[4096];
        private int position, length;
        public async Task<string?> ReadAsync(CancellationToken token)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (position == length)
                {
                    length = await reader.ReadAsync(buffer, token); position = 0;
                    if (length == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
                }
                var end = Array.IndexOf(buffer, '\n', position, length - position);
                var count = (end < 0 ? length : end) - position;
                if (line.Length + count > MaximumFrameCharacters) throw new IOException("Model stream frame exceeded its size limit.");
                line.Append(buffer, position, count); position += count;
                if (end >= 0) { position++; return line.ToString().TrimEnd('\r'); }
            }
        }
    }
}

/// <summary>Native llama.cpp text protocol. Raw token arrays bypass server retokenization.
/// See upstream tools/server/server-common.cpp tokenize_input_subprompt and server-context.cpp handle_completions_impl.</summary>
internal partial class LlamaPreparedGeneration : IPreparedModelGeneration, IProviderGenerationTelemetry
{
    internal const int NativeMaximumContextTokens = 1_048_576;
    internal const int NativeMinimumInputTokens = 1;
    internal static int NativeMaximumVisibleTokens =>
        NativeMaximumContextTokens - NativeMinimumInputTokens - AgentTokenLimits.ContextSafetyTokens;
    internal static ModelAdapterDescriptor Adapter { get; } = new("llama.cpp.native.text", "1.1.0", Array.AsReadOnly(new[] { "text" }), SupportsReasoning: true);
    private readonly HttpClient http;
    private readonly LlamaSlotPool.Lease slot;
    private readonly Func<bool> exited;
    private JsonElement prompt;
    private readonly string[] stops;
    private readonly JsonElement? schema;
    private readonly double temperature;
    private readonly LlamaProtocolTiming timing;
    private readonly CancellationTokenSource disposal = new();
    private readonly object gate = new();
    private Task<ModelGenerationResult>? generation;
    private bool disposed;
    public PreparedModelMetadata Metadata { get; }
    private Action<ProviderGenerationMeasurement>? telemetryObserver;
    public void SetTelemetryObserver(Action<ProviderGenerationMeasurement>? observer) => telemetryObserver = observer;

    private LlamaPreparedGeneration(HttpClient http, LlamaSlotPool.Lease slot, Func<bool> exited, JsonElement prompt,
        string[] stops, JsonElement? schema, double temperature, PreparedModelMetadata metadata, LlamaProtocolTiming timing)
    {
        this.http = http; this.slot = slot; this.exited = exited; this.prompt = prompt;
        this.stops = stops; this.schema = schema; this.temperature = temperature; Metadata = metadata; this.timing = timing;
    }

    private sealed class PolicyPreparedGeneration(HttpClient http, LlamaSlotPool.Lease slot,
        Func<bool> exited, JsonElement prompt, string[] stops, JsonElement? schema,
        double temperature, PreparedModelMetadata metadata, LlamaProtocolTiming timing,
        ModelExecutionPolicy policy)
        : LlamaPreparedGeneration(http, slot, exited, prompt, stops, schema, temperature, metadata, timing),
            IModelExecutionPolicyFacet
    {
        public ModelExecutionPolicy ExecutionPolicy { get; } = policy;
    }

    // Pure native text shape guard shared with the registered adapter's pre-ownership check.
    // Callers validate the bounded request first; this method performs no template/provider I/O.
    internal static void ValidateTextShape(ModelGenerationRequest request)
    {
        var conversational = false;
        foreach (var message in request.Messages)
        {
            foreach (var part in message.Content)
                if (part is not TextContentPart)
                    throw new NotSupportedException($"The text adapter does not support {part.GetType().Name}. Select a native adapter for that content kind.");
            if (message.Role == "system" && conversational)
                throw new LlamaProtocolException("This text adapter requires system guidance before conversational messages. Move guidance to the leading system layer without reordering conversation evidence.");
            if (message.Role != "system") conversational = true;
        }
    }

    internal static ModelMessage[] TextSnapshot(ModelGenerationRequest request)
        => TextSnapshot(request, null);

    internal static ModelMessage[] TextSnapshot(ModelGenerationRequest request, ModelExecutionPolicy? policy)
    {
        if (policy is null) request.Validate(); else request.Validate(policy);
        ValidateTextShape(request);
        var messages = new List<ModelMessage>();
        var leadingGuidance = new List<string>();
        foreach (var message in request.Messages)
        {
            var content = string.Concat(message.Content.Select(part => part is TextContentPart text ? text.Text
                : throw new NotSupportedException($"The text adapter does not support {part.GetType().Name}. Select a native adapter for that content kind.")));
            if (message.Role == "system")
            {
                if (messages.Count != 0)
                    throw new LlamaProtocolException("This text adapter requires system guidance before conversational messages. Move guidance to the leading system layer without reordering conversation evidence.");
                leadingGuidance.Add(content);
            }
            else messages.Add(new(message.Role, content));
        }
        // Qwen and other native templates accept one initial system message. Preserve all host
        // and harness guidance in order; preparation counts the combined template actually sent.
        if (leadingGuidance.Count != 0) messages.Insert(0, new("system", string.Join("\n\n", leadingGuidance)));
        return messages.ToArray();
    }

    internal static async Task<IPreparedModelGeneration> PrepareAsync(HttpClient http, LlamaSlotPool.Lease slot,
        string instanceId, ModelGenerationRequest request, IReadOnlyList<string> stopSequences, Func<bool> exited,
        CancellationToken token, LlamaProtocolTiming? timing = null)
        => await PrepareCoreAsync(http, slot, instanceId, request, stopSequences, exited, token, timing, null)
            .ConfigureAwait(false);

    internal static Task<IPreparedModelGeneration> PrepareSelectedAsync(HttpClient http, LlamaSlotPool.Lease slot,
        string instanceId, ModelGenerationRequest request, IReadOnlyList<string> stopSequences, Func<bool> exited,
        ModelExecutionPolicy selected, CancellationToken token, LlamaProtocolTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(selected);
        selected.Validate();
        if (selected.Binding.InstanceId != instanceId || selected.MinimumContextTokens < 512 ||
            selected.MaximumContextTokens > 1_048_576 ||
            (long)selected.MaximumVisibleOutputTokens + AgentTokenLimits.ContextSafetyTokens + 1 > selected.MaximumContextTokens)
            throw new InvalidOperationException("Selected native policy exceeds this handler's software envelope.");
        return PrepareCoreAsync(http, slot, instanceId, request, stopSequences, exited, token, timing, selected);
    }

    private static async Task<IPreparedModelGeneration> PrepareCoreAsync(HttpClient http, LlamaSlotPool.Lease slot,
        string instanceId, ModelGenerationRequest request, IReadOnlyList<string> stopSequences, Func<bool> exited,
        CancellationToken token, LlamaProtocolTiming? timing, ModelExecutionPolicy? selected)
    {
        try
        {
            var messages = TextSnapshot(request, selected);
            if (request.ReasoningTokens != 0)
                throw new NotSupportedException("Prepared reasoning requires the template-aware reasoning route.");
            var schema = request.ResponseSchema?.Clone(); var stops = stopSequences.ToArray();
            if (stops.Length > 128 || stops.Any(stop => stop is null || stop.Length > 4096 || stop.Contains('\0')))
                throw new ArgumentException("Configured model stop sequences exceed their bounds.");
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
            preparation.CancelAfter(TimeSpan.FromSeconds(60));
            var status = await LlamaProtocol.SlotAsync(http, slot.Id, preparation.Token);
            if (status.Processing)
            {
                slot.Quarantine("Reserved model slot was already occupied. Wait for other work, then explicitly unload and reload the model.");
                throw new IOException("The reserved model slot was unexpectedly occupied; capacity was quarantined.");
            }
            var effective = Math.Min(request.ContextLimit, status.Context);
            using var template = await LlamaProtocol.PostAsync(http, "apply-template", new
            {
                messages, add_generation_prompt = true, chat_template_kwargs = new { enable_thinking = false }
            }, preparation.Token);
            if (!template.RootElement.TryGetProperty("prompt", out var promptValue) || promptValue.ValueKind != JsonValueKind.String)
                throw new IOException("Model template did not return a prompt.");
            var prompt = promptValue.GetString()!;
            if (prompt.Length > LlamaProtocol.MaximumFrameCharacters) throw new IOException("Templated model input exceeds its size limit.");
            // Native /completion string processing uses true,true. Do not count with false and
            // generate with true: some model vocabularies insert an additional BOS token.
            using var tokenized = await LlamaProtocol.PostAsync(http, "tokenize", new { content = prompt, add_special = true, parse_special = true, with_pieces = false }, preparation.Token);
            if (!tokenized.RootElement.TryGetProperty("tokens", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > NativeMaximumContextTokens)
                throw new IOException("Model tokenizer did not return a bounded token array.");
            var ids = new int[array.GetArrayLength()]; var index = 0;
            foreach (var value in array.EnumerateArray())
            {
                if (!value.TryGetInt32(out var id) || id < 0) throw new IOException("Model tokenizer returned an invalid token ID.");
                ids[index++] = id;
            }
            var metadata = ResolveBudget(instanceId, Adapter, request, ids.Length, effective, LlamaProtocol.CountBasis);
            var promptTokens = JsonSerializer.SerializeToElement(ids);
            var effectiveTiming = timing ?? LlamaProtocolTiming.Default;
            return selected is null
                ? new LlamaPreparedGeneration(http, slot, exited, promptTokens, stops, schema,
                    request.Temperature, metadata, effectiveTiming)
                : new PolicyPreparedGeneration(http, slot, exited, promptTokens, stops, schema,
                    request.Temperature, metadata, effectiveTiming, selected);
        }
        catch { slot.Dispose(); throw; }
    }

    public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (generation is not null) throw new InvalidOperationException("A prepared generation can be used only once.");
            return generation = GenerateCoreAsync(onText, cancellationToken);
        }
    }

    private async Task<ModelGenerationResult> GenerateCoreAsync(Action<string>? onText, CancellationToken token)
    {
        using var intent = CancellationTokenSource.CreateLinkedTokenSource(token, disposal.Token);
        using var transport = new CancellationTokenSource();
        var acknowledged = 0; var sent = false; var rejected = false;
        NativeCacheGeneration? cacheGeneration = null;
        var cacheEvidence = NativeCacheProviderEvidence.Missing;
        int? nativeTaskId = null; var nativeTaskConflict = false;
        var text = new StringBuilder(); var publishedCharacters = 0;
        ModelGenerationResult result = new(ModelGenerationFinish.Unknown, "", Error: "Model stream ended without a terminal response.");
        using var cancel = intent.Token.Register(() =>
        {
            // A pre-admission disconnect followed by an idle slot observation is not a barrier:
            // /slots can overtake completion admission. Give the request a bounded chance to acknowledge.
            if (Volatile.Read(ref acknowledged) != 0) transport.Cancel();
            else transport.CancelAfter(timing.AcceptanceAfterCancellation);
        });
        try
        {
            intent.Token.ThrowIfCancellationRequested();
            if (exited()) throw new IOException("The prepared model instance is no longer running.");
            var body = new Dictionary<string, object?>
            {
                ["prompt"] = prompt, ["id_slot"] = slot.Id, ["n_predict"] = Metadata.ResponseBudget,
                ["temperature"] = temperature, ["stream"] = true, ["return_progress"] = true,
                ["timings_per_token"] = true, ["n"] = 1,
                ["cache_prompt"] = true,
                ["response_fields"] = new[] { "content", "id_slot", "stop", "stop_type", "tokens_predicted", "tokens_evaluated", "truncated", "prompt_progress", "timings" }
            };
            if (slot.NativeDecisionEnabled && Metadata.AdapterId == Adapter.AdapterId)
                body["response_fields"] = ((string[])body["response_fields"]!).Append(NativeCacheDecisionCapture.Field).ToArray();
            if (stops.Length > 0) body["stop"] = stops;
            if (schema is { } constraint) body["json_schema"] = constraint;
            using var request = new HttpRequestMessage(HttpMethod.Post, "completion") { Content = JsonContent.Create(body, options: CerebralJson.Options) };
            cacheGeneration = slot.BeginNativeCache(prompt, Metadata, Metadata.AdapterId == Adapter.AdapterId, intent.Token);
            intent.Token.ThrowIfCancellationRequested();
            sent = true;
            NativeCacheDecisionCapture.Requested(cacheGeneration);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transport.Token);
            if (!response.IsSuccessStatusCode)
            {
                // A completed server error response is distinct from losing the transport before admission.
                using var error = await LlamaProtocol.ReadJsonAsync(response.Content, 65_536, transport.Token);
                rejected = error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out _);
                result = result with { Error = $"Model request failed with HTTP {(int)response.StatusCode}." };
            }
            else
            {
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new LlamaProtocolException("Model did not return an inference event stream.");
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(transport.Token));
                var lines = new LlamaProtocol.BoundedLines(reader);
                while (await lines.ReadAsync(transport.Token) is { } line)
                {
                    transport.Token.ThrowIfCancellationRequested();
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line[5..].TrimStart();
                    if (data == "[DONE]") break; // not the native terminal receipt
                    using var frame = JsonDocument.Parse(data); var root = frame.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) throw new LlamaProtocolException("Model inference returned an error frame. Fields: " + LlamaProtocol.DescribeFrame(root));
                    var terminal = root.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.True;
                    if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
                        stop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new LlamaProtocolException("Model response did not contain a valid progress or completion frame. Fields: " + LlamaProtocol.DescribeFrame(root));
                    if (!root.TryGetProperty("id_slot", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var returnedId) ||
                        (returnedId != slot.Id && (returnedId != -1 || terminal)))
                        throw new LlamaProtocolException($"Model response did not identify reserved slot {slot.Id}; returned slot " +
                            (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var reported) ? reported.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable") +
                            ". Fields: " + LlamaProtocol.DescribeFrame(root));
                    // Headers alone are not an admission receipt. A frame naming our reserved slot
                    // establishes ownership even for older builds with different HTTP header timing.
                    if (returnedId == slot.Id)
                    {
                        NativeCacheDecisionCapture.Frame(cacheGeneration, root);
                        Volatile.Write(ref acknowledged, 1);
                    }
                    else
                    {
                        // llama.cpp also emits id_slot=-1 on partials: send_partial_response does
                        // not fill the inherited slot field. Only that sentinel is supported, with
                        // the single-prompt index and our exact prompt count. All callers share the
                        // named slot pool; while this stream stays open, observe the reserved slot
                        // actually processing before using it as cancellation acknowledgement.
                        if (!root.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var ordinal) || ordinal != 0 ||
                            LlamaProtocol.ReadCount(root, "tokens_evaluated") != Metadata.InputTokens || LlamaProtocol.ReadCount(root, "tokens_predicted") is null)
                            throw new LlamaProtocolException("Model partial-slot sentinel did not match the prepared single-prompt request.");
                        // Capture before this existing await: cancellation/failure cannot erase a reported decision.
                        NativeCacheDecisionCapture.Frame(cacheGeneration, root);
                        if (Volatile.Read(ref acknowledged) == 0)
                        {
                            var owned = await LlamaProtocol.SlotAsync(http, slot.Id, transport.Token);
                            if (owned.Processing && owned.TaskId is >= 0)
                            {
                                Volatile.Write(ref acknowledged, 1);
                                if (nativeTaskId is { } previousTask && previousTask != owned.TaskId) nativeTaskConflict = true;
                                nativeTaskId = owned.TaskId;
                            }
                            // A quick completion may already be idle. Keep its text and wait for
                            // a terminal frame naming the slot; idle alone never acknowledges it.
                        }
                    }
                    NativeCacheDecisionCapture.Task(cacheGeneration, nativeTaskId, nativeTaskConflict);
                    if (Volatile.Read(ref acknowledged) != 0 && intent.IsCancellationRequested && !terminal) { transport.Cancel(); transport.Token.ThrowIfCancellationRequested(); }
                    if (content.GetString() is { Length: > 0 } fragment)
                    {
                        if (text.Length + fragment.Length > LlamaProtocol.MaximumFrameCharacters) throw new LlamaProtocolException("Model output exceeded its size limit.");
                        text.Append(fragment);
                    }
                    if (Volatile.Read(ref acknowledged) != 0)
                        ProviderGenerationTelemetry.Observe(telemetryObserver, root);
                    if (Volatile.Read(ref acknowledged) != 0 && text.Length > publishedCharacters)
                    {
                        onText?.Invoke(text.ToString(publishedCharacters, text.Length - publishedCharacters));
                        publishedCharacters = text.Length;
                        if (!terminal) transport.Token.ThrowIfCancellationRequested();
                    }
                    if (terminal)
                    {
                        var reason = root.TryGetProperty("stop_type", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;
                        var truncated = root.TryGetProperty("truncated", out var truncatedValue) && truncatedValue.ValueKind == JsonValueKind.True;
                        var finish = truncated ? ModelGenerationFinish.Unknown : reason switch
                        { "eos" or "word" => ModelGenerationFinish.Stop, "limit" => ModelGenerationFinish.Length, _ => ModelGenerationFinish.Unknown };
                        if (Metadata.AdapterId == VisionAdapter.AdapterId && LlamaProtocol.ReadCount(root, "tokens_evaluated") != Metadata.InputTokens)
                        {
                            result = new(ModelGenerationFinish.Unknown, text.ToString(), Error: "Native image input count changed between preparation and generation; output was not adopted.");
                            break;
                        }
                        result = new(finish, text.ToString(), LlamaProtocol.ReadCount(root, "tokens_predicted"), LlamaProtocol.ReadCount(root, "tokens_evaluated"),
                            Error: truncated ? "Model reported context truncation; output was not adopted as complete." : finish == ModelGenerationFinish.Unknown ? "Model returned an unknown finish reason." : null)
                            { PromptCache = LlamaProtocol.ReadPromptCache(root, Metadata.InputTokens) };
                        cacheEvidence = NativeCacheObservation.Evidence(root, result.PromptCache, nativeTaskId, nativeTaskConflict);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (intent.IsCancellationRequested)
        { result = new(ModelGenerationFinish.Cancelled, text.ToString()); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { result = new(ModelGenerationFinish.Unknown, text.ToString(), Error: ex is LlamaProtocolException ? ex.Message : $"Model protocol failed ({ex.GetType().Name}); incomplete output was retained only as a partial result."); }
        finally
        {
            // Disconnect first, then establish a server-side stop using an independent cleanup budget.
            transport.Cancel();
            var confirmed = !sent || exited() || ((Volatile.Read(ref acknowledged) != 0 || rejected) && await LlamaProtocol.ConfirmIdleAsync(http, slot.Id, exited, timing));
            if (!confirmed)
            {
                const string reason = "Inference stop could not be confirmed. This slot remains reserved; wait for other work, then explicitly unload and reload the model.";
                slot.Quarantine(reason);
                result = result with { Finish = ModelGenerationFinish.Unknown, Text = text.ToString(), CancellationConfirmed = false,
                    Error = string.IsNullOrWhiteSpace(result.Error) ? reason : result.Error + " " + reason };
            }
            else if (result.Finish == ModelGenerationFinish.Cancelled) result = result with { CancellationConfirmed = true };
            var liveForCache = false;
            try { liveForCache = !exited(); }
            catch (Exception e) when (NativeCacheObservation.Ordinary(e)) { }
            // Native input history settles before handoff. The outer accounting terminal may run later.
            slot.CompleteNativeCache(cacheGeneration, result, sent, Volatile.Read(ref acknowledged) != 0,
                confirmed && sent && Volatile.Read(ref acknowledged) != 0 && liveForCache,
                liveForCache, intent.IsCancellationRequested, cacheEvidence);
            slot.Dispose();
            prompt = default;
        }
        return result with { Text = text.ToString() };
    }

    public async ValueTask DisposeAsync()
    {
        Task<ModelGenerationResult>? active;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; disposal.Cancel(); active = generation;
        }
        try { if (active is not null) await active; }
        finally { slot.Dispose(); prompt = default; disposal.Dispose(); }
    }
}
