using System.Runtime.ExceptionServices;
using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker;

internal sealed record RegisteredNativeGenerationObservation(AdapterBinding Binding,
    string RuntimeGenerationId, string AccountingGenerationId, string HostCorrelationId, string Phase,
    PreparedModelMetadata? Prepared, ModelGenerationFinish? ProviderFinish,
    bool? ProviderCancellationConfirmed, int? EvaluatedTokens, int? GeneratedTokens,
    LeaseObservation? RegistryCleanup, bool CallbackSettled,
    string? PublisherFailureType, string? PrimaryFailureType, string? CleanupFailureType);

internal sealed class RegisteredNativeGenerationCapture
{
    // Announced before Begin only to correlate stream observations. This is never
    // native admission, a preparation/lease handle, or caller-supplied authority.
    internal string HostCorrelationId { get; } = "registered-native-" + Guid.NewGuid().ToString("N");
    private int begun;
    private RegisteredNativeGenerationObservation? snapshot;
    internal RegisteredNativeGenerationObservation? Snapshot => Volatile.Read(ref snapshot);
    internal void Begin()
    {
        if (Interlocked.CompareExchange(ref begun, 1, 0) != 0)
            throw new InvalidOperationException("registered_native_capture_reused");
    }
    internal void Publish(RegisteredNativeGenerationObservation value) => Volatile.Write(ref snapshot, value);
}

internal sealed record RegisteredNativeServiceObservation(AdapterBinding Binding,
    string RuntimeGenerationId, string AccountingGenerationId, bool RegistrationOpen,
    int ActiveCallbacks, InstanceObservation Registry, bool? PhysicalStopConfirmed = null,
    string EvidenceTier = "native-path-not-yet-admitted");

// A synchronous publication exception may be translated to Unknown by the native provider.
// Retain the first original exception so cleanup cannot accidentally erase the caller's failure.
internal sealed class RegisteredNativePublisher(Action<string>? publish)
{
    private ExceptionDispatchInfo? first;
    internal string? FailureType => Volatile.Read(ref first)?.SourceException.GetType().Name;
    internal void Invoke(string text)
    {
        try { publish?.Invoke(text); }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref first, ExceptionDispatchInfo.Capture(error), null);
            throw;
        }
    }
    internal void ThrowIfFailed() => Volatile.Read(ref first)?.Throw();
}

internal interface IRegisteredNativeGenerationService : IAsyncDisposable
{
    AdapterBinding Binding { get; }
    RegisteredNativeServiceObservation Observe();
    Task<string> GenerateAsync(string id, IReadOnlyList<ModelMessage> messages,
        int maxTokens, double temperature, bool json, Action<string>? onToken, CancellationToken token,
        JsonElement? responseSchema = null, string reasoningMode = "off", int reasoningTokens = 1024,
        RegisteredNativeGenerationCapture? capture = null, InferenceInvocation? invocation = null);
}

internal sealed record RegisteredNativeRequestSnapshot(string Id, IReadOnlyList<ModelMessage> Messages,
    int MaxTokens, double Temperature, bool Json, JsonElement? ResponseSchema, string ReasoningMode, int ReasoningTokens);

// Worker-private operation entry. Core calls it only after ordinary CommandDispatcher authorization.
// This service never owns the ModelRuntime, native instance, process, or another consumer's leases.
internal sealed partial class RegisteredNativeGenerationService : IRegisteredNativeGenerationService, IRegisteredNativeWorkflowSource
{
    private readonly AdapterRegistry registry;
    private readonly NativeRegistrationFence fence;
    private readonly int contextTokens;
    internal ModelExecutionPolicy? LocalExecutionPolicy { get; }
    private readonly string runtimeGeneration, accountingGeneration;
    private readonly SemaphoreSlim retirement = new(1, 1);
    public AdapterBinding Binding { get; }
    internal RegisteredNativeGenerationService(AdapterRegistry registry, AdapterBinding binding,
        NativeRegistrationFence fence, int contextTokens, string runtimeGeneration, string accountingGeneration,
        ModelExecutionPolicy? localExecutionPolicy = null)
    {
        this.registry = registry; Binding = binding; this.fence = fence; this.contextTokens = contextTokens;
        this.runtimeGeneration = runtimeGeneration; this.accountingGeneration = accountingGeneration;
        LocalExecutionPolicy = localExecutionPolicy;
    }
    public RegisteredNativeServiceObservation Observe()
    {
        var state = fence.Snapshot();
        return new(Binding, runtimeGeneration, accountingGeneration, state.Open, state.Callbacks, registry.Observe(Binding));
    }

