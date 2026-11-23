using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.PersonalityPlugin;

namespace Cerebral.PersonalityServicePlugin;

// Private package DTOs only. Ordinary settings transfer does not include this user-owned domain data.
internal sealed record PersonalityDiagnostic(string Code, string Message);
internal sealed record PersonalityDefinitionPin(string Kind, string Id, long Revision, string Sha256);
internal sealed record PersonalityModifierPin(string Id, long Revision, string Sha256);
internal sealed record PersonalityEffectivePin(string SourceId, long Revision, string Sha256);
internal sealed record PersonalityCatalogEntry(string Kind, string Id, long? Revision, string Sha256,
    string Name, string Availability, PersonalityDiagnostic? Diagnostic);
internal sealed record PersonalityChatView(int SchemaVersion, string ConversationId, long Revision, bool Enabled,
    PersonalityDefinitionPin? BaseSelection, bool BluntMode, PersonalityModifierPin? BluntPin,
    string Availability, PersonalityDiagnostic? Diagnostic, PersonalityEffectivePin? EffectiveSource,
    string? CatalogToken, PersonalityCatalogEntry[] Catalog);
internal sealed record PersonalityChatSaveReply(int SchemaVersion, string ConversationId, string Outcome,
    bool? Changed, PersonalityChatView? State, PersonalityDiagnostic? Diagnostic);
internal sealed record PersonalityChatState(string ConversationId, long Revision, bool Enabled,
    PersonalityDefinitionPin? BaseSelection, bool BluntMode, PersonalityModifierPin? BluntPin,
    PersonalityEffectivePin? EffectiveSource);
internal sealed record PersonalityPrivateProvenance(string Namespace, string RecordType, string RecordId,
    long Revision, string Sha256);
internal sealed record PersonalityPrivateDefinition(string Id, string Name, long Revision, string Text,
    string Sha256, PersonalityPrivateProvenance Provenance);
internal sealed record PersonalityEffectiveSource(PersonalityDefinitionPin BaseSelection,
    PersonalityModifierPin? BluntPin, string BaseText, string? ModifierText, string Sha256)
{
    internal string Text => BaseText + (ModifierText is null ? string.Empty : "\n" + ModifierText);
    // The descriptor hash separates kinds, base revisions and modifier versions even for identical text.
    internal string SourceId => "personality/v2/" + PersonalityContributionCodec.Hash(PersonalityContract.Element(
        new { baseSelection = BaseSelection, bluntPin = BluntPin })) + "/" + Sha256;
    internal PersonalityEffectivePin Pin => new(SourceId, 1, Sha256);
}
internal sealed record PersonalityChatDocument(int SchemaVersion, long LibraryRevision,
    PersonalityPrivateDefinition[] PrivateDefinitions, PersonalityEffectiveSource[] Sources, PersonalityChatState[] Chats);

internal sealed class PersonalityChatFailure(string code) : InvalidOperationException(code)
{
    internal string Code { get; } = code;
}

