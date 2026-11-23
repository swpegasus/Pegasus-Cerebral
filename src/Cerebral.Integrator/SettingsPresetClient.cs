using System.IO;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

/// <summary>One captured owner session. Transport loss retains the submitted preset identity; it never resends a mutation.</summary>
internal sealed class SettingsPresetClient(
    Func<string, object?, CancellationToken, Task<CommandResult>> dispatch, Func<bool> isCurrent,
    string? layoutBackendIdentity = null)
{
    internal RendererLayoutClient LayoutOwner { get; } = new("Integrator", dispatch, isCurrent, layoutBackendIdentity);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly HashSet<string> consumedUncertainMutations = new(StringComparer.Ordinal);
    private readonly List<PresetUncertainty> unconfirmedHistory = [];
    internal IReadOnlyList<PresetUncertainty> UnconfirmedHistory => unconfirmedHistory.AsReadOnly();
    internal bool LastReconciliationWasAbsent { get; private set; }
    internal PresetUncertainty? Uncertainty { get; private set; }
    internal bool IsCurrent => isCurrent();

    internal async Task<SettingsPresetCapabilities> DescribeAsync(CancellationToken token)
    {
        var value = await ReadAsync<SettingsPresetCapabilities>("configuration.presets.describe", new { }, token);
        if (value.SchemaVersion != SettingsPreferenceContract.SchemaVersion ||
            value.BackupSchemaVersion != ConfigurationBackupContract.SchemaVersion || value.MaximumNameCharacters != SettingsPreferenceContract.MaximumNameCharacters ||
            value.MaximumNameUtf8Bytes != SettingsPreferenceContract.MaximumNameUtf8Bytes ||
            value.RestoreApplyOperation != "configuration.restore.apply" || value.RestoreStatusOperation != "configuration.restore.status")
            throw new InvalidOperationException("This service uses an unsupported settings-preset contract.");
        return value;
    }

    internal async Task<SettingsPresetSummary[]> ListAsync(CancellationToken token)
    {
        var values = await ReadAsync<SettingsPresetSummary[]>("configuration.presets.list", new { }, token);
        ValidateList(values);
        return values;
    }

    internal async Task<SettingsPresetSummary> GetAsync(string id, CancellationToken token)
    {
        var value = await ReadAsync<SettingsPresetSummary>("configuration.presets.get", new SettingsPresetIdRequest(id), token);
        Validate(value);
        if (value.Id != id) throw new InvalidDataException("The returned preset identity did not match.");
        return value;
    }

    private bool completeSettingsSupported;

    internal async Task<SettingsExportAvailability> DescribeCompleteSettingsAsync(CancellationToken token)
    {
        RequireCurrent();
        var reply = await dispatch(CompleteSettingsContract.DescribeOperation, new { }, token);
        RequireCurrent();
        completeSettingsSupported = false;
        if (!reply.Success)
        {
            if (reply.Error == "Unknown command: " + CompleteSettingsContract.DescribeOperation || reply.Error == "Unknown configuration operation.")
                return new(false, "Complete settings are unavailable on this Worker.");
            throw new InvalidOperationException(reply.Error ?? "Settings export support could not be read.");
        }
        if (!HasRequiredMembers(reply.Data, ["schemaVersion", "defaultBackupSchemaVersion", "completeBackupSchemaVersion",
            "pluginEnvelopeVersion", "purposes", "presetExportOperation", "backupExportOperation", "pluginGetOperation", "pluginSaveOperation", "layoutCoverage"]))
            return new(false, "Complete settings support could not be verified on this Worker.");
        CompleteSettingsCapabilities? value;
        try { value = reply.Data.Deserialize<CompleteSettingsCapabilities>(CerebralJson.Options); }
        catch (JsonException) { return new(false, "Complete settings support could not be verified on this Worker."); }
        if (value is null || value.SchemaVersion != CompleteSettingsContract.SchemaVersion ||
            value.DefaultBackupSchemaVersion != ConfigurationBackupContract.SchemaVersion ||
            value.CompleteBackupSchemaVersion != CompleteSettingsContract.BackupSchemaVersion || value.PluginEnvelopeVersion != CompleteSettingsContract.PluginEnvelopeVersion ||
            value.Purposes is null || value.Purposes.Count != 2 || value.Purposes.Distinct(StringComparer.Ordinal).Count() != 2 ||
            !value.Purposes.Contains(SettingsTransferPurpose.Portable) || !value.Purposes.Contains(SettingsTransferPurpose.Complete) ||
            value.PresetExportOperation != "configuration.presets.export" || value.BackupExportOperation != "configuration.backup.export" ||
            value.PluginGetOperation != CompleteSettingsContract.PluginGetOperation || value.PluginSaveOperation != CompleteSettingsContract.PluginSaveOperation ||
            value.LayoutCoverage != "opaque-renderer-state")
            return new(false, "Complete settings use an unsupported contract on this Worker.");
        completeSettingsSupported = true;
        return new(true);
    }

    internal Task<ConfigurationBackupExport> ExportAsync(string id, CancellationToken token) =>
        ExportAsync(id, SettingsTransferPurpose.Portable, token);

    internal async Task<ConfigurationBackupExport> ExportAsync(string id, string purpose, CancellationToken token)
    {
        RequireExportPurpose(purpose);
        // Retain the exact legacy ID-only portable request for old owners.
        object request = purpose == SettingsTransferPurpose.Portable ? new SettingsPresetIdRequest(id) : new SettingsPresetExportRequest(id, purpose);
        return await ReadExportAsync("configuration.presets.export", request, purpose, token);
    }

    internal async Task<ConfigurationBackupExport> ExportBackupAsync(string purpose, CancellationToken token)
    {
        RequireExportPurpose(purpose);
        object request = purpose == SettingsTransferPurpose.Portable ? new { } : new ConfigurationBackupExportRequest(purpose);
        return await ReadExportAsync("configuration.backup.export", request, purpose, token);
    }

    private async Task<ConfigurationBackupExport> ReadExportAsync(string operation, object request, string purpose, CancellationToken token)
    {
        var raw = await ReadAsync<JsonElement>(operation, request, token);
        if (purpose == SettingsTransferPurpose.Complete &&
            (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("completeSettingsCoverage", out var coverage) ||
             !HasRequiredMembers(coverage, ["complete", "activePluginDocuments", "pendingPluginDocuments", "excludedFields", "layoutCoverage", "includedFacets"])))
            throw new InvalidDataException("Complete settings coverage was not confirmed. No file was saved.");
        var exported = CerebralJson.Read<ConfigurationBackupExport>(raw);
        SettingsExportReview.Validate(exported, purpose);
        return exported;
    }

    private static bool HasRequiredMembers(JsonElement value, IReadOnlyList<string> names) =>
        value.ValueKind == JsonValueKind.Object && names.All(name => value.TryGetProperty(name, out _) && value.EnumerateObject().Count(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 1);

    private void RequireExportPurpose(string purpose)
    {
        RequireCurrent();
        if (purpose != SettingsTransferPurpose.Portable && purpose != SettingsTransferPurpose.Complete)
            throw new ArgumentException("Select a supported settings export purpose.");
        if (purpose == SettingsTransferPurpose.Complete && !completeSettingsSupported)
            throw new InvalidOperationException("Complete settings support has not been confirmed for this Worker.");
    }

    internal Task<ConfigurationRestorePreview> PreviewAsync(SettingsPresetSummary selected, CancellationToken token) =>
        ReadAsync<ConfigurationRestorePreview>("configuration.presets.preview",
            new SettingsPresetPreviewRequest(selected.Id, selected.Revision), token);

    internal Task<SettingsPresetSummary> CaptureAsync(string id, string name, long revision, CancellationToken token) =>
        MutateAsync("configuration.presets.capture", id, name, revision,
            new SettingsPresetCaptureRequest(id, NormalizeName(name), revision), token);

    internal Task<SettingsPresetSummary> ImportAsync(string id, string name, long revision,
        JsonElement document, CancellationToken token) =>
        MutateAsync("configuration.presets.import", id, name, revision,
            new { id, name = NormalizeName(name), expectedRevision = revision, document = document.Clone() }, token);

    internal async Task<SettingsPresetSummary?> ReconcileAsync(CancellationToken token)
    {
        if (Uncertainty is null) return null;
        return ReconcileFromValidatedList(await ListAsync(token));
    }

    internal SettingsPresetSummary? ReconcileFromValidatedList(SettingsPresetSummary[] values)
    {
        RequireCurrent();
        ValidateList(values);
        if (Uncertainty is not { } submitted) return null;
        var value = values.SingleOrDefault(x => x.Id == submitted.Id);
        if (value is not null && value.Revision < submitted.ExpectedRevision)
            throw new InvalidDataException("The returned preset revision predates the original change.");
        // A complete same-owner list proves current presence/absence only. A late original commit remains possible.
        LastReconciliationWasAbsent = value is null;
        Uncertainty = null;
        return value;
    }

    internal Task<InterfaceLayoutState> ReadLayoutAsync(CancellationToken token) =>
        LayoutOwner.ReadAsync(token);

    internal Task<InterfaceLayoutSaveResult> SaveLayoutAsync(InterfaceLayoutSaveRequest request, CancellationToken token) =>
        LayoutOwner.SaveAsync(request, token);

    private async Task<T> ReadAsync<T>(string operation, object? arguments, CancellationToken token)
    {
        RequireCurrent();
        var result = await dispatch(operation, arguments, token);
        RequireCurrent();
        if (!result.Success)
        {
            if (result.Error == "Unknown command: " + operation)
                throw new SettingsPresetsUnavailableException();
            throw new InvalidOperationException(result.Error ?? "The settings could not be read.");
        }
        return CerebralJson.Read<T>(result.Data);
    }

    private async Task<SettingsPresetSummary> MutateAsync(string operation, string id, string name, long revision,
        object request, CancellationToken token)
    {
        RequireCurrent();
        if (Uncertainty is not null) throw new InvalidOperationException("Refresh the original preset change before making another change.");
        var key = operation + "\n" + id + "\n" + revision;
        if (consumedUncertainMutations.Contains(key))
            throw new InvalidOperationException("The original unconfirmed change will not be sent again. Review current state before creating a new change.");
        if (unconfirmedHistory.Count >= SettingsPreferenceContract.MaximumPresets)
            throw new InvalidOperationException("This window retains too many unconfirmed changes. Inspect the original service before opening a fresh preset session.");
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || revision < 0)
            throw new ArgumentException("A valid preset identity and revision are required.");
        // Everything before this point is local validation. Once dispatched, any missing or invalid reply is uncertain.
        try
        {
            var result = await dispatch(operation, request, token);
            if (!result.Success)
            {
                const string rejected = "settings_preset_rejected:";
                if (result.Error?.StartsWith(rejected, StringComparison.Ordinal) == true)
                    throw new SettingsPresetRejectedException(result.Error[rejected.Length..].Trim());
                throw new InvalidOperationException(result.Error ?? "No successful acknowledgement was received.");
            }
            var saved = CerebralJson.Read<SettingsPresetSummary>(result.Data);
            Validate(saved);
            if (saved.Id != id || saved.Name != normalized ||
                (saved.Revision != checked(revision + 1) && !(revision > 0 && saved.Revision == revision)))
                throw new InvalidDataException("The preset acknowledgement did not match the submitted identity, name or revision.");
            RequireCurrent();
            return saved;
        }
        catch (SettingsPresetRejectedException) { throw; }
        catch (Exception)
        {
            consumedUncertainMutations.Add(key);
            Uncertainty = new(id, normalized, revision);
            unconfirmedHistory.Add(Uncertainty);
            LastReconciliationWasAbsent = false;
            throw new InvalidOperationException("The preset change was not confirmed. Its identity and name are retained. Refresh on the original service; this change will not be resent.");
        }
    }

    internal static string NormalizeName(string name)
    {
        if (name is null) throw new ArgumentException("A preset name is required.");
        var value = name.Trim();
        if (value.Length is 0 or > SettingsPreferenceContract.MaximumNameCharacters || value.Contains('\0') ||
            Utf8.GetByteCount(value) > SettingsPreferenceContract.MaximumNameUtf8Bytes)
            throw new ArgumentException("Use a preset name of 1–128 characters.");
        return value;
    }

    private static void ValidateList(SettingsPresetSummary[] values)
    {
        if (values is null || values.Length > SettingsPreferenceContract.MaximumPresets)
            throw new InvalidDataException("The service returned an invalid preset list.");
        foreach (var value in values) Validate(value);
        if (values.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException("The service returned duplicate preset identities.");
    }

    private static void Validate(SettingsPresetSummary value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 128 || value.Revision <= 0 ||
            value.Name != NormalizeName(value.Name) || value.Coverage is null ||
            value.Coverage.IncludedFacets is null || value.Coverage.PendingPluginDocuments < 0 ||
            value.Coverage.OmittedPortableDocuments < 0)
            throw new InvalidDataException("The service returned invalid preset metadata.");
    }

    private void RequireCurrent()
    {
        if (!isCurrent()) throw new InvalidOperationException("The service connection changed. Refresh presets for the intended service.");
    }

    internal sealed record PresetUncertainty(string Id, string Name, long ExpectedRevision);
}

internal sealed class SettingsPresetsUnavailableException() : InvalidOperationException("Named settings presets are not available on this service.");

internal sealed class SettingsPresetRejectedException(string message) : InvalidOperationException(
    string.IsNullOrWhiteSpace(message) ? "The service rejected this preset change; current settings were preserved." : message);
