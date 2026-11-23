using Cerebral.Contracts;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

internal interface IConfiguredLocalWorkflowAdapter : IModelRuntimeAdapter { }

// Decide from the actual request before acquiring registered text ownership. Image
// sidecars stay on the existing image-capable runtime, never a retry after text failure.
internal sealed class ConfiguredLocalWorkflowRoutingAdapter : IConfiguredLocalWorkflowAdapter, IMultimodalModelRuntimeAdapter
{
    internal IModelRuntimeAdapter TextRuntime { get; }
    internal IModelRuntimeAdapter ImageRuntime { get; }
    private readonly IMultimodalModelRuntimeAdapter images;

    internal ConfiguredLocalWorkflowRoutingAdapter(IModelRuntimeAdapter text, IModelRuntimeAdapter legacyImages)
    {
        TextRuntime = text ?? throw new ArgumentNullException(nameof(text));
        ImageRuntime = legacyImages ?? throw new ArgumentNullException(nameof(legacyImages));
        images = legacyImages as IMultimodalModelRuntimeAdapter ??
            throw new ArgumentException("The existing image route must support prepared native images.", nameof(legacyImages));
    }

    // This is the selected text facet's descriptor. The resource wrapper separately
    // advertises the image route it actually implements via IMultimodalModelRuntimeAdapter.
    public ModelAdapterDescriptor Descriptor => TextRuntime.Descriptor;
    public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return request.Messages.SelectMany(message => message.Content).Any(part => part is ImageContentPart)
            ? ImageRuntime.PrepareAsync(request, token) : TextRuntime.PrepareAsync(request, token);
    }

    public Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
        IReadOnlyList<RemotePreparedImageInput> inputs, CancellationToken token) =>
        images.PrepareMultimodalAsync(request, inputs, token);
}
