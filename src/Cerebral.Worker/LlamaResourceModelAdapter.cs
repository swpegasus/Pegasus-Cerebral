using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Engine.Resources;

namespace Cerebral.Worker;

/// <summary>Preparation receives exact, borrowed image arrays aligned to typed content positions.
/// Implementations must finish copying or projecting the bytes before the returned task completes.</summary>
internal interface IMultimodalModelRuntimeAdapter
{
    Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
        IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token);
}

/// <summary>Host-scoped resolver. The scope comes from an accepted workflow, never model arguments.
/// Originals stay in local SQLite; text and exact admitted image bytes are resolved before prepared inference.</summary>
public sealed class LlamaResourceModelAdapter(IModelRuntimeAdapter runtime, AttachmentStore attachments, AttachmentAccessContext access,
    Func<AttachmentReference, int, CancellationToken, Task<IReadOnlyList<AttachmentArtifactText>>>? archiveText = null)
    : IModelRuntimeAdapter
{
    internal IModelRuntimeAdapter DelegatedRuntime => runtime;

    public ModelAdapterDescriptor Descriptor { get; } = runtime is IConfiguredLocalWorkflowAdapter
        ? new("llama.cpp.native.resources", "1.1.0",
            runtime is IMultimodalModelRuntimeAdapter
                ? Array.AsReadOnly(new[] { "text", "file", "image" })
                : Array.AsReadOnly(new[] { "text", "file" }),
            SupportsReasoning: runtime.Descriptor.SupportsReasoning)
        : runtime is ModelRuntime
        ? new("llama.cpp.native.resources", "1.1.0", Array.AsReadOnly(new[] { "text", "file", "image" }), SupportsReasoning: true)
        : new("cerebral.remote.resources", runtime is IMultimodalModelRuntimeAdapter ? "1.1.0" : "1.0.0",
            runtime is IMultimodalModelRuntimeAdapter
                ? Array.AsReadOnly(new[] { "text", "file", "image" })
                : Array.AsReadOnly(new[] { "text", "file" }),
            SupportsReasoning: runtime.Descriptor.SupportsReasoning);
    public static void ValidateAcceptedContent(AttachmentStore attachments, Cerebral.Contracts.Workflows.WorkflowSubmission submission)
    {
        var scope = new AttachmentAccessContext(submission.ProfileId, submission.ConversationId);
        foreach (var part in submission.AcceptedContent)
        {
            switch (part)
            {
                case FileContentPart file: attachments.ValidateReference(scope, file.Attachment); break;
                case ImageContentPart image: attachments.ValidateReference(scope, image.Attachment); break;
                case AudioContentPart: throw new NotSupportedException("Native audio attachments are not configured; dictation supplies transcript text.");
                case ResourceContentPart: throw new NotSupportedException("This resource provider has no configured admission resolver.");
            }
        }
    }

    internal const int MaximumImages = 4;
    internal const long MaximumImageBytes = 16 * 1024 * 1024;
    internal const long MaximumImagePixels = 16_000_000;

    public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selectedPolicy = (runtime as IModelExecutionPolicyFacet)?.ExecutionPolicy;
        if (selectedPolicy is null) request.Validate(); else request.Validate(selectedPolicy);
        if (request.ReasoningTokens != 0 && request.Messages.SelectMany(message => message.Content).Any(part => part is ImageContentPart))
            throw new ModelCapabilityReadinessException("This llama.cpp adapter supports images with reasoning turned off. Turn off reasoning for the selected agent, or use an image profile; the attachment remains saved.");
        var references = 0; long metadataCharacters = 0;
        foreach (var part in request.Messages.SelectMany(x => x.Content))
        {
            var reference = part switch { FileContentPart file => file.Attachment, ImageContentPart image => image.Attachment, _ => null };
            if (reference is null) continue;
            if (++references > 128) throw new InvalidDataException("Native model input exceeds 128 attachment references.");
            metadataCharacters += reference.Id.Length + reference.MediaType.Length + reference.OriginalName.Length;
            if (reference.Metadata is { } metadata)
                foreach (var pair in metadata) metadataCharacters += pair.Key.Length + pair.Value.Length;
            if (metadataCharacters > 131072) throw new InvalidDataException("Native attachment metadata exceeds its input limit.");
        }
        // Deep snapshot metadata before any load/tokenizer await. AttachmentStore compares the
        // submitted reference with its canonical metadata again before resolving original bytes.
        request = JsonSerializer.Deserialize<ModelGenerationRequest>(
            JsonSerializer.Serialize(request, CerebralJson.Options), CerebralJson.Options)!;
        var expandedMessages = new List<ModelInputMessage>();
        var preparedImages = new List<RemotePreparedImageInput>();
        var ownedImageBytes = new List<byte[]>();
        var leading = new List<string>();
        long imageBytes = 0, pixels = 0;
        var expandedCharacters = 0;
        try
        {
            foreach (var message in request.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = new List<ContentPart>();
                foreach (var part in message.Content)
                {
                    if (message.Role == "system" && part is not TextContentPart)
                        throw new NotSupportedException("Attachments are conversation input, not system instructions.");
                    switch (part)
                    {
                        case TextContentPart text:
                            AddText(text.Text); break;
                        case FileContentPart file:
                        {
                            var reference = attachments.ValidateReference(access, file.Attachment, cancellationToken);
                            if (reference.Metadata?.ContainsKey("handler.archive") == true)
                            {
                                if (archiveText is null) throw new NotSupportedException("Archive input requires the configured Cortex 7-Zip attachment handler.");
                                var remainingBytes = Math.Max(0, Math.Min(AttachmentStore.MaximumTextBytes,
                                    (1_048_576 - expandedCharacters) * 4));
                                var members = await archiveText(reference, remainingBytes, cancellationToken).ConfigureAwait(false);
                                if (members.Count == 0)
                                {
                                    AddText("\nAttached archive " + reference.OriginalName +
                                        " contains no bounded UTF-8 member selected by the archive text handler.\n");
                                    break;
                                }
                                foreach (var member in members)
                                    AddText("\nAttached archive source " + reference.OriginalName + " / " + member.EntryPath +
                                        " (reference data):\n" + member.Text);
                                break;
                            }
                            var prefix = "\nAttached source " + reference.OriginalName + " (reference data):\n";
                            var remaining = 1_048_576 - expandedCharacters - prefix.Length;
                            if (remaining < 0) throw new InvalidDataException("Expanded file input exceeds the model text limit.");
                            // A Unicode character cannot need more than four UTF-8 bytes. This preflight
                            // bounds the next allocation without pretending bytes are exact token counts.
                            var document = attachments.ReadText(access, reference.Id, reference.Revision,
                                Math.Min(AttachmentStore.MaximumTextBytes, remaining * 4), cancellationToken);
                            AddText(prefix + document.Text);
                            break;
                        }
                        case ImageContentPart image:
                        {
                            if (runtime is not IMultimodalModelRuntimeAdapter)
                                throw new RemoteModelProtocolException("This model worker does not support prepared native image input. Keep the image attached and select an image-enabled model worker.");
                            var reference = attachments.ValidateReference(access, image.Attachment, cancellationToken);
                            if (reference.MediaType is not ("image/png" or "image/jpeg"))
                                throw new NotSupportedException("Native image input requires an admitted PNG or JPEG.");
                            var width = Dimension(reference, "image.width"); var height = Dimension(reference, "image.height");
                            if (image.Width is { } submittedWidth && submittedWidth != width ||
                                image.Height is { } submittedHeight && submittedHeight != height)
                                throw new InvalidDataException("Image dimensions differ from the stored original.");
                            pixels = checked(pixels + (long)width * height);
                            imageBytes = checked(imageBytes + reference.Length);
                            if (preparedImages.Count >= MaximumImages || imageBytes > MaximumImageBytes || pixels > MaximumImagePixels)
                                throw new InvalidDataException("This native image request exceeds four images, 16 MiB, or 16 million pixels. Submit a smaller selection.");
                            var bytes = new byte[checked((int)reference.Length)];
                            ownedImageBytes.Add(bytes);
                            for (var offset = 0; offset < bytes.Length;)
                            {
                                var range = attachments.ReadRange(access, reference.Id, reference.Revision, offset,
                                    Math.Min(AttachmentStore.MaximumRangeBytes, bytes.Length - offset), cancellationToken);
                                if (range.Bytes.Length == 0) throw new InvalidDataException("Stored image bytes ended before the admitted attachment length.");
                                range.Bytes.Span.CopyTo(bytes.AsSpan(offset)); offset += range.Bytes.Length;
                            }
                            var inspection = AttachmentAdmission.InspectImage(reference.MediaType, bytes);
                            if (inspection.Width != width || inspection.Height != height)
                                throw new InvalidDataException("Decoded image dimensions differ from the admitted attachment metadata.");
                            var contentIndex = content.Count;
                            content.Add(new ImageContentPart(reference, image.AltText, width, height));
                            preparedImages.Add(new(expandedMessages.Count, contentIndex, reference.MediaType, width, height, bytes));
                            if (image.AltText is { Length: > 0 } alt) AddText("\nUser-supplied image description (not visual proof): " + alt);
                            break;
                        }
                        default: throw new NotSupportedException("This adapter supports text, admitted text files, and native images only.");
                    }
                }
                if (message.Role == "system")
                {
                    if (expandedMessages.Count != 0) throw new LlamaProtocolException("System guidance must precede conversation input.");
                    leading.Add(string.Concat(content.Cast<TextContentPart>().Select(x => x.Text)));
                }
                else expandedMessages.Add(new(message.Role, content.ToArray()));

                void AddText(string value)
                {
                    if (value.Length > 1_048_576 - expandedCharacters)
                        throw new InvalidDataException("Expanded attachment text exceeds the model input limit. Select a smaller source or explicit excerpt.");
                    expandedCharacters += value.Length;
                    content.Add(new TextContentPart(value));
                }
            }
            if (leading.Count != 0)
            {
                var guidance = string.Join("\n\n", leading);
                expandedMessages.Insert(0, new("system", [new TextContentPart(guidance)]));
                for (var index = 0; index < preparedImages.Count; index++)
                    preparedImages[index] = preparedImages[index] with { MessageIndex = preparedImages[index].MessageIndex + 1 };
            }
            // Validate the complete normalized request after bounded file expansion. Images remain
            // typed coordinates; only the transient sidecar carries their exact canonical bytes.
            var expanded = request with { Messages = expandedMessages.ToArray() };
            if (selectedPolicy is null) expanded.Validate(); else expanded.Validate(selectedPolicy);
            if (preparedImages.Count == 0)
            {
                var prepared = await runtime.PrepareAsync(expanded, cancellationToken).ConfigureAwait(false);
                if (selectedPolicy is not null) LocalModelExecutionPolicy.Require(prepared, selectedPolicy);
                return prepared;
            }
            if (selectedPolicy is not null)
                throw new NotSupportedException("Selected local text generation does not accept image sidecars.");
            var multimodal = runtime as IMultimodalModelRuntimeAdapter ??
                throw new RemoteModelProtocolException("This model worker does not support prepared native image input.");
            return await multimodal.PrepareMultimodalAsync(expanded, preparedImages.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var bytes in ownedImageBytes) Array.Clear(bytes);
        }
    }

    internal static int Dimension(AttachmentReference reference, string key) =>
        reference.Metadata?.TryGetValue(key, out var value) == true &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var dimension) && dimension > 0
            ? dimension : throw new InvalidDataException("Stored image dimensions are unavailable.");
}

