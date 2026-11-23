using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.PersonalityPlugin;

internal sealed record PersonalityPreset(string Id, string Name, string Instructions)
{
    internal int Utf8Bytes => PersonalitySettings.Utf8.GetByteCount(Instructions);
    internal int WordCount => Instructions.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

internal sealed record PersonalitySettings(bool Enabled, string SelectedPresetId,
    IReadOnlyList<PersonalityPreset> Presets)
{
    internal const int SchemaVersion = 1;
    internal const int MaximumPresets = 16;
    internal const int MaximumPresetUtf8Bytes = 2_048;
    internal const int MaximumPresetWords = 100;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly PersonalityPreset Neutral = new("neutral", "Neutral", """
        Be clear, direct, and respectful. Lead with the result, then the evidence, material limits, and next step. Distinguish facts from inference and uncertainty. Prefer root causes, cohesive solutions, and honest verification. Preserve the user's autonomy and current instructions. Avoid filler, generic praise, repeated offers, or unnecessary questions. Use warmth or light humor only when it fits.
        """.Trim());
    internal static PersonalitySettings Default { get; } = new(false, Neutral.Id, []);

    internal IReadOnlyList<PersonalityPreset> Choices => [Neutral, .. Presets];

    internal PersonalityPreset Selected()
    {
        Validate(this);
        return Choices.Single(preset => preset.Id == SelectedPresetId);
    }

    internal static void Validate(PersonalitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Presets is null || settings.Presets.Count > MaximumPresets)
            throw new InvalidDataException($"Personality settings support at most {MaximumPresets} local presets.");
        var ids = new HashSet<string>(StringComparer.Ordinal) { Neutral.Id };
        foreach (var preset in settings.Presets)
        {
            ValidatePreset(preset);
            if (!ids.Add(preset.Id)) throw new InvalidDataException("Personality preset IDs must be unique.");
        }
        if (!ids.Contains(settings.SelectedPresetId))
            throw new InvalidDataException("The selected personality preset is unavailable.");
    }

    internal static void ValidatePreset(PersonalityPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Id.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(preset.Id[0]) ||
            preset.Id.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_' or '-')))
            throw new InvalidDataException("Personality preset IDs use 1-64 ASCII letters, digits, dots, underscores, or hyphens.");
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80 || preset.Name.Any(char.IsControl))
            throw new InvalidDataException("Personality preset names must be 1-80 ordinary characters.");
        if (string.IsNullOrWhiteSpace(preset.Instructions) || preset.Instructions.Contains('\0') ||
            preset.Instructions.Contains(PersonalityInstructionLayer.StartMarker, StringComparison.Ordinal) ||
            preset.Instructions.Contains(PersonalityInstructionLayer.EndMarker, StringComparison.Ordinal))
            throw new InvalidDataException("Personality preset instructions are empty or contain a reserved marker.");
        int bytes;
        try { bytes = Utf8.GetByteCount(preset.Instructions); }
        catch (EncoderFallbackException error) { throw new InvalidDataException("Personality preset instructions must be valid Unicode.", error); }
        var words = preset.Instructions.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (bytes > MaximumPresetUtf8Bytes || words > MaximumPresetWords)
            throw new InvalidDataException($"Each personality preset is limited to {MaximumPresetWords} words and {MaximumPresetUtf8Bytes} UTF-8 bytes.");
    }
}

internal sealed record PersonalitySettingsDocument(PersonalitySettings Settings, string Revision);

internal sealed class PersonalitySettingsStore
{
    internal const string FileName = "personality-presets.json";
    internal const int MaximumFileBytes = 64 * 1024;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string directory;
    internal string Path { get; }

