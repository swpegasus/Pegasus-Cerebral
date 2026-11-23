using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Core;

/// <summary>Transfer purpose describes declared settings fields, never trust or automatic text scrubbing.</summary>
public static class SettingsTransferPurpose
{
    public const string Portable = "portable";
    public const string Complete = "complete-nonsecret";
    public const string Local = "local";
}

public static class CompleteSettingsContract
{
    // Portable output remains schema14. Complete schema16 adds owner task-budget policy;
    // older complete schema15 remains readable without inventing an opt-in grant.
    public const int SchemaVersion = 1;
    public const int BackupSchemaVersion = 16;
    public static bool IsCompleteBackupSchemaVersion(int version) =>
        version is >= 15 and <= BackupSchemaVersion;
    public const int PluginEnvelopeVersion = 2;
    public const string DescribeOperation = "configuration.settings.describe";
    public const string PluginGetOperation = "settings.plugins.transfer.get";
    public const string PluginSaveOperation = "settings.plugins.transfer.save";
}

/// <summary>Optional pure package capability; the existing portable policy and host ABI are unchanged.</summary>
public interface IPluginCompleteSettingsPolicy : IPluginSettingsPolicy
{
    PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current);
    PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues);
}

/// <summary>
/// Values contain every package-declared nonsecret preference for this schema.
/// ExcludedFields counts intentional credential/domain exclusions; Complete=false cannot produce a complete file.
/// The package owns classification and migration. Free-form values still require ordinary text review.
/// </summary>
public sealed record PluginSettingsCompleteProjection(int SettingsVersion, JsonElement Values,
    int ExcludedFields = 0, bool Complete = true)
{
    /// <summary>Field identifiers only; no values, URLs, exception messages or credential material.</summary>
    public IReadOnlyList<string> UnsupportedFields { get; init; } = [];
}

/// <summary>Purpose comes from retained service state. A file cannot assert private local-snapshot provenance.</summary>
public sealed record PluginSettingsPendingPurpose(string PendingId, string Purpose);

/// <summary>
/// Capability-gated envelope. State retains its existing shape; every pending item has one explicit purpose.
/// Legacy get/save must reject a coordinate whose pending purpose cannot be represented by the legacy Boolean.
/// </summary>
public sealed record PluginSettingsTransferState(PluginSettingsState State,
    IReadOnlyList<PluginSettingsPendingPurpose> PendingPurposes)
{
    public int SchemaVersion { get; init; } = CompleteSettingsContract.PluginEnvelopeVersion;
}

/// <summary>Owner-client complete projection is package-produced; it never proves service-policy admission.</summary>
public sealed record PluginSettingsTransferSaveRequest(PluginSettingsSaveRequest Save,
    PluginSettingsCompleteProjection? CompleteProjection = null)
{
    /// <summary>
    /// An owner-client package reports actual AdmitComplete success for the selected complete pending item.
    /// This does not assert exportability of merged local state and is never accepted as file provenance.
    /// A registered service policy supplies its own admission instead of trusting this declaration.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CompleteAdmitted { get; init; }
}
public sealed record PluginSettingsTransferSaveResult(PluginSettingsTransferState State, bool Changed);

/// <summary>Optional service-package view of the same transfer envelopes; existing host and scoped ABI stay intact.</summary>
public interface IScopedPluginSettingsTransfer
{
    PluginSettingsTransferState GetTransfer(string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null);
    PluginSettingsTransferSaveResult SaveTransfer(PluginSettingsTransferSaveRequest request);
}

/// <summary>Omitted purpose selects the existing portable behavior.</summary>
public sealed record SettingsPresetExportRequest(string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Purpose = null);
public sealed record ConfigurationBackupExportRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Purpose = null);

/// <summary>Complete concerns declared settings coverage. Layout values remain opaque renderer-owned state.</summary>
public sealed record CompleteSettingsFileCoverage(bool Complete, int ActivePluginDocuments,
    int PendingPluginDocuments, int ExcludedFields, string LayoutCoverage,
    IReadOnlyList<string> IncludedFacets);

/// <summary>Separate support from the unchanged default portable backup schema.</summary>
public sealed record CompleteSettingsCapabilities(int SchemaVersion, int DefaultBackupSchemaVersion,
    int CompleteBackupSchemaVersion, IReadOnlyList<string> Purposes,
    string PresetExportOperation, string BackupExportOperation,
    string PluginGetOperation, string PluginSaveOperation,
    string LayoutCoverage = "opaque-renderer-state")
{
    public int PluginEnvelopeVersion { get; init; } = CompleteSettingsContract.PluginEnvelopeVersion;
}
