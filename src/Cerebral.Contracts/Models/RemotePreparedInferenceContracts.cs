using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Contracts.Models;

/// <summary>Versioned owner-only preparation; input is never a command or an approximate token count.</summary>
public static class RemotePreparedProtocol
{
    public const int Version = 1;
    public const int ImageVersion = 2;
    public const int MaximumRequestBytes = 3 * 1024 * 1024;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    public const int MaximumOutputCharacters = 1_048_576;
    public const int MaximumImages = 4;
    public const long MaximumImageBytes = 16 * 1024 * 1024;
    public const long MaximumImagePixels = 16_000_000;
    public const string PrepareOperation = "inference.prepare";
    public const string GenerateOperation = "inference.prepared.generate";
    public const string ReleaseOperation = "inference.prepared.release";

    public static void Validate(RemotePrepareRequest value)
    {
        if (value.ProtocolVersion != Version) throw new ArgumentException("Unsupported remote prepared-inference protocol.");
        ValidateRequest(value.Request, value.ExpectedModel);
        if (value.Request.Messages.SelectMany(message => message.Content).Any(part => part is not TextContentPart))
            throw new NotSupportedException("Remote prepared inference version 1 supports text only. Expand admitted files locally; use a local image-enabled model for images.");
    }

    public static void Validate(RemotePrepareRequestV2 value)
    {
        if (value.ProtocolVersion != ImageVersion) throw new ArgumentException("Unsupported remote prepared-image protocol.");
        ValidateRequest(value.Request, value.ExpectedModel);
        if (value.Images is null || value.Images.Count is < 1 or > MaximumImages)
            throw new ArgumentException($"Remote prepared image input requires between 1 and {MaximumImages} images.");
        if (value.Request.ReasoningTokens != 0)
            throw new NotSupportedException("Prepared remote image reasoning is not supported.");
        if (value.Request.Messages.SelectMany(message => message.Content).Any(part => part is not (TextContentPart or ImageContentPart)))
            throw new NotSupportedException("Remote prepared image inference supports text and already-resolved PNG or JPEG images only.");

        var expected = new Dictionary<(int Message, int Content), ImageContentPart>();
        for (var messageIndex = 0; messageIndex < value.Request.Messages.Count; messageIndex++)
        {
            var message = value.Request.Messages[messageIndex];
            for (var contentIndex = 0; contentIndex < message.Content.Count; contentIndex++)
            {
                if (message.Content[contentIndex] is not ImageContentPart image) continue;
                if (message.Role == "system") throw new NotSupportedException("Images are conversation input, not system instructions.");
                expected.Add((messageIndex, contentIndex), image);
            }
        }
        if (expected.Count != value.Images.Count)
            throw new ArgumentException("Every prepared image content part requires one exact inline payload.");

        long bytes = 0, pixels = 0;
        var seen = new HashSet<(int Message, int Content)>();
        foreach (var input in value.Images)
        {
            if (input is null) throw new ArgumentException("A prepared image payload cannot be null.");
            var position = (input.MessageIndex, input.ContentIndex);
            if (!seen.Add(position) || !expected.TryGetValue(position, out var image))
                throw new ArgumentException("Prepared image coordinates must identify each image content part exactly once.");
            if (input.MediaType is not ("image/png" or "image/jpeg") || input.MediaType != image.Attachment.MediaType)
                throw new ArgumentException("Prepared image media type must match its admitted PNG or JPEG reference.");
            if (input.Width is < 1 or > 1_000_000 || input.Height is < 1 or > 1_000_000 ||
                image.Width is { } width && width != input.Width || image.Height is { } height && height != input.Height)
                throw new ArgumentException("Prepared image dimensions must match its admitted content part.");
            if (input.Data is null || input.Data.Length == 0 || image.Attachment.Length != input.Data.LongLength)
                throw new ArgumentException("Prepared image bytes must exactly match the admitted attachment length.");
            bytes = checked(bytes + input.Data.LongLength);
            pixels = checked(pixels + (long)input.Width * input.Height);
            if (bytes > MaximumImageBytes || pixels > MaximumImagePixels)
                throw new ArgumentException($"Remote prepared image input exceeds {MaximumImageBytes} bytes or {MaximumImagePixels} pixels.");
        }
    }

