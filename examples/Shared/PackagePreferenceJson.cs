using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.PackageSettings;

/// <summary>Closed package schemas and bounded pure JSON parsing; this helper owns no fields or storage.</summary>
internal static class PackagePreferenceJson
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, MaxDepth = 8
    };
    internal static readonly JsonSerializerOptions LegacyJson = new(Json)
    { PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true };
    internal static void Shape(JsonElement raw, int maximum, params string[] names)
    {
        Bound(raw, maximum);
        var properties = raw.EnumerateObject().ToArray();
        if (properties.Length != names.Length ||
            !properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names))
            throw Unsupported();
    }
    internal static void Bound(JsonElement raw, int maximum)
    {
        if (raw.ValueKind != JsonValueKind.Object || new UTF8Encoding(false, true).GetByteCount(raw.GetRawText()) > maximum)
            throw Unsupported();
        Unique(raw, StringComparer.Ordinal);
    }
    private static void Unique(JsonElement raw, StringComparer comparer)
    {
        if (raw.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(comparer);
            foreach (var item in raw.EnumerateObject())
            { if (!seen.Add(item.Name)) throw Unsupported(); Unique(item.Value, comparer); }
        }
        else if (raw.ValueKind == JsonValueKind.Array) foreach (var value in raw.EnumerateArray()) Unique(value, comparer);
    }
    internal static T Legacy<T>(byte[] bytes, int maximum) where T : class
    {
        if (bytes.Length > maximum) throw Unsupported();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Unsupported();
        Unique(document.RootElement, StringComparer.OrdinalIgnoreCase);
        return document.RootElement.Deserialize<T>(LegacyJson) ?? throw Unsupported();
    }
    internal static void Document(PluginSettingsDocument value, string id)
    {
        if (value.PluginId != id || value.Scope != "global" || value.ScopeId is not null ||
            value.SettingsVersion != 1 || !PluginVersion.TryParse(value.SourcePackageVersion, out _)) throw Unsupported();
    }
    internal static byte[] ReadLegacy(string path, int maximum)
    {
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw Unsupported();
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        { if (output.Length + count > maximum) throw Unsupported(); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    internal static void Text(string? value, int maximum, bool nullable = false)
    {
        if (value is null) { if (!nullable) throw Unsupported(); return; }
        if (value.Length > maximum || value.Any(char.IsControl)) throw Unsupported();
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw Unsupported(); }
    }
    internal static InvalidDataException Unsupported() => new("The saved preference format is unsupported. Existing settings are preserved.");
}
