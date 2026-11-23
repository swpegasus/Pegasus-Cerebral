using System.IO;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.PackageSettings;

namespace Cerebral.AiDictationPlugin;

/// <summary>Pure preference codec. Local paths, device identity and free-form provenance are never portable.</summary>
internal sealed class AiDictationSettingsPolicy : IPluginCompleteSettingsPolicy
{
    internal const string PluginId = "pegasus.ai-dictation";
    internal const string PackageVersion = "1.2.8";
    internal const int MaximumBytes = 16 * 1024;
    private static readonly string[] Fields = ["schemaVersion", "runtimePath", "modelPath", "modelId", "modelRevision", "modelSource",
        "modelLicense", "runtimeSource", "runtimeLicense", "inputDeviceName", "inputBoostDb", "normalizeQuietAudio",
        "normalizationCalibrationSeconds", "autoTranscribeOnSilence", "liveWordsWhileSpeaking", "silenceWaitSeconds", "dockControls", "allowExternalTarget",
        "grammarCorrection", "grammarProfileId", "grammarProfileRevision"];
    private static readonly string[] PreLiveWordsFields = Fields.Where(name => name != "liveWordsWhileSpeaking").ToArray();
    private static readonly string[] PortableFields = ["schemaVersion", "inputBoostDb", "normalizeQuietAudio",
        "normalizationCalibrationSeconds", "autoTranscribeOnSilence", "silenceWaitSeconds"];
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        try
        {
            PackagePreferenceJson.Document(incoming, PluginId);
            var current = currentValues is { } raw ? Decode(raw) : new AiDictationSettings();
            AiDictationSettings value;
            if (portable)
            {
                PackagePreferenceJson.Shape(incoming.Values, MaximumBytes, PortableFields);
                var safe = incoming.Values.Deserialize<AiDictationSettings>(PackagePreferenceJson.Json) ?? throw PackagePreferenceJson.Unsupported();
                Validate(safe);
                value = current with { InputBoostDb = safe.InputBoostDb, NormalizeQuietAudio = safe.NormalizeQuietAudio,
                    NormalizationCalibrationSeconds = safe.NormalizationCalibrationSeconds,
                    AutoTranscribeOnSilence = safe.AutoTranscribeOnSilence, SilenceWaitSeconds = safe.SilenceWaitSeconds };
            }
            else value = Decode(incoming.Values);
            return new(true, 1, Encode(value));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { return new(false, 1, JsonSerializer.SerializeToElement(new { }), "ai-dictation-settings-unsupported"); }
    }
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    {
        PackagePreferenceJson.Document(current, PluginId);
        var value = Decode(current.Values);
        return new(1, JsonSerializer.SerializeToElement(new { schemaVersion = 1, inputBoostDb = value.InputBoostDb,
            normalizeQuietAudio = value.NormalizeQuietAudio, normalizationCalibrationSeconds = value.NormalizationCalibrationSeconds,
            autoTranscribeOnSilence = value.AutoTranscribeOnSilence, silenceWaitSeconds = value.SilenceWaitSeconds }), 9);
    }
    public PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current)
    {
        PackagePreferenceJson.Document(current, PluginId);
        var value = Decode(current.Values);
        var unsupported = UnsupportedCompleteSourceFields(value);
        if (unsupported.Count != 0)
            return new(1, JsonSerializer.SerializeToElement(new { }), Complete: false) { UnsupportedFields = unsupported };
        return new(1, Encode(value));
    }
    public PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues)
    {
        try
        {
            // Ordinary/portable admission remains unchanged. Full transfer additionally protects source references.
            var admitted = Admit(incoming, currentValues, portable: false);
            if (!admitted.Accepted) return admitted;
            var value = Decode(admitted.Values) with { AllowExternalTarget = false, GrammarProfileId = "", GrammarProfileRevision = 0, GrammarCorrection = false };
            if (UnsupportedCompleteSourceFields(value).Count != 0)
                return new(false, 1, JsonSerializer.SerializeToElement(new { }), "ai-dictation-complete-source-reference-unsupported");
            if (currentValues is { } raw)
            {
                var previous = Decode(raw);
                // An existing credential-bearing source remains excluded local state, never an imported replacement.
                value = value with
                {
                    // OS input permission and exact local AI profile choices never arrive from a transfer.
                    AllowExternalTarget = previous.AllowExternalTarget,
                    GrammarProfileId = previous.GrammarProfileId, GrammarProfileRevision = previous.GrammarProfileRevision,
                    GrammarCorrection = previous.GrammarCorrection,
                    ModelSource = CredentialBearingSource(previous.ModelSource) ? previous.ModelSource : value.ModelSource,
                    RuntimeSource = CredentialBearingSource(previous.RuntimeSource) ? previous.RuntimeSource : value.RuntimeSource
                };
            }
            return admitted with { Values = Encode(value) };
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { return new(false, 1, JsonSerializer.SerializeToElement(new { }), "ai-dictation-complete-settings-unsupported"); }
    }
    private static IReadOnlyList<string> UnsupportedCompleteSourceFields(AiDictationSettings value)
    {
        var fields = new List<string>(2);
        if (CredentialBearingSource(value.ModelSource)) fields.Add("modelSource");
        if (CredentialBearingSource(value.RuntimeSource)) fields.Add("runtimeSource");
        return fields;
    }
    private static bool CredentialBearingSource(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.UserInfo.Length != 0 || uri.Query.Length > 1 || uri.Fragment.Length > 1);

    internal static AiDictationSettings Decode(JsonElement raw)
    {
        // Schema 1 before 1.2.3 saved every original field but had no live-word choice.
        // Admit that exact closed shape while preserving all existing values. Missing
        // original fields and unknown fields still fail the normal strict reader.
        var hasLiveWords = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("liveWordsWhileSpeaking", out _);
        PackagePreferenceJson.Shape(raw, MaximumBytes, hasLiveWords ? Fields : PreLiveWordsFields);
        var value = raw.Deserialize<AiDictationSettings>(PackagePreferenceJson.Json) ?? throw PackagePreferenceJson.Unsupported();
        if (!hasLiveWords) value = value with { LiveWordsWhileSpeaking = false };
        Validate(value); return value;
    }
    internal static JsonElement Encode(AiDictationSettings value)
    {
        Validate(value); var raw = JsonSerializer.SerializeToElement(value, PackagePreferenceJson.Json);
        PackagePreferenceJson.Bound(raw, MaximumBytes); return raw;
    }
    internal static PluginSettingsDocument Document(AiDictationSettings value) => new(PluginId, "global", null, PackageVersion, 1, Encode(value));
    internal static AiDictationSettings PrepareLegacy(byte[] bytes)
    {
        var value = bytes.Length == 0 ? new() : PackagePreferenceJson.Legacy<AiDictationSettings>(bytes, MaximumBytes);
        Validate(value); _ = Encode(value); return value;
    }
    internal static void Validate(AiDictationSettings value)
    {
        if (value.SchemaVersion != 1) throw PackagePreferenceJson.Unsupported();
        foreach (var text in new[] { value.RuntimePath, value.ModelPath, value.ModelId, value.ModelRevision, value.ModelSource,
            value.ModelLicense, value.RuntimeSource, value.RuntimeLicense, value.InputDeviceName, value.GrammarProfileId })
            PackagePreferenceJson.Text(text, 2048);
        if (value.GrammarProfileRevision < 0 || value.GrammarProfileId.Length > 128 ||
            value.GrammarProfileId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
            (value.GrammarProfileId.Length == 0) != (value.GrammarProfileRevision == 0)) throw PackagePreferenceJson.Unsupported();
        if (!double.IsFinite(value.InputBoostDb) || value.InputBoostDb is < 0 or > 24 ||
            !double.IsFinite(value.NormalizationCalibrationSeconds) || value.NormalizationCalibrationSeconds is < 5 or > 10 ||
            !double.IsFinite(value.SilenceWaitSeconds) || value.SilenceWaitSeconds is < .5 or > 10) throw PackagePreferenceJson.Unsupported();
    }
}
