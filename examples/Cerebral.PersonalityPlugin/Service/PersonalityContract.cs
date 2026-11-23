using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Package-local JSON contract. No types are added to the shared SDK.</summary>
public static class PersonalityContract
{
    public const string PluginId = "pegasus.personality";
    public const string ProviderId = PluginId + ":context.resolve";
    public const int ProviderVersion = 1;
    public const string ReferenceKind = "personality.behavior";
    public const string ContributionId = PluginId + ":style";
    public const int SchemaVersion = 1;
    public const int MaximumUtf8Bytes = 2048;
    public const int MaximumPresetUtf8Bytes = 1024;
    public const int MaximumPresetWords = 120;
    public const int MaximumCustomPresets = 8;
    public const int MaximumRetainedVersions = 32;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static readonly PersonalityPreset[] BuiltIns =
    [
        Preset("cerebral", "Cerebral", 1,
            "Speak directly, candidly, and respectfully. Lead with the outcome, then the evidence, material limits, and next useful action. Keep explanations practical and concise; add detail when it helps a decision. Distinguish verified facts from inference and uncertainty. Avoid filler, generic praise, repeated summaries, and unnecessary offers. Adapt to the latest user correction and use natural warmth when it fits."),
        Preset("succinct", "Succinct", 1,
            "Give a brief, clear answer focused on the requested result. Include essential evidence, uncertainty, and the next action when relevant. Use short paragraphs or a compact list. Add technical detail only when needed to understand or use the result. Follow the latest user correction.")
    ];

    internal static PersonalityPreset Preset(string id, string name, long revision, string text) =>
        new(id, name, revision, text, Digest(Utf8.GetBytes(text)));

    internal static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static string SourceId(PersonalityPreset preset) =>
        $"personality/v1/{preset.Id}/{preset.Revision.ToString(CultureInfo.InvariantCulture)}/{preset.Sha256}";

    internal static string ReferenceText(PersonalityPreset preset) =>
        $"Optional communication style reference '{preset.Id}', revision {preset.Revision.ToString(CultureInfo.InvariantCulture)}, SHA-256 {preset.Sha256}:\n{preset.Text}";

    internal static ContextProviderResolution Resolution(PersonalityPreset preset) => new(ProviderId, ProviderVersion,
        [new ContextProviderSource(ContributionId, ReferenceKind, preset.Revision, ContextRequirement.Optional,
            [new ModelInputMessage("user", [new TextContentPart(ReferenceText(preset))])],
            Priority: 0, BeforeCurrentInput: true)]);

    internal static void ValidatePreset(PersonalityPreset preset)
    {
        if (preset is null || !ValidId(preset.Id) || preset.Revision < 1 ||
            string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80 || preset.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(preset.Text) || preset.Text.Any(value => char.IsControl(value) && value is not ('\n' or '\r' or '\t')))
            throw new InvalidDataException("A personality preset has invalid identity, revision, name, or text.");
        try
        {
            if (Utf8.GetByteCount(preset.Name) > 320 || Utf8.GetByteCount(preset.Text) > MaximumPresetUtf8Bytes ||
                preset.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > MaximumPresetWords ||
                preset.Sha256 != Digest(Utf8.GetBytes(preset.Text)) ||
                JsonSerializer.SerializeToUtf8Bytes(Resolution(preset), Json).Length > MaximumUtf8Bytes)
                throw new InvalidDataException("A personality preset exceeds its byte/word bound or has an invalid content hash.");
        }
        catch (EncoderFallbackException error) { throw new InvalidDataException("Personality text must be valid Unicode.", error); }
    }

    internal static bool ValidId(string? value) => value is { Length: >= 1 and <= 64 } &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');

    internal static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
    internal static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    internal static readonly JsonElement SaveSchema = Parse("""
        {"type":"object","properties":{
          "expectedRevision":{"type":"integer","minimum":0},
          "expectedSha256":{"type":"string","minLength":64,"maxLength":64},
          "id":{"type":"string","minLength":1,"maxLength":64},
          "name":{"type":"string","minLength":1,"maxLength":80},
          "text":{"type":"string","minLength":1,"maxLength":1024}},
          "required":["expectedRevision","expectedSha256","id","name","text"],"additionalProperties":false}
        """);
    internal static readonly JsonElement SelectSchema = Parse("""
        {"type":"object","properties":{
          "expectedRevision":{"type":"integer","minimum":0},
          "expectedSha256":{"type":"string","minLength":64,"maxLength":64},
          "enabled":{"type":"boolean"},
          "presetId":{"type":"string","minLength":1,"maxLength":64}},
          "required":["expectedRevision","expectedSha256","enabled","presetId"],"additionalProperties":false}
        """);
}

internal sealed record PersonalityPreset(string Id, string Name, long Revision, string Text, string Sha256);
internal sealed record PersonalityDocument(int SchemaVersion, long Revision, bool Enabled, string SelectedPresetId,
    PersonalityPreset[] Versions);
internal sealed record PersonalityDocumentSnapshot(PersonalityDocument Document, string Sha256)
{
    internal int PrivateIds { get; init; }
    internal int PrivateVersions { get; init; }
    internal IEnumerable<PersonalityPreset> AllVersions => PersonalityContract.BuiltIns.Concat(Document.Versions);
    internal PersonalityPreset Selected => Latest(Document.SelectedPresetId);
    internal PersonalityPreset Latest(string id) => AllVersions.Where(preset => preset.Id == id)
        .MaxBy(preset => preset.Revision) ?? throw new InvalidDataException("The selected personality preset is unavailable.");
}
