using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace Cerebral.Core;

/// <summary>Cross-platform service plugin contract loaded by the Worker from isolated package folders.</summary>
public static class ServicePluginContract
{
    public const string Id = "pegasus.cerebral.service";
    public const int Version = 1;
    public const int MaxManifestBytes = 65_536;
    public const int MaxDependencies = 32;
    public const int MaxPortableSettings = 64;
    public const int MaxRequiredHostFacets = 8;
    public const string ScalarGenerationHostFacetV1 = "cerebral.service.scalar-generation/1";
}

public sealed class ServicePluginManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Contract { get; set; } = ServicePluginContract.Id;
    public int ApiVersion { get; set; } = ServicePluginContract.Version;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    public List<ServicePluginDependency> Dependencies { get; set; } = [];
    /// <summary>Schema 2 only. An absent list preserves legacy admission; each named host facet is required before plugin code loads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RequiredHostFacets { get; set; }
    /// <summary>Schema 2 only. Declared fields are explicitly nonsecret and eligible for portable configuration backup.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServicePluginPortableSettings? PortableSettings { get; set; }
}

public sealed class ServicePluginPortableSettings
{
    public int Version { get; set; } = 1;
    public List<ServicePluginPortableSettingField> Fields { get; set; } = [];
}

public sealed class ServicePluginPortableSettingField
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public int? MaximumUtf8Bytes { get; set; }
    public int? MaximumItems { get; set; }
    public long? Minimum { get; set; }
    public long? Maximum { get; set; }
    public List<string> AllowedValues { get; set; } = [];
}

public sealed record ServicePluginPortableSettingFieldBinding(string Name, string Type, bool Required,
    int? MaximumUtf8Bytes, int? MaximumItems, long? Minimum, long? Maximum,
    IReadOnlyList<string> AllowedValues);
public sealed record ServicePluginPortableSettingsBinding(string PluginId, string PackageVersion, int Version,
    IReadOnlyList<ServicePluginPortableSettingFieldBinding> Fields);

public static class ServicePluginPortableSettingsPolicy
{
    public static ServicePluginPortableSettingsBinding Freeze(string pluginId, string packageVersion,
        ServicePluginPortableSettings declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (!ValidId(pluginId) || !PluginVersion.TryParse(packageVersion, out _))
            throw new InvalidDataException("Portable settings require a valid plugin identity and package version.");
        if (declaration.Version is < 1 or > 1_000_000 || declaration.Fields is null ||
            declaration.Fields.Count is < 1 or > ServicePluginContract.MaxPortableSettings)
            throw new InvalidDataException("Portable settings require a positive version and between 1 and 64 fields.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<ServicePluginPortableSettingFieldBinding>(declaration.Fields.Count);
        foreach (var field in declaration.Fields)
        {
            if (field is null || !ValidId(field.Name) || !names.Add(field.Name) || field.AllowedValues is null)
                throw new InvalidDataException("Portable setting fields require unique bounded identifiers.");
            var allowed = field.AllowedValues.ToArray();
            if (allowed.Length > 64 || allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Length ||
                allowed.Any(value => value is null || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) > 4096))
                throw new InvalidDataException("Portable setting allowed values are invalid or exceed their bounds.");
            switch (field.Type)
            {
                case "boolean":
                    ExactConstraints(field, allowBounds: false, allowText: false, allowItems: false, allowed);
                    break;
                case "integer":
                    ExactConstraints(field, allowBounds: true, allowText: false, allowItems: false, allowed);
                    if (field.Minimum is { } minimum && field.Maximum is { } maximum && minimum > maximum)
                        throw new InvalidDataException("A portable integer setting has an empty range.");
                    break;
                case "string":
                    ExactConstraints(field, allowBounds: false, allowText: true, allowItems: false, allowed);
                    break;
                case "string-list":
                    ExactConstraints(field, allowBounds: false, allowText: true, allowItems: true, allowed);
                    break;
                default:
                    throw new InvalidDataException("Portable setting types are limited to boolean, integer, string, and string-list.");
            }
            if (allowed.Any(value => field.MaximumUtf8Bytes is { } maximum && Encoding.UTF8.GetByteCount(value) > maximum))
                throw new InvalidDataException("A portable setting allowed value exceeds its field bound.");
            fields.Add(new(field.Name, field.Type, field.Required, field.MaximumUtf8Bytes, field.MaximumItems,
                field.Minimum, field.Maximum, Array.AsReadOnly(allowed)));
        }
        return new(pluginId, packageVersion, declaration.Version, Array.AsReadOnly(fields.ToArray()));
    }

