using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Core;

public sealed record ModelRuntimeSupportQueryRequest(int Version, string ModelId,
    string ExpectedWorkerInstallationId, ModelRuntimeSupportBinding? ExpectedBinding = null);
public sealed record RemoteModelRuntimeSupportQueryRequest(string WorkerId, ModelRuntimeSupportQueryRequest Request);

/// <summary>A point-in-time observation, never a lease, fit result or inference admission.
/// Null owner/time means no Worker observation was obtained. Unavailable never means unloaded.</summary>
public sealed record ModelRuntimeSupportQueryResponse(int Version, string ModelId,
    string? WorkerInstallationId, DateTimeOffset? ObservedAt, string State, string? Reason,
    ModelRuntimeSupportDescriptor? Support, bool ExpandedPathSupported);

/// <summary>Ordinary OwnerOnly model-management metadata, with the existing transport authority.
/// These query codecs are unrelated to native generation capability witnesses.</summary>
public static class ModelRuntimeSupportQueryProtocol
{
    public const int Version = 1;
    public const string ReadOperation = "models.runtime-support.read";
    public const string RemoteReadOperation = "workers.models.runtime-support.read";
    public const int MaximumRequestBytes = 4096;
    // 64 capabilities * (32 input + 32 output kinds) * 128 ASCII characters, plus
    // all capability IDs, binding, six ranges and JSON structure, fits below 1 MiB.
    // Preserve base descriptor bounds; this is deliberately larger than native's empty list.
    public const int MaximumResponseBytes = 1_048_576;
    public const int MaximumDiscoveryBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = CreateJson();
    public static JsonTypeInfo<ModelRuntimeSupportQueryRequest> RequestCodec { get; } = Codec<ModelRuntimeSupportQueryRequest>();
    public static JsonTypeInfo<RemoteModelRuntimeSupportQueryRequest> RemoteRequestCodec { get; } = Codec<RemoteModelRuntimeSupportQueryRequest>();
    public static JsonTypeInfo<ModelRuntimeSupportQueryResponse> ResponseCodec { get; } = Codec<ModelRuntimeSupportQueryResponse>();

