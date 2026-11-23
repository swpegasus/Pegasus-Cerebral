using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

internal enum InferencePriority { Normal, Interactive }

/// <summary>Host-only admission provenance. Provider requests and plugin SDKs carry no priority value.</summary>
internal static class WorkerInferencePriority
{
    private static readonly AsyncLocal<InferencePriority?> current = new();
    internal static InferencePriority Current => current.Value ?? InferencePriority.Normal;

    internal static InferencePriority ForSubmission(WorkflowSubmission submission) =>
        submission.Caller?.Kind == EventActorKind.User && submission.ParentRunId is null &&
        submission.DefinitionId == ConversationWorkflowModule.DefinitionId
            ? InferencePriority.Interactive : InferencePriority.Normal;

    internal static IDisposable Enter(InferencePriority priority) => new Scope(priority);
    internal static IModelRuntimeAdapter Apply(IModelRuntimeAdapter adapter, InferencePriority priority) =>
        adapter is IMultimodalModelRuntimeAdapter multimodal
            ? new MultimodalPriorityAdapter(adapter, multimodal, priority)
            : new PriorityAdapter(adapter, priority);

    private sealed class Scope : IDisposable
    {
        private readonly InferencePriority? previous;
        private bool disposed;
        public Scope(InferencePriority priority)
        {
            previous = current.Value;
            current.Value = priority;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            current.Value = previous;
        }
    }

    private class PriorityAdapter(IModelRuntimeAdapter inner, InferencePriority priority) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => inner.Descriptor;
        protected IDisposable EnterPreparation() => Enter(priority);
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            // Limit propagation to this preparation/acquisition. In particular, a returned lease
            // does not leave interactive priority ambient during inference or subsequent tool work.
            using var scope = EnterPreparation();
            return await inner.PrepareAsync(request, token).ConfigureAwait(false);
        }
    }

    private sealed class MultimodalPriorityAdapter(IModelRuntimeAdapter inner,
        IMultimodalModelRuntimeAdapter multimodal, InferencePriority priority)
        : PriorityAdapter(inner, priority), IMultimodalModelRuntimeAdapter
    {
        public async Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
            IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
        {
            using var scope = EnterPreparation();
            return await multimodal.PrepareMultimodalAsync(request, images, token).ConfigureAwait(false);
        }
    }
}
