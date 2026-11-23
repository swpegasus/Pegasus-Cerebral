using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Owner-only declared settings transfer. No file provisioning, credentials or plugin lifecycle effects.</summary>
public sealed partial class ConfigurationBackups(StateStore store, Func<bool> isBusy, Action? onApplied = null, TimeProvider? clock = null,
    Func<IReadOnlyDictionary<string, ServicePluginPortableSettingsBinding>>? pluginSettingsSchemas = null)
{
    private const int MaximumPortablePluginSettingsBytes = 65_536;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false, PropertyNameCaseInsensitive = false, MaxDepth = 32,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly object gate = new();
    private readonly Dictionary<string, Candidate> previews = new(StringComparer.Ordinal);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Func<IReadOnlyDictionary<string, ServicePluginPortableSettingsBinding>> portableSchemas =
        pluginSettingsSchemas ?? (() => new Dictionary<string, ServicePluginPortableSettingsBinding>(StringComparer.Ordinal));

    public ConfigurationBackupExport Export(bool owner)
    {
        RequireOwner(owner);
        return store.Locked(() =>
        {
            // Legacy agent projection keeps its existing omission behavior; complete local capture is strict.
            var current = Capture(includeLegacySettings: false); var attention = Omitted();
            var document = CreateDocument(current, attention, portableSettings: true);
            CheckDocumentSize(document);
            return new ConfigurationBackupExport(document, attention);
        });
    }

    public ConfigurationRestorePreview Preview(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<ConfigurationRestorePreviewRequest>(input);
        return PreviewCore(request, portableSettings: true, retainUnavailable: request.Document.SchemaVersion >= 14);
    }

    private ConfigurationRestorePreview PreviewCore(ConfigurationRestorePreviewRequest request, bool portableSettings,
        bool retainUnavailable, string? presetId = null, long presetRevision = 0, SettingsSnapshot? localSettings = null)
    {
        lock (gate)
        {
            Expire();
            if (previews.Count >= 8) throw new InvalidOperationException("Eight restore previews are already pending. Allow a preview to expire before creating another.");
            return store.Locked(() =>
            {
                var current = Capture();
                var attention = Omitted();
                var candidate = Build(request, current, attention, portableSettings, retainUnavailable, localSettings) with
                    { PresetId = presetId, PresetRevision = presetRevision };
                var id = Guid.NewGuid().ToString("N"); var expires = time.GetUtcNow().AddMinutes(10);
                var addedAgents = candidate.Agents.Where(x => !current.Agents.Any(old => old.Id == x.Id)).Select(x => x.Id).ToArray();
                var addedModels = candidate.Models.Where(x => !current.Models.Any(old => old.Id == x.Id)).Select(x => x.Id).ToArray();
                var result = new ConfigurationRestorePreview(id, expires, current.Host.Revision, addedAgents,
                    candidate.Agents.Select(x => x.Id).Except(addedAgents, StringComparer.Ordinal).ToArray(), addedModels,
                    candidate.Models.Select(x => x.Id).Except(addedModels, StringComparer.Ordinal).ToArray(), attention,
                    UpdatedPromptRoles: candidate.Prompts.Select(x => x.Role).ToArray(),
                    UpdatedSkillIds: candidate.RestoreSkills ? candidate.Skills.Select(x => x.Manifest.Id).ToArray() : [])
                    { Settings = candidate.Settings?.Summary };
                // The baseline stays private. It includes identity-bearing current settings so a credential
                // rotation or unrelated concurrent edit invalidates this preview without exporting secrets.
                previews.Add(id, candidate with { Baseline = current.Canonical, Expires = expires, Preview = result });
                return result;
            });
        }
    }

    /// <summary>Caller also holds the model lifecycle gate. The busy predicate runs inside the admission/store transaction.</summary>
    public ConfigurationRestoreResult Apply(JsonElement input, bool owner)
    {
        RequireOwner(owner);
        var request = Read<ConfigurationRestoreApplyRequest>(input);
        ValidateApply(request);
        ConfigurationRestoreResult result; var applied = false;
        lock (gate)
        {
            Expire();
            result = store.Locked(() =>
            {
                if (MatchingReceipt(request) is { } prior) return prior.Result;
                if (!previews.TryGetValue(request.PreviewId, out var candidate))
                    throw new InvalidOperationException("Restore preview is unavailable or expired and no committed receipt is retained. The outcome is unknown; do not assume it was not applied.");
                var current = Capture();
                if (candidate.PresetId is { } selectedPreset && ReadPreset(selectedPreset).Summary.Revision != candidate.PresetRevision)
                    throw new InvalidOperationException("The selected preset changed after preview. Create a new preview.");
                if (request.ExpectedHostRevision != candidate.Preview!.ExpectedHostRevision || current.Canonical != candidate.Baseline)
                    throw new InvalidOperationException("Configuration changed after preview. Create and approve a new preview.");
                if (isBusy() || current.Models.Any(x => x.State is "loaded" or "loading"))
                    throw new InvalidOperationException("Finish active work and unload models before restoring configuration.");
                candidate.Host.Revision = checked(current.Host.Revision + 1);
                foreach (var profile in candidate.Agents)
                {
                    profile.Revision = checked((current.Agents.FirstOrDefault(x => x.Id == profile.Id)?.Revision ?? 0) + 1);
                    store.Put("agent", profile.Id, profile);
                }
                foreach (var model in candidate.Models) store.Put("model", model.Id, model);
                var instructions = new SystemInstructions(store);
                foreach (var prompt in candidate.Prompts) instructions.UpsertForRestore(prompt.Role, prompt.Content);
                if (candidate.RestoreSkills)
                {
                    var skills = new SkillCatalog(store);
                    foreach (var skill in candidate.Skills) skills.UpsertForRestore(skill);
                }
                if (candidate.RestoreInterfacePreferences)
                {
                    candidate.InterfacePreferences.Revision = checked(current.InterfacePreferences.Revision + 1);
                    store.Put("settings", "interface", candidate.InterfacePreferences);
                }
                CommitSettings(candidate.Settings);
                store.Put("settings", "host", candidate.Host);
                var outcome = new ConfigurationRestoreResult(candidate.Host.Revision,
                    candidate.Agents.Select(x => x.Id).ToArray(), candidate.Models.Select(x => x.Id).ToArray(),
                    PromptRoles: candidate.Prompts.Select(x => x.Role).ToArray(),
                    InterfacePreferencesRestored: candidate.RestoreInterfacePreferences,
                    PortablePluginSettingsRestored: candidate.PortablePluginSettingsRestored,
                    SkillsRestored: candidate.RestoreSkills ? candidate.Skills.Length : 0)
                    { Settings = candidate.Settings?.Summary };
                SaveReceipt(new(request.PreviewId, request.ExpectedHostRevision, request.ReviewedFreeText, time.GetUtcNow(), outcome));
                applied = true;
                return outcome;
            });
            previews.Remove(request.PreviewId);
        }
        // The committed result never depends on notification delivery. The receipt records a
        // value-free delivery outcome; a failed diagnostic update leaves "pending", not a false success.
        if (applied) PublishRestoreNotification(request.PreviewId);
        return result;
    }

    private Candidate Build(ConfigurationRestorePreviewRequest request, Current current,
        List<ConfigurationBackupAttention> attention, bool portableSettings = true, bool retainUnavailable = false, SettingsSnapshot? localSettings = null)
    {
        var document = request.Document ?? throw new ArgumentException("A configuration document is required.");
        CheckDocumentSize(document);
        if (document.Format != ConfigurationBackupContract.Format || document.SchemaVersion is < 1 or > CompleteSettingsContract.BackupSchemaVersion)
            throw new ArgumentException("Unsupported configuration backup format or version.");
        var filePurpose = FileSettingsPurpose(document);
        var settingsPurpose = localSettings is not null && !portableSettings ? SettingsTransferPurpose.Local : filePurpose;
        if (filePurpose == SettingsTransferPurpose.Complete && document.Agents?.Any(agent => agent?.PluginSettings?.Count > 0) == true)
            throw new ArgumentException("Complete settings uses only the centralized plugin settings lane.");
        Text(document.ApplicationVersion, 64, "application version");
        if (document.Host is null || document.Paths is null || document.Agents is null || document.Models is null)
            throw new ArgumentException("The backup requires host preferences, paths, agents and models.");
        if (document.SchemaVersion >= 7 && (document.Host.EngineEventHistoryLimit is null ||
            document.Host.EngineEventHistoryMegabytes is null || document.Host.EngineEventHistoryDays is null))
            throw new ArgumentException("Configuration backup schema 7 requires Engine event retention limits.");
        if (document.SchemaVersion >= 8 && document.Skills is null)
            throw new ArgumentException("Configuration backup schema 8 requires a skills list.");
        if (document.SchemaVersion < 8 && document.Skills is not null)
            throw new ArgumentException("Earlier configuration backups cannot carry skills.");
        if (document.SchemaVersion >= 9 && (document.Host.MaxLogicalProcessorCount is null || document.Host.AllowedLogicalProcessors is null))
            throw new ArgumentException("Configuration backup schema 9 requires the logical processor policy.");
        if (document.SchemaVersion >= 10 && document.InterfacePreferences?.AppearanceTheme is null)
            throw new ArgumentException("Configuration backup schema 10 requires an appearance theme.");
        if (document.SchemaVersion >= 12 && document.InterfacePreferences?.RecentChatLimit is null)
            throw new ArgumentException("Configuration backup schema 12 requires the recent chat limit.");
        if (document.SchemaVersion < 12 && document.InterfacePreferences?.RecentChatLimit is not null)
            throw new ArgumentException("Earlier configuration backups cannot carry a recent chat limit.");
        if (document.SchemaVersion >= 13 && (document.Host.PromptCacheDiagnosticsEnabled is null ||
            document.Host.PromptCacheDiagnosticsChangePercentagePoints is null))
            throw new ArgumentException("Configuration backup schema 13 requires prompt-cache diagnostic preferences.");
        if (document.Agents.Count > ConfigurationBackupContract.MaximumEntries || document.Models.Count > ConfigurationBackupContract.MaximumEntries ||
            document.Skills?.Count > ConfigurationBackupContract.MaximumEntries)
            throw new ArgumentException("A configuration section exceeds 128 entries.");
        Unique(document.Agents.Select(x => x?.Id), "agents"); Unique(document.Models.Select(x => x?.Id), "models");
        var prompts = ReadPrompts(document);
        var skills = ReadSkills(document);
        foreach (var skill in skills)
        {
            var existing = current.Skills.SingleOrDefault(x => x.Manifest.Id == skill.Manifest.Id);
            if (skill.BuiltIn && existing?.BuiltIn != true)
                throw new ArgumentException("The destination does not provide built-in skill " + skill.Manifest.Id + ".");
            if (!skill.BuiltIn && existing?.BuiltIn == true)
                throw new ArgumentException("A portable skill cannot replace built-in skill " + skill.Manifest.Id + ".");
        }
        var interfacePreferences = ReadInterfacePreferences(document, current.InterfacePreferences, attention);
        if (document.SchemaVersion == 1)
            attention.Add(new("not-supported", "prompts", null, "This version 1 backup has no shared or role prompts. Current prompts are preserved."));
        // Versions before 7 had no Engine retention fields. Ignore any in-memory values attached to an older
        // version marker and preserve the destination settings, matching deserialized legacy documents.
        var portableHost = document.SchemaVersion < 7
            ? document.Host with { EngineEventHistoryLimit = null, EngineEventHistoryMegabytes = null, EngineEventHistoryDays = null }
            : document.Host;
        // Processor policy became portable in schema 9. An older document preserves the destination
        // policy even if an in-memory caller attaches fields that its declared schema did not contain.
        if (document.SchemaVersion < 9)
            portableHost = portableHost with { MaxLogicalProcessorCount = null, AllowedLogicalProcessors = null };
        if (document.SchemaVersion < 11) portableHost = portableHost with { DefaultModelWorkerId = "local" };
        if (document.SchemaVersion < 13) portableHost = portableHost with
        { PromptCacheDiagnosticsEnabled = null, PromptCacheDiagnosticsChangePercentagePoints = null };
        ValidateHost(portableHost);
        var oldTools = string.IsNullOrWhiteSpace(document.Paths.ToolsDirectory) ? null : PathValue(document.Paths.ToolsDirectory);
        var selectedTools = request.PathOverrides?.ToolsDirectory;
        var tools = !string.IsNullOrWhiteSpace(selectedTools) ? PathValue(selectedTools) : oldTools;
        var oldPaths = new ConfigurationBackupPaths(PathValue(document.Paths.ModelDirectory), PathValue(document.Paths.ProjectsDirectory),
            PathValue(document.Paths.SharedDirectory), oldTools);
        var paths = new ConfigurationBackupPaths(PathValue(request.PathOverrides?.ModelDirectory ?? oldPaths.ModelDirectory),
            PathValue(request.PathOverrides?.ProjectsDirectory ?? oldPaths.ProjectsDirectory), PathValue(request.PathOverrides?.SharedDirectory ?? oldPaths.SharedDirectory),
            tools);
        var mappings = new List<(string, string)>
            { (oldPaths.ModelDirectory, paths.ModelDirectory), (oldPaths.ProjectsDirectory, paths.ProjectsDirectory), (oldPaths.SharedDirectory, paths.SharedDirectory) };
        if (oldPaths.ToolsDirectory is { } oldToolsRoot && paths.ToolsDirectory is { } toolsRoot)
            mappings.Add((oldToolsRoot, toolsRoot));
        var orderedMappings = mappings
            .OrderByDescending(x => x.Item1.Length).ToArray();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (orderedMappings.GroupBy(x => x.Item1, pathComparer).Any(group => group.Select(x => x.Item2).Distinct(pathComparer).Count() > 1))
            throw new ArgumentException("A shared source root cannot map to multiple destinations. Use the same destination for that root, then configure individual paths after restore.");
        string MapPath(string value)
        {
            var path = PathValue(value);
            foreach (var (source, destination) in orderedMappings)
                if (Within(path, source)) return PathValue(Path.Combine(destination, Path.GetRelativePath(source, path)));
            return path;
        }
        if (request.PathOverrides?.ModelFiles is { } files && (files.Count > 128 || files.Keys.Any(id => !document.Models.Any(x => x.Id == id))))
            throw new ArgumentException("Model path overrides must identify included model registrations.");
        if (document.SchemaVersion < 4 && document.Models.Any(x => x.VisionProjectorPath is not null))
            throw new ArgumentException("Earlier backup versions cannot carry image component bindings.");
        if (request.PathOverrides?.ModelProjectorFiles is { } components &&
            (components.Count > 128 || components.Keys.Any(id => !document.Models.Any(x => x.Id == id && x.VisionProjectorPath is not null))))
            throw new ArgumentException("Image component overrides must identify included component bindings.");
        var models = document.Models.Select(item =>
        {
            var model = Model(item);
            model.Path = request.PathOverrides?.ModelFiles?.TryGetValue(item.Id, out var mapped) == true ? PathValue(mapped!) : MapPath(item.Path);
            if (document.SchemaVersion >= 4)
                model.VisionProjectorPath = item.VisionProjectorPath is null ? null :
                    request.PathOverrides?.ModelProjectorFiles?.TryGetValue(item.Id, out var component) == true
                        ? PathValue(component!) : MapPath(item.VisionProjectorPath);
            else
            {
                var local = current.Models.FirstOrDefault(x => x.Id == item.Id);
                // An old backup has no opinion about components. Retain only a demonstrably unchanged local model binding.
                model.VisionProjectorPath = local is not null && pathComparer.Equals(PathValue(local.Path), model.Path)
                    ? local.VisionProjectorPath : null;
                if (local?.VisionProjectorPath is not null && model.VisionProjectorPath is null)
                    attention.Add(new("model-component-reconfigure", "models", model.Id, "The model path changed; its previous local image component was not carried to another model."));
            }
            ValidateModel(model);
            // Metadata and capabilities are observed by the normal inspect/load path; backup values do not prove them.
            attention.Add(new("model-inspection-required", "models", model.Id, "Model metadata and capabilities must be inspected before use. Restore does not load or download models."));
            if (!model.Path.StartsWith("\\\\", StringComparison.Ordinal) && !File.Exists(model.Path))
                attention.Add(new("model-file-missing", "models", model.Id, "The model file is unavailable at the selected reference. Registration is restored unloaded."));
            if (model.VisionProjectorPath is { } projector)
            {
                attention.Add(new("model-component-unverified", "models", model.Id, "The image component reference is retained. Only a matching loaded runtime and native input preparation establish image support."));
                if (!projector.StartsWith("\\\\", StringComparison.Ordinal) && !File.Exists(projector))
                    attention.Add(new("model-component-missing", "models", model.Id, "The image component is unavailable at its selected reference. Restore does not copy or download it."));
            }
            return model;
        }).ToArray();
        var schemas = portableSchemas();
        var portablePluginSettingsRestored = 0;
        var retainedAgentSettings = retainUnavailable ? new List<ConfigurationBackupSettingsEntry>() : null;
        var agents = document.Agents.Select(item =>
        {
            var profile = Agent(item);
            if (profile.WorkerId == "local") profile.AllowedRoots = profile.AllowedRoots.Select(MapPath).ToList();
            var original = current.Agents.FirstOrDefault(x => x.Id == item.Id);
            // Earlier documents cannot grant, clear, or silently replace the new owner policy.
            // Portable schema14 carries it only as reference data for local preset projection.
            if (document.SchemaVersion < 16) profile.TaskBudgetPolicy = original?.TaskBudgetPolicy;
            profile.PluginSettings = CopyPluginSettings(original?.PluginSettings);
            if (filePurpose != SettingsTransferPurpose.Complete)
                portablePluginSettingsRestored += ApplyPortablePluginSettings(document.SchemaVersion, item, profile, schemas, attention, retainedAgentSettings);
            ValidateAgent(profile);
            attention.Add(new("ordinary-grants-review", "agents", profile.Id, "Review the agent's ordinary tool, capability and workspace grants in the backup. Full Access is never restored."));
            return profile;
        }).ToArray();
        var settingsPlan = BuildSettings(document, current, agents, retainedAgentSettings ?? [], attention, settingsPurpose, localSettings);
        if (settingsPlan is not null)
            agents = agents.Concat(settingsPlan.AffectedAgents.Where(x => !agents.Any(existing => existing.Id == x.Id))).ToArray();
        ApplyConnections(document, current.Host, attention);
        var combinedAgents = current.Agents.Where(x => !agents.Any(y => y.Id == x.Id)).Concat(agents).ToArray();
        var modelIds = current.Models.Select(x => x.Id).Concat(models.Select(x => x.Id)).ToHashSet(StringComparer.Ordinal);
        if (combinedAgents.Length > ConfigurationBackupContract.MaximumEntries || modelIds.Count > ConfigurationBackupContract.MaximumEntries)
            throw new ArgumentException("Upsert restore would exceed this version's 128-entry section limit.");
        if (combinedAgents.Count(x => x.Role == "lead") > 1) throw new ArgumentException("Upsert restore would leave more than one lead profile. Resolve the conflicting profile before restore.");
        if (document.SchemaVersion >= 8 && current.Skills.Select(x => x.Manifest.Id).Concat(skills.Select(x => x.Manifest.Id))
            .Distinct(StringComparer.Ordinal).Count() > ConfigurationBackupContract.MaximumEntries)
            throw new ArgumentException("Upsert restore would exceed this version's 128-skill limit.");
        if (portableHost.DefaultModelId.Length > 0 && portableHost.DefaultModelWorkerId == "local" && !modelIds.Contains(portableHost.DefaultModelId))
            throw new ArgumentException("The default model does not identify an available registration.");
        if (portableHost.DefaultModelWorkerId != "local" && !current.Host.Workers.Any(x => x.Id == portableHost.DefaultModelWorkerId && !x.RequiresConfiguration))
            throw new ArgumentException("Reconnect the default model's worker locally before restoring its placement.");
        foreach (var profile in combinedAgents)
        {
            if (profile.ModelWorkerId == "local" && !modelIds.Contains(profile.ModelId)) throw new ArgumentException("An agent references a missing model registration.");
            if (profile.CompactionProfileId is { Length: > 0 } compactor && !combinedAgents.Any(x => x.Id == compactor))
                throw new ArgumentException("An agent references a missing compactor profile.");
            if (new[] { profile.WorkerId, profile.ModelWorkerId }.Any(id => id != "local" && !current.Host.Workers.Any(worker => worker.Id == id)))
                throw new ArgumentException("An agent references an unavailable worker connection. Reconnect that worker locally before restore.");
            if (profile.Role == "lead" && !profile.FollowsDefaultModel)
                throw new ArgumentException("The lead profile must follow the default model. Correct the profile before restore.");
            if (profile.FollowsDefaultModel && portableHost.DefaultModelId.Length > 0 &&
                (profile.ModelId != portableHost.DefaultModelId || profile.ModelWorkerId != portableHost.DefaultModelWorkerId))
                throw new ArgumentException("An agent following the default model would have a conflicting model binding.");
        }
        var host = current.Host;
        ApplyHost(host, portableHost, paths);
        return new(host, agents, models, prompts, skills, document.SchemaVersion >= 8,
            interfacePreferences, document.SchemaVersion >= 5,
            portablePluginSettingsRestored, "", default, null) { Settings = settingsPlan };
    }

    private static ConfigurationBackupPrompt[] ReadPrompts(ConfigurationBackupDocument document)
    {
        if (document.SchemaVersion == 1)
        {
            if (document.Prompts is not null) throw new ArgumentException("Version 1 backups cannot carry prompt overrides.");
            return [];
        }
        if (document.Prompts is null || document.Prompts.Count > 4)
            throw new ArgumentException("This backup version requires a prompt list with at most four role layers.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prompt in document.Prompts)
        {
            if (prompt is null || !seen.Add(prompt.Role)) throw new ArgumentException("Prompt roles must be present and unique.");
            SystemInstructions.ValidatePrompt(prompt.Role, prompt.Content);
        }
        return document.Prompts.ToArray();
    }

    private static ConfigurationBackupSkill[] ReadSkills(ConfigurationBackupDocument document)
    {
        if (document.SchemaVersion < 8) return [];
        var skills = document.Skills ?? throw new ArgumentException("This backup version requires a skills list.");
        Unique(skills.Select(x => x?.Manifest?.Id), "skills");
        foreach (var item in skills) SkillCatalog.ValidateBackupSkill(item ?? throw new ArgumentException("A skill entry is missing."));
        return skills.ToArray();
    }

    private static InterfacePreferences ReadInterfacePreferences(ConfigurationBackupDocument document,
        InterfacePreferences current, List<ConfigurationBackupAttention> attention)
    {
        if (document.SchemaVersion < 5)
        {
            if (document.InterfacePreferences is not null)
                throw new ArgumentException("Earlier backup versions cannot carry interface preferences.");
            attention.Add(new("not-supported", "interface", null,
                "This earlier backup has no portable composer or layout preferences. Current interface preferences are preserved."));
            return CopyInterfacePreferences(current);
        }
        var portable = document.InterfacePreferences ??
            throw new ArgumentException("This backup version requires portable interface preferences.");
        var value = new InterfacePreferences
        {
            EnterSends = portable.EnterSends,
            NavigationVisible = portable.NavigationVisible,
            PanelsVisible = portable.PanelsVisible,
            SelectedSurface = portable.SelectedSurface,
            // Appearance became portable in schema 10. Older backups preserve the destination choice.
            AppearanceTheme = document.SchemaVersion >= 10
                ? portable.AppearanceTheme
                : current.AppearanceTheme ?? AppearanceThemePolicy.System,
            MessageTimeFormat = MessageTimeFormatPolicy.Effective(portable.MessageTimeFormat),
            // Earlier backups have no chat visibility choice; preserve the destination preference.
            RecentChatLimit = document.SchemaVersion >= 12 ? portable.RecentChatLimit : current.RecentChatLimit
        };
        InterfacePreferencePolicy.Validate(value);
        return value;
    }

    private static IReadOnlyList<ConfigurationBackupPluginSettings> ExportPortablePluginSettings(AgentProfile profile,
        IReadOnlyDictionary<string, ServicePluginPortableSettingsBinding> schemas,
        List<ConfigurationBackupAttention> attention)
    {
        var output = new List<ConfigurationBackupPluginSettings>();
        foreach (var item in profile.PluginSettings.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!schemas.TryGetValue(item.Key, out var schema))
            {
                attention.Add(new("opaque-settings-omitted", "agents", profile.Id,
                    "Plugin settings without an installed declared portable schema were omitted."));
                continue;
            }
            try
            {
                var values = PortableProjection(item.Value, schema, exact: false, out var omitted);
                RequirePortablePayloadSize(values);
                output.Add(new(schema.PluginId, schema.PackageVersion, schema.Version, values));
                if (omitted > 0)
                    attention.Add(new("opaque-settings-omitted", "agents", profile.Id,
                        "Plugin fields outside the declared portable schema were omitted."));
            }
            catch (ArgumentException)
            {
                attention.Add(new("invalid-portable-settings-omitted", "agents", profile.Id,
                    "Plugin settings did not satisfy the installed portable schema and were omitted."));
            }
        }
        return output;
    }

    private static int ApplyPortablePluginSettings(int documentVersion, ConfigurationBackupAgent item,
        AgentProfile profile, IReadOnlyDictionary<string, ServicePluginPortableSettingsBinding> schemas,
        List<ConfigurationBackupAttention> attention, List<ConfigurationBackupSettingsEntry>? retained = null)
    {
        if (documentVersion < 6)
        {
            if (item.PluginSettings is not null)
                throw new ArgumentException("Earlier backup versions cannot carry portable plugin settings.");
            attention.Add(new("not-supported", "plugin-settings", profile.Id,
                "This earlier backup has no portable plugin settings. Current plugin settings are preserved."));
            return 0;
        }
        if (item.PluginSettings is null || item.PluginSettings.Count > 32)
            throw new ArgumentException("This backup version requires at most 32 portable plugin settings entries per agent.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var applied = 0;
        foreach (var entry in item.PluginSettings)
        {
            if (entry is null) throw new ArgumentException("Portable plugin settings entries cannot be null.");
            Id(entry.PluginId);
            if (!ids.Add(entry.PluginId) || !PluginVersion.TryParse(entry.SourcePackageVersion, out _) ||
                entry.SettingsVersion is < 1 or > 1_000_000 || entry.Values.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Portable plugin settings identity, version or payload is invalid.");
            RequirePortablePayloadSize(entry.Values);
            if (!schemas.TryGetValue(entry.PluginId, out var schema))
            {
                attention.Add(new("plugin-settings-unavailable", "plugin-settings", entry.PluginId,
                    "The destination package has no installed portable settings schema. Existing local settings are preserved."));
                KeepUnavailableAgentSettings(retained, item, entry);
                continue;
            }
            if (schema.Version != entry.SettingsVersion)
            {
                attention.Add(new("plugin-settings-version-mismatch", "plugin-settings", entry.PluginId,
                    "The destination package uses a different portable settings schema. Existing local settings are preserved."));
                KeepUnavailableAgentSettings(retained, item, entry);
                continue;
            }
            if (!string.Equals(schema.PackageVersion, entry.SourcePackageVersion, StringComparison.Ordinal))
                attention.Add(new("plugin-package-version-changed", "plugin-settings", entry.PluginId,
                    "The destination package version differs, but it declares the same portable settings version. Review the restored values."));
            var portable = PortableProjection(entry.Values, schema, exact: true, out _);
            profile.PluginSettings[entry.PluginId] = MergePortablePluginSettings(
                profile.PluginSettings.GetValueOrDefault(entry.PluginId), portable, schema);
            applied++;
        }
        return applied;
    }

    private static JsonElement PortableProjection(JsonElement source, ServicePluginPortableSettingsBinding schema,
        bool exact, out int omitted)
    {
        if (source.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Plugin settings must be an object.");
        var properties = source.EnumerateObject().ToArray();
        if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new ArgumentException("Plugin settings cannot contain duplicate fields.");
        var fields = schema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        omitted = 0;
        foreach (var property in properties)
        {
            if (!fields.TryGetValue(property.Name, out var field))
            {
                if (exact) throw new ArgumentException("Portable plugin settings contain an undeclared field.");
                omitted++; continue;
            }
            ServicePluginPortableSettingsPolicy.ValidateValue(field, property.Value);
            values.Add(property.Name, property.Value.Clone());
        }
        if (schema.Fields.Any(field => field.Required && !values.ContainsKey(field.Name)))
            throw new ArgumentException("Portable plugin settings omit a required declared field.");
        return JsonSerializer.SerializeToElement(values, Json);
    }

    private static JsonElement MergePortablePluginSettings(JsonElement existing, JsonElement portable,
        ServicePluginPortableSettingsBinding schema)
    {
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (existing.ValueKind == JsonValueKind.Object)
        {
            Configuration.ValidatePluginSettingsValue(existing);
            foreach (var property in existing.EnumerateObject()) values.Add(property.Name, property.Value.Clone());
        }
        foreach (var field in schema.Fields) values.Remove(field.Name);
        foreach (var property in portable.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return JsonSerializer.SerializeToElement(values, Json);
    }

    private static void RequirePortablePayloadSize(JsonElement value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value, Json).Length > MaximumPortablePluginSettingsBytes)
            throw new ArgumentException("Portable plugin settings payload exceeds 64 KiB after canonical serialization.");
    }

    private static Dictionary<string, JsonElement> CopyPluginSettings(Dictionary<string, JsonElement>? source) =>
        source?.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal) ??
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private Current Capture(bool includeLegacySettings = true)
    {
        var host = store.Get<HostSettings>("settings", "host") ?? throw new InvalidOperationException("Initialize local service configuration before backup or restore.");
        var agents = store.List<AgentProfile>("agent").OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var models = store.List<ModelRecord>("model").OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (agents.Length > 128 || models.Length > 128) throw new InvalidOperationException("Configuration exceeds this backup version's 128-entry section limit.");
        var instructions = new SystemInstructions(store);
        var capturedPrompts = instructions.CaptureEffective();
        var prompts = capturedPrompts.Select(x => new ConfigurationBackupPrompt(x.Role, x.Content)).ToArray();
        var promptVersions = capturedPrompts.Select(x => x.Sha256).ToArray();
        var interfacePreferences = new InterfacePreferencesStore(store).Get();
        InterfacePreferencePolicy.Validate(interfacePreferences);
        var skills = new SkillCatalog(store).ExportForBackup().ToArray();
        var settings = CaptureSettings(includeLegacySettings);
        var canonicalSettings = settings with { TransferMetadata = null };
        var canonical = JsonSerializer.Serialize(new { host, agents, models, prompts, promptVersions, interfacePreferences, skills, settings = canonicalSettings }, Json);
        if (Encoding.UTF8.GetByteCount(canonical) > 4 * ConfigurationBackupContract.MaximumUtf8Bytes)
            throw new InvalidOperationException("Configuration is too large for a bounded restore preview.");
        if (settings.TransferMetadata is not null) canonical += "|settings-transfer:" + SettingsMetadataFingerprint(settings);
        return new(host, agents, models, prompts, skills, interfacePreferences, canonical) { Settings = settings };
    }

    private static T Read<T>(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(input).Length > ConfigurationBackupContract.MaximumUtf8Bytes + 64 * 1024)
            throw new ArgumentException("Configuration request is invalid or exceeds its size bound.");
        RejectDuplicateFields(input);
        try { return input.Deserialize<T>(Json) ?? throw new ArgumentException("Configuration request is empty."); }
        catch (JsonException) { throw new ArgumentException("Configuration request contains unsupported or invalid fields."); }
    }
    private static void RejectDuplicateFields(JsonElement value, bool opaque = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(opaque ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Configuration request contains duplicate fields.");
                RejectDuplicateFields(property.Value, opaque || SettingsPreferenceJson.IsOpaqueProperty(property));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item, opaque);
    }
    private static void RequireOwner(bool owner) { if (!owner) throw new UnauthorizedAccessException("Configuration backup and restore require the authenticated installation owner."); }
    private void Expire()
    { foreach (var id in previews.Where(x => x.Value.Expires <= time.GetUtcNow()).Select(x => x.Key).ToArray()) previews.Remove(id); }
    private static void CheckDocumentSize(ConfigurationBackupDocument document)
    { if (JsonSerializer.SerializeToUtf8Bytes(document, Json).Length > ConfigurationBackupContract.MaximumUtf8Bytes) throw new ArgumentException("Configuration backup exceeds the 1 MiB limit."); }
    private static List<ConfigurationBackupAttention> Omitted() =>
    [
        new("excluded", "credentials", null, "Credentials, local authorization and Full Access activation are neither exported nor restored."),
        new("excluded", "machine-identity", null, "Service identity, installation authorization and model runtime executable paths remain local and unchanged."),
        new("excluded", "interface-session", null, "Per-conversation response balance and transient session state are excluded; saved app layout is included."),
        new("not-supported", "plugins", null, "Only package-declared nonsecret settings projections are portable. Packages and enablement are not restored."),
        new("excluded", "domain-data", null, "Conversations, projects, book databases, model files, logs and background tasks are not included."),
        new("owner-review", "free-text", null, "Review included text for private information before saving or restoring. Automatic secret scrubbing is not promised.")
    ];
    private sealed record Current(HostSettings Host, AgentProfile[] Agents, ModelRecord[] Models,
        ConfigurationBackupPrompt[] Prompts, ConfigurationBackupSkill[] Skills,
        InterfacePreferences InterfacePreferences, string Canonical)
    {
        public required SettingsSnapshot Settings { get; init; }
    }
    private sealed record Candidate(HostSettings Host, AgentProfile[] Agents, ModelRecord[] Models,
        ConfigurationBackupPrompt[] Prompts, ConfigurationBackupSkill[] Skills, bool RestoreSkills,
        InterfacePreferences InterfacePreferences, bool RestoreInterfacePreferences,
        int PortablePluginSettingsRestored, string Baseline, DateTimeOffset Expires, ConfigurationRestorePreview? Preview)
    {
        public SettingsPlan? Settings { get; init; }
        public string? PresetId { get; init; }
        public long PresetRevision { get; init; }
    }

    private static ConfigurationBackupInterfacePreferences ExportInterfacePreferences(InterfacePreferences value) =>
        new(value.EnterSends, value.NavigationVisible, value.PanelsVisible, value.SelectedSurface,
            AppearanceThemePolicy.Effective(value.AppearanceTheme), MessageTimeFormatPolicy.Effective(value.MessageTimeFormat),
            RecentChatPolicy.Effective(value.RecentChatLimit));
    private static InterfacePreferences CopyInterfacePreferences(InterfacePreferences value) => new()
    {
        Revision = value.Revision,
        EnterSends = value.EnterSends,
        NavigationVisible = value.NavigationVisible,
        PanelsVisible = value.PanelsVisible,
        SelectedSurface = value.SelectedSurface,
        AppearanceTheme = value.AppearanceTheme,
        MessageTimeFormat = value.MessageTimeFormat,
        RecentChatLimit = value.RecentChatLimit
    };

    private static ConfigurationBackupHost Host(HostSettings x) => new(x.MaxConcurrentJobs, x.QueueCapacity, x.JobHistoryLimit,
        x.JobHistoryDays, x.ActivityHistoryLimit, x.ActivityHistoryDays, x.ReservedMemoryMb, x.DefaultModelIdleMinutes,
        x.MaxExecutableAttemptsPerAssignment, x.DefaultModelId, x.QuestionTimeoutSeconds, x.QuestionTimeoutAction,
        x.EngineEventHistoryLimit, x.EngineEventHistoryMegabytes, x.EngineEventHistoryDays,
        x.MaxLogicalProcessorCount, x.AllowedLogicalProcessors.Order().ToArray(), x.DefaultModelWorkerId,
        x.PromptCacheDiagnosticsEnabled, x.PromptCacheDiagnosticsChangePercentagePoints)
        { WorkflowIdleExpiryDays = x.WorkflowIdleExpiryDays };
    private static ConfigurationBackupPaths Paths(HostSettings x) => new(x.ModelDirectory, x.ProjectsDirectory, x.SharedDirectory, x.ToolsDirectory);
    private static void ApplyHost(HostSettings x, ConfigurationBackupHost h, ConfigurationBackupPaths p)
    {
        x.MaxConcurrentJobs = h.MaxConcurrentJobs; x.QueueCapacity = h.QueueCapacity; x.JobHistoryLimit = h.JobHistoryLimit;
        x.JobHistoryDays = h.JobHistoryDays; x.ActivityHistoryLimit = h.ActivityHistoryLimit; x.ActivityHistoryDays = h.ActivityHistoryDays;
        if (h.WorkflowIdleExpiryDays is { } expiryDays) x.WorkflowIdleExpiryDays = expiryDays;
        x.ReservedMemoryMb = h.ReservedMemoryMb; x.DefaultModelIdleMinutes = h.DefaultModelIdleMinutes;
        x.MaxExecutableAttemptsPerAssignment = h.MaxExecutableAttemptsPerAssignment; x.DefaultModelId = h.DefaultModelId; x.DefaultModelWorkerId = h.DefaultModelWorkerId;
        x.QuestionTimeoutSeconds = h.QuestionTimeoutSeconds; x.QuestionTimeoutAction = h.QuestionTimeoutAction;
        // Schema 1-6 backups omit Engine journal retention. Preserve the destination's current values in that case.
        if (h.EngineEventHistoryLimit is { } eventLimit) x.EngineEventHistoryLimit = eventLimit;
        if (h.EngineEventHistoryMegabytes is { } eventMegabytes) x.EngineEventHistoryMegabytes = eventMegabytes;
        if (h.EngineEventHistoryDays is { } eventDays) x.EngineEventHistoryDays = eventDays;
        // Schema 1-8 backups omit processor policy. Preserve the destination's current values.
        if (h.MaxLogicalProcessorCount is { } maximum) x.MaxLogicalProcessorCount = maximum;
        if (h.AllowedLogicalProcessors is { } processors) x.AllowedLogicalProcessors = processors.Order().ToList();
        if (h.PromptCacheDiagnosticsEnabled is { } diagnostics) x.PromptCacheDiagnosticsEnabled = diagnostics;
        if (h.PromptCacheDiagnosticsChangePercentagePoints is { } threshold) x.PromptCacheDiagnosticsChangePercentagePoints = threshold;
        x.ModelDirectory = p.ModelDirectory; x.ProjectsDirectory = p.ProjectsDirectory; x.SharedDirectory = p.SharedDirectory;
        if (!string.IsNullOrWhiteSpace(p.ToolsDirectory)) x.ToolsDirectory = p.ToolsDirectory;
    }
    private static ConfigurationBackupAgent Agent(AgentProfile x,
        IReadOnlyDictionary<string, ServicePluginPortableSettingsBinding> schemas,
        List<ConfigurationBackupAttention> attention, bool includePluginSettings = true) => new()
    {
        Id = x.Id, Name = x.Name, Purpose = x.Purpose, Instructions = x.Instructions, Role = x.Role, ModelId = x.ModelId,
        FollowsDefaultModel = x.FollowsDefaultModel, WorkerId = x.WorkerId, ModelWorkerId = x.ModelWorkerId,
        ContextTokens = x.ContextTokens, ResponseTokens = x.ResponseTokens, FollowsModelBudgets = x.FollowsModelBudgets,
        ReasoningMode = x.ReasoningMode, ReasoningTokens = x.ReasoningTokens,
        AllowReasoningAdjustment = x.AllowReasoningAdjustment, ReasoningTokenCeiling = x.ReasoningTokenCeiling,
        TaskBudgetPolicy = x.TaskBudgetPolicy,
        Temperature = x.Temperature,
        MaxSteps = x.MaxSteps, MaxSeconds = x.MaxSeconds, MemoryBudgetMb = x.MemoryBudgetMb, IdleMinutes = x.IdleMinutes,
        AutomaticCompaction = x.AutomaticCompaction, CompactionProfileId = x.CompactionProfileId,
        CompactionReductionPercent = x.CompactionReductionPercent, CompactionTriggerPercent = x.CompactionTriggerPercent, AllowedRoots = x.AllowedRoots.ToArray(), AllowedTools = x.AllowedTools.ToArray(),
        Capabilities = x.Capabilities.ToArray(), RequiredModelCapabilities = x.RequiredModelCapabilities.ToArray(),
        PluginSettings = includePluginSettings ? ExportPortablePluginSettings(x, schemas, attention) : []
    };
    private static AgentProfile Agent(ConfigurationBackupAgent x) => new()
    {
        Id = x.Id, Name = x.Name, Purpose = x.Purpose, Instructions = x.Instructions, Role = x.Role, ModelId = x.ModelId,
        FollowsDefaultModel = x.FollowsDefaultModel, WorkerId = x.WorkerId, ModelWorkerId = x.ModelWorkerId,
        ContextTokens = x.ContextTokens, ResponseTokens = x.ResponseTokens, FollowsModelBudgets = x.FollowsModelBudgets,
        ReasoningMode = x.ReasoningMode, ReasoningTokens = x.ReasoningTokens,
        AllowReasoningAdjustment = x.AllowReasoningAdjustment, ReasoningTokenCeiling = x.ReasoningTokenCeiling,
        TaskBudgetPolicy = x.TaskBudgetPolicy,
        Temperature = x.Temperature,
        MaxSteps = x.MaxSteps, MaxSeconds = x.MaxSeconds, MemoryBudgetMb = x.MemoryBudgetMb, IdleMinutes = x.IdleMinutes,
        AutomaticCompaction = x.AutomaticCompaction, CompactionProfileId = x.CompactionProfileId,
        CompactionReductionPercent = x.CompactionReductionPercent, CompactionTriggerPercent = x.CompactionTriggerPercent, AllowedRoots = Copy(x.AllowedRoots), AllowedTools = Copy(x.AllowedTools),
        Capabilities = Copy(x.Capabilities), RequiredModelCapabilities = Copy(x.RequiredModelCapabilities)
    };
    private static ConfigurationBackupModel Model(ModelRecord x, List<ConfigurationBackupAttention> attention) => new()
    {
        Id = x.Id, Name = x.Name, Path = x.Path, VisionProjectorPath = x.VisionProjectorPath, SourceUrl = ExportUrl(x.SourceUrl, x.Id, "sourceUrl", attention),
        License = x.License, Publisher = x.Publisher, UpstreamModelId = x.UpstreamModelId,
        UpstreamModelUrl = ExportUrl(x.UpstreamModelUrl, x.Id, "upstreamModelUrl", attention), LicenseUrl = ExportUrl(x.LicenseUrl, x.Id, "licenseUrl", attention),
        UpstreamRevision = x.UpstreamRevision, Description = x.Description, Tags = x.Tags.ToArray(), PackageId = x.PackageId,
        PackageVersion = x.PackageVersion, PackagePurpose = x.PackagePurpose, StarterPackage = x.StarterPackage, Purpose = x.Purpose,
        AutomaticContextSizing = x.AutomaticContextSizing, MaximumContextTokens = x.MaximumContextTokens, ContextTokens = x.ContextTokens,
        ParallelRequests = x.ParallelRequests, GpuLayers = x.GpuLayers, IdleMinutes = x.IdleMinutes, ChatTemplate = x.ChatTemplate, StopSequences = x.StopSequences.ToArray()
    };
    private static ModelRecord Model(ConfigurationBackupModel x) => new()
    {
        Id = x.Id, Name = x.Name, Path = x.Path, VisionProjectorPath = x.VisionProjectorPath, SourceUrl = x.SourceUrl, License = x.License, Publisher = x.Publisher,
        UpstreamModelId = x.UpstreamModelId, UpstreamModelUrl = x.UpstreamModelUrl, LicenseUrl = x.LicenseUrl,
        UpstreamRevision = x.UpstreamRevision, Description = x.Description, Tags = Copy(x.Tags), PackageId = x.PackageId,
        PackageVersion = x.PackageVersion, PackagePurpose = x.PackagePurpose, StarterPackage = x.StarterPackage, Purpose = x.Purpose,
        AutomaticContextSizing = x.AutomaticContextSizing, MaximumContextTokens = x.MaximumContextTokens, ContextTokens = x.ContextTokens,
        ParallelRequests = x.ParallelRequests, GpuLayers = x.GpuLayers, IdleMinutes = x.IdleMinutes, ChatTemplate = x.ChatTemplate, StopSequences = Copy(x.StopSequences),
        State = "unloaded", Error = null, CapabilityStatus = []
    };
    private static List<string> Copy(IReadOnlyList<string>? values) => values?.ToList() ?? throw new ArgumentException("Configuration lists cannot be null.");
    private static void Unique(IEnumerable<string?> ids, string section)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) { Id(id); if (!seen.Add(id!)) throw new ArgumentException("Duplicate identifier in " + section + "."); }
    }
    private static void Id(string? value) { if (value is null) throw new ArgumentException("A configuration identifier is missing."); Configuration.ValidateId(value); }
    private static void Text(string? value, int maximum, string field, bool optional = false)
    { if ((!optional && value is null) || value?.Contains('\0') == true || value?.Length > maximum) throw new ArgumentException("Invalid configuration " + field + "."); }
    private static string PathValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32768 || value.Contains('\0') || !Path.IsPathFullyQualified(value) ||
            value.Split(['/', '\\']).Contains("..", StringComparer.Ordinal)) throw new ArgumentException("Configuration paths must be absolute and cannot traverse parent directories.");
        if (OperatingSystem.IsWindows() && (value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            value.Skip(2).Contains(':') || value.Any(character => character < ' ' || character is '<' or '>' or '"' or '|' or '?' or '*')))
            throw new ArgumentException("Configuration paths cannot contain device syntax, alternate streams or invalid path characters.");
        string path;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Configuration path syntax is invalid."); }
        var program = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (Path.GetFileName(program).Equals("Worker", StringComparison.OrdinalIgnoreCase)) program = Path.GetDirectoryName(program)!;
        if (Within(path, program)) throw new ArgumentException("Configuration data references must remain outside replaceable program output.");
        return path;
    }
    private static bool Within(string path, string root) => string.Equals(path, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string? ExportUrl(string? url, string id, string field, List<ConfigurationBackupAttention> attention)
    {
        if (url is null || SafeUrl(url)) return url;
        attention.Add(new("unsafe-url-omitted", "models", id, "A model " + field + " was omitted because URL credentials, queries, fragments or an unsupported format may contain private data."));
        return null;
    }
    private static bool SafeUrl(string value) => value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    private static void ValidateHost(ConfigurationBackupHost x)
    {
        if (x.MaxConcurrentJobs is < 1 or > 16 || x.QueueCapacity is < 1 or > 1000 || x.ReservedMemoryMb < 0 ||
            x.DefaultModelIdleMinutes is < 0 or > 10080 || x.MaxExecutableAttemptsPerAssignment is < 1 or > 20 ||
            x.JobHistoryLimit is < 1 or > 10000 || x.JobHistoryDays is < 1 or > 3650 || x.ActivityHistoryLimit is < 100 or > 50000 ||
            x.ActivityHistoryDays is < 1 or > 3650 || x.QuestionTimeoutSeconds is < 0 or > 86400 || x.QuestionTimeoutAction is not ("wait" or "assume") ||
            x.EngineEventHistoryLimit is < 1 or > 1_000_000 || x.EngineEventHistoryMegabytes is < 1 or > 16_384 ||
            x.EngineEventHistoryDays is < 1 or > 3650)
            throw new ArgumentException("Host preference values exceed their supported bounds.");
        if (x.WorkflowIdleExpiryDays is < 0 or > HostSettings.MaximumWorkflowIdleExpiryDays)
            throw new ArgumentException("Workflow idle expiry must be 0–3650 days.");
        if (x.PromptCacheDiagnosticsChangePercentagePoints is < 1 or > 100)
            throw new ArgumentException("Prompt-cache diagnostic change threshold must be 1-100 percent.");
        if (x.MaxLogicalProcessorCount is < 0 or > 1024 || x.AllowedLogicalProcessors is { Count: > 1024 } ||
            x.AllowedLogicalProcessors is { } processors &&
            (processors.Any(processor => processor is < 0 or > 1023) || processors.Distinct().Count() != processors.Count))
            throw new ArgumentException("Host logical processor policy exceeds its supported bounds.");
        if (x.DefaultModelId is null) throw new ArgumentException("Default model reference cannot be null.");
        if (x.DefaultModelId.Length > 0) Id(x.DefaultModelId);
        Id(x.DefaultModelWorkerId);
    }
    private static void ValidateAgent(AgentProfile x)
    {
        foreach (var list in new[] { x.AllowedRoots, x.AllowedTools, x.Capabilities, x.RequiredModelCapabilities })
            if (list.Count > 128 || list.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 32768 || value.Contains('\0')))
                throw new ArgumentException("Agent grants exceed their supported bounds.");
        // Existing profile validation can quote unsupported capability values; backup diagnostics must not.
        try { Configuration.ValidateProfile(x); }
        catch (ArgumentException) { throw new ArgumentException("An agent profile has invalid identity, role, resource limits or required capabilities."); }
        Id(x.WorkerId); Id(x.ModelWorkerId); Id(x.ModelId);
        if (x.CompactionProfileId is { Length: > 0 }) Id(x.CompactionProfileId);
        Text(x.Name, 256, "agent name"); Text(x.Purpose, 4000, "agent purpose"); Text(x.Instructions, 32768, "agent instructions");
        foreach (var root in x.AllowedRoots) _ = PathValue(root);
    }
    private static void ValidateModel(ModelRecord x)
    {
        Id(x.Id); Text(x.Name, 256, "model name"); Text(x.Purpose, 4000, "model purpose");
        if (string.IsNullOrWhiteSpace(x.Name)) throw new ArgumentException("A model name is required.");
        if (x.VisionProjectorPath is not null) _ = ModelComponentConfiguration.NormalizePath(PathValue(x.VisionProjectorPath));
        if (x.ContextTokens is < 512 or > 1048576 || x.MaximumContextTokens is < 0 or > 1048576 || x.MaximumContextTokens is > 0 and < 512 ||
            x.ParallelRequests is < 1 or > 16 || x.GpuLayers is < -1 or > 999 || x.IdleMinutes is < -1 or > 10080)
            throw new ArgumentException("Model resource preferences exceed their supported bounds.");
        Text(x.ChatTemplate, 32000, "chat template", true);
        if (x.StopSequences.Count > 16 || x.StopSequences.Any(value => string.IsNullOrEmpty(value) || value.Length > 256 || value.Contains('\0')) ||
            x.Tags.Count > 32 || x.Tags.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Contains('\0')))
            throw new ArgumentException("Model stop sequences or tags exceed their supported bounds.");
        foreach (var value in new[] { x.SourceUrl, x.UpstreamModelUrl, x.LicenseUrl })
            if (value is not null && !SafeUrl(value)) throw new ArgumentException("Model URLs must be HTTP(S) without credentials, query strings or fragments.");
        foreach (var value in new[] { x.License, x.Publisher, x.UpstreamModelId, x.UpstreamRevision, x.Description, x.PackageId, x.PackageVersion, x.PackagePurpose })
            Text(value, 4000, "model metadata", true);
    }
}
