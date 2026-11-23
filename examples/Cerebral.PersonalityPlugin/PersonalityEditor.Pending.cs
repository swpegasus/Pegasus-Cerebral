using System.IO;
using System.Text.Json;
using Cerebral.Desktop.Plugins;

namespace Cerebral.PersonalityPlugin;
internal sealed record PersonalityPendingInput(string Id, string Purpose, int SettingsVersion, string PackageVersion, JsonElement Document)
{
    public string Label => (Purpose == "complete-nonsecret" ? "Complete preferences" : Purpose == "portable" ? "Portable preferences" : "Local preferences") + " · " + PackageVersion + " · " + Id;
}
internal sealed class PersonalityPendingSnapshot(JsonElement transfer, IReadOnlyList<PersonalityPendingInput> inputs,
    PersonalityOwnerSources sources, PersonalityInstructionDocument lead, PersonalitySettingsDocument legacy, DesktopConversationBinding? binding)
{
    internal JsonElement Transfer { get; } = transfer;
    internal IReadOnlyList<PersonalityPendingInput> Inputs { get; } = inputs;
    internal PersonalityOwnerSources Sources { get; } = sources;
    internal PersonalityInstructionDocument Lead { get; } = lead;
    internal PersonalitySettingsDocument Legacy { get; } = legacy;
    internal DesktopConversationBinding? Owner { get; } = binding;
    internal long Revision => Transfer.GetProperty("state").GetProperty("revision").GetInt64();
    internal int Consumed;
}
internal sealed class PersonalityPendingPreparation(PersonalityPendingSnapshot snapshot, PersonalityPendingInput input,
    PersonalityOwnerSettings values, JsonElement rawValues, string pendingHash, string valuesHash,
    PersonalityOwnerPreset? preset, PersonalityOwnerSources sources)
{
    internal PersonalityPendingSnapshot Snapshot { get; } = snapshot;
    internal PersonalityPendingInput Input { get; } = input;
    internal PersonalityOwnerSettings Values { get; } = values;
    internal JsonElement RawValues { get; } = rawValues;
    internal string PendingHash { get; } = pendingHash;
    internal string ValuesHash { get; } = valuesHash;
    internal PersonalityOwnerPreset? Preset { get; } = preset;
    internal PersonalityOwnerSources Sources { get; } = sources;
    internal int Consumed;
}
internal sealed partial class PersonalityEditor
{
    // One active plus eight generic 128 KiB documents and bounded metadata. Prepared values stay at 32 KiB.
    private const int MaximumTransferReplyBytes = 9 * 128 * 1024 + 64 * 1024;
    private Task<JsonElement> ExecuteTransfer(string operation, object args, DesktopConversationBinding? binding, CancellationToken token) =>
        Execute(operation, args, binding, token, MaximumTransferReplyBytes);
    private const string TransferGet = "personality.preferences.transfer.get";
    private const string TransferSave = "personality.preferences.transfer.save";
    private const string PendingPrepare = "personality.preferences.pending.prepare";
    private const string PendingApply = "personality.preferences.pending.apply";
    internal Task<PersonalityPendingSnapshot> LoadPendingAsync(CancellationToken token = default) => LoadPendingForOwner(owner?.Invoke(), token);
    private async Task<PersonalityPendingSnapshot> LoadPendingForOwner(DesktopConversationBinding? binding, CancellationToken token)
    {
        Guard(binding, token);
        if (owner is not null && binding?.Generation == "configuration")
            throw new NotSupportedException("Reconnect with a host that exposes the current owner before reviewing imports.");
        var legacy = store.Load();
        var transfer = ReadTransfer(await ExecuteTransfer(TransferGet, new { }, binding, token));
        var sources = ReadSources(await Execute("personality.sources.get", new { }, binding, token), true);
        var lead = ReadLead(await Execute("instructions.get", new { role = "lead" }, binding, token), null);
        var confirmed = ReadTransfer(await ExecuteTransfer(TransferGet, new { }, binding, token));
        var confirmedSources = ReadSources(await Execute("personality.sources.get", new { }, binding, token), true);
        if (!JsonElement.DeepEquals(transfer, confirmed) || sources != confirmedSources || legacy.Revision != store.Load().Revision)
            throw new InvalidOperationException("Imported preference state changed while loading. Reload before continuing.");
        return new(transfer, Inputs(transfer), sources, lead, legacy, binding);
    }

