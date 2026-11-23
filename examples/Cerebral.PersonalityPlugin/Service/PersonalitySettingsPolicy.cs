using System.Globalization;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Current editable preferences only; retained source versions and learning indexes are not settings.</summary>
public sealed record PersonalityCustomDefinition(string Id, string Name, string Text);

public sealed record PersonalitySettingsPreferences(bool Enabled, string SelectedPresetId,
    IReadOnlyList<PersonalityCustomDefinition> CustomPresets);

/// <summary>Pure package codec shared by settings admission and explicit source preparation.</summary>
public static class PersonalitySettingsCodec
{
    public const int MaximumValuesUtf8Bytes = 32 * 1024;
    public const int MaximumDepth = 4;

    public static PersonalitySettingsPreferences Read(JsonElement values)
    {
        Boundary(values);
        Fields(values, ["enabled", "selectedPresetId", "customPresets"]);
        var enabled = Boolean(values.GetProperty("enabled"));
        var selected = Identifier(values.GetProperty("selectedPresetId"));
        var items = values.GetProperty("customPresets");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > PersonalityContract.MaximumCustomPresets)
            throw Invalid();
        var definitions = new List<PersonalityCustomDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var definition = ReadDefinition(item);
            if (!ids.Add(definition.Id)) throw Invalid();
            definitions.Add(definition);
        }
        // A desired custom ID may be absent: preparation reports unavailable, never a substitute.
        var result = new PersonalitySettingsPreferences(enabled, selected, Array.AsReadOnly(definitions.ToArray()));
        _ = Write(result);
        return result;
    }

    public static JsonElement Write(PersonalitySettingsPreferences preferences)
    {
        if (preferences is null || !PersonalityContract.ValidId(preferences.SelectedPresetId) ||
            preferences.CustomPresets is null || preferences.CustomPresets.Count > PersonalityContract.MaximumCustomPresets)
            throw Invalid();
        var definitions = preferences.CustomPresets.Take(PersonalityContract.MaximumCustomPresets + 1).ToArray();
        if (definitions.Length != preferences.CustomPresets.Count ||
            definitions.Select(item => item?.Id).Distinct(StringComparer.Ordinal).Count() != definitions.Length)
            throw Invalid();
        foreach (var definition in definitions) ValidateDefinition(definition!);
        var value = PersonalityContract.Element(new
        {
            enabled = preferences.Enabled,
            selectedPresetId = preferences.SelectedPresetId,
            customPresets = definitions.Select(item => new { id = item.Id, name = item.Name, text = item.Text }).ToArray()
        });
        Boundary(value);
        return value;
    }

    public static PersonalityCustomDefinition ReadDefinition(JsonElement value)
    {
        Boundary(value);
        Fields(value, ["id", "name", "text"]);
        var result = new PersonalityCustomDefinition(Identifier(value.GetProperty("id")),
            Text(value.GetProperty("name")), Text(value.GetProperty("text")));
        ValidateDefinition(result);
        return result;
    }

    public static void ValidateDefinition(PersonalityCustomDefinition definition)
    {
        if (definition is null || !PersonalityContract.ValidId(definition.Id) || IsBuiltIn(definition.Id) ||
            definition.Name is null || definition.Name.Length > 80 ||
            definition.Text is null || definition.Text.Length > PersonalityContract.MaximumPresetUtf8Bytes)
            throw Invalid();
        // This preserves the stored codec's 80 UTF-16 / 320 UTF-8 name allowance without truncation.
        PersonalityContract.ValidatePreset(PersonalityContract.Preset(definition.Id, definition.Name, 1, definition.Text));
    }

    internal static bool IsBuiltIn(string id) => PersonalityContract.BuiltIns.Any(item => item.Id == id);

    internal static (bool Enabled, string SelectedPresetId) ReadPair(JsonElement value)
    {
        Boundary(value);
        Fields(value, ["enabled", "selectedPresetId"]);
        return (Boolean(value.GetProperty("enabled")), Identifier(value.GetProperty("selectedPresetId")));
    }

    internal static void Boundary(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var raw = value.GetRawText();
        if (PersonalityContract.Utf8.GetByteCount(raw) > MaximumValuesUtf8Bytes) throw Invalid();
        using var parsed = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = MaximumDepth });
    }

    internal static void Fields(JsonElement value, IReadOnlyList<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var actual = value.EnumerateObject().Select(item => item.Name).ToArray();
        if (actual.Length != fields.Count || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            fields.Any(field => !actual.Contains(field, StringComparer.Ordinal))) throw Invalid();
    }

    private static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Invalid()
    };

    private static string Identifier(JsonElement value)
    {
        var result = Text(value);
        return PersonalityContract.ValidId(result) ? result : throw Invalid();
    }

    internal static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        // JsonElement string extraction must not normalize a lone escaped surrogate into a new value.
        var raw = value.GetRawText();
        for (var index = 1; index < raw.Length - 1; index++)
        {
            if (raw[index] != '\\') continue;
            index++;
            if (raw[index] != 'u') continue;
            var code = (char)int.Parse(raw.AsSpan(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (char.IsHighSurrogate(code))
            {
                var next = index + 5;
                if (next + 5 >= raw.Length - 1 || raw[next] != '\\' || raw[next + 1] != 'u' ||
                    !char.IsLowSurrogate((char)int.Parse(raw.AsSpan(next + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
                    throw Invalid();
                index = next + 5;
            }
            else
            {
                if (char.IsLowSurrogate(code)) throw Invalid();
                index += 4;
            }
        }
        var result = value.GetString() ?? throw Invalid();
        _ = PersonalityContract.Utf8.GetByteCount(result);
        return result;
    }

    private static InvalidDataException Invalid() => new("Personality preference values are invalid or exceed their bounds.");
}

/// <summary>Pure schema admission/projection; this never prepares sources or changes legacy data.</summary>
public sealed class PersonalitySettingsPolicy : IPluginCompleteSettingsPolicy
{
    public const string PackageVersion = "1.2.3";
    public const int SettingsVersion = 2;

    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        if (!portable) return AdmitComplete(incoming, currentValues);
        try
        {
            RequireDocument(incoming);
            PersonalitySettingsCodec.Boundary(incoming.Values);
            var count = incoming.Values.EnumerateObject().Count();
            if (count == 0)
            {
                if (currentValues is null) return Reject("portable-destination-unavailable");
                return Accept(PersonalitySettingsCodec.Read(currentValues.Value));
            }
            var pair = PersonalitySettingsCodec.ReadPair(incoming.Values);
            if (!PersonalitySettingsCodec.IsBuiltIn(pair.SelectedPresetId)) return Reject("unsupported-portable-personality-selection");
            var definitions = currentValues is { } current
                ? PersonalitySettingsCodec.Read(current).CustomPresets
                : Array.AsReadOnly(Array.Empty<PersonalityCustomDefinition>());
            return Accept(new(pair.Enabled, pair.SelectedPresetId, definitions));
        }
        catch (Exception error) when (InvalidInput(error)) { return Reject("invalid-personality-settings"); }
    }

    public PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues)
    {
        try
        {
            // Replacement must not silently overwrite a malformed or unsupported active destination.
            // Its version is not supplied here, so only the current schema-two value shape is admissible.
            if (currentValues is { } current) _ = PersonalitySettingsCodec.Read(current);
            return Accept(ReadDocument(incoming));
        }
        catch (Exception error) when (InvalidInput(error)) { return Reject("invalid-personality-settings"); }
    }

    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    {
        var values = ReadDocument(current);
        return PersonalitySettingsCodec.IsBuiltIn(values.SelectedPresetId)
            ? new(SettingsVersion, PersonalityContract.Element(new { enabled = values.Enabled, selectedPresetId = values.SelectedPresetId }), 1)
            : new(SettingsVersion, PersonalityContract.Element(new { }), 3);
    }

    public PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current) =>
        new(SettingsVersion, PersonalitySettingsCodec.Write(ReadDocument(current)), ExcludedFields: 0, Complete: true);

    private static PersonalitySettingsPreferences ReadDocument(PluginSettingsDocument document)
    {
        RequireDocument(document);
        if (document.SettingsVersion == SettingsVersion) return PersonalitySettingsCodec.Read(document.Values);
        // Only an explicitly versioned schema-one input has the old pair shape. No implicit conversion.
        var pair = PersonalitySettingsCodec.ReadPair(document.Values);
        return new(pair.Enabled, pair.SelectedPresetId, Array.AsReadOnly(Array.Empty<PersonalityCustomDefinition>()));
    }

    private static void RequireDocument(PluginSettingsDocument document)
    {
        if (document is null || document.PluginId != PersonalityContract.PluginId ||
            document.Scope != SettingsPreferenceContract.GlobalScope || document.ScopeId is not null ||
            document.SettingsVersion is not (1 or SettingsVersion))
            throw new InvalidDataException("The personality settings coordinate or schema is unsupported.");
    }

    private static PluginSettingsAdmission Accept(PersonalitySettingsPreferences preferences) =>
        new(true, SettingsVersion, PersonalitySettingsCodec.Write(preferences));

    private static PluginSettingsAdmission Reject(string reason) =>
        new(false, SettingsVersion, PersonalityContract.Element(new { }), reason);

    private static bool InvalidInput(Exception error) => error is ArgumentException or InvalidDataException or
        InvalidOperationException or JsonException or FormatException or OverflowException;
}
