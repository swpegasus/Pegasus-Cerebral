using System.IO;
using System.Text.Json;
using Cerebral.Desktop.Plugins;
using Cerebral.Speech;
using Cerebral.PackageSettings;

namespace Cerebral.AiDictationPlugin;

internal sealed record AiDictationSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string RuntimePath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string ModelId { get; init; } = "nvidia/parakeet-tdt-0.6b-v3";
    public string ModelRevision { get; init; } = "541d1f99c6b0c3cd0b11a95167540bb8edefd82b";
    public string ModelSource { get; init; } = "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3";
    public string ModelLicense { get; init; } = "CC-BY-4.0";
    public string RuntimeSource { get; init; } = "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.1.0";
    public string RuntimeLicense { get; init; } = "Apache-2.0";
    public string InputDeviceName { get; init; } = "";
    public double InputBoostDb { get; init; }
    public bool NormalizeQuietAudio { get; init; } = true;
    public double NormalizationCalibrationSeconds { get; init; } = 5;
    public bool AutoTranscribeOnSilence { get; init; }
    public bool LiveWordsWhileSpeaking { get; init; }
    public double SilenceWaitSeconds { get; init; } = 1.5;
    public bool DockControls { get; init; } = true;
    public bool AllowExternalTarget { get; init; }
    public bool GrammarCorrection { get; init; }
    public string GrammarProfileId { get; init; } = "";
    public long GrammarProfileRevision { get; init; }

    // Effective host-owned optional asset; never saved or substituted for the user's offline model.
    [System.Text.Json.Serialization.JsonIgnore]
    public string StreamingModelPath { get; init; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public SpeechModelBinding Binding => new(ModelId, ModelRevision, ModelPath);
    [System.Text.Json.Serialization.JsonIgnore]
    public AiDictationCaptureOptions Capture => new(InputDeviceName, InputBoostDb, NormalizeQuietAudio,
        NormalizationCalibrationSeconds, AutoTranscribeOnSilence, SilenceWaitSeconds);
}

internal sealed class AiDictationSettingsStore(string directory, DesktopProductPaths? productPaths = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string path = Path.Combine(Path.GetFullPath(directory), "ai-dictation.settings.json");
    public string CaptureDirectory { get; } = Path.Combine(Path.GetFullPath(directory), "ai-dictation-capture");

    // Legacy hydration and saving stay explicit. Runtime resolution fills only empty
    // bundled choices from host-owned roots and never rewrites acknowledged settings.
    public AiDictationSettings Load() => AiDictationSettingsPolicy.PrepareLegacy(
        PackagePreferenceJson.ReadLegacy(path, AiDictationSettingsPolicy.MaximumBytes));

    internal AiDictationSettings ResolveInstalledPaths(AiDictationSettings settings) => ResolvePaths(settings, emptyOnly: false);
    internal AiDictationSettings ResolveRuntimePaths(AiDictationSettings settings) => ResolvePaths(settings, emptyOnly: true);
    private AiDictationSettings ResolvePaths(AiDictationSettings settings, bool emptyOnly)
    {
        AiDictationSettingsPolicy.Validate(settings);
        if (productPaths is null) return settings;
        var bundled = new AiDictationSettings();
        var resolveRuntime = emptyOnly ? settings.RuntimePath.Length == 0 && settings.RuntimeSource == bundled.RuntimeSource : !File.Exists(settings.RuntimePath);
        var resolveModel = emptyOnly ? settings.ModelPath.Length == 0 && settings.ModelId == bundled.ModelId &&
            settings.ModelRevision == bundled.ModelRevision && settings.ModelSource == bundled.ModelSource : !File.Exists(settings.ModelPath);
        var runtime = !resolveRuntime ? settings.RuntimePath : FirstExisting(
            Path.Combine(productPaths.ToolsDirectory, "AI-Dictation", "NeMo-Speech", "bin", "nemo-speech.exe"),
            Path.Combine(productPaths.ToolsDirectory, "AI-Dictation", "nemo-speech.exe"),
            Path.Combine(productPaths.ToolsDirectory, "Speech", "nemo-speech.exe"));
        var model = !resolveModel ? settings.ModelPath : FirstExisting(
            Path.Combine(productPaths.ModelsDirectory, "AI-Dictation", "parakeet-tdt-0.6b-v3.q8_0.gguf"),
            Path.Combine(productPaths.ModelsDirectory, "Speech", "parakeet-tdt-0.6b-v3.q8_0.gguf"),
            Path.Combine(productPaths.ToolsDirectory, "AI-Dictation", "models", "parakeet-tdt-0.6b-v3.q8_0.gguf"));
        var streaming = FirstExisting(Path.Combine(productPaths.ModelsDirectory, "AI-Dictation", "Nemotron-English", "nemotron-speech-streaming-en-0.6b.q8_0.gguf"));
        return settings with { RuntimePath = runtime ?? settings.RuntimePath, ModelPath = model ?? settings.ModelPath,
            StreamingModelPath = streaming ?? "" };
    }

    private static string? FirstExisting(params string[] candidates) =>
        candidates.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).FirstOrDefault(File.Exists);

    public void Save(AiDictationSettings settings)
    {
        Validate(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
        if (bytes.Length > 16384) throw new InvalidDataException("AI dictation settings exceed their size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(AiDictationSettings value) => AiDictationSettingsPolicy.Validate(value);
}
