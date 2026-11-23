using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Native multimodal adapter tests; deterministic protocol and actual local runtime lanes are distinct.</summary>
public static class ResourceModelTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool result, string text) { if (!result) throw new Exception(text); checks++; }
        using var protocol = new VisionProtocol();
        using var http = new HttpClient(protocol) { BaseAddress = new Uri("http://localhost/") };
        var pool = new LlamaSlotPool(1);
        var request = Request("Describe the image.");
        var messages = JsonSerializer.SerializeToElement(new[] { new { role = "user", content = "image fixture" } });
        async Task<IPreparedModelGeneration> Prepare() => await LlamaPreparedGeneration.PrepareMultimodalAsync(http,
            await pool.AcquireAsync(() => { }, CancellationToken.None), "instance", request, messages,
            ["fixture-image-bytes"], [], () => false, CancellationToken.None);
        await using (var prepared = await Prepare())
        {
            Check(prepared.Metadata.InputTokens == 112 && prepared.Metadata.AdapterId == "llama.cpp.native.vision", "native count is reflected in prepared metadata");
            Check(protocol.Generated == 0, "preparation must not sample a token");
            var result = await prepared.GenerateAsync(null, CancellationToken.None);
            Check(result.Finish == ModelGenerationFinish.Stop && result.EvaluatedTokens == 112, "matching native final count is adopted");
            Check(protocol.Prompt.GetProperty("multimodal_data")[0].GetString() == "fixture-image-bytes", "native image data remains in transport");
            Check(protocol.Prompt.GetProperty("prompt_string").GetString() == "exact templated input", "same rendered input is sent");
        }
        Check(pool.AvailableCount == 1, "completed request releases capacity");
        protocol.ReturnedCount = 113;
        await using (var mismatch = await Prepare())
            Check((await mismatch.GenerateAsync(null, CancellationToken.None)).Finish == ModelGenerationFinish.Unknown, "changed visual input count cannot be adopted");
        protocol.ReturnedCount = 112; protocol.Multimodal = false;
        Check(await Reject<NotSupportedException>(async () => { await using var rejected = await Prepare(); }), "unavailable native capability is rejected");
        Check(pool.AvailableCount == 1, "failed capability check releases capacity");
        protocol.Multimodal = true; protocol.Count = 8190;
        Check(await Reject<ModelContextLimitException>(async () => { await using var rejected = await Prepare(); }), "image count plus reserved response enforces context");
        protocol.Count = 112;
        request = request with { ResponseSharePercent = 20, ResponseLimit = 4096 };
        await using (var balance = await Prepare())
            Check(balance.Metadata.ResponseBudget == 1638, "response share uses effective model capacity");

        var root = Path.Combine(Path.GetTempPath(), "cerebral-resource-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new StateStore(root);
            var attachments = new AttachmentStore(new EventJournal(Path.Combine(root, "events.db")));
            var scope = new AttachmentAccessContext("lead", "chat");
            var canonicalImage = Image();
            using var bytes = new MemoryStream(canonicalImage);
            var reference = await attachments.ImportAsync(scope, "image-import", "colors.png", bytes);
            using var text = new MemoryStream(Encoding.UTF8.GetBytes("exact attachment text"));
            var textReference = await attachments.ImportAsync(scope, "text-import", "notes.txt", text);

            var capture = new CapturingMultimodalRuntime();
            var remoteLike = new LlamaResourceModelAdapter(capture, attachments, scope);
            var resolvedRequest = Request("unused") with
            {
                Messages =
                [
                    new("system", [new TextContentPart("Exact guidance")]),
                    new("user",
                    [
                        new FileContentPart(textReference),
                        new ImageContentPart(reference, "claimed colors"),
                        new TextContentPart("after image")
                    ])
                ]
            };
            await using (var captured = await remoteLike.PrepareAsync(resolvedRequest, default)) { }
            Check(remoteLike.Descriptor.ContentCapabilities.Contains("image", StringComparer.Ordinal) &&
                capture.TextCalls == 0 && capture.MultimodalCalls == 1, "a V2-style nonlocal adapter advertises and receives native image input");
            var normalized = capture.Request!;
            var capturedImages = capture.Images!;
            var normalizedImage = normalized.Messages[1].Content[1] as ImageContentPart;
            var payload = capturedImages.Single();
            Check(normalized.Messages[0] is { Role: "system", Content.Count: 1 } &&
                normalized.Messages[1].Content[0] is TextContentPart expandedFile && expandedFile.Text.Contains("exact attachment text", StringComparison.Ordinal) &&
                normalizedImage is { Width: 256, Height: 256 } && normalized.Messages[1].Content[2] is TextContentPart alt &&
                alt.Text.Contains("not visual proof", StringComparison.Ordinal) && normalized.Messages[1].Content[3] is TextContentPart { Text: "after image" },
                "resolver preserves typed image position after bounded file expansion and system normalization");
            Check(payload is { MessageIndex: 1, ContentIndex: 1, MediaType: "image/png", Width: 256, Height: 256 } &&
                payload.Data.SequenceEqual(canonicalImage), "resolver supplies exact admitted bytes and authoritative coordinates");
            Check(capture.BorrowedData!.Single().All(value => value == 0), "resolver clears its temporary image array after preparation returns");
            var normalizedJson = JsonSerializer.Serialize(normalized, CerebralJson.Options);
            Check(!normalizedJson.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                !normalizedJson.Contains(Convert.ToBase64String(canonicalImage), StringComparison.Ordinal),
                "normalized request contains neither a storage path nor embedded image bytes");
            var projected = ModelRuntime.ProjectMultimodalInput(normalized, capturedImages);
            var encoded = Convert.ToBase64String(canonicalImage);
            Check(projected.Media.SequenceEqual(new[] { encoded }) && projected.Messages[1].GetProperty("content")[1]
                .GetProperty("image_url").GetProperty("url").GetString() == "data:image/png;base64," + encoded,
                "local projection derives the exact llama data URL and raw multimodal payload from the same bytes");
            var corruptData = payload.Data.ToArray(); corruptData[0] = 0;
            try
            {
                Check(RejectSync<InvalidDataException>(() => ModelRuntime.ProjectMultimodalInput(normalized,
                    [payload with { Data = corruptData }])), "native boundary reinspects PNG framing");
                Check(RejectSync<InvalidDataException>(() => ModelRuntime.ProjectMultimodalInput(normalized,
                    [payload with { ContentIndex = 0 }])), "native boundary rejects positional drift");
            }
            finally { Array.Clear(corruptData); }
            var tooMany = Request("unused") with
            {
                Messages = [new("user", Enumerable.Repeat<ContentPart>(new ImageContentPart(reference), 5).ToArray())]
            };
            Check(await Reject<InvalidDataException>(async () => { await using var p = await remoteLike.PrepareAsync(tooMany, default); }),
                "resolver enforces the four-image request bound before dispatch");
            Check(capture.MultimodalCalls == 1, "rejected image-count input never reaches the runtime adapter");
            Check(await Reject<ModelCapabilityReadinessException>(async () => { await using var p = await remoteLike.PrepareAsync(
                resolvedRequest with { ReasoningTokens = 128 }, default); }) && capture.MultimodalCalls == 1,
                "unsupported image reasoning has a host-safe readiness diagnostic and remains rejected before resolution");
            Check(await Reject<NotSupportedException>(async () => { await using var p = await remoteLike.PrepareAsync(
                resolvedRequest with { Messages = [new("system", [new ImageContentPart(reference)])] }, default); }),
                "system images remain rejected before dispatch");
            foreach (var item in capturedImages) Array.Clear(item.Data);

            await using var runtime = new ModelRuntime(state, () => new HostSettings(), new EventHub());
            var adapter = new LlamaResourceModelAdapter(runtime, attachments, scope);
            var withImage = Request("Describe the image.") with { Messages = [new("user", [new ImageContentPart(reference)])] };
            Check(await Reject<KeyNotFoundException>(async () => { await using var p = await new LlamaResourceModelAdapter(runtime, attachments, new("lead", "other")).PrepareAsync(withImage, default); }), "foreign conversation cannot resolve original image");
            Check(await Reject<InvalidDataException>(async () => { await using var p = await adapter.PrepareAsync(withImage with { Messages = [new("user", [new ImageContentPart(reference with { OriginalName = "changed.png" })])] }, default); }), "forged metadata is rejected before loading");
            Check(await Reject<InvalidDataException>(async () => { await using var p = await adapter.PrepareAsync(withImage with { Messages = [new("user", [new ImageContentPart(reference, Width: 12)])] }, default); }), "display dimensions cannot override admitted dimensions");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check(await Reject<OperationCanceledException>(async () => { await using var p = await adapter.PrepareAsync(withImage, cancelled.Token); }), "cancelled resolution does not start inference");
            state.Put("model", "vision", new ModelRecord { Id="vision" });
            Check(await Reject<NotSupportedException>(async () => { await using var p = await adapter.PrepareAsync(withImage, default); }), "text-only model does not pretend a projector exists");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"Resource model: {checks} deterministic protocol/SQLite checks passed; no real inference in this lane.");
    }

    public static async Task RunLiveAsync(string modelPath, string serverPath, string projectorPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-resource-model-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new StateStore(root);
            var settings = new HostSettings { LlamaServerPath = Path.GetFullPath(serverPath), ReservedMemoryMb = 1024 };
            state.Put("model", "vision", new ModelRecord { Id="vision", Name="Native image acceptance", Path=Path.GetFullPath(modelPath),
                VisionProjectorPath=Path.GetFullPath(projectorPath), SizeBytes=new FileInfo(modelPath).Length,
                ContextTokens=8192, MaximumContextTokens=8192, AutomaticContextSizing=false, ParallelRequests=1, IdleMinutes=0 });
            var path = Path.Combine(root, "events.db");
            var access = new AttachmentAccessContext("lead", "image-chat");
            AttachmentReference reference;
            using (var source = new MemoryStream(Image()))
                reference = await new AttachmentStore(new EventJournal(path)).ImportAsync(access, "visual-import", "colors.png", source);
            // Resolve through a reopened store; neither a file path nor base64 is in conversation data.
            var resources = new AttachmentStore(new EventJournal(path));
            await using var runtime = new ModelRuntime(state, () => settings, new EventHub());
            var adapter = new LlamaResourceModelAdapter(runtime, resources, access);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{"left":{"type":"string","enum":["red","blue","green","yellow","unknown"]},"right":{"type":"string","enum":["red","blue","green","yellow","unknown"]}},"required":["left","right"],"additionalProperties":false}""");
            var input = Request("Which color is on the left and which is on the right?") with
            { Messages = [new("system", [new TextContentPart("Describe only the supplied image concisely.")]),
                new("user", [new TextContentPart("Identify the colored rectangles. Return only JSON with left and right color names."), new ImageContentPart(reference)])], ResponseSchema=schema.RootElement };
            await using (var prepared = await adapter.PrepareAsync(input, timeout.Token))
            {
                var result = await prepared.GenerateAsync(null, timeout.Token);
                using var answer = JsonDocument.Parse(result.Text);
                if (result.Finish != ModelGenerationFinish.Stop || result.EvaluatedTokens != prepared.Metadata.InputTokens ||
                    answer.RootElement.GetProperty("left").GetString() != "red" || answer.RootElement.GetProperty("right").GetString() != "blue")
                    throw new Exception($"Actual visual answer failed: finish={result.Finish}, error={result.Error}, text={result.Text}");
                Console.WriteLine($"Actual image: {result.Text} input={prepared.Metadata.InputTokens}; evaluated={result.EvaluatedTokens}; output={result.GeneratedTokens}; finish={result.Finish}");
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            await using (var next = await adapter.PrepareAsync(input with { ResponseLimit=512, ResponseSchema=null, Messages=[new("user", [new TextContentPart("Describe the image in at least ten detailed sentences, discussing shapes, positions, sizes and colors."),new ImageContentPart(reference)])] }, timeout.Token))
            {
                var result = await next.GenerateAsync(_ => cancellation.Cancel(), cancellation.Token);
                if (result.Finish != ModelGenerationFinish.Cancelled || !result.CancellationConfirmed)
                    throw new Exception("Native image generation did not confirm cancellation: " + result.Finish + " " + result.Error);
                Console.WriteLine("Actual image cancellation confirmed; same model remained loaded.");
            }
            await runtime.UnloadAsync("vision", timeout.Token);
            if (runtime.IsLoaded("vision")) throw new Exception("Owned image model remained loaded.");
            Console.WriteLine("Resource model live: scoped SQLite reopen, real visual inference, exact count, cancellation and unload passed. No microphone or desktop claim.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static ModelGenerationRequest Request(string text) => new("vision", [new("user", [new TextContentPart(text)])],8192,128,Temperature:0);
    private static async Task<bool> Reject<T>(Func<Task> action) where T:Exception
    { try { await action(); return false; } catch(T) { return true; } }
    private static bool RejectSync<T>(Action action) where T:Exception
    { try { action(); return false; } catch(T) { return true; } }

    private sealed class CapturingMultimodalRuntime : IModelRuntimeAdapter, IMultimodalModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("remote-image-fixture", "2.0.0", ["text", "image"]);
        public int TextCalls;
        public int MultimodalCalls;
        public ModelGenerationRequest? Request;
        public IReadOnlyList<RemotePreparedImageInput>? Images;
        public IReadOnlyList<byte[]>? BorrowedData;

        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); TextCalls++;
            return Task.FromResult<IPreparedModelGeneration>(new CapturedLease(request));
        }

        public Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
            IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); MultimodalCalls++; Request = request;
            BorrowedData = images.Select(image => image.Data).ToArray();
            Images = images.Select(image => image with { Data = image.Data.ToArray() }).ToArray();
            return Task.FromResult<IPreparedModelGeneration>(new CapturedLease(request));
        }

        private sealed class CapturedLease(ModelGenerationRequest request) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("fixture", "remote-image-fixture", "2.0.0",
                request.ModelId, 1, request.ContextLimit, request.ResponseLimit, "captured exact image input");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token) =>
                throw new InvalidOperationException("The resolver fixture never generates.");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class VisionProtocol : HttpMessageHandler
    {
        public int Count=112, ReturnedCount=112, Generated;
        public bool Multimodal=true;
        public JsonElement Prompt;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string output; var type="application/json";
            switch(request.RequestUri!.AbsolutePath)
            {
                case "/slots": output="""[{"id":0,"n_ctx":8192,"is_processing":false}]"""; break;
                case "/v1/models": output=Multimodal ? """{"models":[{"capabilities":["multimodal"]}]}""" : """{"models":[{"capabilities":["completion"]}]}"""; break;
                case "/apply-template": output="""{"prompt":"exact templated input"}"""; break;
                case "/v1/chat/completions/input_tokens": output=JsonSerializer.Serialize(new { @object="response.input_tokens",input_tokens=Count }); break;
                case "/completion":
                    Generated++;
                    using(var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token))) Prompt=body.RootElement.GetProperty("prompt").Clone();
                    output="data: "+JsonSerializer.Serialize(new { content="red left, blue right",id_slot=0,stop=true,stop_type="eos",tokens_predicted=8,tokens_evaluated=ReturnedCount,truncated=false })+"\n\n";
                    type="text/event-stream"; break;
                default: throw new Exception("Unexpected protocol route " + request.RequestUri.AbsolutePath);
            }
            return new(HttpStatusCode.OK) { Content=new StringContent(output,Encoding.UTF8,type) };
        }
    }

    internal static byte[] Image()
    {
        using var result = new MemoryStream(); result.Write([137,80,78,71,13,10,26,10]);
        var header=new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header,256); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),256); header[8]=8; header[9]=2;
        Chunk("IHDR",header);
        using var compressed = new MemoryStream();
        using(var zip = new ZLibStream(compressed,CompressionLevel.Fastest,leaveOpen:true))
            for(var y=0;y<256;y++)
            {
                zip.WriteByte(0);
                for(var x=0;x<256;x++)
                {
                    byte[] color = x is >=24 and <112 && y is >=40 and <216 ? [230,25,25] :
                        x is >=144 and <232 && y is >=40 and <216 ? [25,50,230] : [250,250,250];
                    zip.Write(color);
                }
            }
        Chunk("IDAT",compressed.ToArray()); Chunk("IEND",[]); return result.ToArray();
        void Chunk(string name,byte[] bytes)
        {
            Span<byte> number=stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number,bytes.Length); result.Write(number);
            var kind=Encoding.ASCII.GetBytes(name); result.Write(kind); result.Write(bytes);
            uint crc=uint.MaxValue;
            foreach(var value in kind.Concat(bytes)) { crc^=value; for(var bit=0;bit<8;bit++) crc=(crc>>1)^((crc&1)==0?0:0xedb88320); }
            BinaryPrimitives.WriteUInt32BigEndian(number,~crc); result.Write(number);
        }
    }
}
