using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;

namespace Cerebral.Engine.Context;

/// <summary>Validates untrusted provider DTOs and mints the durable host-owned context projection.
/// This performs no retrieval, persistence, scheduling or model token estimation.</summary>
public static class ContextProviderNormalizer
{
    private const int MaximumPriority = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ContextProviderRequest ReadRequest(ContextProviderDescriptor descriptor, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var request = Deserialize<ContextProviderRequest>(value, "Context provider request is invalid.");
        if (!string.Equals(request.ProviderId, descriptor.Id, StringComparison.Ordinal) ||
            request.ProviderVersion != descriptor.Version)
            throw new ArgumentException("Context provider request identity and version must match the admitted command.", nameof(value));
        if (request.ContextRevision < 0 || request.ExclusionRevision < 0)
            throw new ArgumentException("Context provider revisions cannot be negative.", nameof(value));
        if (request.MaximumUtf8Bytes < 1 || request.MaximumUtf8Bytes > descriptor.MaxUtf8Bytes)
            throw new ArgumentException("Context provider request byte limit exceeds the admitted provider bound.", nameof(value));
        if (request.References is null || request.References.Count is < 1 ||
            request.References.Count > descriptor.MaxReferences)
            throw new ArgumentException("Context provider request requires a bounded non-empty reference list.", nameof(value));
        var kinds = descriptor.SupportedReferenceKinds.ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in request.References)
        {
            if (reference is null) throw new ArgumentException("Context provider references cannot be null.", nameof(value));
            ValidateToken(reference.Id, nameof(reference.Id), 256);
            ValidateToken(reference.Kind, nameof(reference.Kind), 64);
            ValidateToken(reference.SourceId, nameof(reference.SourceId), 256);
            if (!kinds.Contains(reference.Kind))
                throw new ArgumentException($"Context reference kind '{reference.Kind}' is not supported by provider '{descriptor.Id}'.", nameof(value));
            if (!ids.Add(reference.Id))
                throw new ArgumentException("Context provider reference IDs must be unique.", nameof(value));
            if (reference.ExpectedRevision < 0)
                throw new ArgumentException("Expected context source revisions cannot be negative.", nameof(value));
            if (!Enum.IsDefined(reference.Requirement))
                throw new ArgumentException("Context provider references require a valid context classification.", nameof(value));
            if (reference.Priority is < 0 or > MaximumPriority)
                throw new ArgumentException("Context provider priorities must be between 0 and 1,000,000.", nameof(value));
        }
        return request with { References = Array.AsReadOnly(request.References.ToArray()) };
    }

    public static ContextProviderResolution ReadResolution(JsonElement value) =>
        Deserialize<ContextProviderResolution>(value, "Context provider resolution is invalid.");

    public static ContextProviderResult Normalize(ContextProviderDescriptor descriptor,
        ContextProviderRequest request, ContextProviderResolution resolution, string effectId,
        string providerReceiptId)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolution);
        ValidateToken(effectId, nameof(effectId), 256);
        ValidateText(providerReceiptId, nameof(providerReceiptId), 512, 512);
        if (!string.Equals(resolution.ProviderId, descriptor.Id, StringComparison.Ordinal) ||
            resolution.ProviderVersion != descriptor.Version ||
            !string.Equals(request.ProviderId, descriptor.Id, StringComparison.Ordinal) ||
            request.ProviderVersion != descriptor.Version)
            throw new ArgumentException("Context provider descriptor, request, and result identities must match.", nameof(resolution));
        if (resolution.Sources is null || resolution.Sources.Count > descriptor.MaxReferences ||
            resolution.Sources.Count > request.References.Count)
            throw new ArgumentException("Context provider result exceeds the admitted reference count.", nameof(resolution));
        if (JsonSerializer.SerializeToUtf8Bytes(resolution, Json).Length > request.MaximumUtf8Bytes)
            throw new ArgumentException("Context provider result exceeds the exact requested UTF-8 byte limit.", nameof(resolution));

        var references = request.References.ToDictionary(reference => reference.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var contributions = new List<ContextContribution>(resolution.Sources.Count);
        foreach (var source in resolution.Sources)
        {
            if (source is null) throw new ArgumentException("Context provider sources cannot be null.", nameof(resolution));
            ValidateToken(source.Id, nameof(source.Id), 256);
            ValidateToken(source.Kind, nameof(source.Kind), 64);
            if (!seen.Add(source.Id)) throw new ArgumentException("Context provider source IDs must be unique.", nameof(resolution));
            if (!references.TryGetValue(source.Id, out var reference))
                throw new ArgumentException($"Context provider returned unrequested source '{source.Id}'.", nameof(resolution));
            if (!string.Equals(source.Kind, reference.Kind, StringComparison.Ordinal))
                throw new ArgumentException($"Context provider source '{source.Id}' changed its requested kind.", nameof(resolution));
            if (source.SourceRevision < 0 || reference.ExpectedRevision is { } expected && source.SourceRevision != expected)
                throw new ArgumentException($"Context provider source '{source.Id}' returned an unexpected revision.", nameof(resolution));
            if (reference.Requirement != source.Requirement)
                throw new ArgumentException($"Context provider source '{source.Id}' changed its workflow-authored context classification.", nameof(resolution));
            if (source.Requirement is not (ContextRequirement.Required or ContextRequirement.Optional or ContextRequirement.Reducible))
                throw new ArgumentException($"Context provider source '{source.Id}' has an invalid requirement.", nameof(resolution));
            if (source.Priority != reference.Priority || source.BeforeCurrentInput != reference.BeforeCurrentInput)
                throw new ArgumentException($"Context provider source '{source.Id}' changed its workflow-authored placement.", nameof(resolution));
            if (source.Messages is null)
                throw new ArgumentException($"Context provider source '{source.Id}' has no messages.", nameof(resolution));
            contributions.Add(new(source.Id, descriptor.Id, source.Kind, source.SourceRevision, source.Requirement,
                Array.AsReadOnly(source.Messages.ToArray()),
                ExactReadHandle(descriptor, effectId, providerReceiptId, reference, source), source.Complete,
                source.Priority, source.BeforeCurrentInput));
        }
        foreach (var reference in request.References.Where(reference =>
                     reference.Requirement == ContextRequirement.Required))
            if (!seen.Contains(reference.Id))
                throw new ArgumentException($"Context provider omitted required source '{reference.Id}'.", nameof(resolution));

        var ordered = contributions.OrderBy(source => source.Priority)
            .ThenBy(source => source.Id, StringComparer.Ordinal).ToArray();
        var window = new ContextWindow(request.ContextRevision, request.ExclusionRevision,
            Array.AsReadOnly(ordered));
        ContextValidation.ValidateWindow(window);
        var result = new ContextProviderResult(descriptor.Id, descriptor.Version, window);
        if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > ContextProviderContract.MaximumUtf8Bytes)
            throw new ArgumentException("Normalized context provider result exceeds the durable workflow result limit.", nameof(resolution));
        return result;
    }

    internal static JsonElement ToElement(ContextProviderResult value) => JsonSerializer.SerializeToElement(value, Json);

    private static T Deserialize<T>(JsonElement value, string error)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException(error, nameof(value));
        try { return value.Deserialize<T>(Json) ?? throw new ArgumentException(error, nameof(value)); }
        catch (JsonException exception) { throw new ArgumentException(error, nameof(value), exception); }
    }

    private static string ExactReadHandle(ContextProviderDescriptor descriptor, string effectId,
        string providerReceiptId, ContextProviderReference reference, ContextProviderSource source)
    {
        var handle = string.Join('.', "cerebral-context:v1", Token(descriptor.Id),
            descriptor.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), Token(effectId),
            Token(providerReceiptId), Token(reference.SourceId),
            source.SourceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (handle.Length > 2048)
            throw new ArgumentException("Host-authored context exact-read handle exceeds 2,048 characters.", nameof(source));
        return handle;
    }

    private static string Token(string value) => Convert.ToBase64String(StrictUtf8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidateToken(string value, string name, int maximumLength)
    {
        ValidateText(value, name, maximumLength);
        if (!char.IsAsciiLetterOrDigit(value[0]) || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ':' or '/' or '+')))
            throw new ArgumentException($"{name} must be a bounded ASCII context identifier.", name);
    }

    private static void ValidateText(string value, string name, int maximumLength, int? maximumUtf8Bytes = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Contains('\0'))
            throw new ArgumentException($"{name} is empty, too large, or contains a null character.", name);
        try
        {
            var bytes = StrictUtf8.GetByteCount(value);
            if (maximumUtf8Bytes is { } maximum && bytes > maximum)
                throw new ArgumentException($"{name} exceeds {maximum} UTF-8 bytes.", name);
        }
        catch (EncoderFallbackException exception) { throw new ArgumentException($"{name} is not valid UTF-8 text.", name, exception); }
    }
}
