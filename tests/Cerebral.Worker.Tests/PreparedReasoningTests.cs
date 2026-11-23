using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic chat-protocol fixtures only: no inference process, model or PAO child calls.</summary>
public static class PreparedReasoningTests
{
    private const string Private = "PRIVATE_REASONING_NEVER_RETAIN_THIS";
    private const string Visible = "{\"action\":\"final\"}";

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Prepared reasoning: " + message);
            checks++;
        }
        using (var fixture = new Fixture())
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            Check(prepared.Metadata.AdapterId == LlamaPreparedReasoningGeneration.Adapter.AdapterId &&
                LlamaPreparedReasoningGeneration.Adapter.SupportsReasoning, "distinct adapter advertises prepared reasoning");
            Check(prepared.Metadata.InputTokens == 3 && prepared.Metadata.ResponseBudget == 256 &&
                prepared.Metadata.ReasoningReserveTokens == 128 && prepared.Metadata.CombinedGenerationBudget == 384,
                "visible allocation and reasoning reservation retain one honest combined ceiling");
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Text == Visible &&
                result.GeneratedTokens == 7 && result.EvaluatedTokens == 3, "post-finish usage supplies actual combined tokens");
            Check(result.PromptCache is null, "older reasoning usage without cache fields stays explicitly unavailable");
            Check(callbacks.SequenceEqual([Visible]) && fixture.Pool.AvailableCount == 1,
                "only complete visible content is published after confirmed cleanup");
            Check(!JsonSerializer.Serialize(result).Contains(Private, StringComparison.Ordinal) &&
                !string.Join("", callbacks).Contains(Private, StringComparison.Ordinal), "private deltas never enter result or callback");
            Check(fixture.Counted.Count == 1 && JsonElement.DeepEquals(fixture.Counted[0], fixture.Submitted!.Value),
                "one immutable full body is counted and generated");
            var body = fixture.Submitted!.Value;
            Check(body.GetProperty("cache_prompt").ValueKind == JsonValueKind.True &&
                fixture.Counted[0].GetProperty("cache_prompt").ValueKind == JsonValueKind.True &&
                !body.TryGetProperty("prompt_cache", out _) && !body.TryGetProperty("save_cache", out _),
                "prepared reasoning counts and generates the same explicit resident prefix-reuse policy without disk cache controls");
            Check(body.GetProperty("max_tokens").GetInt32() == 384 && body.GetProperty("id_slot").GetInt32() == 0 &&
                body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean() &&
                body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(),
                "combined cap, exclusive slot, usage request and thinking option reach provider");
            Check(body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString() == "object",
                "requested schema reaches the same counting and generation body");
            await ThrowsAsync<InvalidOperationException>(() => prepared.GenerateAsync(null, default)); checks++;
        }
        foreach (var sample in new[]
        {
            (Terminal: "\"timings\":{\"cache_n\":2,\"prompt_n\":1,\"prompt_ms\":7.25}",
                Usage: "\"prompt_tokens_details\":{\"cached_tokens\":2}", Cached: 2, Processed: (int?)1, Time: (double?)7.25),
            (Terminal: "\"timings\":{\"prompt_n\":1,\"prompt_ms\":7.25}",
                Usage: "\"prompt_tokens_details\":{\"cached_tokens\":2}", Cached: 2, Processed: (int?)1, Time: (double?)7.25),
            (Terminal: "\"usage\":null", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":0}",
                Cached: 0, Processed: (int?)null, Time: (double?)null)
        })
        {
            using var fixture = new Fixture { StreamText =
                WithRootFields(Frame(new { content = Visible }, "stop"), sample.Terminal) + WithUsageFields(Usage(), sample.Usage) };
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.PromptCache is { PreparedInputTokens: 3, Diagnostic: null } metrics &&
                metrics.CachedTokens == sample.Cached && metrics.ProcessedPromptTokens == sample.Processed &&
                metrics.PromptProcessingMilliseconds == sample.Time && metrics.ReuseRatio == (double)sample.Cached / 3,
                "reasoning terminal timings and usage details agree and merge against the complete prepared input");
            Check(callbacks.SequenceEqual([Visible]) && fixture.Pool.AvailableCount == 1 &&
                prepared.Metadata.InputTokens == 3 && prepared.Metadata.ResponseBudget == 256,
                "reasoning cache diagnostics preserve privacy, exact budgeting and confirmed cleanup");
        }
        foreach (var sample in new[]
        {
            (Terminal: "\"timings\":{\"cache_n\":2,\"prompt_n\":1}", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":1}"),
            (Terminal: "\"timings\":{\"prompt_n\":2}", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":2}"),
            (Terminal: "\"timings\":{\"prompt_ms\":1e400}", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":2}"),
            (Terminal: "\"timings\":{},\"timings\":{}", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":2}"),
            (Terminal: "\"usage\":null", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":-1}"),
            (Terminal: "\"usage\":null", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":4}"),
            (Terminal: "\"usage\":null", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":\"2\"}"),
            (Terminal: "\"usage\":null", Usage: "\"prompt_tokens_details\":{\"cached_tokens\":2,\"cached_tokens\":2}"),
            (Terminal: "\"usage\":null", Usage: "\"prompt_tokens_details\":{},\"prompt_tokens_details\":{}")
        })
        {
            using var fixture = new Fixture { StreamText =
                WithRootFields(Frame(new { content = Visible }, "stop"), sample.Terminal) + WithUsageFields(Usage(), sample.Usage) };
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Error is null && callbacks.SequenceEqual([Visible]) &&
                fixture.Pool.AvailableCount == 1 && result.PromptCache is { PreparedInputTokens: 3,
                    CachedTokens: null, ProcessedPromptTokens: null, PromptProcessingMilliseconds: null,
                    ReuseRatio: null, Diagnostic.Length: > 0 and <= 256 },
                "conflicting or malformed optional reasoning cache data cannot prevent adoption of independently valid output");
        }
        using (var fixture = new Fixture { TokenCount = 1700 })
        {
            await using var prepared = await fixture.PrepareAsync(Request() with { AllowResponseClamping = true });
            Check(prepared.Metadata.ResponseBudget == 92 && prepared.Metadata.CombinedGenerationBudget == 220 &&
                prepared.Metadata.UnallocatedTokens == 0, "clamping subtracts reasoning and safety reserves from actual headroom");
            Check(fixture.Counted.Count == 2 && fixture.Counted[1].GetProperty("max_tokens").GetInt32() == 220,
                "changed generation cap recreates and recounts the final body");
            fixture.StreamText = Success(input: 1700);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && JsonElement.DeepEquals(fixture.Counted[1], fixture.Submitted!.Value),
                "generation consumes precisely the final recounted body");
        }
        using (var fixture = new Fixture { TokenCount = 1700, ChangeSecondCount = true })
        {
            await ThrowsAsync<LlamaProtocolException>(() => fixture.PrepareAsync(Request() with { AllowResponseClamping = true }));
            Check(fixture.CompletionCalls == 0 && fixture.Pool.AvailableCount == 1,
                "unstable final count dispatches no inference and releases preparation");
        }
        using (var fixture = new Fixture { TokenCount = 1900 })
        {
            await ThrowsAsync<ModelContextLimitException>(() => fixture.PrepareAsync(Request() with { AllowResponseClamping = true }));
            Check(fixture.CompletionCalls == 0 && fixture.Pool.AvailableCount == 1,
                "reasoning reserve cannot consume input or minimum output capacity");
        }
        using (var fixture = new Fixture { CountStatus = HttpStatusCode.BadRequest })
        {
            await ThrowsAsync<LlamaProtocolException>(() => fixture.PrepareAsync(Request()));
            Check(fixture.CompletionCalls == 0 && fixture.Pool.AvailableCount == 1,
                "unsupported count/template/schema fails without an unmeasured fallback");
        }
        using (var fixture = new Fixture())
        {
            var messages = new List<ModelInputMessage> { new("user", [new TextContentPart("Before")]) };
            using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
            var preparing = fixture.PrepareAsync(Request() with { Messages = messages, ResponseSchema = schema.RootElement });
            messages[0] = new("user", [new TextContentPart("After")]); schema.Dispose();
            await using var prepared = await preparing;
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && fixture.Submitted!.Value.GetProperty("messages")[0].GetProperty("content").GetString() == "Before",
                "messages and schema document lifetime cannot mutate a prepared request");
        }
        async Task RejectStream(string stream, string description, bool quarantined = false)
        {
            using var fixture = new Fixture { StreamText = stream };
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && callbacks.Count == 0 &&
                !JsonSerializer.Serialize(result).Contains(Private, StringComparison.Ordinal), description);
            Check(quarantined ? fixture.Pool.Quarantined.Count == 1 : fixture.Pool.AvailableCount == 1,
                "invalid output releases only acknowledged, confirmed-idle capacity");
        }
        await RejectStream(Success(input: 4), "prompt count mismatch cannot adopt an otherwise valid action");
        await RejectStream(Success(generated: 385), "provider cannot exceed the combined prepared ceiling");
        await RejectStream(Frame(new { content = Visible }, "stop") + "data: [DONE]\n\n", "terminal without usage cannot claim measured completion");
        await RejectStream(Frame(new { content = Visible }) + Usage() + "data: [DONE]\n\n", "usage before terminal is invalid");
        await RejectStream(Frame(new { reasoning_content = Private }) + Frame(new { content = Visible }, "stop", id: "other") + Usage(id: "other"),
            "changed completion identity cannot adopt output");
        await RejectStream(Frame(new { content = Visible }, "stop") + Usage(id: "other"), "usage identity must match terminal");
        await RejectStream("data: [DONE]\n\n", "DONE alone proves neither admission nor completion", quarantined: true);
        await RejectStream(Frame(new { reasoning_content = Private }, "stop") + Usage(), "reasoning-only completion supplies no visible action");
        await RejectStream(Frame(new { content = "<think>" + Private + "</think>" + Visible }, "stop") + Usage(),
            "mixed malformed structured output is never published or echoed in diagnostics");
        await RejectStream(Frame(new { content = JsonSerializer.Serialize(new { action = "final", text = "<think>" + Private + "</think>" }) }, "stop") + Usage(),
            "tagged private content is rejected even inside a syntactically valid JSON object");
        await RejectStream(Frame(new { content = Visible }, "tool_calls") + Usage(), "provider tool-call finish is not a visible structured action");
        await RejectStream(Frame(new { content = Visible }, "stop") + Usage() + Usage(), "duplicate terminal usage is rejected");
        await RejectStream(Frame(new { content = Visible }, "stop").Replace("\"id\":\"completion\"",
            "\"id\":\"completion\",\"id\":\"completion\"", StringComparison.Ordinal) + Usage(),
            "optional cache degradation does not relax uniqueness of required completion identity", quarantined: true);
        await RejectStream(Frame(new { content = Visible }, "stop") + Usage().Replace("\"prompt_tokens\":3",
            "\"prompt_tokens\":3,\"prompt_tokens\":3", StringComparison.Ordinal),
            "optional cache degradation does not relax uniqueness of required prompt-token evidence");
        await RejectStream("data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"" + Private + "\"},\"finish_reason\":\"stop\"}]}\n\n",
            "missing completion identity proves no admission", quarantined: true);
        using (var fixture = new Fixture { StreamText = Frame(new { content = "not-json" }, "stop") + Usage() })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && result.Text == "" && result.GeneratedTokens == 7 && result.EvaluatedTokens == 3,
                "invalid structured content retains real terminal usage while withholding all text");
        }
        using (var fixture = new Fixture { GenerationStatus = HttpStatusCode.BadRequest })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && result.Text == "" && fixture.Pool.AvailableCount == 1 &&
                result.Error!.Contains("HTTP 400", StringComparison.Ordinal) && !JsonSerializer.Serialize(result).Contains(Private, StringComparison.Ordinal),
                "explicit bounded server rejection confirms idle without echoing its raw body");
        }
        using (var fixture = new Fixture { StreamText = Frame(new { content = "partial" }, "length") + Usage() + "data: [DONE]\n\n" })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>(); var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Length && result.Text == "" && result.GeneratedTokens == 7 &&
                callbacks.Count == 0 && fixture.Pool.AvailableCount == 1, "length retains actual usage without retaining potentially unseparated partial text");
        }
        using (var fixture = new Fixture { CleanupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>(); var running = prepared.GenerateAsync(callbacks.Add, default);
            await fixture.CleanupRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!running.IsCompleted && callbacks.Count == 0 && fixture.Pool.AvailableCount == 0,
                "completed visible output waits for independent idle confirmation before publication");
            fixture.CleanupGate.SetResult(); var result = await running;
            Check(result.Finish == ModelGenerationFinish.Stop && callbacks.SequenceEqual([Visible]), "confirmed idle permits publication once");
        }
        using (var fixture = new Fixture { NeverIdle = true })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>(); var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && callbacks.Count == 0 && fixture.Pool.Quarantined.Count == 1,
                "unconfirmed terminal cleanup quarantines capacity and withholds content");
        }
        using (var fixture = new Fixture { CleanupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource(); var callbacks = new List<string>();
            var running = prepared.GenerateAsync(callbacks.Add, cancelled.Token);
            await fixture.CleanupRequested.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancelled.Cancel(); fixture.CleanupGate.SetResult();
            var result = await running;
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed && result.Text == "" &&
                result.GeneratedTokens == 7 && result.EvaluatedTokens == 3 && callbacks.Count == 0,
                "cancellation after terminal preserves real usage and awaits confirmed cleanup before withholding publication");
        }
        using (var fixture = new Fixture())
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var result = await prepared.GenerateAsync(null, cancelled.Token);
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed &&
                fixture.CompletionCalls == 0 && fixture.Pool.AvailableCount == 1, "pre-dispatch cancellation releases unused capacity");
        }
        using (var fixture = new Fixture { BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Prefix = Frame(new { reasoning_content = Private }) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource();
            var callbacks = new List<string>(); var running = prepared.GenerateAsync(callbacks.Add, cancelled.Token);
            await fixture.BodyWaiting.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancelled.Cancel();
            var result = await running;
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed && callbacks.Count == 0 &&
                fixture.Pool.AvailableCount == 1 && !JsonSerializer.Serialize(result).Contains(Private, StringComparison.Ordinal),
                "acknowledged reasoning cancellation confirms idle without publishing private or partial content");
        }
        using (var fixture = new Fixture { BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource(); var running = prepared.GenerateAsync(null, cancelled.Token);
            await fixture.HeadersReturned.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancelled.Cancel();
            var result = await running;
            Check(result.Finish == ModelGenerationFinish.Unknown && !result.CancellationConfirmed && fixture.Pool.Quarantined.Count == 1,
                "headers without acknowledgement do not authorize optimistic slot release");
        }
        using (var fixture = new Fixture { BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Prefix = Frame(new { reasoning_content = Private }) })
        {
            var prepared = await fixture.PrepareAsync(Request()); var running = prepared.GenerateAsync(null, default);
            await fixture.BodyWaiting.Task.WaitAsync(TimeSpan.FromSeconds(3)); await prepared.DisposeAsync();
            var result = await running;
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed && fixture.Pool.AvailableCount == 1,
                "disposing an active preparation waits for confirmed stop");
            await prepared.DisposeAsync();
        }
        using (var fixture = new Fixture())
        {
            var prepared = await fixture.PrepareAsync(Request()); await prepared.DisposeAsync();
            Check(fixture.Pool.AvailableCount == 1 && fixture.CompletionCalls == 0, "disposing an unused preparation releases without inference");
            await ThrowsAsync<ObjectDisposedException>(() => prepared.GenerateAsync(null, default)); checks++;
            await ThrowsAsync<ArgumentException>(() => fixture.PrepareAsync(Request() with { ReasoningTokens = 0 })); checks++;
        }
        using (var fixture = new Fixture())
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => fixture.PrepareAsync(Request(), cancelled.Token));
            Check(fixture.Pool.AvailableCount == 1 && fixture.Counted.Count == 0 && fixture.CompletionCalls == 0,
                "cancelled preparation releases its lease before count or inference");
        }
        Console.WriteLine($"Prepared reasoning: {checks} deterministic HTTP/usage/privacy/lifecycle checks passed; no real model inference.");
    }

    private static ModelGenerationRequest Request() => new("fixture", [new("user", [new TextContentPart("Hello")])],
        2048, 256, ResponseSchema: JsonSerializer.SerializeToElement(new { type = "object" })) { ReasoningTokens = 128 };
    private static string Frame(object delta, string? finish = null, string id = "completion") =>
        "data: " + JsonSerializer.Serialize(new { id, choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";
    private static string Usage(int input = 3, int generated = 7, string id = "completion") =>
        "data: " + JsonSerializer.Serialize(new { id, choices = Array.Empty<object>(),
            usage = new { prompt_tokens = input, completion_tokens = generated, total_tokens = input + generated } }) + "\n\n";
    private static string WithRootFields(string frame, string fields) => frame.TrimEnd()[..^1] + "," + fields + "}\n\n";
    private static string WithUsageFields(string frame, string fields) =>
        frame.Replace("\"total_tokens\":10", "\"total_tokens\":10," + fields, StringComparison.Ordinal);
    private static string Success(int input = 3, int generated = 7) => Frame(new { reasoning_content = Private }) +
        Frame(new { content = Visible[..8] }) + Frame(new { content = Visible[8..] }, "stop") + Usage(input, generated) + "data: [DONE]\n\n";
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Prepared reasoning expected " + typeof(T).Name);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public readonly LlamaSlotPool Pool = new(1);
        private readonly HttpClient http;
        public readonly List<JsonElement> Counted = [];
        public int TokenCount = 3, CompletionCalls;
        public bool ChangeSecondCount, NeverIdle;
        public HttpStatusCode CountStatus = HttpStatusCode.OK, GenerationStatus = HttpStatusCode.OK;
        public JsonElement? Submitted;
        public string StreamText = Success(), Prefix = "";
        public TaskCompletionSource? BodyGate, CleanupGate;
        public readonly TaskCompletionSource HeadersReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource BodyWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CleanupRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool connectionClosed;
        private readonly LlamaProtocolTiming timing = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(1));
        public Fixture() => http = new(this, disposeHandler: false) { BaseAddress = new("http://reasoning.fixture/"), Timeout = Timeout.InfiniteTimeSpan };
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token = default)
        {
            var slot = await Pool.AcquireAsync(() => { }, default);
            return await LlamaPreparedReasoningGeneration.PrepareAsync(http, slot, "fixture-instance", request, [], () => false, token, timing);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/slots")
            {
                if (CompletionCalls > 0)
                {
                    CleanupRequested.TrySetResult();
                    if (CleanupGate is not null) await CleanupGate.Task.WaitAsync(cancellationToken);
                }
                var processing = CompletionCalls > 0 && (NeverIdle || !connectionClosed);
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new[] { new { id = 0, n_ctx = 2048, is_processing = processing, id_task = processing ? 7 : -1 } }));
            }
            using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (path == "/v1/chat/completions/input_tokens")
            {
                Counted.Add(parsed.RootElement.Clone());
                return CountStatus == HttpStatusCode.OK
                    ? Json(CountStatus, JsonSerializer.Serialize(new { @object = "response.input_tokens", input_tokens = TokenCount + (ChangeSecondCount && Counted.Count > 1 ? 1 : 0) }))
                    : Json(CountStatus, JsonSerializer.Serialize(new { error = Private }));
            }
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected reasoning fixture endpoint.");
            CompletionCalls++; Submitted = parsed.RootElement.Clone();
            if (GenerationStatus != HttpStatusCode.OK)
            {
                connectionClosed = true;
                return Json(GenerationStatus, JsonSerializer.Serialize(new { error = Private }));
            }
            Stream stream = BodyGate is null
                ? new ObservedStream(Encoding.UTF8.GetBytes(StreamText), () => connectionClosed = true)
                : new GatedStream(Prefix, StreamText, BodyGate.Task, BodyWaiting, () => connectionClosed = true);
            var content = new StreamContent(stream); content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            HeadersReturned.TrySetResult(); return new(HttpStatusCode.OK) { Content = content };
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string text) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ObservedStream(byte[] bytes, Action closed) : MemoryStream(bytes)
    {
        protected override void Dispose(bool disposing) { if (disposing) closed(); base.Dispose(disposing); }
    }
    private sealed class GatedStream(string prefix, string text, Task ready, TaskCompletionSource waiting, Action closed) : Stream
    {
        private readonly MemoryStream first = new(Encoding.UTF8.GetBytes(prefix));
        private readonly MemoryStream rest = new(Encoding.UTF8.GetBytes(text));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (first.Position < first.Length) return await first.ReadAsync(buffer, cancellationToken);
            waiting.TrySetResult(); await ready.WaitAsync(cancellationToken); return await rest.ReadAsync(buffer, cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { closed(); first.Dispose(); rest.Dispose(); } base.Dispose(disposing); }
    }
}