public sealed partial class ModelRuntime : IMultimodalModelRuntimeAdapter
{
    internal static void ValidateProjector(string? path)
    {
        if (path is null) return;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The image projector must use an explicit absolute component path.");
        using var file = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        file.ReadExactly(magic);
        if (!magic.SequenceEqual("GGUF"u8)) throw new InvalidDataException("The image projector is not GGUF.");
    }

    public async Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
        IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var projected = ProjectMultimodalInput(request, images);
        token.ThrowIfCancellationRequested();
        var model = store.Get<ModelRecord>("model", request.ModelId) ?? throw new ArgumentException("Model not found.");
        if (model.VisionProjectorPath is null)
            throw new NotSupportedException("This model has no configured image projector. Configure its matching component before using images.");
        ValidateProjector(model.VisionProjectorPath);
        var ownership = Accounting.Begin(ModelOwnershipKind.LocalPreparation, request.ModelId);
        var (instance, slot) = await AcquireSlotAsync(request.ModelId, token, ownership);
        try
        {
            if (!string.Equals(instance.VisionProjectorPath, model.VisionProjectorPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("The loaded model uses a different image component. Unload and reload the configured model.");
            ownership.Phase(ModelOwnershipPhase.Preparing, effectStarted: true);
            var prepared = await LlamaPreparedGeneration.PrepareMultimodalAsync(instance.Http, slot, instance.Id, request,
                projected.Messages, projected.Media, model.StopSequences, () => HasExited(instance), token);
            return TrackUsage(new AccountingPreparedGeneration(prepared, ownership, cancellationToken: token));
        }
        catch { slot.Dispose(); throw; }
    }

    internal static (JsonElement Messages, string[] Media) ProjectMultimodalInput(ModelGenerationRequest request,
        IReadOnlyList<RemotePreparedImageInput> images)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(images);
        request.Validate();
        if (request.ReasoningTokens != 0)
            throw new NotSupportedException("Prepared image reasoning has no verified runtime route.");
        if (images.Count is < 1 or > LlamaResourceModelAdapter.MaximumImages)
            throw new InvalidDataException("Prepared native image input requires between one and four images.");

        var messages = new List<Dictionary<string, object?>>();
        var media = new List<string>(images.Count);
        var leading = new List<string>();
        long bytes = 0, pixels = 0;
        var imageIndex = 0;
        for (var messageIndex = 0; messageIndex < request.Messages.Count; messageIndex++)
        {
            var message = request.Messages[messageIndex];
            if (message.Role == "system")
            {
                if (messages.Count != 0) throw new LlamaProtocolException("System guidance must precede conversation input.");
                if (message.Content.Any(part => part is not TextContentPart))
                    throw new NotSupportedException("Images are conversation input, not system instructions.");
                leading.Add(string.Concat(message.Content.Cast<TextContentPart>().Select(part => part.Text)));
                continue;
            }

            var content = new List<object>();
            for (var contentIndex = 0; contentIndex < message.Content.Count; contentIndex++)
            {
                switch (message.Content[contentIndex])
                {
                    case TextContentPart text:
                        content.Add(new { type = "text", text = text.Text });
                        break;
                    case ImageContentPart image:
                    {
                        if (imageIndex >= images.Count) throw new InvalidDataException("Every image content part requires one exact byte payload.");
                        var input = images[imageIndex++];
                        if (input is null || input.Data is null || input.MessageIndex != messageIndex || input.ContentIndex != contentIndex)
                            throw new InvalidDataException("Prepared image payloads must follow their exact message and content positions.");
                        var reference = image.Attachment;
                        var width = LlamaResourceModelAdapter.Dimension(reference, "image.width");
                        var height = LlamaResourceModelAdapter.Dimension(reference, "image.height");
                        if (input.MediaType is not ("image/png" or "image/jpeg") || input.MediaType != reference.MediaType ||
                            input.Width != width || input.Height != height || reference.Length != input.Data.LongLength ||
                            image.Width is { } submittedWidth && submittedWidth != width ||
                            image.Height is { } submittedHeight && submittedHeight != height)
                            throw new InvalidDataException("Prepared image metadata does not match its admitted content part.");
                        var inspection = AttachmentAdmission.InspectImage(input.MediaType, input.Data);
                        if (inspection.Width != width || inspection.Height != height)
                            throw new InvalidDataException("Prepared image bytes do not match their admitted dimensions.");
                        bytes = checked(bytes + input.Data.LongLength);
                        pixels = checked(pixels + (long)width * height);
                        if (bytes > LlamaResourceModelAdapter.MaximumImageBytes || pixels > LlamaResourceModelAdapter.MaximumImagePixels)
                            throw new InvalidDataException("Prepared native image input exceeds 16 MiB or 16 million pixels.");
                        var encoded = Convert.ToBase64String(input.Data);
                        media.Add(encoded);
                        content.Add(new { type = "image_url", image_url = new { url = "data:" + input.MediaType + ";base64," + encoded } });
                        break;
                    }
                    default:
                        throw new NotSupportedException("Prepared native image input must contain normalized text and image parts only.");
                }
            }
            messages.Add(new() { ["role"] = message.Role, ["content"] = content });
        }
        if (imageIndex != images.Count)
            throw new InvalidDataException("Every prepared image payload must identify one exact image content part.");
        if (leading.Count != 0)
            messages.Insert(0, new() { ["role"] = "system", ["content"] = string.Join("\n\n", leading) });
        return (JsonSerializer.SerializeToElement(messages, CerebralJson.Options), media.ToArray());
    }
}