    public static void Validate(RemoteLeaseRequest value)
    {
        if (value.ProtocolVersion is not (Version or ImageVersion) || value.LeaseId is null || value.LeaseId.Length != 32 ||
            value.LeaseId.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("An exact supported prepared lease identifier is required.");
    }

    private static void ValidateRequest(ModelGenerationRequest? request, RemotePreparedModelBinding? expectedModel)
    {
        if (request is null || expectedModel is null) throw new ArgumentException("A generation request and immutable model binding are required.");
        request.Validate();
        expectedModel.Validate();
        if (request.ModelId != expectedModel.Id) throw new ArgumentException("The generation request does not match its frozen model.");
    }

    public static void ValidateMetadata(PreparedModelMetadata metadata, ModelGenerationRequest request, bool contextLimited = false)
    {
        if (metadata is null || metadata.ModelId != request.ModelId ||
            string.IsNullOrWhiteSpace(metadata.InstanceId) || metadata.InstanceId.Length > 256 ||
            string.IsNullOrWhiteSpace(metadata.AdapterId) || metadata.AdapterId.Length > 128 ||
            string.IsNullOrWhiteSpace(metadata.AdapterVersion) || metadata.AdapterVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(metadata.CountBasis) || metadata.CountBasis.Length > 4096 ||
            metadata.InputTokens is < 0 or > 1_048_576 || metadata.EffectiveContext is < 512 or > 1_048_576 ||
            metadata.EffectiveContext > request.ContextLimit || metadata.ResponseBudget < 0 ||
            metadata.ResponseBudget > request.ResponseLimit || metadata.DesiredResponseBudget < metadata.ResponseBudget ||
            metadata.DesiredResponseBudget > request.ResponseLimit || metadata.SafetyReserveTokens is < 0 or > 131072 ||
            metadata.ReasoningReserveTokens != request.ReasoningTokens)
            throw new InvalidDataException("Remote preparation returned inconsistent exact token metadata.");
        if (!contextLimited && (metadata.ResponseBudget < 1 ||
            (long)metadata.InputTokens + metadata.ResponseBudget + metadata.SafetyReserveTokens + metadata.ReasoningReserveTokens > metadata.EffectiveContext))
            throw new InvalidDataException("Remote preparation returned an invalid measured generation budget.");
    }
}

public sealed record RemotePreparedModelBinding(string Id, string Path, string? ChatTemplate,
    IReadOnlyList<string> StopSequences, int MaximumContextTokens, int ParallelRequests, int GpuLayers,
    string? VisionProjectorPath = null)
{
    public void Validate()
    {
        ContractValidation.Identifier(Id, nameof(Id), 128);
        if (string.IsNullOrWhiteSpace(Path) || Path.Length > 32768 || Path.Contains('\0') ||
            ChatTemplate?.Length > 65536 || MaximumContextTokens is < 0 or > 1_048_576 || MaximumContextTokens is > 0 and < 512 ||
            ParallelRequests is < 1 or > 64 || StopSequences is null || StopSequences.Count > 128 ||
            StopSequences.Any(value => value is null || value.Length > 4096 || value.Contains('\0')) ||
            VisionProjectorPath?.Length > 32768)
            throw new ArgumentException("Remote model binding exceeds its immutable contract.");
        // The path belongs to the remote OS. The coordinator never normalizes or probes it.
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemotePrepareRequest(int ProtocolVersion, ModelGenerationRequest Request, RemotePreparedModelBinding ExpectedModel);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemotePrepareRequestV2(int ProtocolVersion, ModelGenerationRequest Request, RemotePreparedModelBinding ExpectedModel,
    IReadOnlyList<RemotePreparedImageInput> Images);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemotePreparedImageInput(int MessageIndex, int ContentIndex, string MediaType, int Width, int Height, byte[] Data);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteLeaseRequest(int ProtocolVersion, string LeaseId);
public sealed record RemotePreparedResponse(int ProtocolVersion, string? LeaseId, PreparedModelMetadata Metadata,
    ModelAdapterDescriptor Adapter, DateTimeOffset? ExpiresAt, bool ContextLimited = false);
public sealed record RemoteGeneratedResponse(int ProtocolVersion, string LeaseId, PreparedModelMetadata Metadata, ModelGenerationResult Result);
public sealed record RemoteReleaseResponse(int ProtocolVersion, string LeaseId, bool Released, bool CancellationRequested);
