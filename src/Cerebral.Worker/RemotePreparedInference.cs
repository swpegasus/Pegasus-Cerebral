using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Process-local owner preparations. Dispatch authorization belongs to the fixed owner route.
/// Consumed leases never dispatch twice; provider cancellation confirmation remains provider evidence.</summary>
public sealed class RemotePreparedInference : IAsyncDisposable
{
    private readonly IModelRuntimeAdapter models;
    private readonly ModelExecutionAccounting accounting;
    private readonly Func<RemotePreparedModelBinding, CancellationToken, Task> validateBinding;
    private readonly Func<ModelGenerationRequest, IReadOnlyList<RemotePreparedImageInput>, CancellationToken, Task<IPreparedModelGeneration>>? prepareMultimodal;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan ttl;
    private readonly int maximumLeases;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> leases = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Timer sweep;
    private bool disposed;
    private static readonly JsonSerializerOptions InputJson = new(CerebralJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public RemotePreparedInference(IModelRuntimeAdapter models,
        Func<RemotePreparedModelBinding, CancellationToken, Task> validateBinding,
        Func<DateTimeOffset>? clock = null, TimeSpan? ttl = null, int maxLeases = 16,
        Func<ModelGenerationRequest, IReadOnlyList<RemotePreparedImageInput>, CancellationToken, Task<IPreparedModelGeneration>>? prepareMultimodal = null)
    {
        this.models = models; this.validateBinding = validateBinding;
        accounting = models is ModelRuntime native ? native.Accounting : new();
        this.prepareMultimodal = prepareMultimodal;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.ttl = ttl ?? TimeSpan.FromMinutes(5);
        if (this.ttl <= TimeSpan.Zero || this.ttl > TimeSpan.FromMinutes(10) || maxLeases is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(ttl));
        maximumLeases = maxLeases;
        sweep = new Timer(_ => _ = SweepSafelyAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public static Task ValidateModelBinding(StateStore store, RemotePreparedModelBinding expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        expected.Validate();
        var current = store.Get<ModelRecord>("model", expected.Id) ?? throw new InvalidOperationException("The prepared remote model is unavailable.");
        if (JsonSerializer.Serialize(expected, CerebralJson.Options) !=
            JsonSerializer.Serialize(Bind(current), CerebralJson.Options))
            throw new InvalidOperationException("The prepared remote model binding changed. No inference was dispatched.");
        return Task.CompletedTask;
    }

    public static RemotePreparedModelBinding Bind(ModelRecord model) => new(model.Id, model.Path, model.ChatTemplate,
        model.StopSequences.ToArray(), model.MaximumContextTokens, model.ParallelRequests, model.GpuLayers, model.VisionProjectorPath);

    public async Task<CommandResult> PrepareAsync(JsonElement args, CancellationToken token)
    {
        ValidateInput(args);
        if (!args.TryGetProperty("protocolVersion", out var versionElement) || !versionElement.TryGetInt32(out var protocolVersion))
            throw new ArgumentException("A supported remote prepared protocol version is required.");
        if (protocolVersion == RemotePreparedProtocol.Version)
        {
            var input = Deserialize<RemotePrepareRequest>(args);
            RemotePreparedProtocol.Validate(input);
            return await PrepareCoreAsync(protocolVersion, input.Request, input.ExpectedModel, null, token).ConfigureAwait(false);
        }
        if (protocolVersion == RemotePreparedProtocol.ImageVersion)
        {
            var input = Deserialize<RemotePrepareRequestV2>(args);
            try
            {
                RemotePreparedProtocol.Validate(input);
                return await PrepareCoreAsync(protocolVersion, input.Request, input.ExpectedModel, input.Images, token).ConfigureAwait(false);
            }
            finally { ClearImages(input.Images); }
        }
        throw new ArgumentException("Unsupported remote prepared-inference protocol.");
    }

    private async Task<CommandResult> PrepareCoreAsync(int protocolVersion, ModelGenerationRequest request,
        RemotePreparedModelBinding expectedModel, IReadOnlyList<RemotePreparedImageInput>? images, CancellationToken token)
    {
        var ownership = accounting.Begin(ModelOwnershipKind.InboundReservation, request.ModelId);
        Entry entry;
        try
        {
        await SweepExpiredAsync().ConfigureAwait(false);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (leases.Count >= maximumLeases) throw new InvalidOperationException("Remote prepared capacity is full. Release unused preparations or wait for active inference.");
            entry = new(Guid.NewGuid().ToString("N"), protocolVersion, expectedModel, clock() + ttl);
            entry.Ownership = ownership; ownership.Bind(leaseId: entry.Id);
            leases.Add(entry.Id, entry);
        }
        }
        catch { ownership.Complete(); throw; }
        IPreparedModelGeneration? prepared = null;
        var retained = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Cancel.Token, shutdown.Token);
            await validateBinding(entry.Binding, linked.Token).ConfigureAwait(false);
            if (request.ReasoningTokens != 0 && !models.Descriptor.SupportsReasoning)
                throw new NotSupportedException("This remote adapter does not support prepared reasoning.");
            ownership.Phase(ModelOwnershipPhase.Preparing, effectStarted: true);
            using (WorkerInferenceInvocation.EnterInboundPreparation(
                new InferenceInboundPreparation(accounting.ProcessGenerationId, ownership.Id, entry.Id)))
            {
                prepared = images is null
                    ? await models.PrepareAsync(request, linked.Token).ConfigureAwait(false)
                    : await (prepareMultimodal ?? throw new NotSupportedException("This worker does not support prepared remote image input."))
                        (request, images, linked.Token).ConfigureAwait(false);
            }
            await validateBinding(entry.Binding, linked.Token).ConfigureAwait(false);
            RemotePreparedProtocol.ValidateMetadata(prepared.Metadata, request);
            lock (gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (disposed || clock() >= entry.ExpiresAt) throw new OperationCanceledException("Remote preparation expired before it could be retained.");
                entry.Prepared = prepared; entry.State = LeaseState.Ready; retained = true;
                ownership.Bind(instanceId: prepared.Metadata.InstanceId); ownership.Phase(ModelOwnershipPhase.Ready);
            }
            return CommandResult.Ok(new RemotePreparedResponse(protocolVersion, entry.Id,
                prepared.Metadata, ResponseAdapter(protocolVersion, prepared.Metadata), entry.ExpiresAt));
        }
        catch (ModelContextLimitException limit)
        {
            RemotePreparedProtocol.ValidateMetadata(limit.Metadata, request, contextLimited: true);
            return CommandResult.Ok(new RemotePreparedResponse(protocolVersion, null,
                limit.Metadata, ResponseAdapter(protocolVersion, limit.Metadata), null, ContextLimited: true));
        }
        finally
        {
            if (!retained)
            {
                try
                {
                    ownership.Phase(ModelOwnershipPhase.Releasing);
                    if (prepared is not null) await DisposePreparedAsync(entry, prepared).ConfigureAwait(false);
                    else ownership.Complete();
                }
                finally { Finish(entry); }
            }
        }
    }

