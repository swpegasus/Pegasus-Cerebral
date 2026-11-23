using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Core;

/// <summary>Generic envelopes only. Packages and renderers own the schemas inside Values.</summary>
public static class SettingsPreferenceContract
{
    public const int SchemaVersion = 1;
    public const int MaximumPluginValuesUtf8Bytes = 128 * 1024;
    public const int MaximumAgentValuesUtf8Bytes = 64 * 1024;
    public const int MaximumPendingPerDocument = 8;
    public const int MaximumLayoutValuesUtf8Bytes = 64 * 1024;
    public const int MaximumDocuments = 128;
    public const int MaximumLayouts = 32;
    public const int MaximumPresets = 128;
    public const int MaximumNameCharacters = 128;
    public const int MaximumNameUtf8Bytes = 512;
    public const string GlobalScope = "global";
    public const string AgentScope = "agent";
}

public sealed record PluginSettingsCoordinate(string PluginId, string Scope, string? ScopeId = null);

/// <summary>Null package/schema version is reserved for retained, unversioned legacy agent settings.</summary>
public sealed record PluginSettingsDocument(string PluginId, string Scope, string? ScopeId,
    string? SourcePackageVersion, int? SettingsVersion, JsonElement Values)
{
    [JsonIgnore]
    public PluginSettingsCoordinate Coordinate => new(PluginId, Scope, ScopeId);
}

/// <summary>Active values and a deferred incoming document are distinct. Revision changes on either change.</summary>
public sealed record PluginSettingsState(PluginSettingsCoordinate Coordinate, long Revision,
    PluginSettingsDocument? Active, IReadOnlyList<PluginSettingsDeferredDocument> Pending, string ValidationAuthority,
    JsonElement? PortableValues = null, int OmittedPortableFields = 0, string? PendingReason = null);

public sealed record PluginSettingsDeferredDocument(string Id, PluginSettingsDocument Document,
    bool Portable, string Reason);

/// <summary>Owner-client projection is supplied by the desktop package; it never proves service validation.</summary>
public sealed record PluginSettingsSaveRequest(PluginSettingsDocument Document, long ExpectedRevision,
    bool OwnerValidated = false, JsonElement? PortableValues = null, int OmittedPortableFields = 0)
{
    /// <summary>An explicit successful package admission removes only this deferred document.</summary>
    public string? PendingId { get; init; }
}
public sealed record PluginSettingsSaveResult(PluginSettingsState State, bool Changed);
public sealed record PluginSettingsPendingSummary(string Id, PluginSettingsCoordinate Coordinate, long Revision,
    string Reason, string? SourcePackageVersion, int? SettingsVersion);

/// <summary>Pure package callback. No filesystem, network, lifecycle, UI or secret-store effects.</summary>
public interface IPluginSettingsPolicy
{
    PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable);
    PluginSettingsProjection ExportPortable(PluginSettingsDocument current);
}
public sealed record PluginSettingsAdmission(bool Accepted, int SettingsVersion, JsonElement Values,
    string? Reason = null);
/// <summary>Values contain only explicitly nonsecret portable settings. Omitted fields stay locally owned.</summary>
public sealed record PluginSettingsProjection(int SettingsVersion, JsonElement Values, int OmittedFields = 0);

/// <summary>Optional package-bound database access; the existing IServicePluginHost ABI is unchanged.</summary>
public interface IServicePluginSettingsHost
{
    IScopedPluginSettings Settings { get; }
}
public interface IScopedPluginSettings
{
    PluginSettingsState Get(string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null);
    PluginSettingsSaveResult Save(int settingsVersion, JsonElement values, long expectedRevision,
        string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null);
}

public sealed record InterfaceLayoutGetRequest(string ApplicationId);
/// <summary>Revision zero with null schema/values means absent; saved empty object is an explicit renderer reset.</summary>
public sealed record InterfaceLayoutState(string ApplicationId, long Revision, int? SchemaVersion, JsonElement? Values);
public sealed record InterfaceLayoutSaveRequest(string ApplicationId, long ExpectedRevision,
    int SchemaVersion, JsonElement Values);
public sealed record InterfaceLayoutSaveResult(InterfaceLayoutState State, bool Changed);

/// <summary>Declared settings facet. File schema/purpose selects projection; package-owned consumers admit pending documents.</summary>
/// <summary>Role is active or pending. Pending IDs distinguish bounded retained inputs, never authority.</summary>
public sealed record ConfigurationBackupSettingsEntry(string Role, PluginSettingsDocument Document,
    string? PendingId = null);
public sealed record ConfigurationBackupSettings(IReadOnlyList<ConfigurationBackupSettingsEntry> PluginDocuments,
    IReadOnlyList<InterfaceLayoutState> Layouts, JsonElement AppearancePreset);

public sealed record SettingsRestoreSummary(int AppliedPluginDocuments, int PendingPluginDocuments,
    int RestoredLayouts, bool AppearancePresetRestored);
public sealed record SettingsPresetCoverage(bool CapturedComplete, bool PortableComplete,
    IReadOnlyList<string> IncludedFacets, int PendingPluginDocuments, int OmittedPortableDocuments);
public sealed record SettingsPresetSummary(string Id, string Name, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, SettingsPresetCoverage Coverage);
public sealed record SettingsPresetIdRequest(string Id);
public sealed record SettingsPresetCaptureRequest(string Id, string Name, long ExpectedRevision);
public sealed record SettingsPresetImportRequest(string Id, string Name, long ExpectedRevision,
    ConfigurationBackupDocument Document);
public sealed record SettingsPresetPreviewRequest(string Id, long ExpectedRevision,
    ConfigurationRestorePathOverrides? PathOverrides = null);
public sealed record SettingsPresetCapabilities(int SchemaVersion, int BackupSchemaVersion,
    int MaximumPluginValuesUtf8Bytes, int MaximumLayoutValuesUtf8Bytes, int MaximumDocuments,
    int MaximumLayouts, int MaximumPresets, int MaximumNameCharacters, int MaximumNameUtf8Bytes,
    IReadOnlyList<string> IncludedFacets, string RestoreApplyOperation, string RestoreStatusOperation,
    string LayoutCapability = "opaque-renderer-state", bool DockingImplemented = false)
{
    public int MaximumAgentValuesUtf8Bytes { get; init; } = SettingsPreferenceContract.MaximumAgentValuesUtf8Bytes;
    public int MaximumPendingPerDocument { get; init; } = SettingsPreferenceContract.MaximumPendingPerDocument;
}
