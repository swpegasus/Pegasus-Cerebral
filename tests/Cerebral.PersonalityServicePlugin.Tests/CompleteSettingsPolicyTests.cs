using System.Text.Json;
using Cerebral.Core;
using Cerebral.PersonalityServicePlugin;

/// <summary>Pure policy fixtures; no source store, user data, host runtime or migration is exercised.</summary>
internal static class CompleteSettingsPolicyTests
{
    internal static void Run(Action<bool, string> check)
    {
        var policy = new PersonalitySettingsPolicy();
        var definitions = Enumerable.Range(0, 8).Select(index => new PersonalityCustomDefinition(
            "style-" + index, "Style " + index, "Prefer clear practical answers " + index + ".")).ToArray();
        var preferences = new PersonalitySettingsPreferences(true, "style-0", Array.AsReadOnly(definitions));
        var values = PersonalitySettingsCodec.Write(preferences);
        var document = Document(values);
        var complete = policy.ExportComplete(document);
        check(complete.Complete && complete.ExcludedFields == 0 && complete.UnsupportedFields.Count == 0 &&
            complete.SettingsVersion == 2, "complete projection reports all declared nonsecret preferences");
        check(Fields(complete.Values, "enabled", "selectedPresetId", "customPresets") &&
            complete.Values.GetProperty("customPresets").GetArrayLength() == 8 &&
            complete.Values.GetProperty("customPresets").EnumerateArray().All(item => Fields(item, "id", "name", "text")),
            "complete projection contains current editable definitions without source history metadata");
        var roundTrip = policy.AdmitComplete(Document(complete.Values), null);
        check(roundTrip.Accepted && roundTrip.SettingsVersion == 2 && JsonElement.DeepEquals(values, roundTrip.Values),
            "complete eight-definition round trip preserves exact current selection names and text");
        check(policy.Admit(Document(values), null, portable: false).Accepted,
            "local full admission uses the same pure complete codec");

        var clear = PersonalitySettingsCodec.Write(new(false, "cerebral", Array.AsReadOnly(Array.Empty<PersonalityCustomDefinition>())));
        var cleared = policy.AdmitComplete(Document(clear), values);
        check(cleared.Accepted && cleared.Values.GetProperty("customPresets").GetArrayLength() == 0,
            "explicit complete empty list clears only the editable preference list");
        check(values.GetProperty("customPresets").GetArrayLength() == 8,
            "complete replacement leaves the caller's prior value unchanged");
        var missing = PersonalitySettingsCodec.Write(new(true, "desired-but-unprepared", Array.AsReadOnly(Array.Empty<PersonalityCustomDefinition>())));
        check(policy.AdmitComplete(Document(missing), null).Accepted &&
            policy.ExportComplete(Document(missing)).Values.GetProperty("selectedPresetId").GetString() == "desired-but-unprepared",
            "complete transfer preserves a valid desired custom reference without fabricating a definition");

        var builtinValues = PersonalitySettingsCodec.Write(preferences with { Enabled = false, SelectedPresetId = "succinct" });
        var portableBuiltin = policy.ExportPortable(Document(builtinValues));
        check(portableBuiltin.SettingsVersion == 2 && portableBuiltin.OmittedFields == 1 &&
            Fields(portableBuiltin.Values, "enabled", "selectedPresetId"),
            "portable built-in selection exports the pair and omits the custom list");
        var mergedBuiltin = policy.Admit(Document(portableBuiltin.Values), values, portable: true);
        var merged = PersonalitySettingsCodec.Read(mergedBuiltin.Values);
        check(mergedBuiltin.Accepted && !merged.Enabled && merged.SelectedPresetId == "succinct" &&
            JsonElement.DeepEquals(mergedBuiltin.Values.GetProperty("customPresets"), values.GetProperty("customPresets")),
            "portable built-in pair updates selection and preserves destination definitions");
        var builtinNoDestination = policy.Admit(Document(portableBuiltin.Values), null, portable: true);
        check(builtinNoDestination.Accepted && builtinNoDestination.Values.GetProperty("customPresets").GetArrayLength() == 0,
            "portable built-in pair can establish a destination with no custom definitions");
        var portableCustom = policy.ExportPortable(document);
        check(portableCustom.OmittedFields == 3 && !portableCustom.Values.EnumerateObject().Any(),
            "portable custom selection omits all three preference fields");
        check(policy.ExportPortable(Document(missing)).OmittedFields == 3,
            "unavailable desired custom reference also remains local during portable export");
        var preserved = policy.Admit(Document(portableCustom.Values), values, portable: true);
        check(preserved.Accepted && JsonElement.DeepEquals(preserved.Values, values),
            "portable empty projection preserves all destination preferences");
        var noDestination = policy.Admit(Document(portableCustom.Values), null, portable: true);
        check(!noDestination.Accepted && noDestination.Reason == "portable-destination-unavailable",
            "portable empty projection preserves absent destination instead of manufacturing a default");

        RejectAdmission(policy, Raw("{\"enabled\":true}"), values, check, "portable partial enabled field refuses", portable: true);
        RejectAdmission(policy, Raw("{\"selectedPresetId\":\"cerebral\"}"), values, check, "portable partial selected ID refuses", portable: true);
        RejectAdmission(policy, Raw("{\"enabled\":false,\"selectedPresetId\":\"style-0\"}"), values, check,
            "portable custom pair refuses even while disabled", portable: true);
        RejectAdmission(policy, builtinValues, values, check, "portable full custom-list document refuses", portable: true);
        var malformedDestination = Raw("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[],\"future\":1}");
        check(!policy.Admit(Document(portableCustom.Values), malformedDestination, true).Accepted &&
            !policy.Admit(Document(portableBuiltin.Values), malformedDestination, true).Accepted,
            "portable merging refuses malformed or future destination values without replacing them");
        var malformedBefore = malformedDestination.GetRawText();
        check(!policy.AdmitComplete(Document(values), malformedDestination).Accepted &&
            !policy.Admit(Document(values), malformedDestination, portable: false).Accepted &&
            malformedDestination.GetRawText() == malformedBefore,
            "complete and local valid incoming values preserve a malformed or future destination");
        check(!policy.AdmitComplete(Document(values), Raw("{\"enabled\":false,\"selectedPresetId\":\"cerebral\"}")).Accepted,
            "complete replacement refuses an unversioned schema-one destination instead of inferring conversion");
        check(!policy.Admit(Document(portableCustom.Values), Raw("{\"enabled\":false,\"selectedPresetId\":\"cerebral\"}"), true).Accepted,
            "portable merging never infers a schema-one destination from an unversioned pair");

        var legacyPair = Raw("{\"enabled\":true,\"selectedPresetId\":\"legacy-style\"}");
        var legacy = policy.AdmitComplete(Document(legacyPair, version: 1), values);
        check(legacy.Accepted && legacy.SettingsVersion == 2 &&
            legacy.Values.GetProperty("customPresets").GetArrayLength() == 0 &&
            legacy.Values.GetProperty("selectedPresetId").GetString() == "legacy-style",
            "explicit schema-one full pair converts to schema two with an empty current definition list");
        check(policy.ExportComplete(Document(legacyPair, version: 1)).SettingsVersion == 2 &&
            policy.ExportPortable(Document(legacyPair, version: 1)).OmittedFields == 3,
            "explicit schema-one export uses successor complete and portable semantics");
        check(!policy.AdmitComplete(Document(legacyPair), values).Accepted &&
            !policy.AdmitComplete(Document(values, version: 1), values).Accepted,
            "schema pair and complete shapes are not inferred across version declarations");
        check(!policy.AdmitComplete(Document(values, version: null), values).Accepted &&
            !policy.AdmitComplete(Document(values, version: 3), values).Accepted &&
            !policy.Admit(Document(portableBuiltin.Values, version: 3), values, true).Accepted,
            "unversioned and future settings documents refuse both admission paths");
        Reject(() => policy.ExportComplete(Document(values, version: 3)), check,
            "future settings complete export refuses rather than relabeling");
        check(!policy.AdmitComplete(Document(values) with { PluginId = "other.plugin" }, values).Accepted &&
            !policy.AdmitComplete(Document(values) with { Scope = "agent", ScopeId = "lead" }, values).Accepted &&
            !policy.AdmitComplete(Document(values) with { ScopeId = "unexpected" }, values).Accepted,
            "personality desired preferences are admitted only at the exact global coordinate");

        var legacyName = new string('界', 80);
        var unicode = PersonalitySettingsCodec.Write(new(true, "unicode", Array.AsReadOnly(new[]
            { new PersonalityCustomDefinition("unicode", legacyName, "Use clear explanations.") })));
        var unicodeAccepted = policy.AdmitComplete(Document(unicode), null);
        check(unicodeAccepted.Accepted && PersonalitySettingsCodec.Read(unicodeAccepted.Values).CustomPresets[0].Name == legacyName,
            "valid legacy 80-unit non-ASCII names survive complete transfer without truncation");
        var supplementary = PersonalitySettingsCodec.Write(new(true, "unicode", Array.AsReadOnly(new[]
            { new PersonalityCustomDefinition("unicode", string.Concat(Enumerable.Repeat("😀", 40)), "Use clear explanations.") })));
        check(policy.AdmitComplete(Document(supplementary), null).Accepted,
            "valid paired supplementary Unicode name remains supported");
        RejectDefinition(new("too-long", new string('a', 81), "Be clear."), check, "name character cap refuses");
        RejectDefinition(new("controls", "Bad\nName", "Be clear."), check, "name control characters refuse");
        RejectDefinition(new("controls", "Name", "Bad\0text"), check, "text control characters refuse");
        RejectDefinition(new("long-text", "Name", new string('a', 1025)), check, "text byte cap refuses");
        RejectDefinition(new("many-words", "Name", string.Join(' ', Enumerable.Repeat("word", 121))), check, "text word cap refuses");
        RejectDefinition(new("large-result", "Name", new string('\\', 1024)), check,
            "definition inside raw text cap still refuses an oversized serialized provider resolution");
        RejectDefinition(new("cerebral", "Shadow", "Be clear."), check, "custom built-in identity shadow refuses");
        RejectDefinition(new("Invalid", "Name", "Be clear."), check, "noncanonical custom identity refuses");
        RejectDefinition(new("unicode", "Name", "\uD800"), check, "typed lone Unicode surrogate refuses");

        var duplicate = definitions.Concat(new[] { definitions[0] }).Take(8).ToArray();
        duplicate[7] = duplicate[0];
        Reject(() => PersonalitySettingsCodec.Write(new(true, "style-0", Array.AsReadOnly(duplicate))), check,
            "duplicate custom identity refuses");
        var ninth = definitions.Append(new PersonalityCustomDefinition("ninth", "Ninth", "Be clear.")).ToArray();
        Reject(() => PersonalitySettingsCodec.Write(new(true, "style-0", Array.AsReadOnly(ninth))), check,
            "ninth current editable definition refuses without eviction");

        foreach (var (raw, label) in new[]
        {
            ("{\"enabled\":true,\"enabled\":false,\"selectedPresetId\":\"cerebral\",\"customPresets\":[]}", "duplicate root field"),
            ("{\"enabled\":\"true\",\"selectedPresetId\":\"cerebral\",\"customPresets\":[]}", "wrong boolean type"),
            ("{\"enabled\":true,\"selectedPresetId\":null,\"customPresets\":[]}", "null selected identity"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":null}", "null definition array"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[],\"versions\":[]}", "historical domain field"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[{\"id\":\"style\",\"id\":\"other\",\"name\":\"Name\",\"text\":\"Clear.\"}]}", "duplicate definition field"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[{\"id\":\"style\",\"name\":\"Name\",\"text\":\"Clear.\",\"revision\":1}]}", "immutable revision field"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[{\"id\":\"style\",\"name\":\"Name\",\"text\":\"\\uD800\"}]}", "escaped lone high surrogate"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[{\"id\":\"style\",\"name\":\"Name\",\"text\":\"\\uDC00\"}]}", "escaped lone low surrogate"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[{\"id\":\"style\",\"name\":\"Name\",\"text\":\"\\uD800x\"}]}", "escaped high surrogate followed by a short literal"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[{\"id\":\"style\",\"name\":\"Name\",\"text\":\"\\uD800\\u0041\"}]}", "escaped high surrogate followed by a non-low escape"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[{\"id\":\"style\",\"name\":\"Name\",\"text\":\"\\uD83D\\uDE00\\uDC00\"}]}", "valid escaped pair followed by a lone low surrogate"),
            ("{\"enabled\":true,\"selectedPresetId\":\"cerebral\",\"customPresets\":[],\"extra\":{\"a\":{\"b\":{\"c\":1}}}}", "depth above four")
        }) RejectAdmission(policy, Raw(raw), values, check, "closed settings refuse " + label);
        var padded = Raw("{\"enabled\":false,\"selectedPresetId\":\"cerebral\",\"customPresets\":[]" +
            new string(' ', PersonalitySettingsCodec.MaximumValuesUtf8Bytes) + "}");
        RejectAdmission(policy, padded, values, check, "32 KiB raw preference bound refuses oversized whitespace");
        var escapedPair = Raw("{\"enabled\":true,\"selectedPresetId\":\"style\",\"customPresets\":[{\"id\":\"style\",\"name\":\"Name\",\"text\":\"\\uD83D\\uDE00\"}]}");
        check(policy.AdmitComplete(Document(escapedPair), null).Accepted,
            "valid escaped surrogate pair is admitted without normalization loss");
        var literalEscape = PersonalitySettingsCodec.Write(new(true, "style", Array.AsReadOnly(new[]
            { new PersonalityCustomDefinition("style", "Name", "\\uD800") })));
        check(policy.AdmitComplete(Document(literalEscape), null).Accepted,
            "literal escaped-looking text is not mistaken for a Unicode escape");
        var decoded = PersonalitySettingsCodec.Read(values);
        Reject(() => ((IList<PersonalityCustomDefinition>)decoded.CustomPresets)[0] = new("changed", "Changed", "Changed."), check,
            "decoded custom definition collection is immutable");
        check(JsonElement.DeepEquals(values, document.Values), "all pure policy fixtures leave the original source values intact");
    }

    private static PluginSettingsDocument Document(JsonElement values, int? version = PersonalitySettingsPolicy.SettingsVersion) =>
        new(PersonalityContract.PluginId, SettingsPreferenceContract.GlobalScope, null,
            PersonalitySettingsPolicy.PackageVersion, version, values);

    private static JsonElement Raw(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static bool Fields(JsonElement value, params string[] expected) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Order(StringComparer.Ordinal));

    private static void RejectAdmission(PersonalitySettingsPolicy policy, JsonElement input, JsonElement current,
        Action<bool, string> check, string label, bool portable = false)
    {
        var before = current.GetRawText();
        var rejected = portable ? policy.Admit(Document(input), current, true) : policy.AdmitComplete(Document(input), current);
        check(!rejected.Accepted && rejected.Reason is { Length: > 0 } && current.GetRawText() == before, label);
    }

    private static void RejectDefinition(PersonalityCustomDefinition definition, Action<bool, string> check, string label) =>
        Reject(() => PersonalitySettingsCodec.ValidateDefinition(definition), check, label);

    private static void Reject(Action action, Action<bool, string> check, string label)
    {
        var refused = false;
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or
            JsonException or FormatException or OverflowException or NotSupportedException) { refused = true; }
        check(refused, label);
    }
}
