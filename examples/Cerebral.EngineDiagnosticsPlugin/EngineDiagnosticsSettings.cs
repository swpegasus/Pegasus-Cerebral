using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.EngineDiagnosticsPlugin;

/// <summary>Pure package policy for desired capture preference. It never starts capture or retention.</summary>
public sealed class EngineDiagnosticsSettingsPolicy : IPluginCompleteSettingsPolicy
{
    public const int SettingsVersion = 1;
    public const int MaximumValuesUtf8Bytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        if (!CoordinateMatches(incoming)) return Reject("diagnostic-settings-coordinate-unsupported");
        if (incoming.SettingsVersion != SettingsVersion) return Reject("diagnostic-settings-version-unsupported");
        if (incoming.SourcePackageVersion is null || !PluginVersion.TryParse(incoming.SourcePackageVersion, out _))
            return Reject("diagnostic-settings-source-version-invalid");
        if (!TryEnabled(incoming.Values, exact: true, out var enabled, out _))
            return Reject("diagnostic-settings-values-invalid");
        // This version declares one portable field and no local-only fields. An unknown current
        // shape is deferred rather than dropping possible newer/private fields on portable merge.
        if (portable && currentValues is { } current && !TryEnabled(current, exact: true, out _, out _))
            return Reject("diagnostic-settings-current-version-unsupported");
        return new(true, SettingsVersion, Values(enabled));
    }

    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    {
        if (!CoordinateMatches(current) || current.SettingsVersion != SettingsVersion ||
            current.SourcePackageVersion is null || !PluginVersion.TryParse(current.SourcePackageVersion, out _) ||
            !TryEnabled(current.Values, exact: false, out var enabled, out var omitted))
            throw new InvalidDataException("Diagnostic settings cannot be projected by this package version.");
        // Only enabled is declared nonsecret. Unknown fields are reported as omitted; their values
        // are neither returned nor removed from the locally stored document.
        return new(SettingsVersion, Values(enabled), omitted);
    }

    public PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current)
    {
        if (!CoordinateMatches(current) || current.SettingsVersion != SettingsVersion ||
            current.SourcePackageVersion is null || !PluginVersion.TryParse(current.SourcePackageVersion, out _) ||
            !TryEnabled(current.Values, exact: true, out var enabled, out _))
            throw new InvalidDataException("Diagnostic complete preferences are unsupported.");
        return new(SettingsVersion, Values(enabled));
    }
    public PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues) =>
        Admit(incoming, currentValues, portable: false);

    /// <summary>Prepare explicit legacy storage translation only; no file read, save, or migration is performed.</summary>
    public static PluginSettingsDocument PrepareLegacy(JsonElement legacy, string sourcePackageVersion)
    {
        if (!PluginVersion.TryParse(sourcePackageVersion, out _) || legacy.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The diagnostic legacy document is unsupported.");
        try
        {
            var names = legacy.EnumerateObject().Select(p => p.Name).ToArray();
            if (Utf8.GetByteCount(legacy.GetRawText()) > MaximumValuesUtf8Bytes || names.Length != 2 ||
                names.Distinct(StringComparer.Ordinal).Count() != 2 ||
                !legacy.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != SettingsVersion ||
                !legacy.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("The diagnostic legacy document is unsupported.");
            return new(EngineDiagnosticsPlugin.Id, SettingsPreferenceContract.GlobalScope, null,
                sourcePackageVersion, SettingsVersion, Values(enabled.GetBoolean()));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("The diagnostic legacy document is unsupported."); }
    }

    internal static JsonElement Values(bool enabled) => JsonSerializer.SerializeToElement(new { enabled });
    private static PluginSettingsAdmission Reject(string reason) =>
        new(false, SettingsVersion, JsonSerializer.SerializeToElement(new { }), reason);
    private static bool CoordinateMatches(PluginSettingsDocument value) =>
        value.PluginId == EngineDiagnosticsPlugin.Id && value.Scope == SettingsPreferenceContract.GlobalScope && value.ScopeId is null;

    private static bool TryEnabled(JsonElement value, bool exact, out bool enabled, out int omitted)
    {
        enabled = false; omitted = 0;
        try
        {
            if (value.ValueKind != JsonValueKind.Object || Utf8.GetByteCount(value.GetRawText()) > MaximumValuesUtf8Bytes) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var found = false;
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) return false;
                if (property.Name == "enabled")
                {
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                    enabled = property.Value.GetBoolean(); found = true;
                }
                else { if (exact) return false; omitted++; }
            }
            return found;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return false; }
    }
}

/// <summary>Example of package-bound central storage use. Calls are data-only; runtime activation is separate.</summary>
public sealed class EngineDiagnosticsSettingsConsumer
{
    private readonly IScopedPluginSettings store;
    private EngineDiagnosticsSettingsConsumer(IScopedPluginSettings store) =>
        this.store = store ?? throw new InvalidOperationException("The package settings store is unavailable.");