    private static JsonSerializerOptions CreateJson()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict, RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true, MaxDepth = 16, TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        json.Converters.Add(new LimitBasisCodec());
        json.MakeReadOnly();
        return json;
    }

    private sealed class LimitBasisCodec : JsonConverter<RuntimeLimitBasis>
    {
        public override RuntimeLimitBasis Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            return reader.GetString() switch {
                "backendHard" => RuntimeLimitBasis.BackendHard, "modelNative" => RuntimeLimitBasis.ModelNative,
                "operatorPolicy" => RuntimeLimitBasis.OperatorPolicy, "wireCompatibility" => RuntimeLimitBasis.WireCompatibility,
                "unreported" => RuntimeLimitBasis.Unreported, _ => throw new JsonException() };
        }
        public override void Write(Utf8JsonWriter writer, RuntimeLimitBasis value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch {
                RuntimeLimitBasis.BackendHard => "backendHard", RuntimeLimitBasis.ModelNative => "modelNative",
                RuntimeLimitBasis.OperatorPolicy => "operatorPolicy", RuntimeLimitBasis.WireCompatibility => "wireCompatibility",
                RuntimeLimitBasis.Unreported => "unreported", _ => throw new JsonException() });
    }

    private static JsonTypeInfo<T> Codec<T>()
    {
        var codec = (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T));
        codec.MakeReadOnly();
        return codec;
    }

    public static ModelRuntimeSupportQueryRequest ReadRequest(JsonElement value)
    {
        var request = Decode(value, RequestCodec, MaximumRequestBytes);
        ValidateRequest(request);
        return request;
    }

    public static RemoteModelRuntimeSupportQueryRequest ReadRemoteRequest(JsonElement value)
    {
        var request = Decode(value, RemoteRequestCodec, MaximumRequestBytes);
        Identifier(request.WorkerId);
        if (request.WorkerId == "local") throw Invalid();
        ValidateRequest(request.Request);
        return request;
    }

    public static JsonElement WriteRequest(ModelRuntimeSupportQueryRequest request, string? workerId = null)
    {
        ValidateRequest(request);
        if (workerId is null) return Encode(request, RequestCodec, MaximumRequestBytes);
        Identifier(workerId);
        if (workerId == "local") throw Invalid();
        return Encode(new RemoteModelRuntimeSupportQueryRequest(workerId, request), RemoteRequestCodec, MaximumRequestBytes);
    }

    public static JsonElement WriteResponse(ModelRuntimeSupportQueryResponse response,
        ModelRuntimeSupportQueryRequest request)
    {
        ValidateResponse(response, request);
        if (response.Support is { } source)
            response = response with { Support = source with { Capabilities = Array.AsReadOnly(
                Bounded(source.Capabilities, ModelRuntimeSupportContract.MaximumCapabilities).Select(capability =>
                {
                    if (capability is null) throw Invalid();
                    return capability with {
                        InputKinds = Array.AsReadOnly(Bounded(capability.InputKinds, ModelRuntimeSupportContract.MaximumKinds)),
                        OutputKinds = Array.AsReadOnly(Bounded(capability.OutputKinds, ModelRuntimeSupportContract.MaximumKinds)) };
                }).ToArray()) } };
        ValidateResponse(response, request);
        var encoded = Encode(response, ResponseCodec, MaximumResponseBytes);
        // Validate the captured wire value too: caller-owned IReadOnlyList implementations
        // need not be immutable between the structural check and serialization.
        _ = ReadResponse(encoded, request);
        return encoded;
    }

    public static ModelRuntimeSupportQueryResponse ReadResponse(JsonElement value,
        ModelRuntimeSupportQueryRequest request)
    {
        var response = Decode(value, ResponseCodec, MaximumResponseBytes);
        ValidateResponse(response, request);
        return response;
    }

    public static ModelRuntimeSupportQueryResponse Unavailable(ModelRuntimeSupportQueryRequest request,
        string reason, string? observedWorker = null, DateTimeOffset? observedAt = null) =>
        new(Version, request.ModelId, observedWorker, observedAt, "unavailable", reason, null, false);

    public static bool SupportsOperation(JsonElement discovery, string operation)
    {
        CheckJson(discovery, MaximumDiscoveryBytes);
        if (discovery.ValueKind != JsonValueKind.Object) throw Invalid();
        if (!discovery.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String ||
            contract.GetString() != WorkflowContract.Id || !discovery.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != WorkflowContract.Version) return false;
        if (!discovery.TryGetProperty("operations", out var operations)) return false;
        if (operations.ValueKind != JsonValueKind.Array) throw Invalid();
        var matched = false;
        var supported = false;
        foreach (var item in operations.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String) throw Invalid();
            if (id.GetString() != operation) continue;
            if (matched || !item.TryGetProperty("version", out var selected) || selected.ValueKind != JsonValueKind.Number ||
                !selected.TryGetInt32(out var selectedVersion) || selectedVersion < 1) throw Invalid();
            matched = true;
            supported = selectedVersion == Version;
        }
        return supported;
    }

    public static void ValidateRequest(ModelRuntimeSupportQueryRequest request)
    {
        if (request is null || request.Version != Version) throw Invalid();
        Identifier(request.ModelId); Identifier(request.ExpectedWorkerInstallationId);
        if (request.ExpectedBinding is { } expected)
        {
            ValidateBinding(expected);
            if (expected.WorkerInstallationId != request.ExpectedWorkerInstallationId ||
                expected.ModelId != request.ModelId || expected.InstanceId is null) throw Invalid();
        }
    }

    public static void ValidateResponse(ModelRuntimeSupportQueryResponse response, ModelRuntimeSupportQueryRequest request)
    {
        ValidateRequest(request);
        if (response is null || response.Version != Version || response.ModelId != request.ModelId ||
            response.ExpandedPathSupported || (response.WorkerInstallationId is null) != (response.ObservedAt is null) ||
            response.WorkerInstallationId is { } worker && worker != request.ExpectedWorkerInstallationId ||
            response.ObservedAt is { } time && (time.Offset != TimeSpan.Zero || time == default)) throw Invalid();
        if (response.State == "unavailable")
        {
            if (response.Support is not null || response.Reason is not ("not_reported" or "operation_unavailable" or
                "identity_changed" or "binding_changed")) throw Invalid();
            return;
        }
        if (response.State != "available" || response.Reason is not null ||
            response.WorkerInstallationId is null || response.Support is not { } support) throw Invalid();
        ValidateDescriptor(support);
        if (support.Binding.WorkerInstallationId != response.WorkerInstallationId || support.Binding.ModelId != request.ModelId ||
            support.Binding.InstanceId is null || request.ExpectedBinding is { } expected && support.Binding != expected) throw Invalid();
    }

    // Base structural bounds stay intact. Native consumption below is a separate semantic check.
    public static void ValidateDescriptor(ModelRuntimeSupportDescriptor descriptor)
    {
        if (descriptor is null || descriptor.SchemaVersion != ModelRuntimeSupportContract.SchemaVersion ||
            descriptor.Capacity is null || descriptor.Capabilities is null ||
            descriptor.Capabilities.Count > ModelRuntimeSupportContract.MaximumCapabilities) throw Invalid();
        ValidateBinding(descriptor.Binding);
        var capacity = descriptor.Capacity;
        foreach (var range in new[] { capacity.InferenceSlots, capacity.ContextPerSlotTokens, capacity.VisibleOutputTokens,
            capacity.ReasoningTokens, capacity.CombinedGenerationTokens, capacity.AggregateContextTokens })
        {
            if (range is null || range.Minimum < 0 || range.Step < 1 || range.Maximum is { } max && max < range.Minimum ||
                range.Provenance is null || !Enum.IsDefined(range.Provenance.Basis)) throw Invalid();
            Identifier(range.Provenance.SourceId); Identifier(range.Provenance.Revision);
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in Bounded(descriptor.Capabilities, ModelRuntimeSupportContract.MaximumCapabilities))
        {
            if (capability is null || capability.ContractVersion < 1) throw Invalid();
            Identifier(capability.Id); Identifier(capability.ContractId);
            if (!ids.Add(capability.Id)) throw Invalid();
            ValidateKinds(capability.InputKinds); ValidateKinds(capability.OutputKinds);
        }
    }

    public static void RequirePassiveQuerySemantics(ModelRuntimeSupportQueryResponse response)
    {
        if (response is null) throw Invalid();
        if (response.Support is { } descriptor) ValidateDescriptor(descriptor);
        if (response.ExpandedPathSupported || response.Support is { } support &&
            support.Capabilities.Count != 0) throw Invalid();
        // The executing Worker owns backend mechanics and range provenance. Structurally
        // valid metadata may evolve; accepting it here never grants execution or changes fit.
    }

    private static void ValidateKinds(IReadOnlyList<string> kinds)
    {
        if (kinds is null || kinds.Count is < 1 or > ModelRuntimeSupportContract.MaximumKinds) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in Bounded(kinds, ModelRuntimeSupportContract.MaximumKinds))
        { Identifier(kind); if (!seen.Add(kind)) throw Invalid(); }
    }

    private static T[] Bounded<T>(IReadOnlyList<T> values, int maximum)
    {
        if (values is null || values.Count > maximum) throw Invalid();
        var copy = values.Take(maximum + 1).ToArray();
        if (copy.Length != values.Count || copy.Length > maximum) throw Invalid();
        return copy;
    }

    private static void ValidateBinding(ModelRuntimeSupportBinding binding)
    {
        if (binding is null) throw Invalid();
        Identifier(binding.WorkerInstallationId); Identifier(binding.BackendId); Identifier(binding.AdapterId);
        Identifier(binding.AdapterVersion); Identifier(binding.ModelId);
        if (binding.InstanceId is not null) Identifier(binding.InstanceId);
        var scope = binding.ConfigurationScopeId;
        if (string.IsNullOrEmpty(scope) || scope.Length > ModelRuntimeSupportContract.MaximumIdentifierCharacters) throw Invalid();
        var slash = scope.LastIndexOf('/');
        if (slash < 1 || slash == scope.Length - 1 || scope.AsSpan(0, slash).IndexOf('/') >= 0) throw Invalid();
        Identifier(scope[..slash]);
        if (!char.IsAsciiLetterOrDigit(scope[0]) || !char.IsAsciiLetterOrDigit(scope[slash - 1]) ||
            scope[(slash + 1)..].Any(c => !char.IsAsciiDigit(c)) ||
            !int.TryParse(scope.AsSpan(slash + 1), out var version) || version <= 0 ||
            binding.ConfigurationSha256 is not { Length: 64 } digest ||
            digest.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))) throw Invalid();
    }

    private static void Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ModelRuntimeSupportContract.MaximumIdentifierCharacters ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or ':'))) throw Invalid();
    }

    private static T Decode<T>(JsonElement value, JsonTypeInfo<T> codec, int maximum) where T : class
    {
        CheckJson(value, maximum);
        try { return value.Deserialize(codec) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
    }

    private static JsonElement Encode<T>(T value, JsonTypeInfo<T> codec, int maximum)
    {
        try
        {
            var encoded = JsonSerializer.SerializeToElement(value, codec);
            CheckJson(encoded, maximum);
            return encoded;
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static void CheckJson(JsonElement value, int maximum)
    {
        if (value.ValueKind is JsonValueKind.Undefined) throw Invalid();
        var text = value.GetRawText();
        if (text.Length > maximum || Encoding.UTF8.GetByteCount(text) > maximum) throw Invalid();
        CheckDuplicates(value, 0);
    }

    private static void CheckDuplicates(JsonElement value, int depth)
    {
        if (depth > 16) throw Invalid();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!fields.Add(property.Name)) throw Invalid(); CheckDuplicates(property.Value, depth + 1); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item, depth + 1);
    }

    private static InvalidDataException Invalid() => new("runtime_support_query_invalid_or_unsupported");
}
