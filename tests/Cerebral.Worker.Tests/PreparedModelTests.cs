using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic protocol tests, without starting a model or claiming inference evidence.</summary>
public static class PreparedModelTests
{
    public static async Task RunAsync()
    {
        using (var fixture = new Fixture())
        {
            await using var prepared = await fixture.PrepareAsync(Request() with { ResponseSharePercent = 20 });
            Check(prepared.Metadata.InputTokens == 3 && prepared.Metadata.EffectiveContext == 2048 && prepared.Metadata.ResponseBudget == 409,
                "exact input count and bounded response share are retained in preparation");
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Text == "Hello" && result.GeneratedTokens == 2 && result.EvaluatedTokens == 3,
                "native terminal receipt supplies actual finish and usage");
            Check(result.PromptCache is null, "missing native cache telemetry is unavailable, never an inferred zero");
            Check(fixture.TemplateCalls == 1 && fixture.TokenizeCalls == 1 && fixture.CompletionCalls == 1,
                "one template and tokenization for the same generation");
            Check(fixture.Submitted!.Value.GetProperty("prompt").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(new[] { 1, 17, 31 }),
                "numeric prompt IDs are identical to the measured tokenizer result");
            Check(fixture.Submitted.Value.GetProperty("id_slot").GetInt32() == 0 && fixture.Submitted.Value.GetProperty("n_predict").GetInt32() == 409,
                "reserved slot and response allocation reach the provider");
            Check(fixture.Submitted.Value.GetProperty("cache_prompt").ValueKind == JsonValueKind.True &&
                !fixture.Submitted.Value.TryGetProperty("prompt_cache", out _) && !fixture.Submitted.Value.TryGetProperty("save_cache", out _),
                "native protocol explicitly requests resident prefix reuse without disk cache controls");
            Check(fixture.Submitted.Value.GetProperty("response_fields").EnumerateArray().Any(field => field.GetString() == "timings"),
                "native terminal timings are explicitly requested on the actual wire body");
            Check(callbacks.SequenceEqual(new[] { "Hello" }) && fixture.Pool.AvailableCount == 1,
                "empty progress does not reach the UI and confirmed completion releases capacity");
            await ThrowsAsync<InvalidOperationException>(() => prepared.GenerateAsync(null, default), "prepared generation cannot be reused");
        }
        foreach (var sample in new[]
        {
            (Fields: "\"timings\":{\"cache_n\":2,\"prompt_n\":1,\"prompt_ms\":12.5}", Cached: (int?)2, Processed: (int?)1, Time: (double?)12.5),
            (Fields: "\"timings\":{\"cache_n\":0,\"prompt_n\":3,\"prompt_ms\":0}", Cached: (int?)0, Processed: (int?)3, Time: (double?)0),
            (Fields: "\"timings\":{\"cache_n\":3,\"prompt_n\":0}", Cached: (int?)3, Processed: (int?)0, Time: (double?)null),
            (Fields: "\"timings\":{\"prompt_ms\":4}", Cached: (int?)null, Processed: (int?)null, Time: (double?)4)
        })
        {
            using var fixture = new Fixture { StreamText = WithRootFields(Frame("Hello", true, "eos", predicted: 2), sample.Fields) };
            await using var prepared = await fixture.PrepareAsync(Request());
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Text == "Hello" && result.PromptCache is
                { PreparedInputTokens: 3, Diagnostic: null } metrics && metrics.CachedTokens == sample.Cached &&
                metrics.ProcessedPromptTokens == sample.Processed && metrics.PromptProcessingMilliseconds == sample.Time &&
                metrics.ReuseRatio == (sample.Cached is { } cached ? (double)cached / 3 : null),
                "native metrics retain only reported reuse, processing count and finite timing against full prepared input");
            Check(prepared.Metadata.InputTokens == 3 && prepared.Metadata.ResponseBudget == 512 && fixture.Pool.AvailableCount == 1,
                "cache measurements do not subtract cached input from the budget or alter slot cleanup");
        }
        foreach (var fields in new[]
        {
            "\"timings\":{\"cache_n\":-1}", "\"timings\":{\"cache_n\":4}",
            "\"timings\":{\"cache_n\":2,\"prompt_n\":2}", "\"timings\":{\"prompt_n\":-1}",
            "\"timings\":{\"cache_n\":1.5}", "\"timings\":{\"cache_n\":\"2\"}",
            "\"timings\":{\"prompt_ms\":-0.1}", "\"timings\":{\"prompt_ms\":1e400}",
            "\"timings\":{\"prompt_ms\":\"NaN\"}", "\"timings\":null",
            "\"timings\":{\"cache_n\":2,\"cache_n\":2}", "\"timings\":{},\"timings\":{}",
            "\"timings\":{\"cache_n\":2},\"usage\":{\"prompt_tokens_details\":{\"cached_tokens\":1}}"
        })
        {
            using var fixture = new Fixture { StreamText = WithRootFields(Frame("Hello", true, "eos", predicted: 2), fields) };
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Error is null && result.Text == "Hello" &&
                callbacks.SequenceEqual(["Hello"]) && fixture.Pool.AvailableCount == 1 && result.PromptCache is
                { PreparedInputTokens: 3, CachedTokens: null, ProcessedPromptTokens: null, PromptProcessingMilliseconds: null,
                    ReuseRatio: null, Diagnostic.Length: > 0 and <= 256 },
                "malformed optional native telemetry is explicitly unavailable without changing valid output adoption or ownership");
        }
        using (var fixture = new Fixture())
        {
            using var schema = JsonDocument.Parse(PreparedModelLiveTests.ConformanceSchema);
            await using var prepared = await fixture.PrepareAsync(Request() with
            {
                ResponseLimit = 128, ResponseSchema = schema.RootElement
            });
            schema.Dispose(); // Preparation owns its clone even if the caller releases the source.
            await prepared.GenerateAsync(null, default);
            var sent = fixture.Submitted!.Value;
            using var expected = JsonDocument.Parse(PreparedModelLiveTests.ConformanceSchema);
            Check(sent.TryGetProperty("json_schema", out var actual) && JsonElement.DeepEquals(actual, expected.RootElement),
                "the exact nested native JSON schema, including case-sensitive minLength/maxLength and required fields, reaches the wire");
            Check(actual.GetProperty("properties").GetProperty("content").GetProperty("maxLength").GetInt32() == 8 &&
                sent.GetProperty("n_predict").GetInt32() == 128 && !sent.TryGetProperty("grammar", out _) &&
                !sent.TryGetProperty("response_format", out _),
                "native completion keeps explicit schema bounds and response limit without replacing them with an empty grammar or chat-only field");
            Check(fixture.TemplateInput!.Value.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").ValueKind == JsonValueKind.False,
                "the prepared non-reasoning route does not defer schema admission into a thinking block");
        }
        using (var fixture = new Fixture())
        {
            await using var prepared = await fixture.PrepareAsync(Request() with
            {
                Messages = [new("system", [new TextContentPart("Host guidance")]),
                    new("system", [new TextContentPart("Harness guidance")]), new("user", [new TextContentPart("Exact task")])]
            });
            var messages = fixture.TemplateInput!.Value.GetProperty("messages");
            Check(messages.GetArrayLength() == 2 && messages[0].GetProperty("role").GetString() == "system" &&
                messages[0].GetProperty("content").GetString() == "Host guidance\n\nHarness guidance" &&
                messages[1].GetProperty("content").GetString() == "Exact task",
                "leading host and harness guidance is preserved in one template-compatible system message");
        }
        using (var fixture = new Fixture())
        {
            await ThrowsAsync<LlamaProtocolException>(() => fixture.PrepareAsync(Request() with
            {
                Messages = [new("user", [new TextContentPart("Evidence")]), new("system", [new TextContentPart("Late instruction")])]
            }), "late system guidance is rejected instead of silently moving it across evidence");
            Check(fixture.TemplateCalls == 0 && fixture.Pool.AvailableCount == 1, "unsupported role ordering releases capacity before template execution");
        }
        using (var fixture = new Fixture { TokenCount = 1800 })
        {
            await using var prepared = await fixture.PrepareAsync(Request() with { ResponseLimit = 1024, ResponseSharePercent = 50 });
            Check(prepared.Metadata.InputTokens == 1800 && prepared.Metadata.DesiredResponseBudget == 1024 && prepared.Metadata.ResponseBudget == 120,
                "explicit balance clamps response to real headroom without removing input");
            Check(prepared.Metadata.IsBudgetClamped && prepared.Metadata.SafetyReserveTokens == 128 && prepared.Metadata.UnallocatedTokens == 0,
                "counter exposes desired and actual allocation with the runtime safety reserve");
        }
        using (var fixture = new Fixture { TokenCount = 1920 })
        {
            await ThrowsAsync<ArgumentException>(() => fixture.PrepareAsync(Request() with { ResponseSharePercent = 50 }),
                "no generation is admitted when required input and reserve leave no output room");
            await ThrowsAsync<ModelContextLimitException>(() => fixture.PrepareAsync(Request() with { AllowResponseClamping = true }),
                "explicit fixed-budget clamping cannot admit generation with no remaining output capacity");
            Check(fixture.Pool.AvailableCount == 1 && fixture.CompletionCalls == 0, "exhausted context releases preparation without generating");
        }
        using (var fixture = new Fixture { TokenCount = 1800 })
        {
            await ThrowsAsync<ArgumentException>(() => fixture.PrepareAsync(Request()), "an explicit fixed response limit still requires the full requested allocation");
            Check(!Request().AllowResponseClamping && fixture.Pool.AvailableCount == 1 && fixture.CompletionCalls == 0,
                "default fixed-budget behavior remains strict and releases the rejected reservation");
        }
        using (var fixture = new Fixture { TokenCount = 1800 })
        {
            var request = Request() with { AllowResponseClamping = true };
            await using var prepared = await fixture.PrepareAsync(request);
            Check(request.ResponseSharePercent is null && prepared.Metadata.InputTokens == 1800 &&
                prepared.Metadata.DesiredResponseBudget == 512 && prepared.Metadata.ResponseBudget == 120 &&
                prepared.Metadata.IsBudgetClamped && prepared.Metadata.SafetyReserveTokens == 128 && prepared.Metadata.UnallocatedTokens == 0,
                "explicit clamping admits a fixed-budget request at measured headroom while retaining its original desired output allocation");
            var result = await prepared.GenerateAsync(null, default);
            var submitted = fixture.Submitted!.Value;
            Check(result.Finish == ModelGenerationFinish.Stop && submitted.GetProperty("n_predict").GetInt32() == 120 &&
                submitted.GetProperty("prompt").EnumerateArray().Select(token => token.GetInt32()).SequenceEqual(Enumerable.Repeat(17, 1800)),
                "fixed-budget clamping sends the exact measured input token IDs and the reduced output budget without dropping input");
            Check(fixture.TemplateCalls == 1 && fixture.TokenizeCalls == 1 && fixture.CompletionCalls == 1 && fixture.Pool.AvailableCount == 1,
                "clamped fixed-budget generation reuses the prepared input once and releases its slot after confirmed completion");
        }
        using (var fixture = new Fixture { StreamText = Frame("short", true, "limit", predicted: 1) })
        {
            await using var prepared = await fixture.PrepareAsync(Request() with { ResponseLimit = 1 });
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Length && result.Text == "short" && result.GeneratedTokens == 1,
                "output limit retains partial text and actual token count without claiming completion");
        }
        using (var fixture = new Fixture { StreamText = Frame("", false) + "data: [DONE]\n\n" })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && result.GeneratedTokens is null && fixture.Pool.AvailableCount == 1,
                "DONE alone is not a native completion or invented usage");
        }
        using (var fixture = new Fixture { StreamText = Frame("", false, id: 1) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && fixture.Pool.Quarantined.Count == 1 && fixture.Pool.AvailableCount == 0,
                "a different slot cannot acknowledge or release this request");
            await ThrowsAsync<IOException>(() => fixture.Pool.AcquireAsync(() => { }, default), "all quarantined slots reject new work immediately");
        }
        using (var fixture = new Fixture { CompletionSlotBusy = true, StreamText = Frame("", false, id: -1) + Frame("Hello", false, id: -1) + Frame("", true, "eos", predicted: 2) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Text == "Hello" && callbacks.SequenceEqual(new[] { "Hello" }),
                "upstream partial -1 sentinel is correlated to an actively owned slot and exact terminal identity");
        }
        using (var fixture = new Fixture { StreamText = Frame("Hello", false, id: -1) + Frame("", true, "eos", predicted: 2) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var callbacks = new List<string>();
            var result = await prepared.GenerateAsync(callbacks.Add, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Text == "Hello" && callbacks.SequenceEqual(new[] { "Hello" }),
                "fast completion that is already idle retains pending text until the exact terminal receipt");
        }
        using (var fixture = new Fixture { CompletionSlotBusy = true, StreamText = Frame("Hello", false, id: -1) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource();
            var result = await prepared.GenerateAsync(_ => cancelled.Cancel(), cancelled.Token);
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed && fixture.Pool.AvailableCount == 1,
                "correlated partial sentinel supports confirmed cancellation without releasing running capacity");
        }
        using (var fixture = new Fixture { CompletionSlotBusy = true, StreamText = Frame("", false, id: -1).Replace("\"index\":0", "\"index\":1", StringComparison.Ordinal) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && fixture.Pool.Quarantined.Count == 1,
                "a malformed partial sentinel cannot establish admission through unrelated slot status");
        }
        using (var fixture = new Fixture { StreamText = Frame("Hello", true, "eos", id: -1, predicted: 2) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && fixture.Pool.Quarantined.Count == 1,
                "terminal receipt must identify the exact slot even when partial sentinels are supported");
        }
        using (var fixture = new Fixture())
        {
            var image = new ImageContentPart(new("image", 1, "image/png", "test.png", 1));
            await ThrowsAsync<NotSupportedException>(() => fixture.PrepareAsync(Request() with { Messages = [new("user", [image])] }),
                "native images are explicitly unavailable in the text adapter");
            Check(fixture.TemplateCalls == 0 && fixture.Pool.AvailableCount == 1, "unsupported content does not load template data or retain a reservation");
            await ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.PrepareAsync(Request() with { ResponseSharePercent = 51 }), "response ratio is bounded");
            await ThrowsAsync<ArgumentException>(() => fixture.PrepareAsync(Request() with { ContextLimit = 512, ResponseLimit = 500 }),
                "prepared input plus response and safety reserve must fit");
            Check(fixture.Pool.AvailableCount == 1 && fixture.CompletionCalls == 0, "failed preparation releases capacity without inference");
        }
        using (var fixture = new Fixture())
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var result = await prepared.GenerateAsync(null, cancelled.Token);
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed && fixture.CompletionCalls == 0 && fixture.Pool.AvailableCount == 1,
                "cancellation before dispatch releases an unused slot");
        }
        using (var fixture = new Fixture { StreamText = Frame("Hello", false) })
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.CleanupGate = idle.Task;
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource();
            var running = prepared.GenerateAsync(_ => cancelled.Cancel(), cancelled.Token);
            await fixture.CleanupRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!running.IsCompleted && fixture.Pool.AvailableCount == 0, "transport cancellation keeps capacity until provider idle is confirmed");
            idle.SetResult();
            var result = await running;
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed && fixture.Pool.AvailableCount == 1,
                "acknowledged cancellation releases only after its independent cleanup completes");
        }
        using (var fixture = new Fixture())
        {
            var body = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.BodyGate = body.Task; fixture.StreamText = Frame("", false);
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource();
            var running = prepared.GenerateAsync(null, cancelled.Token);
            await fixture.HeadersReturned.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancelled.Cancel();
            Check(!fixture.TransportCancelled && fixture.Pool.AvailableCount == 0, "HTTP headers do not establish admission or trigger an immediate disconnect");
            body.SetResult();
            var result = await running;
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed, "a later reserved-slot frame permits confirmed cancellation");
        }
        using (var fixture = new Fixture())
        {
            fixture.BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            fixture.Timing = new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1));
            await using var prepared = await fixture.PrepareAsync(Request());
            using var cancelled = new CancellationTokenSource();
            var running = prepared.GenerateAsync(null, cancelled.Token);
            await fixture.HeadersReturned.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancelled.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
            Check(result.Finish == ModelGenerationFinish.Unknown && !result.CancellationConfirmed && fixture.Pool.Quarantined.Count == 1,
                "lost admission acknowledgement quarantines capacity instead of trusting an idle snapshot");
            Check(!fixture.CleanupRequested.Task.IsCompleted, "pre-admission cancellation never polls idle as false proof");
        }
        using (var fixture = new Fixture { StreamText = Frame("Hello", false) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            await ThrowsAsync<ArgumentException>(() => prepared.GenerateAsync(_ => throw new ArgumentException("callback failure"), default),
                "consumer callback failure is preserved");
            Check(fixture.Pool.AvailableCount == 1, "callback failure still disconnects and confirms provider idle");
            // Dispose awaits the same faulted task; observe it explicitly instead of losing cleanup evidence.
            await ThrowsAsync<ArgumentException>(() => prepared.DisposeAsync().AsTask(), "disposing a failed callback observes the task failure");
        }
        using (var fixture = new Fixture { StreamText = new string('x', LlamaProtocol.MaximumFrameCharacters + 1) })
        {
            await using var prepared = await fixture.PrepareAsync(Request());
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && fixture.Pool.Quarantined.Count == 1,
                "oversized pre-acknowledgement frames remain bounded and cannot release uncertain capacity");
        }
        Console.WriteLine("Prepared model protocol checks passed (deterministic HTTP fixtures; no model inference).");
    }

    private static ModelGenerationRequest Request() => new("fixture", [new("user", [new TextContentPart("Hello")])], 2048, 512);
    private static string Frame(string text, bool stop, string? reason = null, int id = 0, int? predicted = 0) =>
        "data: " + JsonSerializer.Serialize(new { content = text, id_slot = id, index = 0, stop, stop_type = reason, tokens_predicted = predicted, tokens_evaluated = 3, truncated = false }) + "\n\n";
    private static string WithRootFields(string frame, string fields) => frame.TrimEnd()[..^1] + "," + fields + "}\n\n";
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Prepared model check failed: " + message); }
    private static async Task ThrowsAsync<T>(Func<Task> work, string message) where T : Exception
    {
        try { await work(); }
        catch (T) { return; }
        throw new InvalidOperationException("Prepared model check failed: " + message);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public readonly LlamaSlotPool Pool = new(1);
        private readonly HttpClient http;
        public int TemplateCalls, TokenizeCalls, CompletionCalls;
        public int TokenCount = 3;
        public JsonElement? Submitted;
        public JsonElement? TemplateInput;
        public string StreamText = Frame("", false) + Frame("Hello", false) + Frame("", true, "eos", predicted: 2);
        public Task? BodyGate, CleanupGate;
        public bool TransportCancelled;
        public bool CompletionSlotBusy;
        private bool connectionClosed;
        public LlamaProtocolTiming Timing = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));
        public readonly TaskCompletionSource HeadersReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CleanupRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture() => http = new(this, disposeHandler: false) { BaseAddress = new("http://model.fixture/"), Timeout = Timeout.InfiniteTimeSpan };
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request)
        {
            var slot = await Pool.AcquireAsync(() => { }, default);
            return await LlamaPreparedGeneration.PrepareAsync(http, slot, "fixture-instance", request, [], () => false, default, Timing);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/slots")
            {
                if (CompletionCalls > 0)
                {
                    CleanupRequested.TrySetResult();
                    if (CleanupGate is not null) await CleanupGate.WaitAsync(cancellationToken);
                }
                var processing = CompletionSlotBusy && CompletionCalls > 0 && !connectionClosed;
                return Json(JsonSerializer.Serialize(new[] { new { id = 0, n_ctx = 2048, is_processing = processing, id_task = processing ? 7 : -1 } }));
            }
            using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (path == "/apply-template") { TemplateCalls++; TemplateInput = parsed.RootElement.Clone(); return Json("{\"prompt\":\"<bos>Hello\"}"); }
            if (path == "/tokenize")
            {
                TokenizeCalls++;
                Check(parsed.RootElement.GetProperty("add_special").GetBoolean() && parsed.RootElement.GetProperty("parse_special").GetBoolean(), "tokenization matches native special-token behavior");
                return Json(TokenCount == 3 ? "{\"tokens\":[1,17,31]}" : JsonSerializer.Serialize(new { tokens = Enumerable.Repeat(17, TokenCount).ToArray() }));
            }
            if (path != "/completion") throw new InvalidOperationException("Unexpected fixture endpoint.");
            CompletionCalls++; Submitted = parsed.RootElement.Clone();
            cancellationToken.Register(() => TransportCancelled = true);
            // HttpClient detaches its SendAsync cancellation linkage after returning headers.
            // The fake server observes response-stream disposal, just as the real server observes
            // disconnect, rather than assuming that the handler's old token remains linked.
            var content = BodyGate is null
                ? new StreamContent(new ObservedMemoryStream(Encoding.UTF8.GetBytes(StreamText), () => connectionClosed = true))
                : new StreamContent(new GatedStream(StreamText, BodyGate, () => connectionClosed = true));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            HeadersReturned.TrySetResult();
            return new(HttpStatusCode.OK) { Content = content };
        }
        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ObservedMemoryStream(byte[] data, Action closed) : MemoryStream(data)
    {
        protected override void Dispose(bool disposing) { if (disposing) closed(); base.Dispose(disposing); }
    }

    private sealed class GatedStream(string text, Task ready, Action closed) : Stream
    {
        private readonly MemoryStream inner = new(Encoding.UTF8.GetBytes(text));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await ready.WaitAsync(cancellationToken); return await inner.ReadAsync(buffer, cancellationToken); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { closed(); inner.Dispose(); } base.Dispose(disposing); }
    }
}
