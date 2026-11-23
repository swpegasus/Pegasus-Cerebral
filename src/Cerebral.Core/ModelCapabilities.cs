namespace Cerebral.Core;

public static class ModelCapabilityIds
{
    public const string TextGeneration = "text-generation";
    public const string ImageInput = "image-input";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        TextGeneration,
        ImageInput
    };

    public static string DisplayName(string id) => id switch
    {
        TextGeneration => "Text generation",
        ImageInput => "Image input",
        _ => id
    };
}

public static class ModelCapabilityStates
{
    public const string Supported = "supported";
    public const string Potential = "potential";
    public const string Unknown = "unknown";
    public const string Available = "available";
    public const string Missing = "missing";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Separates what a model family may support from the installed payload and the paths Cerebral has verified.
/// A capability is assignable only when <see cref="Usable"/> is true.
/// </summary>
public sealed class ModelCapabilityStatus
{
    public string Id { get; set; } = "";
    public string FamilyStatus { get; set; } = ModelCapabilityStates.Unknown;
    public string PayloadStatus { get; set; } = ModelCapabilityStates.Missing;
    public string RuntimeStatus { get; set; } = ModelCapabilityStates.Unavailable;
    public bool Usable { get; set; }
    public string EvidenceBasis { get; set; } = "explicit";
    public string Evidence { get; set; } = "";
}

public static class ModelCapabilityPolicy
{
    private static readonly string[] NonTextTypes = ["adapter", "projector", "vision projector", "embedding", "reranking", "audio", "vision"];
    private sealed record PayloadProbe(string Status, string Evidence);

