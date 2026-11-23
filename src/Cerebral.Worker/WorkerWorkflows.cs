using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Cerebral.Contracts;
using Cerebral.Contracts.Routing;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Resources;
using Cerebral.Engine.Routing;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;

namespace Cerebral.Worker;

/// <summary>Transport input deliberately excludes caller identity and the host-owned grant snapshot.</summary>
public sealed record SubmitWorkflowRequest(string RequestId, string ConversationId, string ProfileId,
    string DefinitionId, string DefinitionVersion, string UserText, string? AssignmentText, JsonElement Input,
    int StepLimit = 64)
{
    public IReadOnlyList<ContentPart> AcceptedContent { get; init; } = [];
    // Host-owned navigation metadata is separate from plug-in definition input.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerProjectId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowLocalGenerationChoice? LocalGeneration { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? OwnerVerification { get; init; }
}

/// <summary>Owner-supplied locator for a single already dispatched, uncertain file-edit job.</summary>
public sealed record WorkflowReceiptLookupRequest(string Id, long ExpectedRevision, string EffectId,
    string EffectSha256, string ProviderId, string ProviderFingerprint, string JobIdHint);

/// <summary>The new engine adapter shares admission and execution capacity with the existing service scheduler.</summary>
public sealed partial class WorkerWorkflows
{
    private IReadOnlyList<WorkerExecutionSnapshot.SelectedToolOwner> CaptureSelectedToolOwners(
        WorkerExecutionSnapshot.Snapshot snapshot) =>
        Array.AsReadOnly(snapshot.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => new WorkerExecutionSnapshot.SelectedToolOwner(tool.Name,
                servicePlugins?.LoadedWorkflowToolOwner(tool.Name))).ToArray());

    private void RequireSelectedToolOwnersCurrent(WorkerExecutionSnapshot.Snapshot snapshot)
    {
        if (!WorkerExecutionSnapshot.IsCurrentEdition(snapshot.SourceSettingsVersion)) return;
        foreach (var accepted in snapshot.SelectedToolOwners ??
                     throw new InvalidDataException("Selected service tool owners are missing."))
            if (servicePlugins?.LoadedWorkflowToolOwner(accepted.ToolName) != accepted.PackageId)
                throw new InvalidDataException("An accepted selected tool changed its loaded service owner.");
    }

    private readonly StateStore legacy;
    private readonly Configuration configuration;
    private readonly EventHub events;
    private readonly WorkflowPackageBindings packages;
    private readonly IToolDispatcher? tools;
    private readonly Action<WorkflowSubmission>? acceptedContentAdmission;
    private readonly Func<string, string, Func<WorkflowRun>, WorkflowRun>? packageAdmission;
    private readonly Func<string, string, IReadOnlyList<string>, Func<WorkflowRun>, WorkflowRun>? packageAdmissionWithContributions;
    private readonly WorkerContextContributionHost contextContributions;
    private readonly ServicePluginRuntime? servicePlugins;
    private readonly ServicePackageLifecycle? workflowSettingsLifecycle;
    private readonly AttachmentStore? attachments;
    private static readonly JsonSerializerOptions InputJson = new(CerebralJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public EventJournal Journal { get; }
    public WorkflowStore Store { get; }
    public WorkflowRegistry Registry { get; }
    public WorkflowRuntime Runtime { get; }
    public ResponseOutputPipeline OutputPipeline { get; }
    public EngineController? Engine { get; }
    public NativeCortexContinuity? NativeContinuity { get; }

    private static string ReceiptLookupEffectHash(WorkflowEffect effect) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CerebralJson.Element(effect).GetRawText())))
            .ToLowerInvariant();

    private static ToolExecutionEvidence RequireMissingFileEditReceipt(WorkflowRun run)
    {
        if (run.Status != WorkflowStatus.Waiting || !run.CancelRequested ||
            run.Effect is not { State: WorkflowEffectState.Unknown, CommandId: WorkerWorkflowEffects.ToolCommand,
                CommandVersion: 1, ReplayPolicy: WorkflowReplayPolicy.Never } effect ||
            effect.Result is not { } raw)
            throw new WorkflowConflictException("Only the preserved, cancelled Waiting file edit with an Unknown effect can use exact job receipt lookup.");
        var arguments = effect.Arguments.Deserialize<WorkerWorkflowEffects.ToolArguments>(InputJson);
        var result = raw.Deserialize<WorkerWorkflowEffects.ToolData>(InputJson);
        if (arguments is not { Name: "file.edit" } || arguments.Arguments.ValueKind != JsonValueKind.Object ||
            result is not { Name: "file.edit", Evidence: { Outcome: ToolExecutionOutcome.Unknown } evidence } ||
            evidence.RequestId != effect.Id || evidence.JobId is not null ||
            !evidence.ProviderId.StartsWith("mcp.", StringComparison.Ordinal))
            throw new WorkflowConflictException("The saved file-edit effect lacks the exact missing-job evidence required for lookup.");
        return evidence;
    }

    public object InspectReceiptLookup(string id)
    {
        var run = Store.Get(id);
        var effect = run.Effect;
        var effectHash = effect is null ? null : ReceiptLookupEffectHash(effect);
        string? providerId = null;
        string? fingerprint = null;
        string? refusal = null;
        try
        {
            var evidence = RequireMissingFileEditReceipt(run);
            providerId = evidence.ProviderId;
            fingerprint = (tools as IJobReceiptLookup)?.CurrentProviderFingerprint(providerId);
            if (fingerprint is null) throw new WorkflowConflictException("The original PAO provider registration is unavailable.");
            var hint = new JobReceiptLookupHint(effect!.Id, providerId, fingerprint, "inspection-placeholder");
            idleEffects?.RequireReceiptLookupAdmission(run, hint);
            if (idleEffects is null) throw new WorkflowConflictException("The host tool handler is unavailable.");
        }
        catch (Exception error) when (error is WorkflowConflictException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or JsonException)
        {
            refusal = error is WorkflowConflictException ? error.Message :
                "The saved effect or its current grants, path scope, or provider binding cannot be admitted.";
        }
        return new { id = run.Id, revision = run.Revision, effectId = effect?.Id,
            effectSha256Version = 1, effectSha256 = effectHash, providerId,
            providerFingerprint = fingerprint, eligible = refusal is null, refusal };
    }

    public JobReceiptLookupHint RequireReceiptLookup(WorkflowRun run, WorkflowReceiptLookupRequest request)
    {
        if (request.Id is not { Length: > 0 and <= 128 } ||
            request.EffectId is not { Length: > 0 and <= 128 } ||
            request.EffectSha256 is not { Length: 64 } || !request.EffectSha256.All(char.IsAsciiHexDigit) ||
            request.ProviderId is not { Length: > 4 and <= 256 } ||
            request.ProviderFingerprint is not { Length: 64 } || !request.ProviderFingerprint.All(char.IsAsciiHexDigit) ||
            request.JobIdHint is not { Length: > 0 and <= 256 })
            throw new ArgumentException("The exact receipt lookup coordinates are invalid.");
        if (run.Id != request.Id || run.Revision != request.ExpectedRevision ||
            run.Effect is not { } effect || effect.Id != request.EffectId ||
            !ReceiptLookupEffectHash(effect).Equals(request.EffectSha256, StringComparison.Ordinal))
            throw new WorkflowConflictException("The saved run revision or exact effect changed; inspect it again before lookup.");
        var evidence = RequireMissingFileEditReceipt(run);
        if (evidence.ProviderId != request.ProviderId ||
            tools is not IJobReceiptLookup locator ||
            locator.CurrentProviderFingerprint(request.ProviderId) != request.ProviderFingerprint)
            throw new WorkflowConflictException("The original provider registration changed or is unavailable.");
        var hint = new JobReceiptLookupHint(effect.Id, request.ProviderId, request.ProviderFingerprint,
            request.JobIdHint);
        (idleEffects ?? throw new WorkflowConflictException("The host tool handler is unavailable."))
            .RequireReceiptLookupAdmission(run, hint);
        return hint;
    }

    internal IDisposable BeginReceiptLookup(JobReceiptLookupHint hint) =>
        (idleEffects ?? throw new WorkflowConflictException("The host tool handler is unavailable."))
            .BeginReceiptLookup(hint);

    public bool HasActiveLocalModelWork(string modelId) =>
        Store.AnyActiveSettings(settings => WorkflowModelInterest.UsesLocalModel(settings, modelId)) || IdleUsesModel(modelId);

    public WorkerWorkflows(StateStore legacy, Configuration configuration, EventHub events,
        IEnumerable<IWorkflowModule> modules, IReadOnlyList<ServicePluginPackageBinding> packageBindings,
        IToolDispatcher? tools = null, Action<WorkflowSubmission>? acceptedContentAdmission = null, EventJournal? journal = null,
        Func<string, string, Func<WorkflowRun>, WorkflowRun>? packageAdmission = null,
        IEnumerable<IResponseOutputHook>? responseHooks = null,
        IEnumerable<ICommittedOutputObserver>? outputObservers = null,
        AttachmentStore? attachments = null, EngineController? engine = null,
        IEnumerable<IWorkflowAuditAdapter>? auditors = null,
        ServicePluginRuntime? contextRuntime = null,
        Func<string, string, IReadOnlyList<string>, Func<WorkflowRun>, WorkflowRun>? packageAdmissionWithContributions = null,
        Func<string, IReadOnlyList<string>, string?>? contextPackageProblem = null,
        ServicePackageLifecycle? workflowSettingsLifecycle = null)

        : this(null, legacy, configuration, events, modules, packageBindings, tools, acceptedContentAdmission, journal,
            packageAdmission, responseHooks, outputObservers, attachments, engine, auditors, contextRuntime,
            packageAdmissionWithContributions, contextPackageProblem, workflowSettingsLifecycle) { }

    internal WorkerWorkflows(IReadOnlyList<WorkerContextContributionSource>? contextSources, StateStore legacy, Configuration configuration, EventHub events,
        IEnumerable<IWorkflowModule> modules, IReadOnlyList<ServicePluginPackageBinding> packageBindings,
        IToolDispatcher? tools = null, Action<WorkflowSubmission>? acceptedContentAdmission = null, EventJournal? journal = null,
        Func<string, string, Func<WorkflowRun>, WorkflowRun>? packageAdmission = null,
        IEnumerable<IResponseOutputHook>? responseHooks = null,
        IEnumerable<ICommittedOutputObserver>? outputObservers = null,
        AttachmentStore? attachments = null, EngineController? engine = null,
        IEnumerable<IWorkflowAuditAdapter>? auditors = null,
        ServicePluginRuntime? contextRuntime = null,
        Func<string, string, IReadOnlyList<string>, Func<WorkflowRun>, WorkflowRun>? packageAdmissionWithContributions = null,
        Func<string, IReadOnlyList<string>, string?>? contextPackageProblem = null,
        ServicePackageLifecycle? workflowSettingsLifecycle = null)
    {
        this.legacy = legacy; this.configuration = configuration; this.events = events; this.tools = tools;
        this.acceptedContentAdmission = acceptedContentAdmission;
        this.packageAdmission = packageAdmission;
        this.packageAdmissionWithContributions = packageAdmissionWithContributions;
        this.servicePlugins = contextRuntime;
        this.workflowSettingsLifecycle = workflowSettingsLifecycle;
        this.attachments = attachments;
        Engine = engine;
        Journal = journal ?? new EventJournal(Path.Combine(legacy.DirectoryPath, "engine.db"));
        Store = new WorkflowStore(Journal);
        NativeContinuity = tools is null ? null : new NativeCortexContinuity(legacy, configuration, tools, events, Store, Journal);
        DataCatalog = new WorkflowDataCatalog(legacy);
        packages = new WorkflowPackageBindings(packageBindings.Append(WorkflowDataModule.PackageBinding).ToArray());
        contextContributions = new(legacy, packages, contextRuntime,
            RequiredContextGrants, contextPackageProblem, contextSources,
            // The host retains this resolver; it never invokes it before Registry is assigned below.
            (id, version) => Registry!.GetDefinition(id, version).Descriptor);
        var ownedModules = modules.ToArray();
        // Capture the actual host before wrappers: idle compaction owns this exact instance.
        idleEffects = ownedModules.OfType<WorkerWorkflowEffects>().SingleOrDefault();
        var loadedModules = ownedModules.Select(module => WorkerContextContributionProviderGuard.Wrap(module,
                contextContributions.RegisteredProviderIds, InvokeContextProviderAsync))
            .Select(AddToolContextProjection).Append(new WorkflowDataModule()).ToArray();
        Registry = new WorkflowRegistry(loadedModules);
        OutputPipeline = new ResponseOutputPipeline(responseHooks, outputObservers);
        Runtime = new WorkflowRuntime(Store, Registry, HasCapabilities, packages.Check, OutputPipeline,
            diagnostic => events.Publish("workflow.output.observer", diagnostic.RequestId, diagnostic), engine,
            new WorkflowAuditPipeline(auditors), ReadAuditPolicy);
    }

    // The host calls this only during admission/restoration, after the frozen registry is built.
    private IReadOnlyList<string> RequiredContextGrants(WorkflowSubmission submission, IReadOnlyList<string> providers) =>
        Registry.GetDefinition(submission.DefinitionId, submission.DefinitionVersion).Descriptor.RequiredCapabilities
            .Concat(providers.SelectMany(provider => Registry.GetCommand(provider,
                contextContributions.ProviderVersion(provider)).Descriptor.RequiredCapabilities))
            .Distinct(StringComparer.Ordinal).ToArray();

    private async Task<WorkflowCommandResult> InvokeContextProviderAsync(
        Cerebral.Contracts.Context.IContextProviderCommandHandler provider, WorkflowCommandContext context,
        bool reconcile, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (context.RunId != context.Submission.RequestId)
                throw new UnauthorizedAccessException("Context effects require the accepted workflow owner.");
            var accepted = ExactAcceptedContext(context.Submission);
            var saved = Store.Get(context.RunId);
            if (saved.Effect is not { } effect || effect.Id != context.EffectId ||
                effect.CommandId != provider.Provider.Id || effect.CommandVersion != provider.Provider.Version ||
                !JsonElement.DeepEquals(effect.Arguments, context.Arguments))
                throw new UnauthorizedAccessException("Context effects require the exact durable intent.");
            var requested = Cerebral.Engine.Context.ContextProviderNormalizer.ReadRequest(provider.Provider, context.Arguments);
            var restored = await contextContributions.RestoreWithObservationAsync(accepted, requested.ContextRevision,
                requested.ExclusionRevision, token).ConfigureAwait(false);
            if (!restored.View.Items.Any(item => item.Status == "ready" && item.Request is not null &&
                JsonElement.DeepEquals(CerebralJson.Element(item.Request), context.Arguments)))
                throw new UnauthorizedAccessException("Context differs from its immutable accepted request.");
            contextContributions.RecheckCurrent(accepted, restored.CurrentFacts);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(reconcile ? WorkflowEffectState.Unknown : WorkflowEffectState.Cancelled,
            Error: "Context admission was cancelled before provider dispatch."); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or JsonException)
        { return new(reconcile ? WorkflowEffectState.Unknown : WorkflowEffectState.Failed,
            Error: "The exact accepted context admission is unavailable or changed; no provider was dispatched."); }

        // Entry may already have effects or an uncertain outcome. The runtime owns post-entry
        // exception/cancellation handling; it must never be mislabeled as pre-dispatch refusal.
        return reconcile ? await provider.ReconcileAsync(context, token).ConfigureAwait(false)
            : await provider.ExecuteAsync(context, token).ConfigureAwait(false);
    }

    internal Task<WorkerWorkflowEffects.ContextContributionsData> RestoreContextContributionsAsync(
        WorkflowSubmission accepted, long contextRevision, long exclusionRevision, CancellationToken token) =>
        contextContributions.RestoreAsync(ExactAcceptedContext(accepted), contextRevision, exclusionRevision, token);

    internal void RequireCurrentContextContributions(WorkflowSubmission accepted) =>
        contextContributions.RequireCurrent(ExactAcceptedContext(accepted));

    internal IReadOnlyList<WorkerContextContributionAdmission> HistoricalContextAdmissions(WorkflowRun run) =>
        contextContributions.HistoricalAdmissions(run);

    private WorkflowSubmission ExactAcceptedContext(WorkflowSubmission supplied)
    {
        var saved = Store.Get(supplied.RequestId).Submission;
        if (!JsonElement.DeepEquals(CerebralJson.Element(saved), CerebralJson.Element(supplied)))
            throw new UnauthorizedAccessException("Context restoration requires the immutable accepted submission.");
        return saved;
    }

    public static T Read<T>(JsonElement input) => input.Deserialize<T>(InputJson)
        ?? throw new ArgumentException("Missing workflow command input.");

    private static WorkflowAuditPolicy ReadAuditPolicy(WorkflowSubmission submission)
        => submission.AuditPolicy ?? new();

    private static WorkflowAuditPolicy? ConfiguredAuditPolicy(AgentProfile profile)
    {
        if (!profile.PluginSettings.TryGetValue("cerebral.audit", out var value)) return null;
        var policy = Read<WorkflowAuditPolicy>(value);
        if (policy.TimeoutMilliseconds is < 1 or > 30000 || policy.AdapterId?.Length > 256)
            throw new ArgumentException("Audit policy requires a bounded adapter identity and 1–30000 ms timeout.");
        return policy;
    }

    public WorkflowRun Submit(SubmitWorkflowRequest input, AgentProfile? actor)
    {
        input = FreezeInput(input);
        if (input.OwnerVerification is not null)
            throw new UnauthorizedAccessException("Owner verification requires the authenticated local owner pipe.");
        if (input.LocalGeneration is not null)
            throw new UnauthorizedAccessException("Local generation requires asynchronous exact model and definition admission.");
        ValidateCaller(input, actor);
        PreflightOwnerProjectAdmission(input, actor, null, null);
        using var idleAdmission = BeginIdleAdmission(input.ConversationId);
        return input.DefinitionId == WorkflowDataModule.DefinitionId
            ? SubmitDataWorkflow(input, actor, idleAdmission)
            : SubmitCore(input, actor, null, null, null, null, idleAdmission: idleAdmission);
    }

    private static SubmitWorkflowRequest FreezeInput(SubmitWorkflowRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.DefinitionId == Cerebral.Contracts.Models.IsolatedTextGenerationContract.DefinitionId)
            throw new UnauthorizedAccessException("Isolated generation requires its owner-only typed admission operation.");
        if (input.OwnerProjectId is not null &&
            input.DefinitionId is ConversationWorkflowModule.DefinitionId or WorkflowDataModule.DefinitionId)
            throw new ArgumentException("Owner project metadata is for exact non-chat workflow submissions.");
        input.LocalGeneration?.Validate();
        return input with
        {
            Input = input.Input.Clone(), AcceptedContent = WorkflowAcceptedContent.Freeze(input.AcceptedContent),
            LocalGeneration = input.LocalGeneration is { } choice
                ? new WorkflowLocalGenerationChoice(choice.SchemaVersion, choice.Route,
                    choice.ContextTokens, choice.VisibleOutputTokens) : null,
            OwnerVerification = input.OwnerVerification?.Clone()
        };
    }

    public async Task<WorkflowRun> SubmitAsync(SubmitWorkflowRequest input, AgentProfile? actor, CancellationToken cancellationToken)
        => await SubmitAsyncCore(input, actor, null, null, cancellationToken).ConfigureAwait(false);

    internal MainContextBindings OwnedContexts => new(legacy);

    internal void BindCoordinatorSource(CoordinatorSourceBinding binding) => legacy.Locked(() =>
    {
        if (legacy.Get<CoordinatorSourceBinding>("coordination-source", binding.TargetRunId) is { } prior && prior != binding)
            throw new UnauthorizedAccessException("The coordinator attempt already has another source policy.");
        legacy.Put("coordination-source", binding.TargetRunId, binding);
        return true;
    });

    internal AgentProfile CoordinationSourcePolicy(string sourceRunId)
    {
        var source = Store.Get(sourceRunId).Submission;
        if (!ConversationRolePolicy.IsMain(source))
            throw new UnauthorizedAccessException("Coordination requires an accepted Main source policy.");
        var snapshot = WorkerExecutionSnapshot.Read(source.SettingsSnapshot);
        return WorkerExecutionSnapshot.EffectiveProfile(legacy, snapshot, source.ProfileId);
    }

    internal static AgentProfile IntersectCoordinationPolicy(AgentProfile target, AgentProfile source)
    {
        var result = CerebralJson.Read<AgentProfile>(CerebralJson.Element(target));
        result.AllowedTools = source.AllowedTools.Where(name => ChatToolPermissions.BaselineTool(target, name)).ToList();
        result.Capabilities = target.Capabilities.Intersect(source.Capabilities, StringComparer.Ordinal).ToList();
        result.AllowedRoots = WorkerExecutionSnapshot.IntersectRoots(target.AllowedRoots, source.AllowedRoots);
        return result;
    }

    private CoordinatorSourceBinding? OwnedSourceBinding(WorkflowSubmission submission)
    {
        var member = OwnedContexts.Owner(submission.ConversationId);
        if (member is null) return null;
        if (member.ProfileId != submission.ProfileId || member.ProjectId != submission.ProjectId ||
            member.DefinitionId != submission.DefinitionId || member.DefinitionVersion != submission.DefinitionVersion)
            throw new UnauthorizedAccessException("The owned context submission tuple changed.");
        var root = Store.GetConversation(ConversationWorkflowModule.DefinitionId, member.MainConversationId) ??
            throw new UnauthorizedAccessException("The owning Main is unavailable.");
        OwnedContexts.Require(root.Id, root.ProfileId, root.ProjectId, member.ContextId);
        var coordinator = submission;
        if (member.Role == "worker")
        {
            if (submission.ParentRunId is not { } parentId ||
                OwnedContexts.Owner((coordinator = Store.Get(parentId).Submission).ConversationId) is not { Role: "coordinator" } parent ||
                parent.MainConversationId != member.MainConversationId)
                throw new UnauthorizedAccessException("The worker no longer has its exact owned coordinator lineage.");
        }
        if (coordinator.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.collaboration" } ||
            coordinator.ParentRunId is not null)
            throw new UnauthorizedAccessException("An owned context has a different authenticated caller.");
        var grant = legacy.Get<CoordinatorSourceBinding>("coordination-source", coordinator.RequestId) ??
            throw new UnauthorizedAccessException("The owned coordinator has no frozen source policy binding.");
        if (grant.TargetRunId != coordinator.RequestId || grant.MainConversationId != member.MainConversationId ||
            Store.Get(grant.SourceRunId).Submission.ConversationId != member.MainConversationId)
            throw new UnauthorizedAccessException("The coordinator source policy lineage changed.");
        return grant;
    }

    // A coordinator can assign its own workers without the independent delegation grant.
    // The frozen source and admitted owned tuple are required; service labels alone confer nothing.
    internal bool CanAssignWorkers(WorkflowSubmission submission, AgentProfile current)
    {
        if (ConversationRolePolicy.IsMain(submission) || submission.ParentRunId is not null ||
            current.Id != submission.ProfileId || current.Role != "lead") return false;
        var member = OwnedContexts.Owner(submission.ConversationId);
        if (member is null)
            return current.Capabilities.Contains("agents.delegate", StringComparer.Ordinal);
        if (member.Role != "coordinator" || !ConversationRolePolicy.IsCoordinator(submission))
            return false;
        var grant = OwnedSourceBinding(submission) ??
            throw new UnauthorizedAccessException("The owned coordinator source binding is unavailable.");
        var source = Store.Get(grant.SourceRunId);
        return ConversationRolePolicy.IsMain(source.Submission) && !source.IsTerminal && !source.CancelRequested;
    }

    internal AgentProfile NarrowOwnedExecutionPolicy(WorkflowSubmission submission, AgentProfile effective) =>
        OwnedSourceBinding(submission) is { } grant
            ? IntersectCoordinationPolicy(effective, CoordinationSourcePolicy(grant.SourceRunId)) : effective;

    internal ScopedPathAccessScope? NarrowOwnedPathScope(WorkflowSubmission submission,
        ScopedPathAccessScope? targetScope, AgentProfile target, bool fullAccess)
    {
        if (OwnedSourceBinding(submission) is not { } grant) return targetScope;
        var source = Store.Get(grant.SourceRunId).Submission;
        var sourceScope = ConversationPathAuthority.Resolve(legacy, Store, source,
            WorkerExecutionSnapshot.Read(source.SettingsSnapshot), fullAccess) ??
            ConversationPathGrants.ProfileScope(CoordinationSourcePolicy(grant.SourceRunId));
        return ConversationPathAuthority.Intersect(
            targetScope ?? ConversationPathGrants.ProfileScope(target), sourceScope);
    }

    internal async Task<WorkflowRun> SubmitServiceAsync(SubmitWorkflowRequest input, string serviceId,
        Func<AgentProfile, AgentProfile>? executionProfileTransform, CancellationToken cancellationToken)
    {
        Configuration.ValidateId(serviceId);
        return await SubmitAsyncCore(input, null, new EventActor(EventActorKind.Service, serviceId),
            executionProfileTransform,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<WorkflowRun> SubmitChildAsync(SubmitWorkflowRequest input, string parentRunId, int expectedHelperRevision, CancellationToken cancellationToken) =>
        SubmitAsyncCore(input, null, new EventActor(EventActorKind.Service, "cerebral.coordination"),
            profile =>
            {
                profile.Capabilities.RemoveAll(capability => capability is "agents.delegate" or "agents.collaborate");
                return profile;
            }, cancellationToken, parentRunId, expectedHelperRevision);

    private async Task<WorkflowRun> SubmitAsyncCore(SubmitWorkflowRequest input, AgentProfile? actor,
        EventActor? callerOverride, Func<AgentProfile, AgentProfile>? executionProfileTransform,
        CancellationToken cancellationToken, string? parentRunId = null, int? expectedHelperRevision = null,
        SourceBoundWorkflowBinding? sourceBound = null,
        OwnerVerificationAdmission? ownerVerification = null)
    {
        input = FreezeInput(input); // Freeze metadata and JSON before asynchronous configuration discovery.
        if (ownerVerification is null && input.OwnerVerification is not null)
            throw new UnauthorizedAccessException("Owner verification requires the authenticated local owner pipe.");
        if (ownerVerification is not null)
        {
            if (input.OwnerVerification is not { } supplied ||
                OwnerVerificationAdmission.Read(supplied).RecipeSha256 != ownerVerification.RecipeSha256)
                throw new UnauthorizedAccessException("The submitted owner verification recipe changed.");
            RequireOwnerVerificationTuple(input, ownerVerification);
            if (actor is not null || callerOverride is not null || parentRunId is not null ||
                executionProfileTransform is not null || sourceBound is not null || tools is null)
                throw new UnauthorizedAccessException("Owner verification requires one direct local owner submission.");
            if (Store.TryGet(input.RequestId) is { } retained)
            {
                var saved = WorkerExecutionSnapshot.Read(retained.Submission.SettingsSnapshot);
                if (saved.OwnerVerification?.RecipeSha256 != ownerVerification.RecipeSha256)
                    throw new WorkflowConflictException("Retained request identity has a different owner verification recipe.");
            }
        }
        if (input.LocalGeneration is not null &&
            (actor is not null || callerOverride is not null || parentRunId is not null ||
             executionProfileTransform is not null || tools is null))
            throw new UnauthorizedAccessException("Only a direct owner with exact local model admission may select local generation.");
        if (input.LocalGeneration is not null && input.DefinitionId == WorkflowDataModule.DefinitionId)
            throw new UnauthorizedAccessException("Data workflows cannot inherit a local generation choice.");
        ValidateCaller(input, actor);
        if (input.OwnerProjectId is not null && (callerOverride is not null || parentRunId is not null))
            throw new UnauthorizedAccessException("Only a direct owner submission may select its project.");
        PreflightOwnerProjectAdmission(input, actor, callerOverride, parentRunId);
        using var idleAdmission = BeginIdleAdmission(input.ConversationId);
        if (input.DefinitionId == WorkflowDataModule.DefinitionId)
        {
            if (parentRunId is not null || executionProfileTransform is not null || callerOverride is not null)
                throw new UnauthorizedAccessException("Data workflows require their direct source assignment admission.");
            return SubmitDataWorkflow(input, actor, idleAdmission);
        }
        input = PrepareConversationPreferenceInput(input, actor, callerOverride, parentRunId, out var propertiesRevision);
        if (tools is null || Store.TryGet(input.RequestId) is not null)
            return SubmitCore(input, actor, null, null, null, callerOverride, parentRunId, idleAdmission,
                propertiesRevision, preferencesPrepared: true, sourceBound: sourceBound);
        // Complete the legacy host-default normalization before freezing the profile. CaptureAsync
        // also reads configuration; its lazy default-model binding must not look like a concurrent edit.
        configuration.Get();
        var currentProfile = legacy.Get<AgentProfile>("agent", input.ProfileId) ??
            throw new ArgumentException("Agent profile not found.");
        if (expectedHelperRevision is { } expected && currentProfile.Revision != expected)
            throw new WorkflowConflictException("Selected helper revision changed before child snapshot capture.");
        var profile = executionProfileTransform is null ? currentProfile :
            executionProfileTransform(CerebralJson.Read<AgentProfile>(CerebralJson.Element(currentProfile)));
        ValidateNarrowedProfile(currentProfile, profile);
        var definition = Registry.GetDefinition(input.DefinitionId, input.DefinitionVersion);
        RequireLocalGenerationDefinition(input.LocalGeneration, definition.Descriptor);
        WorkflowSchema.Validate(input.Input, definition.Descriptor.InputSchema);
        // The first native turn has no retained original yet. Resolve the eventual conversation
        // project before freezing its permission binding, then recheck it at durable creation.
        var (resolvedProjectId, original) = ResolveChatPermissionProject(input, actor,
            callerOverride, parentRunId, sourceBound);
        ChatToolPermissions.Binding? chatPermissionBinding = null;
        if (parentRunId is { } selectedParent)
        {
            var parent = Store.Get(selectedParent).Submission;
            var parentSnapshot = WorkerExecutionSnapshot.Read(parent.SettingsSnapshot);
            var parentEffective = NarrowOwnedExecutionPolicy(parent,
                WorkerExecutionSnapshot.EffectiveProfile(legacy, parentSnapshot, parent.ProfileId));
            profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            profile.AllowedTools = parentEffective.AllowedTools.Where(name =>
                ChatToolPermissions.BaselineTool(profile, name)).Distinct(StringComparer.Ordinal).ToList();
            profile.Capabilities = profile.Capabilities.Intersect(parentEffective.Capabilities,
                StringComparer.Ordinal).ToList();
            var parentBinding = parentSnapshot.ChatPermissions ??
                ChatToolPermissions.Baseline(parentSnapshot.Profile, parent.ConversationId, parent.ProjectId);
            if (parentBinding.ProjectId != parent.ProjectId)
                throw new UnauthorizedAccessException("The parent chat permission project binding is inconsistent with its saved run.");
            chatPermissionBinding = ChatToolPermissions.Child(profile, parentBinding, selectedParent);
        }
        else if (input.DefinitionId == ConversationWorkflowModule.DefinitionId)
        {
            // Service-originated native work (including collaboration) needs the target chat's
            // exact baseline binding, but may never capture owner-approved exceptions.
            var policy = callerOverride is null
                ? ConversationProperties.ReadAdmissionSettings(legacy, input.ConversationId).ToolPermissionPolicy
                : null;
            if (policy is null)
                chatPermissionBinding = ChatToolPermissions.Baseline(profile, input.ConversationId,
                    resolvedProjectId);
            else
            {
                if (original is null)
                    throw new UnauthorizedAccessException("A chat permission cannot predate its accepted original turn.");
                if (tools is not ToolDispatcher registeredTools)
                    throw new InvalidOperationException("Owner chat permissions require the registered tool catalog.");
                var catalog = ChatRegisteredToolCatalog.Bind(await registeredTools
                    .ListRegisteredAsync(cancellationToken).ConfigureAwait(false),
                    servicePlugins);
                (profile, chatPermissionBinding) = ChatToolPermissions.Capture(profile, policy,
                    catalog, input.ConversationId, resolvedProjectId);
            }
        }
        var expectedProfile = JsonSerializer.Serialize(currentProfile, CerebralJson.Options);
        var expectedCompactionRequestId = input.DefinitionId == ConversationWorkflowModule.DefinitionId
            ? Store.GetPendingConversationCompaction(input.ConversationId)?.RequestId : null;
        var authenticatedCaller = callerOverride ?? (actor is null
            ? new EventActor(EventActorKind.User, "owner") : new EventActor(EventActorKind.Agent, actor.Id));
        // Freeze the admitted chat policy before the Main model/action ceiling. Coordination
        // intersects this policy with the target profile; Main dispatch is separately narrowed.
        if (OwnedContexts.Owner(input.ConversationId) is { } namedContext)
        {
            profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            profile.Name = namedContext.DisplayName;
        }
        var pathAdmission = legacy.Locked(() => ConversationPathAuthority.Capture(legacy, Store, input,
            authenticatedCaller,
            resolvedProjectId, parentRunId, tools.FullAccessActive));
        var snapshot = await WorkerExecutionSnapshot.CaptureWithLocalGenerationAsync(legacy, profile, configuration, tools,
            packages.Capture(input.DefinitionId), cancellationToken,
            requireCompactor: expectedCompactionRequestId is not null || IdleCompactionPreference.EnabledFor(profile),
            projectId: resolvedProjectId,
            toolReceiptConsumer: WorkerToolContextProjectionAdmission.FreezeConsumer(definition.Descriptor),
            localGenerationConsumer: FreezeLocalGenerationConsumer(definition.Descriptor),
            authenticatedCaller: authenticatedCaller, parentRunId: parentRunId, definitionId: input.DefinitionId)
            .ConfigureAwait(false);
        // The base snapshot carries no plugin values. Admit only the host-reviewed subset before
        // a value-bearing edition-6 snapshot or any context/effect record can be serialized.
        var selectedSnapshot = WorkerExecutionSnapshot.Read(snapshot);
        var selectedEdition = chatPermissionBinding is null ? selectedSnapshot.ResolvedSettingsVersion :
            WorkerExecutionSnapshot.ChatPermissionsVersion;
        selectedSnapshot = selectedSnapshot with { ResolvedSettingsVersion = selectedEdition,
            SourceSettingsVersion = selectedEdition,
            SelectedToolOwners = CaptureSelectedToolOwners(selectedSnapshot),
            SourceBoundBackground = sourceBound, ChatPermissions = chatPermissionBinding };
        selectedSnapshot = selectedSnapshot with
        {
            PackageBindings = packages.Capture(input.DefinitionId, SelectedWorkflowToolOwners(selectedSnapshot))
        };
        RequireLocalGenerationSnapshot(input.LocalGeneration, selectedSnapshot, definition.Descriptor);
        snapshot = WorkerExecutionSnapshot.Bounded(CaptureWorkflowPortableSettings(
            selectedSnapshot, currentProfile));
        var trustedIdentity = new WorkflowSubmission(input.RequestId, input.ConversationId, input.ProfileId,
            input.DefinitionId, input.DefinitionVersion, "", null, WorkflowContract.EmptyObject, snapshot,
            input.StepLimit, authenticatedCaller)
            { ParentRunId = parentRunId };
        var capturedContributions = await contextContributions.CaptureAsync(trustedIdentity,
            WorkerExecutionSnapshot.Read(snapshot), cancellationToken).ConfigureAwait(false);
        snapshot = WorkerExecutionSnapshot.Bounded(capturedContributions);
        snapshot = WorkerExecutionSnapshot.Bounded(ConversationPathAuthority.Bind(snapshot, pathAdmission.Binding));
        if (ownerVerification is not null)
        {
            RequireOwnerVerificationCurrent(input, WorkerExecutionSnapshot.Read(snapshot), ownerVerification);
            snapshot = WorkerExecutionSnapshot.BindOwnerVerification(snapshot, ownerVerification);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return SubmitCore(input, actor, snapshot, expectedProfile, expectedCompactionRequestId, callerOverride, parentRunId, idleAdmission,
            propertiesRevision, preferencesPrepared: true, pathAdmission: pathAdmission, sourceBound: sourceBound);
    }

    private static void ValidateCaller(SubmitWorkflowRequest input, AgentProfile? actor)
    {
        Configuration.ValidateId(input.RequestId);
        Configuration.ValidateId(input.ConversationId);
        Configuration.ValidateId(input.ProfileId);
        if (actor is not null && actor.Id != input.ProfileId)
            throw new UnauthorizedAccessException("A calling agent can submit only under its own profile.");
        if (actor is not null && input.OwnerProjectId is not null)
            throw new UnauthorizedAccessException("Agent submissions cannot select owner project metadata.");
    }

    private void PreflightOwnerProjectAdmission(SubmitWorkflowRequest input, AgentProfile? actor,
        EventActor? callerOverride, string? parentRunId)
    {
        // Inspect the retained RID before deciding whether this is an ordinary chat/data path.
        // An incoming definition cannot reclassify an accepted exact-workflow RID.
        legacy.Locked(() =>
        {
            var prior = Store.TryGet(input.RequestId);
            if (prior is null)
            {
                if (input.OwnerProjectId is not null)
                    _ = ResolveProjectId(input, parentRunId, actor, callerOverride);
                return true;
            }
            if (prior.Submission.DefinitionId is ConversationWorkflowModule.DefinitionId or WorkflowDataModule.DefinitionId)
                return true; // Existing chat/data admission remains on its original path.

            // A rejected exact RID must not retire idle work or normalize configuration.
            var caller = callerOverride ?? (actor is null
                ? new EventActor(EventActorKind.User, "owner", "You")
                : new EventActor(EventActorKind.Agent, actor.Id, actor.Name));
            if (prior.Submission.ParentRunId != parentRunId)
                throw new WorkflowConflictException("Request identity belongs to a different delegation lineage.");
            if (prior.Submission.Caller?.Kind != caller.Kind || prior.Submission.Caller?.Id != caller.Id)
                throw new UnauthorizedAccessException("Request identifier belongs to another caller.");
            if (prior.Submission.ParentRunId is null && prior.Submission.Caller?.Kind == EventActorKind.User &&
                input.OwnerProjectId != prior.Submission.ProjectId)
                throw new WorkflowConflictException("The retained request has a different owner project binding.");
            WorkflowStore.RequireEquivalentRetainedSubmission(prior.Submission,
                RetainedSubmission(input, prior, parentRunId));
            return true;
        });
    }

    private static WorkflowSubmission RetainedSubmission(SubmitWorkflowRequest input, WorkflowRun prior, string? parentRunId) =>
        new(input.RequestId, input.ConversationId, input.ProfileId,
            input.DefinitionId, input.DefinitionVersion, input.UserText, input.AssignmentText,
            input.Input, prior.Submission.SettingsSnapshot, input.StepLimit, prior.Submission.Caller)
        { ActiveTimeLimitSeconds = prior.Submission.ActiveTimeLimitSeconds, PolicyStepLimit = prior.Submission.PolicyStepLimit,
            FailureLimits = prior.Submission.FailureLimits, AcceptedContent = input.AcceptedContent,
            HostAddedRequiredSuccessfulTools = prior.Submission.HostAddedRequiredSuccessfulTools.ToArray(),
            HostPluginTestMountIds = prior.Submission.HostPluginTestMountIds.ToArray(),
            ConversationCompactionRequestId = prior.Submission.ConversationCompactionRequestId,
            ProjectId = prior.Submission.ProjectId, ParentRunId = parentRunId, AuditPolicy = prior.Submission.AuditPolicy,
            LocalGeneration = input.LocalGeneration };

    private static void ValidateNarrowedProfile(AgentProfile current, AgentProfile selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Id != current.Id || selected.Revision != current.Revision ||
            selected.ModelId != current.ModelId || selected.WorkerId != current.WorkerId ||
            selected.ModelWorkerId != current.ModelWorkerId || selected.Role != current.Role ||
            selected.MaxSteps > current.MaxSteps || selected.MaxSeconds > current.MaxSeconds ||
            selected.Capabilities.Except(current.Capabilities, StringComparer.Ordinal).Any() ||
            selected.AllowedTools.Any(tool => !current.AllowedTools.Contains("*", StringComparer.Ordinal) &&
                !current.AllowedTools.Contains(tool, StringComparer.Ordinal)) ||
            selected.AllowedRoots.Except(current.AllowedRoots,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Any() ||
            !JsonElement.DeepEquals(CerebralJson.Element(selected.PluginSettings), CerebralJson.Element(current.PluginSettings)))
            throw new UnauthorizedAccessException("Internal workflow admission may only narrow the selected execution profile.");
    }

    private WorkflowRun SubmitCore(SubmitWorkflowRequest input, AgentProfile? actor, JsonElement? captured,
        string? expectedProfile, string? expectedCompactionRequestId, EventActor? callerOverride, string? parentRunId = null,
        IdleAdmission? idleAdmission = null, long? expectedPropertiesRevision = null, bool preferencesPrepared = false,
        ConversationPathAuthority.Admission? pathAdmission = null, SourceBoundWorkflowBinding? sourceBound = null)
    {
        var committedInputDelivery = Volatile.Read(ref committedRootUserInputDelivery);
        CommittedRootUserInputReceipt? committedInputReceipt = null;
        if (!preferencesPrepared)
            input = PrepareConversationPreferenceInput(input, actor, callerOverride, parentRunId, out expectedPropertiesRevision);
        WorkflowRun DurableCreate() => legacy.Locked(() =>
        {
            ValidateCaller(input, actor);
            var caller = callerOverride ?? (actor is null ? new EventActor(EventActorKind.User, "owner", "You")
                : new EventActor(EventActorKind.Agent, actor.Id, actor.Name));
            // A lost response must remain retrievable even after settings, module availability or capacity change.
            if (Store.TryGet(input.RequestId) is { } prior)
            {
                if (sourceBound is not null) RequireEquivalentSourceBoundRetained(prior, sourceBound);
                RequireEquivalentOwnerVerificationRetained(input.OwnerVerification,
                    prior.Submission.SettingsSnapshot);
                if (prior.Submission.ParentRunId != parentRunId)
                    throw new WorkflowConflictException("Request identity belongs to a different delegation lineage.");
                if (prior.Submission.Caller?.Kind != caller.Kind || prior.Submission.Caller.Id != caller.Id)
                    throw new UnauthorizedAccessException("Request identifier belongs to another caller.");
                if (prior.Submission.ParentRunId is null && prior.Submission.Caller?.Kind == EventActorKind.User &&
                    prior.Submission.DefinitionId is not (ConversationWorkflowModule.DefinitionId or WorkflowDataModule.DefinitionId) &&
                    input.OwnerProjectId != prior.Submission.ProjectId)
                    throw new WorkflowConflictException("The retained request has a different owner project binding.");
                return Store.Submit(RetainedSubmission(input, prior, parentRunId));
            }

            var (resolvedProjectId, _) = ResolveChatPermissionProject(input, actor,
                callerOverride, parentRunId, sourceBound);
            if (tools is not null && captured is null) throw new InvalidOperationException("Use asynchronous admission to capture the execution configuration.");

            var profile = legacy.Get<AgentProfile>("agent", input.ProfileId)
                ?? throw new ArgumentException("Agent profile not found.");
            if (parentRunId is { } parentId)
            {
                var parent = Store.Get(parentId);
                var parentSnapshot = WorkerExecutionSnapshot.Read(parent.Submission.SettingsSnapshot);
                var currentParent = WorkerExecutionSnapshot.EffectiveProfile(legacy, parentSnapshot, parent.Submission.ProfileId);
                if (parentSnapshot.RoutingFingerprint != WorkerExecutionSnapshot.RoutingFingerprint(configuration.Get()) ||
                    parent.IsTerminal || parent.CancelRequested || parent.Submission.ParentRunId is not null ||
                    !CanAssignWorkers(parent.Submission, currentParent) ||
                    profile.Role != "helper")
                    throw new UnauthorizedAccessException("Parent delegation grants or child role changed before admission.");
            }
            if (expectedProfile is not null && expectedProfile != JsonSerializer.Serialize(profile, CerebralJson.Options))
                throw new WorkflowConflictException("The selected profile changed during admission. Retry with the current configuration.");
            if (captured is { } approvedSnapshot)
            {
                var current = WorkerExecutionSnapshot.Read(approvedSnapshot);
                if (current.ChatPermissions is { } chat && chat.ProjectId != resolvedProjectId)
                    throw new WorkflowConflictException("The chat permission project binding changed during admission.");
                if (sourceBound is not null) RequireSourceBoundCurrentForCommit(sourceBound, current);
                RequireSelectedToolOwnersCurrent(current);
                RecheckWorkflowPortableSettings(current);
                RequireLocalGenerationSnapshot(input.LocalGeneration, current,
                    Registry.GetDefinition(input.DefinitionId, input.DefinitionVersion).Descriptor);
                if (current.OwnerVerification is { } verification)
                {
                    if (actor is not null || callerOverride is not null || parentRunId is not null || sourceBound is not null)
                        throw new UnauthorizedAccessException("Owner verification cannot be inherited by a child or service run.");
                    RequireOwnerVerificationCurrent(input, current, verification);
                }
            }
            else if (input.LocalGeneration is not null)
                throw new UnauthorizedAccessException("Local generation requires an exact frozen model snapshot.");
            else if (sourceBound is not null)
                throw new UnauthorizedAccessException("Source-bound work requires its exact host-frozen snapshot.");
            if (profile.WorkerId != "local")
                throw new InvalidOperationException("This workflow requires a local profile; remote workflow routing is not yet configured.");
            if (legacy.GetSessionMetadata(input.ConversationId) is not null)
                throw new InvalidOperationException("This conversation uses the previous execution route. Start a new workflow conversation.");
            ValidateConversationPreferenceRevision(input.ConversationId, expectedPropertiesRevision);
            ValidateOwnerConversationDirectory(input, caller, parentRunId);
            pathAdmission ??= ConversationPathAuthority.Capture(legacy, Store, input, caller, resolvedProjectId,
                parentRunId, tools?.FullAccessActive == true);
            ConversationPathAuthority.Recheck(legacy, pathAdmission);
            var pendingCompaction = input.DefinitionId == ConversationWorkflowModule.DefinitionId
                ? Store.GetPendingConversationCompaction(input.ConversationId) : null;
            if (expectedProfile is not null && pendingCompaction?.RequestId != expectedCompactionRequestId)
                throw new WorkflowConflictException("The conversation compaction request changed during admission. Retry the turn against current service state.");
            if (pendingCompaction is not null && captured is { } frozenSettings &&
                WorkerExecutionSnapshot.Read(frozenSettings).Compactor is null)
                throw new WorkflowConflictException("The explicit compaction request was not bound into this run. Retry the turn against current service state.");
            if (legacy.ActiveJobCount() + Store.CountActive() >= configuration.Get().QueueCapacity)
                throw new InvalidOperationException("Queue is full. Finish or cancel existing assignments.");
            var definition = Registry.GetDefinition(input.DefinitionId, input.DefinitionVersion);
            var snapshot = captured ?? CerebralJson.Element(new
            {
                profile.Id, profile.Revision, profile.Name, profile.Role, profile.Instructions,
                profile.ModelId, profile.WorkerId, profile.ModelWorkerId, profile.ContextTokens, profile.ResponseTokens,
                profile.ReasoningMode, profile.ReasoningTokens, profile.Temperature, profile.MaxSteps, profile.MaxSeconds,
                profile.AllowedRoots, profile.AllowedTools, profile.Capabilities,
                packageBindings = packages.Capture(input.DefinitionId)
            });
            // A data run already carries its exact source snapshot. Never recapture owner grants
            // into that inherited edition, or alter an existing RID's accepted settings above.
            snapshot = WorkerExecutionSnapshot.Bounded(ConversationPathAuthority.Bind(snapshot, pathAdmission.Binding));
            var submission = new WorkflowSubmission(input.RequestId, input.ConversationId, input.ProfileId,
                input.DefinitionId, input.DefinitionVersion, input.UserText, input.AssignmentText,
                input.Input, snapshot, input.StepLimit, caller)
                { AcceptedContent = input.AcceptedContent, ActiveTimeLimitSeconds = profile.MaxSeconds,
                    PolicyStepLimit = PolicyStepLimitFor(input.DefinitionId, profile.MaxSteps),
                    FailureLimits = new Dictionary<string, int>(StringComparer.Ordinal)
                    { ["cerebral.host:launch"] = configuration.Get().MaxExecutableAttemptsPerAssignment },
                    ConversationCompactionRequestId = pendingCompaction?.RequestId,
                     ProjectId = resolvedProjectId, ParentRunId = parentRunId, AuditPolicy = ConfiguredAuditPolicy(profile),
                     LocalGeneration = input.LocalGeneration };
            submission = BindDataWorkflowLimits(submission);
            if (sourceBound is not null) RequireSourceBoundPublicProjection(submission, sourceBound);
            if (snapshot.TryGetProperty("resolvedSettingsVersion", out _)) contextContributions.RecheckBeforeAccept(submission);
            if (!HasCapabilities(submission, definition.Descriptor.RequiredCapabilities))
                throw new UnauthorizedAccessException("The selected profile lacks required workflow capabilities.");
            WorkflowSchema.Validate(input.Input, definition.Descriptor.InputSchema);
            if (WorkflowAcceptedContent.RequiresReferenceAdmission(submission.AcceptedContent))
            {
                if (acceptedContentAdmission is null)
                    throw new InvalidOperationException("Attachment and resource input requires a configured host reference resolver. No workflow was submitted.");
                // Caller/profile/conversation were resolved above. A reference in caller JSON is never
                // authority to read bytes. The byte resolver must enforce this scope again at execution.
                acceptedContentAdmission(submission);
            }
            if (committedInputDelivery is null) return Publish(SubmitWithIdle(submission, idleAdmission));
            var committed = SubmitWithIdleAndCommittedRootUserInput(submission, idleAdmission);
            committedInputReceipt = committed.FirstRootUserInput;
            return Publish(committed.Run);
        });
        WorkflowRun Create() => AdmitWithIdle(DurableCreate, idleAdmission);
        IReadOnlyList<string> additional = captured is { } capturedSnapshot &&
            capturedSnapshot.TryGetProperty("resolvedSettingsVersion", out _)
                ? WorkflowPortablePackageRoots(WorkerExecutionSnapshot.Read(capturedSnapshot)) : [];
        // Include optional roots in the same lifecycle critical section as the durable winning commit.
        var accepted = packageAdmissionWithContributions is not null
            ? packageAdmissionWithContributions(input.DefinitionId, input.RequestId, additional, Create)
            : additional.Count != 0
                ? throw new InvalidOperationException("Context contribution admission requires the extended package lifecycle gate.")
                : packageAdmission is null ? Create() : packageAdmission(input.DefinitionId, input.RequestId, Create);
        // A concurrent winner owns this immutable snapshot. Never bind the losing capture to it.
        if (accepted.Submission.SettingsSnapshot.TryGetProperty("resolvedSettingsVersion", out _))
            _ = WorkerExecutionSnapshot.Read(accepted.Submission.SettingsSnapshot);
        // The winning journal transaction and every outer package/legacy/idle gate have
        // returned. Optional delivery never participates in the accepted Send result.
        if (committedInputReceipt is { } receipt)
        {
            try { _ = committedInputDelivery!.TryEnqueue(receipt); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
        }
        return accepted;
    }

    internal static int PolicyStepLimitFor(string definitionId, int maximumAgentActions)
    {
        if (maximumAgentActions < 1) throw new ArgumentOutOfRangeException(nameof(maximumAgentActions));
        // A model action in the built-in conversation and Programming loops is durably linearized through
        // several engine transitions: model preparation, effect execution, observation and the following
        // decision. AgentProfile.MaxSteps is an action budget, while WorkflowRun.Steps counts those lower-level
        // transitions. Preserve the profile's intended action allowance without changing the step semantics of
        // deterministic or third-party workflows whose limits are already expressed in engine transitions.
        if (definitionId == ConversationWorkflowModule.DefinitionId ||
            definitionId == "pegasus.programming:develop")
            return Math.Min(WorkflowContract.MaximumSteps, checked(maximumAgentActions * 4));
        return Math.Min(WorkflowContract.MaximumSteps, maximumAgentActions);
    }

    internal CoordinationWorkflowBinding ResolveHelperWorkflow(AgentProfile profile, string? parentProjectId)
    {
        var binding = CoordinationWorkflowBinding.Read(profile, parentProjectId);
        var definition = Registry.GetDefinition(binding.DefinitionId, binding.DefinitionVersion);
        WorkflowSchema.Validate(binding.Input, definition.Descriptor.InputSchema);
        if (!definition.Descriptor.RequiredCapabilities.All(capability =>
            capability is not ("agents.delegate" or "agents.collaborate") &&
            profile.Capabilities.Contains(capability, StringComparer.Ordinal)))
            throw new UnauthorizedAccessException("The selected helper lacks required workflow capabilities.");
        // Preflight every selected package before the first child. Admission captures the
        // exact current package graph again, so a concurrent unload cannot broaden authority.
        packages.Capture(binding.DefinitionId);
        return binding;
    }

    private string? ResolveProjectId(SubmitWorkflowRequest input, string? parentRunId,
        AgentProfile? actor, EventActor? callerOverride)
    {
        string? selected = input.DefinitionId == WorkflowDataModule.DefinitionId ? DataProjectId(input) : null;
        if (input.DefinitionId == ConversationWorkflowModule.DefinitionId &&
            input.Input.TryGetProperty("projectId", out var project) && project.ValueKind != JsonValueKind.Null)
        {
            if (project.ValueKind != JsonValueKind.String || project.GetString() is not { } id)
                throw new ArgumentException("Conversation projectId must be a string.");
            Configuration.ValidateId(id);
            selected = id;
        }
        if (input.OwnerProjectId is { } ownerProjectId)
        {
            if (actor is not null || callerOverride is not null || parentRunId is not null)
                throw new UnauthorizedAccessException("Only a direct owner submission may select its project.");
            Configuration.ValidateId(ownerProjectId);
            var nodes = (legacy.Get<NavigationState>("navigation", "sidebar") ?? new()).Nodes;
            var projects = nodes.Where(node => node.Id == ownerProjectId && node.Kind == "project").Take(2).ToArray();
            if (projects.Length != 1 || projects[0].Path is not { } path || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("The owner project is not a registered project with a fully qualified directory.");
            selected = ownerProjectId;
        }
        if (parentRunId is null) return selected;
        var inherited = Store.Get(parentRunId).Submission.ProjectId;
        if (selected is not null && selected != inherited)
            throw new UnauthorizedAccessException("A child workflow cannot substitute the parent project scope.");
        // Project identity is host metadata, independent of any plugin's input schema.
        return inherited;
    }

    private (string? ProjectId, WorkflowSubmission? Original) ResolveChatPermissionProject(
        SubmitWorkflowRequest input, AgentProfile? actor, EventActor? callerOverride,
        string? parentRunId, SourceBoundWorkflowBinding? sourceBound)
    {
        var selected = sourceBound is null ? ResolveProjectId(input, parentRunId, actor, callerOverride)
            : sourceBound.Source.SourceProjectId;
        if (input.DefinitionId != ConversationWorkflowModule.DefinitionId || parentRunId is not null)
            return (selected, null);
        var original = Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId,
            input.ConversationId);
        var current = Store.GetConversation(ConversationWorkflowModule.DefinitionId, input.ConversationId);
        if (original is not null && (original.ProfileId != input.ProfileId ||
            original.ProjectId != current?.ProjectId) ||
            current is not null && current.ProfileId != input.ProfileId)
            throw new UnauthorizedAccessException("The saved chat owner changed before permission capture.");
        if (selected is not null && current?.ProjectId is { } bound && selected != bound)
            throw new WorkflowConflictException("The conversation project changed before permission capture.");
        return (current?.ProjectId ?? selected, original);
    }

    public bool HasCapabilities(WorkflowSubmission submission, IReadOnlyList<string> required)
    {
        var current = legacy.Get<AgentProfile>("agent", submission.ProfileId);
        if (current is null || current.WorkerId != "local" ||
            !submission.SettingsSnapshot.TryGetProperty("capabilities", out var saved) || saved.ValueKind != JsonValueKind.Array)
            return false;
        var snapshot = saved.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
        return required.All(x => snapshot.Contains(x) && current.Capabilities.Contains(x, StringComparer.Ordinal));
    }

    public WorkflowRun GetOwned(string id, AgentProfile? actor)
    {
        var run = Store.Get(id);
        if (actor is not null && run.Submission.ProfileId != actor.Id)
            throw new UnauthorizedAccessException("This profile does not own the workflow.");
        return run;
    }

    public WorkflowReceiptPage GetReceiptOwned(WorkflowReceiptRequest input, AgentProfile? actor)
    {
        ArgumentNullException.ThrowIfNull(input);
        GetOwned(input.RunId, actor);
        return WorkflowReceiptReader.Read(Store, input);
    }

    public WorkflowRun Answer(WorkflowAnswerRequest input, AgentProfile? actor)
    {
        // Agent administration is not a human decision. Owner answers use the authenticated owner route.
        if (actor is not null) throw new UnauthorizedAccessException("Workflow questions require an owner answer.");
        var receipt = Store.Answer(input);
        Publish(Store.Get(input.RunId));
        return receipt;
    }

    public WorkflowSteeringReceipt Steer(WorkflowSteeringRequest input, AgentProfile? actor)
    {
        if (actor is not null) throw new UnauthorizedAccessException("Steering requires the owner interface.");
        ArgumentNullException.ThrowIfNull(input);
        var run = Store.Get(input.RunId);
        // Receipt retries remain available after completion or package removal.
        if (!run.Steering.Any(entry => entry.RequestId == input.RequestId) &&
            !Registry.GetDefinition(run.Submission.DefinitionId, run.Submission.DefinitionVersion).Descriptor.SupportsSteering)
            throw new InvalidOperationException("This workflow does not accept steering.");
        var receipt = Store.Steer(input);
        Publish(Store.Get(input.RunId));
        return receipt;
    }

    public ConversationRenameResult RenameConversation(ConversationRenameRequest input, AgentProfile? actor)
    {
        if (actor is not null) throw new UnauthorizedAccessException("Renaming conversations requires the owner interface.");
        ArgumentNullException.ThrowIfNull(input);
        if (input.DefinitionId != ConversationWorkflowModule.DefinitionId)
            throw new ArgumentException("This command renames ordinary conversations only.");
        var result = Store.RenameConversation(input);
        events.Publish("conversation.updated", result.ConversationId, result);
        return result;
    }

    public ConversationCompactionReceipt RequestConversationCompaction(ConversationCompactionRequest input, AgentProfile? actor)
    {
        if (actor is not null) throw new UnauthorizedAccessException("Conversation compaction requires the owner interface.");
        ArgumentNullException.ThrowIfNull(input);
        if (input.DefinitionId != ConversationWorkflowModule.DefinitionId)
            throw new ArgumentException("This command compacts ordinary conversations only.");
        if (Store.GetConversation(ConversationWorkflowModule.DefinitionId, input.ConversationId) is null)
            throw new KeyNotFoundException("Conversation was not found.");
        var result = Store.RequestConversationCompaction(input);
        events.Publish("conversation.updated", result.ConversationId, result);
        return result;
    }

    // Progress streams are wake-ups. Full durable data is read through bounded command pages.
    internal void NotifyWorkflowUpdated(WorkflowRun run) =>
        events.Publish("workflow.updated", run.Id, new { run.Id, run.Revision, run.Status, run.WaitingReason,
            conversationId = run.Submission.ConversationId, definitionId = run.Submission.DefinitionId });

    public WorkflowRun Publish(WorkflowRun run)
    {
        NotifyWorkflowUpdated(run);
        // Isolated advisory work has no continuity consent, conversation context or compactor.
        if (run.Submission.IsolatedText is not null) return run;
        // Only queue bounded local continuity intent here. External provider I/O runs separately
        // after the workflow commit and must never make an accepted run appear rejected.
        try { NativeContinuity?.Observe(run); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            events.Publish("cortex.continuity.updated", run.Id, new
            {
                conversationId = run.Submission.ConversationId, status = "unavailable",
                message = "The native Cortex checkpoint could not be queued (" + error.GetType().Name +
                    "). Cerebral's committed workflow remains saved."
            });
        }
        QueueIdleCompaction(run);
        return run;
    }

    public object Discover(AgentProfile? actor, IReadOnlyList<OperationDescriptor>? additionalOperations = null) => new
    {
        contract = WorkflowContract.Id, version = WorkflowContract.Version,
        operations = OperationDescriptors.Concat(additionalOperations ?? []).Where(x => actor is null ||
                !x.OwnerOnly && x.RequiredCapabilities.All(actor.Capabilities.Contains))
            .Select(x => new { x.Id, x.Version, x.Description }).ToArray(),
        workflows = Registry.Definitions.Where(x => actor is null || x.RequiredCapabilities.All(actor.Capabilities.Contains))
            .Select(x => new { x.Id, x.Version, x.Description }).ToArray(),
        commands = Registry.Commands.Where(x => actor is null || x.RequiredCapabilities.All(actor.Capabilities.Contains))
            .Select(x => new { x.Id, x.Version, x.Description }).ToArray()
    };

    public object Describe(string id, AgentProfile? actor, IReadOnlyList<OperationDescriptor>? additionalOperations = null)
    {
        if (OperationDescriptors.Concat(additionalOperations ?? []).FirstOrDefault(x => x.Id == id &&
                (actor is null || !x.OwnerOnly && x.RequiredCapabilities.All(actor.Capabilities.Contains))) is { } operation)
            return operation;
        var definitions = Registry.Definitions.Where(x => x.Id == id && (actor is null || x.RequiredCapabilities.All(actor.Capabilities.Contains))).ToArray();
        var commands = Registry.Commands.Where(x => x.Id == id && (actor is null || x.RequiredCapabilities.All(actor.Capabilities.Contains))).ToArray();
        var contextProviders = Registry.ContextProviders.Where(x => x.Id == id && commands.Any(command =>
            command.Id == x.Id && command.Version == x.Version)).ToArray();
        if (definitions.Length == 0 && commands.Length == 0) throw new ArgumentException("No available workflow contribution has that ID.");
        return new { contract = WorkflowContract.Id, version = WorkflowContract.Version, definitions, commands, contextProviders };
    }

    public sealed record OperationDescriptor(string Id, int Version, string Description, JsonElement InputSchema, bool OwnerOnly = false)
    {
        public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    }

    public static IReadOnlyList<OperationDescriptor> OperationDescriptors { get; } = BuildOperations();
    public static IReadOnlyList<string> Operations { get; } = Array.AsReadOnly(OperationDescriptors.Select(x => x.Id).ToArray());

    private static IReadOnlyList<OperationDescriptor> BuildOperations()
    {
        static JsonElement Schema(string fields, string required)
        {
            using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{" + fields + "},\"required\":[" + required + "],\"additionalProperties\":false}");
            return document.RootElement.Clone();
        }
        const string id = "\"id\":{\"type\":\"string\"}";
        var items = new[]
        {
            new OperationDescriptor("sdk.discover", 1, "List concise IDs, versions and descriptions. Use sdk.describe for a selected schema.", Schema("", "")),
            new OperationDescriptor("sdk.describe", 1, "Describe a registered contribution or transport operation by exact ID.", Schema(id, "\"id\"")),
            new OperationDescriptor("jobs.workflow.submit", 1, "Accept durable work once using a stable request ID. Caller identity and grants are set by the service.", Schema($$$$"""
                "requestId":{"type":"string"},"conversationId":{"type":"string"},"profileId":{"type":"string"},
                "definitionId":{"type":"string"},"definitionVersion":{"type":"string"},"userText":{"type":"string"},
                "assignmentText":{"type":["string","null"]},"acceptedContent":{"type":"array","maxItems":16,"items":{"type":"object"}},
                "input":{"type":"object"},"stepLimit":{"type":"integer","minimum":1,"maximum":{{{{WorkflowContract.MaximumSteps}}}},"default":64},
                "ownerProjectId":{"type":"string","maxLength":128},
                "localGeneration":{"type":"object","properties":{"schemaVersion":{"const":1},"route":{"const":"local.native.text"},"contextTokens":{"type":"integer","minimum":512,"maximum":{{{{LlamaPreparedGeneration.NativeMaximumContextTokens}}}}},"visibleOutputTokens":{"type":"integer","minimum":1,"maximum":{{{{LlamaPreparedGeneration.NativeMaximumVisibleTokens}}}}}},"required":["schemaVersion","route","contextTokens","visibleOutputTokens"],"additionalProperties":false},
                "ownerVerification":{"type":"object","properties":{
                  "schemaVersion":{"const":2},"recipeId":{"type":"string"},"requestId":{"type":"string"},
                  "conversationId":{"type":"string"},"profileId":{"type":"string"},"definitionId":{"type":"string"},
                  "definitionVersion":{"type":"string"},"executableId":{"type":"string"},"executablePath":{"type":"string"},
                  "prefixArguments":{"type":"array","maxItems":0},"modelArguments":{"type":"array","minItems":1,"maxItems":1,"items":{"type":"string"}},
                  "workingDirectory":{"type":"string"},"checkerRoot":{"type":"string"},"targetRoot":{"type":"string"},
                  "files":{"type":"array","minItems":1,"maxItems":512,"items":{"type":"object","properties":{
                    "role":{"type":"string"},"scope":{"type":"string","enum":["checker","target"]},
                    "relativePath":{"type":"string"},"originalPath":{"type":"string"},"sha256":{"type":"string"},"bytes":{"type":"integer"}},
                    "required":["role","scope","relativePath","originalPath"],"additionalProperties":false}},
                  "checkerContract":{"type":"object","properties":{
                    "descriptorSchema":{"type":"string","minLength":1,"maxLength":256},
                    "closureSchema":{"type":"string","minLength":1,"maxLength":256},
                    "checkerVersion":{"type":"string","minLength":1,"maxLength":64},
                    "summarySchema":{"type":"string","minLength":1,"maxLength":256},
                    "reportSchema":{"type":"string","minLength":1,"maxLength":256},
                    "entryRole":{"type":"string","minLength":1,"maxLength":64},
                    "loadedRoles":{"type":"array","minItems":1,"maxItems":512,"items":{"type":"string","minLength":1,"maxLength":64}},
                    "assertionNames":{"type":"array","minItems":1,"items":{"type":"string","minLength":1,"maxLength":128}}},
                    "required":["descriptorSchema","closureSchema","checkerVersion","summarySchema","reportSchema","entryRole","loadedRoles","assertionNames"],
                    "additionalProperties":false},
                  "maximumFailuresPerInput":{"const":2}},
                  "required":["schemaVersion","recipeId","requestId","conversationId","profileId","definitionId","definitionVersion",
                    "executableId","executablePath","prefixArguments","modelArguments","workingDirectory","checkerRoot","targetRoot","files","checkerContract","maximumFailuresPerInput"],
                  "additionalProperties":false}
                """, "\"requestId\",\"conversationId\",\"profileId\",\"definitionId\",\"definitionVersion\",\"userText\",\"input\"")),
            new OperationDescriptor("jobs.workflow.get", 1, "Read current saved work before retrying or reconnecting.", Schema(id, "\"id\"")),
            new OperationDescriptor("jobs.workflow.receipt", 1, "Read one same-run effect result as bounded compact JSON fragments. Offsets and limits are UTF-8 bytes excluding transport metadata. Continue Unknown results with expectedRevision; restart at zero on conflict. Confirmed results are immutable.", Schema("""
                "runId":{"type":"string"},"effectId":{"type":"string"},"offset":{"type":"integer","minimum":0,"default":0},
                "limit":{"type":"integer","minimum":4,"maximum":16384,"default":4096},"expectedRevision":{"oneOf":[{"type":"integer","minimum":1},{"type":"null"}]}
                """, "\"runId\",\"effectId\"")),
            new OperationDescriptor("jobs.workflow.active", 1, "Page bounded active work for the installation owner; continue with nextAfterRow. Active status can change between pages.", Schema("\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"default\":100},\"afterRow\":{\"type\":\"integer\",\"minimum\":0,\"default\":0}", ""), true),
            new OperationDescriptor("jobs.workflow.active-summaries", 1, "Page compact active workflow rows without assignments or model context. Continue with nextAfterRow; statuses remain live.", Schema("""
                "limit":{"type":"integer","minimum":1,"maximum":200,"default":40},"afterRow":{"type":"integer","minimum":0,"default":0}
                """, ""), true),
            new OperationDescriptor("queue.active-summaries", 1, "Page compact active legacy job rows without private prompts or tool results. Continue with nextAfterRow; statuses remain live.", Schema("""
                "limit":{"type":"integer","minimum":1,"maximum":200,"default":40},"afterRow":{"type":"integer","minimum":0,"default":0}
                """, ""), true),
            new OperationDescriptor("conversations.goal", 1, "Display the original accepted user request and up to eight latest later messages/corrections. Exact event IDs preserve provenance; this does not change instructions or execute work.", Schema("\"id\":{\"type\":\"string\",\"maxLength\":128}", "\"id\""), true),
            new OperationDescriptor("conversations.get", 1, "Read the compact summary and latest run of one ordinary conversation.", Schema("\"id\":{\"type\":\"string\",\"maxLength\":128}", "\"id\""), true),
            new OperationDescriptor("conversations.workflow.get", 1, "Resolve one saved owner-root non-chat conversation from its complete accepted run and workflow edition.", Schema("""
                "conversationId":{"type":"string","maxLength":128},"acceptedRunId":{"type":"string","maxLength":128},
                "definitionId":{"type":"string","maxLength":256},"definitionVersion":{"type":"string","maxLength":100}
                """, "\"conversationId\",\"acceptedRunId\",\"definitionId\",\"definitionVersion\""), true),
            new OperationDescriptor("conversations.workflow.recent", 1, "Page bounded non-chat owner-root conversation summaries. generalOnly excludes saved projects and protected pins.", Schema("""
                "limit":{"type":"integer","minimum":1,"maximum":200},"beforeRow":{"type":"integer","minimum":1},
                "snapshotRow":{"type":"integer","minimum":0},"generalOnly":{"type":"boolean"},
                "conversationId":{"type":"string","maxLength":128}
                """, ""), true),
            new OperationDescriptor("conversations.workflow.protected", 1, "Page saved project-assigned and pinned non-chat owner-root conversations.", Schema("""
                "limit":{"type":"integer","minimum":1,"maximum":200},"beforeRow":{"type":"integer","minimum":1},
                "snapshotRow":{"type":"integer","minimum":0}
                """, ""), true),
            new OperationDescriptor("conversations.recent", 1, "Page compact conversation summaries. generalOnly excludes project chats, saved pins and project-grouped chats for the recent sidebar. Reuse snapshotRow and nextBeforeRow; full history remains saved.", Schema("\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"beforeRow\":{\"type\":\"integer\",\"minimum\":1},\"snapshotRow\":{\"type\":\"integer\",\"minimum\":0},\"generalOnly\":{\"type\":\"boolean\"}", ""), true),
            new OperationDescriptor("conversations.protected", 1, "Page compact project-assigned and pinned conversation summaries for the owner. Pins and project grouping come from saved service navigation; no client-supplied IDs. Continue with snapshotRow and nextBeforeRow.", Schema("""
                "limit":{"type":"integer","minimum":1,"maximum":200},"beforeRow":{"type":"integer","minimum":1},"snapshotRow":{"type":"integer","minimum":0}
                """, ""), true),
            new OperationDescriptor("jobs.workflow.recent", 1, "Read bounded recent run summaries without model context or effect payloads. Continue using nextBeforeRow and snapshotRow.", Schema("\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"beforeRow\":{\"type\":\"integer\",\"minimum\":1},\"snapshotRow\":{\"type\":\"integer\",\"minimum\":0}", ""), true),
            new OperationDescriptor("jobs.workflow.events", 1, "Read a bounded conversation event page. Continue with nextAfterSequence and the same snapshotSequence.", Schema(id + ",\"afterSequence\":{\"type\":\"integer\",\"minimum\":0},\"snapshotSequence\":{\"type\":\"integer\",\"minimum\":0},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}", "\"id\"")),
            new OperationDescriptor("jobs.workflow.events.previous", 1, "Read recent conversation events in chronological order. Continue toward older history with nextBeforeSequence and the same snapshotSequence.", Schema(id + ",\"beforeSequence\":{\"type\":\"integer\",\"minimum\":1},\"snapshotSequence\":{\"type\":\"integer\",\"minimum\":0},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}", "\"id\"")),
            new OperationDescriptor("jobs.workflow.answer", 1, "Submit owner answers to exact question fields. Reuse requestId only for an identical retry.", Schema("""
                "requestId":{"type":"string"},"runId":{"type":"string"},"questionSetId":{"type":"string"},
                "expectedQuestionRevision":{"type":"integer","minimum":1},"answers":{"type":"object","additionalProperties":{"type":"string"}}
                """, "\"requestId\",\"runId\",\"questionSetId\",\"expectedQuestionRevision\",\"answers\""), true),
            new OperationDescriptor("jobs.workflow.steer", 1, "Save an owner correction for the next safe model step. Acceptance does not confirm execution. Reuse requestId only for an identical retry; limits and pending questions remain in force.", Schema("""
                "requestId":{"type":"string","maxLength":128},"runId":{"type":"string","maxLength":128},
                "text":{"type":"string","minLength":1,"maxLength":8192},"assignmentText":{"type":["string","null"],"maxLength":8192}
                """, "\"requestId\",\"runId\",\"text\""), true),
            new OperationDescriptor("conversations.rename", 1, "Rename a conversation without changing agent context. Compare expectedTitleRevision and reuse requestId only for an identical retry. Titles allow 160 Unicode scalars and 640 UTF-8 bytes.", Schema("""
                "requestId":{"type":"string","maxLength":128},"definitionId":{"type":"string","maxLength":128},
                "conversationId":{"type":"string","maxLength":128},"expectedTitleRevision":{"type":"integer","minimum":0},
                "title":{"type":"string","minLength":1,"maxLength":160}
                """, "\"requestId\",\"definitionId\",\"conversationId\",\"expectedTitleRevision\",\"title\""), true),
            new OperationDescriptor("conversations.compact", 1, "Request one measured compaction before the next ordinary Chat turn. The request is retained by the service, is not a user message, and is consumed atomically when that turn is accepted. Reuse requestId only for an identical retry.", Schema("""
                "requestId":{"type":"string","maxLength":128},"definitionId":{"type":"string","maxLength":256},
                "conversationId":{"type":"string","maxLength":128},"expectedConversationRevision":{"type":"integer","minimum":0}
                """, "\"requestId\",\"definitionId\",\"conversationId\",\"expectedConversationRevision\""), true),
            new OperationDescriptor("jobs.workflow.abandon.inspect", 1,
                "Owner-only metadata: inspect exact inactive cancelled-Unknown abandonment coordinates without arguments or model context.",
                Schema(id, "\"id\""), true),
            new OperationDescriptor("jobs.workflow.abandon", 1,
                "Owner-only: abandon one exact cancelled inactive Unknown workflow without settling, retrying or changing its effect receipt.",
                Schema("\"requestId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"runId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"expectedRevision\":{\"type\":\"integer\",\"minimum\":1}," +
                    "\"effectId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"effectSha256\":{\"type\":\"string\",\"pattern\":\"^[a-f0-9]{64}$\"}," +
                    "\"reason\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":3072}",
                    "\"requestId\",\"runId\",\"expectedRevision\",\"effectId\",\"effectSha256\",\"reason\""), true),
            new OperationDescriptor("jobs.workflow.cancel", 1, "Persist cancellation and stop live execution. An uncertain external effect still needs reconciliation.", Schema(id, "\"id\"")),
            new OperationDescriptor("jobs.workflow.resume", 1, "Queue explicitly resumable work at its saved revision; unknown effects cannot be replayed.", Schema(id + ",\"expectedRevision\":{\"type\":\"integer\",\"minimum\":1}", "\"id\",\"expectedRevision\"")),
            new OperationDescriptor("jobs.workflow.reconcile", 1, "Inspect an existing uncertain effect without submitting it again.", Schema(id, "\"id\"")),
            new OperationDescriptor("jobs.workflow.prepared.reject", 1,
                "Owner-only: reject one exact inactive never-dispatched prepared effect, retain its history, and steer the next planning step.",
                Schema("\"requestId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"runId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"expectedRevision\":{\"type\":\"integer\",\"minimum\":1}," +
                    "\"effectId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"effectSha256\":{\"type\":\"string\",\"pattern\":\"^[a-f0-9]{64}$\"}," +
                    "\"reason\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":3072}," +
                    "\"steeringText\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":8192}",
                    "\"requestId\",\"runId\",\"expectedRevision\",\"effectId\",\"effectSha256\",\"reason\",\"steeringText\""), true),
            new OperationDescriptor("jobs.workflow.prepared.permit-once", 1,
                "Owner-only: permit exactly one reviewed prepared effect to cross its exhausted admitted failure category.",
                Schema("\"requestId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"runId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"expectedRevision\":{\"type\":\"integer\",\"minimum\":1}," +
                    "\"effectId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"effectSha256\":{\"type\":\"string\",\"pattern\":\"^[a-f0-9]{64}$\"}," +
                    "\"reason\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":3072}",
                    "\"requestId\",\"runId\",\"expectedRevision\",\"effectId\",\"effectSha256\",\"reason\""), true),
            new OperationDescriptor("jobs.workflow.receipt-lookup.inspect", 1,
                "Inspect owner-only eligibility and the exact saved tuple for one uncertain file-edit receipt lookup.",
                Schema(id, "\"id\""), true),
            new OperationDescriptor("jobs.workflow.receipt-lookup.confirm", 1,
                "Inspect one original provider job by its exact job ID and commit only a confirmed terminal receipt.",
                Schema(id + ",\"expectedRevision\":{\"type\":\"integer\",\"minimum\":1}," +
                    "\"effectId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," +
                    "\"effectSha256\":{\"type\":\"string\",\"pattern\":\"^[a-f0-9]{64}$\"}," +
                    "\"providerId\":{\"type\":\"string\",\"minLength\":5,\"maxLength\":256}," +
                    "\"providerFingerprint\":{\"type\":\"string\",\"pattern\":\"^[a-f0-9]{64}$\"}," +
                    "\"jobIdHint\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256}",
                    "\"id\",\"expectedRevision\",\"effectId\",\"effectSha256\",\"providerId\",\"providerFingerprint\",\"jobIdHint\""), true)
        };
        return Array.AsReadOnly(items.Concat(DataOperationDescriptors).ToArray());
    }
}
