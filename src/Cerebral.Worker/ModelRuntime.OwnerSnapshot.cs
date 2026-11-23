using System.ComponentModel;
using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

// Private Worker seam: these are observations, never model admission or loaded-byte attestations.
internal sealed record OwnerRuntimeModelObservation(
    string ProcessGenerationId, long RuntimeEpoch, string? ModelId, string InstanceState,
    string? InstanceId, RemotePreparedModelBinding? LaunchBinding, string LaunchBindingState,
    OwnerRuntimeProviderObservation? Provider, OwnerRuntimeContextObservation? EffectiveContext,
    int? ConfiguredParallelRequests);
internal sealed record OwnerRuntimeProviderObservation(
    string Kind, string Executable, string IdentityState, string? ImplementationIdentity = null);
internal sealed record OwnerRuntimeContextObservation(
    int? MeasuredContextTokens, int? ConfiguredContextTokens, string State, string Source);
internal sealed class OwnerRuntimeSnapshotUnavailableException(string message) : IOException(message);

internal sealed class OwnerRuntimeLaunchCapture
{
    private readonly string modelJson;
    private readonly RemotePreparedModelBinding? binding;
    private readonly string modelId;
    private readonly string? workerInstallationId;
    private readonly int parallelRequests;
    private readonly OwnerRuntimeProviderObservation provider;
    internal int RequestedContextTokens { get; }
    internal int MinimumContextTokens { get; }

    private OwnerRuntimeLaunchCapture(string modelJson, ModelRecord model, string executable,
        string? workerInstallationId)
    {
        this.modelJson = modelJson;
        modelId = model.Id;
        this.workerInstallationId = workerInstallationId;
        parallelRequests = model.ParallelRequests;
        // Snapshot representability is optional. The frozen remote-wire bounds must not add
        // restrictions to normal local loading of legacy records. Preserve their launch inputs,
        // but expose no full binding rather than normalize, truncate or fabricate one.
        var candidate = new RemotePreparedModelBinding(model.Id, model.Path, model.ChatTemplate,
            model.StopSequences!, model.MaximumContextTokens, model.ParallelRequests, model.GpuLayers,
            model.VisionProjectorPath);
        try { candidate.Validate(); binding = CopyBinding(candidate); }
        catch (ArgumentException) { binding = null; }
        provider = new("llama.cpp", executable, "captured_launch_configuration");
        MinimumContextTokens = LlamaCapacityFitter.MinimumContextTokens(model, model.AutomaticContextSizing);
        RequestedContextTokens = model.AutomaticContextSizing
            ? Math.Max(model.ContextTokens, MinimumContextTokens) : model.ContextTokens;
    }

    internal static OwnerRuntimeLaunchCapture Capture(ModelRecord model, string executable) =>
        Capture(model, executable, null);

    internal static OwnerRuntimeLaunchCapture Capture(ModelRecord model, string executable,
        string? workerInstallationId)
    {
        // Freeze before launch. Both arguments and projector admission consume this copy, so
        // later persisted/configuration changes cannot be mistaken for the running request.
        ArgumentNullException.ThrowIfNull(model);
        var json = JsonSerializer.Serialize(model, CerebralJson.Options);
        var copy = JsonSerializer.Deserialize<ModelRecord>(json, CerebralJson.Options)
            ?? throw new InvalidDataException("Model launch inputs are unavailable.");
        // The scalar comes from the same settings snapshot that starts the native child.
        // Missing/invalid optional identity never changes ordinary model loading.
        return new(json, copy, executable, workerInstallationId);
    }

    internal ModelRecord CreateLaunchInputs() =>
        JsonSerializer.Deserialize<ModelRecord>(modelJson, CerebralJson.Options)!;