    public static IReadOnlyList<ModelCapabilityStatus> Assess(ModelRecord model, IReadOnlySet<string>? verifiedRuntimeCapabilities = null)
    {
        var declaredType = model.DeclaredType?.Trim();
        var installedPayload = InspectPayload(model);
        // Purpose may be a filename or descriptive estimate. Only the declared GGUF type can
        // disqualify a registered payload from the established text path.
        var textPayload = !NonTextTypes.Contains(declaredType ?? "", StringComparer.OrdinalIgnoreCase);
        var textPayloadStatus = textPayload ? installedPayload.Status : ModelCapabilityStates.Missing;
        var textUsable = textPayload && textPayloadStatus == ModelCapabilityStates.Available;
        var architectureFromGguf = model.MetadataSource.StartsWith("gguf", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(model.Architecture) && !model.Architecture.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        var qwen35Family = architectureFromGguf && model.Architecture.Equals("qwen35", StringComparison.OrdinalIgnoreCase);
        var projector = InspectProjector(model.VisionProjectorPath);
        // Only a host-owned live runtime assessment may supply this argument. Persisted model
        // capability fields are never consumed as evidence.
        var imageVerified = textUsable && projector.Status == ModelCapabilityStates.Available &&
            verifiedRuntimeCapabilities?.Contains(ModelCapabilityIds.ImageInput) == true;

        return
        [
            new ModelCapabilityStatus
            {
                Id = ModelCapabilityIds.TextGeneration,
                FamilyStatus = architectureFromGguf ? ModelCapabilityStates.Supported : ModelCapabilityStates.Unknown,
                PayloadStatus = textPayloadStatus,
                RuntimeStatus = ModelCapabilityStates.Available,
                Usable = textUsable,
                EvidenceBasis = textUsable ? architectureFromGguf ? "explicit" : "legacy-default" : "filesystem",
                Evidence = !textPayload
                    ? $"GGUF metadata declares type '{declaredType}' rather than a text-generation model payload. Cerebral's text message path is implemented."
                    : textPayloadStatus != ModelCapabilityStates.Available
                        ? installedPayload.Evidence + " Cerebral's text message path is implemented, but it cannot run without a readable GGUF payload."
                        : installedPayload.Evidence + " " + (architectureFromGguf
                            ? $"GGUF metadata declares general.architecture={model.Architecture}; Cerebral's text message path is implemented."
                            : "Legacy model records remain text-capable unless GGUF metadata explicitly declares a non-text payload; Cerebral's text message path is implemented.")
            },
            new ModelCapabilityStatus
            {
                Id = ModelCapabilityIds.ImageInput,
                FamilyStatus = qwen35Family ? ModelCapabilityStates.Potential : ModelCapabilityStates.Unknown,
                PayloadStatus = projector.Status,
                RuntimeStatus = imageVerified ? ModelCapabilityStates.Available : ModelCapabilityStates.Unknown,
                Usable = imageVerified,
                EvidenceBasis = imageVerified ? "verified-live-native-instance" : "filesystem-and-runtime-unverified",
                Evidence = imageVerified
                    ? "The current native runtime processed the host image probe with matching exact input counts and a complete correct result. Unload retires this proof; the configured role is retained and verified again on its next use."
                    : projector.Evidence + " Cerebral's native image path is implemented. A matching loaded model/projector must pass native runtime verification before an image-required role runs. Configured paths and family metadata do not establish capability."
            }
        ];
    }

    private static PayloadProbe InspectProjector(string? value)
    {
        if (value is null) return new(ModelCapabilityStates.Missing, "No image projector is configured.");
        try
        {
            var path = ModelComponentConfiguration.NormalizePath(value)!;
            var component = InspectPayload(new ModelRecord { Path = path });
            return new(component.Status, component.Status == ModelCapabilityStates.Available
                ? "The configured image projector has a readable GGUF header."
                : "The configured image projector is missing, invalid or unreadable.");
        }
        catch (ArgumentException) { return new(ModelCapabilityStates.Unavailable, "The configured image projector path is invalid."); }
    }

    private static PayloadProbe InspectPayload(ModelRecord model)
    {
        if (string.IsNullOrWhiteSpace(model.Path))
            return new(ModelCapabilityStates.Missing, "No installed model path is registered.");
        string path;
        try { path = Path.GetFullPath(model.Path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(ModelCapabilityStates.Unavailable, "The registered model path is invalid and cannot be opened.");
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[4];
            if (stream.Read(magic) != magic.Length || !magic.SequenceEqual("GGUF"u8))
                return new(ModelCapabilityStates.Unavailable, "The registered payload is readable, but it does not contain a valid GGUF header.");
            return new(ModelCapabilityStates.Available, "The registered GGUF payload exists and its header is readable.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(ModelCapabilityStates.Missing, "The registered GGUF payload is missing from its installed path.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(ModelCapabilityStates.Unavailable, "The registered GGUF payload exists but cannot currently be opened for reading.");
        }
    }

    public static void Refresh(ModelRecord model) => model.CapabilityStatus = [.. Assess(model)];

    public static IReadOnlyList<string> Requirements(AgentProfile profile)
    {
        var requirements = new List<string> { ModelCapabilityIds.TextGeneration };
        foreach (var capability in profile.RequiredModelCapabilities ?? [])
        {
            var normalized = capability.Trim().ToLowerInvariant();
            if (normalized.Length > 0 && !requirements.Contains(normalized, StringComparer.Ordinal)) requirements.Add(normalized);
        }
        return requirements;
    }

    public static void ValidateRequirements(AgentProfile profile)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in profile.RequiredModelCapabilities ?? [])
        {
            var capability = raw.Trim().ToLowerInvariant();
            if (capability.Length == 0 || !ModelCapabilityIds.Supported.Contains(capability))
                throw new ArgumentException($"Unknown required model capability '{raw}'. Supported values are {string.Join(", ", ModelCapabilityIds.Supported)}.");
            if (!seen.Add(capability)) throw new ArgumentException($"Required model capability '{capability}' is listed more than once.");
        }
    }

    /// <summary>Validate durable configuration without treating an idle runtime as a broken role.</summary>
    public static void EnsureConfigured(AgentProfile profile, ModelRecord? model) =>
        EnsureCompatibleCore(profile, model, null, configurationOnly: true);

    public static void EnsureCompatible(AgentProfile profile, ModelRecord? model,
        IReadOnlySet<string>? verifiedRuntimeCapabilities = null) =>
        EnsureCompatibleCore(profile, model, verifiedRuntimeCapabilities, configurationOnly: false);

    private static void EnsureCompatibleCore(AgentProfile profile, ModelRecord? model,
        IReadOnlySet<string>? verifiedRuntimeCapabilities, bool configurationOnly)
    {
        ValidateRequirements(profile);
        var requirements = Requirements(profile);
        if (model is null)
        {
            var unverifiable = requirements.Where(x => x != ModelCapabilityIds.TextGeneration).ToList();
            if (unverifiable.Count > 0)
                throw new InvalidOperationException($"Agent '{profile.Name}' requires {string.Join(", ", unverifiable.Select(ModelCapabilityIds.DisplayName))}, but the selected model's capabilities are not available to this service for verification.");
            return;
        }

        var statuses = Assess(model, verifiedRuntimeCapabilities);
        model.CapabilityStatus = [.. statuses];
        foreach (var requirement in requirements)
        {
            if (configurationOnly && requirement == ModelCapabilityIds.ImageInput) continue;
            var status = statuses.FirstOrDefault(x => x.Id == requirement);
            if (status?.Usable == true) continue;
            var detail = status is null
                ? "No capability evidence is recorded."
                : $"Family: {status.FamilyStatus}; payload: {status.PayloadStatus}; Cerebral runtime path: {status.RuntimeStatus}. {status.Evidence}";
            throw new InvalidOperationException($"Agent '{profile.Name}' requires {ModelCapabilityIds.DisplayName(requirement)}, but model '{model.Name}' cannot currently provide it. {detail}");
        }
    }
}