    public async Task<string> GenerateAsync(string id, IReadOnlyList<ModelMessage> messages,
        int maxTokens, double temperature, bool json, Action<string>? onToken, CancellationToken token,
        JsonElement? responseSchema = null, string reasoningMode = "off", int reasoningTokens = 1024,
        RegisteredNativeGenerationCapture? capture = null, InferenceInvocation? invocation = null)
    {
        capture ??= new(); capture.Begin();
        var owner = new AdapterCommandDispatch(registry);
        AdapterCommandDispatch.Preparation? handle = null; ModelGenerationResult? outcome = null;
        PreparedModelMetadata? metadata = null; LeaseObservation? cleanup = null; IDisposable? callback = null;
        ExceptionDispatchInfo? primaryError = null, cleanupError = null;
        var publisher = new RegisteredNativePublisher(onToken);
        // Reuse the capture's preannounced key for stream/host observation and the existing
        // accounting scope. No second identity is minted during native dispatch.
        var correlation = capture.HostCorrelationId;
        var observedInvocation = invocation ?? WorkerInferenceInvocation.Current;
        observedInvocation = observedInvocation is null
            ? new InferenceInvocation(correlation, "registered-native-direct", null)
            : observedInvocation with { CorrelationId = correlation };
        capture.Publish(new(Binding, runtimeGeneration, accountingGeneration, correlation, "preparing",
            null, null, null, null, null, null, false, null, null, null));
        try
        {
            token.ThrowIfCancellationRequested();
            var arguments = ModelRuntime.SnapshotRegisteredNativeRequest(id, messages, maxTokens,
                temperature, json, responseSchema, reasoningMode, reasoningTokens);
            if (arguments.Id != Binding.ModelId) throw new InvalidOperationException("registered_native_model_mismatch");
            var request = ModelRuntime.BuildDirectTextRequest(arguments.Id, arguments.Messages, contextTokens,
                arguments.MaxTokens, arguments.Temperature, arguments.Json, arguments.ResponseSchema);
            callback = fence.Enter();
            // The host invocation is captured into existing accounting before registry wrapping.
            // It is diagnostic provenance, not authority or a raw preparation escape hatch.
            using var observation = WorkerInferenceInvocation.Enter(observedInvocation);
            handle = await owner.PrepareAsync(Binding, new(["text"]), request, token).ConfigureAwait(false);
            metadata = handle.Metadata;
            capture.Publish(new(Binding, runtimeGeneration, accountingGeneration, correlation, "prepared",
                metadata, null, null, null, null, null, false, null, null, null));
            outcome = await owner.GenerateAsync(handle, Binding, publisher.Invoke, token).ConfigureAwait(false);
        }
        catch (Exception error) { primaryError = ExceptionDispatchInfo.Capture(error); }
        finally
        {
            try
            {
                if (handle is not null)
                {
                    try { await owner.ReleaseAsync(handle, Binding).ConfigureAwait(false); }
                    catch (Exception error) { cleanupError = ExceptionDispatchInfo.Capture(error); }
                    finally { cleanup = handle.Observe(); }
                }
            }
            finally
            {
                callback?.Dispose();
                capture.Publish(new(Binding, runtimeGeneration, accountingGeneration, correlation, "settled",
                    metadata, outcome?.Finish, outcome?.CancellationConfirmed,
                    outcome?.EvaluatedTokens, outcome?.GeneratedTokens, cleanup, true,
                    publisher.FailureType, primaryError?.SourceException.GetType().Name, cleanupError?.SourceException.GetType().Name));
            }
        }
        publisher.ThrowIfFailed(); // First publisher failure wins after the cleanup attempt.
        primaryError?.Throw();
        cleanupError?.Throw();
        if (outcome is null) throw new InvalidOperationException("registered_native_terminal_missing");
        return ModelRuntime.InterpretDirectTextResult(outcome, token);
    }

    public async ValueTask DisposeAsync()
    {
        await retirement.WaitAsync().ConfigureAwait(false);
        try
        {
            // Close first, then refuse active retirement. A later call after settlement may finish.
            // The registry's own reservation check remains authoritative too.
            fence.CloseForRetirement();
            _ = await registry.RetireAsync(Binding).ConfigureAwait(false);
        }
        finally { retirement.Release(); }
    }
}