internal static class PersonalityChatContract
{
    internal const string GetOperation = "personality.chat.get";
    internal const string SaveOperation = "personality.chat.save";
    internal const string ImportOperation = "personality.private.import";
    internal const string PrivateGetOperation = "personality.private.get";
    internal const int MaximumReplyBytes = 64 * 1024;
    internal static PersonalityModifierPin BluntPin => new("blunt", PersonalityTemplates.BluntModeRevision,
        PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(PersonalityTemplates.BluntModeText)));
    internal static PersonalityChatState Absent(string id) => new(id, 0, false, null, false, null, null);

    internal static string ConversationId(JsonElement value)
    {
        var id = PersonalitySettingsCodec.Text(value.GetProperty("conversationId"));
        ValidateConversationId(id);
        return id;
    }
    internal static void ValidateConversationId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || PersonalityContract.Utf8.GetByteCount(id) > 256 ||
            id.Any(char.IsControl) || id.Any(char.IsWhiteSpace)) throw new PersonalityChatFailure("invalid-chat-id");
    }
    internal static bool Boolean(JsonElement value, string name) => value.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new ArgumentException("A personality boolean is required.")
    };
    internal static string Hash(JsonElement value, string name)
    {
        var hash = PersonalityServicePlugin.Text(value, name, 64);
        PersonalityAdmissionEngine.RequireHash(hash);
        return hash;
    }
    internal static void ValidatePin(PersonalityDefinitionPin pin)
    {
        if (pin is null || pin.Kind is not ("packaged" or "custom" or "private") || pin.Revision < 1 ||
            !ValidKey(pin.Id) || pin.Kind != "packaged" && !PersonalityContract.ValidId(pin.Id))
            throw new InvalidDataException("Invalid qualified personality definition pin.");
        PersonalityAdmissionEngine.RequireHash(pin.Sha256);
    }
    internal static bool ValidKey(string? id) => id is { Length: >= 1 and <= 128 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_');
    internal static void ValidateModifier(PersonalityModifierPin pin)
    {
        if (pin is null || pin.Id != "blunt" || pin.Revision < 1)
            throw new InvalidDataException("Invalid personality modifier pin.");
        PersonalityAdmissionEngine.RequireHash(pin.Sha256);
    }
    internal static void ValidateEffectivePin(PersonalityEffectivePin pin)
    {
        if (pin is null || pin.Revision != 1) throw new InvalidDataException("Invalid effective personality pin.");
        PersonalityAdmissionEngine.RequireHash(pin.Sha256);
        var parts = pin.SourceId?.Split('/');
        if (parts is not { Length: 4 } || parts[0] != "personality" || parts[1] != "v2" || parts[3] != pin.Sha256)
            throw new InvalidDataException("Invalid effective personality source identity.");
        PersonalityAdmissionEngine.RequireHash(parts[2]);
    }
    internal static PersonalityPreset AsPreset(PersonalityEffectiveSource source) =>
        new("effective", "Effective communication style", 1, source.Text, source.Sha256);
    internal static ContextProviderResolution Resolution(PersonalityEffectiveSource source) =>
        PersonalityContract.Resolution(AsPreset(source));
    internal static void ValidateSource(PersonalityEffectiveSource source)
    {
        ValidatePin(source.BaseSelection);
        if (source.BluntPin is not null) ValidateModifier(source.BluntPin);
        if (string.IsNullOrWhiteSpace(source.BaseText) ||
            PersonalityContract.Utf8.GetByteCount(source.BaseText) > PersonalityContract.MaximumPresetUtf8Bytes ||
            source.BaseText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > PersonalityContract.MaximumPresetWords ||
            source.BaseSelection.Sha256 != PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(source.BaseText)) ||
            (source.BluntPin is null) != (source.ModifierText is null) ||
            source.ModifierText is not null && (string.IsNullOrWhiteSpace(source.ModifierText) ||
                source.BluntPin!.Sha256 != PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(source.ModifierText))))
            throw new InvalidDataException("The effective source does not match its base and modifier pins.");
        if (string.IsNullOrWhiteSpace(source.Text) || source.Text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ||
            source.Sha256 != PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(source.Text)))
            throw new InvalidDataException("Invalid effective personality text or hash.");
        // Base admission retains 1024 bytes / 120 words. Composition is independently bounded by the
        // existing serialized provider budget, including escaping, wrappers and optional modifier text.
        if (JsonSerializer.SerializeToUtf8Bytes(Resolution(source), PersonalityContract.Json).Length > PersonalityContract.MaximumUtf8Bytes)
            throw new PersonalityChatFailure("effective-style-too-large");
        ValidateEffectivePin(source.Pin);
    }
    internal static IEnumerable<(PersonalityDefinitionPin Pin, string Name, string Text)> Packaged()
    {
        foreach (var preset in PersonalityContract.BuiltIns)
            yield return (new("packaged", preset.Id, preset.Revision, preset.Sha256), preset.Name, preset.Text);
        foreach (var template in PersonalityTemplates.All)
        {
            // Package keys are a distinct family. In particular gary_2 never weakens custom ValidId.
            var hash = PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(template.Text));
            PersonalityContract.ValidatePreset(PersonalityContract.Preset("package-validation", template.Name, 1, template.Text));
            if (template.Revision < 1) throw new InvalidDataException("A packaged personality revision must be positive.");
            yield return (new("packaged", template.PackageKey, template.Revision, hash), template.Name, template.Text);
        }
    }
    internal static PersonalityDiagnostic Diagnostic(string code) => new(code, code switch
    {
        "invalid-chat-id" => "No valid active chat identity is available.",
        "chat-revision-conflict" => "This chat changed. Reload before making another selection.",
        "catalog-changed" => "The preset library changed. Reload before selecting a preset.",
        "catalog-unavailable" => "The preset library could not be read. No replacement was selected.",
        "selection-unavailable" => "The exact selected style is unavailable. No replacement was selected.",
        "source-capacity" => "Personality storage capacity was reached. Existing data was preserved.",
        "effective-style-too-large" => "This style and mode exceed the response context limit. Nothing was truncated.",
        "cancelled-before-commit" => "The operation was cancelled before the chat selection was published.",
        "commit-unconfirmed" => "The result could not be confirmed. Reload this chat; do not replay the save.",
        "unsupported-schema" => "This personality request schema is unsupported.",
        _ => "The personality request could not be completed. Reload before another change."
    });
    internal static void Version(JsonElement value)
    {
        if (PersonalityServicePlugin.Integer(value, "schemaVersion", 1) != 1)
            throw new PersonalityChatFailure("unsupported-schema");
    }
    internal static JsonElement Reply(object value)
    {
        var result = PersonalityContract.Element(value);
        if (PersonalityContract.Utf8.GetByteCount(result.GetRawText()) > MaximumReplyBytes)
            throw new InvalidDataException("Personality reply exceeds its bound.");
        return result;
    }
}
