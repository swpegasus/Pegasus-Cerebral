using System.ComponentModel;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Worker.ModelAdapters;

namespace Cerebral.Worker;

// The declaration belongs to one captured native instance. It reports current v1 software
// envelopes only; these are necessary outer bounds, not a native maximum or fit promise.
internal static class NativeInstanceRuntimeSupport
{
    internal static ModelRuntimeSupportDescriptor? TryCreate(OwnerRuntimeSuccessfulLaunch launch,
        AdapterBinding actual, ModelAdapterDescriptor facade)
    {
        if (launch.Binding is not { } model ||
            launch.ModelId != actual.ModelId || model.Id != actual.ModelId ||
            actual.BackendId != "worker.local.llama.cpp" ||
            actual.AdapterId != LlamaPreparedGeneration.Adapter.AdapterId ||
            actual.AdapterVersion != LlamaPreparedGeneration.Adapter.Version ||
            facade.AdapterId != actual.AdapterId || facade.Version != actual.AdapterVersion ||
            !Identifier(launch.WorkerInstallationId) ||
            launch.ConfigurationScopeId != OwnerModelSnapshotCodec.BindingCodec ||
            !Digest(launch.ConfigurationSha256)) return null;

        try
        {
            if (OwnerModelSnapshotCodec.EncodeBinding(model).Sha256 != launch.ConfigurationSha256)
                return null;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { return null; }

        var binding = new ModelRuntimeSupportBinding(launch.WorkerInstallationId!, actual.BackendId,
            actual.AdapterId, actual.AdapterVersion, actual.ModelId,
            launch.ConfigurationScopeId!, launch.ConfigurationSha256!, actual.InstanceId);
        // The native handler does not use the paired immutable JSON codecs required to
        // witness executable capabilities. Empty declarations cannot open a request path.
        return new(ModelRuntimeSupportContract.SchemaVersion, binding, SoftwareOuterBounds(), []);
    }

    internal static bool IsCurrent(ModelRuntimeSupportSnapshot? support,
        OwnerRuntimeSuccessfulLaunch? launch, AdapterBinding? actual, NativeSupportReadFacts facts)
    {
        if (support is null || launch is null || launch.Binding is not { } launchModel || actual is null ||
            !facts.SameInstance || !facts.SameRegistration || !facts.InstanceAlive ||
            facts.Quarantined || !facts.RegistrationUsable) return false;
        var bound = support.Descriptor.Binding;
        return bound.WorkerInstallationId == launch.WorkerInstallationId &&
            bound.WorkerInstallationId == facts.CurrentWorkerInstallationId &&
            bound.BackendId == actual.BackendId && bound.AdapterId == actual.AdapterId &&
            bound.AdapterVersion == actual.AdapterVersion &&
            bound.ModelId == facts.RequestedModelId && bound.ModelId == actual.ModelId &&
            bound.ModelId == launch.ModelId && bound.ModelId == launchModel.Id &&
            bound.InstanceId == facts.ActualInstanceId && bound.InstanceId == actual.InstanceId &&
            bound.ConfigurationScopeId == OwnerModelSnapshotCodec.BindingCodec &&
            bound.ConfigurationScopeId == launch.ConfigurationScopeId &&
            bound.ConfigurationScopeId == facts.PersistedConfigurationScopeId &&
            bound.ConfigurationSha256 == launch.ConfigurationSha256 &&
            bound.ConfigurationSha256 == facts.PersistedConfigurationSha256 &&
            support.Descriptor.Capabilities.Count == 0 && !support.ExpandedPathSupported;
    }

    private static ModelRuntimeCapacityConstraints SoftwareOuterBounds()
    {
        static RuntimeLimitRange Wire(long minimum, long maximum, string source) =>
            new(minimum, maximum, 1, new(RuntimeLimitBasis.WireCompatibility, source, "1"));
        static RuntimeLimitRange Unreported(string source) =>
            new(0, null, 1, new(RuntimeLimitBasis.Unreported, source, "1"));
        return new(
            Wire(1, 16, "cerebral.local-parallel-requests-v1"),
            Wire(512, 1_048_576, "cerebral.model-request-context-v1"),
            Wire(1, 131_072, "cerebral.model-request-visible-output-v1"),
            // Actual request validation allows 0 or 128..32768. Step 1 is only its outer hull.
            Wire(0, 32_768, "cerebral.model-request-reasoning-outer-v1"),
            Unreported("cerebral.combined-generation-maximum-unreported"),
            Unreported("cerebral.aggregate-context-maximum-unreported"));
    }

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ':');

