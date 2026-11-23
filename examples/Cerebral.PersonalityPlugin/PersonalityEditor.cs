using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Desktop.Plugins;

namespace Cerebral.PersonalityPlugin;
internal sealed record PersonalityInstructionDocument(string Path, string Content, string Sha256);
internal sealed record PersonalityOwnerDefinition(string Id, string Name, string Text);
internal sealed record PersonalityOwnerSettings(long Revision, string Sha256, bool Enabled, string SelectedPresetId,
    IReadOnlyList<PersonalityOwnerDefinition> CustomPresets);
internal sealed record PersonalityOwnerSources(long Revision, string Sha256)
{
    internal int RetainedCustomVersions { get; init; }
    internal int CustomPresetCount { get; init; }
}
internal sealed record PersonalityOwnerPreset(string Id, string Name, string Text, long Revision, string Sha256, bool ReadOnly, bool Legacy = false)
{
    internal int Utf8Bytes => Encoding.UTF8.GetByteCount(Text);
    internal int WordCount => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
internal sealed class PersonalityEditorSnapshot(PersonalitySettingsDocument legacy, PersonalityInstructionDocument lead,
    PersonalityOwnerSettings settings, PersonalityOwnerSources sources, IReadOnlyList<PersonalityOwnerPreset> presets,
    DesktopConversationBinding? owner, string? legacySelectionId, string? legacyNotice = null)
{
    internal PersonalitySettingsDocument Legacy { get; } = legacy;
    internal PersonalityInstructionDocument Lead { get; } = lead;
    internal PersonalityOwnerSettings Settings { get; } = settings;
    internal PersonalityOwnerSources Sources { get; } = sources;
    internal IReadOnlyList<PersonalityOwnerPreset> Presets { get; } = presets;
    internal DesktopConversationBinding? Owner { get; } = owner;
    internal string SelectedPresetId => Settings.Revision == 0 && legacySelectionId is not null ? legacySelectionId : Settings.SelectedPresetId;
    internal bool Enabled => Settings.Revision == 0 && legacySelectionId is not null ? Legacy.Settings.Enabled : Settings.Enabled;
    internal string? LegacyNotice { get; } = legacyNotice;
    internal int Consumed;
}

internal sealed partial class PersonalityEditor(IDesktopServiceHost service, PersonalitySettingsStore store, Func<DesktopConversationBinding>? owner = null)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal Task<PersonalityEditorSnapshot> LoadAsync(CancellationToken token = default) => LoadForOwner(owner?.Invoke(), token);

    private async Task<PersonalityEditorSnapshot> LoadForOwner(DesktopConversationBinding? binding, CancellationToken token)
    {
        Guard(binding, token);
        if (owner is not null && binding?.Generation == "configuration")
            throw new NotSupportedException("This Configuration host does not expose its current owner identity. Reconnect before saving personality settings.");
        var local = store.Load();
        var settings = ReadSettings(await Execute("personality.preferences.get", new { }, binding, token));
        var sources = ReadSources(await Execute("personality.sources.get", new { }, binding, token), true);
        var list = await Execute("personality.list", new { }, binding, token);
        if (Integer(list, "schemaVersion") != 1 || !list.TryGetProperty("presets", out var items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 10)
            throw new InvalidDataException("The personality service returned an unsupported source catalog.");
        var choices = new List<PersonalityOwnerPreset>();
        var catalogIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var id = Identifier(item, "id");
            if (!catalogIds.Add(id)) throw new InvalidDataException("The source catalog contains duplicate identities.");
            // Retained custom versions are immutable sources, not current editable definitions.
            if (!Boolean(item, "readOnly")) continue;
            var revision = Integer(item, "revision");
            var preset = ReadPreset(await Execute("personality.get", new { id, revision }, binding, token));
            if (!preset.ReadOnly || preset.Id != id || preset.Revision != revision || preset.Sha256 != Hash(item, "sha256"))
                throw new InvalidDataException("The exact built-in source changed while loading. Reload the panel.");
            choices.Add(preset);
        }
        var ids = choices.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var definition in settings.CustomPresets)
        {
            if (!ids.Add(definition.Id)) throw new InvalidDataException("A custom definition shadows a built-in style.");
            choices.Add(new(definition.Id, definition.Name, definition.Text, 0, Digest(Utf8.GetBytes(definition.Text)), false));
        }
        var lead = ReadLead(await Execute("instructions.get", new { role = "lead" }, binding, token), null);
        _ = PersonalityLegacyMigration.Remove(lead.Content, local.Settings);
        var confirmed = ReadSettings(await Execute("personality.preferences.get", new { }, binding, token));
        var confirmedSources = ReadSources(await Execute("personality.sources.get", new { }, binding, token), true);
        if (!SameSettings(confirmed, settings) || confirmedSources != sources || store.Load().Revision != local.Revision)
            throw new InvalidOperationException("Personality preferences or sources changed while loading. Reload the panel.");