    public static void ValidateValue(ServicePluginPortableSettingFieldBinding field, JsonElement value)
    {
        switch (field.Type)
        {
            case "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return;
            case "integer" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number):
                if ((field.Minimum is { } minimum && number < minimum) ||
                    (field.Maximum is { } maximum && number > maximum))
                    throw new ArgumentException("A portable integer setting is outside its declared range.");
                return;
            case "string" when value.ValueKind == JsonValueKind.String:
                ValidateString(field, value.GetString()!); return;
            case "string-list" when value.ValueKind == JsonValueKind.Array:
                var items = value.EnumerateArray().ToArray();
                if (items.Length > field.MaximumItems!.Value || items.Any(item => item.ValueKind != JsonValueKind.String))
                    throw new ArgumentException("A portable string-list setting exceeds its declared shape.");
                foreach (var item in items) ValidateString(field, item.GetString()!);
                return;
            default:
                throw new ArgumentException("A portable plugin setting does not match its declared type.");
        }
    }

    private static void ValidateString(ServicePluginPortableSettingFieldBinding field, string value)
    {
        if (value.Contains('\0') || Encoding.UTF8.GetByteCount(value) > field.MaximumUtf8Bytes!.Value ||
            field.AllowedValues.Count > 0 && !field.AllowedValues.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException("A portable string setting exceeds its declared values or size.");
    }

    private static void ExactConstraints(ServicePluginPortableSettingField field, bool allowBounds, bool allowText,
        bool allowItems, IReadOnlyList<string> allowed)
    {
        if ((!allowBounds && (field.Minimum is not null || field.Maximum is not null)) ||
            (!allowText && (field.MaximumUtf8Bytes is not null || allowed.Count > 0)) ||
            (!allowItems && field.MaximumItems is not null) ||
            (allowText && field.MaximumUtf8Bytes is not (>= 1 and <= 32768)) ||
            (allowItems && field.MaximumItems is not (>= 1 and <= 128)))
            throw new InvalidDataException("A portable setting declaration contains unsupported or missing constraints for its type.");
    }

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

/// <summary>One package identity with either an exact version or a bounded, inclusive/exclusive range.</summary>
public sealed class ServicePluginDependency
{
    public string Id { get; set; } = "";
    public string? ExactVersion { get; set; }
    public string? MinimumVersion { get; set; }
    public string? MaximumVersionExclusive { get; set; }
    public bool Optional { get; set; }
}

/// <summary>Resolved at package admission; no package-private CLR objects cross this boundary.</summary>
public sealed record ServicePluginDependencyBinding(string Id, string? ExactVersion, string? MinimumVersion,
    string? MaximumVersionExclusive, bool Optional, string? ResolvedVersion, bool Available, string? Reason = null);

public sealed record ServicePluginPackageBinding(string Id, string Version, bool Loaded,
    IReadOnlyList<ServicePluginDependencyBinding> Dependencies, string? Error = null, string? Generation = null);

/// <summary>The Worker supplies only a private data root and its normal bounded event channel.</summary>
public interface IServicePluginHost
{
    string PluginId { get; }
    string PackageDirectory { get; }
    string DataDirectory { get; }
    IReadOnlyList<ServicePluginDependencyBinding> Dependencies => [];
    void Publish(string eventType, string? jobId, object? data);
}

/// <summary>Owner-facing structured commands contributed by a service plugin. Agents use IToolExtension instead.</summary>
public interface IServiceCommandExtension
{
    IReadOnlyList<string> Operations { get; }
    Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>A service plugin composes typed tools and owner commands into the existing Worker process.</summary>
public interface IServicePlugin : IDisposable
{
    void Initialize(IServicePluginHost host);
    IReadOnlyList<IToolExtension> ToolExtensions { get; }
    IReadOnlyList<IServiceCommandExtension> CommandExtensions { get; }
}
