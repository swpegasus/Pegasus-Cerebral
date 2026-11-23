using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ConfigurationBackups
{
    // Optional callbacks come only from already-admitted packages. Backup input never supplies code.
    internal Func<IReadOnlyDictionary<string, SettingsPolicyBinding>> SettingsPolicies { get; init; } =
        () => new Dictionary<string, SettingsPolicyBinding>(StringComparer.Ordinal);

    private SettingsPreferencesStore Preferences => new(store, SettingsPolicies);
    private sealed record SettingsSnapshot(PluginSettingsState[] Plugins, InterfaceLayoutState[] Layouts,
        JsonElement Appearance, JsonElement? SavedAppearance)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CapturedSettingsTransfer[]? TransferMetadata { get; init; }
    }
    private sealed record CapturedSettingsTransfer(PluginSettingsCoordinate Coordinate,
        IReadOnlyList<PluginSettingsPendingPurpose> PendingPurposes, IReadOnlyList<PluginSettingsCompleteCache> CompleteProjections);
    private sealed record SettingsPlan(PluginSettingsTransferState[] Transfers, InterfaceLayoutState[] Layouts,
        JsonElement? Appearance, SettingsRestoreSummary Summary, AgentProfile[] AffectedAgents)
    {
        public PluginSettingsTransferSnapshot[]? ProjectionSnapshots { get; init; }
    }

    private ConfigurationBackupDocument CreateDocument(Current current, List<ConfigurationBackupAttention> attention,
        bool portableSettings, bool completeFile = false)
    {
        var schemas = portableSchemas();
        return new(ConfigurationBackupContract.Format, completeFile ? CompleteSettingsContract.BackupSchemaVersion : ConfigurationBackupContract.SchemaVersion,
            typeof(ConfigurationBackups).Assembly.GetName().Version?.ToString() ?? "0.0.0", time.GetUtcNow(),
            Host(current.Host), Paths(current.Host), current.Agents.Select(profile => Agent(profile, schemas, attention, includePluginSettings: !completeFile)).ToArray(),
            current.Models.Select(model => Model(model, attention)).ToArray(), current.Prompts,
            ExportConnections(current.Host, attention), ExportInterfacePreferences(current.InterfacePreferences), current.Skills)
            { Settings = SettingsDocument(current.Settings, portableSettings, attention),
                SettingsTransferMode = completeFile ? SettingsTransferPurpose.Complete : null };
    }

    private SettingsSnapshot CaptureSettings(bool includeLegacy = true)
    {
        var saved = store.ReadSettingsRecord("settings", "appearance-preset", 32 * 1024);
        var appearance = JsonSerializer.SerializeToElement(new InterfaceThemeStore(store).Get().Preset, Json);
        var transfers = Preferences.CaptureTransferSnapshots(includeLegacy).ToArray();
        return new(transfers.Select(item => item.Transfer.State).ToArray(), Preferences.AllLayouts().ToArray(),
            appearance.Clone(), saved?.Clone())
        {
            TransferMetadata = CaptureTransferMetadata(transfers)
        };
    }

    private ConfigurationBackupSettings SettingsDocument(SettingsSnapshot snapshot, bool portable,
        List<ConfigurationBackupAttention> attention, HashSet<string>? omittedDocuments = null)
    {
        var entries = new List<ConfigurationBackupSettingsEntry>();
        foreach (var state in snapshot.Plugins)
        {
            if (state.Active is { } active)
            {
                var partial = false;
                var document = portable ? ProjectSettings(active, state.PortableValues,
                    state.OmittedPortableFields, state.ValidationAuthority, attention, out partial) : active;
                if (portable && (document is null || partial))
                    omittedDocuments?.Add(SettingsEntryKey(active.Coordinate, "active", null));
                // Existing per-agent portable fields are already represented in agents[].pluginSettings.
                if (document is not null) entries.Add(new("active", document));
            }
            foreach (var pending in state.Pending)
            {
                var partial = false;
                // Input origin never proves that opaque values are safe to export.
                var document = !portable ? pending.Document :
                    ProjectSettings(pending.Document, null, 0, "none", attention, out partial);
                if (portable && (document is null || partial))
                    omittedDocuments?.Add(SettingsEntryKey(pending.Document.Coordinate, "pending", pending.Id));
                if (document is not null)
                    entries.Add(new("pending", document, pending.Id));
            }
        }
        return new(entries.ToArray(), snapshot.Layouts.Select(x => x with
            { Values = x.Values?.Clone() }).ToArray(), snapshot.Appearance.Clone());
    }

    private PluginSettingsDocument? ProjectSettings(PluginSettingsDocument document, JsonElement? ownerProjection,
        int omittedFields, string authority, List<ConfigurationBackupAttention> attention, out bool partial)
    {
        partial = false;
        PluginSettingsProjection? projection = null;
        string? packageVersion = document.SourcePackageVersion;
        try
        {
            if (SettingsPolicies().TryGetValue(document.PluginId, out var binding))
            {
                projection = binding.Policy.ExportPortable(document);
                packageVersion = binding.PackageVersion;
            }
            else if (authority == "owner-client" && ownerProjection is { } values &&
                document.SettingsVersion is { } version && packageVersion is not null)
                projection = new(version, values.Clone(), omittedFields);
            else if (document.Scope == SettingsPreferenceContract.AgentScope)
            {
                // This compatibility projection still uses the package's existing nonsecret declaration.
                if (portableSchemas().TryGetValue(document.PluginId, out var schema))
                {
                    if (document.SettingsVersion is { } sourceSchema && sourceSchema != schema.Version)
                        throw new ArgumentException("The installed portable declaration cannot migrate this settings version.");
                    var legacyValues = PortableProjection(document.Values, schema, exact: false, out var omitted);
                    projection = new(schema.Version, legacyValues, omitted);
                    packageVersion = schema.PackageVersion;
                }
                else throw new ArgumentException("No installed nonsecret portable declaration is available.");
            }
            if (projection is null) throw new ArgumentException();
            CheckSettingValues(projection.Values, document.Scope);
            if (projection.SettingsVersion is < 1 or > 1_000_000 || projection.OmittedFields is < 0 or > 100_000 ||
                !PluginVersion.TryParse(packageVersion, out _)) throw new ArgumentException();
            partial = projection.OmittedFields > 0;
            if (partial)
                attention.Add(new("plugin-fields-omitted", "plugin-settings", document.PluginId,
                    "Package-owned portable projection omitted locally owned fields."));
            return document with { SourcePackageVersion = packageVersion,
                SettingsVersion = projection.SettingsVersion, Values = projection.Values.Clone() };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            attention.Add(new("plugin-document-omitted", "plugin-settings", document.PluginId,
                "No usable explicitly nonsecret portable projection is available for this document."));
            return null;
        }
    }


    private ConfigurationBackupDocument ProjectImportedPreset(ConfigurationBackupDocument source,
        List<ConfigurationBackupAttention> attention, HashSet<string>? omittedDocuments = null)
    {
        var agents = source.Agents.Select(agent => agent with
        {
            PluginSettings = agent.PluginSettings is null ? null : agent.PluginSettings.Select(setting =>
            {
                var document = new PluginSettingsDocument(setting.PluginId, SettingsPreferenceContract.AgentScope,
                    agent.Id, setting.SourcePackageVersion, setting.SettingsVersion, setting.Values);
                var projected = ProjectSettings(document, null, 0, "none", attention, out var partial);
                if (projected is null || partial)
                    omittedDocuments?.Add(SettingsEntryKey(document.Coordinate, "legacy-agent", null));
                return projected is null ? null : new ConfigurationBackupPluginSettings(projected.PluginId,
                    projected.SourcePackageVersion!, projected.SettingsVersion!.Value, projected.Values);
            }).OfType<ConfigurationBackupPluginSettings>().ToArray()
        }).ToArray();
        var facet = source.Settings;
        if (facet is not null)
            facet = facet with
            {
                PluginDocuments = facet.PluginDocuments.Select(entry =>
                {
                    var projected = ProjectSettings(entry.Document, null, 0, "none", attention, out var partial);
                    if (projected is null || partial)
                        omittedDocuments?.Add(SettingsEntryKey(entry.Document.Coordinate, entry.Role, entry.PendingId));
                    return projected is null ? null : entry with { Document = projected };
                }).OfType<ConfigurationBackupSettingsEntry>().ToArray()
            };
        return source with { Agents = agents, Settings = facet,
            SchemaVersion = CompleteSettingsContract.IsCompleteBackupSchemaVersion(source.SchemaVersion)
                ? ConfigurationBackupContract.SchemaVersion : source.SchemaVersion,
            SettingsTransferMode = null };
    }

    private static string SettingsEntryKey(PluginSettingsCoordinate coordinate, string role, string? id) =>
        JsonSerializer.Serialize(coordinate, Json) + "/" + role + "/" + id;

    private static void CheckSettingValues(JsonElement values, string scope)
    {
        var maximum = scope == SettingsPreferenceContract.AgentScope
            ? SettingsPreferenceContract.MaximumAgentValuesUtf8Bytes : SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes;
        if (values.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(values, Json).Length > maximum)
            throw new ArgumentException("Plugin settings exceed the declared scope bound.");
        RejectDuplicateFields(values, opaque: true);
    }

    private SettingsPlan? BuildSettings(ConfigurationBackupDocument document, Current current,
        AgentProfile[] agents, List<ConfigurationBackupSettingsEntry> retainedAgentSettings,
        List<ConfigurationBackupAttention> attention, string purpose, SettingsSnapshot? localSettings = null)
    {
        if (document.SchemaVersion < 14 && document.Settings is not null)
            throw new ArgumentException("Earlier configuration versions cannot carry centralized settings.");
        if (document.SchemaVersion >= 14 && document.Settings is null)
            throw new ArgumentException("Schema14 requires the centralized settings facet.");
        if (document.Settings is null && retainedAgentSettings.Count == 0)
        {
            Preferences.ValidateRestoreCapacity([], [], CandidateAgentCoordinates(agents));
            return null;
        }
        var facet = document.Settings;
        if (facet is not null && (facet.PluginDocuments is null || facet.Layouts is null ||
            facet.PluginDocuments.Count > SettingsPreferenceContract.MaximumDocuments *
                (SettingsPreferenceContract.MaximumPendingPerDocument + 1) ||
            facet.Layouts.Count > SettingsPreferenceContract.MaximumLayouts))
            throw new ArgumentException("Centralized settings exceed their entry bounds.");
        var source = (facet?.PluginDocuments ?? []).Concat(retainedAgentSettings).ToArray();
        var planned = new Dictionary<PluginSettingsCoordinate, PluginSettingsTransferState>();
        var activeSeen = new HashSet<PluginSettingsCoordinate>();
        var admittedCoordinates = new HashSet<PluginSettingsCoordinate>();
        var pendingSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in source)
        {
            if (entry is null || entry.Document is null || entry.Role is not ("active" or "pending"))
                throw new ArgumentException("A centralized settings role is unsupported.");
            var incoming = entry.Document;
            CheckSettingValues(incoming.Values, incoming.Scope);
            if (purpose != SettingsTransferPurpose.Local && (incoming.SourcePackageVersion is null || incoming.SettingsVersion is null))
                throw new ArgumentException("Transferred plugin settings require explicit package and schema versions.");
            if (incoming.Scope == SettingsPreferenceContract.AgentScope &&
                !agents.Concat(current.Agents).Any(x => x.Id == incoming.ScopeId))
                throw new ArgumentException("A plugin settings scope references an unavailable agent.");
            if (entry.Role == "active" && (!activeSeen.Add(incoming.Coordinate) || entry.PendingId is not null))
                throw new ArgumentException("Active plugin settings coordinates must be unique.");
            if (entry.Role == "pending" && (entry.PendingId is null ||
                !Guid.TryParseExact(entry.PendingId, "N", out _) ||
                !pendingSeen.Add(JsonSerializer.Serialize(incoming.Coordinate, Json) + "/" + entry.PendingId)))
                throw new ArgumentException("Pending plugin settings coordinates must be unique.");
            planned.TryGetValue(incoming.Coordinate, out var previous);
            if (entry.Role == "pending")
            {
                // Only a private local snapshot carries origin. Public/imported roles cannot claim it.
                var legacyPortable = retainedAgentSettings.Contains(entry);
                var origin = legacyPortable ? SettingsTransferPurpose.Portable : purpose; var reason = "retained-pending";
                if (localSettings is not null && !legacyPortable)
                {
                    var captured = localSettings.Plugins.SingleOrDefault(x => x.Coordinate == incoming.Coordinate)?
                        .Pending.SingleOrDefault(x => x.Id == entry.PendingId);
                    if (captured is null ||
                        !JsonElement.DeepEquals(CerebralJson.Element(captured.Document), CerebralJson.Element(incoming)))
                        throw new InvalidDataException("The local preset deferred document differs from its captured origin.");
                    var capturedState = localSettings.Plugins.Single(x => x.Coordinate == incoming.Coordinate);
                    origin = SnapshotTransfer(localSettings, capturedState).Transfer.PendingPurposes.Single(x => x.PendingId == captured.Id).Purpose;
                    reason = captured.Reason;
                }
                planned[incoming.Coordinate] = Preferences.PlanTransferPendingIncoming(incoming, origin, reason, previous);
            }
            else
            {
                planned[incoming.Coordinate] = Preferences.PlanTransferIncoming(incoming, purpose, previous, out var admitted);
                if (admitted) admittedCoordinates.Add(incoming.Coordinate);
            }
        }
        if (planned.Count > SettingsPreferenceContract.MaximumDocuments)
            throw new ArgumentException("Centralized settings exceed128 scoped documents.");
        var affectedAgents = new Dictionary<string, AgentProfile>(StringComparer.Ordinal);
        foreach (var coordinate in planned.Keys.Where(x => x.Scope == SettingsPreferenceContract.AgentScope).ToArray())
        {
            var transfer = planned[coordinate];
            var state = transfer.State;
            var id = coordinate.ScopeId!;
            var profile = affectedAgents.GetValueOrDefault(id) ??
                agents.FirstOrDefault(x => x.Id == id) ?? current.Agents.First(x => x.Id == id);
            profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            if (admittedCoordinates.Contains(coordinate) && state.Active is { } active)
                profile.PluginSettings[active.PluginId] = active.Values.Clone();
            else
            {
                // A pending generic document must not undo the existing declared per-agent portable merge.
                var has = profile.PluginSettings.TryGetValue(coordinate.PluginId, out var values);
                if (!has)
                    state = state with { Active = null, ValidationAuthority = "none", PortableValues = null, OmittedPortableFields = 0 };
                else if (state.Active is null || !JsonElement.DeepEquals(state.Active.Values, values))
                    state = state with { Active = new(coordinate.PluginId, coordinate.Scope, coordinate.ScopeId,
                        null, null, values.Clone()), ValidationAuthority = "unversioned",
                        PortableValues = null, OmittedPortableFields = 0 };
                planned[coordinate] = transfer with { State = state };
            }
            if (profile.PluginSettings.Count > 32)
                throw new ArgumentException("An agent has more than32 plugin settings documents.");
            foreach (var value in profile.PluginSettings.Values) Configuration.ValidatePluginSettingsValue(value);
            affectedAgents[id] = profile;
        }
        foreach (var pair in affectedAgents)
        {
            var index = Array.FindIndex(agents, x => x.Id == pair.Key);
            if (index >= 0) agents[index] = pair.Value;
            // Candidate construction appends these existing profiles outside this helper.
        }
        var layouts = facet?.Layouts.Select(SettingsPreferencesStore.ValidateSnapshotLayout).ToArray() ?? [];
        if (layouts.Select(x => x.ApplicationId).Distinct(StringComparer.Ordinal).Count() != layouts.Length ||
            layouts.Any(x => x.Values is null || x.SchemaVersion is null || x.Revision < 0))
            throw new ArgumentException("Saved layout entries require unique complete states.");
        JsonElement? appearance = null;
        if (facet is not null)
            appearance = JsonSerializer.SerializeToElement(DesktopThemePresetPolicy.Parse(facet.AppearancePreset), Json);
        Preferences.ValidateTransferRestoreCapacity(planned.Values, layouts,
            CandidateAgentCoordinates(agents.Concat(affectedAgents.Values)));
        var pendingCount = planned.Values.Sum(x => x.State.Pending.Count);
        foreach (var state in planned.Values.Select(x => x.State).Where(x => x.Pending.Count > 0))
            attention.Add(new("plugin-settings-pending", "plugin-settings", state.Coordinate.PluginId,
                "Incoming settings remain retained pending explicit package-owned admission."));
        return new(planned.Values.ToArray(), layouts, appearance,
            new(admittedCoordinates.Count, pendingCount, layouts.Length, appearance is not null),
            affectedAgents.Values.ToArray())
        { ProjectionSnapshots = localSettings is null ? null : SnapshotTransfers(localSettings) };
    }

    private static IEnumerable<PluginSettingsCoordinate> CandidateAgentCoordinates(IEnumerable<AgentProfile> agents) =>
        agents.SelectMany(agent => agent.PluginSettings.Keys.Select(pluginId =>
            new PluginSettingsCoordinate(pluginId, SettingsPreferenceContract.AgentScope, agent.Id)));

    private void CommitSettings(SettingsPlan? plan)
    {
        if (plan is null) return;
        Preferences.CommitTransferStates(plan.Transfers, updateAgentBacking: false, projectionSnapshots: plan.ProjectionSnapshots);
        Preferences.CommitLayouts(plan.Layouts);
        if (plan.Appearance is { } appearance)
        {
            var current = new InterfaceThemeStore(store).Get();
            _ = new InterfaceThemeStore(store).Set(CerebralJson.Element(new
                { expectedRevision = current.Revision, preset = appearance }));
        }
    }

    private static void KeepUnavailableAgentSettings(List<ConfigurationBackupSettingsEntry>? retained,
        ConfigurationBackupAgent agent, ConfigurationBackupPluginSettings setting)
    {
        retained?.Add(new("pending", new(setting.PluginId, SettingsPreferenceContract.AgentScope,
            agent.Id, setting.SourcePackageVersion, setting.SettingsVersion, setting.Values.Clone()),
            Guid.NewGuid().ToString("N")));
    }
}