        string? legacyId = null, notice = null;
        if (File.Exists(store.Path))
        {
            var previous = local.Settings.Selected();
            var id = "legacy-" + Digest(Utf8.GetBytes(previous.Id + "\n" + previous.Name + "\n" + previous.Instructions))[..16];
            var existing = choices.SingleOrDefault(value => value.Id == id);
            if (existing is not null && existing.Name == previous.Name && existing.Text == previous.Instructions) legacyId = id;
            else if (existing is null)
            {
                var imported = new PersonalityOwnerPreset(id, previous.Name, previous.Instructions, 0, Digest(Utf8.GetBytes(previous.Instructions)), false, true);
                if (imported.Utf8Bytes <= 1024 && imported.WordCount <= 120)
                {
                    ValidateDraft(id, imported.Name, imported.Text);
                    choices.Add(imported); ids.Add(id); legacyId = id;
                }
                else notice = "The previous local style exceeds the current size limit and is preserved unchanged. Choose or create a compact style before saving.";
            }
        }
        if (!ids.Contains(settings.SelectedPresetId))
        {
            choices.Add(new(settings.SelectedPresetId, "Unavailable saved style", "", 0, "", true));
            notice = "The saved style definition is unavailable. Choose a loaded style or create one before saving; no substitute was selected.";
        }
        return new(local, lead, settings, sources, choices.AsReadOnly(), binding, legacyId, notice);
    }

    internal async Task<PersonalityEditorSnapshot> SaveAsync(PersonalityEditorSnapshot baseline, bool enabled,
        string selectedPresetId, string name, string text, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var existing = baseline.Presets.SingleOrDefault(value => value.Id == selectedPresetId);
        if (existing is null && !selectedPresetId.StartsWith("style-", StringComparison.Ordinal))
            throw new InvalidDataException("Choose a loaded style or create a new style.");
        ValidateDraft(selectedPresetId, name, text);
        if (existing?.ReadOnly == true && (name != existing.Name || text != existing.Text))
            throw new InvalidDataException("Built-in styles are read-only. Create a new style to customize one.");
        var definitions = baseline.Settings.CustomPresets.ToList();
        if (existing?.ReadOnly != true)
        {
            var index = definitions.FindIndex(value => value.Id == selectedPresetId);
            var definition = new PersonalityOwnerDefinition(selectedPresetId, name, text);
            if (index >= 0) definitions[index] = definition; else definitions.Add(definition);
        }
        if (definitions.Count > 8) throw new InvalidDataException("At most eight current custom styles are supported. Existing definitions were preserved.");
        var cleaned = PersonalityLegacyMigration.Remove(baseline.Lead.Content, baseline.Legacy.Settings);
        Guard(baseline.Owner, token);
        if (store.Load().Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("The previous local selection changed. Reload before saving.");
        // Every mutation sequence consumes its baseline, including a lost save/preparation acknowledgement.
        if (Interlocked.Exchange(ref baseline.Consumed, 1) != 0)
            throw new InvalidOperationException("This save was already attempted. Reload to reconcile actual state; do not replay it.");
        var current = baseline.Settings;
        if (cleaned != baseline.Lead.Content)
        {
            if (current.Enabled)
                current = await SavePreferences(current, false, current.SelectedPresetId, current.CustomPresets, baseline.Owner, token);
            var acknowledgement = ReadLead(await Execute("instructions.save",
                new { role = "lead", content = cleaned, expectedSha256 = baseline.Lead.Sha256 }, baseline.Owner, token), cleaned);
            var readback = ReadLead(await Execute("instructions.get", new { role = "lead" }, baseline.Owner, token), cleaned);
            if (acknowledgement.Path != baseline.Lead.Path || readback != acknowledgement)
                throw new InvalidDataException("The exact legacy-removal CAS/readback was not confirmed. Reload before another save.");
        }

        // Save the entire editable definition set while disabled. Preparation never substitutes a source.
        current = await SavePreferences(current, false, selectedPresetId, definitions.AsReadOnly(), baseline.Owner, token);
        var prepared = await Execute("personality.source.prepare", new {
            schemaVersion = 1, expectedSettingsRevision = current.Revision,
            expectedStoreRevision = baseline.Sources.Revision, expectedStoreSha256 = baseline.Sources.Sha256
        }, baseline.Owner, token);
        Fields(prepared, ["schemaVersion", "settingsRevision", "preset", "sources"]);
        if (Integer(prepared, "schemaVersion") != 1 || Integer(prepared, "settingsRevision") != current.Revision)
            throw new InvalidDataException("Source preparation did not confirm the exact preference revision.");
        var preset = ReadPreset(prepared.GetProperty("preset"));
        var preparedSources = ReadSources(prepared.GetProperty("sources"), false);
        if (preset.Id != selectedPresetId || preset.Name != name || preset.Text != text ||
            preset.ReadOnly != (existing?.ReadOnly == true) || preparedSources.Revision < baseline.Sources.Revision ||
            preparedSources.Revision > baseline.Sources.Revision + 1)
            throw new InvalidDataException("Source preparation did not confirm the exact selected style.");
        var sourceReadback = ReadSources(await Execute("personality.sources.get", new { }, baseline.Owner, token), true);
        var preferenceReadback = ReadSettings(await Execute("personality.preferences.get", new { }, baseline.Owner, token));
        if (sourceReadback != preparedSources || !SameSettings(preferenceReadback, current))
            throw new InvalidOperationException("Preferences or sources changed during preparation. Reload; the unused source was retained.");

        // This explicit owner check is local to this save. It is not an every-dispatch legacy certificate.
        var lead = ReadLead(await Execute("instructions.get", new { role = "lead" }, baseline.Owner, token), null);
        if (PersonalityInstructionLayer.HasLayer(lead.Content))
            throw new InvalidOperationException("A legacy personality section appeared during this save. The new style was not enabled. Reload and review it.");
        if (enabled)
            current = await SavePreferences(current, true, selectedPresetId, current.CustomPresets, baseline.Owner, token);
        Guard(baseline.Owner, token);
        var result = await LoadForOwner(baseline.Owner, token);
        if (!SameSettings(result.Settings, current) || result.Sources != preparedSources)
            throw new InvalidOperationException("Saved personality state changed before readback. Reload before another save.");
        return result;
    }

    private async Task<PersonalityOwnerSettings> SavePreferences(PersonalityOwnerSettings current, bool enabled, string id,
        IReadOnlyList<PersonalityOwnerDefinition> definitions, DesktopConversationBinding? binding, CancellationToken token)
    {
        var values = Values(enabled, id, definitions);
        var result = await Execute("personality.preferences.save", new { schemaVersion = 2, expectedRevision = current.Revision, values }, binding, token);
        Fields(result, ["state", "changed"]);
        var changed = Boolean(result, "changed");
        var state = result.GetProperty("state");
        if (!state.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The preference save did not acknowledge an active document.");
        var revision = Integer(state, "revision");
        if (revision != current.Revision + (changed ? 1 : 0))
            throw new InvalidDataException("The preference save acknowledged an inconsistent revision.");
        var saved = ReadState(state, revision, values);
        var readback = ReadSettings(await Execute("personality.preferences.get", new { }, binding, token));
        if (!SameSettings(readback, saved))
            throw new InvalidDataException("The exact saved preference values were not confirmed. Reload before another save.");
        return saved;
    }

    private async Task<JsonElement> Execute(string operation, object args, DesktopConversationBinding? binding, CancellationToken token, int maximumUtf8Bytes = 384 * 1024)
    {
        Guard(binding, token);
        var result = await service.ExecuteServiceCommandAsync(operation, args, token);
        Guard(binding, token);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "The personality operation was rejected. Reload to inspect current state.");
        // Includes bounded active/pending owner metadata; only current editable definitions enter a save.
        if (result.Data.ValueKind != JsonValueKind.Object || Utf8.GetByteCount(result.Data.GetRawText()) > maximumUtf8Bytes)
            throw new InvalidDataException("The owner returned a malformed or oversized personality reply.");
        return result.Data;
    }
    private void Guard(DesktopConversationBinding? binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (owner is not null && owner() != binding)
            throw new InvalidOperationException("The configuration owner changed. Reload before another operation.");
    }
    private static bool SameSettings(PersonalityOwnerSettings a, PersonalityOwnerSettings b) => a.Revision == b.Revision && a.Sha256 == b.Sha256;
    private static PersonalityOwnerSettings ReadSettings(JsonElement value)
    {
        Fields(value, ["schemaVersion", "settingsVersion", "revision", "values", "state", "provider"]);
        if (Integer(value, "schemaVersion") != 2 || Integer(value, "settingsVersion") != 2)
            throw new InvalidDataException("The personality service requires supported central schema-two preferences.");
        return ReadState(value.GetProperty("state"), Integer(value, "revision"), value.GetProperty("values"));
    }
    private static PersonalityOwnerSettings ReadState(JsonElement state, long revision, JsonElement values)
    {
        UniqueMetadata(state);
        var coordinate = state.GetProperty("coordinate");
        Fields(coordinate, ["pluginId", "scope"], ["scopeId"]);
        if (String(coordinate, "pluginId", 64) != PersonalityPlugin.PluginId || String(coordinate, "scope", 16) != "global" ||
            !NullOrAbsent(coordinate, "scopeId") || Integer(state, "revision") != revision ||
            String(state, "validationAuthority", 32) != "service-policy")
            throw new InvalidDataException("The personality reply has an unexpected owner coordinate or revision.");
        var preferences = ReadValues(values, revision);
        if (!state.TryGetProperty("active", out var active) || active.ValueKind == JsonValueKind.Null)
        {
            if (preferences.Enabled || preferences.SelectedPresetId != "cerebral" || preferences.CustomPresets.Count != 0)
                throw new InvalidDataException("An absent active document did not return the declared defaults.");
        }
        else
        {
            UniqueMetadata(active);
            if (String(active, "pluginId", 64) != PersonalityPlugin.PluginId || String(active, "scope", 16) != "global" ||
                !NullOrAbsent(active, "scopeId") || Integer(active, "settingsVersion") != 2 ||
                !JsonElement.DeepEquals(active.GetProperty("values"), values))
                throw new InvalidDataException("Central active preferences and reply values are inconsistent.");
        }
        return preferences;
    }
    private static PersonalityOwnerSettings ReadValues(JsonElement values, long revision)
    {
        Fields(values, ["enabled", "selectedPresetId", "customPresets"]);
        if (Utf8.GetByteCount(values.GetRawText()) > 32 * 1024)
            throw new InvalidDataException("Personality preferences exceed their bound.");
        using var parsed = JsonDocument.Parse(values.GetRawText(), new JsonDocumentOptions { MaxDepth = 4 });
        var enabled = Boolean(values, "enabled"); var id = Identifier(values, "selectedPresetId");
        var items = values.GetProperty("customPresets");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 8) throw new InvalidDataException("Unsupported custom style collection.");
        var definitions = new List<PersonalityOwnerDefinition>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            Fields(item, ["id", "name", "text"]);
            var definition = new PersonalityOwnerDefinition(Identifier(item, "id"), String(item, "name", 320), String(item, "text", 1024));
            ValidateDraft(definition.Id, definition.Name, definition.Text);
            if (!ids.Add(definition.Id)) throw new InvalidDataException("Custom style identities must be unique.");
            definitions.Add(definition);
        }
        var frozen = definitions.AsReadOnly();
        return new(revision, Digest(Utf8.GetBytes(Values(enabled, id, frozen).GetRawText())), enabled, id, frozen);
    }
    private static JsonElement Values(bool enabled, string id, IReadOnlyList<PersonalityOwnerDefinition> definitions) =>
        JsonSerializer.SerializeToElement(new { enabled, selectedPresetId = id,
            customPresets = definitions.Select(value => new { id = value.Id, name = value.Name, text = value.Text }).ToArray() }, Json);
    private static PersonalityOwnerSources ReadSources(JsonElement value, bool versioned)
    {
        Fields(value, versioned ? ["schemaVersion", "revision", "sha256", "retention"] : ["revision", "sha256", "retention"]);
        if (versioned && Integer(value, "schemaVersion") != 1) throw new InvalidDataException("Unsupported source-state edition.");
        var retention = value.GetProperty("retention");
        Fields(retention, ["retainedCustomVersions", "maximumRetainedCustomVersions", "customPresetCount", "maximumCustomPresets", "automaticallyEvicts"]);
        var versions = Integer(retention, "retainedCustomVersions"); var count = Integer(retention, "customPresetCount");
        if (Integer(retention, "maximumRetainedCustomVersions") != 32 || Integer(retention, "maximumCustomPresets") != 8 ||
            versions > 32 || count > 8 || count > versions || Boolean(retention, "automaticallyEvicts"))
            throw new InvalidDataException("Unsupported immutable-source retention contract.");
        return new(Integer(value, "revision"), Hash(value, "sha256"))
        { RetainedCustomVersions = (int)versions, CustomPresetCount = (int)count };
    }
    private static PersonalityOwnerPreset ReadPreset(JsonElement value)
    {
        Fields(value, ["id", "name", "text", "revision", "sha256", "sourceId", "readOnly", "utf8Bytes"]);
        var id = Identifier(value, "id"); var name = String(value, "name", 320); var text = String(value, "text", 1024);
        ValidateDraft(id, name, text);
        var sha = Hash(value, "sha256"); var revision = Integer(value, "revision");
        if (sha != Digest(Utf8.GetBytes(text)) || revision < 1 || Integer(value, "utf8Bytes") != Utf8.GetByteCount(text) ||
            String(value, "sourceId", 160) != "personality/v1/" + id + "/" + revision.ToString(CultureInfo.InvariantCulture) + "/" + sha)
            throw new InvalidDataException("The personality source has an invalid content identity.");
        return new(id, name, text, revision, sha, Boolean(value, "readOnly"));
    }
    private static PersonalityInstructionDocument ReadLead(JsonElement value, string? expected)
    {
        var content = String(value, "content", 32768);
        if (expected is not null && content != expected) throw new InvalidDataException("The exact legacy removal was not confirmed. Reload before another save.");
        return new(String(value, "path", 4096), content, Hash(value, "sha256"));
    }
    internal static void ValidateDraft(string id, string name, string text)
    {
        if (!ValidId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl) ||
            Utf8.GetByteCount(name) > 320 || string.IsNullOrWhiteSpace(text) ||
            text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ||
            Utf8.GetByteCount(text) > 1024 || text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 120)
            throw new InvalidDataException("Use a nonempty name (at most 80 characters / 320 UTF-8 bytes) and text of at most 120 words / 1024 UTF-8 bytes.");
    }
    private static bool ValidId(string id) => id.Length is >= 1 and <= 64 && id[0] is >= 'a' and <= 'z' &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');
    private static string Identifier(JsonElement value, string property)
    { var id = String(value, property, 64); return ValidId(id) ? id : throw new InvalidDataException("Malformed style identity."); }
    private static bool NullOrAbsent(JsonElement value, string property) => !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null;
    private static bool Boolean(JsonElement value, string property) => value.GetProperty(property).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException("Malformed personality Boolean.") };
    private static void Fields(JsonElement value, IReadOnlyList<string> required, IReadOnlyList<string>? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Personality object required.");
        var names = value.EnumerateObject().Select(item => item.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || required.Any(field => !names.Contains(field, StringComparer.Ordinal)) ||
            names.Any(field => !required.Contains(field, StringComparer.Ordinal) && !(optional?.Contains(field, StringComparer.Ordinal) ?? false)))
            throw new InvalidDataException("Personality fields do not match the supported contract.");
    }
    private static void UniqueMetadata(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EnumerateObject().Count())
            throw new InvalidDataException("Duplicate or aliased personality metadata.");
    }
    private static long Integer(JsonElement value, string property)
    { if (!value.TryGetProperty(property, out var item) || !item.TryGetInt64(out var number) || number < 0) throw new InvalidDataException("Malformed personality revision or schema."); return number; }
    private static string String(JsonElement value, string property, int maximum)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Malformed personality " + property + ".");
        // Reject lone escaped surrogates before JsonElement can replace them during extraction.
        var raw = item.GetRawText();
        for (var index = 1; index < raw.Length - 1; index++)
        {
            if (raw[index] != '\\') continue; index++;
            if (raw[index] != 'u') continue;
            var code = (char)int.Parse(raw.AsSpan(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (char.IsHighSurrogate(code))
            {
                var next = index + 5;
                if (next + 5 >= raw.Length - 1 || raw[next] != '\\' || raw[next + 1] != 'u' ||
                    !char.IsLowSurrogate((char)int.Parse(raw.AsSpan(next + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
                    throw new InvalidDataException("Personality text must be valid Unicode.");
                index = next + 5;
            }
            else { if (char.IsLowSurrogate(code)) throw new InvalidDataException("Personality text must be valid Unicode."); index += 4; }
        }
        var text = item.GetString()!;
        if (Utf8.GetByteCount(text) > maximum) throw new InvalidDataException("Personality " + property + " exceeds its bound.");
        return text;
    }
    private static string Hash(JsonElement value, string property)
    { var hash = String(value, property, 64); if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("Malformed personality hash."); return hash; }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
