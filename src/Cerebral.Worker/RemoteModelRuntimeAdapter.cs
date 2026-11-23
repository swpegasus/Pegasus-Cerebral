using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Exact one-use remote preparation over the owner channel. Prepared protocols return terminal
/// text rather than advertising a token stream, and never retries or substitutes a local model.</summary>
public sealed class RemoteModelRuntimeAdapter : IModelRuntimeAdapter, IMultimodalModelRuntimeAdapter
{
    private readonly RemoteWorkerManagement management;
    private readonly string workerId;
    private readonly string workerInstallationId;
    private readonly RemotePreparedModelBinding binding;
    private readonly RemotePreparedClientTiming timing;
    public ModelAdapterDescriptor Descriptor { get; } = new("cerebral.remote.prepared", "2.0.0", ["text", "image"], SupportsReasoning: true);

    public RemoteModelRuntimeAdapter(RemoteWorkerManagement management, string workerId, RemotePreparedModelBinding binding, string workerInstallationId)
        : this(management, workerId, binding, workerInstallationId, RemotePreparedClientTiming.Default) { }

    internal RemoteModelRuntimeAdapter(RemoteWorkerManagement management, string workerId, RemotePreparedModelBinding binding,
        string workerInstallationId, RemotePreparedClientTiming timing)
    {
        Configuration.ValidateId(workerId);
        if (workerId == "local") throw new ArgumentException("A remote model adapter requires a remote worker.");
        if (string.IsNullOrWhiteSpace(workerInstallationId)) throw new ArgumentException("The frozen remote worker identity is required.");
        ArgumentNullException.ThrowIfNull(timing); timing.Validate();
        this.management = management; this.workerId = workerId; this.workerInstallationId = workerInstallationId;
        this.timing = timing;
        this.binding = CerebralJson.Read<RemotePreparedModelBinding>(CerebralJson.Element(binding));
        this.binding.Validate();
    }