    internal PersonalitySettingsStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !System.IO.Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("An absolute plugin data directory is required.", nameof(dataDirectory));
        directory = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dataDirectory));
        Path = System.IO.Path.Combine(directory, FileName);
    }

    internal PersonalitySettingsDocument Load()
    {
        lock (Sync)
        {
            if (!File.Exists(Path)) return new(PersonalitySettings.Default, MissingRevision);
            RejectReparse(Path);
            var bytes = File.ReadAllBytes(Path);
            if (bytes.Length is 0 or > MaximumFileBytes)
                throw new InvalidDataException($"{FileName} must contain 1-{MaximumFileBytes} UTF-8 bytes.");
            string text;
            try { text = PersonalitySettings.Utf8.GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException($"{FileName} must be valid UTF-8.", error); }
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            var settings = ReadSettings(document.RootElement);
            return new(settings, Digest(bytes));
        }
    }

    internal PersonalitySettingsDocument Save(PersonalitySettings settings, string expectedRevision)
    {
        PersonalitySettings.Validate(settings);
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new ArgumentException("Reload local personality settings before saving.");
        lock (Sync)
        {
            var current = File.Exists(Path) ? Digest(ReadCurrentBytes()) : MissingRevision;
            if (!current.Equals(expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("Local personality settings changed since they were loaded. Reopen this panel before saving; the current settings were preserved.");
            Directory.CreateDirectory(directory);
            RejectReparse(directory);
            if (File.Exists(Path)) RejectReparse(Path);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = PersonalitySettings.SchemaVersion,
                enabled = settings.Enabled,
                selectedPresetId = settings.SelectedPresetId,
                presets = settings.Presets.Select(preset => new { preset.Id, preset.Name, preset.Instructions }).ToArray()
            }, WriteOptions);
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException($"{FileName} exceeds its {MaximumFileBytes}-byte limit.");
            var temporary = System.IO.Path.Combine(directory, "." + FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
                File.Move(temporary, Path, true);
            }
            finally { try { File.Delete(temporary); } catch { } }
            return new(settings, Digest(bytes));
        }
    }

    private byte[] ReadCurrentBytes()
    {
        RejectReparse(Path);
        var bytes = File.ReadAllBytes(Path);
        if (bytes.Length is 0 or > MaximumFileBytes)
            throw new InvalidDataException($"{FileName} must contain 1-{MaximumFileBytes} bytes.");
        return bytes;
    }

    private static PersonalitySettings ReadSettings(JsonElement root)
    {
        RequireObject(root, ["schemaVersion", "enabled", "selectedPresetId", "presets"]);
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("schemaVersion").TryGetInt32(out var schemaVersion) || schemaVersion != PersonalitySettings.SchemaVersion)
            throw new InvalidDataException("Personality settings use an unsupported schema version.");
        var enabled = root.GetProperty("enabled");
        if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Personality enabled must be boolean.");
        var selected = RequiredString(root, "selectedPresetId", 64);
        var array = root.GetProperty("presets");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > PersonalitySettings.MaximumPresets)
            throw new InvalidDataException("Personality presets must be a bounded array.");
        var presets = new List<PersonalityPreset>();
        foreach (var item in array.EnumerateArray())
        {
            RequireObject(item, ["id", "name", "instructions"]);
            presets.Add(new(RequiredString(item, "id", 64), RequiredString(item, "name", 80),
                RequiredString(item, "instructions", PersonalitySettings.MaximumPresetUtf8Bytes)));
        }
        var settings = new PersonalitySettings(enabled.GetBoolean(), selected, presets.AsReadOnly());
        PersonalitySettings.Validate(settings);
        return settings;
    }

    private static string RequiredString(JsonElement value, string name, int maximumLength)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } text || text.Length > maximumLength)
            throw new InvalidDataException($"Personality settings require bounded string {name}.");
        return text;
    }

    private static void RequireObject(JsonElement value, IReadOnlyList<string> names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Personality settings contain a non-object record.");
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != names.Count || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            properties.Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("Personality settings contain missing, duplicate, or unsupported fields.");
    }

    private static void RejectReparse(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Personality settings cannot use a filesystem link.");
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private const string MissingRevision = "missing";
}