    private static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}

internal sealed record NativeSupportReadFacts(string RequestedModelId, string ActualInstanceId,
    string? CurrentWorkerInstallationId, string? PersistedConfigurationScopeId,
    string? PersistedConfigurationSha256, bool SameInstance, bool SameRegistration,
    bool InstanceAlive, bool Quarantined, bool RegistrationUsable);

public sealed partial class ModelRuntime
{
    // An internal typed check only. Freeze the bounded text request before settings/store
    // callbacks, then rejoin the exact loaded instance, registration and scoped binding after
    // the pure handler check. Null means no current observation; success still needs exact
    // native preparation and does not reserve a slot or establish fit.
    internal async Task<ModelRuntimeSupportBinding?> ReadLoadedNativeTextRequestShapeAsync(
        string modelId, ModelGenerationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(modelId)) return null;
        var frozen = RegisteredNativeTextRequestShape.Freeze(request, modelId);
        var before = await ReadLoadedNativeRuntimeSupportAsync(modelId, token).ConfigureAwait(false);
        if (before is null || !instances.TryGetValue(modelId, out var instance) ||
            instance.RegisteredText is not { } registration ||
            !ReferenceEquals(registration.RuntimeSupport, before)) return null;

        var checkedShape = registration.ReadRequestShape(frozen, before);
        token.ThrowIfCancellationRequested();
        if (checkedShape is null) return null;
        var after = await ReadLoadedNativeRuntimeSupportAsync(modelId, token).ConfigureAwait(false);
        return ReferenceEquals(before, after) &&
            instances.TryGetValue(modelId, out var current) && ReferenceEquals(current, instance) &&
            ReferenceEquals(instance.RegisteredText, registration) ? checkedShape : null;
    }

    // Private passive read. It does not load a model, create a registration, enter a native
    // callback, reserve capacity, query a provider or expose any new wire field.
    internal async Task<ModelRuntimeSupportSnapshot?> ReadLoadedNativeRuntimeSupportAsync(
        string modelId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(modelId)) return null;
        try { await lifecycle.WaitAsync(token).ConfigureAwait(false); }
        catch (ObjectDisposedException) { token.ThrowIfCancellationRequested(); return null; }
        try
        {
            token.ThrowIfCancellationRequested();
            if (!instances.TryGetValue(modelId, out var instance) ||
                instance.OwnerLaunch is not { } launch ||
                instance.RegisteredText is not { } registration) return null;

            // Retained configuration is read outside the short owner/fence gates. Only the
            // eight fields named by OwnerModelSnapshotCodec.BindingCodec are compared.
            HostSettings? currentSettings = settings();
            var currentWorkerInstallationId = currentSettings?.InstallationId;
            var persisted = store.Get<ModelRecord>("model", modelId);
            if (persisted is null || persisted.Id != modelId || persisted.State != "loaded" ||
                persisted.StopSequences is null) return null;
            string persistedDigest;
            try { persistedDigest = OwnerModelSnapshotCodec.EncodeBinding(RemotePreparedInference.Bind(persisted)).Sha256; }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
            { return null; }
            token.ThrowIfCancellationRequested();

            // Existing order is lifecycle -> owner membership -> registration fence -> registry.
            // No store callback, await or provider work runs under the short nested gates.
            lock (ownerSnapshotGate)
            {
                if (ownerRuntimeDisposed) return null;
                var sameInstance = instances.TryGetValue(modelId, out var current) &&
                    ReferenceEquals(current, instance);
                var sameRegistration = ReferenceEquals(instance.RegisteredText, registration);
                bool alive;
                try { alive = !instance.Stopped && !instance.Process.HasExited; }
                catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or Win32Exception)
                { alive = false; }
                if (!sameInstance || !sameRegistration || !alive || instance.Slots.Quarantined.Count != 0)
                    return null;
                var support = registration.ReadRuntimeSupport();
                var facts = new NativeSupportReadFacts(modelId, instance.Id,
                    currentWorkerInstallationId, OwnerModelSnapshotCodec.BindingCodec,
                    persistedDigest, sameInstance, sameRegistration, alive,
                    instance.Slots.Quarantined.Count != 0, support is not null);
                token.ThrowIfCancellationRequested();
                return NativeInstanceRuntimeSupport.IsCurrent(support, launch,
                    registration.Service.Binding, facts) ? support : null;
            }
        }
        finally { lifecycle.Release(); }
    }
}
