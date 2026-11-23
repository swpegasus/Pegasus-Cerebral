using System.Net.Http.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher(StateStore store, Configuration configuration, ModelRuntime runtime, JobScheduler scheduler, ToolDispatcher tools, EventHub events, LocalRunControl? localRun = null, IToolDispatcher? recordedTools = null, ServicePluginRuntime? servicePlugins = null, WorkerWorkflows? workflows = null, WorkerAttachmentTransfers? attachments = null, PeerAccessControl? peerAccess = null, ServicePackageLifecycle? servicePackageLifecycle = null, SkillCatalog? skillCatalog = null, ProcessorPolicyState? processorPolicy = null, CollaborationService? collaboration = null, Func<WorkerRegistration, IRemoteWorkerOwnerClient>? remoteWorkerClientFactory = null, RemotePreparedInference? remotePreparedInference = null) : ICommandDispatcher
{
    // Core owns these transport namespaces. Plugins contribute their own domain namespace, so a new
    // host operation cannot accidentally be shadowed by an older package loaded before the switch.
    private static readonly HashSet<string> HostOperationNamespaces = new(StringComparer.Ordinal)
    {
        "health", "service", "resources", "metrics", "diagnostics", "settings", "access", "instructions", "agents",
        "models", "workers", "jobs", "queue", "activity", "sessions", "cortex", "projects", "knowledge",
        "navigation", "failures", "plugins", "service-packages", "skills", "questions", "interface", "tools", "shared", "inference", "sdk", "configuration", "conversations", "collaboration", "peer", "worker", "engine", "internet"
    };
    public static bool IsHostOperationName(string operation) => HostOperationNamespaces.Contains(operation.Split('.', 2)[0]);
    private readonly ActivityJournal activityJournal = new(store, events, configuration.Get);
    private readonly IToolDispatcher executionTools = recordedTools ?? new RecordingToolDispatcher(tools, new ActivityJournal(store, events, configuration.Get));
    private readonly SemaphoreSlim modelFiles = new(1, 1);
    private RemoteWorkerManagement? remoteModelManagement;
    private RemoteWorkerManagement remoteModels => remoteModelManagement ??= new(configuration, remoteWorkerClientFactory, HasPlacementAssignments);
    private readonly InterfaceRequests interfaceRequests = new(store, events, () => tools.FullAccessActive, () => configuration.Get().MaxExecutableAttemptsPerAssignment);
    private readonly PluginControlRequests pluginControls = new(store, events);
    private readonly SystemInstructions systemInstructions = new(store);
    private readonly AgentInstructions agentInstructions = new(store, events);
    private readonly object resourceSampleGate = new();
    private long priorResourceProcessorTicks;
    private long priorResourceSampleTicks;
    private readonly PeerAccessControl peers = peerAccess ?? new PeerAccessControl(store, configuration, scheduler);
    private readonly ConfigurationBackups configurationBackups = new(store,
        () => store.ActiveJobCount() > 0 || workflows?.Store.CountActive() > 0,
        () => { servicePlugins?.QueueAllOwnSettingsRefresh();
            events.Publish("settings.updated", null, new { configurationRestored = true }); },
        pluginSettingsSchemas: () => servicePlugins?.PortableSettingsSchemas ??
            new Dictionary<string, ServicePluginPortableSettingsBinding>(StringComparer.Ordinal))
    {
        SettingsPolicies = () => servicePlugins?.SettingsPolicies ??
            new Dictionary<string, SettingsPolicyBinding>(StringComparer.Ordinal)
    };
    private readonly SettingsPreferencesStore settingsPreferences = new(store, () => servicePlugins?.SettingsPolicies ??
        new Dictionary<string, SettingsPolicyBinding>(StringComparer.Ordinal));
    public HostInfo Health() => new("Pegasus Cerebral", typeof(CommandDispatcher).Assembly.GetName().Version?.ToString(3) ?? "0.1.0", 1, configuration.Get().Name, RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSDescription, configuration.Get().Coordinator, store.DirectoryPath, localRun?.Enabled == true, configuration.Get().InstallationId);
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            // The early recovery pipe is primary, but the attached dispatcher also fails closed
            // before profile lookup, normalization, plugin dispatch, or model/tool admission.
            if (localRun?.SourceRecovery is { } recovery &&
                !recovery.RuntimeAdmissionOpen &&
                !recovery.AllowsAttachedInspection(request))
                return CommandResult.Fail("source_recovery_held");
            // These two package value commands trust identities authored by the private workflow
            // admission path. Generic owner/admin dispatch must not authenticate caller-supplied JSON.
            if (OwnerModelSnapshotService.IsOwnerOperation(request.Operation))
                return CommandResult.Fail("verified_owner_context_required");
            if (request.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Command arguments must be a JSON object.");
            if (HasOwnerVerificationField(request))
                throw new UnauthorizedAccessException("Owner verification requires the authenticated local owner pipe.");
            var args = request.Arguments; var ct = cancellationToken;
            AgentProfile? actor = request.ProfileId is null ? null : store.Get<AgentProfile>("agent", request.ProfileId) ?? throw new UnauthorizedAccessException("Calling agent profile does not exist.");
            Authorize(request.Operation, actor, args);
            if (request.Operation is "inference.prepare" or "inference.prepared.generate" or "inference.prepared.release")
            {
                var status = configuration.ManagementStatus();
                if (configuration.Get().Coordinator || status.State != "assigned" || string.IsNullOrWhiteSpace(status.ControllerInstallationId))
                    throw new UnauthorizedAccessException("Prepared remote inference requires a standalone worker assigned to its owning coordinator.");
                var prepared = remotePreparedInference ?? throw new InvalidOperationException("Prepared remote inference is not registered on this Worker.");
                return request.Operation switch
                {
                    "inference.prepare" => await prepared.PrepareAsync(args, ct),
                    "inference.prepared.generate" => await prepared.GenerateAsync(args, ct),
                    _ => await prepared.ReleaseAsync(args, ct)
                };
            }
            object? data;
            if (request.Operation.StartsWith("inference.isolated.", StringComparison.Ordinal))
                data = await IsolatedTextCommandAsync(request.Operation, args, ct);
            else if (request.Operation.StartsWith(WorkerAttachmentTransfers.Prefix, StringComparison.Ordinal))
                data = await (attachments ?? throw new InvalidOperationException("Attachment transfer service is unavailable. Reconnect to a current Worker."))
                    .ExecuteAsync(request.Operation, args, actor, ct);
            else if (CollaborationService.Operations.Contains(request.Operation, StringComparer.Ordinal))
                data = await (collaboration ?? throw new InvalidOperationException("Collaboration service is unavailable. Reconnect to a current Worker."))
                    .ExecuteAsync(request.Operation, args, actor, ct);
            else if (WorkerWorkflows.Operations.Contains(request.Operation, StringComparer.Ordinal))
                data = await WorkflowCommandAsync(request.Operation, args, actor, ct);
            else if (request.Operation.StartsWith("interface.native-plugins.", StringComparison.Ordinal))
                data = NativePluginManagementCommand(request.Operation, args);
            else if (request.Operation.StartsWith("settings.plugins.", StringComparison.Ordinal) ||
                request.Operation.StartsWith("interface.layout.", StringComparison.Ordinal))
                data = SettingsPreferenceCommand(request.Operation, args, actor);
            else if (request.Operation.StartsWith("configuration.", StringComparison.Ordinal))
                data = await ConfigurationCommandAsync(request.Operation, args, actor, ct);
            else if (request.Operation.StartsWith("engine.", StringComparison.Ordinal))
                data = await EngineCommandAsync(request.Operation, args, ct);
            else if (!IsHostOperationName(request.Operation) && servicePlugins?.CommandFor(request.Operation) is { } pluginCommand)
                data = await pluginCommand.ExecuteAsync(request.Operation, args, ct);
            else data = request.Operation switch
            {
                "health" => Health(),
                "service.stop-local" => StopLocal(),
                "resources" => await HardwareProbe.ReadAsync(ct),
                "resources.local.status" => scheduler.GetResourcePauseStatus(),
                "resources.local.pause" => await scheduler.PauseLocalResourcesAsync(Flag(args, "rerouteEligibleWork"), ct),
                "resources.local.resume" => await scheduler.ResumeLocalResourcesAsync(ct),
                "metrics.status" => await StatusMetricsAsync(args, ct),
                "metrics.execution.status" => ReadExecutionMetrics(args, ct),
                "metrics.inference.status" => ReadInferenceObservation(args, ct),
                "metrics.slots.status" => ReadManagedSlotMetrics(args, ct),
                "metrics.generation" => ReadGenerationTelemetry(args),
                "diagnostics.prompt-cache.get" => PromptCacheDiagnostics.Read(store, configuration, Number(args, "limit", 100, 1, 200)),
                "peer.metrics" => await peers.MetricsAsync(ct),
                "peer.grants.list" => peers.List(),
                "peer.grants.create" => peers.Create(args),
                "peer.grants.rotate" => peers.Rotate(Text(args, "id")),
                "peer.grants.drain" => peers.Drain(Text(args, "id"), Flag(args, "draining")),
                "peer.grants.revoke" => peers.Revoke(Text(args, "id")),
                "diagnostics" => new { host = Health(), resources = await HardwareProbe.ReadAsync(ct), processors = (processorPolicy ?? ProcessorPolicyState.NotInitialized(configuration.Get())).Diagnostics(configuration.Get()), models = runtime.Diagnostics(), tools = tools.Diagnostics, servicePlugins = servicePlugins?.Status ?? [], jobs = store.List<AgentJob>("job").GroupBy(x => x.Status).ToDictionary(x => x.Key, x => x.Count()) },
                "settings.get" => configuration.Get(),
                "settings.save" => SaveSettings(args),
                "worker.management.status" => configuration.ManagementStatus(),
                "worker.management.claim" => configuration.ClaimStandalone(Text(args, "provisioningId"),
                    Text(args, "controllerInstallationId"), Text(args, "controllerName"), Text(args, "replacementManagementKey")),
                "access.full.status" => FullAccessState(),
                "access.full.enable" => await tools.EnableFullAccessAsync(ct),
                "access.full.rotate" => await tools.RotateFullAccessAsync(ct),
                "access.full.disable" => await tools.DisableFullAccessAsync(ct),
                "instructions.get" => systemInstructions.Get(Optional(args, "role")),
                "instructions.save" => systemInstructions.Save(Optional(args, "content") ?? throw new ArgumentException("Instruction content must be a string; an empty string disables this shared layer."), Text(args, "expectedSha256"), Optional(args, "role")),
                "agents.instructions.get" => agentInstructions.Get(args),
                "agents.instructions.save" => agentInstructions.Save(args),
                "agents.list" => store.List<AgentProfile>("agent"),
                "agents.save" => await SaveAgentAsync(CerebralJson.Read<AgentProfile>(args), ct),
                "agents.task-budget-policy.save" => await SaveTaskBudgetPolicyAsync(args, ct),
                "agents.bootstrap" => await BootstrapAgentsAsync(Text(args, "modelId"), Optional(args, "cortexServerId"), ct),
                "models.default.switch" => await SwitchDefaultModelCommandAsync(args, false, ct),
                "workers.models.default" => await SwitchDefaultModelCommandAsync(args, true, ct),
                "workers.models.recover" => await RecoverRemoteModelAsync(args, ct),
                "workers.models.capacity.preview" or "workers.models.capacity.apply" or "workers.models.list" or "workers.models.load" or "workers.models.unload" or "workers.models.save" or "workers.models.configure" or "workers.models.inspect" or "workers.models.diagnostics" or "workers.models.scan" or "workers.models.import" or "workers.models.download" or "workers.models.idle.set" => await remoteModels.ExecuteAsync(request.Operation, args, ct),
                "workers.inference.generate" => await remoteModels.GenerateAsync(args, ct),
                "agents.delete" => DeleteAgent(Text(args, "id")),
                "models.list" => await ListModelsAsync(ct),
                "models.inspect" => await InspectModelAsync(Text(args, "id"), ct),
                "models.content.describe" => await DescribeModelContentAsync(args, ct),
                "models.verify-image" => await VerifyModelImageAsync(args, ct),
                "models.scan" => await new ModelRepository(configuration.Get().ModelDirectory).ScanAsync(store.List<ModelRecord>("model"), ct),
                "models.huggingface" => await ModelRepository.HuggingFaceAsync(Text(args, "repository"), Optional(args, "revision") ?? "main", ct),
                "models.package.import" => await ImportPackageAsync(Text(args, "path"), ct),
                "models.package.export" => await ExportPackageAsync(Text(args, "id"), Text(args, "path"), Optional(args, "settingsMode") ?? "current", ct),
                "models.import" => await ImportAsync(args, ct),
                "models.download" => await DownloadAsync(args, ct),
                "models.component.download" => await DownloadModelComponentAsync(args, ct),
                ModelCapacityProtocol.PreviewOperation => await ModelCapacityPreviewAsync(args, ct),
                ModelCapacityProtocol.ApplyOperation => await ModelCapacityApplyAsync(args, ct),
                "models.save" => await SaveModelSafelyAsync(CerebralJson.Read<ModelRecord>(args), ct, args.TryGetProperty("visionProjectorPath", out _)),
                "models.configure" => await ConfigureModelComponentAsync(args, ct),
                "models.idle.set" => await SetModelIdleAsync(args, ct),
                "models.load" => await LoadModelWithFileLockAsync(Text(args, "id"), ct),
                "models.load-tracked" => await LoadTrackedModelAsync(Text(args, "id"), ct),
                "models.runtime.status" => await ModelRuntimeStatusAsync(ct),
                ModelRuntimeSupportQueryProtocol.ReadOperation => await ReadNativeRuntimeSupportAsync(args, ct),
                ModelRuntimeSupportQueryProtocol.RemoteReadOperation => await remoteModels.ExecuteAsync(request.Operation, args, ct),
                "models.replace" => await ReplaceModelAsync(Text(args, "id"), Text(args, "path"), ct),
                "models.unload" => await UnloadAsync(Text(args, "id"), ct),
                "models.delete" => await DeleteModelAsync(Text(args, "id"), ct),
                "models.distribute" => await DistributeAsync(Text(args, "id"), Text(args, "workerId"), ct),
                "workers.probe" => await ProbeAsync(Text(args, "id"), ct),
                "jobs.submit" => scheduler.Submit(CerebralJson.Read<SubmitJobRequest>(args)),
                "jobs.list" => store.List<AgentJob>("job"),
                "queue.list" => scheduler.Queue(),
                "jobs.history" => scheduler.History(args.TryGetProperty("limit", out var historyLimit) ? historyLimit.GetInt32() : null, args.TryGetProperty("days", out var historyDays) ? historyDays.GetInt32() : null),
                "jobs.get" => scheduler.Get(Text(args, "id")),
                "jobs.cancel" => scheduler.Cancel(Text(args, "id")),
                "jobs.steer" => scheduler.Steer(Text(args, "id"), Text(args, "prompt"), Optional(args, "requestId")),
                "jobs.reconcile" => scheduler.Reconcile(Text(args, "id")),
                "activity.turns" => activityJournal.Turns(Text(args, "sessionId")),
                "activity.list" => activityJournal.List(Text(args, "sessionId"), Number(args, "turnSequence", 0, 0, int.MaxValue),
                    args.TryGetProperty("afterSequence", out var cursor) && cursor.ValueKind != JsonValueKind.Null ? cursor.GetInt64() : 0,
                    Number(args, "limit", 40, 1, 100)),
                "sessions.stop" => scheduler.StopConversation(Text(args, "id")),
                "sessions.rename" => new WorkspaceManagement(store, events, scheduler).Rename(Text(args, "id"), Text(args, "title")),
                "sessions.response-balance" => SetResponseBalance(Text(args, "id"), args),
                "conversations.properties.get" => ConversationPropertiesCommand(args, save: false),
                "conversations.properties.save" => ConversationPropertiesCommand(args, save: true),
                "internet.capabilities" => InternetCapabilitiesRead(args),
                "internet.policy.get" => InternetGlobalGet(args),
                "internet.policy.save" => InternetGlobalSave(args),
                "conversations.internet.get" => InternetMainGet(args),
                "conversations.internet.save" => InternetMainSave(args),
                "internet.requests.get" => InternetRequestGet(args),
                "internet.requests.list" => InternetRequestList(args),
                "internet.requests.decide" => InternetRequestDecide(args),
                "internet.requests.retire" => InternetRequestRetire(args),
                "conversations.permissions.get" => ConversationPermissionsGet(args),
                "conversations.permissions.save" => await ConversationPermissionsSaveAsync(args, ct),
                "conversations.permissions.catalog" => await ConversationPermissionsCatalogAsync(args, ct),
                "conversations.paths.get" => ConversationPathsCommand(args, save: false),
                "conversations.paths.save" => ConversationPathsCommand(args, save: true),
                "conversations.plugin-install.get" => ConversationPluginInstallPermissionCommand(args, save: false, actor),
                "conversations.plugin-install.save" => ConversationPluginInstallPermissionCommand(args, save: true, actor),
                "interface.install-authorization.get" => ReadChatPluginInstallAuthorization(args, actor),
                "interface.install-authorization.recheck" => RecheckChatPluginInstallEffect(args, actor),
                "conversations.workspace.get" => new ConversationWorkspaces(store, workflows, events).Get(Text(args, "conversationId")),
                "conversations.workspace.set" => new ConversationWorkspaces(store, workflows, events).Set(
                    Text(args, "conversationId"), Text(args, "pluginId"), Text(args, "surfaceId"), Text(args, "resourceId"),
                    args.TryGetProperty("expectedRevision", out var workspaceRevision) ? workspaceRevision.GetInt64() : 0),
                "conversations.workspace.clear" => new ConversationWorkspaces(store, workflows, events).Clear(
                    Text(args, "conversationId"), args.GetProperty("expectedRevision").GetInt64()),
                "cortex.assets.providers" => CortexAssetProviders(args),
                "cortex.assets.read" => await ReadCortexAssetResourceAsync(args, ct),
                "cortex.connection.inspect" => await tools.InspectCortexAsync(Text(args, "serverId"), ct),
                "cortex.continuity.get" => new CortexContinuity(store, configuration, executionTools, events).Get(Text(args, "sessionId")),
                "cortex.native-continuity.get" => workflows?.NativeContinuity?.Get(Text(args, "conversationId")),
                "cortex.native-continuity.inspect" => await InspectNativeContinuityAsync(args, ct),
                "projects.knowledge.list" => new ProjectKnowledge(store, configuration, events).List(),
                "projects.knowledge.get" => new ProjectKnowledge(store, configuration, events).Get(Text(args, "projectId")),
                "projects.knowledge.save" => new ProjectKnowledge(store, configuration, events).Save(CerebralJson.Read<ProjectKnowledgeSettings>(args)),
                "sessions.project" => new ProjectKnowledge(store, configuration, events).BindSession(Text(args, "id"), Optional(args, "projectId")),
                "knowledge.retrieval.get" => new CortexContext(store, configuration, executionTools, events).Get(Text(args, "jobId")),
                "knowledge.proposals.list" => new KnowledgeProposals(store, configuration, executionTools, events).List(Text(args, "sessionId")),
                "knowledge.proposals.get" => new KnowledgeProposals(store, configuration, executionTools, events).Get(Text(args, "id")),
                "navigation.get" => new WorkspaceManagement(store, events, scheduler).GetNavigation(),
                "navigation.save" => new WorkspaceManagement(store, events, scheduler, workflows?.Store).SaveNavigation(CerebralJson.Read<NavigationState>(args.GetProperty("navigation"))),
                "failures.list" => new WorkspaceManagement(store, events, scheduler).Failures(Text(args, "sessionId")),
                "failures.resolve" => new WorkspaceManagement(store, events, scheduler).ResolveFailure(Text(args, "id"), Text(args, "note")),
                "plugins.list" => new PluginCatalog(store, configuration).List(),
                "plugins.validate" => new PluginCatalog(store, configuration).Validate(CerebralJson.Read<PluginManifest>(args.GetProperty("manifest"))),
                "plugins.inspect" => new PluginCatalog(store, configuration).Inspect(Text(args, "id")),
                "plugins.install" => InstallPlugin(CerebralJson.Read<PluginManifest>(args.GetProperty("manifest"))),
                "plugins.remove" => RemovePlugin(Text(args, "id")),
                "skills.list" => Skills().List(),
                "skills.inspect" => Skills().Inspect(Text(args, "id")),
                "skills.install" => Skills().Install(CerebralJson.Read<SkillManifest>(args.GetProperty("manifest")),
                    Text(args, "content"), Text(args, "source"),
                    args.TryGetProperty("expectedRevision", out var skillRevision) && skillRevision.ValueKind != JsonValueKind.Null
                        ? skillRevision.GetInt32() : null),
                "skills.configure" => Skills().Configure(Text(args, "id"), Flag(args, "enabled"),
                    CerebralJson.Read<List<SkillBinding>>(args.GetProperty("bindings")),
                    Number(args, "expectedRevision", 0, 1, int.MaxValue)),
                "skills.remove" => RemoveSkill(Text(args, "id"), Number(args, "expectedRevision", 0, 1, int.MaxValue)),
                "service-packages.inspect" => Packages().Inspect(Text(args, "id"), store),
                "service-packages.stage" => Packages().Stage(Text(args, "manifestPath"),
                    args.GetProperty("expectedRevision").GetInt64(),
                    args.TryGetProperty("expectedId", out var stageExpectedId) && stageExpectedId.ValueKind != JsonValueKind.Null
                        ? Text(args, "expectedId") : null),
                "service-packages.activate" => Packages().Activate(Text(args, "id"), args.GetProperty("expectedRevision").GetInt64()),
                "service-packages.deactivate" => Packages().Deactivate(Text(args, "id"), args.GetProperty("expectedRevision").GetInt64()),
                "service-packages.remove" => Packages().Remove(Text(args, "id"), args.GetProperty("expectedRevision").GetInt64()),
                "service-packages.workflow-settings.approve" => Packages().ApproveWorkflowSettings(store,
                    WorkflowPortableSettingsApprovalInput.Parse(args)),
                 "sessions.list" => store.List<TaskSession>("session"),
                 "sessions.get" => SessionPage(args),
                 "sessions.longform" => GetLongFormStatus(Text(args, "id")),
                 "sessions.compact" => QueueCompaction(Text(args, "id")),
                "sessions.exclude" => ExcludeContext(Text(args, "id"), Number(args, "sequence", 0, 1, int.MaxValue), !args.TryGetProperty("includeFollowing", out var include) || include.ValueKind == JsonValueKind.True),
                "questions.list" => store.List<UserQuestion>("question").Where(x => x.SessionId == Text(args, "sessionId")).ToList(),
                "questions.answer" => scheduler.Answer(Text(args, "id"), Text(args, "answer")),
                "interface.list" => interfaceRequests.List(Text(args, "sessionId"), Number(args, "limit", 50, 1, 100)),
                "interface.get" => ReadInterfaceForOwner(args),
                "interface.retry" => interfaceRequests.RetryPlugin(Text(args, "id")),
                "interface.repair" => RepairInterface(args),
                "interface.claim" => args.TryGetProperty("chatInstallAuthorization", out _)
                    ? ClaimChatAuthorizedPlugin(args, actor)
                    : interfaceRequests.Claim(Text(args, "id"), Text(args, "claimId")),
                "interface.observe" => interfaceRequests.Observe(CerebralJson.Read<ApplicationObservation>(args)),
                "interface.complete" => CompleteInterface(args, ct),
                 "interface.controls.claim" => pluginControls.Claim(Text(args, "id"), Text(args, "sessionId"), Text(args, "claimId")),
                 "interface.controls.ready" => ReadyPluginControl(args),
                 "interface.controls.complete" => CompletePluginControl(args),
                "interface.preferences.get" => new InterfacePreferencesStore(store).Get(),
                "interface.preferences.set" => SaveInterfacePreference(args),
                "interface.theme.get" => new InterfaceThemeStore(store).Read(args),
                "interface.theme.set" => SaveInterfaceTheme(args),
                "tools.list" => await ListToolsAsync(args, actor, ct),
                "tools.registered.catalog" => await RegisteredToolsCatalogAsync(args, ct),
                "shared.call" => await SharedCallAsync(args, actor, ct),
                "tools.call" => await CallToolAsync(args, actor, ct),
                "inference.generate" => new { text = await runtime.GenerateAsync(Text(args, "modelId"), CerebralJson.Read<List<ModelMessage>>(args.GetProperty("messages")), Number(args, "maxTokens", 1024, 1, 131072), args.TryGetProperty("temperature", out var t) ? t.GetDouble() : 0.2, Flag(args, "json"), null, ct, args.TryGetProperty("responseSchema", out var schema) && schema.ValueKind != JsonValueKind.Null ? schema : null, Optional(args, "reasoningMode") ?? "off", Number(args, "reasoningTokens", 1024, 128, 32768)) },
                "inference.count" => await runtime.CountTokensAsync(Text(args, "modelId"), CerebralJson.Read<List<ModelMessage>>(args.GetProperty("messages")), ct, Optional(args, "reasoningMode") ?? "off"),
                _ => throw new ArgumentException("Unknown command: " + request.Operation)
            };
            return CommandResult.Ok(data);
        }
        catch (OperationCanceledException) { return CommandResult.Fail("Command cancelled or timed out. Inspect any interrupted action before retrying."); }
        catch (Exception ex) { return CommandResult.Fail(ex.Message); }
    }
    private object InstallPlugin(PluginManifest manifest) { var result = new PluginCatalog(store, configuration).Install(manifest); events.Publish("plugins.updated", null, new { }); events.Publish("settings.updated", null, new { }); return result; }
    private object RemovePlugin(string id) { var result = new PluginCatalog(store, configuration).Remove(id); events.Publish("plugins.updated", null, new { }); events.Publish("settings.updated", null, new { }); return result; }
    private SkillCatalog Skills() => skillCatalog ?? new SkillCatalog(store, events);
    private object RemoveSkill(string id, int expectedRevision) { Skills().Remove(id, expectedRevision); return new { id, removed = true }; }
    private ServicePackageLifecycle Packages() => servicePackageLifecycle ??
        throw new InvalidOperationException("Service package lifecycle is unavailable. Reconnect to a current Worker.");
    private InterfaceRequest ReadInterfaceForOwner(JsonElement args)
    {
        var fields = args.EnumerateObject().ToArray();
        if (fields.Length != 2 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            fields.Any(field => field.Name is not ("id" or "sessionId")))
            throw new ArgumentException("Exact owner receipt inspection accepts only id and sessionId.");
        return interfaceRequests.GetForOwner(Text(args, "id"), Text(args, "sessionId"));
    }

    private object CompleteInterface(JsonElement args, CancellationToken token)
    {
        var request = interfaceRequests.GetForOwner(Text(args, "id"));
        InterfaceResourceReceipt? resource = null;
        if (Optional(args, "resourceRequestId") is { } requestId)
        {
            if (request.Kind != "plugin") throw new ArgumentException("Only a native plugin presentation can publish a desktop capture receipt.");
            var attachment = (attachments ?? throw new InvalidOperationException("Attachment transfer service is unavailable. Reconnect to a current Worker."))
                .ResolveImport(request.ProfileId, request.SessionId, requestId, token);
            resource = new(attachment.Id, attachment.Revision, attachment.MediaType, attachment.OriginalName, attachment.Length);
        }
        InterfaceFailureStage? failureStage = null;
        if (args.TryGetProperty("failureStage", out var stage) && stage.ValueKind != JsonValueKind.Null)
        {
            if (stage.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Native plugin failureStage must be a named string value or null.");
            failureStage = CerebralJson.Read<InterfaceFailureStage>(stage);
        }
        return interfaceRequests.Complete(request.Id, Text(args, "claimId"), Flag(args, "success"),
            Optional(args, "error"), Optional(args, "message"), resource,
            args.TryGetProperty("plugin", out var plugin) && plugin.ValueKind != JsonValueKind.Null
                ? CerebralJson.Read<InterfacePluginReceipt>(plugin) : null,
            Flag(args, "controlTestsAllowed"), failureStage);
    }
    private object RepairInterface(JsonElement args)
    {
        var fields = args.EnumerateObject().ToArray();
        if (fields.Length != 3 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Any(field => field.Name is not ("id" or "sessionId" or "controlTestsAllowed")) ||
            !args.TryGetProperty("controlTestsAllowed", out var consent) ||
            consent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Plugin repair accepts only exact id, sessionId, and boolean controlTestsAllowed.");
        var id = Text(args, "id");
        var sessionId = Text(args, "sessionId");
        if (id.Length > 128 || sessionId.Length > 128)
            throw new ArgumentException("Plugin repair id and sessionId are limited to 128 characters.");
        return interfaceRequests.SchedulePluginRepair(id, sessionId, consent.GetBoolean());
    }
    private object CompletePluginControl(JsonElement args) => pluginControls.Complete(
        Text(args, "id"), Text(args, "sessionId"), Text(args, "claimId"), Text(args, "status"),
        args.TryGetProperty("observation", out var observation) && observation.ValueKind != JsonValueKind.Null
            ? CerebralJson.Read<PluginControlObservation>(observation) : null, Optional(args, "error"));
    private object ReadyPluginControl(JsonElement args)
    {
        var fields = args.EnumerateObject().ToArray();
        if (fields.Length != 3 || fields.Any(field => field.Name is not ("id" or "sessionId" or "claimId")))
            throw new ArgumentException("Control readiness accepts only exact id, sessionId, and claimId.");
        return pluginControls.Ready(Text(args, "id"), Text(args, "sessionId"), Text(args, "claimId"),
            (presentation, request) =>
            {
                var host = workflows ?? throw new InvalidOperationException("The workflow engine is unavailable; no control may be delivered.");
                if (presentation.Source != "workflow")
                    throw new UnauthorizedAccessException("Control delivery requires a retained native workflow presentation.");
                var source = host.Store.Get(presentation.JobId);
                var executing = host.Store.Get(request.JobId);
                foreach (var run in new[] { source, executing })
                {
                    if (run.Submission.ConversationId != request.SessionId || run.Submission.ProfileId != request.ProfileId)
                        throw new UnauthorizedAccessException("The retained workflow does not own this control request.");
                    if (run.CancelRequested || run.Status is WorkflowStatus.Cancelled or WorkflowStatus.Failed or WorkflowStatus.Cancelling)
                        throw new InvalidOperationException("The presenting or executing workflow was stopped or failed. No action was invoked.");
                }
                if (executing.Status != WorkflowStatus.Running || executing.Effect?.Id != request.Id ||
                    executing.Effect.State != WorkflowEffectState.Dispatched)
                    throw new InvalidOperationException("The exact control effect is no longer executing. No action was invoked.");
            });
    }
    private object StopLocal()
    {
        if (localRun?.Enabled != true || localRun.Stop is null)
            throw new InvalidOperationException("This command only stops an explicitly launched local worker. Use installed service controls for an installation.");
        if (store.List<AgentJob>("job").Any(JobScheduler.IsActive) || workflows?.Store.CountShutdownBlocking() > 0)
            throw new InvalidOperationException("Finish or cancel active assignments before stopping the local worker.");
        // Allow the HTTP response to leave before graceful host shutdown drains model processes.
        _ = Task.Run(async () => { await Task.Delay(250); localRun.Stop(); });
        return new { stopping = true };
    }
    private void Authorize(string operation, AgentProfile? actor, JsonElement args)
    {
        if (actor is null) return; // An authenticated installation owner uses the same handlers as both GUIs.
        if (operation.StartsWith("inference.isolated.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Isolated suggestion generation requires the owner interface.");
        if (operation.StartsWith("cortex.assets.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Passive Cortex asset reads require the owner interface; agent tools and namespace grants remain separate.");
        if (operation is "metrics.generation" or "metrics.execution.status" or "metrics.slots.status" or "metrics.inference.status")
            throw new UnauthorizedAccessException("Generation diagnostics require the owner interface.");
        if (operation.StartsWith("metrics-history.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Metrics history and capture settings require the owner interface.");
        if (operation == "diagnostics.prompt-cache.get")
            throw new UnauthorizedAccessException("Prompt-cache diagnostic history requires the owner interface.");
        if (operation.StartsWith("engine.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Engine activation and inspection require the owner interface.");
        if (operation.StartsWith("workers.models.", StringComparison.Ordinal) || operation is ModelRuntimeSupportQueryProtocol.ReadOperation or "workers.inference.generate" or "models.idle.set" or "models.content.describe" or "models.default.switch" or "models.distribute" or "models.load-tracked" or "models.runtime.status" or "inference.prepare" or "inference.prepared.generate" or "inference.prepared.release")
            throw new UnauthorizedAccessException("Worker model management and prepared owner inference require the owner interface, including for administration agents.");
        if (operation.StartsWith("resources.local.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Local GPU pause and resume require the owner interface.");
        if (operation.StartsWith("peer.grants.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Peer grant management requires the owner interface.");
        if (CollaborationService.Operations.Contains(operation, StringComparer.Ordinal))
        {
            (collaboration ?? throw new InvalidOperationException("Collaboration service is unavailable."))
                .Authorize(operation, actor, args);
            return;
        }
        if (operation.StartsWith(WorkerAttachmentTransfers.Prefix, StringComparison.Ordinal))
        {
            WorkerAttachmentTransfers.Authorize(operation, actor, args);
            return;
        }
        if (operation is "agents.instructions.get" or "agents.instructions.save")
            throw new UnauthorizedAccessException("Agent prompt editing requires the owner interface.");
        if (operation == "agents.task-budget-policy.save")
            throw new UnauthorizedAccessException("Task budget policy changes require the installation owner interface.");
        if (operation.StartsWith("interface.native-plugins.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Native plugin catalog claims and outcomes require the owner interface, including for administration agents.");
        if (operation.StartsWith("interface.controls.", StringComparison.Ordinal) ||
            operation is "interface.get" or "interface.claim" or "interface.complete" or "interface.repair")
            throw new UnauthorizedAccessException("Presentation and plugin control claims and outcomes require the owner interface, including for administration agents.");
        if (operation.StartsWith("interface.preferences.", StringComparison.Ordinal) ||
            operation.StartsWith("interface.theme.", StringComparison.Ordinal) ||
            operation.StartsWith("interface.layout.", StringComparison.Ordinal) ||
            operation.StartsWith("settings.plugins.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Interface preference editing requires the owner interface.");
        if (operation == "interface.retry")
            throw new UnauthorizedAccessException("Retrying a failed native plugin review requires the owner interface.");
        if (operation.StartsWith("conversations.properties.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Chat Properties require the owner interface, including for administration agents.");
        if (operation.StartsWith("internet.", StringComparison.Ordinal) ||
            operation.StartsWith("conversations.internet.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Managed web policy and owner decisions require the installation owner, including for administration agents.");
        if (operation.StartsWith("conversations.permissions.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Chat tool permissions require the owner interface, including for administration agents.");
        if (operation.StartsWith("conversations.plugin-install.", StringComparison.Ordinal) ||
            operation.StartsWith("interface.install-authorization.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Chat native-plugin install permission and authorization require the owner interface, including administration agents.");
        if (operation.StartsWith("conversations.paths.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Chat path rules require the owner interface, including for administration agents.");
        if (operation.StartsWith("conversations.workspace.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Conversation workspace bindings require the owner interface.");
        if (operation.StartsWith("configuration.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Configuration backup and restore require the owner interface, including for agents with administration capability.");
        if (operation.StartsWith("service-packages.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Service package lifecycle requires the owner interface.");
        if (operation == "tools.registered.catalog")
            throw new UnauthorizedAccessException("The registered tool catalog requires the owner interface, including for administration agents.");
        if (operation.StartsWith("skills.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Skill installation and assignment require the owner interface. Agents use only the read-only skills.list and skills.read tools.");
        if (WorkerWorkflows.Operations.Contains(operation, StringComparer.Ordinal))
        {
            if (operation is "jobs.workflow.abandon.inspect" or "jobs.workflow.abandon")
                throw new UnauthorizedAccessException("Workflow abandonment requires the installation owner route.");
            if (operation is "jobs.workflow.receipt-lookup.inspect" or "jobs.workflow.receipt-lookup.confirm")
                throw new UnauthorizedAccessException("Exact job receipt lookup requires the installation owner route.");
            if (operation is "jobs.workflow.answer" or "jobs.workflow.steer" or
                "jobs.workflow.prepared.reject" or "jobs.workflow.prepared.permit-once" or
                "conversations.rename" or "conversations.compact")
                throw new UnauthorizedAccessException("Workflow answers, steering and conversation titles require the owner interface.");
            if (operation is "sdk.discover" or "sdk.describe") return;
            if (operation.StartsWith("workflows.catalog.", StringComparison.Ordinal))
            {
                (workflows ?? throw new InvalidOperationException("Workflow engine is unavailable.")).GetOwned(Text(args, "sourceRequestId"), actor);
                return;
            }
            if (operation is "jobs.workflow.active" or "jobs.workflow.active-summaries" or "queue.active-summaries" or "jobs.workflow.recent" or "conversations.recent" or "conversations.protected" or "conversations.get" or "conversations.goal" or "conversations.workflow.get" or "conversations.workflow.recent" or "conversations.workflow.protected") throw new UnauthorizedAccessException("Listing owner workflows requires the owner route.");
            if (operation == "jobs.workflow.submit")
            {
                if (Text(args, "profileId") == actor.Id) return;
                throw new UnauthorizedAccessException("A calling agent can submit only under its own profile.");
            }
            (workflows ?? throw new InvalidOperationException("Workflow engine is unavailable."))
                .GetOwned(Text(args, operation == "jobs.workflow.receipt" ? "runId" : "id"), actor);
            return;
        }
        if (operation is "cortex.native-continuity.get" or "cortex.native-continuity.inspect")
            throw new UnauthorizedAccessException("Native continuity inspection requires the owner interface.");
        if (actor.Capabilities.Contains("administration")) return;
        if (operation is "health" or "resources" or "metrics.status" or "diagnostics" or "plugins.list") { if (actor.Capabilities.Contains("diagnostics")) return; }
        if (operation is "activity.turns" or "activity.list")
        {
            if (store.GetSessionMetadata(Text(args, "sessionId"))?.ProfileId == actor.Id) return;
            throw new UnauthorizedAccessException("This profile does not own the activity conversation.");
        }
        if (operation == "cortex.continuity.get" && store.GetSessionMetadata(Text(args, "sessionId"))?.ProfileId == actor.Id) return;
        if (operation == "knowledge.retrieval.get" && scheduler.Get(Text(args, "jobId")).ProfileId == actor.Id) return;
        if (operation == "knowledge.proposals.list" && store.GetSessionMetadata(Text(args, "sessionId"))?.ProfileId == actor.Id) return;
        if (operation == "knowledge.proposals.get" && store.Get<KnowledgeProposal>("knowledge-proposal", Text(args, "id")) is { } proposal && store.GetSessionMetadata(proposal.SessionId)?.ProfileId == actor.Id) return;
        if (operation is "tools.list" or "tools.call" or "shared.call") { if (Optional(args, "profileId") is null || Optional(args, "profileId") == actor.Id) return; }
        if (operation == "jobs.submit" && Text(args, "profileId") == actor.Id) return;
        if (operation == "interface.list" && store.GetSessionMetadata(Text(args, "sessionId"))?.ProfileId == actor.Id) return;
        if (operation is "jobs.get" or "jobs.cancel" or "jobs.steer" or "jobs.reconcile") { if (scheduler.Get(Text(args, "id")).ProfileId == actor.Id) return; }
        if (operation is "sessions.get" or "sessions.longform" or "sessions.compact" or "sessions.exclude" or "sessions.stop" or "sessions.response-balance") { if (store.GetSessionMetadata(Text(args, "id"))?.ProfileId == actor.Id) return; }
        throw new UnauthorizedAccessException("This command requires the separately configured administration capability.");
    }

    private async Task<object> ConfigurationCommandAsync(string operation, JsonElement args, AgentProfile? actor, CancellationToken ct)
    {
        if (actor is not null) throw new UnauthorizedAccessException("Configuration backup and restore require the owner interface.");
        switch (operation)
        {
            case "configuration.settings.describe":
                EmptySettingsArguments(args); return configurationBackups.DescribeCompleteSettings(owner: true);
            case "configuration.presets.describe":
                EmptySettingsArguments(args); return configurationBackups.DescribePresets(owner: true);
            case "configuration.presets.list":
                EmptySettingsArguments(args); return configurationBackups.ListPresets(owner: true);
            case "configuration.presets.get": return configurationBackups.GetPreset(args, owner: true);
            case "configuration.presets.capture": return PresetMutation(() => configurationBackups.CapturePreset(args, owner: true));
            case "configuration.presets.import": return PresetMutation(() => configurationBackups.ImportPreset(args, owner: true));
            case "configuration.presets.export": return configurationBackups.ExportPreset(args, owner: true);
            case "configuration.presets.preview": return configurationBackups.PreviewPreset(args, owner: true);
            case "configuration.backup.export":
                return configurationBackups.Export(args, owner: true);
            case "configuration.restore.preview": return configurationBackups.Preview(args, owner: true);
            case "configuration.restore.status": return configurationBackups.Status(args, owner: true);
            case "configuration.restore.apply":
                // An exact committed retry only returns its receipt, even if work/models changed later.
                if (configurationBackups.CommittedResult(args, owner: true) is { } committed) return committed;
                // Transfers and imports capture paths/registrations before their I/O. Exclude them
                // as well as loaded runtimes so a late transfer cannot overwrite the restored row.
                if (!await modelFiles.WaitAsync(0, ct))
                    throw new InvalidOperationException("Finish the current model file operation before restoring configuration. No new restore was dispatched; check retained status if an earlier response was lost.");
                try { return await runtime.WithNoLoadedModelsAsync(() => configurationBackups.Apply(args, owner: true), ct); }
                finally { modelFiles.Release(); }
            default: throw new ArgumentException("Unknown configuration operation.");
        }
    }


    private static object PresetMutation(Func<SettingsPresetSummary> mutation)
    {
        try { return mutation(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            // Only a validated rejection permits the client to repair and resubmit its draft.
            // Serialization, SQLite commit failures, and a lost response remain unknown outcomes.
            throw new InvalidOperationException("settings_preset_rejected: Reload the current revision and review the preset name, document, and size limits.");
        }
    }

    private object SettingsPreferenceCommand(string operation, JsonElement args, AgentProfile? actor)
    {
        if (actor is not null) throw new UnauthorizedAccessException("Settings and layouts require the owner interface.");
        switch (operation)
        {
            case "settings.plugins.transfer.get":
                return settingsPreferences.GetTransfer(ReadSettingsRequest<PluginSettingsCoordinate>(args));
            case "settings.plugins.transfer.save":
                var transfer = settingsPreferences.SaveTransfer(ReadSettingsRequest<PluginSettingsTransferSaveRequest>(args));
                if (transfer.Changed && transfer.State.State.Coordinate.Scope == SettingsPreferenceContract.GlobalScope)
                    servicePlugins?.QueueOwnSettingsRefresh(transfer.State.State.Coordinate.PluginId);
                if (transfer.Changed) events.Publish("settings.plugins.updated", null,
                    new { coordinate = transfer.State.State.Coordinate, revision = transfer.State.State.Revision });
                return transfer;
            case "settings.plugins.get": return settingsPreferences.GetPlugin(ReadSettingsRequest<PluginSettingsCoordinate>(args));
            case "settings.plugins.pending.list":
                EmptySettingsArguments(args); return settingsPreferences.PendingSummaries();
            case "settings.plugins.save":
                var plugin = settingsPreferences.SavePlugin(ReadSettingsRequest<PluginSettingsSaveRequest>(args));
                if (plugin.Changed && plugin.State.Coordinate.Scope == SettingsPreferenceContract.GlobalScope)
                    servicePlugins?.QueueOwnSettingsRefresh(plugin.State.Coordinate.PluginId);
                if (plugin.Changed) events.Publish("settings.plugins.updated", null,
                    new { coordinate = plugin.State.Coordinate, revision = plugin.State.Revision });
                return plugin;
            case "interface.layout.get":
                return settingsPreferences.GetLayout(ReadSettingsRequest<InterfaceLayoutGetRequest>(args).ApplicationId);
            case "interface.layout.save":
                var layout = settingsPreferences.SaveLayout(ReadSettingsRequest<InterfaceLayoutSaveRequest>(args));
                if (layout.Changed) events.Publish("interface.layout.updated", null,
                    new { applicationId = layout.State.ApplicationId, revision = layout.State.Revision });
                return layout;
            default: throw new ArgumentException("Unknown settings preference operation.");
        }
    }

    private static void EmptySettingsArguments(JsonElement args)
    {
        if (args.EnumerateObject().Any()) throw new ArgumentException("This settings operation takes no arguments.");
    }

    private static T ReadSettingsRequest<T>(JsonElement args)
    {
        try
        {
            var options = new JsonSerializerOptions(CerebralJson.Options)
            {
                PropertyNameCaseInsensitive = false, MaxDepth = 32,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(args, options);
            if (bytes.Length > 2 * 1024 * 1024) throw new ArgumentException("Settings request exceeds its byte bound.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            UniqueSettingsRequestFields(document.RootElement);
            return document.RootElement.Deserialize<T>(options) ?? throw new ArgumentException("Settings request is invalid.");
        }
        catch (JsonException) { throw new ArgumentException("Settings request contains unsupported or invalid fields."); }
    }

    private static void UniqueSettingsRequestFields(JsonElement value, bool opaque = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(opaque ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Settings request contains duplicate fields.");
                UniqueSettingsRequestFields(property.Value, opaque || SettingsPreferenceJson.IsOpaqueProperty(property));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueSettingsRequestFields(item, opaque);
    }

    private static void RequireExactReceiptLookupFields(JsonElement args, bool confirm)
    {
        if (args.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(args.GetRawText()) > 2048)
            throw new ArgumentException("Receipt lookup input must be a bounded object.");
        string[] expected = confirm
            ? new[] { "id", "expectedRevision", "effectId", "effectSha256", "providerId", "providerFingerprint", "jobIdHint" }
            : ["id"];
        var fields = args.EnumerateObject().Select(item => item.Name).ToArray();
        if (fields.Length != expected.Length || fields.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            fields.Except(expected, StringComparer.Ordinal).Any())
            throw new ArgumentException("Receipt lookup requires only its exact versioned fields, once each.");
    }

    private async Task<object> WorkflowCommandAsync(string operation, JsonElement args, AgentProfile? actor, CancellationToken ct)
    {
        var host = workflows ?? throw new InvalidOperationException("Workflow engine is unavailable.");
        switch (operation)
        {
            case "sdk.discover": return host.Discover(actor, AdditionalOperationDescriptors());
            case "sdk.describe": return host.Describe(Text(args, "id"), actor, AdditionalOperationDescriptors());
            case "workflows.catalog.register": return host.RegisterWorkflowData(WorkerWorkflows.Read<WorkerWorkflows.WorkflowDataRegisterRequest>(args), actor);
            case "workflows.catalog.list": return host.ListWorkflowData(WorkerWorkflows.Read<WorkerWorkflows.WorkflowDataListRequest>(args), actor);
            case "workflows.catalog.get": return host.GetWorkflowData(WorkerWorkflows.Read<WorkerWorkflows.WorkflowDataGetRequest>(args), actor);
            case "jobs.workflow.submit": return await host.SubmitAsync(WorkerWorkflows.Read<SubmitWorkflowRequest>(args), actor, ct);
            case "jobs.workflow.get": return host.GetOwnedWithActivityTotals(Text(args, "id"), actor);
            case "jobs.workflow.receipt": return host.GetReceiptOwned(WorkerWorkflows.Read<WorkflowReceiptRequest>(args), actor);
            case "jobs.workflow.active": return host.Store.ListActivePage(Number(args, "limit", 100, 1, 200),
                args.TryGetProperty("afterRow", out var row) ? row.GetInt64() : 0);
            case "jobs.workflow.active-summaries": return host.Store.ListActiveSummaries(Number(args, "limit", 40, 1, 200),
                args.TryGetProperty("afterRow", out var activeSummaryRow) ? activeSummaryRow.GetInt64() : 0);
            case "queue.active-summaries": return store.ActiveJobSummaries(Number(args, "limit", 40, 1, 200),
                args.TryGetProperty("afterRow", out var legacySummaryRow) ? legacySummaryRow.GetInt64() : 0);
            case "conversations.get": return host.Store.GetConversation(ConversationWorkflowModule.DefinitionId, Text(args, "id"))
                ?? throw new KeyNotFoundException("Conversation was not found.");
            case "conversations.workflow.get":
                RequireExactWorkflowConversationArguments(args, true, "conversationId", "acceptedRunId", "definitionId", "definitionVersion");
                return host.Store.GetOwnerWorkflowConversation(Text(args, "conversationId"), Text(args, "acceptedRunId"),
                    Text(args, "definitionId"), Text(args, "definitionVersion"));
            case "conversations.workflow.recent":
                RequireExactWorkflowConversationArguments(args, false, "limit", "beforeRow", "snapshotRow", "generalOnly", "conversationId");
                return host.Store.ListOwnerWorkflowConversations(Number(args, "limit", 40, 1, 200),
                    args.TryGetProperty("beforeRow", out var workflowBefore) ? workflowBefore.GetInt64() : null,
                    args.TryGetProperty("snapshotRow", out var workflowSnapshot) ? workflowSnapshot.GetInt64() : null,
                    protectedIds: Flag(args, "generalOnly") ? ProtectedNavigationConversationIds() : null,
                    generalOnly: Flag(args, "generalOnly"), excludedDefinitionId: WorkflowDataModule.DefinitionId,
                    conversationId: args.TryGetProperty("conversationId", out _) ? Text(args, "conversationId") : null);
            case "conversations.workflow.protected":
                RequireExactWorkflowConversationArguments(args, false, "limit", "beforeRow", "snapshotRow");
                return host.Store.ListOwnerWorkflowConversations(Number(args, "limit", 200, 1, 200),
                    args.TryGetProperty("beforeRow", out var protectedWorkflowBefore) ? protectedWorkflowBefore.GetInt64() : null,
                    args.TryGetProperty("snapshotRow", out var protectedWorkflowSnapshot) ? protectedWorkflowSnapshot.GetInt64() : null,
                    protectedIds: ProtectedNavigationConversationIds(), excludedDefinitionId: WorkflowDataModule.DefinitionId);
            case "conversations.goal":
                if (actor is not null) throw new UnauthorizedAccessException("Conversation goal display requires the owner interface.");
                if (args.EnumerateObject().Count() != 1 || !args.TryGetProperty("id", out _))
                    throw new ArgumentException("Conversation goal display accepts only id.");
                return host.Store.ReadConversationGoal(ConversationWorkflowModule.DefinitionId, Text(args, "id"));
            case "conversations.protected":
                var protectedArguments = args.EnumerateObject().Select(item => item.Name).ToArray();
                if (protectedArguments.Distinct(StringComparer.Ordinal).Count() != protectedArguments.Length ||
                    protectedArguments.Any(name => name is not ("limit" or "beforeRow" or "snapshotRow")))
                    throw new ArgumentException("Protected conversations accept only limit, beforeRow and snapshotRow.");
                return host.Store.ListProtectedConversations(ConversationWorkflowModule.DefinitionId,
                ProtectedNavigationConversationIds(),
                Number(args, "limit", 200, 1, 200),
                args.TryGetProperty("beforeRow", out var protectedBefore) ? protectedBefore.GetInt64() : null,
                args.TryGetProperty("snapshotRow", out var protectedSnapshot) ? protectedSnapshot.GetInt64() : null);
            case "conversations.recent":
                if (Flag(args, "generalOnly"))
                    return host.Store.ListGeneralConversations(ConversationWorkflowModule.DefinitionId,
                        ProtectedNavigationConversationIds(),
                        Number(args, "limit", 40, 1, 200),
                        args.TryGetProperty("beforeRow", out var generalBefore) ? generalBefore.GetInt64() : null,
                        args.TryGetProperty("snapshotRow", out var generalSnapshot) ? generalSnapshot.GetInt64() : null);
                return host.Store.ListConversations(ConversationWorkflowModule.DefinitionId,
                Number(args, "limit", 40, 1, 200),
                args.TryGetProperty("beforeRow", out var chatBefore) ? chatBefore.GetInt64() : null,
                args.TryGetProperty("snapshotRow", out var chatSnapshot) ? chatSnapshot.GetInt64() : null);
            case "jobs.workflow.recent": return host.Store.ListRecentPage(Number(args, "limit", 40, 1, 200),
                args.TryGetProperty("beforeRow", out var recentBefore) ? recentBefore.GetInt64() : null,
                args.TryGetProperty("snapshotRow", out var recentSnapshot) ? recentSnapshot.GetInt64() : null);
            case "jobs.workflow.answer": return host.Answer(WorkerWorkflows.Read<WorkflowAnswerRequest>(args), actor);
            case "jobs.workflow.steer": return host.Steer(WorkerWorkflows.Read<WorkflowSteeringRequest>(args), actor);
            case "conversations.rename": return host.RenameConversation(WorkerWorkflows.Read<ConversationRenameRequest>(args), actor);
            case "conversations.compact": return host.RequestConversationCompaction(WorkerWorkflows.Read<ConversationCompactionRequest>(args), actor);
            case "jobs.workflow.abandon.inspect":
                if (actor is not null) throw new UnauthorizedAccessException("Workflow abandonment requires the installation owner route.");
                RequireExactWorkflowConversationArguments(args, true, "id");
                host.GetOwned(Text(args, "id"), actor);
                return scheduler.InspectWorkflowAbandon(Text(args, "id"));
            case "jobs.workflow.abandon":
                if (actor is not null) throw new UnauthorizedAccessException("Workflow abandonment requires the installation owner route.");
                RequireExactWorkflowConversationArguments(args, true, "requestId", "runId", "expectedRevision", "effectId", "effectSha256", "reason");
                var abandon = WorkerWorkflows.Read<OwnerWorkflowAbandonRequest>(args);
                host.GetOwned(abandon.RunId, actor);
                return scheduler.AbandonWorkflow(abandon);
            case "jobs.workflow.cancel":
                host.GetOwned(Text(args, "id"), actor);
                return scheduler.CancelWorkflow(Text(args, "id"));
            case "jobs.workflow.resume":
                host.GetOwned(Text(args, "id"), actor);
                return host.Publish(host.Store.Resume(Text(args, "id"), args.GetProperty("expectedRevision").GetInt64()));
            case "jobs.workflow.reconcile":
                host.GetOwned(Text(args, "id"), actor);
                return await scheduler.ReconcileWorkflowAsync(Text(args, "id"), ct);
            case "jobs.workflow.prepared.reject":
                if (actor is not null) throw new UnauthorizedAccessException("Prepared-effect intervention requires the installation owner route.");
                var reject = WorkerWorkflows.Read<WorkflowPreparedRejectRequest>(args);
                host.GetOwned(reject.RunId, actor);
                return host.RejectPrepared(reject);
            case "jobs.workflow.prepared.permit-once":
                if (actor is not null) throw new UnauthorizedAccessException("Prepared-effect intervention requires the installation owner route.");
                var permit = WorkerWorkflows.Read<WorkflowPreparedPermitRequest>(args);
                host.GetOwned(permit.RunId, actor);
                return host.PermitPreparedOnce(permit);
            case "jobs.workflow.receipt-lookup.inspect":
                if (actor is not null) throw new UnauthorizedAccessException("Exact job receipt lookup requires the installation owner route.");
                RequireExactReceiptLookupFields(args, confirm: false);
                return host.InspectReceiptLookup(Text(args, "id"));
            case "jobs.workflow.receipt-lookup.confirm":
                if (actor is not null) throw new UnauthorizedAccessException("Exact job receipt lookup requires the installation owner route.");
                RequireExactReceiptLookupFields(args, confirm: true);
                return await scheduler.ReconcileReceiptLookupAsync(
                    WorkerWorkflows.Read<WorkflowReceiptLookupRequest>(args), ct);
            case "jobs.workflow.events":
                if (DurableInferenceOwnerRead.IsSelected(args))
                    return ReadDurableInferenceEvents(host, args, actor, ct);
                var run = host.GetOwned(Text(args, "id"), actor);
                var page = host.Journal.ReadPage(run.Submission.ConversationId,
                    args.TryGetProperty("afterSequence", out var after) ? after.GetInt64() : 0,
                    Number(args, "limit", 40, 1, 200),
                    args.TryGetProperty("snapshotSequence", out var snapshot) ? snapshot.GetInt64() : null, ct);
                // These bounded UI pages may normalize a proven legacy stage's human projection.
                // The canonical journal and model exact-history route remain unchanged.
                return page with { Events = page.Events.Select(item => PluginContinuationGoal.ForDisplay(
                    store, host.Journal, run.Submission, item, page.SnapshotSequence, ct)).ToArray() };
            case "jobs.workflow.events.previous":
                var previousRun = host.GetOwned(Text(args, "id"), actor);
                var previousPage = host.Journal.ReadPreviousPage(previousRun.Submission.ConversationId,
                    args.TryGetProperty("beforeSequence", out var before) ? before.GetInt64() : null,
                    Number(args, "limit", 40, 1, 200),
                    args.TryGetProperty("snapshotSequence", out var previousSnapshot) ? previousSnapshot.GetInt64() : null, ct);
                return previousPage with { Events = previousPage.Events.Select(item => PluginContinuationGoal.ForDisplay(
                    store, host.Journal, previousRun.Submission, item, previousPage.SnapshotSequence, ct)).ToArray() };
            default: throw new ArgumentException("Unknown workflow command.");
        }
    }
    private static void RequireExactWorkflowConversationArguments(JsonElement args, bool required, params string[] allowed)
    {
        var names = args.EnumerateObject().Select(item => item.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(name => !allowed.Contains(name, StringComparer.Ordinal)) ||
            required && (names.Length != allowed.Length || allowed.Any(name => !names.Contains(name, StringComparer.Ordinal))))
            throw new ArgumentException("Exact workflow conversation arguments do not match the bounded command schema.");
    }

    private string[] ProtectedNavigationConversationIds()
    {
        var nodes = new WorkspaceManagement(store, events, scheduler).GetNavigation().Nodes;
        var index = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        bool Protected(NavigationNode node)
        {
            if (node.Pinned) return true;
            var parent = node.ParentId;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (parent is not null && seen.Add(parent) && index.TryGetValue(parent, out var ancestor))
            {
                if (ancestor.Kind == "project") return true;
                parent = ancestor.ParentId;
            }
            return false;
        }
        // Only persisted owner organization declares protection; callers never supply target IDs.
        return nodes.Where(node => node.Kind == "conversation" && node.TargetId is not null && Protected(node))
            .Select(node => node.TargetId!).Distinct(StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<WorkerWorkflows.OperationDescriptor> AdditionalOperationDescriptors()
    {
        IEnumerable<WorkerWorkflows.OperationDescriptor> descriptors = attachments is null
            ? [] : WorkerAttachmentOperationDescriptors.All;
        descriptors = descriptors.Append(RemoteModelRecoveryOperation).Append(GenerationTelemetryOperation).Append(OwnerSnapshotOperation)
            .Append(ExecutionMetricsOperation).Append(ManagedSlotMetricsOperation).Append(InferenceObservationOperation)
            .Concat(ModelCapacityOperations).Concat(RuntimeSupportOperations);
        if (workflows?.Engine is not null) descriptors = descriptors.Concat(IntentionalWorkHoldOperations).Concat(IsolatedTextOperations);
        if (servicePackageLifecycle is not null) descriptors = descriptors.Concat(ServicePackageLifecycle.OperationDescriptors);
        if (collaboration is not null) descriptors = descriptors.Concat(CollaborationService.OperationDescriptors);
        return descriptors.Concat(InternetOperations).ToArray();
    }

    private async Task<DesktopStatusMetrics> StatusMetricsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var resources = await HardwareProbe.ReadAsync(cancellationToken);
        var queue = scheduler.QueueMetrics();
        var processIds = runtime.ResourceProcessIds().Append(Environment.ProcessId).ToHashSet();
        if (arguments.TryGetProperty("desktopProcessId", out var desktopProcessId) &&
            desktopProcessId.ValueKind == JsonValueKind.Number && desktopProcessId.TryGetInt32(out var desktopPid) && desktopPid > 0)
            processIds.Add(desktopPid);
        var cerebral = await ReadCerebralProcessMetricsAsync(processIds, cancellationToken);
        return new(resources,
            queue.OwnerRunning + queue.PeerRunning,
            queue.OwnerQueued + queue.PeerQueued,
            workflows?.Store.CountActive() ?? 0,
            runtime.UsageTotals(),
            DateTimeOffset.UtcNow) { Cerebral = cerebral };
    }

    private async Task<CerebralProcessMetrics> ReadCerebralProcessMetricsAsync(HashSet<int> processIds,
        CancellationToken cancellationToken)
    {
        long memoryMb = 0; long processorTicks = 0; var live = 0;
        foreach (var processId in processIds.ToArray())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.HasExited) continue;
                memoryMb += Math.Max(0, process.WorkingSet64 / 1024 / 1024);
                processorTicks += Math.Max(0, process.TotalProcessorTime.Ticks);
                live++;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        var now = Stopwatch.GetTimestamp();
        double? cpu = null;
        lock (resourceSampleGate)
        {
            if (priorResourceSampleTicks > 0 && now > priorResourceSampleTicks && processorTicks >= priorResourceProcessorTicks)
            {
                var elapsed = (now - priorResourceSampleTicks) / (double)Stopwatch.Frequency;
                var processorSeconds = TimeSpan.FromTicks(processorTicks - priorResourceProcessorTicks).TotalSeconds;
                if (elapsed > 0)
                    cpu = Math.Round(Math.Clamp(processorSeconds * 100d / (elapsed * Math.Max(1, Environment.ProcessorCount)), 0d, 100d), 1);
            }
            priorResourceSampleTicks = now;
            priorResourceProcessorTicks = processorTicks;
        }
        var gpuMemory = await HardwareProbe.ReadGpuProcessMemoryAsync(processIds, cancellationToken);
        var storagePath = configuration.Get().ModelDirectory;
        if (string.IsNullOrWhiteSpace(storagePath)) storagePath = store.DirectoryPath;
        long? available = null; long? total = null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(storagePath));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady) { available = drive.AvailableFreeSpace; total = drive.TotalSize; }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return new(memoryMb, cpu, gpuMemory.UsedMb, live, available, total, storagePath)
        {
            GpuMemoryUnavailableReason = gpuMemory.Error
        };
    }
    private AgentProfile Profile(JsonElement args, AgentProfile? actor, bool allowRemote = false)
    {
        var profile = actor ?? store.Get<AgentProfile>("agent", Text(args, "profileId")) ?? throw new ArgumentException("Agent profile not found.");
        if (profile.WorkerId != "local" && !allowRemote && Optional(args, "name")?.StartsWith("shared.", StringComparison.Ordinal) != true)
            throw new InvalidOperationException($"This agent executes on worker '{profile.WorkerId}'. Connect directly to that worker and select its local profile for direct tool access; no local filesystem action was attempted.");
        return profile;
    }
    private async Task<IReadOnlyList<ToolDescriptor>> ListToolsAsync(JsonElement args, AgentProfile? actor, CancellationToken ct)
    {
        var profile = Profile(args, actor, true); var list = await tools.ListAsync(profile, ct);
        return profile.WorkerId == "local" ? list : list.Where(x => x.Name.StartsWith("shared.", StringComparison.Ordinal)).ToList();
    }
    private async Task<object> InspectNativeContinuityAsync(JsonElement arguments, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
            if (!names.Add(field.Name) || field.Name != "conversationId")
                throw new ArgumentException("Native task inspection accepts only one conversationId.");
        var continuity = workflows?.NativeContinuity ??
            throw new InvalidOperationException("Native Cortex continuity is unavailable on this Worker.");
        return await continuity.InspectAsync(Text(arguments, "conversationId"), token);
    }
    private async Task<ToolResult> CallToolAsync(JsonElement args, AgentProfile? actor, CancellationToken ct)
    {
        var profile = Profile(args, actor); var sessionId = Optional(args, "sessionId");
        var context = new ToolExecutionContext(profile, "direct", "direct");
        if (sessionId is not null && sessionId != "direct")
        {
            var session = store.GetSessionMetadata(sessionId) ?? throw new ArgumentException(
                workflows?.NativeContinuity?.Get(sessionId) is not null
                    ? "This is a native conversation. Use cortex.native-continuity.inspect for its bound Cortex task."
                    : "Conversation not found.");
            if (session.ProfileId != profile.Id || (actor is not null && session.ProfileId != actor.Id))
                throw new UnauthorizedAccessException("This profile does not own the selected conversation.");
            context = new ProjectKnowledge(store, configuration, events).ContextFor(session, profile, "direct");
        }
        var call = new ToolCall(Text(args, "name"), args.GetProperty("arguments"));
        if (context.Cortex is { RequireKnowledgeApproval: true } && CortexOperationPolicy.IsDurableWrite(call.Name, call.Arguments))
            throw new UnauthorizedAccessException("This project requires approval of the exact knowledge proposal. Submit the change through its conversation review.");
        return await executionTools.ExecuteAsync(call, context, ct);
    }
    private async Task<ToolResult> SharedCallAsync(JsonElement args, AgentProfile? actor, CancellationToken ct)
    {
        if (configuration.Get().SharedFilesWorkerId != "local") throw new InvalidOperationException("This machine forwards shared files; connect to the actual shared file host.");
        var name = Text(args, "name"); if (!name.StartsWith("shared.", StringComparison.Ordinal)) throw new ArgumentException("Only shared file commands are allowed here.");
        return await executionTools.ExecuteAsync(new(name, args.GetProperty("arguments")), new(Profile(args, actor, true), "shared", "shared"), ct);
    }
    private HostSettings SaveSettings(JsonElement input)
    {
        var settings = CerebralJson.Read<HostSettings>(input);
        var current = configuration.Get();
        if (!input.EnumerateObject().Any(property => property.Name.Equals("workflowIdleExpiryDays", StringComparison.OrdinalIgnoreCase)))
            settings.WorkflowIdleExpiryDays = current.WorkflowIdleExpiryDays;
        if (!string.Equals(settings.DefaultModelId, current.DefaultModelId, StringComparison.Ordinal) ||
            !string.Equals(settings.DefaultModelWorkerId, current.DefaultModelWorkerId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Pegasus Cerebral default model changed while these settings were open. Refresh settings; change the default only with Use for Pegasus Cerebral.");
        var processorChanged = settings.MaxLogicalProcessorCount != current.MaxLogicalProcessorCount ||
            !settings.AllowedLogicalProcessors.Order().SequenceEqual(current.AllowedLogicalProcessors.Order());
        configuration.Save(settings); events.Publish("settings.updated", null, new
        {
            restartNeededForListenerChanges = true,
            restartNeededForProcessorChanges = processorChanged
        }); return settings;
    }
    private InterfacePreferences SaveInterfacePreference(JsonElement input)
    {
        var result = new InterfacePreferencesStore(store).Set(input);
        events.Publish("settings.updated", null, new { interfacePreferences = true });
        return result;
    }
    private DesktopThemePresetState SaveInterfaceTheme(JsonElement input)
    {
        var updated = new InterfaceThemeStore(store).Set(input);
        if (updated.Changed) events.Publish("interface.theme.updated", null, updated.State);
        return updated.State;
    }

    private AgentProfile CommitAgent(AgentProfile profile, bool requireImageVerification, ModelRecord? remoteModel = null,
        Action? validatePlacement = null, bool noOpWhenUnchanged = false)
    {
        Configuration.ValidateProfile(profile);
        var result = store.Locked(() =>
        {
            validatePlacement?.Invoke();
            var defaults = configuration.Get();
            var current = store.Get<AgentProfile>("agent", profile.Id);
            if (profile.Revision != (current?.Revision ?? 0))
                throw new InvalidOperationException("The agent changed while this copy was open. Refresh before saving; current settings were preserved.");
            // Generic settings forms predate this owner grant and can omit it. They may carry
            // back its exact value, but cannot create, change, or clear it.
            if (profile.TaskBudgetPolicy is not null && profile.TaskBudgetPolicy != current?.TaskBudgetPolicy)
                throw new UnauthorizedAccessException("Use the owner task budget policy operation to change this grant.");
            profile.TaskBudgetPolicy = current?.TaskBudgetPolicy;
            if (profile.Role == "lead") profile.FollowsDefaultModel = true;
            if (profile.FollowsDefaultModel && !string.IsNullOrWhiteSpace(defaults.DefaultModelId) &&
                (profile.ModelId != defaults.DefaultModelId || profile.ModelWorkerId != defaults.DefaultModelWorkerId))
                throw new ArgumentException("A profile following the Pegasus Cerebral default must use the configured default model. Pin the profile or use the default-model switch.");
            if (profile.WorkerId == "local" && profile.ModelWorkerId == "local")
            {
                var model = store.Get<ModelRecord>("model", profile.ModelId) ?? throw new ArgumentException("Import the selected model first.");
                ModelCapabilityPolicy.EnsureConfigured(profile, model);
                if (requireImageVerification) runtime.EnsureCurrentlyCompatible(profile, model);
                var automaticContext = model.AutomaticContextSizing ? model.ContextTokens : (int?)null;
                var budget = AgentBudgetPolicy.Resolve(profile, model, automaticContext);
                profile.ContextTokens = budget.ContextTokens;
                profile.ResponseTokens = budget.ResponseTokens;
                profile.ReasoningMode = budget.ReasoningMode;
                profile.ReasoningTokens = budget.ReasoningTokens;
                Configuration.ValidateProfile(profile);
            }
            else if (remoteModel is not null)
            {
                if (remoteModel.Id != profile.ModelId) throw new ArgumentException("Remote model identity does not match the agent binding.");
                RemoteManagedWorkerSession.EnsureCapabilities(profile, remoteModel, configurationOnly: !requireImageVerification);
                var budget = AgentBudgetPolicy.Resolve(profile, remoteModel, remoteModel.AutomaticContextSizing ? remoteModel.ContextTokens : null);
                profile.ContextTokens = budget.ContextTokens; profile.ResponseTokens = budget.ResponseTokens;
                profile.ReasoningMode = budget.ReasoningMode; profile.ReasoningTokens = budget.ReasoningTokens;
                Configuration.ValidateProfile(profile);
            }
            else ModelCapabilityPolicy.EnsureCompatible(profile, null);
            if (profile.Role == "lead" && store.List<AgentProfile>("agent").Any(x => x.Role == "lead" && x.Id != profile.Id))
                throw new InvalidOperationException("One Cerebral lead is already configured. Change its role before selecting another.");
            // An ordinary agents.save may be a repeated settings form submission. Compare only
            // after the same current-revision, model, budget and role admission as a real edit.
            // The input still carries the expected revision here; no metadata field is erased.
            if (noOpWhenUnchanged && current is not null &&
                JsonElement.DeepEquals(CerebralJson.Element(current), CerebralJson.Element(profile)))
                return (Profile: current, Changed: false);
            profile.Revision = checked((current?.Revision ?? 0) + 1);
            if (current is not null && RetainedModelBindingAuthorizations.RouteChanged(current, profile))
                RetainedModelBindingAuthorizations.RevokeForProfile(store, profile.Id);
            store.Put("agent", profile.Id, profile);
            return (Profile: profile, Changed: true);
        });
        if (result.Changed) events.Publish("agents.updated", null, new { profileId = result.Profile.Id });
        return result.Profile;
    }
    private object DeleteAgent(string id)
    {
        if (store.List<AgentJob>("job").Any(x => x.ProfileId == id && JobScheduler.IsActive(x))) throw new InvalidOperationException("Cancel or finish this agent's work first.");
        store.Locked(() =>
        {
            RetainedModelBindingAuthorizations.RevokeForProfile(store, id);
            settingsPreferences.RemoveAgentScope(id);
            store.Delete("agent", id);
            return true;
        });
        events.Publish("agents.updated", null, new { profileId = id, deleted = true }); return new { deleted = id };
    }
    private async Task<ModelRecord> SaveModelSafelyAsync(ModelRecord model, CancellationToken ct, bool projectorSpecified = true)
    {
        await modelFiles.WaitAsync(ct);
        try
        {
            var saved = await runtime.WithUnloadedModelAsync(model.Id, _ => store.Locked(() =>
            {
                // Accepted work retains its profile snapshot even after the configured agent changes models.
                if (HasModelAssignments(model.Id))
                    throw new InvalidOperationException("Finish or cancel this model's assignments before changing its settings.");
                if (!projectorSpecified) model.VisionProjectorPath = store.Get<ModelRecord>("model", model.Id)!.VisionProjectorPath;
                return SaveModel(model);
            }), ct);
            events.Publish("model.updated", null, saved);
            events.Publish("agents.updated", null, new { modelId = saved.Id, budgetsResolved = true });
            return saved;
        }
        finally { modelFiles.Release(); }
    }
    private Task<ModelRecord> SetModelIdleAsync(JsonElement args, CancellationToken ct)
    {
        RemoteWorkerManagement.RequireFields(args, ["id", "idleMinutes"]);
        var id = Text(args, "id"); Configuration.ValidateId(id);
        return runtime.SetIdleMinutesAsync(id, Number(args, "idleMinutes", -1, -1, 10080), ct);
    }
    private ModelRecord SaveModel(ModelRecord model)
    {
        var old = store.Get<ModelRecord>("model", model.Id) ?? throw new ArgumentException("Model not found.");
        if (old.State is "loaded" or "loading") throw new InvalidOperationException("Unload the model before changing its settings.");
        PreparedModelSave.ValidateResourceSettings(model);
        // Ordinary Save keeps its established read-only GGUF normalization. Capacity choice
        // preparation shares the detached validator/budget calculation but preserves saved metadata.
        model.VisionProjectorPath = ModelComponentConfiguration.NormalizePath(model.VisionProjectorPath);
        ModelRuntime.ValidateProjector(model.VisionProjectorPath);
        model.Path = old.Path; model.SizeBytes = old.SizeBytes; model.State = "unloaded"; model.Error = null;
        ModelMetadataDefaults.Apply(model, GgufMetadataInspector.Inspect(model.Path), preserveConfiguredContext: true, preserveDisplayName: true);
        var prepared = PreparedModelSave.Prepare(model, store.List<AgentProfile>("agent"));
        PreparedModelSave.ObserveCapabilities(prepared, preserveModelMetadata: false);
        store.Put("model", prepared.Model.Id, prepared.Model);
        foreach (var profile in prepared.Profiles) store.Put("agent", profile.Id, profile);
        return prepared.Model;
    }
    private async Task<ModelRecord> UnloadAsync(string id, CancellationToken ct)
    {
        await modelFiles.WaitAsync(ct);
        try
        {
            await runtime.UnloadAsync(id, ct);
            return store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
        }
        finally { modelFiles.Release(); }
    }
    private async Task<object> DeleteModelAsync(string id, CancellationToken ct)
    {
        await modelFiles.WaitAsync(ct);
        try
        {
            if (configuration.Get() is { DefaultModelWorkerId: "local" } selected && selected.DefaultModelId == id) throw new InvalidOperationException("Choose another Pegasus Cerebral default before unregistering this model.");
            if (store.List<AgentProfile>("agent").Any(x => x.ModelId == id && x.ModelWorkerId == "local")) throw new InvalidOperationException("Model is still selected by an agent profile.");
            if (store.ActiveJobs().Any(job => UsesModel(job, id))) throw new InvalidOperationException("Finish or cancel this model's assignments before unregistering it.");
            await runtime.UnloadAsync(id, ct);
            return await runtime.WithUnloadedModelAsync(id, _ => store.Locked(() =>
            {
                if (configuration.Get() is { DefaultModelWorkerId: "local" } currentDefault && currentDefault.DefaultModelId == id) throw new InvalidOperationException("Choose another Pegasus Cerebral default before unregistering this model.");
                if (store.List<AgentProfile>("agent").Any(x => x.ModelId == id && x.ModelWorkerId == "local")) throw new InvalidOperationException("Model is still selected by an agent profile.");
                if (store.ActiveJobs().Any(job => UsesModel(job, id))) throw new InvalidOperationException("Finish or cancel this model's assignments before unregistering it.");
                store.Delete("model", id); return new { unregistered = id, fileDeleted = false };
            }), ct);
        }
        finally { modelFiles.Release(); }
    }
    private AgentJob QueueCompaction(string id)
    {
        var session = store.GetSessionMetadata(id) ?? throw new ArgumentException("Session not found.");
        return scheduler.Submit(new() { SessionId = id, ProfileId = session.ProfileId, Kind = "compact", Prompt = "Compact saved context." });
    }
    private TaskSession SessionPage(JsonElement args)
    {
        int? beforeSequence = null;
        if (args.TryGetProperty("beforeSequence", out var before) && before.ValueKind != JsonValueKind.Null)
            beforeSequence = Number(args, "beforeSequence", 0, 1, int.MaxValue);
        var limit = Number(args, "messageLimit", 200, 1, 1000);
        return store.GetSession(Text(args, "id"), beforeSequence, limit)
            ?? throw new ArgumentException("Session not found.");
    }
    private TaskSession ExcludeContext(string id, int sequence, bool following)
    {
        return store.Locked(() =>
        {
            var session = store.Get<TaskSession>("session", id) ?? throw new ArgumentException("Session not found.");
            if (store.List<AgentJob>("job").Any(x => x.SessionId == id && JobScheduler.IsActive(x))) throw new InvalidOperationException("Cancel or finish this conversation's active work before removing context.");
            var target = session.Messages.FirstOrDefault(x => x.Sequence == sequence) ?? throw new ArgumentException("Message not found.");
            var end = following ? session.Messages.FirstOrDefault(x => x.Sequence > sequence && x.Role == "user" && x.ToolName is null && x.Origin == "user")?.Sequence ?? int.MaxValue : sequence + 1L;
            foreach (var message in session.Messages.Where(x => x.Sequence >= sequence && x.Sequence < end)) message.ExcludedFromContext = true;
            session.Checkpoint = null; session.CheckpointThroughSequence = 0;
            // Future remote work starts with the filtered context, never the old remote conversation containing excluded text.
            session.RemoteSessionIds.Clear(); session.Revision++; store.Put("session", id, session);
            events.Publish("session.updated", null, new { id });
            return store.GetSession(id, messageLimit: 200)!;
        });
    }
    private TaskSession SetResponseBalance(string id, JsonElement args)
    {
        int? share = null;
        if (args.TryGetProperty("responseSharePercent", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
                throw new ArgumentException("Response balance must be an integer percentage or null.");
            share = parsed;
        }
        AgentTokenLimits.ValidateResponseShare(share, "responseSharePercent");
        var updated = store.Locked(() =>
        {
            var session = store.GetSessionMetadata(id) ?? throw new ArgumentException("Session not found.");
            session.ResponseSharePercent = share;
            store.Put("session", id, session);
            return store.GetSession(id, messageLimit: 200)!;
        });
        events.Publish("session.updated", null, new { id, responseBalanceChanged = true });
        return updated;
    }
    private ModelRecord NewModel(JsonElement args, string path)
    {
        if (args.TryGetProperty("visionProjectorPath", out var component) && component.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("Single-file model import cannot carry an image component. Configure its local path explicitly after import or import a version 2 model package.");
        var id = Optional(args, "id") ?? Guid.NewGuid().ToString("N"); Configuration.ValidateId(id);
        if (store.Get<ModelRecord>("model", id) is not null) throw new IOException("Model identifier already exists.");
        var model = new ModelRecord
        {
            Id = id, Name = Text(args, "name"), Path = path, SourceUrl = Optional(args, "sourceUrl"), License = Optional(args, "license"),
            Publisher = Optional(args, "publisher"), UpstreamModelId = Optional(args, "upstreamModelId"), UpstreamModelUrl = Optional(args, "upstreamModelUrl"),
            LicenseUrl = Optional(args, "licenseUrl"), UpstreamRevision = Optional(args, "upstreamRevision"), Description = Optional(args, "description"),
            Tags = args.TryGetProperty("tags", out var tags) ? CerebralJson.Read<List<string>>(tags) : [],
            PackageId = Optional(args, "packageId"), PackageVersion = Optional(args, "packageVersion"), PackagePurpose = Optional(args, "packagePurpose"),
            StarterPackage = args.TryGetProperty("starterPackage", out var starter) && starter.ValueKind == JsonValueKind.True,
            AutomaticContextSizing = !args.TryGetProperty("automaticContextSizing", out var automatic) || automatic.ValueKind == JsonValueKind.True,
            MaximumContextTokens = args.TryGetProperty("maximumContextTokens", out var maximum) && maximum.ValueKind == JsonValueKind.Number ? maximum.GetInt32() : 0,
            ContextTokens = Number(args, "contextTokens", 4096, 512, 1048576), ParallelRequests = Number(args, "parallelRequests", 1, 1, 16),
            GpuLayers = Number(args, "gpuLayers", -1, -1, 999), IdleMinutes = Number(args, "idleMinutes", -1, -1, 10080), ChatTemplate = Optional(args, "chatTemplate"),
            StopSequences = args.TryGetProperty("stopSequences", out var stops) ? CerebralJson.Read<List<string>>(stops) : []
        };
        if (model.MaximumContextTokens is < 0 or > 1048576 || model.MaximumContextTokens is > 0 and < 512) throw new ArgumentException("Automatic context ceiling must be zero or 512–1,048,576 tokens.");
        if (model.ChatTemplate?.Length > 32000 || model.ChatTemplate?.Contains('\0') == true || model.StopSequences.Count > 16 || model.StopSequences.Any(x => string.IsNullOrEmpty(x) || x.Length > 256 || x.Contains('\0'))) throw new ArgumentException("Invalid chat template or stop sequences.");
        foreach (var (value, maxLength, label) in new[]
        {
            (model.License, 128, "license"), (model.Publisher, 256, "publisher"), (model.UpstreamModelId, 256, "upstream model ID"),
            (model.UpstreamRevision, 256, "upstream revision"), (model.Description, 4000, "description"), (model.PackageId, 160, "package ID"),
            (model.PackageVersion, 64, "package version"), (model.PackagePurpose, 256, "package purpose")
        })
            if (value is not null && (value.Length > maxLength || value.Contains('\0') || value.Any(char.IsControl)))
                throw new ArgumentException($"Invalid model {label}.");
        foreach (var (value, label) in new[] { (model.SourceUrl, "artifact source URL"), (model.UpstreamModelUrl, "upstream model URL"), (model.LicenseUrl, "license URL") })
            if (value is not null && (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
                throw new ArgumentException($"Model {label} must be HTTP(S) without embedded credentials.");
        if (model.Tags.Count > 32 || model.Tags.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64 || x.Contains('\0') || x.Any(char.IsControl)))
            throw new ArgumentException("Model tags must contain at most 32 short non-empty values.");
        return model;
    }
    public static async Task ValidateGgufAsync(string path, CancellationToken ct)
    {
        _ = await Task.Run(() => GgufMetadataInspector.Inspect(path, ct), ct);
    }
    private async Task<ModelRecord> ImportAsync(JsonElement args, CancellationToken ct)
    {
        await modelFiles.WaitAsync(ct);
        try
        {
            var path = Path.GetFullPath(Text(args, "path")); await ValidateGgufAsync(path, ct); var model = NewModel(args, path);
            if (Flag(args, "copy"))
            {
                var destination = ModelDestination(model.Id + ".gguf");
                await using var source = File.OpenRead(path); await ReceiveFileAsync(source, destination, new FileInfo(path).Length, ct); model.Path = destination;
            }
            model.SizeBytes = new FileInfo(model.Path).Length;
            model = await ApplyModelMetadataAsync(model, args.TryGetProperty("contextTokens", out _), preserveDisplayName: true, ct);
            store.Put("model", model.Id, model); events.Publish("model.updated", null, model); return model;
        }
        finally { modelFiles.Release(); }
    }
    private string ModelDestination(string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use a simple .gguf filename without directories.");
        var directory = configuration.Get().ModelDirectory; Directory.CreateDirectory(directory); return Path.Combine(directory, fileName);
    }
    private async Task<ModelRecord> DownloadAsync(JsonElement args, CancellationToken ct)
    {
        var uri = new Uri(Text(args, "url")); if (uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Use an HTTP(S) model download without credentials in its URL.");
        await modelFiles.WaitAsync(ct);
        try
        {
            var model = NewModel(args, ""); model.Path = ModelDestination(Optional(args, "fileName") ?? model.Id + ".gguf"); model.SourceUrl = uri.ToString();
            await DownloadFileAsync(uri, model.Path, model.Id, ct);
            model.SizeBytes = new FileInfo(model.Path).Length;
            model = await ApplyModelMetadataAsync(model, args.TryGetProperty("contextTokens", out _), preserveDisplayName: true, ct);
            store.Put("model", model.Id, model); events.Publish("model.updated", null, model); return model;
        }
        finally { modelFiles.Release(); }
    }
    private async Task DownloadFileAsync(Uri uri, string destination, string transferId, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromHours(8));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await ReceiveFileAsync(input, destination, response.Content.Headers.ContentLength, timeout.Token); return;
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or IOException && !File.Exists(destination) && !timeout.IsCancellationRequested)
            {
                events.Publish("transfer", null, new { id = transferId, retry = attempt, message = ex.Message });
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), timeout.Token);
            }
        }
    }
    private async Task ReceiveFileAsync(Stream input, string destination, long? expectedLength, CancellationToken ct)
    {
        if (File.Exists(destination)) throw new IOException("Destination file already exists; choose a new name or import that file.");
        var partial = destination + ".partial";
        var created = false;
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                created = true;
                var buffer = new byte[1024 * 1024]; long total = 0; var next = DateTimeOffset.MinValue;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct); if (count == 0) break; total = checked(total + count);
                    if (expectedLength is { } bound && total > bound) throw new IOException("Transfer exceeds its declared component length.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    if (DateTimeOffset.UtcNow >= next) { events.Publish("transfer", null, new { file = Path.GetFileName(destination), bytes = total, expectedLength }); next = DateTimeOffset.UtcNow.AddSeconds(1); }
                }
                if (expectedLength is not null && total != expectedLength) throw new IOException("Transfer ended before the complete model arrived.");
                await output.FlushAsync(ct);
            }
            await ValidateGgufAsync(partial, ct); File.Move(partial, destination, false);
        }
        finally { if (created && File.Exists(partial)) File.Delete(partial); }
    }
    public Task<ModelRecord> ReceiveModelAsync(JsonElement metadata, Stream input, long? length, CancellationToken ct) =>
        ReceiveModelAsync(metadata, input, length, null, ct);
    private async Task<ModelRecord> ReceiveModelAsync(JsonElement metadata, Stream input, long? length, string? sourceUrl, CancellationToken ct)
    {
        await modelFiles.WaitAsync(ct);
        try
        {
            var model = NewModel(metadata, ""); model.Path = ModelDestination(model.Id + ".gguf");
            await ReceiveFileAsync(input, model.Path, length, ct); model.SizeBytes = new FileInfo(model.Path).Length;
            model = await ApplyModelMetadataAsync(model, metadata.TryGetProperty("contextTokens", out _), preserveDisplayName: true, ct);
            model.SourceUrl = sourceUrl ?? model.SourceUrl;
            store.Put("model", model.Id, model); events.Publish("model.updated", null, model); return model;
        }
        finally { modelFiles.Release(); }
    }
    private async Task<object> DistributeAsync(string id, string workerId, CancellationToken ct)
    {
        Configuration.ValidateId(id); Configuration.ValidateId(workerId);
        await modelFiles.WaitAsync(ct);
        try
        {
            var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
            if (model.Id != id) throw new InvalidDataException("Registered model identity does not match its storage key.");
            using var owner = await remoteModels.ConnectAsync(workerId, ct);
            var source = await ModelDistributionContent.DescribeAsync(configuration.Get().InstallationId, model, ct);
            return await ModelDistributionCoordinator.VerifyExistingAsync(owner, source, ct);
        }
        finally { modelFiles.Release(); }
    }
    private async Task<WorkerProbeResult> ProbeAsync(string id, CancellationToken ct)
    {
        var worker = configuration.Get().Workers.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("Worker not configured.");
        using var client = new CerebralClient(worker); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        PeerHostInfo info;
        if (string.IsNullOrWhiteSpace(worker.PeerToken))
        {
            var owner = await client.GetAsync<HostInfo>("health", cancellationToken: timeout.Token);
            info = new(owner.Product, owner.Version, owner.Protocol, owner.Name, owner.Architecture, owner.OperatingSystem, owner.Coordinator);
        }
        else info = await client.GetAsync<PeerHostInfo>("health", cancellationToken: timeout.Token);
        clock.Stop();
        if (info.Product != "Pegasus Cerebral" || info.Protocol != 1) throw new InvalidOperationException("Worker identity or protocol mismatch.");
        var metrics = await client.GetAsync<PeerWorkerMetrics>("peer.metrics", cancellationToken: timeout.Token);
        if (metrics.Host.Product != info.Product || metrics.Host.Protocol != info.Protocol || metrics.Host.Name != info.Name)
            throw new InvalidOperationException("Worker metrics identity does not match the probed peer.");
        var catalog = string.IsNullOrWhiteSpace(worker.PeerToken)
            ? null
            : await client.GetAsync<PeerCatalog>("peer.catalog", cancellationToken: timeout.Token);
        return new(info, metrics, Math.Max(0, clock.ElapsedMilliseconds), catalog);
    }
    internal static string Text(JsonElement value, string name) => Optional(value, name) is { Length: > 0 } text && !text.Contains('\0') ? text : throw new ArgumentException("Missing or invalid field: " + name);
    internal static string? Optional(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    internal static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    internal static int Number(JsonElement value, string name, int fallback, int min, int max)
    {
        var number = value.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;
        return number >= min && number <= max ? number : throw new ArgumentException("Out of range: " + name);
    }
}
