using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Contracts;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class PreparedStreamingTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        async Task Reject(Func<Task> action, string name)
        {
            var rejected = false;
            try { await action(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException) { rejected = true; }
            Check(rejected, name);
        }
        var json = new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false };
        var metadata = new PreparedModelMetadata("fixture-instance", "fixture.native", "1.0", "model", 21, 8192, 128, "fixture exact")
            { SafetyReserveTokens = 64 };
        var request = new RemotePreparedStreamRequest(1, Guid.NewGuid().ToString("N"), "fixture-worker", "fixture-controller",
            1, Guid.NewGuid().ToString("N"), RemotePreparedStreamingCodec.MetadataSha256(metadata));
        var output = "{\"action\":\"final\",\"text\":\"Hello café 🙂\"}";
        var terminal = new RemoteGeneratedResponse(1, request.LeaseId, metadata,
            new(ModelGenerationFinish.Stop, output, 8, 21));
        async Task<byte[]> Wire(params RemotePreparedStreamFrame[] frames)
        {
            using var stream = new MemoryStream();
            foreach (var frame in frames) await RemotePreparedStreamingCodec.WriteFrameAsync(stream, frame, default);
            return stream.ToArray();
        }
        var start = RemotePreparedStreamingCodec.Start(request, metadata);
        var text = RemotePreparedStreamingCodec.TextFrame(request, 1, output);
        var finish = RemotePreparedStreamingCodec.Terminal(request, 2, terminal);
        var visible = new List<string>();
        var wire = await Wire(start, text, finish);
        var read = await RemotePreparedStreamingCodec.ReadAsync(new MemoryStream(wire), request, visible.Add, default);
        Check(read == terminal && string.Concat(visible) == output, "private wire preserves actual callbacks and exact terminal metadata");
        Check(wire[^1] == 10 && !Encoding.UTF8.GetString(wire).Contains("\n  "), "frames use compact bounded NDJSON with LF termination");

        var capabilityOwner = new RemotePreparedStreamOwnerIdentity("fixture-worker", "fixture-controller");
        var capabilities = new RemotePreparedStreamCapabilities(1, "fixture-worker", "fixture-controller",
            "/api/inference/prepared/generate-stream", "application/x-ndjson", [1, 2], 4096, 65536, 131072,
            RemotePreparedProtocol.MaximumResponseBytes + 4096, 32 * 1024 * 1024, 4096, 16384);
        var parsedCapabilities = RemotePreparedStreamingCodec.ParseCapabilities(JsonSerializer.SerializeToUtf8Bytes(capabilities, json), capabilityOwner);
        Check(parsedCapabilities.WorkerInstallationId == capabilities.WorkerInstallationId &&
            parsedCapabilities.PreparedProtocolVersions.SequenceEqual(capabilities.PreparedProtocolVersions) &&
            parsedCapabilities.MaximumFrames == capabilities.MaximumFrames,
            "capabilities preserve exact owner and bounded protocol");
        Check(RemotePreparedStreamingCodec.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(request, json)) == request,
            "private request preserves all frozen comparison coordinates");
        foreach (var malformed in new[]
        {
            "{\"schemaVersion\":1,\"expectedWorkerInstallationId\":\"w\",\"expectedControllerInstallationId\":\"c\",\"profileId\":\"agent\"}",
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"expectedWorkerInstallationId\":\"w\",\"expectedControllerInstallationId\":\"c\"}",
            "{\"schemaVersion\":\"1\",\"expectedWorkerInstallationId\":\"w\",\"expectedControllerInstallationId\":\"c\"}",
            "{\"schemaVersion\":1,\"expectedWorkerInstallationId\":\"\\ud800\",\"expectedControllerInstallationId\":\"c\"}"
        })
            await Reject(() => { _ = RemotePreparedStreamingCodec.ParseCapabilitiesRequest(Encoding.UTF8.GetBytes(malformed)); return Task.CompletedTask; },
                "discovery rejects extra/duplicate/type/private-scalar input");
        await Reject(() => { _ = RemotePreparedStreamingCodec.ParseCapabilities(JsonSerializer.SerializeToUtf8Bytes(capabilities with { MaximumFrames = 8192 }, json), capabilityOwner); return Task.CompletedTask; },
            "capability limit changes fail before generation");
        await Reject(() => { _ = RemotePreparedStreamingCodec.ParseCapabilities(JsonSerializer.SerializeToUtf8Bytes(capabilities with { WorkerInstallationId = "other" }, json), capabilityOwner); return Task.CompletedTask; },
            "capability identity mismatch fails before generation");

        foreach (var pair in new[]
        {
            (await Wire(start, text with { Sequence = 2 }, finish), "sequence gap"),
            (await Wire(start, text with { LeaseId = Guid.NewGuid().ToString("N") }, finish), "foreign lease"),
            (await Wire(start, text with { StreamId = Guid.NewGuid().ToString("N") }, finish), "foreign stream"),
            (await Wire(start, text with { MetadataSha256 = new string('a',64) }, finish), "changed metadata"),
            (await Wire(start, text), "missing terminal"),
            (await Wire(start, text, finish, finish), "post-terminal data"),
            (await Wire(start, start), "repeated start")
        })
            await Reject(() => RemotePreparedStreamingCodec.ReadAsync(new MemoryStream(pair.Item1), request, _ => { }, default),
                "reader rejects " + pair.Item2);
        await Reject(() => RemotePreparedStreamingCodec.ReadAsync(new MemoryStream(wire[..^1]), request, _ => { }, default),
            "unterminated terminal does not become proof");
        await Reject(() => RemotePreparedStreamingCodec.ReadAsync(new MemoryStream(new byte[] { 255, 10 }), request, _ => { }, default),
            "invalid UTF8 fails without replacement text");
        await Reject(() => Wire(start, RemotePreparedStreamingCodec.TextFrame(request, 1, new string('x', 16385))),
            "writer enforces actual fragment bound");
        await Reject(() => Wire(start, RemotePreparedStreamingCodec.TextFrame(request, 1, "\ud800")),
            "writer rejects unpaired scalar");
        var canonicalWire = Encoding.UTF8.GetString(wire);
        foreach (var mutation in new[]
        {
            canonicalWire.Replace("\"inputTokens\":21", "\"inputTokens\":\"21\""),
            canonicalWire.Replace("\"combinedGenerationBudget\":128", "\"combinedGenerationBudget\":129"),
            canonicalWire.Replace("\"countBasis\":\"fixture exact\"", "\"countBasis\":\"fixture exact\",\"extra\":1"),
            canonicalWire.Replace("\"finish\":\"stop\"", "\"finish\":0"),
            canonicalWire.Replace("\"generatedTokens\":8", "\"generatedTokens\":\"8\"")
        })
            await Reject(() => RemotePreparedStreamingCodec.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(mutation)), request, null, default),
                "nested stream metadata/result rejects unknown, wrong types or contradictory derived values");
        var unknown = terminal with { Result = terminal.Result with { Finish = ModelGenerationFinish.Unknown } };
        var unknownRead = await RemotePreparedStreamingCodec.ReadAsync(new MemoryStream(await Wire(start,
            RemotePreparedStreamingCodec.Terminal(request, 1, unknown))), request, null, default);
        Check(unknownRead.Result.Finish == ModelGenerationFinish.Unknown, "valid terminal Unknown is preserved instead of success");

        using (var buffer = new RemotePreparedStreamBuffer(request))
        {
            buffer.Start(metadata); buffer.Append("{\"action\":\"final\",\"text\":\"");
            buffer.Append("🙂"); buffer.Append(" café\"}"); buffer.Complete(terminal);
            var frames = new List<RemotePreparedStreamFrame>();
            await foreach (var frame in buffer.ReadAllAsync(default)) frames.Add(frame);
            Check(frames[0].Kind == "start" && frames[^1].Kind == "terminal" &&
                string.Concat(frames.Where(x => x.Kind == "text").Select(x => x.Text)) == "{\"action\":\"final\",\"text\":\"🙂 café\"}",
                "genuine pending tail is flushed before terminal without dropped characters");
            Check(frames.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(0,frames.Count)), "coalescing preserves monotonic sequence");
        }
        using (var buffer = new RemotePreparedStreamBuffer(request))
        {
            buffer.Start(metadata);
            await using var reader = buffer.ReadAllAsync(default).GetAsyncEnumerator();
            Check(await reader.MoveNextAsync() && reader.Current.Kind == "start", "buffer acknowledges consumed lease before fragments");
            buffer.Append("\ud83d");
            var waiting = reader.MoveNextAsync().AsTask();
            Check(!waiting.IsCompleted, "split high surrogate remains provisional");
            buffer.Append("\ude42"); buffer.Complete(terminal);
            Check(await waiting && reader.Current.Text == "🙂", "surrogate pair is preserved across callback boundary");
        }
        {
            var buffer = new RemotePreparedStreamBuffer(request);
            buffer.Start(metadata);
            await using var reader = buffer.ReadAllAsync(default).GetAsyncEnumerator();
            await reader.MoveNextAsync();
            var waiting = reader.MoveNextAsync().AsTask();
            buffer.Dispose(); buffer.Dispose(); buffer.Append("ignored-after-owner-disposal");
            Check(!await waiting.WaitAsync(TimeSpan.FromSeconds(1)), "idempotent dispose wakes reader without disposing provider ownership");
        }
        using (var buffer = new RemotePreparedStreamBuffer(request))
        {
            buffer.Start(metadata); buffer.Fail(new IOException("fixture private failure")); buffer.Fail(new IOException());
            await Reject(async () => { await foreach (var _ in buffer.ReadAllAsync(default)) { } },
                "idempotent failure wakes reader and never fabricates terminal");
        }

        var binding = new RemotePreparedModelBinding("model", "/fixture/model.gguf", null, [], 8192, 1, -1);
        var modelRequest = new ModelGenerationRequest("model", [new("user", [new TextContentPart("public fixture")])], 8192, 128);
        var model = new ControlledModel();
        await using (var server = new RemotePreparedInference(model, (_, _) => Task.CompletedTask))
        {
            var p = CerebralJson.Read<RemotePreparedResponse>((await server.PrepareAsync(
                CerebralJson.Element(new RemotePrepareRequest(1, modelRequest, binding)), default)).Data);
            var claim = request with { LeaseId = p.LeaseId!, ExpectedMetadataSha256 = RemotePreparedStreamingCodec.MetadataSha256(p.Metadata) };
            using var buffer = new RemotePreparedStreamBuffer(claim);
            var generation = server.GenerateStreamingAsync(claim, buffer.Start, buffer.Append, default);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await using var reader = buffer.ReadAllAsync(default).GetAsyncEnumerator();
            Check(await reader.MoveNextAsync() && reader.Current.Kind == "start", "server streams same admitted lease");
            Check(await reader.MoveNextAsync() && reader.Current.Text == "real-first" && !generation.IsCompleted && model.Disposals == 0,
                "real provider callback arrives while generation remains active");
            model.Finish.TrySetResult();
            var result = await generation;
            Check(model.Generations == 1 && model.Disposals == 1, "one-use server disposes before terminal publication");
            buffer.Complete(result);
            await Reject(() => server.GenerateAsync(CerebralJson.Element(new RemoteLeaseRequest(1,p.LeaseId!)), default),
                "legacy path cannot replay a stream-consumed lease");
            var release = CerebralJson.Read<RemoteReleaseResponse>((await server.ReleaseAsync(
                CerebralJson.Element(new RemoteLeaseRequest(1,p.LeaseId!)), default)).Data);
            Check(release.Released && !release.CancellationRequested && model.Disposals == 1, "independent release does not double-dispose completed stream");
        }
        {
            var withheld = new ControlledModel();
            await using var server = new RemotePreparedInference(withheld, (_, _) => Task.CompletedTask);
            var p = CerebralJson.Read<RemotePreparedResponse>((await server.PrepareAsync(
                CerebralJson.Element(new RemotePrepareRequest(1,modelRequest,binding)), default)).Data);
            await Reject(() => server.GenerateStreamingAsync(request with { LeaseId=p.LeaseId!, ExpectedMetadataSha256=new string('a',64) }, _=>{}, _=>{}, default),
                "wrong complete metadata digest is rejected before lease consumption");
            Check(withheld.Generations == 0 && withheld.Disposals == 0, "metadata refusal holds unused preparation for exact cleanup");
            await server.ReleaseAsync(CerebralJson.Element(new RemoteLeaseRequest(1,p.LeaseId!)), default);
            Check(withheld.Disposals == 1, "unused refused lease releases independently");
        }

        var root = Path.Combine(Path.GetTempPath(),"cerebral-slots-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StateStore(root);
            await using var runtime = new ModelRuntime(store,()=>throw new Exception("normalization must not run"),new EventHub());
            var slots=runtime.ObserveManagedSlots();
            Check(slots.State=="observed" && slots.Models.Count==0 && slots.ModelAdmissionOpen==true &&
                !string.IsNullOrWhiteSpace(slots.ProcessGenerationId), "empty managed allocator observation is pure and not fabricated readiness");
            await Reject(() => { _=runtime.ObserveManagedSlots(257);return Task.CompletedTask; }, "slot observation stays bounded");
            await runtime.DisposeAsync();
            Check(runtime.ObserveManagedSlots().State=="unavailable", "disposed runtime capacity is unavailable instead of ready zero");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // Task-private sequential fixture process only.
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Fixture cleanup escaped its temporary root.");
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"PreparedStreamingTests: {checks} checks passed; deterministic fixtures only; no network/provider/model/Source calls.");
    }

    private sealed class ControlledModel : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.native","1.0",["text"]);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Generations,Disposals;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request,CancellationToken token) =>
            Task.FromResult<IPreparedModelGeneration>(new Prepared(this,request));
        private sealed class Prepared(ControlledModel owner,ModelGenerationRequest request):IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("fixture-instance","fixture.native","1.0",request.ModelId,21,8192,128,"fixture exact"){SafetyReserveTokens=64};
            public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText,CancellationToken token)
            {
                owner.Generations++;onText?.Invoke("real-first");owner.Entered.TrySetResult();
                await owner.Finish.Task.WaitAsync(token);onText?.Invoke("real-tail");
                return new(ModelGenerationFinish.Stop,"real-firstreal-tail",8,21);
            }
            public ValueTask DisposeAsync(){owner.Disposals++;return ValueTask.CompletedTask;}
        }
    }
}