    internal OwnerRuntimeSuccessfulLaunch SuccessfulStartup(int? measuredContextTokens)
    {
        if (measuredContextTokens is { } measured && measured < MinimumContextTokens)
            throw new InvalidDataException("Measured startup context is below the admitted minimum.");
        string? configurationSha256 = null;
        if (binding is not null)
        {
            try { configurationSha256 = OwnerModelSnapshotCodec.EncodeBinding(binding).Sha256; }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
            { /* Optional support identity is unavailable; ordinary startup remains authoritative. */ }
        }
        return new(modelId, binding is null ? null : CopyBinding(binding), provider,
            new(measuredContextTokens, RequestedContextTokens,
                measuredContextTokens.HasValue ? "measured" : "unobserved",
                measuredContextTokens.HasValue ? "startup_log" : "launch_arguments"),
            parallelRequests)
        {
            WorkerInstallationId = workerInstallationId,
            ConfigurationScopeId = configurationSha256 is null ? null : OwnerModelSnapshotCodec.BindingCodec,
            ConfigurationSha256 = configurationSha256
        };
    }

    internal static RemotePreparedModelBinding CopyBinding(RemotePreparedModelBinding value) =>
        value with { StopSequences = Array.AsReadOnly(value.StopSequences.ToArray()) };
}

internal sealed record OwnerRuntimeSuccessfulLaunch(
    string ModelId, RemotePreparedModelBinding? Binding, OwnerRuntimeProviderObservation Provider,
    OwnerRuntimeContextObservation Context, int ParallelRequests)
{
    // Optional identity for passive support only. The hash covers the versioned model binding,
    // not all Worker settings, executable bytes, selected context or fitted observations.
    internal string? WorkerInstallationId { get; init; }
    internal string? ConfigurationScopeId { get; init; }
    internal string? ConfigurationSha256 { get; init; }
}

public sealed partial class ModelRuntime
{
    private readonly string ownerProcessGenerationId = Guid.NewGuid().ToString("N");
    // Normal transitions hold lifecycle. Shutdown also uses this short gate, so a stop outside
    // lifecycle cannot race a copied observation. Never hold it across store callbacks or awaits.
    private readonly object ownerSnapshotGate = new();
    private long ownerRuntimeEpoch;
    private bool ownerRuntimeDisposed;

    internal async Task<T> WithOwnerRuntimeSnapshotAsync<T>(string? modelId,
        Func<OwnerRuntimeModelObservation, T> readRetainedSnapshot, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(readRetainedSnapshot);
        token.ThrowIfCancellationRequested();
        try { await lifecycle.WaitAsync(token).ConfigureAwait(false); }
        catch (ObjectDisposedException) { throw new OwnerRuntimeSnapshotUnavailableException("Runtime observation is unavailable after shutdown."); }
        try
        {
            token.ThrowIfCancellationRequested();
            var observation = CopyOwnerRuntimeObservation(modelId);
            // Core must perform one bounded synchronous read-only store callback here, taking
            // its store gate AFTER lifecycle. No await, runtime re-entry or provider operation.
            var result = readRetainedSnapshot(observation);
            token.ThrowIfCancellationRequested();
            if (!IsOwnerRuntimeSnapshotCurrent(observation))
                throw new OwnerRuntimeSnapshotUnavailableException("Runtime observation changed during the retained snapshot read.");
            return result;
        }
        finally { lifecycle.Release(); }
    }

    internal bool IsOwnerRuntimeSnapshotCurrent(OwnerRuntimeModelObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (ownerSnapshotGate)
        {
            if (ownerRuntimeDisposed || observation.ProcessGenerationId != ownerProcessGenerationId ||
                observation.RuntimeEpoch != ownerRuntimeEpoch) return false;
            var (state, instance) = OwnerInstanceState(observation.ModelId);
            return observation.InstanceState == state && observation.InstanceId == instance?.Id;
        }
    }

