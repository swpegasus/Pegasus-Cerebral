using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Cerebral.Worker;

/// <summary>Fixed owner-only HTTP edge for the existing one-use prepared lease. No additional listener or SDK contract.</summary>
internal sealed class OwnerPreparedStreamingHttp(Configuration configuration, RemotePreparedInference inference,
    CancellationToken applicationStopping)
{
    internal const string CapabilitiesPath = "/api/inference/prepared/stream-capabilities";
    internal const string GeneratePath = "/api/inference/prepared/generate-stream";
    internal const int MaximumRequestBytes = RemotePreparedStreamingLimits.RequestBytes, MaximumCapabilitiesBytes = 4096;
    internal const int MaximumStreamBytes = RemotePreparedStreamingLimits.StreamBytes;
    private const int MaximumTrackedGenerations = 64;
    private readonly object gate = new();
    private readonly Dictionary<string, GenerationHandle> tracked = new(StringComparer.Ordinal);
    private sealed class GenerationHandle(CancellationTokenSource intent)
    {
        internal CancellationTokenSource Intent { get; } = intent;
        internal Task? Completion { get; set; }
        internal bool ProducerFinished { get; set; }
        internal bool WriterFinished { get; set; }
    }
    internal int TrackedGenerations { get { lock (gate) return tracked.Count; } }

    internal static OwnerPreparedStreamingHttp UsePreparedStreamingRoutes(WebApplication app,
        Configuration configuration, RemotePreparedInference inference)
    {
        var host = new OwnerPreparedStreamingHttp(configuration, inference, app.Lifetime.ApplicationStopping);
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (!path.StartsWith(CapabilitiesPath, StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(GeneratePath, StringComparison.OrdinalIgnoreCase)) { await next(context); return; }
            await host.HandleAsync(context);
        });
        return host;
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var inspection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, applicationStopping);
        inspection.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var path = context.Request.Path.Value;
            if (path is not (CapabilitiesPath or GeneratePath) || context.Request.PathBase.HasValue || context.Request.QueryString.HasValue ||
                context.Features.Get<IHttpRequestFeature>()?.RawTarget is { } raw && raw != path)
            { await RefuseAsync(context, 400, "prepared_stream_path_noncanonical", inspection.Token); return; }
            if (context.Request.Method != "POST") { context.Response.StatusCode = 405; return; }
            var owner = Authenticate(context, inspection.Token);
            if (context.Request.ContentLength is > MaximumRequestBytes)
            { await RefuseAsync(context, 413, "prepared_stream_request_bound", inspection.Token); return; }
            var body = await RemotePreparedStreamingOwnerClient.ReadBoundedAsync(context.Request.Body, MaximumRequestBytes, inspection.Token);
            if (path == CapabilitiesPath)
            {
                var input = RemotePreparedStreamingCodec.ParseCapabilitiesRequest(body);
                RequireOwner(owner, input.ExpectedWorkerInstallationId, input.ExpectedControllerInstallationId);
                RequireCurrent(owner, inspection.Token);
                var reply = Capabilities(owner);
                // The same closed codec used by the client verifies the advertised limits.
                _ = RemotePreparedStreamingCodec.ParseCapabilities(reply, input);
                context.Response.ContentType = "application/json; charset=utf-8";
                SetHeaders(context);
                await context.Response.Body.WriteAsync(reply, inspection.Token);
                await context.Response.Body.FlushAsync(inspection.Token);
                return;
            }
            var request = RemotePreparedStreamingCodec.ParseRequest(body);
            RequireOwner(owner, request.ExpectedWorkerInstallationId, request.ExpectedControllerInstallationId);
            RequireCurrent(owner, inspection.Token);
            await GenerateAsync(context, owner, request);
        }
        catch (UnauthorizedAccessException) { await RefuseAsync(context, 403, "prepared_stream_owner_refused", inspection.Token); }
        catch (OwnerSnapshotUnavailableException) { await RefuseAsync(context, 503, "prepared_stream_owner_unavailable", inspection.Token); }
        catch (OperationCanceledException) { context.Abort(); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { await RefuseAsync(context, 400, "prepared_stream_invalid_or_unconfirmed", inspection.Token); }
    }

    private OwnerSnapshotConfiguration Authenticate(HttpContext context, CancellationToken token)
    {
        if (context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("X-Cerebral-Peer"))
            throw new UnauthorizedAccessException();
        var owner = configuration.ReadOwnerSnapshotConfiguration(token);
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote) && !owner.AllowLan) throw new UnauthorizedAccessException();
        var key = context.Request.Headers["X-Cerebral-Key"];
        if (key.Count != 1 || !owner.Authenticates(key[0] ?? "")) throw new UnauthorizedAccessException();
        if (!owner.Standalone || owner.Coordinator || owner.ControllerId is null) throw new UnauthorizedAccessException();
        return owner;
    }

    private static void RequireOwner(OwnerSnapshotConfiguration owner, string worker, string controller)
    {
        if (owner.InstallationId != worker || owner.ControllerId != controller || worker == controller)
            throw new UnauthorizedAccessException();
    }

    private void RequireCurrent(OwnerSnapshotConfiguration original, CancellationToken token)
    {
        var current = configuration.ReadOwnerSnapshotConfiguration(token);
        if (!original.Matches(current)) throw new UnauthorizedAccessException();
    }

    private static byte[] Capabilities(OwnerSnapshotConfiguration owner)
    {
        var reply = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, workerInstallationId = owner.InstallationId, controllerInstallationId = owner.ControllerId,
            generatePath = GeneratePath, contentType = "application/x-ndjson", preparedProtocolVersions = new[] { 1, 2 },
            maximumRequestBytes = MaximumRequestBytes, maximumStartFrameBytes = RemotePreparedStreamingLimits.StartBytes,
            maximumTextFrameBytes = RemotePreparedStreamingLimits.TextBytes, maximumTerminalFrameBytes = RemotePreparedStreamingLimits.TerminalBytes,
            maximumStreamBytes = MaximumStreamBytes, maximumFrames = RemotePreparedStreamingLimits.Frames,
            maximumTextFragmentCharacters = RemotePreparedStreamingLimits.FragmentCharacters
        });
        if (reply.Length > MaximumCapabilitiesBytes) throw new InvalidDataException();
        return reply;
    }

    private async Task GenerateAsync(HttpContext context, OwnerSnapshotConfiguration owner, RemotePreparedStreamRequest request)
    {
        var buffer = new RemotePreparedStreamBuffer(request);
        var intent = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, applicationStopping);
        intent.CancelAfter(TimeSpan.FromMinutes(11));
        var generationToken = intent.Token;
        using var writer = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, applicationStopping);
        writer.CancelAfter(TimeSpan.FromMinutes(11));
        var handle = new GenerationHandle(intent);
        lock (gate)
        {
            if (tracked.Count >= MaximumTrackedGenerations || !tracked.TryAdd(request.StreamId, handle))
            { buffer.Dispose(); intent.Dispose(); throw new InvalidOperationException("Prepared stream is already active or at capacity."); }
        }
        var generation = ProduceAsync();
        handle.Completion = generation;
        try
        {
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            SetHeaders(context);
            await foreach (var frame in buffer.ReadAllAsync(writer.Token))
            {
                if (frame.Kind == "terminal") RequireCurrent(owner, writer.Token);
                await RemotePreparedStreamingCodec.WriteFrameAsync(context.Response.Body, frame, writer.Token);
                await context.Response.Body.FlushAsync(writer.Token);
            }
            await generation;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Transport failure is cancellation intent. Observe the SAME generation and its
            // cleanup independently; never retry a consumed lease or fabricate a terminal.
            try { intent.Cancel(); } catch (ObjectDisposedException) { }
            try { await generation.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { /* The tracked handle remains until its real finally completes. */ }
            context.Abort();
        }
        finally { FinishLifetime(producer: false); }

        async Task ProduceAsync()
        {
            try
            {
                var response = await inference.GenerateStreamingAsync(request,
                    metadata => { RequireCurrent(owner, generationToken); buffer.Start(metadata); }, buffer.Append, generationToken);
                // This runs only after provider/lease cleanup has returned. A changed current
                // owner cannot turn a previous request into completion evidence for a new one.
                RequireCurrent(owner, generationToken);
                buffer.Complete(response);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            { buffer.Fail(error); }
            finally
            {
                FinishLifetime(producer: true);
            }
        }

        void FinishLifetime(bool producer)
        {
            var dispose = false;
            lock (gate)
            {
                if (producer) handle.ProducerFinished = true; else handle.WriterFinished = true;
                if (handle.ProducerFinished && handle.WriterFinished)
                {
                    if (tracked.TryGetValue(request.StreamId, out var current) && ReferenceEquals(current, handle)) tracked.Remove(request.StreamId);
                    dispose = true;
                }
            }
            // A late producer still owns its callbacks and wake signal after HTTP grace
            // expires. Dispose only once both users have exited, never the provider lease.
            if (dispose) { buffer.Dispose(); intent.Dispose(); }
        }
    }

    private static void SetHeaders(HttpContext context)
    { context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["X-Content-Type-Options"] = "nosniff"; }

    private static async Task RefuseAsync(HttpContext context, int status, string code, CancellationToken token)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        SetHeaders(context);
        try { await context.Response.WriteAsJsonAsync(new { error = code }, token); }
        catch (OperationCanceledException) { context.Abort(); }
    }
}