    internal async Task<PersonalityPendingPreparation> PreparePendingAsync(PersonalityPendingSnapshot baseline, string id, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var input = baseline.Inputs.SingleOrDefault(value => value.Id == id)
            ?? throw new InvalidOperationException("Select a retained imported preference entry.");
        Guard(baseline.Owner, token);
        var lead = ReadLead(await Execute("instructions.get", new { role = "lead" }, baseline.Owner, token), null);
        if (lead != baseline.Lead || store.Load().Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("Previous selection or lead instructions changed before review. Reload before preparing.");
        var current = ReadValues(CurrentOrDefaultValues(baseline.Transfer), baseline.Revision);
        if (current.Enabled && PersonalityInstructionLayer.HasLayer(baseline.Lead.Content))
        {
            PersonalityOwnerPreset? available = null;
            try { available = ReadPreset(await Execute("personality.get", new { id = current.SelectedPresetId }, baseline.Owner, token)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException) { }
            if (!SourceMatches(available, current))
                throw new InvalidOperationException("Remove the previous style section explicitly before reviewing imports. Preparing the unavailable enabled style could otherwise overlap that section.");
        }
        if (Interlocked.Exchange(ref baseline.Consumed, 1) != 0)
            throw new InvalidOperationException("This preparation was already attempted. Reload to reconcile actual state.");
        var response = await Execute(PendingPrepare, new {
            schemaVersion = 1, expectedRevision = baseline.Revision, pendingId = input.Id, expectedPurpose = input.Purpose,
            expectedStoreRevision = baseline.Sources.Revision, expectedStoreSha256 = baseline.Sources.Sha256
        }, baseline.Owner, token);
        var result = ReadPreparation(response, baseline, input);
        var transfer = ReadTransfer(await ExecuteTransfer(TransferGet, new { }, baseline.Owner, token));
        var sources = ReadSources(await Execute("personality.sources.get", new { }, baseline.Owner, token), true);
        var confirmedLead = ReadLead(await Execute("instructions.get", new { role = "lead" }, baseline.Owner, token), null);
        if (!JsonElement.DeepEquals(transfer, baseline.Transfer) || sources != result.Sources || confirmedLead != lead || store.Load().Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("Preference or source state changed during preparation. Reload; an unused source may remain retained.");
        if (result.Preset is { } preset)
        {
            var exact = ReadPreset(await Execute("personality.get", new { id = preset.Id, revision = preset.Revision }, baseline.Owner, token));
            if (exact != preset) throw new InvalidDataException("The exact prepared source was not confirmed.");
        }
        return result;
    }
    private static PersonalityPendingPreparation ReadPreparation(JsonElement response, PersonalityPendingSnapshot baseline, PersonalityPendingInput input)
    {
        Fields(response, ["schemaVersion", "pendingId", "purpose", "settingsRevision", "pendingSha256", "valuesSha256", "values", "preset", "sources"]);
        if (Integer(response, "schemaVersion") != 1 || String(response, "pendingId", 160) != input.Id ||
            String(response, "purpose", 32) != input.Purpose || Integer(response, "settingsRevision") != baseline.Revision)
            throw new InvalidDataException("Import preparation did not confirm the selected entry, purpose and revision.");
        var rawValues = response.GetProperty("values").Clone();
        var values = ReadValues(rawValues, baseline.Revision);
        PersonalityOwnerPreset? preset = response.GetProperty("preset").ValueKind == JsonValueKind.Null ? null : ReadPreset(response.GetProperty("preset"));
        if (values.Enabled && preset is null) throw new InvalidDataException("Enabled imported preferences require an exact prepared style.");
        if (preset is not null)
        {
            var definition = values.CustomPresets.SingleOrDefault(value => value.Id == values.SelectedPresetId);
            if (preset.Id != values.SelectedPresetId || definition is not null &&
                (preset.ReadOnly || preset.Name != definition.Name || preset.Text != definition.Text) || definition is null && !preset.ReadOnly)
                throw new InvalidDataException("Import preparation returned a different selected style.");
        }
        var sources = ReadSources(response.GetProperty("sources"), false);
        if (sources.Revision < baseline.Sources.Revision || sources.Revision > baseline.Sources.Revision + 1)
            throw new InvalidDataException("Import preparation returned an inconsistent source revision.");
        var pendingHash = Hash(response, "pendingSha256"); var valuesHash = Hash(response, "valuesSha256");
        // Match the package's typed document serialization; incoming Values retain their original order.
        var document = input.Document;
        var original = JsonSerializer.SerializeToElement(new { pendingId = input.Id, purpose = input.Purpose,
            document = new { pluginId = PersonalityPlugin.PluginId, scope = "global", scopeId = (string?)null,
                sourcePackageVersion = document.GetProperty("sourcePackageVersion").GetString(),
                settingsVersion = input.SettingsVersion, values = document.GetProperty("values") } }, Json);
        if (pendingHash != Digest(JsonSerializer.SerializeToUtf8Bytes(original, Json)) ||
            valuesHash != Digest(JsonSerializer.SerializeToUtf8Bytes(Values(values.Enabled, values.SelectedPresetId, values.CustomPresets), Json)))
            throw new InvalidDataException("Import preparation hashes do not match the original entry and admitted values.");
        return new(baseline, input, values, rawValues, pendingHash, valuesHash, preset, sources);
    }


    internal async Task<PersonalityPendingSnapshot> ApplyPendingAsync(PersonalityPendingPreparation reviewed, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        var baseline = reviewed.Snapshot;
        var cleaned = PersonalityLegacyMigration.Remove(baseline.Lead.Content, baseline.Legacy.Settings);
        Guard(baseline.Owner, token);
        if (store.Load().Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("The previous local selection changed. Reload before applying.");
        // Each explicit Apply is single use, including readback failure or a commit with a lost acknowledgement.
        if (Interlocked.Exchange(ref reviewed.Consumed, 1) != 0)
            throw new InvalidOperationException("This Apply was already attempted. Reload and review actual state; do not replay.");
        var transfer = ReadTransfer(await ExecuteTransfer(TransferGet, new { }, baseline.Owner, token));
        var sources = ReadSources(await Execute("personality.sources.get", new { }, baseline.Owner, token), true);
        var lead = ReadLead(await Execute("instructions.get", new { role = "lead" }, baseline.Owner, token), null);
        if (!JsonElement.DeepEquals(transfer, baseline.Transfer) || sources != reviewed.Sources || lead != baseline.Lead)
            throw new InvalidOperationException("Preferences, prepared sources or lead instructions changed after review. Reload before applying.");
        var refreshed = reviewed;
        if (cleaned != lead.Content)
        {
            (transfer, sources, lead) = await CleanPendingLegacy(baseline, transfer, sources, reviewed.Preset, token);
            var fresh = new PersonalityPendingSnapshot(transfer, Inputs(transfer), sources, lead, baseline.Legacy, baseline.Owner);
            refreshed = await PreparePendingAsync(fresh, reviewed.Input.Id, token);
            if (refreshed.PendingHash != reviewed.PendingHash || refreshed.ValuesHash != reviewed.ValuesHash ||
                !JsonElement.DeepEquals(refreshed.RawValues, reviewed.RawValues))
                throw new InvalidOperationException("The selected import no longer produces the values you reviewed. Reload and review it again.");
        }

        var finalLead = ReadLead(await Execute("instructions.get", new { role = "lead" }, baseline.Owner, token), null);
        if (finalLead != lead || PersonalityInstructionLayer.HasLayer(finalLead.Content))
            throw new InvalidOperationException("Lead instructions changed during Apply. Reload before continuing.");
        if (store.Load().Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("The previous local selection changed during Apply. Reload before continuing.");
        var preset = refreshed.Preset;
        var response = await ExecuteTransfer(PendingApply, new {
            schemaVersion = 1, expectedRevision = Integer(transfer.GetProperty("state"), "revision"),
            pendingId = reviewed.Input.Id, expectedPurpose = reviewed.Input.Purpose,
            pendingSha256 = reviewed.PendingHash, valuesSha256 = reviewed.ValuesHash,
            expectedSourceId = preset is null ? null : SourceId(preset),
            expectedSourceRevision = preset is null ? (long?)null : preset.Revision,
            expectedSourceSha256 = preset?.Sha256
        }, baseline.Owner, token);
        var saved = await ReadTransferSave(response, transfer, reviewed.RawValues, reviewed.Input.Id, baseline.Owner, token);
        var result = await LoadPendingForOwner(baseline.Owner, token);
        if (!JsonElement.DeepEquals(result.Transfer, saved) || result.Sources != refreshed.Sources ||
            result.Lead != finalLead || result.Legacy.Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("Applied state changed before final readback. Reload; do not replay.");
        return result;
    }



    internal async Task<PersonalityPendingSnapshot> CleanPendingLegacyAsync(PersonalityPendingSnapshot baseline, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        Guard(baseline.Owner, token);
        _ = PersonalityLegacyMigration.Remove(baseline.Lead.Content, baseline.Legacy.Settings);
        if (store.Load().Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("The previous local selection changed. Reload before removing its section.");
        if (Interlocked.Exchange(ref baseline.Consumed, 1) != 0)
            throw new InvalidOperationException("This cleanup was already attempted. Reload to reconcile actual state; do not replay.");
        var transfer = ReadTransfer(await ExecuteTransfer(TransferGet, new { }, baseline.Owner, token));
        var sources = ReadSources(await Execute("personality.sources.get", new { }, baseline.Owner, token), true);
        var lead = ReadLead(await Execute("instructions.get", new { role = "lead" }, baseline.Owner, token), null);
        if (!JsonElement.DeepEquals(transfer, baseline.Transfer) || sources != baseline.Sources || lead != baseline.Lead)
            throw new InvalidOperationException("Preferences, sources or instructions changed. Reload before removing the previous section.");
        (transfer, sources, lead) = await CleanPendingLegacy(baseline, transfer, sources, null, token);
        var result = await LoadPendingForOwner(baseline.Owner, token);
        if (!JsonElement.DeepEquals(result.Transfer, transfer) || result.Sources != sources || result.Lead != lead ||
            result.Legacy.Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("Cleanup state changed before final readback. Reload; do not replay.");
        return result;
    }

    private async Task<(JsonElement Transfer, PersonalityOwnerSources Sources, PersonalityInstructionDocument Lead)> CleanPendingLegacy(
        PersonalityPendingSnapshot baseline, JsonElement transfer, PersonalityOwnerSources sources, PersonalityOwnerPreset? available,
        CancellationToken token)
    {
        var lead = baseline.Lead;
        var cleaned = PersonalityLegacyMigration.Remove(lead.Content, baseline.Legacy.Settings);
        if (cleaned == lead.Content) return (transfer, sources, lead);
        // Capture the destination, not the incoming proposal. Portable {} preserves this full preimage.
        var original = CurrentOrDefaultValues(transfer);
        var originalSettings = ReadValues(original, Integer(transfer.GetProperty("state"), "revision"));
        if (originalSettings.Enabled)
        {
            transfer = await SaveCurrentTransfer(transfer,
                Values(false, originalSettings.SelectedPresetId, originalSettings.CustomPresets), baseline.Owner, token);
            // Materializing an unavailable current source must occur only after the acknowledged disable.
            if (!SourceMatches(available, originalSettings))
                sources = await PrepareCurrentTransfer(transfer, sources, baseline.Owner, token);
        }
        if (store.Load().Revision != baseline.Legacy.Revision)
            throw new InvalidOperationException("The previous local selection changed during cleanup. Reload before continuing.");
        var acknowledgement = ReadLead(await Execute("instructions.save",
            new { role = "lead", content = cleaned, expectedSha256 = lead.Sha256 }, baseline.Owner, token), cleaned);
        lead = ReadLead(await Execute("instructions.get", new { role = "lead" }, baseline.Owner, token), cleaned);
        if (acknowledgement.Path != baseline.Lead.Path || lead != acknowledgement)
            throw new InvalidDataException("The exact previous-section removal was not acknowledged. Reload before another attempt.");
        if (PersonalityInstructionLayer.HasLayer(lead.Content))
            throw new InvalidOperationException("A previous personality section remains. Reload and review it.");
        if (originalSettings.Enabled)
            transfer = await SaveCurrentTransfer(transfer, original, baseline.Owner, token);
        return (transfer, sources, lead);
    }

    private async Task<PersonalityOwnerSources> PrepareCurrentTransfer(JsonElement transfer, PersonalityOwnerSources sources,
        DesktopConversationBinding? binding, CancellationToken token)
    {
        var revision = Integer(transfer.GetProperty("state"), "revision");
        var original = CurrentValues(transfer);
        var settings = ReadValues(original, revision);
        var response = await Execute("personality.preferences.transfer.prepare", new {
            schemaVersion = 1, expectedRevision = revision, expectedStoreRevision = sources.Revision, expectedStoreSha256 = sources.Sha256
        }, binding, token);
        Fields(response, ["schemaVersion", "settingsRevision", "valuesSha256", "values", "preset", "sources"]);
        if (Integer(response, "schemaVersion") != 1 || Integer(response, "settingsRevision") != revision ||
            !JsonElement.DeepEquals(response.GetProperty("values"), original) ||
            Hash(response, "valuesSha256") != Digest(JsonSerializer.SerializeToUtf8Bytes(
                Values(settings.Enabled, settings.SelectedPresetId, settings.CustomPresets), Json)))
            throw new InvalidDataException("Current-source preparation did not confirm the exact destination values.");
        var preset = ReadPreset(response.GetProperty("preset"));
        var preparedSources = ReadSources(response.GetProperty("sources"), false);
        if (!SourceMatches(preset, settings) || preparedSources.Revision < sources.Revision ||
            preparedSources.Revision > sources.Revision + 1)
            throw new InvalidDataException("Current-source preparation did not confirm the exact destination style.");
        var exact = ReadPreset(await Execute("personality.get", new { id = preset.Id, revision = preset.Revision }, binding, token));
        var confirmed = ReadTransfer(await ExecuteTransfer(TransferGet, new { }, binding, token));
        var confirmedSources = ReadSources(await Execute("personality.sources.get", new { }, binding, token), true);
        if (exact != preset || !JsonElement.DeepEquals(confirmed, transfer) || confirmedSources != preparedSources)
            throw new InvalidOperationException("Current values or sources changed during preparation. Reload; an unused source may remain retained.");
        return preparedSources;
    }

    private static string SourceId(PersonalityOwnerPreset preset) =>
        "personality/v1/" + preset.Id + "/" + preset.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + preset.Sha256;
    private static bool SourceMatches(PersonalityOwnerPreset? preset, PersonalityOwnerSettings settings)
    {
        if (preset is null || preset.Id != settings.SelectedPresetId) return false;
        var definition = settings.CustomPresets.SingleOrDefault(value => value.Id == settings.SelectedPresetId);
        return definition is null ? preset.ReadOnly : !preset.ReadOnly && preset.Name == definition.Name && preset.Text == definition.Text;
    }
    private static JsonElement CurrentOrDefaultValues(JsonElement transfer) =>
        transfer.GetProperty("state").GetProperty("active").ValueKind == JsonValueKind.Null
            ? Values(false, "cerebral", []) : CurrentValues(transfer);

    private static JsonElement ReadTransfer(JsonElement value)
    {
        Fields(value, ["schemaVersion", "state", "pendingPurposes"]);
        if (Integer(value, "schemaVersion") != 2) throw new InvalidDataException("Unsupported purpose-aware preference envelope.");
        var state = value.GetProperty("state"); UniqueMetadata(state);
        Fields(state, ["coordinate", "revision", "active", "pending", "validationAuthority"], ["portableValues", "omittedPortableFields", "pendingReason"]);
        var coordinate = state.GetProperty("coordinate"); Fields(coordinate, ["pluginId", "scope"], ["scopeId"]);
        if (String(coordinate, "pluginId", 64) != PersonalityPlugin.PluginId || String(coordinate, "scope", 16) != "global" ||
            !NullOrAbsent(coordinate, "scopeId") || String(state, "validationAuthority", 32) != "service-policy")
            throw new InvalidDataException("Imported preferences belong to an unexpected owner coordinate.");
        _ = Integer(state, "revision");
        if (state.TryGetProperty("active", out var active) && active.ValueKind != JsonValueKind.Null) DocumentCoordinate(active);
        var pending = state.GetProperty("pending"); var purposes = value.GetProperty("pendingPurposes");
        if (pending.ValueKind != JsonValueKind.Array || pending.GetArrayLength() > 8 ||
            purposes.ValueKind != JsonValueKind.Array || purposes.GetArrayLength() != pending.GetArrayLength())
            throw new InvalidDataException("Imported preference entries and purpose mappings are inconsistent.");
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in purposes.EnumerateArray())
        {
            Fields(item, ["pendingId", "purpose"]);
            var id = String(item, "pendingId", 160); var purpose = String(item, "purpose", 32);
            if (!PendingId(id) || purpose is not ("complete-nonsecret" or "portable" or "local") || !mappings.TryAdd(id, purpose))
                throw new InvalidDataException("Invalid or duplicate retained import purpose.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in pending.EnumerateArray())
        {
            UniqueMetadata(item); Fields(item, ["id", "document", "portable", "reason"]);
            var id = String(item, "id", 160); var document = item.GetProperty("document");
            if (!PendingId(id) || !ids.Add(id) || !mappings.TryGetValue(id, out var purpose) ||
                Boolean(item, "portable") != (purpose == "portable"))
                throw new InvalidDataException("Imported entry identity or purpose is inconsistent.");
            _ = String(item, "reason", 4096);
            DocumentCoordinate(document);
        }
        return value.Clone();
    }
    private static void DocumentCoordinate(JsonElement document)
    {
        UniqueMetadata(document);
        Fields(document, ["pluginId", "scope", "sourcePackageVersion", "settingsVersion", "values"], ["scopeId"]);
        if (String(document, "pluginId", 64) != PersonalityPlugin.PluginId || String(document, "scope", 16) != "global" ||
            !NullOrAbsent(document, "scopeId") || Integer(document, "settingsVersion") is < 1 or > int.MaxValue ||
            String(document, "sourcePackageVersion", 64) is not { Length: > 0 })
            throw new InvalidDataException("Unexpected imported preference document coordinate.");
        if (Utf8.GetByteCount(document.GetProperty("values").GetRawText()) > 128 * 1024 || document.GetProperty("values").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Imported preference values require an object.");
    }
    private static IReadOnlyList<PersonalityPendingInput> Inputs(JsonElement transfer)
    {
        var purposes = transfer.GetProperty("pendingPurposes").EnumerateArray().ToDictionary(
            value => value.GetProperty("pendingId").GetString()!, value => value.GetProperty("purpose").GetString()!, StringComparer.Ordinal);
        return transfer.GetProperty("state").GetProperty("pending").EnumerateArray().Select(item =>
        {
            var document = item.GetProperty("document");
            return new PersonalityPendingInput(item.GetProperty("id").GetString()!, purposes[item.GetProperty("id").GetString()!],
                checked((int)document.GetProperty("settingsVersion").GetInt64()), document.GetProperty("sourcePackageVersion").GetString()!, document.Clone());
        }).ToArray();
    }
    private static bool PendingId(string id) => id.Length > 0 && !id.Any(char.IsControl);
    private static JsonElement CurrentValues(JsonElement transfer)
    {
        var state = transfer.GetProperty("state");
        if (!state.TryGetProperty("active", out var active) || active.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("No acknowledged current preference document is available for a local edit.");
        if (Integer(active, "settingsVersion") != 2) throw new InvalidDataException("Current preferences require explicit supported-schema admission.");
        var values = active.GetProperty("values").Clone(); _ = ReadValues(values, Integer(state, "revision")); return values;
    }
    private static bool SamePending(JsonElement before, JsonElement after, string? removed = null)
    {
        var a = before.GetProperty("state").GetProperty("pending").EnumerateArray().Where(item => item.GetProperty("id").GetString() != removed).ToArray();
        var b = after.GetProperty("state").GetProperty("pending").EnumerateArray().ToArray();
        var ap = before.GetProperty("pendingPurposes").EnumerateArray().Where(item => item.GetProperty("pendingId").GetString() != removed).ToArray();
        var bp = after.GetProperty("pendingPurposes").EnumerateArray().ToArray();
        return a.Length == b.Length && ap.Length == bp.Length &&
            a.All(item => b.Count(other => JsonElement.DeepEquals(item, other)) == 1) &&
            ap.All(item => bp.Count(other => JsonElement.DeepEquals(item, other)) == 1);
    }
    private async Task<JsonElement> SaveCurrentTransfer(JsonElement baseline, JsonElement values, DesktopConversationBinding? binding, CancellationToken token)
    {
        var response = await ExecuteTransfer(TransferSave, new { schemaVersion = 2, expectedRevision = Integer(baseline.GetProperty("state"), "revision"), values }, binding, token);
        return await ReadTransferSave(response, baseline, values, null, binding, token);
    }
    private async Task<JsonElement> ReadTransferSave(JsonElement response, JsonElement baseline, JsonElement values, string? removed,
        DesktopConversationBinding? binding, CancellationToken token)
    {
        Fields(response, ["state", "changed"]); var changed = Boolean(response, "changed");
        var transfer = ReadTransfer(response.GetProperty("state"));
        if (Integer(transfer.GetProperty("state"), "revision") != Integer(baseline.GetProperty("state"), "revision") + (changed ? 1 : 0) ||
            removed is not null && !changed || !SamePending(baseline, transfer, removed) ||
            !JsonElement.DeepEquals(CurrentValues(transfer), values))
            throw new InvalidDataException("Preference mutation did not acknowledge the exact values, revision and retained imports.");
        var readback = ReadTransfer(await ExecuteTransfer(TransferGet, new { }, binding, token));
        if (!JsonElement.DeepEquals(transfer, readback))
            throw new InvalidOperationException("Preference state changed before exact mutation readback. Reload; do not replay.");
        return transfer;
    }
}
