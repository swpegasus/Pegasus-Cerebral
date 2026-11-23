using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Core;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

/// <summary>Host-owned admission settings. Secrets and mutable plugin settings never enter workflow state.</summary>
public static class WorkerExecutionSnapshot
{
    public const int Version = 6;
    internal const int OwnerVerificationVersion = 7;
    internal const int ChatPermissionsVersion = 8;
    private const int ClosedLegacyVersion = 5;
    internal static bool IsCurrentEdition(int version) =>
        version is Version or OwnerVerificationVersion or ChatPermissionsVersion;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // The loaded service-tool owner is frozen for each selected semantic tool, including a
    // null owner for built-in tools. Package membership alone cannot prove a later remap.
    internal sealed record SelectedToolOwner(string ToolName, string? PackageId);

    public sealed record Snapshot(int ResolvedSettingsVersion, AgentProfile Profile, string SharedInstructions,
        string RoleInstructions, ModelBinding? Model, IReadOnlyList<ToolDescriptor> Tools,
        IReadOnlyList<string> Capabilities, IReadOnlyList<string> AllowedTools, IReadOnlyList<string> AllowedRoots,
        bool FullAccess, string RoutingFingerprint, IReadOnlyList<ServicePluginPackageBinding> PackageBindings,
        IReadOnlyList<SkillSnapshot> Skills, CompactorBinding? Compactor = null, CortexAccessScope? CortexScope = null,
        NativeContinuityBinding? NativeContinuity = null, JsonElement? ConversationPaths = null)
    {
        [JsonInclude]
        internal IReadOnlyList<WorkerContextContributionAdmission> ContextContributions { get; init; } = [];
        public string? ContextContributionInstallationId { get; init; }
        // A host-frozen DATA edition is opaque admission content, not a new execution authority.
        [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        internal JsonElement? WorkflowData { get; init; }
        // New edition-6 snapshots built through the public constructor write an explicit empty
        // collection. Older constructor fixtures omit it; their wire formats remain unchanged.
        private IReadOnlyList<WorkflowApprovedSettingsEntry>? approvedPluginSettings = [];
        [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        internal IReadOnlyList<WorkflowApprovedSettingsEntry>? ApprovedPluginSettings
        {
            get => IsCurrentEdition(ResolvedSettingsVersion) ? approvedPluginSettings : null;
            init => approvedPluginSettings = value;
        }
        // Edition 6 freezes a body-free receiver declaration even when unsupported.
        // Older constructor fixtures retain their old wire shape.
        private WorkflowToolReceiptContextConsumer? toolReceiptConsumer =
            new(WorkflowToolReceiptContextConsumerContract.SchemaVersion, false);
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WorkflowToolReceiptContextConsumer? ToolReceiptConsumer
        {
            get => IsCurrentEdition(ResolvedSettingsVersion) ? toolReceiptConsumer : null;
            init => toolReceiptConsumer = value;
        }
        // Optional edition-6 declaration: old snapshots omit it and remain unsupported.
        // It is separate from the receipt-context consumer and grants no authority by itself.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WorkflowLocalGenerationConsumer? LocalGenerationConsumer { get; init; }
        // Only the separately admitted internal Service path sets this marker.
        // Workflow Input can carry matching public facts but never this authority.
        [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        internal SourceBoundWorkflowBinding? SourceBoundBackground { get; init; }
        [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        internal OwnerVerificationAdmission? OwnerVerification { get; init; }
        private IReadOnlyList<SelectedToolOwner>? selectedToolOwners;
        [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        internal IReadOnlyList<SelectedToolOwner>? SelectedToolOwners
        {
            get => IsCurrentEdition(ResolvedSettingsVersion)
                ? selectedToolOwners ?? Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal)
                    .Select(tool => new SelectedToolOwner(tool.Name, null)).ToArray()
                : null;
            init => selectedToolOwners = value;
        }
        [JsonIgnore]
        internal bool HasLegacyApprovedSettings => !IsCurrentEdition(ResolvedSettingsVersion) &&
            approvedPluginSettings is { Count: > 0 };
        [JsonIgnore]
        internal bool HasLegacySelectedToolOwners => !IsCurrentEdition(ResolvedSettingsVersion) &&
            selectedToolOwners is { Count: > 0 };
        // Edition eight binds a saved native chat's exact tool policy. Edition seven keeps
        // its original owner-verification-only meaning for retained runs.
        [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        internal ChatToolPermissions.Binding? ChatPermissions { get; init; }
        [JsonIgnore]
        internal int SourceSettingsVersion { get; init; } = Version;
        [JsonIgnore]
        internal IReadOnlyDictionary<string, JsonElement> LegacyExtensions { get; init; } =
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
    }

    // Presence binds explicit owner consent at admission; older snapshots never inherit a later opt-in.
    public sealed record NativeContinuityBinding(int ProjectSettingsRevision);

    public sealed record CompactorBinding(AgentProfile Profile, string Instructions, ModelBinding Model);

    // Operational state, error messages and upstream URLs are deliberately excluded. Context can be
    // resized by an idle loaded runtime; the prepared lease supplies the actual capacity and count.
    public sealed record ModelBinding(string Id, string Path, string? ChatTemplate,
        IReadOnlyList<string> StopSequences, int MaximumContextTokens, int ParallelRequests, int GpuLayers,
        string? VisionProjectorPath = null, string ModelWorkerId = "local", string? ModelWorkerInstallationId = null);

    public static Task<JsonElement> CaptureAsync(StateStore store, AgentProfile profile,
        Configuration configuration, IToolDispatcher tools,
        IReadOnlyList<ServicePluginPackageBinding> packageBindings, CancellationToken cancellationToken,
        bool requireCompactor = false, string? projectId = null,
        WorkflowToolReceiptContextConsumer? toolReceiptConsumer = null)
        => CaptureWithLocalGenerationAsync(store, profile, configuration, tools, packageBindings,
            cancellationToken, requireCompactor, projectId, toolReceiptConsumer, null);

    internal static async Task<JsonElement> CaptureWithLocalGenerationAsync(StateStore store, AgentProfile profile,
        Configuration configuration, IToolDispatcher tools,
        IReadOnlyList<ServicePluginPackageBinding> packageBindings, CancellationToken cancellationToken,
        bool requireCompactor, string? projectId,
        WorkflowToolReceiptContextConsumer? toolReceiptConsumer,
        WorkflowLocalGenerationConsumer? localGenerationConsumer,
        EventActor? authenticatedCaller = null, string? parentRunId = null, string? definitionId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (toolReceiptConsumer is { SchemaVersion: not WorkflowToolReceiptContextConsumerContract.SchemaVersion })
            throw new InvalidDataException("Unsupported loaded workflow receipt consumer schema.");
        localGenerationConsumer?.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var settings = configuration.Get();
        var frozen = CopyProfile(profile);
        if (frozen.WorkerId != "local") throw new InvalidOperationException("This workflow requires a local execution profile.");
        var routing = RoutingFingerprint(settings);
        var projectKnowledge = new ProjectKnowledge(store, configuration, new EventHub());
        // ScopeFor may create retrieval-only defaults. Only an already saved owner consent can opt in.
        var savedProjectKnowledge = projectId is null ? null : store.Get<ProjectKnowledgeSettings>("project-knowledge", projectId);
        var cortexScope = projectId is null ? null : projectKnowledge.ScopeFor(projectId);
        var nativeContinuity = CaptureNativeContinuity(savedProjectKnowledge, cortexScope, projectKnowledge, configuration);
        var remote = new RemoteWorkerManagement(configuration);
        var resolvedModel = await ResolveModelAsync(store, remote, frozen, cancellationToken).ConfigureAwait(false);
        var model = resolvedModel.Model;
        if (frozen.ModelWorkerId != "local" && ModelCapabilityPolicy.Requirements(frozen).Contains(ModelCapabilityIds.ImageInput))
            throw new NotSupportedException("Remote native chat currently supports text and admitted files only. Choose a local image-enabled model for an image-required agent.");
        if (model is not null)
        {
            var budget = AgentBudgetPolicy.Resolve(frozen, model,
                model.AutomaticContextSizing ? model.ContextTokens : null);
            frozen.ContextTokens = budget.ContextTokens;
            frozen.ResponseTokens = budget.ResponseTokens;
            frozen.ReasoningMode = budget.ReasoningMode;
            frozen.ReasoningTokens = budget.ReasoningTokens;
        }
        frozen.FollowsModelBudgets = false;
        // Capture all prompt layers together; an owner save cannot split a newly accepted
        // assignment across old and new shared, role, and compactor guidance.
        var instructions = new SystemInstructions(store);
        var instructionSources = authenticatedCaller is null ? instructions.CaptureEffective()
            : instructions.CaptureForAssignment(frozen.Role, authenticatedCaller, parentRunId, definitionId);
        var prompts = instructionSources.ToDictionary(x => x.Role, x => x.Content);
        var shared = prompts["system"];
        var role = prompts[frozen.Role is "lead" or "compactor" ? frozen.Role : "helper"];
        var fullAccess = tools.FullAccessActive;
        var descriptors = (await tools.ListAsync(frozen, cancellationToken)).Where(x =>
            AllowsTool(frozen, x.Name) && frozen.Capabilities.Contains(x.Capability, StringComparer.Ordinal)).ToArray();
        if (descriptors.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != descriptors.Length)
            throw new InvalidOperationException("The selected tools contain duplicate semantic names.");
        if (routing != RoutingFingerprint(configuration.Get()))
            throw new InvalidOperationException("Tool routing changed during workflow admission. Submit again with the current configuration.");
        if (fullAccess != tools.FullAccessActive)
            throw new InvalidOperationException("Application access changed during workflow admission. Submit again with the current access setting.");
        CompactorBinding? compactor = null;
        if ((frozen.AutomaticCompaction || requireCompactor) && model is not null)
        {
            var selected = string.IsNullOrWhiteSpace(frozen.CompactionProfileId) || frozen.CompactionProfileId == frozen.Id
                ? frozen : CopyProfile(store.Get<AgentProfile>("agent", frozen.CompactionProfileId)
                    ?? throw new InvalidOperationException("The configured compactor profile is unavailable."));
            if (selected.WorkerId != "local")
                throw new InvalidOperationException("Compaction execution must remain local; its model may use an assigned remote worker.");
            var resolvedCompactor = await ResolveModelAsync(store, remote, selected, cancellationToken).ConfigureAwait(false);
            var selectedModel = resolvedCompactor.Model;
            if (selectedModel is null) throw new InvalidOperationException("The configured compactor model is unavailable.");
            if (selected.ModelWorkerId != "local" && ModelCapabilityPolicy.Requirements(selected).Contains(ModelCapabilityIds.ImageInput))
                throw new NotSupportedException("Remote compaction currently supports text only; its image-required role needs a local compatible model.");
            var budget = AgentBudgetPolicy.Resolve(selected, selectedModel,
                selectedModel.AutomaticContextSizing ? selectedModel.ContextTokens : null);
            selected = CopyProfile(selected);
            selected.ContextTokens = budget.ContextTokens; selected.ResponseTokens = budget.ResponseTokens;
            selected.ReasoningMode = budget.ReasoningMode; selected.ReasoningTokens = budget.ReasoningTokens;
            selected.FollowsModelBudgets = false;
            // The compactor receives data only, with no executable tools or filesystem authority.
            selected.AllowedTools.Clear(); selected.Capabilities.Clear(); selected.AllowedRoots.Clear();
            compactor = new(selected, prompts["compactor"], Bind(selectedModel, selected.ModelWorkerId, resolvedCompactor.InstallationId));
        }
        var skills = new SkillCatalog(store).Resolve(frozen, projectId);
        var snapshot = new Snapshot(Version, frozen, shared, role, model is null ? null : Bind(model, frozen.ModelWorkerId, resolvedModel.InstallationId), descriptors,
            frozen.Capabilities.ToArray(), frozen.AllowedTools.ToArray(), frozen.AllowedRoots.ToArray(),
            fullAccess, routing, packageBindings.ToArray(), skills, compactor, cortexScope, nativeContinuity)
        {
            ContextContributions = [],
            ApprovedPluginSettings = [],
            ToolReceiptConsumer = new(WorkflowToolReceiptContextConsumerContract.SchemaVersion,
                toolReceiptConsumer?.Supported == true),
            LocalGenerationConsumer = localGenerationConsumer is null ? null :
                new(localGenerationConsumer.SchemaVersion, localGenerationConsumer.CommandId,
                    localGenerationConsumer.CommandVersion, localGenerationConsumer.Route),
            ContextContributionInstallationId = settings.InstallationId
        };
        if (routing != RoutingFingerprint(configuration.Get()) || fullAccess != tools.FullAccessActive)
            throw new InvalidOperationException("Model routing or application access changed during workflow admission. Submit again with the current settings.");
        if (projectId is not null)
        {
            var currentScope = projectKnowledge.ScopeFor(projectId);
            if (!WorkflowReferenceRouting.SameScope(cortexScope!, currentScope))
                throw new InvalidOperationException("Project Cortex scope changed during workflow admission. Submit again with the current settings.");
            if (nativeContinuity is not null)
            {
                var currentConsent = CaptureNativeContinuity(store.Get<ProjectKnowledgeSettings>("project-knowledge", projectId),
                    currentScope, projectKnowledge, configuration);
                if (currentConsent != nativeContinuity)
                    throw new InvalidOperationException("Project continuity consent changed during workflow admission. Submit again with the current settings.");
            }
        }
        return Bounded(snapshot);
    }

    internal static JsonElement BindOwnerVerification(JsonElement captured,
        OwnerVerificationAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        admission.Validate();
        var snapshot = Read(captured);
        if (snapshot.ResolvedSettingsVersion is not (Version or ChatPermissionsVersion) ||
            snapshot.OwnerVerification is not null ||
            snapshot.LocalGenerationConsumer is not null || snapshot.SourceBoundBackground is not null)
            throw new InvalidDataException("Owner verification requires one fresh ordinary owner snapshot.");
        var edition = snapshot.ResolvedSettingsVersion == ChatPermissionsVersion
            ? ChatPermissionsVersion : OwnerVerificationVersion;
        return Bounded(snapshot with { ResolvedSettingsVersion = edition,
            SourceSettingsVersion = edition, OwnerVerification = admission });
    }

    public static Snapshot Read(JsonElement value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value).Length > Cerebral.Contracts.Workflows.WorkflowContract.MaximumStateBytes)
            throw new InvalidOperationException("Workflow settings exceed the supported size.");
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EnumerateObject().Count())
            throw new InvalidDataException("Workflow execution settings have duplicate or invalid fields.");
        if (!value.TryGetProperty("resolvedSettingsVersion", out var edition) ||
            edition.ValueKind != JsonValueKind.Number || !edition.TryGetInt32(out var sourceVersion) ||
            sourceVersion is not (2 or 3 or 4 or ClosedLegacyVersion or Version or
                OwnerVerificationVersion or ChatPermissionsVersion))
            throw new InvalidDataException("Unsupported workflow execution snapshot edition.");
        // Reserve the edition-6 field for edition 6 even if serializer reflection changes later.
        // Earlier editions cannot turn an opaque extension into approved settings on replay.
        if (!IsCurrentEdition(sourceVersion) && value.EnumerateObject().Any(property =>
                string.Equals(property.Name, "approvedPluginSettings", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Legacy execution settings cannot carry approved plugin settings.");
        if (!IsCurrentEdition(sourceVersion) && value.EnumerateObject().Any(property =>
                string.Equals(property.Name, "toolReceiptConsumer", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Legacy execution settings cannot carry a receipt consumer binding.");
        if (!IsCurrentEdition(sourceVersion) && value.EnumerateObject().Any(property =>
                string.Equals(property.Name, "localGenerationConsumer", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Legacy execution settings cannot carry a local generation consumer binding.");
        if (!IsCurrentEdition(sourceVersion) && value.EnumerateObject().Any(property =>
                string.Equals(property.Name, "selectedToolOwners", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Legacy execution settings cannot carry selected service tool owners.");
        if (!IsCurrentEdition(sourceVersion) && value.EnumerateObject().Any(property =>
                string.Equals(property.Name, "sourceBoundBackground", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Legacy execution settings cannot carry source-bound background provenance.");
        if (sourceVersion is not (OwnerVerificationVersion or ChatPermissionsVersion) &&
            value.EnumerateObject().Any(property =>
                string.Equals(property.Name, "ownerVerification", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Only the owner verification snapshot edition can carry its admission.");
        if (sourceVersion != ChatPermissionsVersion && value.EnumerateObject().Any(property =>
                string.Equals(property.Name, "chatPermissions", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Only the chat permission snapshot edition can carry its binding.");
        var coreFields = typeof(Snapshot).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                JsonNamingPolicy.CamelCase.ConvertName(property.Name)).ToHashSet(StringComparer.Ordinal);
        // The conditional-ignored member above is deliberately excluded from reflected legacy fields.
        // Editions two/three still reject it; edition four retains its prior opaque extension behavior.
        if (sourceVersion is ClosedLegacyVersion or Version or OwnerVerificationVersion or ChatPermissionsVersion)
            coreFields.Add("workflowData");
        if (IsCurrentEdition(sourceVersion))
        {
            coreFields.Add("approvedPluginSettings");
            coreFields.Add("toolReceiptConsumer");
            coreFields.Add("localGenerationConsumer");
            coreFields.Add("selectedToolOwners");
            coreFields.Add("sourceBoundBackground");
            if (sourceVersion is OwnerVerificationVersion or ChatPermissionsVersion)
                coreFields.Add("ownerVerification");
        }
        if (sourceVersion == ChatPermissionsVersion) coreFields.Add("chatPermissions");
        var extensions = value.EnumerateObject().Where(property => !coreFields.Contains(property.Name))
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        if ((sourceVersion is ClosedLegacyVersion or Version or OwnerVerificationVersion or ChatPermissionsVersion) &&
            (extensions.Count != 0 ||
            !value.TryGetProperty("contextContributions", out var explicitContributions) ||
            explicitContributions.ValueKind != JsonValueKind.Array))
            throw new InvalidDataException("Current execution settings require a closed explicit contribution collection.");
        if (IsCurrentEdition(sourceVersion) && (!value.TryGetProperty("approvedPluginSettings", out var explicitSettings) ||
            explicitSettings.ValueKind != JsonValueKind.Array))
            throw new InvalidDataException("Current execution settings require a closed explicit approved-settings collection.");
        WorkflowToolReceiptContextConsumer? receiptConsumer = IsCurrentEdition(sourceVersion)
            ? value.TryGetProperty("toolReceiptConsumer", out var rawConsumer) ? ReadReceiptConsumer(rawConsumer)
                : throw new InvalidDataException("Current execution settings require an explicit receipt consumer binding.")
            : null;
        WorkflowLocalGenerationConsumer? localGenerationConsumer = IsCurrentEdition(sourceVersion) &&
            value.TryGetProperty("localGenerationConsumer", out var rawLocalConsumer)
                ? ReadLocalGenerationConsumer(rawLocalConsumer) : null;
        IReadOnlyList<SelectedToolOwner>? selectedToolOwners = IsCurrentEdition(sourceVersion)
            ? value.TryGetProperty("selectedToolOwners", out var rawOwners) ? ReadSelectedToolOwners(rawOwners)
                : throw new InvalidDataException("Current execution settings require explicit selected tool owners.")
            : null;
        SourceBoundWorkflowBinding? sourceBoundBackground = IsCurrentEdition(sourceVersion) &&
            value.TryGetProperty("sourceBoundBackground", out var rawSourceBound)
                ? ReadSourceBoundBackground(rawSourceBound) : null;
        OwnerVerificationAdmission? ownerVerification = sourceVersion is OwnerVerificationVersion or ChatPermissionsVersion
            ? value.TryGetProperty("ownerVerification", out var rawVerification)
                ? OwnerVerificationAdmission.Read(rawVerification)
                : sourceVersion == OwnerVerificationVersion
                    ? throw new InvalidDataException("Owner verification snapshot is missing its exact admission.")
                    : null
            : null;
        ChatToolPermissions.Binding? chatPermissions = sourceVersion == ChatPermissionsVersion
            ? value.TryGetProperty("chatPermissions", out var rawChatPermissions)
                ? ChatToolPermissions.ReadBinding(rawChatPermissions)
                : throw new InvalidDataException("Chat permission snapshot is missing its exact binding.")
            : null;
        if (sourceVersion is 2 or 3 && extensions.Count != 0)
            throw new InvalidDataException("Legacy execution settings cannot acquire later extension state.");
        if (sourceVersion is not (ClosedLegacyVersion or Version or OwnerVerificationVersion or ChatPermissionsVersion) &&
            value.TryGetProperty("contextContributions", out var later) &&
            (later.ValueKind != JsonValueKind.Array || later.GetArrayLength() != 0))
            throw new InvalidDataException("Legacy execution settings cannot carry later context selections.");
        JsonElement? workflowData = (sourceVersion is ClosedLegacyVersion or Version or OwnerVerificationVersion or ChatPermissionsVersion) &&
            value.TryGetProperty("workflowData", out var data)
            ? ReadWorkflowData(data) : null;
        var snapshot = value.Deserialize<Snapshot>(new JsonSerializerOptions(CerebralJson.Options) { PropertyNameCaseInsensitive = false })
            ?? throw new InvalidOperationException("Workflow execution settings are missing.");
        if (snapshot.ResolvedSettingsVersion == 2)
        {
            // Version two could not retain a verified remote installation. Recover local history only.
            if (snapshot.Profile is null || snapshot.Profile.ModelWorkerId != "local" ||
                snapshot.Model is { } legacyModel && (legacyModel.ModelWorkerId != "local" || legacyModel.ModelWorkerInstallationId is not null) ||
                snapshot.Compactor is { } legacyCompactor && (legacyCompactor.Profile is null || legacyCompactor.Profile.ModelWorkerId != "local" ||
                    legacyCompactor.Model is null || legacyCompactor.Model.ModelWorkerId != "local" || legacyCompactor.Model.ModelWorkerInstallationId is not null))
                throw new InvalidOperationException("Version-two execution settings have no verified remote model origin. Submit a fresh request.");
            snapshot = snapshot with { Skills = snapshot.Skills ?? [], NativeContinuity = null };
        }
        snapshot = snapshot with
        {
            ResolvedSettingsVersion = sourceVersion is ClosedLegacyVersion or Version or OwnerVerificationVersion or ChatPermissionsVersion
                ? sourceVersion : ClosedLegacyVersion,
            SourceSettingsVersion = sourceVersion,
            ContextContributions = sourceVersion is ClosedLegacyVersion or Version or OwnerVerificationVersion or ChatPermissionsVersion
                ? WorkerContextContributionSnapshot.ReadCollection(value.GetProperty("contextContributions")) : [],
            ContextContributionInstallationId = sourceVersion is 4 or ClosedLegacyVersion or Version or OwnerVerificationVersion or ChatPermissionsVersion
                ? snapshot.ContextContributionInstallationId : null,
            WorkflowData = workflowData,
            ApprovedPluginSettings = IsCurrentEdition(sourceVersion)
                ? WorkflowPortableSettingsProjection.ReadCollection(value.GetProperty("approvedPluginSettings")) : null,
            ToolReceiptConsumer = receiptConsumer,
            LocalGenerationConsumer = localGenerationConsumer,
            SelectedToolOwners = selectedToolOwners,
            SourceBoundBackground = sourceBoundBackground,
            OwnerVerification = ownerVerification,
            ChatPermissions = chatPermissions,
            LegacyExtensions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(extensions)
        };
        if (snapshot.ResolvedSettingsVersion is not (ClosedLegacyVersion or Version or OwnerVerificationVersion or ChatPermissionsVersion) ||
            snapshot.Profile is null || snapshot.Tools is null ||
            snapshot.Capabilities is null || snapshot.AllowedTools is null || snapshot.AllowedRoots is null ||
            snapshot.SharedInstructions is null || snapshot.RoleInstructions is null || snapshot.Skills is null ||
            string.IsNullOrWhiteSpace(snapshot.RoutingFingerprint))
            throw new InvalidOperationException("Workflow execution settings are missing or have an unsupported version.");
        ValidateApprovedBindings(snapshot);
        ValidateSelectedToolOwners(snapshot);
        if (snapshot.ChatPermissions is { } chat &&
            (sourceVersion != ChatPermissionsVersion || chat.ProfileId != snapshot.Profile.Id ||
             chat.ParentRunId is null && chat.OwnerProfileId != chat.ProfileId ||
             chat.ToolExceptions.Any(x => !snapshot.Tools.Any(tool => tool.Name == x.Name)) ||
             chat.CapabilityExceptions.Any(x => !snapshot.Profile.Capabilities.Contains(x,
                 StringComparer.Ordinal))))
            throw new InvalidDataException("Accepted chat permissions do not match the selected snapshot.");
        if (snapshot.SourceBoundBackground is { } sourceBound &&
            (snapshot.Profile.Id != sourceBound.Source.SelectedProfileId ||
             snapshot.Profile.Revision != sourceBound.Source.SelectedProfileRevision ||
             snapshot.Profile.WorkerId != "local" ||
             snapshot.PackageBindings.Count(package => package.Id == sourceBound.Source.PackageId &&
                 package.Version == sourceBound.Source.PackageVersion &&
                 package.Generation == sourceBound.Source.PackageGeneration && package.Loaded) != 1))
            throw new InvalidDataException("Source-bound work disagrees with its frozen profile or package graph.");
        if (snapshot.Model is not null && (snapshot.Model.ModelWorkerId != snapshot.Profile.ModelWorkerId ||
                snapshot.Model.ModelWorkerId != "local" && string.IsNullOrWhiteSpace(snapshot.Model.ModelWorkerInstallationId)) ||
            snapshot.Compactor is { } compactor && (compactor.Model.ModelWorkerId != compactor.Profile.ModelWorkerId ||
                compactor.Model.ModelWorkerId != "local" && string.IsNullOrWhiteSpace(compactor.Model.ModelWorkerInstallationId)))
            throw new InvalidOperationException("Workflow model origin does not match its frozen execution profile. Submit a fresh request.");
        if (snapshot.NativeContinuity is { } continuity)
        {
            if (continuity.ProjectSettingsRevision < 1 || snapshot.CortexScope is not { } scope ||
                string.IsNullOrWhiteSpace(scope.ProjectId) || string.IsNullOrWhiteSpace(scope.ServerId) ||
                scope.ReadNamespaces is null || scope.ReadNamespaces.Count is < 1 or > 8 ||
                string.IsNullOrWhiteSpace(scope.WriteNamespace) || !scope.ReadNamespaces.Contains(scope.WriteNamespace, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(scope.ProviderFingerprint) || scope.ProviderFingerprint == "unavailable")
                throw new InvalidOperationException("Workflow continuity consent has no valid frozen project scope. Submit a fresh request.");
            Configuration.ValidateId(scope.ProjectId);
        }
        if (snapshot.ConversationPaths is { } paths) ConversationPathAuthority.Read(paths);
        WorkerContextContributionHost.ValidateSnapshot(snapshot);
        return snapshot;
    }

    private static WorkflowToolReceiptContextConsumer ReadReceiptConsumer(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version != WorkflowToolReceiptContextConsumerContract.SchemaVersion ||
            !value.TryGetProperty("supported", out var supported) ||
            supported.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("The frozen receipt consumer binding is invalid.");
        return new(version, supported.GetBoolean());
    }

    private static WorkflowLocalGenerationConsumer? ReadLocalGenerationConsumer(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null; // Historical/null is unsupported.
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 4 ||
            !value.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) ||
            !value.TryGetProperty("commandId", out var command) || command.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("commandVersion", out var commandVersion) ||
            commandVersion.ValueKind != JsonValueKind.Number || !commandVersion.TryGetInt32(out var revision) ||
            !value.TryGetProperty("route", out var route) || route.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The frozen local generation consumer binding is invalid.");
        var result = new WorkflowLocalGenerationConsumer(version, command.GetString()!, revision, route.GetString()!);
        result.Validate();
        return result;
    }

    private static SourceBoundWorkflowBinding? ReadSourceBoundBackground(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (JsonSerializer.SerializeToUtf8Bytes(value).Length > 4096)
            throw new InvalidDataException("Source-bound provenance exceeds its bound.");
        Exact(value, "schemaVersion", "workRequestId", "workConversationId", "source");
        var rawSource = value.GetProperty("source");
        Exact(rawSource, "schemaVersion", "ownerBinding", "packageId", "packageVersion",
            "packageGeneration", "sourceDefinitionId", "sourceDefinitionVersion",
            "sourceConversationId", "sourceProfileId", "sourceRequestId", "sourceEventId",
            "sourceEventSequence", "sourceInputSha256", "sourceProjectId", "sourceTitle",
            "sourceTitleRevision", "settingsRevision", "selectedProfileId", "selectedProfileRevision");
        SourceBoundWorkflowBinding marker;
        try
        {
            marker = value.Deserialize<SourceBoundWorkflowBinding>(new JsonSerializerOptions(CerebralJson.Options)
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                RespectRequiredConstructorParameters = true,
                RespectNullableAnnotations = true
            }) ?? throw new InvalidDataException("Source-bound provenance is null.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Source-bound provenance is malformed.", error);
        }
        var source = marker.Source;
        if (marker.SchemaVersion != SourceBoundWorkflowInputContract.SchemaVersion || source is null ||
            source.SchemaVersion != SourceBoundWorkflowInputContract.SchemaVersion ||
            source.SourceEventSequence < 1 || source.SourceTitleRevision < 0 ||
            source.SettingsRevision < 1 || source.SelectedProfileRevision < 1 ||
            source.SourceInputSha256 is not { Length: 64 } hash ||
            hash.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new InvalidDataException("Source-bound provenance has invalid identity or revisions.");
        static void Id(string? text, int maximum = 128)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > maximum ||
                text.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                throw new InvalidDataException("Source-bound provenance contains an invalid identifier.");
        }
        Id(marker.WorkRequestId); Id(marker.WorkConversationId);
        Id(source.OwnerBinding, 256); Id(source.PackageId); Id(source.PackageVersion);
        Id(source.PackageGeneration, 160); Id(source.SourceDefinitionId, 256);
        Id(source.SourceDefinitionVersion); Id(source.SourceConversationId); Id(source.SourceProfileId);
        Id(source.SourceRequestId); Id(source.SourceEventId); Id(source.SelectedProfileId);
        if (source.SourceProjectId is not null) Id(source.SourceProjectId);
        if (source.SourceTitle is null || source.SourceTitle.Length > 320 ||
            source.SourceTitle.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new InvalidDataException("Source-bound provenance contains an invalid source title.");
        return marker;

        static void Exact(JsonElement item, params string[] names)
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != names.Length ||
                item.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != names.Length ||
                item.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
                throw new InvalidDataException("Source-bound provenance has unexpected fields.");
        }
    }

    private static IReadOnlyList<SelectedToolOwner> ReadSelectedToolOwners(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Selected tool owners are invalid.");
        var owners = new List<SelectedToolOwner>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("toolName", out var name) || name.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("packageId", out var package) ||
                package.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("Selected tool owner entry is invalid.");
            owners.Add(new(name.GetString()!, package.ValueKind == JsonValueKind.Null ? null : package.GetString()));
        }
        return Array.AsReadOnly(owners.ToArray());
    }

    private static JsonElement ReadWorkflowData(JsonElement value)
    {
        // Reuse the catalog's exact edition validation/canonical hash; admit no arbitrary extensions.
        if (value.ValueKind != JsonValueKind.Object ||
            JsonSerializer.SerializeToUtf8Bytes(value).Length > WorkflowDataCatalog.MaximumEditionBytes + 2048)
            throw new InvalidDataException("The host-frozen workflow data entry is missing or exceeds its bound.");
        var nodes = 0;
        Check(value, 0);
        try
        {
            var entry = value.Deserialize<WorkflowDataEntry>(new JsonSerializerOptions(CerebralJson.Options)
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 32
            }) ?? throw new InvalidDataException("The host-frozen workflow data entry is null.");
            Configuration.ValidateId(entry.ProjectId);
            if (entry.Hash is null || entry.Hash.Length != 64 ||
                entry.Hash.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')) || entry.Edition is null)
                throw new InvalidDataException("The host-frozen workflow data identity is invalid.");
            WorkflowDataCatalog.Validate(entry.Edition);
            if (WorkflowDataCatalog.Hash(entry.Edition) != entry.Hash)
                throw new InvalidDataException("The host-frozen workflow data content hash does not match.");
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { throw new InvalidDataException("The host-frozen workflow data entry is incompatible.", error); }
        return value.Clone();

        void Check(JsonElement element, int depth)
        {
            if (depth > 32 || ++nodes > 8192)
                throw new InvalidDataException("The host-frozen workflow data entry exceeds its depth/node bound.");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in element.EnumerateObject())
                {
                    if (field.Name.Contains('\0') || !names.Add(field.Name))
                        throw new InvalidDataException("The host-frozen workflow data entry contains duplicate or invalid fields.");
                    Check(field.Value, depth + 1);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Check(item, depth + 1);
        }
    }

    private static NativeContinuityBinding? CaptureNativeContinuity(ProjectKnowledgeSettings? saved, CortexAccessScope? scope,
        ProjectKnowledge projectKnowledge, Configuration configuration)
    {
        if (saved is not { AutomaticContinuity: true, Revision: >= 1 } || scope is null ||
            saved.ProjectId != scope.ProjectId || saved.CortexServerId != scope.ServerId ||
            saved.WriteNamespace != scope.WriteNamespace || saved.RequireKnowledgeApproval != scope.RequireKnowledgeApproval ||
            saved.ReadNamespaces is null || !saved.ReadNamespaces.SequenceEqual(scope.ReadNamespaces, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(scope.WriteNamespace) || string.IsNullOrWhiteSpace(scope.ProviderFingerprint) ||
            scope.ProviderFingerprint == "unavailable") return null;
        try { projectKnowledge.Validate(saved); }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException) { return null; }
        var provider = configuration.Get().McpServers.SingleOrDefault(x => x.Id == scope.ServerId && x.Enabled && !x.RequiresConfiguration);
        return provider is not null && scope.ProviderFingerprint == CortexOperationPolicy.ProviderFingerprint(provider)
            ? new(saved.Revision) : null;
    }

    public static AgentProfile EffectiveProfile(StateStore store, Snapshot snapshot, string profileId)
    {
        if (snapshot.Profile.Id != profileId) throw new UnauthorizedAccessException("Workflow profile identity does not match its accepted settings.");
        var current = store.Get<AgentProfile>("agent", profileId)
            ?? throw new UnauthorizedAccessException("The accepted workflow profile is no longer available.");
        if (current.WorkerId != "local" || snapshot.Profile.WorkerId != "local")
            throw new UnauthorizedAccessException("The workflow's local execution binding changed.");
        var effective = snapshot.SourceSettingsVersion == ChatPermissionsVersion &&
            snapshot.ChatPermissions is { } chatBinding
            ? ChatToolPermissions.Effective(store, snapshot.Profile, snapshot.Tools, chatBinding)
            : CopyProfile(snapshot.Profile);
        // Expand wildcards only against the tools captured at admission. A newly installed tool
        // cannot become available to a task that was accepted before it existed.
        effective.AllowedTools = snapshot.Tools.Select(x => x.Name).Where(x =>
            AllowsTool(effective, x) && (snapshot.ChatPermissions is not null || AllowsTool(current, x)))
            .Distinct(StringComparer.Ordinal).ToList();
        if (snapshot.ChatPermissions is null)
            effective.Capabilities = snapshot.Profile.Capabilities.Intersect(current.Capabilities, StringComparer.Ordinal).ToList();
        effective.AllowedRoots = IntersectRoots(snapshot.Profile.AllowedRoots, current.AllowedRoots);
        return effective;
    }

    public static ModelBinding Bind(ModelRecord model, string modelWorkerId = "local", string? workerInstallationId = null) => new(model.Id, model.Path, model.ChatTemplate,
        model.StopSequences.ToArray(), model.MaximumContextTokens, model.ParallelRequests, model.GpuLayers, model.VisionProjectorPath, modelWorkerId, workerInstallationId);

    public static RemotePreparedModelBinding RemoteBinding(ModelBinding model) => new(model.Id, model.Path, model.ChatTemplate,
        model.StopSequences.ToArray(), model.MaximumContextTokens, model.ParallelRequests, model.GpuLayers, model.VisionProjectorPath);

    public static ModelRecord BudgetModel(ModelBinding model, AgentProfile profile) => new()
    {
        Id = model.Id, Name = model.Id, Path = model.Path, MaximumContextTokens = model.MaximumContextTokens,
        ContextTokens = model.MaximumContextTokens == 0 ? profile.ContextTokens : Math.Min(profile.ContextTokens, model.MaximumContextTokens),
        RecommendedContextTokens = model.MaximumContextTokens == 0 ? profile.ContextTokens : Math.Min(profile.ContextTokens, model.MaximumContextTokens)
    };

    public static bool SameModel(ModelBinding accepted, ModelRecord current) =>
        JsonSerializer.Serialize(accepted, CerebralJson.Options) == JsonSerializer.Serialize(Bind(current, accepted.ModelWorkerId, accepted.ModelWorkerInstallationId), CerebralJson.Options);

    public static bool SameTool(ToolDescriptor accepted, ToolDescriptor current)
    {
        // Prerequisite tools are bounded model-preparation hints, not execution authority. Keep using the
        // descriptor frozen at admission while allowing newer hosts to add or refine only those hints.
        // Cloning and serializing preserves every other present and future descriptor field in the identity.
        var acceptedIdentity = accepted with { PrerequisiteTools = Array.Empty<string>() };
        var currentIdentity = current with { PrerequisiteTools = Array.Empty<string>() };
        return JsonSerializer.Serialize(acceptedIdentity, CerebralJson.Options) ==
            JsonSerializer.Serialize(currentIdentity, CerebralJson.Options);
    }

    public static bool AllowsTool(AgentProfile profile, string name) =>
        profile.AllowedTools.Contains(name, StringComparer.Ordinal) ||
        name != "file.edit-range" && profile.AllowedTools.Contains("*", StringComparer.Ordinal);

    /// <summary>A private configuration equality marker, not a tool receipt or an authorization token.</summary>
    public static string RoutingFingerprint(HostSettings settings) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            settings.ExternalCommandsEnabled, settings.LlamaServerPath, settings.SharedDirectory,
            settings.SharedFilesWorkerId, settings.CortexNamespaces, settings.CortexWriteNamespaces,
            executables = settings.Executables.OrderBy(x => x.Id, StringComparer.Ordinal),
            providers = settings.McpServers.Where(x => x.Enabled).OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => new { x.Id, fingerprint = CortexOperationPolicy.ProviderFingerprint(x) }),
            // The hash binds remote routes without persisting their access keys in the snapshot.
            workers = settings.Workers.OrderBy(x => x.Id, StringComparer.Ordinal)
        }, CerebralJson.Options))).ToLowerInvariant();

    private static async Task<(ModelRecord? Model, string? InstallationId)> ResolveModelAsync(StateStore store,
        RemoteWorkerManagement remote, AgentProfile profile, CancellationToken token)
    {
        if (profile.ModelWorkerId == "local") return (store.Get<ModelRecord>("model", profile.ModelId), null);
        using var session = await remote.ConnectAsync(profile.ModelWorkerId, token).ConfigureAwait(false);
        var model = await session.InspectAsync(profile.ModelId, token).ConfigureAwait(false);
        session.EnsureCurrent();
        return (model, session.Management.WorkerInstallationId);
    }

    private static AgentProfile CopyProfile(AgentProfile profile) => new()
    {
        Id = profile.Id, Revision = profile.Revision, Name = profile.Name, Purpose = profile.Purpose,
        Instructions = profile.Instructions, Role = profile.Role, ModelId = profile.ModelId,
        WorkerId = profile.WorkerId, ModelWorkerId = profile.ModelWorkerId,
        ContextTokens = profile.ContextTokens, ResponseTokens = profile.ResponseTokens,
        FollowsModelBudgets = profile.FollowsModelBudgets, ReasoningMode = profile.ReasoningMode,
        ReasoningTokens = profile.ReasoningTokens, AllowReasoningAdjustment = profile.AllowReasoningAdjustment,
        ReasoningTokenCeiling = profile.ReasoningTokenCeiling, TaskBudgetPolicy = profile.TaskBudgetPolicy,
        Temperature = profile.Temperature,
        MaxSteps = profile.MaxSteps, MaxSeconds = profile.MaxSeconds,
        AutomaticCompaction = profile.AutomaticCompaction, CompactionProfileId = profile.CompactionProfileId,
        CompactionReductionPercent = profile.CompactionReductionPercent, CompactionTriggerPercent = profile.CompactionTriggerPercent,
        AllowedRoots = profile.AllowedRoots.ToList(), AllowedTools = profile.AllowedTools.ToList(),
        Capabilities = profile.Capabilities.ToList(), RequiredModelCapabilities = profile.RequiredModelCapabilities.ToList()
    };

    internal static List<string> IntersectRoots(IEnumerable<string> accepted, IEnumerable<string> current)
    {
        var left = accepted.Select(CanonicalRoot).ToArray();
        var right = current.Select(CanonicalRoot).ToArray();
        var result = new HashSet<string>(PathComparer);
        foreach (var a in left)
            foreach (var b in right)
                if (Within(a, b)) result.Add(a); else if (Within(b, a)) result.Add(b);
        return result.OrderBy(x => x, PathComparer).ToList();
    }

    private static string CanonicalRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split(['/', '\\']).Contains("..", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Workflow roots must be absolute paths without parent traversal.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool Within(string path, string root) => PathComparer.Equals(path, root) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static JsonElement Bounded(object value)
    {
        if (value is Snapshot snapshot)
        {
            if (IsCurrentEdition(snapshot.ResolvedSettingsVersion))
            {
                _ = WorkflowPortableSettingsProjection.ValidateCollection(snapshot.ApprovedPluginSettings
                    ?? throw new InvalidDataException("Current execution settings require an approved-settings collection."));
                snapshot.LocalGenerationConsumer?.Validate();
                if (snapshot.ResolvedSettingsVersion == OwnerVerificationVersion)
                    (snapshot.OwnerVerification ?? throw new InvalidDataException("Owner verification admission is missing.")).Validate();
                else if (snapshot.ResolvedSettingsVersion == ChatPermissionsVersion)
                    snapshot.OwnerVerification?.Validate();
                else if (snapshot.OwnerVerification is not null)
                    throw new InvalidDataException("The ordinary snapshot cannot carry owner verification admission.");
            }
            else if (snapshot.ResolvedSettingsVersion != ClosedLegacyVersion || snapshot.HasLegacyApprovedSettings ||
                      snapshot.HasLegacySelectedToolOwners || snapshot.LocalGenerationConsumer is not null)
                throw new InvalidDataException("Legacy execution settings cannot carry approved plugin settings.");
            ValidateApprovedBindings(snapshot);
            ValidateSelectedToolOwners(snapshot);
            if (snapshot.ResolvedSettingsVersion == ChatPermissionsVersion)
            {
                if (snapshot.ChatPermissions is null)
                    throw new InvalidDataException("The chat permission snapshot has no binding.");
            }
            else if (snapshot.ChatPermissions is not null)
                throw new InvalidDataException("Earlier execution settings cannot carry chat permissions.");
        }
        // Seed encoding is opaque and canonically compact; surrounding persistence must not inflate it.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false });
        if (bytes.Length > Cerebral.Contracts.Workflows.WorkflowContract.MaximumStateBytes)
            throw new InvalidOperationException("Selected instructions and tool descriptors exceed the workflow settings limit. Select a smaller tool set.");
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static void ValidateApprovedBindings(Snapshot snapshot)
    {
        if (!IsCurrentEdition(snapshot.ResolvedSettingsVersion)) return;
        if (snapshot.PackageBindings is null) throw new InvalidDataException("Approved settings have no package binding.");
        foreach (var entry in snapshot.ApprovedPluginSettings ??
                     throw new InvalidDataException("Approved settings collection is missing."))
        {
            var matches = snapshot.PackageBindings.Where(binding => binding.Id == entry.PluginId).ToArray();
            if (matches.Length != 1 || !matches[0].Loaded || matches[0].Version != entry.PluginVersion ||
                matches[0].Generation != entry.LoadedGeneration)
                throw new InvalidDataException("Approved settings do not match their frozen package binding.");
        }
    }

    private static void ValidateSelectedToolOwners(Snapshot snapshot)
    {
        if (!IsCurrentEdition(snapshot.ResolvedSettingsVersion)) return;
        var selected = snapshot.SelectedToolOwners ??
            throw new InvalidDataException("Selected service tool owners are missing.");
        var names = snapshot.Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (selected.Count != names.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Selected service tool owners do not cover the selected tools.");
        for (var index = 0; index < names.Length; index++)
        {
            var owner = selected[index];
            if (owner is null || owner.ToolName != names[index] ||
                owner.PackageId is { } packageId &&
                (string.IsNullOrWhiteSpace(packageId) || packageId.Length > 128 ||
                    snapshot.PackageBindings.Count(binding => binding.Id == packageId && binding.Loaded) != 1))
                throw new InvalidDataException("Selected service tool owner does not match its frozen tool and package graph.");
        }
    }
}
