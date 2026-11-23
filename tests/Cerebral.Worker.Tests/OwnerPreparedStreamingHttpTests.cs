using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cerebral.Worker.Tests;

/// <summary>Real ephemeral loopback HTTP with deterministic prepared adapters. No Source,
/// native model, hardware or external network is exercised. Fake-handler deadlines are a separate tier.</summary>
public static class OwnerPreparedStreamingHttpTests
{
    private const string WorkerId = "11111111111111111111111111111111";
    private const string ControllerId = "22222222222222222222222222222222";
    private const string OtherId = "33333333333333333333333333333333";
    private const string SyntheticKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };

    public static async Task RunAsync(bool includeDeadlineChecks = true)
    {
        var networkChecks = 0; var unitChecks = 0;
        void Network(bool value, string name)
        { if (!value) throw new InvalidOperationException("Loopback HTTP fixture: " + name); networkChecks++; }
        void Unit(bool value, string name)
        { if (!value) throw new InvalidOperationException("HTTP handler fixture: " + name); Interlocked.Increment(ref unitChecks); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;

        await using (var fixture = await Fixture.CreateAsync(token))
        {
            var preparation = await fixture.PrepareAsync(token);
            var request = fixture.StreamRequest(preparation);
            var body = JsonSerializer.Serialize(request, Json);
            async Task Rejected(string name, int status, string? key = SyntheticKey, string? payload = null,
                string? path = null, string? header = null, string? headerValue = null, HttpMethod? method = null)
            {
                using var message = new HttpRequestMessage(method ?? HttpMethod.Post, path ?? OwnerPreparedStreamingHttp.GeneratePath);
                message.Content = new StringContent(payload ?? body, Encoding.UTF8, "application/json");
                if (key is not null) message.Headers.Add("X-Cerebral-Key", key);
                if (header is not null) message.Headers.Add(header, headerValue);
                using var reply = await fixture.Raw.SendAsync(message, token);
                Network((int)reply.StatusCode == status, name + " has the expected refusal status");
                Network(fixture.Adapter.Generations == 0 && fixture.Adapter.Disposals == 0,
                    name + " leaves the prepared lease unconsumed");
            }
            await Rejected("missing installation key on loopback", 403, key: null);
            await Rejected("wrong installation key", 403, key: new string('b', 48));
            await Rejected("wrong controller assignment", 403, payload: JsonSerializer.Serialize(request with { ExpectedControllerInstallationId = OtherId }, Json));
            await Rejected("wrong worker identity", 403, payload: JsonSerializer.Serialize(request with { ExpectedWorkerInstallationId = OtherId }, Json));
            await Rejected("browser Origin", 403, header: "Origin", headerValue: "http://fixture.invalid");
            await Rejected("peer-token context", 403, header: "X-Cerebral-Peer", headerValue: "fixture-peer-token");
            await Rejected("query alias", 400, path: OwnerPreparedStreamingHttp.GeneratePath + "?alias=1");
            await Rejected("trailing path alias", 400, path: OwnerPreparedStreamingHttp.GeneratePath + "/");
            await Rejected("case alias", 400, path: OwnerPreparedStreamingHttp.GeneratePath.ToUpperInvariant());
            await Rejected("unknown request field", 400, payload: body[..^1] + ",\"arbitraryCommand\":\"settings.save\"}");
            await Rejected("profile forwarding field", 400, payload: body[..^1] + ",\"profileId\":\"fixture-agent\"}");
            await Rejected("duplicate schema field", 400, payload: body[..^1] + ",\"schemaVersion\":1}");
            await Rejected("non-POST request", 405, method: HttpMethod.Get);
            await Rejected("oversized request", 413, payload: new string('x', OwnerPreparedStreamingHttp.MaximumRequestBytes + 1));
            fixture.SaveRole(standalone: false, coordinator: true);
            await Rejected("coordinator role", 403);
            fixture.SaveRole(standalone: true, coordinator: false);

            var capabilities = await fixture.Owner.GetPreparedStreamCapabilitiesAsync(new(WorkerId, ControllerId), token);
            Network(capabilities is { WorkerInstallationId: WorkerId, ControllerInstallationId: ControllerId } &&
                capabilities.GeneratePath == OwnerPreparedStreamingHttp.GeneratePath,
                "real owner client reads the exact capability and assigned identities over HTTP");
            var callback = NewSignal(); var received = new StringBuilder();
            var generation = fixture.Owner.GenerateStreamingAsync(request, text => { received.Append(text); callback.TrySetResult(); }, token);
            await callback.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Network(received.ToString() == FixtureAdapter.Prefix && !generation.IsCompleted && fixture.Adapter.Disposals == 0,
                "actual callback crosses HTTP before the fixture permits terminal completion");
            Network(fixture.Edge!.TrackedGenerations == 1 && fixture.Adapter.Generations == 1,
                "one consumed generation remains tracked while its provider fixture is held");
            fixture.Adapter.HoldDisposal = true;
            fixture.Adapter.Release.TrySetResult();
            await fixture.Adapter.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Network(!generation.IsCompleted && fixture.Adapter.Disposals == 0,
                "terminal cannot cross HTTP while exact prepared cleanup is still held");
            fixture.Adapter.AllowDispose.TrySetResult();
            var terminal = await generation.WaitAsync(TimeSpan.FromSeconds(5), token);
            Network(terminal.LeaseId == preparation.LeaseId && terminal.ProtocolVersion == preparation.ProtocolVersion &&
                terminal.Metadata == preparation.Metadata && terminal.Result.Text == FixtureAdapter.Output &&
                terminal.Result.Finish == ModelGenerationFinish.Stop && received.ToString() == FixtureAdapter.Output,
                "terminal retains full exact metadata/result and genuine callback tail over HTTP");
            Network(fixture.Adapter.Disposals == 1 && fixture.Adapter.DisposeCalls == 1,
                "prepared disposal completes exactly once before verified terminal publication");
            await UntilAsync(() => fixture.Edge.TrackedGenerations == 0, token);
            var repeated = false;
            try { await fixture.Owner.GenerateStreamingAsync(request with { StreamId = Guid.NewGuid().ToString("N") }, null, token); }
            catch (Exception error) when (TransportFailure(error)) { repeated = true; }
            Network(repeated && fixture.Adapter.Generations == 1 && fixture.Adapter.Disposals == 1,
                "another stream ID cannot replay the consumed prepared lease");
            Network(fixture.Configuration.GetInvocationCount == 0,
                "real capability/auth/generation edges never normalize fixture configuration");
        }

        await using (var fixture = await Fixture.CreateAsync(token))
        {
            var prepared = await fixture.PrepareAsync(token); var callback = NewSignal();
            var generation = fixture.Owner.GenerateStreamingAsync(fixture.StreamRequest(prepared), _ => callback.TrySetResult(), token);
            await callback.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            fixture.Settings.ManagementControllerId = OtherId; fixture.Store.Put("settings", "host", fixture.Settings);
            fixture.Adapter.Release.TrySetResult();
            var refused = false;
            try { await generation.WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch (Exception error) when (TransportFailure(error)) { refused = true; }
            await UntilAsync(() => fixture.Edge!.TrackedGenerations == 0, token);
            Network(refused && fixture.Adapter.Generations == 1 && fixture.Adapter.Disposals == 1,
                "owner change after public fragments prevents terminal adoption while still completing lease cleanup");
        }

        await using (var fixture = await Fixture.CreateAsync(token))
        {
            var prepared = await fixture.PrepareAsync(token);
            using var message = new HttpRequestMessage(HttpMethod.Post, OwnerPreparedStreamingHttp.GeneratePath)
            { Content = new StringContent(JsonSerializer.Serialize(fixture.StreamRequest(prepared), Json), Encoding.UTF8, "application/json") };
            message.Headers.Add("X-Cerebral-Key", SyntheticKey); message.Headers.Add("X-Fixture-Write-Fault", "1");
            var transport = ObserveRejectedTransportAsync(fixture.Raw.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token), token);
            await fixture.WriteFault.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await fixture.Adapter.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Network(fixture.Adapter.Generations == 1 && fixture.Adapter.Disposals == 0 && fixture.Edge!.TrackedGenerations == 1,
                "a real HTTP writer fault cancels the same generation but retains tracking until its actual cleanup returns");
            fixture.Adapter.Release.TrySetResult();
            Network(await transport.WaitAsync(TimeSpan.FromSeconds(5), token), "writer fault never produces usable terminal evidence");
            await UntilAsync(() => fixture.Edge!.TrackedGenerations == 0, token);
            Network(fixture.Adapter.Generations == 1 && fixture.Adapter.Disposals == 1,
                "writer-fault cleanup observes one provider fixture and one disposal without retry");
        }

        await using (var absent = await Fixture.CreateAsync(token, mount: false))
            Network(await absent.Owner.GetPreparedStreamCapabilitiesAsync(new(WorkerId, ControllerId), token) is null,
                "actual HTTP 404 at the dedicated capability path is the unsupported result");
        await using (var redirect = await Fixture.CreateAsync(token, mount: false, redirect: true))
        {
            var refused = false;
            try { await redirect.Owner.GetPreparedStreamCapabilitiesAsync(new(WorkerId, ControllerId), token); }
            catch (RemoteModelProtocolException) { refused = true; }
            Network(refused && redirect.RedirectTargetRequests == 0,
                "real owner HTTP handler rejects a redirect without forwarding the installation key to its target");
        }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError })
        {
            using var handler = new ReplyHandler(status, "{}");
            using var client = new RemotePreparedStreamingOwnerClient(Registration("http://127.0.0.1/"), handler);
            var failed = false;
            try { await client.GetPreparedStreamCapabilitiesAsync(new(WorkerId, ControllerId), token); }
            catch (Exception error) when (error is UnauthorizedAccessException or RemoteModelProtocolException) { failed = true; }
            Unit(failed && handler.Requests == 1, status + " is a failure and never unsupported or retried");
        }
        using (var handler = new ReplyHandler(HttpStatusCode.OK, "{\"schemaVersion\":1,\"unexpected\":true}"))
        using (var client = new RemotePreparedStreamingOwnerClient(Registration("http://127.0.0.1/"), handler))
        {
            var failed = false;
            try { await client.GetPreparedStreamCapabilitiesAsync(new(WorkerId, ControllerId), token); }
            catch (Exception error) when (error is RemoteModelProtocolException or InvalidDataException or JsonException) { failed = true; }
            Unit(failed && handler.Requests == 1, "malformed successful discovery cannot become unsupported");
        }
        if (includeDeadlineChecks)
        {
            await Task.WhenAll(DiscoveryDeadlineAsync(body: false, Unit), DiscoveryDeadlineAsync(body: true, Unit));
        }
        else Console.WriteLine("HTTP fake-handler deadline fixtures were explicitly skipped; they are not passes.");
        Console.WriteLine($"Prepared streaming HTTP: {networkChecks} real ephemeral loopback Kestrel assertions with deterministic prepared adapters; {unitChecks} fake-handler transport assertions passed. No Source, native model, provider, hardware or external network exercised.");
    }

    private static bool TransportFailure(Exception error) => error is HttpRequestException or IOException or OperationCanceledException;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WorkerRegistration Registration(string url) => new() { Id = "fixture-worker", Name = "Fixture", Url = url, AccessKey = SyntheticKey };

    private static async Task<bool> ObserveRejectedTransportAsync(Task<HttpResponseMessage> pending, CancellationToken token)
    {
        try
        {
            using var reply = await pending;
            await reply.Content.ReadAsByteArrayAsync(token);
            return !reply.IsSuccessStatusCode;
        }
        catch (Exception error) when (TransportFailure(error)) { return true; }
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Fixture streaming lifetime did not settle.");
            await Task.Delay(20, token);
        }
    }

    private static async Task DiscoveryDeadlineAsync(bool body, Action<bool, string> check)
    {
        using var handler = new StalledHandler(body);
        using var client = new RemotePreparedStreamingOwnerClient(Registration("http://127.0.0.1/"), handler);
        var stopwatch = Stopwatch.StartNew(); var cancelled = false;
        try { await client.GetPreparedStreamCapabilitiesAsync(new(WorkerId, ControllerId), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(25)); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && handler.CancelObserved && stopwatch.Elapsed >= TimeSpan.FromSeconds(8) && stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            "dedicated discovery deadline bounds stalled " + (body ? "body" : "headers") + " with a non-expiring caller token");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private WebApplication app = null!;
        internal StateStore Store { get; }
        internal Configuration Configuration { get; }
        internal HostSettings Settings { get; }
        internal FixtureAdapter Adapter { get; } = new();
        internal RemotePreparedInference Inference { get; }
        internal OwnerPreparedStreamingHttp? Edge { get; private set; }
        internal HttpClient Raw { get; private set; } = null!;
        internal RemotePreparedStreamingOwnerClient Owner { get; private set; } = null!;
        internal TaskCompletionSource WriteFault { get; } = NewSignal();
        internal int RedirectTargetRequests;

        private Fixture()
        {
            root = Path.Combine(Path.GetTempPath(), "cerebral-prepared-http-" + Guid.NewGuid().ToString("N"));
            Store = new(root); Configuration = new(Store);
            Settings = new() { InstallationId = WorkerId, Revision = 1, AccessKey = SyntheticKey,
                StandaloneWorker = true, Coordinator = false, ManagementControllerId = ControllerId, AllowLan = false };
            Store.Put("settings", "host", Settings);
            Inference = new(Adapter, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        }

        internal static async Task<Fixture> CreateAsync(CancellationToken token, bool mount = true, bool redirect = false)
        {
            var fixture = new Fixture();
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.root,
                    ApplicationName = typeof(OwnerPreparedStreamingHttpTests).Assembly.GetName().Name });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                fixture.app = builder.Build();
                // Test-only failure injection wraps the real network response stream, never the producer.
                fixture.app.Use(async (context, next) =>
                {
                    if (!context.Request.Headers.ContainsKey("X-Fixture-Write-Fault")) { await next(context); return; }
                    var original = context.Response.Body;
                    context.Response.Body = new FaultingWriteStream(original, fixture.WriteFault);
                    try { await next(context); }
                    finally { context.Response.Body = original; }
                });
                if (mount) fixture.Edge = OwnerPreparedStreamingHttp.UsePreparedStreamingRoutes(fixture.app, fixture.Configuration, fixture.Inference);
                fixture.app.Run(context =>
                {
                    if (redirect && context.Request.Path == OwnerPreparedStreamingHttp.CapabilitiesPath)
                        context.Response.Redirect("/fixture-redirect-target");
                    else { if (context.Request.Path == "/fixture-redirect-target") fixture.RedirectTargetRequests++; context.Response.StatusCode = 404; }
                    return Task.CompletedTask;
                });
                await fixture.app.StartAsync(token);
                var address = fixture.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                fixture.Raw = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
                fixture.Owner = new(Registration(address));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal void SaveRole(bool standalone, bool coordinator)
        {
            Settings.StandaloneWorker = standalone; Settings.Coordinator = coordinator;
            Settings.ManagementControllerId = standalone ? ControllerId : null;
            Store.Put("settings", "host", Settings);
        }

        internal async Task<RemotePreparedResponse> PrepareAsync(CancellationToken token)
        {
            var binding = new RemotePreparedModelBinding("fixture-model", "fixture-only/model.gguf", "fixture-template", [], 0, 1, -1);
            var request = new ModelGenerationRequest(binding.Id, [new("user", [new TextContentPart("Fixture input only.")])], 8192, 512);
            var reply = await Inference.PrepareAsync(CerebralJson.Element(new RemotePrepareRequest(1, request, binding)), token);
            return CerebralJson.Read<RemotePreparedResponse>(reply.Data);
        }

        internal RemotePreparedStreamRequest StreamRequest(RemotePreparedResponse preparation) => new(1,
            Guid.NewGuid().ToString("N"), WorkerId, ControllerId, preparation.ProtocolVersion, preparation.LeaseId!,
            RemotePreparedStreamingCodec.MetadataSha256(preparation.Metadata));

        public async ValueTask DisposeAsync()
        {
            Adapter.Release.TrySetResult(); Adapter.AllowDispose.TrySetResult(); Raw?.Dispose(); Owner?.Dispose();
            if (app is not null) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await app.StopAsync(stop.Token); await app.DisposeAsync(); }
            await Inference.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FixtureAdapter : IModelRuntimeAdapter
    {
        internal const string Prefix = "{\"action\":\"final\",\"text\":\"Café Δ";
        internal const string Tail = " and completed\"}";
        internal const string Output = Prefix + Tail;
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.http.streaming", "1", ["text"]);
        internal int Generations, Disposals, DisposeCalls;
        internal bool HoldDisposal;
        internal TaskCompletionSource Release { get; } = NewSignal();
        internal TaskCompletionSource CancelObserved { get; } = NewSignal();
        internal TaskCompletionSource DisposeEntered { get; } = NewSignal();
        internal TaskCompletionSource AllowDispose { get; } = NewSignal();
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); request.Validate();
            var metadata = new PreparedModelMetadata("fixture-http-instance", Descriptor.AdapterId, Descriptor.Version,
                request.ModelId, 321, request.ContextLimit, request.ResponseLimit, "deterministic fixture token evidence")
            { SafetyReserveTokens = 128, DesiredResponseBudget = request.ResponseLimit, ReasoningReserveTokens = request.ReasoningTokens };
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, metadata));
        }
        private sealed class Prepared(FixtureAdapter owner, PreparedModelMetadata metadata) : IPreparedModelGeneration
        {
            private int disposed;
            public PreparedModelMetadata Metadata => metadata;
            public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                Interlocked.Increment(ref owner.Generations);
                using var registration = token.Register(() => owner.CancelObserved.TrySetResult());
                onText?.Invoke(Prefix);
                // Intentionally retain execution ownership through cancellation until the fixture releases it.
                await owner.Release.Task;
                token.ThrowIfCancellationRequested(); onText?.Invoke(Tail);
                return new(ModelGenerationFinish.Stop, Output, 17, metadata.InputTokens);
            }
            public async ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.DisposeCalls);
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                owner.DisposeEntered.TrySetResult();
                if (owner.HoldDisposal) await owner.AllowDispose.Task;
                Interlocked.Increment(ref owner.Disposals);
            }
        }
    }

    private sealed class FaultingWriteStream(Stream inner, TaskCompletionSource fault) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { fault.TrySetResult(); throw new IOException("fixture response write fault"); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { fault.TrySetResult(); return ValueTask.FromException(new IOException("fixture response write fault")); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { fault.TrySetResult(); return Task.FromException(new IOException("fixture response write fault")); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ReplyHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class StalledHandler(bool body) : HttpMessageHandler
    {
        internal bool CancelObserved;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (body)
            {
                var content = new StreamContent(new StalledReadStream(() => CancelObserved = true));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return new(HttpStatusCode.OK) { Content = content };
            }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancelObserved = true; throw; }
            throw new InvalidOperationException("Stalled fixture unexpectedly resumed.");
        }
    }

    private sealed class StalledReadStream(Action cancelled) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { cancelled(); throw; }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
