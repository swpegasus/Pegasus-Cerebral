using System.Text.Json;
using System.Security.Cryptography;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ConfigurationBackups
{
    public CompleteSettingsCapabilities DescribeCompleteSettings(bool owner)
    {
        RequireOwner(owner);
        return new(CompleteSettingsContract.SchemaVersion, ConfigurationBackupContract.SchemaVersion,
            CompleteSettingsContract.BackupSchemaVersion, [SettingsTransferPurpose.Portable, SettingsTransferPurpose.Complete],
            "configuration.presets.export", "configuration.backup.export",
            CompleteSettingsContract.PluginGetOperation, CompleteSettingsContract.PluginSaveOperation);
    }

    public ConfigurationBackupExport Export(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<ConfigurationBackupExportRequest>(input);
        if (ExportPurpose(request.Purpose) == SettingsTransferPurpose.Portable) return Export(owner);
        return store.Locked(() => CompleteExport(Capture(), CompleteAttention()));
    }

    private static string ExportPurpose(string? purpose) => purpose switch
    {
        null or SettingsTransferPurpose.Portable => SettingsTransferPurpose.Portable,
        SettingsTransferPurpose.Complete => SettingsTransferPurpose.Complete,
        _ => throw new ArgumentException("Choose Complete settings or Portable preset.")
    };

    private static string FileSettingsPurpose(ConfigurationBackupDocument document)
    {
        if (document.SchemaVersion is < 1 or > CompleteSettingsContract.BackupSchemaVersion)
            throw new ArgumentException("Unsupported configuration backup format or version.");
        if (CompleteSettingsContract.IsCompleteBackupSchemaVersion(document.SchemaVersion))
        {
            if (document.SettingsTransferMode != SettingsTransferPurpose.Complete)
                throw new ArgumentException("Complete settings requires the complete-nonsecret transfer mode.");
            return SettingsTransferPurpose.Complete;
        }
        if (document.SettingsTransferMode is not null)
            throw new ArgumentException("Earlier portable documents cannot carry a settings transfer mode.");
        return SettingsTransferPurpose.Portable;
    }

    private static List<ConfigurationBackupAttention> CompleteAttention()
    {
        var result = Omitted();
        result.RemoveAll(item => item.Section == "plugins");
        result.Add(new("declared-preferences", "plugins", null,
            "Complete settings includes package-declared nonsecret preferences. Packages, enablement, credentials and domain data remain excluded."));
        result.Add(new("renderer-owned", "layouts", null,
            "Saved layouts remain bounded opaque renderer preferences. Their schema is interpreted by the renderer; ordinary text review remains required."));
        return result;
    }

    private ConfigurationBackupExport CompleteExport(Current current, List<ConfigurationBackupAttention> attention)
    {
        var document = CreateDocument(current, attention, portableSettings: false, completeFile: true);
        var facet = CompleteSettingsDocument(current.Settings, attention, out var coverage);
        document = document with { Settings = facet };
        CheckDocumentSize(document);
        return new(document, attention) { CompleteSettingsCoverage = coverage };
    }

    private ConfigurationBackupExport CompleteExport(SavedPreset saved, List<ConfigurationBackupAttention> attention)
    {
        ConfigurationBackupDocument document;
        CompleteSettingsFileCoverage coverage;
        if (saved.LocalSettings is { } snapshot)
        {
            document = saved.Document with
            {
                SchemaVersion = CompleteSettingsContract.BackupSchemaVersion,
                SettingsTransferMode = SettingsTransferPurpose.Complete,
                Agents = saved.Document.Agents.Select(agent => agent with { PluginSettings = [] }).ToArray(),
                Settings = CompleteSettingsDocument(snapshot, attention, out coverage)
            };
        }
        else
        {
            if (FileSettingsPurpose(saved.Document) != SettingsTransferPurpose.Complete)
                throw new InvalidOperationException("This imported Portable preset lacks the original complete preferences. Capture Complete settings from the original source; missing preferences are not filled from defaults.");
            document = ProjectImportedComplete(saved.Document, attention, out coverage);
        }
        CheckDocumentSize(document);
        return new(document, attention) { CompleteSettingsCoverage = coverage };
    }

    private ConfigurationBackupSettings CompleteSettingsDocument(SettingsSnapshot snapshot,
        List<ConfigurationBackupAttention> attention, out CompleteSettingsFileCoverage coverage)
    {
        var entries = new List<ConfigurationBackupSettingsEntry>();
        var excluded = 0;
        foreach (var state in snapshot.Plugins)
        {
            var transfer = SnapshotTransfer(snapshot, state);
            if (state.Active is { } active)
                entries.Add(new("active", ProjectComplete(active, transfer, ref excluded)));
            foreach (var pending in state.Pending)
                entries.Add(new("pending", ProjectComplete(pending.Document, transfer, ref excluded), pending.Id));
        }
        coverage = CompleteCoverage(entries, excluded);
        return new(entries.ToArray(), snapshot.Layouts.Select(layout => layout with { Values = layout.Values?.Clone() }).ToArray(),
            snapshot.Appearance.Clone());
    }

    private ConfigurationBackupDocument ProjectImportedComplete(ConfigurationBackupDocument source,
        List<ConfigurationBackupAttention> attention, out CompleteSettingsFileCoverage coverage)
    {
        _ = FileSettingsPurpose(source);
        if (source.Settings is null || source.Agents.Any(agent => agent.PluginSettings?.Count > 0))
            throw new InvalidOperationException("Complete settings requires one centralized plugin settings facet.");
        var excluded = 0;
        var entries = source.Settings.PluginDocuments.Select(entry =>
            entry with { Document = ProjectComplete(entry.Document, null, ref excluded) }).ToArray();
        coverage = CompleteCoverage(entries, excluded);
        return source with { Settings = source.Settings with { PluginDocuments = entries } };
    }

    private PluginSettingsDocument ProjectComplete(PluginSettingsDocument document,
        PluginSettingsTransferSnapshot? snapshot, ref int excluded)
    {
        var projection = Preferences.ResolveCompleteProjection(document, snapshot);
        if (projection is null || !projection.Complete || document.SettingsVersion is null ||
            !PluginVersion.TryParse(document.SourcePackageVersion, out _) ||
            projection.SettingsVersion != document.SettingsVersion)
        {
            var fields = projection?.UnsupportedFields;
            var suffix = fields is { Count: > 0 and <= 64 } && fields.All(IsFieldIdentifier)
                ? " Review these package fields: " + string.Join(", ", fields) + "." : "";
            throw new InvalidOperationException("Complete settings unavailable for " + document.PluginId +
                ": every active and pending document requires a supported complete nonsecret projection." + suffix);
        }
        CheckSettingValues(projection.Values, document.Scope);
        if (projection.ExcludedFields is < 0 or > 1_000_000)
            throw new InvalidOperationException("Complete settings projection coverage is invalid.");
        excluded = checked(excluded + projection.ExcludedFields);
        return document with { Values = projection.Values.Clone() };
    }

    private static bool IsFieldIdentifier(string value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static CompleteSettingsFileCoverage CompleteCoverage(IEnumerable<ConfigurationBackupSettingsEntry> entries, int excluded)
    {
        var all = entries.ToArray();
        return new(true, all.Count(item => item.Role == "active"), all.Count(item => item.Role == "pending"),
            excluded, "opaque-renderer-state", PresetFacets);
    }

    private static PluginSettingsTransferSnapshot SnapshotTransfer(SettingsSnapshot snapshot, PluginSettingsState state)
    {
        var metadata = snapshot.TransferMetadata?.SingleOrDefault(item => item.Coordinate == state.Coordinate);
        var purposes = metadata?.PendingPurposes ?? state.Pending.Select(item =>
            new PluginSettingsPendingPurpose(item.Id, item.Portable ? SettingsTransferPurpose.Portable : SettingsTransferPurpose.Local)).ToArray();
        return SettingsPreferencesStore.ValidateSnapshotTransfer(new(new(state, purposes), metadata?.CompleteProjections ?? []));
    }

    private static CapturedSettingsTransfer[]? CaptureTransferMetadata(IEnumerable<PluginSettingsTransferSnapshot> transfers)
    {
        var metadata = transfers.Where(item => item.CompleteProjections.Count > 0 ||
            item.Transfer.PendingPurposes.Any(purpose => purpose.Purpose == SettingsTransferPurpose.Complete))
            .Select(item => new CapturedSettingsTransfer(item.Transfer.State.Coordinate,
                item.Transfer.PendingPurposes, item.CompleteProjections)).ToArray();
        return metadata.Length == 0 ? null : metadata;
    }

    // Optional caches never displace captured raw preferences or their mandatory pending purpose.
    private static SavedPreset FitPresetProjectionCaches(SavedPreset saved)
    {
        if (saved.LocalSettings?.TransferMetadata is not { } metadata) return saved;
        var mandatory = metadata.Where(item => item.PendingPurposes.Any(purpose => purpose.Purpose == SettingsTransferPurpose.Complete))
            .Select(item => item with { CompleteProjections = [] }).ToArray();
        var fitted = saved with { LocalSettings = saved.LocalSettings with
            { TransferMetadata = mandatory.Length == 0 ? null : mandatory } };
        if (!PresetFits(fitted)) return fitted; // The ordinary save guard reports an oversized mandatory snapshot.
        foreach (var item in metadata)
        {
            foreach (var cache in item.CompleteProjections)
            {
                var current = fitted.LocalSettings!.TransferMetadata ?? [];
                var previous = current.SingleOrDefault(entry => entry.Coordinate == item.Coordinate);
                var proposed = new CapturedSettingsTransfer(item.Coordinate, item.PendingPurposes,
                    (previous?.CompleteProjections ?? []).Append(cache).ToArray());
                var next = current.Where(entry => entry.Coordinate != item.Coordinate).Append(proposed).ToArray();
                var candidate = fitted with { LocalSettings = fitted.LocalSettings with { TransferMetadata = next } };
                if (PresetFits(candidate)) fitted = candidate;
            }
        }
        return fitted;
    }

    private static bool PresetFits(SavedPreset saved)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, CerebralJson.Options);
        if (bytes.Length > ConfigurationBackupContract.MaximumUtf8Bytes) return false;
        try { using var envelope = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); return true; }
        catch (JsonException) { return false; }
    }

    private static string SettingsMetadataFingerprint(SettingsSnapshot snapshot)
    {
        // Bind private purposes and exact cache envelopes without copying optional Values into the 4MiB baseline.
        var metadata = snapshot.TransferMetadata!.Select(item => new
        {
            item.Coordinate,
            pending = item.PendingPurposes.OrderBy(purpose => purpose.PendingId, StringComparer.Ordinal).ToArray(),
            projections = item.CompleteProjections.OrderBy(cache => cache.SourceDigest, StringComparer.Ordinal).Select(cache => new
            {
                cache.SourceDigest,
                projectionHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(cache.Projection, Json)))
            }).ToArray()
        }).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(metadata, Json)));
    }

    private static PluginSettingsTransferSnapshot[] SnapshotTransfers(SettingsSnapshot snapshot) =>
        snapshot.Plugins.Select(state => SnapshotTransfer(snapshot, state)).ToArray();
}
