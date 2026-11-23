using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace Cerebral.Contracts.ModelOperations;

/// <summary>Strict structural admission only. The host separately verifies grants, exact identities,
/// immutable bytes, qualified settings and current resource ownership before any effects.</summary>
public static class ModelOperationContract
{
    public const int Version = 1;
    public const string ImageTo3D = "image_to_3d";
    public const string MeshRetexture = "mesh_retexture";
    public const int MaximumTextureResolution = 4096;
    public const int MaximumBatchItems = 64;
    public const int MaximumRequestBytes = 4 * 1024 * 1024;
    public const long MaximumArtifactBytes = 512L * 1024 * 1024;
    public const long MaximumJobArtifactBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumOutputs = 32;
    public static IReadOnlyList<string> Operations { get; } = Array.AsReadOnly(new[] { ImageTo3D, MeshRetexture });
    public static IReadOnlyList<int> TextureResolutions { get; } = Array.AsReadOnly(new[] { 1024, 2048, 3072, 4096 });
    /// <summary>Sampler ranges are pinned upstream UI bounds; token/sample/export ceilings are operator policy.</summary>
    public const string SamplerRangeBasis = "upstream_ui";
    public const string ExportRangeBasis = "operator_policy";
    public static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict, RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true, MaxDepth = 24,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    public static T Read<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumRequestBytes) throw Invalid("Model operation JSON exceeds its bound.");
        RejectDuplicateProperties(bytes);
        var value = JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid("Model operation JSON is null.");
        ValidateValue(value);
        return value;
    }

    public static byte[] Write<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateValue(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumRequestBytes) throw Invalid("Model operation JSON exceeds its bound.");
        return bytes;
    }

    /// <summary>A serialization snapshot prevents a caller-owned collection changing after admission.</summary>
    public static T Freeze<T>(T value) => Read<T>(Write(value));

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 24 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.OrdinalIgnoreCase));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw Invalid("Duplicate model operation JSON property.");
        }
    }

    private static void ValidateValue<T>(T value)
    {
        switch (value)
        {
            case ModelOperationSubmissionRequest request: Validate(request); break;
            case ModelOperationRequest request: Validate(request); break;
            case ModelOperationResult result: Validate(result); break;
            case ModelOperationProgress progress: Validate(progress); break;
            case ModelOperationRuntimeBinding binding: Validate(binding); break;
            case ModelOperationArtifactReference artifact: Validate(artifact); break;
            case ModelOperationOptions options: Validate(options); break;
            case ModelOperationRuntimeDescriptor descriptor: Validate(descriptor); break;
            case ModelOperationProfile profile: Validate(profile); break;
            case ModelOperationExportRequest request:
                Header(request.Version, request.RequestId, request.JobId, request.EffectId); Validate(request.Binding); Validate(request.Options); break;
            case ModelOperationCancelRequest request:
                Header(request.Version, request.RequestId, request.JobId, request.EffectId); Validate(request.Binding); break;
            case ModelOperationCancellation cancellation:
                Require(cancellation.Version == Version, "Unsupported cancellation version."); Id(cancellation.JobId); Id(cancellation.EffectId);
                Require(!cancellation.PhysicalReleaseConfirmed || cancellation.Confirmed, "Physical release requires confirmed cancellation."); break;
            case ModelOperationRuntimeState state:
                Validate(state.Binding); Require(state.State is "unloaded" or "loading" or "loaded" or "draining" or "unknown", "Invalid runtime state.");
                Require(!state.PhysicalReleaseConfirmed || state.State == "unloaded", "Release is confirmed only for an unloaded instance."); Error(state.Error); break;
            default: throw Invalid("Unsupported model operation value type.");
        }
    }

    public static void Validate(ModelOperationSubmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.Version == Version, "Unsupported submission version."); Id(request.RequestId); Id(request.ModelId); Operation(request.Operation);
        var items = Bounded(request.Items, 1, MaximumBatchItems);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            Require(item is not null, "Null batch item."); Id(item.ItemId);
            Require(ids.Add(item.ItemId), "Duplicate batch item identity."); Inputs(request.Operation, item.Inputs); Validate(item.Options);
        }
    }

    public static void Validate(ModelOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Header(request.Version, request.RequestId, request.JobId, request.EffectId);
        Operation(request.Operation); Validate(request.Binding); Inputs(request.Operation, request.Inputs); Validate(request.Options);
    }

    public static void Validate(ModelOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Header(result.Version, result.RequestId, result.JobId, result.EffectId); Validate(result.Binding);
        Require(Enum.IsDefined(result.State), "Unknown result state."); Error(result.Error);
        var outputs = Bounded(result.Outputs, 0, MaximumOutputs);
        long total = 0;
        var identities = new HashSet<(string, long)>();
        foreach (var output in outputs)
        {
            Require(output is not null, "Null output."); Id(output.Role, 64); Validate(output.Artifact);
            Require(output.Artifact.WorkerInstallationId == result.Binding.WorkerInstallationId &&
                output.Artifact.JobId == result.JobId && output.Artifact.EffectId == result.EffectId,
                "Output does not belong to the exact executing worker/job/effect.");
            Require(identities.Add((output.Artifact.ArtifactId, output.Artifact.Revision)), "Duplicate output artifact.");
            total = checked(total + output.Artifact.Bytes);
        }
        Require(total <= MaximumJobArtifactBytes, "Job artifact bytes exceed the bound.");
        if (result.State == ModelOperationState.Succeeded)
            Require(!result.Partial && result.Error is null && outputs.Length > 0, "Succeeded requires complete admitted outputs and no error.");
        else Require(outputs.Length == 0, "Only success exposes admitted output artifacts.");
        if (result.State == ModelOperationState.Unknown) Require(result.Partial, "Unknown results must preserve uncertainty.");
    }

    public static void Validate(ModelOperationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Require(progress.Version == Version && Enum.IsDefined(progress.State), "Invalid progress version or state.");
        Id(progress.JobId); Id(progress.EffectId); Id(progress.Stage, 64); Range(progress.Fraction, 0, 1, "progress fraction");
        Text(progress.Message, 512, optional: true);
    }

    public static void Validate(ModelOperationRuntimeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        foreach (var id in new[] { binding.WorkerInstallationId, binding.RuntimeId, binding.RuntimeVersion,
                     binding.ModelId, binding.PackageId, binding.PackageVersion, binding.ProfileId, binding.InstanceId }) Id(id);
        Digest(binding.RuntimeSha256); Digest(binding.PackageSha256); Digest(binding.ProfileSha256);
    }

    public static void Validate(ModelOperationArtifactReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        foreach (var id in new[] { reference.WorkerInstallationId, reference.JobId, reference.EffectId, reference.ArtifactId }) Id(id);
        Id(reference.Role, 64); ProfileName(reference.MediaProfile);
        Require(reference.Revision >= 1 && reference.Bytes is > 0 and <= MaximumArtifactBytes, "Invalid artifact revision or byte length.");
        Require(reference.MediaType is "image/png" or "image/jpeg" or "model/gltf-binary" or "application/json", "Unsupported artifact media type.");
        Digest(reference.Sha256);
    }

    public static void Validate(ModelOperationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Require(options.PipelineType is null || Enum.IsDefined(options.PipelineType.Value), "Unknown pipeline type.");
        Require(options.Resolution is null or 512 or 1024 or 1536, "Retexture resolution must be 512, 1024 or 1536.");
        Require(options.NumSamples is null or (>= 1 and <= 4), "Sample count must be 1..4.");
        Require(options.MaxNumTokens is null or (>= 1 and <= 49152), "Token ceiling must be 1..49152.");
        Require(options.Seed is null or >= 0, "Seed must be nonnegative.");
        Require(options.TextureResolution is null || TextureResolutions.Contains(options.TextureResolution.Value), "Texture resolution must be 1024, 2048, 3072 or 4096.");
        foreach (var sampler in new[] { options.SparseStructure, options.Shape, options.Texture })
        {
            if (sampler is null) continue;
            Require(sampler.Steps is null or (>= 1 and <= 50), "Sampler steps must be 1..50.");
            Range(sampler.GuidanceStrength, 1, 10, "guidance strength"); Range(sampler.GuidanceRescale, 0, 1, "guidance rescale");
            Range(sampler.TimeRescale, 1, 6, "time rescale");
            if (sampler.GuidanceInterval is { } interval)
            {
                Range(interval.Start, 0, 1, "guidance interval start"); Range(interval.End, 0, 1, "guidance interval end");
                Require(interval.Start <= interval.End, "Guidance interval start exceeds end.");
            }
        }
        if (options.Export is not null) Validate(options.Export);
    }

    public static void Validate(ModelOperationExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Require(options.DecimationTarget is null or (>= 100000 and <= 1000000), "Decimation target must be 100000..1000000.");
        Require(options.RemeshBand is null or 1, "Only the fixed remesh band 1 is admitted by this contract.");
        Range(options.RemeshProjection, 0, 1, "remesh projection");
    }

    public static void Validate(ModelOperationRuntimeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor); Id(descriptor.RuntimeId); Id(descriptor.RuntimeVersion);
        var capabilities = Bounded(descriptor.Capabilities, 1, 2);
        Require(capabilities.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == capabilities.Length, "Duplicate capability.");
        foreach (var capability in capabilities)
        {
            Operation(capability.Id); Require(capability.ContractVersion == Version, "Unsupported capability contract.");
            foreach (var media in Bounded(capability.InputMediaTypes, 1, 3))
                Require(media is "image/png" or "image/jpeg" or "model/gltf-binary", "Unsupported input media.");
            foreach (var profile in Bounded(capability.OutputMediaProfiles, 1, 16)) ProfileName(profile);
        }
        var profiles = Bounded(descriptor.Profiles, 1, 64);
        Require(profiles.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == profiles.Length, "Duplicate runtime profile.");
        foreach (var profile in profiles)
        {
            Validate(profile);
            Require(profile.Operations.All(id => capabilities.Any(item => item.Id == id)), "Profile has an undeclared operation.");
        }
    }

    public static void Validate(ModelOperationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile); Id(profile.Id); Id(profile.Platform); Id(profile.Device); Digest(profile.Sha256);
        Require(profile.Mode is "native" or "container", "Runtime profile must be native or container.");
        var operations = Bounded(profile.Operations, 1, 2);
        Require(operations.Distinct(StringComparer.Ordinal).Count() == operations.Length, "Duplicate profile operation.");
        foreach (var operation in operations) Operation(operation);
    }

    private static void Inputs(string operation, IReadOnlyList<ModelOperationInput> inputs)
    {
        var items = Bounded(inputs, operation == ImageTo3D ? 1 : 2, operation == ImageTo3D ? 1 : 2);
        foreach (var input in items)
        {
            Require(input is not null, "Null input."); Validate(input.Artifact);
            if (input.Role is "image" or "appearance") Require(input.Artifact.MediaType is "image/png" or "image/jpeg", "Image role requires PNG/JPEG.");
            else if (input.Role == "mesh") Require(input.Artifact.MediaType == "model/gltf-binary", "Mesh role requires GLB.");
            else throw Invalid("Unsupported input role.");
        }
        Require(operation == ImageTo3D ? items[0].Role == "image" :
            items.Count(item => item.Role == "mesh") == 1 && items.Count(item => item.Role == "appearance") == 1,
            "Operation input roles do not match its contract.");
    }

    private static T[] Bounded<T>(IReadOnlyList<T> items, int minimum, int maximum)
    {
        ArgumentNullException.ThrowIfNull(items);
        Require(items.Count >= minimum && items.Count <= maximum, "Collection exceeds model operation bounds.");
        var copy = items.Take(maximum + 1).ToArray();
        Require(copy.Length == items.Count && copy.Length <= maximum && copy.All(item => item is not null), "Invalid collection snapshot.");
        return copy;
    }
    private static void Header(int version, string request, string job, string effect)
    { Require(version == Version, "Unsupported model operation version."); Id(request); Id(job); Id(effect); }
    private static void Operation(string operation) => Require(Operations.Contains(operation), "Unsupported model operation.");
    private static void Error(ModelOperationError? error) { if (error is not null) { Id(error.Code); Text(error.Message, 1024); } }
    private static void Range(double? value, double minimum, double maximum, string field)
        => Require(value is null || double.IsFinite(value.Value) && value >= minimum && value <= maximum, "Invalid " + field + ".");
    private static void Text(string? value, int maximum, bool optional = false)
        => Require(value is null ? optional : value.Length <= maximum && !value.Contains('\0'), "Text exceeds its bound.");
    private static void Id(string? value, int maximum = 128)
        => Require(value is not null && value.Length <= maximum && Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9._+-]*\\z", RegexOptions.CultureInvariant), "Invalid model operation identifier.");
    private static void ProfileName(string? value)
        => Require(value is not null && value.Length <= 128 && Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9._/-]*\\z", RegexOptions.CultureInvariant), "Invalid media profile identifier.");
    private static void Digest(string? value)
        => Require(value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'), "Invalid lowercase SHA-256.");
    private static void Require([DoesNotReturnIf(false)] bool value, string message) { if (!value) throw Invalid(message); }
    private static InvalidDataException Invalid(string message) => new(message);
}
