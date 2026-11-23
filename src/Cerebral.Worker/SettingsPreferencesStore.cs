using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Worker;

internal sealed record SettingsPolicyBinding(string PluginId, string PackageVersion, IPluginSettingsPolicy Policy);

/// <summary>Generic owner state. Packages interpret values; agent values remain in their existing profile backing.</summary>
internal sealed partial class SettingsPreferencesStore(StateStore store,
    Func<IReadOnlyDictionary<string, SettingsPolicyBinding>>? policies = null)
{
    private const string PluginKind = "plugin-settings";
    private const string LayoutKind = "interface-layout";
    private const int MaximumSavedBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false, MaxDepth = 32, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    private sealed record PluginRecord(int SchemaVersion, PluginSettingsState State)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PluginTransferMetadata? Transfer { get; init; }
    }
    private sealed record LayoutRecord(int SchemaVersion, InterfaceLayoutState State);

    internal PluginSettingsState GetPlugin(PluginSettingsCoordinate coordinate)
    {
        ValidateCoordinate(coordinate);
        return store.Locked(() =>
        {
            var transfer = GetTransferCore(coordinate);
            RequireLegacy(transfer);
            return transfer.State;
        });
    }

    internal PluginSettingsSaveResult SavePlugin(PluginSettingsSaveRequest request, bool owner = true)
    {
        var result = SaveTransferCore(new(request), owner, legacy: true);
        return new(result.State.State, result.Changed);
    }

    internal void RemoveAgentScope(string agentId)
    {
        ValidateId(agentId);
        store.Locked(() =>
        {
            var prefix = "agent:" + agentId + ":";
            foreach (var id in store.ListSettingsRecordIds(PluginKind, SettingsPreferenceContract.MaximumDocuments)
                .Where(id => id.StartsWith(prefix, StringComparison.Ordinal)))
                store.Delete(PluginKind, id);
            return true;
        });
    }

    internal void ValidateRestoreCapacity(IEnumerable<PluginSettingsState> states, IEnumerable<InterfaceLayoutState> layouts,
        IEnumerable<PluginSettingsCoordinate>? candidateAgentCoordinates = null) =>
        ValidateRestoreCoordinates(states.Select(FreezeState).Select(state => state.Coordinate).ToArray(),
            layouts.Select(FreezeLayout).ToArray(), candidateAgentCoordinates);

    private void ValidateRestoreCoordinates(IReadOnlyList<PluginSettingsCoordinate> proposed,
        IReadOnlyList<InterfaceLayoutState> proposedLayouts, IEnumerable<PluginSettingsCoordinate>? candidateAgentCoordinates)
    {
        store.Locked(() =>
        {
            var coordinates = AllPluginsCore().Select(state => state.Coordinate).ToHashSet();
            coordinates.UnionWith(proposed);
            if (candidateAgentCoordinates is not null) coordinates.UnionWith(candidateAgentCoordinates);
            var ids = store.ListSettingsRecordIds(LayoutKind, SettingsPreferenceContract.MaximumLayouts).ToHashSet(StringComparer.Ordinal);
            ids.UnionWith(proposedLayouts.Select(layout => layout.ApplicationId));
            if (coordinates.Count > SettingsPreferenceContract.MaximumDocuments || ids.Count > SettingsPreferenceContract.MaximumLayouts)
                throw new InvalidOperationException("Restore exceeds retained settings or layout capacity. No existing scope was evicted.");
            return true;
        });
    }

    internal IReadOnlyList<PluginSettingsState> AllPlugins(bool includeLegacy = true) => store.Locked(() =>
        AllTransfersCore(includeLegacy).Select(transfer => { RequireLegacy(transfer); return transfer.State; }).ToArray());

    internal IReadOnlyList<PluginSettingsPendingSummary> PendingSummaries() => AllTransfers()
        .Select(transfer => transfer.State)
        .SelectMany(state => state.Pending.Select(item => new PluginSettingsPendingSummary(item.Id, state.Coordinate,
            state.Revision, item.Reason, item.Document.SourcePackageVersion, item.Document.SettingsVersion))).ToArray();

    internal PluginSettingsState PlanIncoming(PluginSettingsDocument document, bool portable, PluginSettingsState? planned = null) =>
        PlanIncoming(document, portable, planned, out _);

    internal PluginSettingsState PlanIncoming(PluginSettingsDocument document, bool portable,
        PluginSettingsState? planned, out bool admitted)
    {
        ArgumentNullException.ThrowIfNull(document);
        var current = GetTransfer(document.Coordinate);
        RequireLegacy(current);
        var transfer = PlanTransferIncoming(document, portable ? SettingsTransferPurpose.Portable : SettingsTransferPurpose.Local,
            planned is null ? null : WithLegacyState(current, planned), out admitted);
        return transfer.State;
    }

    internal PluginSettingsState PlanPendingIncoming(PluginSettingsDocument document, bool portable,
        string reason = "retained-pending", PluginSettingsState? planned = null)
    {
        var current = GetTransfer(document.Coordinate);
        RequireLegacy(current);
        return PlanTransferPendingIncoming(document, portable ? SettingsTransferPurpose.Portable : SettingsTransferPurpose.Local,
            reason, planned is null ? null : WithLegacyState(current, planned)).State;
    }

    internal void CommitPluginStates(IEnumerable<PluginSettingsState> states, bool updateAgentBacking = true)
    {
        var transfers = states.Select(state =>
        {
            var current = GetTransfer(state.Coordinate);
            RequireLegacy(current);
            return WithLegacyState(current, state);
        }).ToArray();
        CommitTransferStates(transfers, updateAgentBacking);
    }

    internal InterfaceLayoutState GetLayout(string appId)
    {
        ValidateId(appId);
        return store.Locked(() => GetLayoutCore(appId));
    }

    internal InterfaceLayoutSaveResult SaveLayout(InterfaceLayoutSaveRequest request, bool owner = true)
    {
        RequireOwner(owner);
        ArgumentNullException.ThrowIfNull(request);
        ValidateId(request.ApplicationId);
        if (request.ExpectedRevision < 0 || request.SchemaVersion is < 1 or > 1_000_000)
            throw new ArgumentException("Layout revision or schema version is invalid.");
        var values = FreezeValues(request.Values, SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes);
        return store.Locked<InterfaceLayoutSaveResult>(() =>
        {
            var current = GetLayoutCore(request.ApplicationId);
            if (current.Revision != request.ExpectedRevision)
                throw new InvalidOperationException("Interface layout changed; reload before saving.");
            if (current.SchemaVersion == request.SchemaVersion && current.Values is { } saved && Equal(saved, values))
                return new(FreezeLayout(current), false);
            var state = new InterfaceLayoutState(request.ApplicationId, checked(current.Revision + 1), request.SchemaVersion, values);
            CommitLayouts([state]);
            return new(FreezeLayout(state), true);
        });
    }

    internal IReadOnlyList<InterfaceLayoutState> AllLayouts() => store.Locked(() =>
        store.ListSettingsRecordIds(LayoutKind, SettingsPreferenceContract.MaximumLayouts).Select(GetLayoutCore).ToArray());

    internal void CommitLayouts(IEnumerable<InterfaceLayoutState> states)
    {
        var proposed = states.Select(FreezeLayout).ToArray();
        ValidateUnique(proposed.Select(state => state.ApplicationId), SettingsPreferenceContract.MaximumLayouts);
        store.Locked(() =>
        {
            var ids = store.ListSettingsRecordIds(LayoutKind, SettingsPreferenceContract.MaximumLayouts).ToHashSet(StringComparer.Ordinal);
            foreach (var state in proposed) ids.Add(state.ApplicationId);
            if (ids.Count > SettingsPreferenceContract.MaximumLayouts)
                throw new InvalidOperationException("Interface layout capacity is full.");
            foreach (var state in proposed)
            {
                if (state.SchemaVersion is null || state.Values is null)
                    throw new ArgumentException("A committed layout requires its renderer schema and values.");
                var current = GetLayoutCore(state.ApplicationId);
                var saved = state with { Revision = checked(current.Revision + 1) };
                ValidateSavedSize(new LayoutRecord(1, saved), SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes + 16 * 1024);
                store.Put(LayoutKind, state.ApplicationId, new LayoutRecord(1, saved));
            }
            return true;
        });
    }

    private PluginSettingsState GetPluginCore(PluginSettingsCoordinate coordinate) => GetTransferCore(coordinate).State;

    private IReadOnlyList<PluginSettingsState> AllPluginsCore() => AllPluginsCore(true);

    private IReadOnlyList<PluginSettingsState> AllPluginsCore(bool includeLegacy) =>
        AllTransfersCore(includeLegacy).Select(transfer => transfer.State).ToArray();

    private InterfaceLayoutState GetLayoutCore(string appId)
    {
        var raw = store.ReadSettingsRecord(LayoutKind, appId, SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes + 16 * 1024);
        if (raw is null) return new(appId, 0, null, null);
        var record = ReadSaved<LayoutRecord>(raw.Value);
        if (record.SchemaVersion != 1 || record.State is null || record.State.ApplicationId != appId)
            throw new InvalidDataException("Saved interface layout envelope is invalid.");
        try
        {
            var state = FreezeLayout(record.State);
            if (state.Revision < 1 || state.SchemaVersion is null || state.Values is null)
                throw new ArgumentException();
            return state;
        }
        catch (ArgumentException) { throw new InvalidDataException("Saved interface layout is invalid."); }
    }

    private SettingsPolicyBinding? Binding(string pluginId)
    {
        try
        {
            var available = policies?.Invoke();
            if (available is null || !available.TryGetValue(pluginId, out var binding)) return null;
            if (binding is null || binding.PluginId != pluginId || binding.Policy is null || !ValidVersion(binding.PackageVersion))
                throw new InvalidOperationException();
            return binding;
        }
        catch (Exception error) when (Ordinary(error))
        { throw new InvalidOperationException("Plugin settings policy registry is unavailable."); }
    }

    private static bool TryAdmit(SettingsPolicyBinding binding, PluginSettingsDocument incoming, JsonElement? current,
        bool portable, out PluginSettingsDocument? active, out PluginSettingsProjection? projection)
    {
        active = null; projection = null;
        try
        {
            var admitted = binding.Policy.Admit(FreezeDocument(incoming, legacy: true), current?.Clone(), portable);
            if (admitted is null || !admitted.Accepted || admitted.SettingsVersion is < 1 or > 1_000_000) return false;
            active = new(incoming.PluginId, incoming.Scope, incoming.ScopeId, binding.PackageVersion,
                admitted.SettingsVersion, FreezeValues(admitted.Values, ValuesLimit(incoming.Coordinate)));
            var portableValues = binding.Policy.ExportPortable(FreezeDocument(active, legacy: false));
            if (portableValues is null || portableValues.SettingsVersion != admitted.SettingsVersion ||
                portableValues.OmittedFields is < 0 or > 1_000_000) return false;
            projection = portableValues with { Values = FreezeValues(portableValues.Values, ValuesLimit(incoming.Coordinate)) };
            return true;
        }
        catch (Exception error) when (Ordinary(error)) { active = null; projection = null; return false; }
    }

    internal static InterfaceLayoutState ValidateSnapshotLayout(InterfaceLayoutState state) => FreezeLayout(state);

    internal static PluginSettingsState ValidateSnapshotState(PluginSettingsState state) => FreezeState(state);

    private static PluginSettingsState FreezeState(PluginSettingsState state) => FreezeState(state, null);

    private static PluginSettingsState FreezeState(PluginSettingsState state, IReadOnlyDictionary<string, string>? purposes)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateCoordinate(state.Coordinate);
        if (state.Revision < 0 || state.Pending is null || state.Pending.Count > SettingsPreferenceContract.MaximumPendingPerDocument ||
            state.OmittedPortableFields is < 0 or > 1_000_000)
            throw new ArgumentException("Plugin settings state is invalid.");
        var active = state.Active is null ? null : FreezeDocument(state.Active, legacy: true);
        if (active is not null && active.Coordinate != state.Coordinate)
            throw new ArgumentException("Plugin settings active coordinate is invalid.");
        var pending = state.Pending.Select(item =>
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out var id) || id == Guid.Empty ||
                item.Id != id.ToString("N") || item.Reason is not ("policy-unavailable" or "policy-rejected" or "retained-pending"))
                throw new ArgumentException("Deferred plugin settings metadata is invalid.");
            var document = FreezeDocument(item.Document, legacy: true);
            if (document.Coordinate != state.Coordinate) throw new ArgumentException("Deferred settings coordinate is invalid.");
            return item with { Document = document };
        }).ToArray();
        if (pending.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != pending.Length ||
            state.PendingReason != PendingReason(pending))
            throw new ArgumentException("Deferred plugin settings metadata is invalid.");
        var portable = state.PortableValues is { } values ? FreezeValues(values, ValuesLimit(state.Coordinate)) : (JsonElement?)null;
        for (var index = 0; index < pending.Length; index++)
            if (pending.Take(index).Any(item => Purpose(item, purposes) == Purpose(pending[index], purposes) &&
                SameDocument(item.Document, pending[index].Document)))
                throw new ArgumentException("Deferred plugin settings contain duplicate documents.");
        var validAuthority = state.ValidationAuthority switch
        {
            "none" => active is null && portable is null && state.OmittedPortableFields == 0,
            "unversioned" => active is not null && active.SourcePackageVersion is null && portable is null && state.OmittedPortableFields == 0,
            "service-policy" => active?.SourcePackageVersion is not null && portable is not null,
            "owner-client" => active?.SourcePackageVersion is not null,
            _ => false
        };
        if (!validAuthority) throw new ArgumentException("Plugin settings validation metadata is invalid.");
        return state with { Active = active, Pending = pending, PortableValues = portable };
    }

    private static PluginSettingsDocument FreezeDocument(PluginSettingsDocument document, bool legacy)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateCoordinate(document.Coordinate);
        if (document.SourcePackageVersion is null || document.SettingsVersion is null)
        {
            if (!legacy || document.Scope != SettingsPreferenceContract.AgentScope ||
                document.SourcePackageVersion is not null || document.SettingsVersion is not null)
                throw new ArgumentException("Plugin settings package and schema versions are required.");
        }
        else if (!ValidVersion(document.SourcePackageVersion) || document.SettingsVersion is < 1 or > 1_000_000)
            throw new ArgumentException("Plugin settings package or schema version is invalid.");
        var values = FreezeValues(document.Values, ValuesLimit(document.Coordinate));
        if (document.Scope == SettingsPreferenceContract.AgentScope) Configuration.ValidatePluginSettingsValue(values);
        return document with { Values = values };
    }

    private static InterfaceLayoutState FreezeLayout(InterfaceLayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateId(state.ApplicationId);
        if (state.Revision < 0 || (state.SchemaVersion is null) != (state.Values is null) ||
            state.SchemaVersion is < 1 or > 1_000_000 || state.SchemaVersion is null && state.Revision != 0)
            throw new ArgumentException("Interface layout state is invalid.");
        return state with { Values = state.Values is { } values ?
            FreezeValues(values, SettingsPreferenceContract.MaximumLayoutValuesUtf8Bytes) : null };
    }

    private static JsonElement FreezeValues(JsonElement value, int maximum)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Settings values require a JSON object.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > maximum) throw new ArgumentException("Settings values exceed their byte bound.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            UniqueFields(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new ArgumentException("Settings values contain invalid JSON."); }
    }

    private static void UniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Opaque JSON keys are case-sensitive; package policy may impose stricter semantics.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Settings values contain duplicate fields.");
                UniqueFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueFields(item);
    }

    private static T ReadSaved<T>(JsonElement raw) where T : class
    {
        try { return raw.Deserialize<T>(Json) ?? throw new InvalidDataException("Saved settings envelope is invalid."); }
        catch (JsonException) { throw new InvalidDataException("Saved settings envelope is invalid."); }
    }

    private static void ValidateSavedSize<T>(T value, int maximum)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options);
        if (bytes.Length > maximum) throw new ArgumentException("Saved settings envelope exceeds its byte bound.");
        try { using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new ArgumentException("Saved settings envelope exceeds its JSON depth bound."); }
    }

    private static void ValidateCoordinate(PluginSettingsCoordinate coordinate)
    {
        ArgumentNullException.ThrowIfNull(coordinate);
        ValidateId(coordinate.PluginId);
        if (coordinate.Scope == SettingsPreferenceContract.GlobalScope && coordinate.ScopeId is null) return;
        if (coordinate.Scope == SettingsPreferenceContract.AgentScope && coordinate.ScopeId is not null)
        { ValidateId(coordinate.ScopeId); return; }
        throw new ArgumentException("Plugin settings scope is invalid.");
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Settings identity is invalid.");
    }

    private static string Key(PluginSettingsCoordinate coordinate) => coordinate.Scope == SettingsPreferenceContract.GlobalScope
        ? "global:" + coordinate.PluginId : "agent:" + coordinate.ScopeId + ":" + coordinate.PluginId;
    private static int ValuesLimit(PluginSettingsCoordinate coordinate) => coordinate.Scope == SettingsPreferenceContract.AgentScope
        ? SettingsPreferenceContract.MaximumAgentValuesUtf8Bytes : SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes;
    private static string? PendingReason(IReadOnlyList<PluginSettingsDeferredDocument> pending) => pending.Count == 0 ? null : pending[0].Reason;
    private static bool ValidVersion(string version) => version.Length <= 128 && PluginVersion.TryParse(version, out _);
    private static bool Equal(JsonElement left, JsonElement right) => JsonElement.DeepEquals(left, right);
    private static bool Equal(JsonElement? left, JsonElement? right) => left is null ? right is null : right is not null && Equal(left.Value, right.Value);
    private static bool SameDocument(PluginSettingsDocument? left, PluginSettingsDocument? right) =>
        left is null ? right is null : right is not null && left.Coordinate == right.Coordinate &&
            left.SourcePackageVersion == right.SourcePackageVersion && left.SettingsVersion == right.SettingsVersion && Equal(left.Values, right.Values);
    private static bool SameState(PluginSettingsState left, PluginSettingsState right) => left.Coordinate == right.Coordinate &&
        SameDocument(left.Active, right.Active) && left.ValidationAuthority == right.ValidationAuthority &&
        left.OmittedPortableFields == right.OmittedPortableFields && Equal(left.PortableValues, right.PortableValues) &&
        left.Pending.Count == right.Pending.Count && left.Pending.Zip(right.Pending).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Portable == pair.Second.Portable && pair.First.Reason == pair.Second.Reason &&
            SameDocument(pair.First.Document, pair.Second.Document));
    private static void ValidateUnique(IEnumerable<string> ids, int maximum)
    {
        var values = ids.ToArray();
        if (values.Length > maximum || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Settings commit contains duplicate or excessive coordinates.");
    }
    private static bool Ordinary(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static void RequireOwner(bool owner)
    { if (!owner) throw new UnauthorizedAccessException("Settings and layout changes require the owner interface."); }
}

// Host gates read only this product-owned metadata, never package values or a
// package-supplied policy/getter while holding runtime or callback locks.
internal sealed record ScopedPluginSettingsAdmissionView(long Revision, bool HasActive);

// The admitted host fixes package identity; callers choose only a supported scope and schema.
internal sealed class ScopedPluginSettingsAccess(StateStore store, string pluginId, string packageVersion,
    IPluginSettingsPolicy? policy) : IScopedPluginSettings, IScopedPluginSettingsTransfer
{
    private SettingsPreferencesStore Preferences => new(store, () => policy is null
        ? new Dictionary<string, SettingsPolicyBinding>(StringComparer.Ordinal)
        : new Dictionary<string, SettingsPolicyBinding>(StringComparer.Ordinal)
            { [pluginId] = new(pluginId, packageVersion, policy) });

    // Keep the settings revision read and fixed host metadata decision in one
    // StateStore critical section. This read path does not call package policy.
    internal T WithGlobalStateLocked<T>(Func<ScopedPluginSettingsAdmissionView, T> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return store.Locked(() =>
        {
            // Complete GetTransfer admits valid complete-pending state that the
            // legacy GetPlugin projection intentionally refuses.
            var state = new SettingsPreferencesStore(store)
                .GetTransfer(new(pluginId, SettingsPreferenceContract.GlobalScope)).State;
            return decide(new(state.Revision, state.Active is not null));
        });
    }

    public PluginSettingsState Get(string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null) =>
        Preferences.GetPlugin(new(pluginId, scope, scopeId));
    public PluginSettingsSaveResult Save(int settingsVersion, JsonElement values, long expectedRevision,
        string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null) =>
        Preferences.SavePlugin(new(new(pluginId, scope, scopeId, packageVersion, settingsVersion, values), expectedRevision));

    public PluginSettingsTransferState GetTransfer(string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null) =>
        Preferences.GetTransfer(new(pluginId, scope, scopeId));

    public PluginSettingsTransferSaveResult SaveTransfer(PluginSettingsTransferSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Save);
        ArgumentNullException.ThrowIfNull(request.Save.Document);
        if (request.Save.Document.PluginId != pluginId || request.Save.Document.SourcePackageVersion != packageVersion)
            throw new ArgumentException("Scoped settings must match their bound package identity and version.");
        // A service consumer uses its registered policy; it cannot claim desktop owner-client validation.
        return Preferences.SaveTransfer(request with { CompleteAdmitted = false,
            Save = request.Save with { OwnerValidated = false } });
    }
}
