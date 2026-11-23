using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.ChatIconsPlugin;

internal sealed record ChatIconPreferences(IReadOnlyDictionary<string, string> Roles,
    IReadOnlyDictionary<string, string> Meanings);

/// <summary>Pure package codec. Presentation choices never authorize execution or install assets.</summary>
internal sealed class ChatIconsSettingsPolicy : IPluginCompleteSettingsPolicy
{
    internal const int SettingsVersion = 1;
    internal const string PackageVersion = "1.0.3";
    internal const int MaximumValuesBytes = 16 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static ChatIconPreferences Defaults => new(RoleSettings.Defaults, SemanticIconSettings.Defaults);

    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        try
        {
            RequireDocument(incoming);
            var decoded = Decode(incoming.Values);
            if (currentValues is { } current) _ = Decode(current);
            return new(true, SettingsVersion, Encode(decoded));
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
        { return new(false, SettingsVersion, JsonSerializer.SerializeToElement(new { }), "chat-icons-settings-unsupported"); }
    }

    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    {
        RequireDocument(current);
        // Both closed maps consist solely of declared nonsecret built-in asset identities.
        return new(SettingsVersion, Encode(Decode(current.Values)));
    }

    public PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current)
    {
        // Both closed schema-one maps are already entirely declared nonsecret glyph identities.
        var projected = ExportPortable(current);
        return new(SettingsVersion, projected.Values);
    }
    public PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues) =>
        Admit(incoming, currentValues, portable: false);

    internal static ChatIconPreferences Decode(JsonElement values)
    {
        try
        {
            if (values.ValueKind != JsonValueKind.Object || Utf8.GetByteCount(values.GetRawText()) > MaximumValuesBytes)
                throw Unsupported();
            var properties = values.EnumerateObject().ToArray();
            if (properties.Length != 2 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
                !values.TryGetProperty("roles", out var roles) || !values.TryGetProperty("meanings", out var meanings))
                throw Unsupported();
            var result = new ChatIconPreferences(Map(roles), Map(meanings));
            RoleSettings.Validate(result.Roles); SemanticIconSettings.Validate(result.Meanings);
            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { throw Unsupported(); }
    }

    internal static JsonElement Encode(ChatIconPreferences value)
    {
        RoleSettings.Validate(value.Roles); SemanticIconSettings.Validate(value.Meanings);
        var json = JsonSerializer.SerializeToElement(new { roles = value.Roles, meanings = value.Meanings });
        if (Utf8.GetByteCount(json.GetRawText()) > MaximumValuesBytes) throw Unsupported();
        return json;
    }

    internal static PluginSettingsDocument Document(ChatIconPreferences value) =>
        new(ChatIconsPlugin.PluginId, "global", null, PackageVersion, SettingsVersion, Encode(value));

    /// <summary>Supplied legacy text is decoded only. Callers explicitly choose when to read or save.</summary>
    internal static ChatIconPreferences PrepareLegacy(string? rolesIni, string? meaningsIni) =>
        new(rolesIni is null ? RoleSettings.Defaults : RoleSettings.ParseLegacy(rolesIni),
            meaningsIni is null ? SemanticIconSettings.Defaults : SemanticIconSettings.ParseLegacy(meaningsIni));

    private static IReadOnlyDictionary<string, string> Map(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw Unsupported();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in raw.EnumerateObject())
            if (item.Value.ValueKind != JsonValueKind.String || !values.TryAdd(item.Name, item.Value.GetString()!))
                throw Unsupported();
        return new ReadOnlyDictionary<string, string>(values);
    }

    private static void RequireDocument(PluginSettingsDocument value)
    {
        if (value.PluginId != ChatIconsPlugin.PluginId || value.Scope != "global" || value.ScopeId is not null ||
            value.SettingsVersion != SettingsVersion || value.SourcePackageVersion is null ||
            !PluginVersion.TryParse(value.SourcePackageVersion, out _)) throw Unsupported();
    }
    private static InvalidDataException Unsupported() => new("These icon settings use an unsupported format or asset identity. The saved document is preserved.");
}