    public static EngineDiagnosticsSettingsConsumer? Connect(IServicePluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.PluginId != EngineDiagnosticsPlugin.Id) throw new InvalidOperationException("The diagnostic settings host identity is invalid.");
        return host is IServicePluginSettingsHost central ? new(central.Settings) : null;
    }
    public PluginSettingsState Read() => store is IScopedPluginSettingsTransfer ? ReadTransfer().State : Check(store.Get());
    public PluginSettingsTransferState ReadTransfer()
    {
        if (store is not IScopedPluginSettingsTransfer transfer) throw new NotSupportedException("Version-2 preference transfer is unavailable.");
        return CheckTransfer(transfer.GetTransfer());
    }
    private static PluginSettingsTransferState CheckTransfer(PluginSettingsTransferState value)
    {
        if (value is null || value.State is null || value.PendingPurposes is null || value.SchemaVersion != CompleteSettingsContract.PluginEnvelopeVersion) throw new InvalidDataException("Diagnostic transfer version is invalid.");
        _ = Check(value.State);
        if (value.PendingPurposes.Count != value.State.Pending.Count || value.PendingPurposes.Select(p => p.PendingId).Distinct(StringComparer.Ordinal).Count() != value.PendingPurposes.Count ||
            value.PendingPurposes.Any(p => !value.State.Pending.Any(item => item.Id == p.PendingId) ||
                p.Purpose is not (SettingsTransferPurpose.Local or SettingsTransferPurpose.Portable or SettingsTransferPurpose.Complete) ||
                value.State.Pending.Single(item => item.Id == p.PendingId).Portable != (p.Purpose == SettingsTransferPurpose.Portable)))
            throw new InvalidDataException("Diagnostic transfer purpose is invalid.");
        return value;
    }
    public PluginSettingsSaveResult SaveDesiredEnabled(bool enabled, long expectedRevision)
    {
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        PluginSettingsSaveResult result;
        if (store is IScopedPluginSettingsTransfer transfer)
        {
            var values = EngineDiagnosticsSettingsPolicy.Values(enabled);
            var document = new PluginSettingsDocument(EngineDiagnosticsPlugin.Id, SettingsPreferenceContract.GlobalScope, null,
                "1.0.0", EngineDiagnosticsSettingsPolicy.SettingsVersion, values);
            var saved = transfer.SaveTransfer(new(new(document, expectedRevision, true, values),
                new EngineDiagnosticsSettingsPolicy().ExportComplete(document)));
            var checkedState = CheckTransfer(saved.State);
            result = new(checkedState.State, saved.Changed);
        }
        else result = store.Save(EngineDiagnosticsSettingsPolicy.SettingsVersion,
            EngineDiagnosticsSettingsPolicy.Values(enabled), expectedRevision);
        var state = Check(result.State);
        var admitted = state.Active is { } active ? new EngineDiagnosticsSettingsPolicy().Admit(active, null, false) : null;
        if (admitted is not { Accepted: true } || admitted.Values.GetProperty("enabled").GetBoolean() != enabled ||
            (result.Changed ? state.Revision <= expectedRevision : state.Revision != expectedRevision))
            throw new InvalidDataException("The diagnostic settings save acknowledgement is inconsistent. Inspect before retrying.");
        return result;
    }
    private static PluginSettingsState Check(PluginSettingsState state)
    {
        if (state is null || state.Coordinate is null || state.Pending is null || state.Pending.Count > SettingsPreferenceContract.MaximumPendingPerDocument ||
            state.Pending.Any(item => item is null || item.Document is null || string.IsNullOrWhiteSpace(item.Id)) ||
            state.Pending.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != state.Pending.Count ||
            state.Coordinate.PluginId != EngineDiagnosticsPlugin.Id || state.Coordinate.Scope != SettingsPreferenceContract.GlobalScope ||
            state.Coordinate.ScopeId is not null || state.Revision < 0)
            throw new InvalidDataException("The diagnostic settings reply does not match its package scope.");
        // Deferred schemas remain opaque here; only document storage identity is checked.
        if ((state.Active is { } active && active.Coordinate != state.Coordinate) ||
            state.Pending.Any(pending => pending.Document.Coordinate != state.Coordinate))
            throw new InvalidDataException("The diagnostic settings reply does not match its package scope.");
        return state;
    }
}

// The existing entry point exposes an optional pure policy facet to the loader. Its lifecycle,
// owner capture commands, legacy file writer and activation behavior are otherwise unchanged.
public sealed partial class EngineDiagnosticsPlugin : IPluginCompleteSettingsPolicy
{
    private static readonly EngineDiagnosticsSettingsPolicy SettingsPolicy = new();
    PluginSettingsAdmission IPluginSettingsPolicy.Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable) =>
        SettingsPolicy.Admit(incoming, currentValues, portable);
    PluginSettingsProjection IPluginSettingsPolicy.ExportPortable(PluginSettingsDocument current) =>
        SettingsPolicy.ExportPortable(current);
    PluginSettingsCompleteProjection IPluginCompleteSettingsPolicy.ExportComplete(PluginSettingsDocument current) => SettingsPolicy.ExportComplete(current);
    PluginSettingsAdmission IPluginCompleteSettingsPolicy.AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues) => SettingsPolicy.AdmitComplete(incoming, currentValues);
}
