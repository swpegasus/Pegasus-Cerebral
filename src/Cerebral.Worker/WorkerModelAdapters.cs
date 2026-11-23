using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Resources;

namespace Cerebral.Worker;

/// <summary>Native workflow routing uses the admitted worker identity, never a same-ID local fallback.</summary>
internal sealed class WorkerModelAdapters(ModelRuntime local, RemoteWorkerManagement remote,
    AttachmentStore attachments, AttachmentArtifacts artifacts, ConfiguredWorkflowModelProvider configured)
{
    public IModelRuntimeAdapter ForSubmission(WorkflowSubmission submission)
    {
        if (submission.LocalGeneration is not null) return new LocalPolicyCandidate(this, submission);
        var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
        var binding = snapshot.Model ?? throw new RemoteModelProtocolException("The accepted model binding is unavailable.");
        var adapter = new LlamaResourceModelAdapter(ForBinding(snapshot.Profile, binding, submission.AcceptedContent.Any(part => part is ImageContentPart)), attachments,
            new(submission.ProfileId, submission.ConversationId),
            (reference, maximum, token) => artifacts.ResolveModelTextAsync(
                new(submission.ProfileId, submission.ConversationId), reference, maximum, token));
        // The local pool owns this priority policy. Remote preparation keeps its existing
        // capability and usage-tracking identity; no unverified peer priority is transmitted.
        return snapshot.Profile.ModelWorkerId == "local"
            ? WorkerInferencePriority.Apply(adapter, WorkerInferencePriority.ForSubmission(submission)) : adapter;
    }

    private sealed class LocalPolicyCandidate(WorkerModelAdapters owner, WorkflowSubmission submission)
        : IModelRuntimeAdapter, ILocalModelPolicySelection
    {
        public ModelAdapterDescriptor Descriptor { get; } = new(
            "cerebral.configured.policy-candidate", "1.0.0", Array.AsReadOnly(new[] { "text" }));
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Select the exact loaded local policy before constructing or preparing a request.");
        public Task<ILocalModelPolicyScope> SelectAsync(CancellationToken token) =>
            owner.SelectLocalPolicyAsync(submission, token);
    }

    private async Task<ILocalModelPolicyScope> SelectLocalPolicyAsync(WorkflowSubmission submission,
        CancellationToken token)
    {
        var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
        var binding = snapshot.Model ?? throw new InvalidOperationException("The accepted local model binding is unavailable.");
        if (submission.LocalGeneration is null || snapshot.LocalGenerationConsumer is null ||
            snapshot.Profile.WorkerId != "local" || snapshot.Profile.ModelWorkerId != "local" ||
            snapshot.Profile.ReasoningMode != "off" || binding.ModelWorkerId != "local" ||
            binding.VisionProjectorPath is not null ||
            submission.AcceptedContent.Any(part => part is not TextContentPart))
            throw new UnauthorizedAccessException("The accepted workflow has no local native text policy route.");
        var candidate = configured.ForAccepted(snapshot.Profile, binding) as ILocalModelPolicySelection ??
            throw new InvalidOperationException("The configured local model cannot select a policy.");
        var selected = await candidate.SelectAsync(token).ConfigureAwait(false);
        try
        {
            var runtime = new LlamaResourceModelAdapter(selected.Adapter, attachments,
                new(submission.ProfileId, submission.ConversationId),
                (reference, maximum, cancellation) => artifacts.ResolveModelTextAsync(
                    new(submission.ProfileId, submission.ConversationId), reference, maximum, cancellation));
            var prioritized = WorkerInferencePriority.Apply(runtime, WorkerInferencePriority.ForSubmission(submission));
            return new DecoratedPolicyScope(selected, LocalModelExecutionPolicy.Bind(prioritized, selected.Policy));
        }
        catch { await selected.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private sealed class DecoratedPolicyScope(ILocalModelPolicyScope inner, IModelRuntimeAdapter adapter)
        : ILocalModelPolicyScope
    {
        public ModelExecutionPolicy Policy => inner.Policy;
        public IModelRuntimeAdapter Adapter => adapter;
        public Task RequireCurrentAsync(CancellationToken token) => inner.RequireCurrentAsync(token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    public IModelRuntimeAdapter ForCompactor(WorkerExecutionSnapshot.CompactorBinding binding)
    {
        var adapter = ForBinding(binding.Profile, binding.Model);
        return binding.Profile.ModelWorkerId == "local"
            ? WorkerInferencePriority.Apply(adapter, InferencePriority.Normal) : adapter;
    }

    private IModelRuntimeAdapter ForBinding(AgentProfile profile, WorkerExecutionSnapshot.ModelBinding binding, bool actualImages = false)
    {
        if (binding.ModelWorkerId != profile.ModelWorkerId || binding.Id != profile.ModelId)
            throw new RemoteModelProtocolException("The accepted model and worker identity do not match.");
        if (profile.ModelWorkerId == "local")
        {
            // The existing image/projector route wins before registered text ownership.
            if (actualImages || binding.VisionProjectorPath is not null ||
                ModelCapabilityPolicy.Requirements(profile).Contains(ModelCapabilityIds.ImageInput))
                return local;
            return new ConfiguredLocalWorkflowRoutingAdapter(configured.ForAccepted(profile, binding), local);
        }
        return local.TrackExternalUsage(new RemoteModelRuntimeAdapter(remote, profile.ModelWorkerId,
            WorkerExecutionSnapshot.RemoteBinding(binding),
            binding.ModelWorkerInstallationId ?? throw new RemoteModelProtocolException("The admitted remote worker identity is unavailable.")));
    }

    public async Task<IDisposable?> AcquireRequiredAsync(WorkflowSubmission submission, CancellationToken token)
    {
        var profile = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Profile;
        if (profile.ModelWorkerId != "local")
        {
            if (ModelCapabilityPolicy.Requirements(profile).Contains(ModelCapabilityIds.ImageInput))
                throw new RemoteModelProtocolException("Remote image-required roles do not yet have verified image readiness acquisition. Choose a local image-enabled model for this required role.");
            return null;
        }
        using var scope = WorkerInferencePriority.Enter(WorkerInferencePriority.ForSubmission(submission));
        return await local.AcquireRequiredCapabilitiesAsync(profile, token).ConfigureAwait(false);
    }

    public void ValidateAcceptedContent(WorkflowSubmission submission)
    {
        LlamaResourceModelAdapter.ValidateAcceptedContent(attachments, submission);
        var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
        if (snapshot.Profile.ModelWorkerId != "local" && submission.AcceptedContent.Any(part => part is ImageContentPart))
        {
            var binding = snapshot.Model ?? throw new RemoteModelProtocolException("The accepted model binding is unavailable.");
            if (ForBinding(snapshot.Profile, binding) is not IMultimodalModelRuntimeAdapter)
                throw new RemoteModelProtocolException("The selected remote model worker does not support prepared native image input. Keep the image attached and select an image-enabled model worker before sending.");
        }
    }
}