internal partial class LlamaPreparedGeneration
{
    internal static readonly ModelAdapterDescriptor VisionAdapter = new("llama.cpp.native.vision", "1.0.0",
        Array.AsReadOnly(new[] { "text", "image" }));
    internal const string VisionCountBasis = "llama.cpp native chat input_tokens, including image preprocessing; same rendered prompt and image bytes";

    internal static async Task<IPreparedModelGeneration> PrepareMultimodalAsync(HttpClient http, LlamaSlotPool.Lease slot,
        string instanceId, ModelGenerationRequest request, JsonElement messages, string[] media,
        IReadOnlyList<string> stopSequences, Func<bool> exited, CancellationToken token)
    {
        try
        {
            request.Validate();
            if (request.ReasoningTokens != 0)
                throw new NotSupportedException("Prepared image reasoning has no verified runtime route.");
            var stops = stopSequences.ToArray();
            if (stops.Length > 128 || stops.Any(x => x is null || x.Length > 4096 || x.Contains('\0')))
                throw new ArgumentException("Configured stop sequences exceed their bounds.");
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
            preparation.CancelAfter(TimeSpan.FromSeconds(60));
            var status = await LlamaProtocol.SlotAsync(http, slot.Id, preparation.Token);
            if (status.Processing)
            {
                slot.Quarantine("Reserved vision slot was already occupied. Explicitly unload and reload the model.");
                throw new IOException("The reserved vision slot was unexpectedly occupied.");
            }
            using (var response = await http.GetAsync("v1/models", HttpCompletionOption.ResponseHeadersRead, preparation.Token))
            {
                response.EnsureSuccessStatusCode();
                using var info = await LlamaProtocol.ReadJsonAsync(response.Content, 65_536, preparation.Token);
                if (!info.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array ||
                    models.GetArrayLength() != 1 || !models[0].TryGetProperty("capabilities", out var capabilities) ||
                    capabilities.ValueKind != JsonValueKind.Array || !capabilities.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "multimodal"))
                    throw new NotSupportedException("The loaded runtime does not advertise native multimodal input.");
            }
            var body = new { messages, add_generation_prompt = true, chat_template_kwargs = new { enable_thinking = false } };
            // This endpoint invokes native multimodal tokenization without sampling. n_predict=0
            // is deliberately not used: build 10937 still sampled one token during the live probe.
            using var counted = await LlamaProtocol.PostAsync(http, "v1/chat/completions/input_tokens", body, preparation.Token);
            if (counted.RootElement.ValueKind != JsonValueKind.Object ||
                !counted.RootElement.TryGetProperty("object", out var kind) || kind.GetString() != "response.input_tokens" ||
                LlamaProtocol.ReadCount(counted.RootElement, "input_tokens") is not { } input || input is < 1 or > 1_048_576)
                throw new LlamaProtocolException("The runtime did not return an exact multimodal input count.");
            using var rendered = await LlamaProtocol.PostAsync(http, "apply-template", body, preparation.Token);
            if (!rendered.RootElement.TryGetProperty("prompt", out var value) || value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { } text || text.Length > LlamaProtocol.MaximumFrameCharacters)
                throw new LlamaProtocolException("The runtime did not return a bounded multimodal prompt.");
            var metadata = ResolveBudget(instanceId, VisionAdapter, request, input, Math.Min(request.ContextLimit, status.Context), VisionCountBasis);
            var nativePrompt = JsonSerializer.SerializeToElement(new { prompt_string = text, multimodal_data = media });
            return new LlamaPreparedGeneration(http, slot, exited, nativePrompt, stops, request.ResponseSchema?.Clone(),
                request.Temperature, metadata, LlamaProtocolTiming.Default);
        }
        catch { slot.Dispose(); throw; }
    }

    internal static PreparedModelMetadata ResolveBudget(string instanceId, ModelAdapterDescriptor adapter,
        ModelGenerationRequest request, int input, int effective, string countBasis)
    {
        var desired = Math.Min(request.ResponseLimit, request.ResponseSharePercent is { } share
            ? (int)((long)effective * share / 100) : request.ResponseLimit);
        var available = effective - input - request.ReasoningTokens - AgentTokenLimits.ContextSafetyTokens;
        var response = request.ResponseSharePercent is not null || request.AllowResponseClamping ? Math.Min(desired, available) : desired;
        var metadata = new PreparedModelMetadata(instanceId, adapter.AdapterId, adapter.Version, request.ModelId, input, effective,
            Math.Max(0, response), countBasis) { DesiredResponseBudget = desired, SafetyReserveTokens = AgentTokenLimits.ContextSafetyTokens,
                ReasoningReserveTokens = request.ReasoningTokens };
        if (response < 1 || (long)input + response + request.ReasoningTokens + AgentTokenLimits.ContextSafetyTokens > effective)
            throw new ModelContextLimitException(metadata with { ResponseBudget = 0 });
        return metadata;
    }
}
