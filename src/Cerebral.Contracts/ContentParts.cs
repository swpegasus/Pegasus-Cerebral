using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Contracts;

/// <summary>Durable attachment coordinate. Binary data is owned by the referenced store, never embedded here.</summary>
public sealed record AttachmentReference(
    string Id,
    long Revision,
    string MediaType,
    string OriginalName,
    long Length,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Durable external or plugin-owned resource coordinate.</summary>
public sealed record ResourceReference(
    string ProviderId,
    string ResourceId,
    string? Revision = null,
    string? MediaType = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextContentPart), "text")]
[JsonDerivedType(typeof(FileContentPart), "file")]
[JsonDerivedType(typeof(ImageContentPart), "image")]
[JsonDerivedType(typeof(AudioContentPart), "audio")]
[JsonDerivedType(typeof(StructuredContentPart), "structured")]
[JsonDerivedType(typeof(ResourceContentPart), "resource")]
public abstract record ContentPart
{
    internal abstract void Validate(int index);
}

public sealed record TextContentPart(string Text, string Format = "plain") : ContentPart
{
    internal override void Validate(int index)
    {
        ContractValidation.Text(Text, $"Content[{index}].Text", ContractValidation.MaximumTextCharacters, allowEmpty: true);
        ContractValidation.DottedName(Format, $"Content[{index}].Format", 64);
    }
}

public sealed record FileContentPart(AttachmentReference Attachment) : ContentPart
{
    internal override void Validate(int index) => ContractValidation.Attachment(Attachment, index);
}

public sealed record ImageContentPart(
    AttachmentReference Attachment,
    string? AltText = null,
    int? Width = null,
    int? Height = null) : ContentPart
{
    internal override void Validate(int index)
    {
        ContractValidation.Attachment(Attachment, index);
        ContractValidation.OptionalText(AltText, $"Content[{index}].AltText", 16_384);
        if (Width is <= 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(Width));
        if (Height is <= 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(Height));
    }
}

public sealed record AudioContentPart(
    AttachmentReference Attachment,
    string? Transcript = null,
    double? DurationSeconds = null) : ContentPart
{
    internal override void Validate(int index)
    {
        ContractValidation.Attachment(Attachment, index);
        ContractValidation.OptionalText(Transcript, $"Content[{index}].Transcript", ContractValidation.MaximumTextCharacters);
        if (DurationSeconds is { } duration && (!double.IsFinite(duration) || duration < 0 || duration > 31_536_000))
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
    }
}

public sealed record StructuredContentPart(string Schema, JsonElement Value) : ContentPart
{
    internal override void Validate(int index)
    {
        ContractValidation.DottedName(Schema, $"Content[{index}].Schema", 128);
        if (Value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException($"Content[{index}].Value cannot be undefined.", nameof(Value));
    }
}

public sealed record ResourceContentPart(ResourceReference Resource) : ContentPart
{
    internal override void Validate(int index)
    {
        if (Resource is null) throw new ArgumentException($"Content[{index}].Resource is required.", nameof(Resource));
        ContractValidation.Identifier(Resource.ProviderId, $"Content[{index}].Resource.ProviderId", 128);
        ContractValidation.Text(Resource.ResourceId, $"Content[{index}].Resource.ResourceId", 2048);
        ContractValidation.OptionalText(Resource.Revision, $"Content[{index}].Resource.Revision", 256);
        ContractValidation.OptionalText(Resource.MediaType, $"Content[{index}].Resource.MediaType", 256);
    }
}

internal static class ContractValidation
{
    public const int MaximumTextCharacters = 1_048_576;

    public static void Attachment(AttachmentReference attachment, int index)
    {
        if (attachment is null) throw new ArgumentException($"Content[{index}].Attachment is required.", nameof(attachment));
        Identifier(attachment.Id, $"Content[{index}].Attachment.Id", 128);
        Text(attachment.MediaType, $"Content[{index}].Attachment.MediaType", 256);
        Text(attachment.OriginalName, $"Content[{index}].Attachment.OriginalName", 1024);
        if (attachment.Revision < 1) throw new ArgumentOutOfRangeException(nameof(attachment.Revision));
        if (attachment.Length < 0) throw new ArgumentOutOfRangeException(nameof(attachment.Length));
        if (attachment.Metadata is null) return;
        if (attachment.Metadata.Count > 64) throw new ArgumentException("Attachment metadata is limited to 64 entries.", nameof(attachment.Metadata));
        foreach (var (key, value) in attachment.Metadata)
        {
            DottedName(key, $"Content[{index}].Attachment.Metadata key", 128);
            Text(value, $"Content[{index}].Attachment.Metadata[{key}]", 4096, allowEmpty: true);
        }
    }

    public static void Identifier(string value, string name, int maximumLength)
    {
        Text(value, name, maximumLength);
        if (value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
            throw new ArgumentException($"{name} cannot contain whitespace or control characters.", name);
    }

    public static void OptionalIdentifier(string? value, string name, int maximumLength)
    {
        if (value is not null) Identifier(value, name, maximumLength);
    }

    public static void DottedName(string value, string name, int maximumLength)
    {
        Identifier(value, name, maximumLength);
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or '/')))
            throw new ArgumentException($"{name} contains unsupported characters.", name);
    }

    public static void OptionalText(string? value, string name, int maximumLength)
    {
        if (value is not null) Text(value, name, maximumLength, allowEmpty: true);
    }

    public static void Text(string value, string name, int maximumLength, bool allowEmpty = false)
    {
        if (value is null) throw new ArgumentNullException(name);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximumLength || value.Contains('\0'))
            throw new ArgumentException($"{name} is empty, too large, or contains a null character.", name);
    }
}