    private OwnerRuntimeModelObservation CopyOwnerRuntimeObservation(string? modelId)
    {
        lock (ownerSnapshotGate)
        {
            if (ownerRuntimeDisposed)
                throw new OwnerRuntimeSnapshotUnavailableException("Runtime observation is unavailable after shutdown.");
            var (state, instance) = OwnerInstanceState(modelId);
            var launch = state == "present" ? instance?.OwnerLaunch : null;
            return new(ownerProcessGenerationId, ownerRuntimeEpoch, modelId, state, instance?.Id,
                launch?.Binding is { } binding ? OwnerRuntimeLaunchCapture.CopyBinding(binding) : null,
                launch?.Binding is null ? "unavailable" : "captured", launch?.Provider, launch?.Context,
                launch?.ParallelRequests);
        }
    }

    private (string State, Instance? Instance) OwnerInstanceState(string? modelId)
    {
        if (modelId is null) return ("unobserved", null);
        if (!instances.TryGetValue(modelId, out var instance)) return ("absent", null);
        try
        {
            return !instance.Stopped && !instance.Process.HasExited
                ? ("present", instance) : ("unobserved", instance);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
            return ("unobserved", instance);
        }
    }

    private void InstallOwnerObservedInstance(string id, Instance instance)
    {
        lock (ownerSnapshotGate)
        {
            if (ownerRuntimeDisposed) throw new OwnerRuntimeSnapshotUnavailableException("Runtime is shutting down.");
            instances[id] = instance;
            instance.Slots.ObserveChanges(() => CaptureInferenceAllocator(id, instance));
            ownerRuntimeEpoch++;
            CaptureInferenceAllocator(id, instance);
        }
    }

    private void PublishOwnerSuccessfulLaunch(Instance instance, OwnerRuntimeSuccessfulLaunch launch)
    {
        long nativeCacheEpoch;
        lock (ownerSnapshotGate)
        {
            if (ownerRuntimeDisposed || !instances.TryGetValue(launch.ModelId, out var current) || !ReferenceEquals(current, instance))
                throw new OwnerRuntimeSnapshotUnavailableException("Runtime changed before startup completed.");
            instance.OwnerLaunch = launch;
            ownerRuntimeEpoch++;
            CaptureInferenceLaunch(instance, launch);
            nativeCacheEpoch = ownerRuntimeEpoch;
        }
        if (!nativeCacheObservationEnabled) return;
        try
        {
            string? binding = null;
            if (launch.Binding is not null) binding = OwnerModelSnapshotCodec.EncodeBinding(launch.Binding).Sha256;
            var captured = new NativeCacheOwner(launch.ModelId, instance.Id, ownerProcessGenerationId,
                nativeCacheEpoch, binding, instance.NativeCacheIdentity);
            // This callback is only an Interlocked read, never an owner lock or runtime query.
            instance.Slots.ConfigureNativeCache(captured, () => Interlocked.Read(ref ownerRuntimeEpoch));
            if (instance.Slots.NativeCacheOwner is { } cacheOwner) Accounting.Observation.NativeCacheModel(cacheOwner);
            else Accounting.Observation.NativeCacheCaptureFailed();
        }
        catch (Exception e) when (NativeCacheObservation.Ordinary(e))
        {
            try { Accounting.Observation.NativeCacheCaptureFailed(); }
            catch (Exception nested) when (NativeCacheObservation.Ordinary(nested)) { }
        }
    }

    private void RemoveOwnerObservedInstance(string id, Instance instance)
    {
        lock (ownerSnapshotGate)
        {
            if (((ICollection<KeyValuePair<string, Instance>>)instances).Remove(new(id, instance)))
            {
                ownerRuntimeEpoch++;
                instance.Slots.StopNativeCache();
                Accounting.Observation.Stopped(instance.Id, ownerProcessGenerationId, ownerRuntimeEpoch);
            }
        }
    }

    private void ThrowIfOwnerRuntimeDisposed()
    {
        lock (ownerSnapshotGate)
            if (ownerRuntimeDisposed) throw new ObjectDisposedException(nameof(ModelRuntime));
    }

    private void MarkOwnerRuntimeDisposed()
    {
        lock (ownerSnapshotGate)
        {
            ownerRuntimeDisposed = true;
            ownerRuntimeEpoch++;
            foreach (var instance in instances.Values) instance.Slots.StopNativeCache();
        }
    }
}