    public async Task<CommandResult> GenerateAsync(JsonElement args, CancellationToken token)
    {
        var input = Read<RemoteLeaseRequest>(args); RemotePreparedProtocol.Validate(input);
        return CommandResult.Ok(await GenerateCoreAsync(input, null, null, null, token).ConfigureAwait(false));
    }

    internal Task<RemoteGeneratedResponse> GenerateStreamingAsync(RemotePreparedStreamRequest request,
        Action<PreparedModelMetadata> onStarted, Action<string> onText, CancellationToken token)
    {
        RemotePreparedStreamingCodec.ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(onStarted); ArgumentNullException.ThrowIfNull(onText);
        return GenerateCoreAsync(new(request.ProtocolVersion, request.LeaseId),
            request.ExpectedMetadataSha256, onStarted, onText, token);
    }

    private async Task<RemoteGeneratedResponse> GenerateCoreAsync(RemoteLeaseRequest input,
        string? expectedMetadataSha256, Action<PreparedModelMetadata>? onStarted,
        Action<string>? onText, CancellationToken token)
    {
        RemotePreparedProtocol.Validate(input);
        await SweepExpiredAsync().ConfigureAwait(false);
        Entry entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!leases.TryGetValue(input.LeaseId, out entry!) || entry.State != LeaseState.Ready || entry.Prepared is null)
                throw new InvalidOperationException("The remote preparation is unavailable, expired, or already consumed. It was not replayed.");
            if (entry.ProtocolVersion != input.ProtocolVersion)
                throw new InvalidOperationException("The remote preparation belongs to a different protocol version. It was not consumed.");
            if (expectedMetadataSha256 is not null &&
                RemotePreparedStreamingCodec.MetadataSha256(entry.Prepared.Metadata) != expectedMetadataSha256)
                throw new RemoteModelProtocolException("Prepared stream metadata changed before lease consumption.");
            entry.State = LeaseState.Generating;
            entry.Ownership?.Phase(ModelOwnershipPhase.Generating);
            entry.ExpiresAt = clock() + TimeSpan.FromMinutes(10);
        }
        var prepared = entry.Prepared!;
        var sent = false;
        ModelGenerationResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Cancel.Token, shutdown.Token);
            linked.CancelAfter(TimeSpan.FromMinutes(10));
            onStarted?.Invoke(prepared.Metadata);
            await validateBinding(entry.Binding, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            sent = true; entry.Ownership?.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
            result = await prepared.GenerateAsync(onText, linked.Token).ConfigureAwait(false);
            await validateBinding(entry.Binding, CancellationToken.None).ConfigureAwait(false);
            if (result.Text is null || result.Text.Length > RemotePreparedProtocol.MaximumOutputCharacters ||
                !Enum.IsDefined(result.Finish) || result.GeneratedTokens < 0 || result.EvaluatedTokens < 0)
                result = new(ModelGenerationFinish.Unknown, "", Error: "The remote provider returned malformed or oversized completion evidence.");
            // Optional telemetry must be safe before serialization: NaN/Infinity or unbounded
            // provider diagnostics cannot invalidate otherwise confirmed generation evidence.
            result = result with { PromptCache = LlamaProtocol.ValidatePromptCache(result.PromptCache, prepared.Metadata.InputTokens) };
            var response = new RemoteGeneratedResponse(entry.ProtocolVersion, entry.Id, prepared.Metadata, result);
            if (JsonSerializer.SerializeToUtf8Bytes(response, CerebralJson.Options).Length > RemotePreparedProtocol.MaximumResponseBytes)
                result = result with { Finish = ModelGenerationFinish.Unknown, Text = "", Error = "Model output exceeded the remote prepared-result transport limit." };
        }
        catch (OperationCanceledException)
        {
            result = sent
                ? new(ModelGenerationFinish.Unknown, "", Error: "Remote inference cancellation was not confirmed by the provider.")
                : new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: true);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            result = new(ModelGenerationFinish.Unknown, "", Error: sent
                ? "Remote inference did not return valid terminal evidence for its frozen binding."
                : "The remote model binding or preparation is no longer available. No inference was dispatched.");
        }
        finally
        {
            try { await DisposePreparedAsync(entry, prepared).ConfigureAwait(false); }
            finally { Finish(entry); }
        }
        return new RemoteGeneratedResponse(entry.ProtocolVersion, entry.Id, prepared.Metadata, result);
    }

    public async Task<CommandResult> ReleaseAsync(JsonElement args, CancellationToken token)
    {
        var input = Read<RemoteLeaseRequest>(args); RemotePreparedProtocol.Validate(input); token.ThrowIfCancellationRequested();
        Entry? entry;
        lock (gate)
        {
            if (!leases.TryGetValue(input.LeaseId, out entry))
            {
                var unknown = accounting.Snapshot().Entries.Any(value => value.Kind == ModelOwnershipKind.InboundReservation && value.LeaseId == input.LeaseId);
                return CommandResult.Ok(new RemoteReleaseResponse(input.ProtocolVersion, input.LeaseId, !unknown, unknown));
            }
            if (entry.ProtocolVersion != input.ProtocolVersion)
                throw new InvalidOperationException("The remote preparation belongs to a different protocol version. It was not released.");
            entry.Cancel.Cancel();
            if (entry.State != LeaseState.Ready)
                return CommandResult.Ok(new RemoteReleaseResponse(entry.ProtocolVersion, input.LeaseId, false, true));
            entry.State = LeaseState.Disposing;
        }
        try { await DisposePreparedAsync(entry, entry.Prepared!).ConfigureAwait(false); }
        finally { Finish(entry); }
        return CommandResult.Ok(new RemoteReleaseResponse(entry.ProtocolVersion, input.LeaseId, true, false));
    }

    internal async Task SweepExpiredAsync()
    {
        var release = new List<Entry>();
        lock (gate)
        {
            foreach (var entry in leases.Values.Where(entry => clock() >= entry.ExpiresAt).ToArray())
            {
                entry.Cancel.Cancel();
                if (entry.State != LeaseState.Ready) continue;
                entry.State = LeaseState.Disposing; release.Add(entry);
            }
        }
        foreach (var entry in release)
            try { await DisposePreparedAsync(entry, entry.Prepared!).ConfigureAwait(false); }
            finally { Finish(entry); }
    }

    private async Task SweepSafelyAsync()
    {
        try { await SweepExpiredAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }

    private static async ValueTask DisposePreparedAsync(Entry entry, IPreparedModelGeneration prepared)
    {
        entry.Ownership?.Phase(ModelOwnershipPhase.Releasing);
        try
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
            if (prepared is IModelReleaseObservation observed && !observed.ReleaseConfirmed)
                entry.Ownership?.Unknown("inbound-provider-release-unconfirmed");
            else entry.Ownership?.Complete();
        }
        catch { entry.Ownership?.Unknown("inbound-provider-disposal-failed"); throw; }
    }

    private void Finish(Entry entry)
    {
        lock (gate) { leases.Remove(entry.Id); entry.State = LeaseState.Finished; }
        entry.Done.TrySetResult(); entry.Cancel.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; pending = leases.Values.ToArray();
            foreach (var entry in pending) { entry.Cancel.Cancel(); entry.ExpiresAt = DateTimeOffset.MinValue; }
        }
        await sweep.DisposeAsync().ConfigureAwait(false);
        shutdown.Cancel();
        await SweepExpiredAsync().ConfigureAwait(false);
        await Task.WhenAll(pending.Select(entry => entry.Done.Task)).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        shutdown.Dispose();
    }

    private static T Read<T>(JsonElement args)
    {
        ValidateInput(args);
        return Deserialize<T>(args);
    }

    private static T Deserialize<T>(JsonElement args) =>
        args.Deserialize<T>(InputJson) ?? throw new ArgumentException("Remote prepared arguments are required.");

    private static void ValidateInput(JsonElement args)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(args).Length > RemotePreparedProtocol.MaximumRequestBytes)
            throw new ArgumentException("Remote preparation exceeds the bounded transport input. Select a smaller explicit context.");
        Unique(args, 0);
    }

    private ModelAdapterDescriptor ResponseAdapter(int protocolVersion, PreparedModelMetadata metadata) =>
        protocolVersion == RemotePreparedProtocol.ImageVersion
            ? new(metadata.AdapterId, metadata.AdapterVersion, ["text", "image"])
            : models.Descriptor;

    private static void ClearImages(IReadOnlyList<RemotePreparedImageInput>? images)
    {
        if (images is null) return;
        foreach (var image in images)
            if (image?.Data is { } data) Array.Clear(data);
    }

    private static void Unique(JsonElement value, int depth)
    {
        if (depth > 64) throw new ArgumentException("Remote prepared arguments exceed the nesting limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.EnumerateObject().GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
                throw new ArgumentException("Remote prepared arguments contain duplicate fields.");
            foreach (var property in value.EnumerateObject()) Unique(property.Value, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var part in value.EnumerateArray()) Unique(part, depth + 1);
    }

    private enum LeaseState { Preparing, Ready, Generating, Disposing, Finished }
    private sealed class Entry(string id, int protocolVersion, RemotePreparedModelBinding binding, DateTimeOffset expiresAt)
    {
        public ModelExecutionAccounting.ModelOwnership? Ownership;
        public string Id { get; } = id;
        public int ProtocolVersion { get; } = protocolVersion;
        public RemotePreparedModelBinding Binding { get; } = binding;
        public DateTimeOffset ExpiresAt = expiresAt;
        public LeaseState State;
        public IPreparedModelGeneration? Prepared;
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
