using System.IO;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.PackageSettings;

namespace Cerebral.DictationPlugin;

/// <summary>Windows speech preferences only. Recognizer identity and free-form text remain local.</summary>
internal sealed class DictationSettingsPolicy : IPluginCompleteSettingsPolicy
{
    internal const string PluginId = "pegasus.dictation";
    internal const string PackageVersion = "1.0.8";
    internal const int MaximumBytes = 128 * 1024;
    private static readonly string[] Fields = ["schemaVersion", "recognizerId", "minimumConfidence", "voiceControlsEnabled", "vocabulary", "corrections"];
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        try
        {
            PackagePreferenceJson.Document(incoming, PluginId);
            var current = currentValues is { } raw ? Decode(raw) : new DictationPreferences();
            DictationPreferences value;
            if (portable)
            {
                PackagePreferenceJson.Shape(incoming.Values, MaximumBytes, "schemaVersion", "minimumConfidence", "voiceControlsEnabled");
                var safe = incoming.Values.Deserialize<DictationPreferences>(PackagePreferenceJson.Json) ?? throw PackagePreferenceJson.Unsupported();
                Validate(safe);
                value = Copy(current);
                value.MinimumConfidence = safe.MinimumConfidence; value.VoiceControlsEnabled = safe.VoiceControlsEnabled;
            }
            else value = Decode(incoming.Values);
            return new(true, 1, Encode(value));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { return new(false, 1, JsonSerializer.SerializeToElement(new { }), "dictation-settings-unsupported"); }
    }
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    {
        PackagePreferenceJson.Document(current, PluginId);
        var value = Decode(current.Values);
        return new(1, JsonSerializer.SerializeToElement(new { schemaVersion = 1, minimumConfidence = value.MinimumConfidence,
            voiceControlsEnabled = value.VoiceControlsEnabled }), 3);
    }
    public PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current)
    {
        PackagePreferenceJson.Document(current, PluginId);
        // All schema-one preference fields are included. Audio/profile/domain state is outside this document.
        return new(1, Encode(Decode(current.Values)));
    }
    public PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues) =>
        Admit(incoming, currentValues, portable: false);

    internal static DictationPreferences Decode(JsonElement raw)
    {
        PackagePreferenceJson.Shape(raw, MaximumBytes, Fields);
        foreach (var pair in raw.GetProperty("corrections").EnumerateArray())
            PackagePreferenceJson.Shape(pair, MaximumBytes, "heard", "replacement");
        var value = raw.Deserialize<DictationPreferences>(PackagePreferenceJson.Json) ?? throw PackagePreferenceJson.Unsupported();
        Validate(value); return Copy(value);
    }
    internal static JsonElement Encode(DictationPreferences value)
    {
        Validate(value);
        var raw = JsonSerializer.SerializeToElement(Copy(value), PackagePreferenceJson.Json);
        PackagePreferenceJson.Bound(raw, MaximumBytes); return raw;
    }
    internal static PluginSettingsDocument Document(DictationPreferences value) => new(PluginId, "global", null, PackageVersion, 1, Encode(value));
    internal static DictationPreferences PrepareLegacy(byte[] bytes)
    {
        var value = bytes.Length == 0 ? new() : PackagePreferenceJson.Legacy<DictationPreferences>(bytes, MaximumBytes);
        Validate(value); _ = Encode(value); return Copy(value);
    }
    internal static DictationPreferences Copy(DictationPreferences value) => new()
    {
        RecognizerId = value.RecognizerId, MinimumConfidence = value.MinimumConfidence, VoiceControlsEnabled = value.VoiceControlsEnabled,
        Vocabulary = [.. value.Vocabulary], Corrections = value.Corrections.Select(p => new CorrectionPair { Heard = p.Heard, Replacement = p.Replacement }).ToList()
    };
    private static void Validate(DictationPreferences value)
    {
        if (value.SchemaVersion != 1 || !double.IsFinite(value.MinimumConfidence) || value.MinimumConfidence is < .20 or > .90 ||
            value.Vocabulary is null || value.Corrections is null || value.Vocabulary.Count > 250 || value.Corrections.Count > 250) throw PackagePreferenceJson.Unsupported();
        Text(value.RecognizerId, 512, true);
        foreach (var word in value.Vocabulary) Text(word, 100, false);
        foreach (var pair in value.Corrections)
        { if (pair is null) throw PackagePreferenceJson.Unsupported(); Text(pair.Heard, 100, false); Text(pair.Replacement, 200, false); }
    }
    private static void Text(string? text, int maximum, bool nullable)
        => PackagePreferenceJson.Text(text, maximum, nullable);
}
