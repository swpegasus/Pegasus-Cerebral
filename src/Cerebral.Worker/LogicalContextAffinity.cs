using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

// A bounded scheduling preference captured by this host. It is not a lease, grant or cache proof.
internal readonly record struct LocalContextAffinityKey(string Fingerprint)
{
    internal void Validate()
    {
        if (Fingerprint is not { Length: 64 } ||
            Fingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid local context affinity binding.");
    }
}

internal static class WorkerLogicalContextAffinity
{
    private static readonly AsyncLocal<LocalContextAffinityKey?> current = new();
    internal static LocalContextAffinityKey? Current => current.Value;

    internal static IModelRuntimeAdapter ForSubmission(IModelRuntimeAdapter adapter, WorkflowSubmission submission)
    {
        var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
        // Legacy/unconfigured/package routes without a captured native model keep FIFO behavior.
        if (snapshot.Model is null) return adapter;
        return Bind(adapter, submission, snapshot, snapshot.Profile, snapshot.Model, "generation");
    }

    internal static IModelRuntimeAdapter ForCompactor(IModelRuntimeAdapter adapter, WorkflowSubmission submission,
        WorkerExecutionSnapshot.Snapshot snapshot)
    {
        var compactor = snapshot.Compactor ?? throw new InvalidOperationException("The accepted compactor binding is unavailable.");
        return Bind(adapter, submission, snapshot, compactor.Profile, compactor.Model, "compaction");
    }

    private static IModelRuntimeAdapter Bind(IModelRuntimeAdapter adapter, WorkflowSubmission submission,
        WorkerExecutionSnapshot.Snapshot snapshot, AgentProfile producer, WorkerExecutionSnapshot.ModelBinding model, string purpose)
    {
        ArgumentNullException.ThrowIfNull(adapter); ArgumentNullException.ThrowIfNull(submission);
        if (snapshot.Profile.Id != submission.ProfileId || producer.ModelId != model.Id ||
            producer.ModelWorkerId != model.ModelWorkerId)
            throw new InvalidOperationException("The accepted local context binding does not match its owner/model.");
        // Remote/package providers keep their existing selection. No affinity value enters their protocol.
        if (model.ModelWorkerId != "local") return adapter;
        OwnerModelSnapshotCodec.ValidateIdentifier(submission.ProfileId);
        OwnerModelSnapshotCodec.ValidateIdentifier(submission.ConversationId);
        if (snapshot.Profile.Revision < 1 || producer.Revision < 1) return adapter; // No stable host binding: keep FIFO.

        // Do not key on request/run/effect IDs or prompt bytes: new turns of one context should reuse.
        // Scope the compactor separately and include immutable producer/model settings. Pool lifetime
        // additionally scopes this preference to one loaded native instance.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            owner = snapshot.Profile.Id, ownerRevision = snapshot.Profile.Revision, submission.ConversationId,
            purpose, producer = producer.Id, producerRevision = producer.Revision, model
        });
        var key = new LocalContextAffinityKey(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return adapter is IMultimodalModelRuntimeAdapter multimodal
            ? new MultimodalContextAdapter(adapter, multimodal, key) : new ContextAdapter(adapter, key);
    }

    internal static IDisposable Enter(LocalContextAffinityKey key) { key.Validate(); return new Scope(key); }
    private sealed class Scope : IDisposable
    {
        private readonly LocalContextAffinityKey? previous = current.Value;
        private bool disposed;
        internal Scope(LocalContextAffinityKey key) => current.Value = key;
        public void Dispose() { if (!disposed) { disposed = true; current.Value = previous; } }
    }
    private class ContextAdapter(IModelRuntimeAdapter inner, LocalContextAffinityKey key) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => inner.Descriptor;
        protected IDisposable EnterPreparation() => Enter(key);
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            using var scope = EnterPreparation();
            return await inner.PrepareAsync(request, token).ConfigureAwait(false);
        }
    }
    private sealed class MultimodalContextAdapter(IModelRuntimeAdapter inner,
        IMultimodalModelRuntimeAdapter multimodal, LocalContextAffinityKey key) : ContextAdapter(inner, key), IMultimodalModelRuntimeAdapter
    {
        public async Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
            IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
        {
            using var scope = EnterPreparation();
            return await multimodal.PrepareMultimodalAsync(request, images, token).ConfigureAwait(false);
        }
    }
}