    public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = new RemotePrepareRequest(RemotePreparedProtocol.Version,
            CerebralJson.Read<ModelGenerationRequest>(CerebralJson.Element(request)), binding);
        RemotePreparedProtocol.Validate(input);
        return await PrepareCoreAsync(input.Request, input, RemotePreparedProtocol.Version, requiresImage: false, token: token).ConfigureAwait(false);
    }

    public async Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
        IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(images);
        var input = CerebralJson.Read<RemotePrepareRequestV2>(CerebralJson.Element(
            new RemotePrepareRequestV2(RemotePreparedProtocol.ImageVersion, request, binding, images)));
        try
        {
            RemotePreparedProtocol.Validate(input);
            return await PrepareCoreAsync(input.Request, input, RemotePreparedProtocol.ImageVersion, requiresImage: true, token: token).ConfigureAwait(false);
        }
        finally { ClearImages(input.Images); }
    }

    internal ModelExecutionAccounting Accounting => management.Accounting;

    private async Task<IPreparedModelGeneration> PrepareCoreAsync(ModelGenerationRequest request, object input,
        int protocolVersion, bool requiresImage, CancellationToken token)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(input, CerebralJson.Options).Length > RemotePreparedProtocol.MaximumRequestBytes)
            throw new RemoteModelProtocolException("The remote prepared input exceeds its bounded transport. Select a smaller explicit context; no input was truncated.");
        var ownership = management.Accounting.Begin(ModelOwnershipKind.OutboundPreparation, request.ModelId);
        using var preparationCancellation = ownership.ObserveCancellation(token);
        try { management.RequirePreparedRoute(workerId, workerInstallationId, binding); }
        catch { ownership.Complete(); throw; }
        RemoteManagedWorkerSession? session = null;
        string? leaseId = null;
        string? reservation = null;
        try
        {
            session = await management.ConnectAsync(workerId, token).ConfigureAwait(false);
            if (session.Management.WorkerInstallationId != workerInstallationId)
                throw new RemoteModelProtocolException("The remote worker installation changed since this request was accepted. Submit a fresh request.");
            reservation = management.BeginPreparedRoute(session, binding, ownership);
            ownership.Phase(ModelOwnershipPhase.Preparing, effectStarted: true);
            var result = await session.Client.GetAsync<RemotePreparedResponse>(RemotePreparedProtocol.PrepareOperation, input, token).ConfigureAwait(false);
            session.EnsureCurrent();
            if (result.ProtocolVersion != protocolVersion || result.Metadata is null || result.Adapter is null)
                throw new RemoteModelProtocolException("The remote worker did not return the supported prepared-inference contract.");
            RemotePreparedProtocol.ValidateMetadata(result.Metadata, request, result.ContextLimited);
            if (result.ContextLimited)
            {
                if (result.LeaseId is not null || result.ExpiresAt is not null)
                    throw new RemoteModelProtocolException("The remote worker retained a lease for an invalid generation budget.");
                management.CompletePreparedRoute(reservation); reservation = null;
                throw new ModelContextLimitException(result.Metadata);
            }
            leaseId = result.LeaseId;
            RemotePreparedProtocol.Validate(new RemoteLeaseRequest(protocolVersion, leaseId!));
            if (result.ExpiresAt is null || result.Adapter.ContentCapabilities is null ||
                !result.Adapter.ContentCapabilities.Contains("text", StringComparer.Ordinal) ||
                requiresImage && !result.Adapter.ContentCapabilities.Contains("image", StringComparer.Ordinal) ||
                requiresImage && (result.Adapter.AdapterId != result.Metadata.AdapterId || result.Adapter.Version != result.Metadata.AdapterVersion) ||
                request.ReasoningTokens != 0 && !result.Adapter.SupportsReasoning)
                throw new RemoteModelProtocolException("Remote preparation does not support this exact content/reasoning request.");
            ownership.Bind(instanceId: result.Metadata.InstanceId, leaseId: leaseId);
            ownership.Phase(ModelOwnershipPhase.Ready);
            ownership.Prepared(result.Metadata);
            var prepared = new Prepared(session, request, result.Metadata, leaseId!, protocolVersion, timing, management, reservation, ownership,
                ownership.ObserveCancellation(token));
            session = null; return prepared;
        }
        catch (Exception error) when (reservation is not null && error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            var released = session is not null && leaseId is not null &&
                await TryReleaseAsync(session, leaseId, protocolVersion, timing.ReleaseTimeout).ConfigureAwait(false);
            if (released) management.CompletePreparedRoute(reservation);
            else management.QuarantinePreparedRoute(reservation);
            reservation = null; leaseId = null;
            if (!released) throw new RemotePreparedOwnershipUnknownException();
            throw;
        }
        finally
        {
            if (reservation is null && !ownership.IsUnknown) ownership.Complete();
            if (session is not null)
            {
                try
                {
                    var released = leaseId is not null && await TryReleaseAsync(session, leaseId, protocolVersion, timing.ReleaseTimeout).ConfigureAwait(false);
                    if (reservation is not null)
                    {
                        if (released) management.CompletePreparedRoute(reservation);
                        else management.QuarantinePreparedRoute(reservation);
                    }
                }
                finally { session.Dispose(); }
            }
        }
    }

    private static async Task<bool> TryReleaseAsync(RemoteManagedWorkerSession session, string leaseId, int protocolVersion, TimeSpan releaseTimeout)
    {
        try
        {
            session.EnsureCurrent();
            using var timeout = new CancellationTokenSource(releaseTimeout);
            var response = await session.Client.GetAsync<RemoteReleaseResponse>(RemotePreparedProtocol.ReleaseOperation,
                new RemoteLeaseRequest(protocolVersion, leaseId), timeout.Token).ConfigureAwait(false);
            if (response.ProtocolVersion != protocolVersion || response.LeaseId != leaseId)
                throw new RemoteModelProtocolException("Remote release did not match its exact prepared lease.");
            return response.Released && !response.CancellationRequested;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // A changed owner connection must not reuse the old key. The server's bounded lease
            // expiry remains responsible for cleanup; this is not proof of provider cancellation.
            return false;
        }
    }

    private sealed class Prepared(RemoteManagedWorkerSession session, ModelGenerationRequest request,
        PreparedModelMetadata metadata, string leaseId, int protocolVersion, RemotePreparedClientTiming timing,
        RemoteWorkerManagement management, string reservation, ModelExecutionAccounting.ModelOwnership ownership,
        CancellationTokenRegistration preparationCancellation) : IPreparedModelGeneration, IModelReleaseObservation
    {
        private int used;
        private int disposed;
        private int releaseAttempted;
        private bool serverReleased;
        private bool inferenceSent;
        public PreparedModelMetadata Metadata { get; } = metadata;
        public bool ReleaseConfirmed => ownership.IsRetired;

        public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Interlocked.CompareExchange(ref used, 1, 0) != 0)
                throw new InvalidOperationException("A remote prepared generation is one-use and cannot be replayed.");
            using var cancellation = ownership.ObserveCancellation(token);
            ownership.Phase(ModelOwnershipPhase.Generating);
            var sent = false;
            var terminalObserved = false;
            try
            {
                token.ThrowIfCancellationRequested();
                await session.RevalidateAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using var transport = new CancellationTokenSource(timing.GenerationTransportLimit);
                IRemotePreparedStreamingOwnerClient? streaming = null;
                if (onText is not null && session.Client is IRemotePreparedStreamingOwnerClient candidate)
                {
                    using var discovery = CancellationTokenSource.CreateLinkedTokenSource(token);
                    discovery.CancelAfter(TimeSpan.FromSeconds(10));
                    var owner = new RemotePreparedStreamOwnerIdentity(session.Management.WorkerInstallationId,
                        session.Management.ControllerInstallationId!);
                    var capabilities = await candidate.GetPreparedStreamCapabilitiesAsync(owner, discovery.Token).ConfigureAwait(false);
                    session.EnsureCurrent();
                    if (capabilities is not null)
                    {
                        _ = RemotePreparedStreamingCodec.ParseCapabilities(JsonSerializer.SerializeToUtf8Bytes(capabilities,
                            new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false }), owner);
                        streaming = candidate;
                    }
                }
                token.ThrowIfCancellationRequested();
                sent = true; inferenceSent = true;
                ownership.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
                Task<RemoteGeneratedResponse> generation;
                if (streaming is not null)
                {
                    var streamRequest = new RemotePreparedStreamRequest(1, Guid.NewGuid().ToString("N"),
                        session.Management.WorkerInstallationId, session.Management.ControllerInstallationId!,
                        protocolVersion, leaseId, RemotePreparedStreamingCodec.MetadataSha256(Metadata));
                    generation = streaming.GenerateStreamingAsync(streamRequest, fragment =>
                    {
                        session.EnsureCurrent();
                        onText!(fragment);
                    }, transport.Token);
                }
                else generation = session.Client.GetAsync<RemoteGeneratedResponse>(RemotePreparedProtocol.GenerateOperation,
                    new RemoteLeaseRequest(protocolVersion, leaseId), transport.Token);
                var response = await AwaitTerminalAsync(generation, transport, token).ConfigureAwait(false);
                session.EnsureCurrent();
                if (response.ProtocolVersion != protocolVersion || response.LeaseId != leaseId ||
                    JsonSerializer.Serialize(response.Metadata, CerebralJson.Options) != JsonSerializer.Serialize(Metadata, CerebralJson.Options))
                    throw new RemoteModelProtocolException("Remote completion did not match its exact prepared lease and token metadata.");
                RemotePreparedProtocol.ValidateMetadata(response.Metadata, request);
                var result = response.Result;
                if (result is not null)
                {
                    result = result with { PromptCache = LlamaProtocol.ValidatePromptCache(result.PromptCache, Metadata.InputTokens) };
                    response = response with { Result = result };
                }
                if (result is null || result.Text is null || result.Text.Length > RemotePreparedProtocol.MaximumOutputCharacters ||
                    !Enum.IsDefined(result.Finish) || result.GeneratedTokens < 0 || result.EvaluatedTokens < 0 ||
                    result.CancellationConfirmed && result.Finish != ModelGenerationFinish.Cancelled ||
                    JsonSerializer.SerializeToUtf8Bytes(response, CerebralJson.Options).Length > RemotePreparedProtocol.MaximumResponseBytes)
                    throw new RemoteModelProtocolException("Remote completion exceeded its bounded terminal-result contract.");
                if (result.Finish == ModelGenerationFinish.Cancelled && !result.CancellationConfirmed)
                    result = result with { Finish = ModelGenerationFinish.Unknown,
                        Error = "Remote provider returned cancellation without confirmed terminal evidence." };
                // Capture only normalized, validated terminal data before route retirement can trim history.
                // A later bookkeeping failure must not erase this separately confirmed provider evidence.
                ownership.Terminal(result);
                terminalObserved = true;
                if (result.Finish == ModelGenerationFinish.Unknown) management.QuarantinePreparedRoute(reservation);
                else management.CompletePreparedRoute(reservation);
                serverReleased = true;
                // Legacy and unsupported owners remain terminal-only; never replay terminal text as fragments.
                return result;
            }
            catch (OperationCanceledException)
            {
                if (sent) management.QuarantinePreparedRoute(reservation);
                if (sent && !terminalObserved) ownership.Terminal(null);
                return sent
                    ? new(ModelGenerationFinish.Unknown, "", Error: "Remote inference cancellation was not confirmed. This preparation was not replayed.")
                    : new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: true);
            }
            catch (RemoteModelProtocolException error)
            {
                management.QuarantinePreparedRoute(reservation);
                if (sent && !terminalObserved) ownership.Terminal(null);
                return new(ModelGenerationFinish.Unknown, "", Error: error.Message.Length <= 2048 ? error.Message : error.Message[..2048]);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                management.QuarantinePreparedRoute(reservation);
                if (sent && !terminalObserved) ownership.Terminal(null);
                return new(ModelGenerationFinish.Unknown, "", Error: sent
                    ? "Remote inference did not return verified terminal evidence. It was not replayed and no local model was substituted."
                    : "The verified remote owner/model route is unavailable. No inference was dispatched.");
            }
        }

        private async Task<RemoteGeneratedResponse> AwaitTerminalAsync(Task<RemoteGeneratedResponse> generation,
            CancellationTokenSource transport, CancellationToken caller)
        {
            if (!caller.CanBeCanceled) return await generation.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = caller.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
            if (await Task.WhenAny(generation, cancelled.Task).ConfigureAwait(false) == generation)
                return await generation.ConfigureAwait(false);

            // Release asks the exact lease to stop, while the independently bounded generate request
            // remains the only channel capable of returning provider-confirmed terminal evidence.
            var release = RequestReleaseAsync();
            try
            {
                try { return await generation.WaitAsync(timing.CancellationConfirmationGrace).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    transport.Cancel();
                    await ObserveAsync(generation).ConfigureAwait(false);
                    throw new OperationCanceledException(caller);
                }
            }
            finally { await release.ConfigureAwait(false); }
        }

        private async Task RequestReleaseAsync()
        {
            if (Interlocked.CompareExchange(ref releaseAttempted, 1, 0) != 0) return;
            ownership.Phase(ModelOwnershipPhase.Releasing);
            var released = await TryReleaseAsync(session, leaseId, protocolVersion, timing.ReleaseTimeout).ConfigureAwait(false);
            // Release acknowledgement is useful only for an unused preparation, never terminal inference proof.
            if (released && !inferenceSent) management.CompletePreparedRoute(reservation);
            else if (!serverReleased) management.QuarantinePreparedRoute(reservation);
        }

        private static async Task ObserveAsync(Task generation)
        {
            try { await generation.ConfigureAwait(false); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { if (!serverReleased) await RequestReleaseAsync().ConfigureAwait(false); }
            finally { preparationCancellation.Dispose(); session.Dispose(); }
        }
    }

    private static void ClearImages(IReadOnlyList<RemotePreparedImageInput> images)
    {
        foreach (var image in images)
            if (image?.Data is { } data) Array.Clear(data);
    }
}

internal sealed record RemotePreparedClientTiming(TimeSpan GenerationTransportLimit,
    TimeSpan CancellationConfirmationGrace, TimeSpan ReleaseTimeout)
{
    public static RemotePreparedClientTiming Default { get; } =
        new(TimeSpan.FromMinutes(11), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

    public void Validate()
    {
        if (GenerationTransportLimit <= TimeSpan.Zero || GenerationTransportLimit > TimeSpan.FromMinutes(15) ||
            CancellationConfirmationGrace < TimeSpan.Zero || CancellationConfirmationGrace > TimeSpan.FromMinutes(1) ||
            ReleaseTimeout <= TimeSpan.Zero || ReleaseTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(RemotePreparedClientTiming));
    }
}

/// <summary>Safe protocol diagnostics authored by this adapter, never raw remote context or credentials.</summary>
public sealed class RemoteModelProtocolException(string message) : IOException(message);

internal sealed class RemotePreparedOwnershipUnknownException() : IOException(
    "Remote preparation did not return confirmed lease ownership. This model route remains quarantined; explicitly unload and verify the same worker reports the model unloaded before retrying.");
