using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ConfigurationBackups
{
    private const string PresetKind = "configuration-preset";
    private static readonly UTF8Encoding PresetUtf8 = new(false, true);
    private static readonly string[] PresetFacets =
        ["host", "agents", "model-registrations", "prompts", "connections", "skills",
         "interface", "appearance", "plugin-settings", "layouts"];
    // Local raw settings remain in this DB record. Public get/list/preview never return its body.
    private sealed record SavedPreset(int SchemaVersion, SettingsPresetSummary Summary,
        ConfigurationBackupDocument Document, SettingsSnapshot? LocalSettings);

    public SettingsPresetCapabilities DescribePresets(bool owner)
    {
        RequireOwner(owner);
        return new(SettingsPreferenceContract.SchemaVersion, ConfigurationBackupContract.SchemaVersion,
            SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes, SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes,
            SettingsPreferenceContract.MaximumDocuments, SettingsPreferenceContract.MaximumLayouts,
            SettingsPreferenceContract.MaximumPresets, SettingsPreferenceContract.MaximumNameCharacters,
            SettingsPreferenceContract.MaximumNameUtf8Bytes, PresetFacets,
            "configuration.restore.apply", "configuration.restore.status");
    }

    public IReadOnlyList<SettingsPresetSummary> ListPresets(bool owner)
    {
        RequireOwner(owner);
        return store.Locked(() => store.ListSettingsRecordIds(PresetKind, SettingsPreferenceContract.MaximumPresets)
            .Select(id => ReadPreset(id).Summary).OrderBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal).ToArray());
    }

    public SettingsPresetSummary GetPreset(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<SettingsPresetIdRequest>(input);
        return store.Locked(() => ReadPreset(request.Id).Summary);
    }

    public SettingsPresetSummary CapturePreset(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<SettingsPresetCaptureRequest>(input);
        var name = PresetName(request.Id, request.Name, request.ExpectedRevision);
        return store.Locked(() =>
        {
            var previous = OptionalPreset(request.Id);
            CheckPresetRevision(previous, request.ExpectedRevision);
            var current = Capture();
            var attention = Omitted();
            var document = CreateDocument(current, attention, portableSettings: true);
            // Avoid a second raw-values copy: the local snapshot holds every active/deferred document.
            document = document with { Settings = new([], [], current.Settings.Appearance.Clone()) };
            var portableAttention = Omitted();
            var omitted = new HashSet<string>(StringComparer.Ordinal);
            _ = SettingsDocument(current.Settings, portable: true, portableAttention, omitted);
            var coverage = new SettingsPresetCoverage(true, omitted.Count == 0, PresetFacets,
                current.Settings.Plugins.Sum(x => x.Pending.Count), omitted.Count);
            var now = time.GetUtcNow();
            var summary = new SettingsPresetSummary(request.Id, name, checked(request.ExpectedRevision + 1),
                previous?.Summary.CreatedAt ?? now, now, coverage);
            var saved = FitPresetProjectionCaches(new SavedPreset(1, summary, document, current.Settings));
            if (previous?.LocalSettings is not null && previous.Summary.Name == name &&
                SamePresetContent(previous, saved))
                return previous.Summary;
            SavePreset(saved, previous is null);
            return summary;
        });
    }

    public SettingsPresetSummary ImportPreset(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<SettingsPresetImportRequest>(input);
        var name = PresetName(request.Id, request.Name, request.ExpectedRevision);
        return store.Locked(() =>
        {
            var previous = OptionalPreset(request.Id);
            CheckPresetRevision(previous, request.ExpectedRevision);
            var current = Capture(); var attention = Omitted();
            var candidate = Build(new(request.Document), current, attention,
                portableSettings: true, retainUnavailable: true);
            var omitted = new HashSet<string>(StringComparer.Ordinal);
            _ = ProjectImportedPreset(request.Document, attention, omitted);
            var coverage = new SettingsPresetCoverage(request.Document.SchemaVersion >= 14, omitted.Count == 0, IncludedPresetFacets(request.Document),
                candidate.Settings?.Summary.PendingPluginDocuments ?? 0, omitted.Count);
            var now = time.GetUtcNow();
            var saved = new SavedPreset(1, new(request.Id, name, checked(request.ExpectedRevision + 1),
                previous?.Summary.CreatedAt ?? now, now, coverage),
                CerebralJson.Read<ConfigurationBackupDocument>(CerebralJson.Element(request.Document)), null);
            if (previous?.LocalSettings is null && previous is not null &&
                previous.Summary.Name == name && SamePresetContent(previous, saved))
                return previous.Summary;
            SavePreset(saved, previous is null);
            return saved.Summary;
        });
    }

    public ConfigurationBackupExport ExportPreset(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<SettingsPresetExportRequest>(input);
        var purpose = ExportPurpose(request.Purpose);
        return store.Locked(() =>
        {
            var saved = ReadPreset(request.Id);
            if (purpose == SettingsTransferPurpose.Complete) return CompleteExport(saved, CompleteAttention());
            var attention = Omitted();
            var document = saved.LocalSettings is null ? ProjectImportedPreset(saved.Document, attention) :
                saved.Document with { Settings = SettingsDocument(saved.LocalSettings, portable: true, attention) };
            CheckDocumentSize(document);
            return new ConfigurationBackupExport(document, attention);
        });
    }

    public ConfigurationRestorePreview PreviewPreset(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<SettingsPresetPreviewRequest>(input);
        Id(request.Id);
        if (request.ExpectedRevision < 1) throw new ArgumentException("Choose the exact saved preset revision.");
        // Match the existing gate-to-store lock order; no second preview or apply manager.
        lock (gate)
        {
            return store.Locked(() =>
            {
                var saved = ReadPreset(request.Id);
                CheckPresetRevision(saved, request.ExpectedRevision);
                var document = saved.LocalSettings is null ? saved.Document :
                    saved.Document with { Settings = SettingsDocument(saved.LocalSettings, portable: false, []) };
                return PreviewCore(new(document, request.PathOverrides), portableSettings: saved.LocalSettings is null,
                    retainUnavailable: true, presetId: saved.Summary.Id, presetRevision: saved.Summary.Revision, localSettings: saved.LocalSettings);
            });
        }
    }

    private SavedPreset ReadPreset(string id) => OptionalPreset(id) ??
        throw new InvalidOperationException("The named preset is unavailable.");

    private SavedPreset? OptionalPreset(string id)
    {
        Id(id);
        var raw = store.ReadSettingsRecord(PresetKind, id, ConfigurationBackupContract.MaximumUtf8Bytes);
        if (raw is null) return null;
        try
        {
            var saved = raw.Value.Deserialize<SavedPreset>(Json) ?? throw new ArgumentException();
            if (saved.SchemaVersion != 1 || saved.Summary.Id != id || saved.Summary.Revision < 1 ||
                PresetName(id, saved.Summary.Name, 0) != saved.Summary.Name ||
                saved.Summary.CreatedAt > saved.Summary.UpdatedAt ||
                saved.Summary.Coverage is null || saved.Summary.Coverage.IncludedFacets is null ||
                saved.Summary.Coverage.PendingPluginDocuments is < 0 or > 1024 ||
                saved.Summary.Coverage.OmittedPortableDocuments is < 0 or > 5248 ||
                saved.Summary.Coverage.IncludedFacets.Count > PresetFacets.Length ||
                saved.Summary.Coverage.IncludedFacets.Distinct(StringComparer.Ordinal).Count() != saved.Summary.Coverage.IncludedFacets.Count ||
                saved.Summary.Coverage.IncludedFacets.Any(x => !PresetFacets.Contains(x, StringComparer.Ordinal)) ||
                saved.Document.Format != ConfigurationBackupContract.Format ||
                saved.Document.SchemaVersion is < 1 or > CompleteSettingsContract.BackupSchemaVersion)
                throw new ArgumentException();
            _ = FileSettingsPurpose(saved.Document);
            CheckDocumentSize(saved.Document);
            if (saved.LocalSettings is { } snapshot)
            {
                if (snapshot.Plugins is null || snapshot.Layouts is null ||
                    snapshot.Plugins.Length > SettingsPreferenceContract.MaximumDocuments ||
                    snapshot.Layouts.Length > SettingsPreferenceContract.MaximumLayouts)
                    throw new ArgumentException();
                if (snapshot.TransferMetadata is { } metadata &&
                    (metadata.Length > snapshot.Plugins.Length ||
                     metadata.Select(item => item.Coordinate).Distinct().Count() != metadata.Length ||
                     metadata.Any(item => !snapshot.Plugins.Any(state => state.Coordinate == item.Coordinate))))
                    throw new ArgumentException();
                foreach (var state in snapshot.Plugins) _ = SnapshotTransfer(snapshot, state);
                if (snapshot.Plugins.Select(x => x.Coordinate).Distinct().Count() != snapshot.Plugins.Length ||
                    snapshot.Layouts.Select(x => x.ApplicationId).Distinct(StringComparer.Ordinal).Count() != snapshot.Layouts.Length)
                    throw new ArgumentException();
                foreach (var layout in snapshot.Layouts)
                {
                    Id(layout.ApplicationId);
                    if (layout.Revision < 1 || layout.SchemaVersion is null or < 1 ||
                        layout.Values is not { ValueKind: JsonValueKind.Object } values ||
                        JsonSerializer.SerializeToUtf8Bytes(values, Json).Length > SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes)
                        throw new ArgumentException();
                }
                _ = DesktopThemePresetPolicy.Parse(snapshot.Appearance);
            }
            return saved;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("The saved preset is invalid; it was preserved for review."); }
    }

    private void SavePreset(SavedPreset saved, bool creating)
    {
        if (creating && store.ListSettingsRecordIds(PresetKind, SettingsPreferenceContract.MaximumPresets).Length >=
            SettingsPreferenceContract.MaximumPresets)
            throw new InvalidOperationException("Named preset capacity is full.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, CerebralJson.Options);
        if (bytes.Length > ConfigurationBackupContract.MaximumUtf8Bytes)
            throw new InvalidOperationException("Complete preset capture exceeds1MiB. No fields were dropped and no preset was saved.");
        try { using var envelope = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new InvalidOperationException("Complete preset capture exceeds the saved JSON depth bound. No preset was saved."); }
        store.Put(PresetKind, saved.Summary.Id, saved);
    }

    private static IReadOnlyList<string> IncludedPresetFacets(ConfigurationBackupDocument document)
    {
        var facets = new List<string> { "host", "agents", "model-registrations" };
        if (document.SchemaVersion >= 2) facets.Add("prompts");
        if (document.SchemaVersion >= 3) facets.Add("connections");
        if (document.SchemaVersion >= 5) facets.Add("interface");
        if (document.SchemaVersion >= 6) facets.Add("plugin-settings");
        if (document.SchemaVersion >= 8) facets.Add("skills");
        if (document.SchemaVersion >= 14) { facets.Add("appearance"); facets.Add("layouts"); }
        return facets.ToArray();
    }

    private static string PresetName(string id, string name, long revision)
    {
        Id(id);
        if (name is null || revision < 0) throw new ArgumentException("Preset name or revision is invalid.");
        name = name.Trim();
        try
        {
            if (name.Length is < 1 or > SettingsPreferenceContract.MaximumNameCharacters || name.Contains('\0') ||
                PresetUtf8.GetByteCount(name) > SettingsPreferenceContract.MaximumNameUtf8Bytes)
                throw new ArgumentException("Preset name exceeds its character or UTF8 bound.");
        }
        catch (EncoderFallbackException) { throw new ArgumentException("Preset name is not valid Unicode."); }
        return name;
    }

    private static void CheckPresetRevision(SavedPreset? previous, long expected)
    {
        if ((previous?.Summary.Revision ?? 0) != expected)
            throw new InvalidOperationException("The named preset changed. Reload its current revision before saving or restoring.");
    }

    private static bool SamePresetContent(SavedPreset left, SavedPreset right)
    {
        var proposed = right.Document with { CreatedAt = left.Document.CreatedAt };
        return JsonElement.DeepEquals(CerebralJson.Element(left.Document), CerebralJson.Element(proposed)) &&
            JsonElement.DeepEquals(CerebralJson.Element(left.LocalSettings), CerebralJson.Element(right.LocalSettings));
    }
}
