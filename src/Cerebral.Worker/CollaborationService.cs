using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;

namespace Cerebral.Worker;

public interface ICollaborationWorkflowGateway
{
    ConversationSummary GetConversation(string conversationId);
    WorkflowRun? TryGet(string runId);
    WorkflowQuestionSet ReadSourceOwnerAnswer(string runId, string questionSetId, long questionRevision);
    Task<WorkflowRun> SubmitAsync(CollaborationTask task, CancellationToken cancellationToken);
    WorkflowRun Answer(CollaborationTask task, CollaborationAnswerRequest request);
    WorkflowRun Steer(CollaborationTask task, CollaborationSteerRequest request);
    WorkflowRun Cancel(CollaborationTask task);
    Task<WorkflowRun> ReconcileAsync(CollaborationTask task, CancellationToken cancellationToken);
}

public interface ICollaborationViewGateway
{
    IReadOnlyList<string> ChildRunIdsForView(string parentRunId);
}

internal sealed record MainOwnedMemberView(string ContextId, string ConversationId, string Role, string DisplayName);
internal sealed record MainOwnedMembersView(int SchemaVersion, string MainConversationId, long RootRevision,
    bool AdmissionClosed, IReadOnlyList<MainOwnedMemberView> Members);
internal sealed record MainActivityCursor(string MainConversationId, string ContextId, long RootRevision,
    long AfterSequence, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SnapshotSequence);
internal sealed record MainOwnedActivityPage(int SchemaVersion, string MainConversationId, string ContextId,
    long RootRevision, IReadOnlyList<CerebralEvent> Events, long SnapshotSequence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JournalRetentionGap? RetentionGap,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MainActivityCursor? NextCursor, int ScannedEvents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MainActivityCursor? TailCursor);

internal interface IMainOwnedCollaborationGateway
{
    ConversationSummary PrepareCoordinator(WorkflowSubmission source);
    bool IsOwnedCoordinator(string mainId, string conversationId);
    bool IsOwnedWorker(string mainId, WorkflowSubmission submission);
    object ReadMembers(string mainId);
    object ReadActivity(JsonElement arguments, CancellationToken cancellationToken);
}

public interface ICollaborationCommunicationGate
{
    void RequireAllowed(string sourceConversationId, string targetConversationId);
}

internal sealed class StoreCollaborationCommunicationGate(ConversationCommunicationStore store)
    : ICollaborationCommunicationGate
{
    public void RequireAllowed(string sourceConversationId, string targetConversationId)
    {
        if (!store.Get(sourceConversationId, targetConversationId).Allowed)
            throw new UnauthorizedAccessException("Communication between these conversations is not currently allowed.");
    }
}

internal sealed class WorkerCollaborationWorkflowGateway(
    WorkerWorkflows workflows,
    JobScheduler scheduler,
    CollaborationStore collaboration) : ICollaborationWorkflowGateway, ICollaborationViewGateway, IMainOwnedCollaborationGateway
{
    public ConversationSummary GetConversation(string conversationId) =>
        workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, conversationId) ??
        throw new KeyNotFoundException("Collaboration conversation was not found.");

    private ConversationSummary Main(string id)
    {
        var source = GetConversation(id);
        var original = workflows.Store.ReadOriginalConversationSubmission(ConversationWorkflowModule.DefinitionId, id);
        if (original is null || !ConversationRolePolicy.IsMain(original) ||
            original.ProfileId != source.ProfileId || original.ProjectId != source.ProjectId ||
            workflows.OwnedContexts.Owner(id) is not null)
            throw new UnauthorizedAccessException("An exact owner Main conversation is required.");
        return source;
    }

    public ConversationSummary PrepareCoordinator(WorkflowSubmission source)
    {
        if (!ConversationRolePolicy.IsMain(source))
            throw new UnauthorizedAccessException("Only the accepted Main workflow can create its coordinator context.");
        var main = Main(source.ConversationId);
        var root = workflows.OwnedContexts.Read(main.Id, main.ProfileId, main.ProjectId);
        var existing = root.Members.SingleOrDefault(x => x.Role == "coordinator");
        var member = workflows.OwnedContexts.Reserve(main.Id, main.ProfileId, main.ProjectId,
            main.ProfileId, ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
            "coordinator", "Coordinator", existing?.ContextId);
        // Called only after host reservation; Engine separately proves a fresh native owned target.
        collaboration.RegisterOwnedCoordinator(source.RequestId, member.ConversationId, member.ProfileId);
        return workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, member.ConversationId) ??
            new(member.ConversationId, "Coordinator", member.ProfileId, member.ProjectId, "",
                WorkflowStatus.Queued, DateTimeOffset.UtcNow);
    }

    public bool IsOwnedCoordinator(string mainId, string conversationId)
    {
        var member = workflows.OwnedContexts.Owner(conversationId);
        if (member is null || member.MainConversationId != mainId || member.Role != "coordinator") return false;
        var main = Main(mainId);
        return workflows.OwnedContexts.Require(mainId, main.ProfileId, main.ProjectId, member.ContextId) == member;
    }

    public bool IsOwnedWorker(string mainId, WorkflowSubmission submission)
    {
        var member = workflows.OwnedContexts.Owner(submission.ConversationId);
        if (member is null || member.MainConversationId != mainId || member.Role != "worker" ||
            member.ProfileId != submission.ProfileId || member.ProjectId != submission.ProjectId ||
            member.DefinitionId != submission.DefinitionId || member.DefinitionVersion != submission.DefinitionVersion)
            return false;
        var main = Main(mainId);
        return workflows.OwnedContexts.Require(mainId, main.ProfileId, main.ProjectId, member.ContextId) == member;
    }

    public object ReadMembers(string mainId)
    {
        var main = Main(mainId);
        var root = workflows.OwnedContexts.Read(main.Id, main.ProfileId, main.ProjectId);
        return new MainOwnedMembersView(1, main.Id, root.Revision, root.AdmissionClosed,
            new[] { new MainOwnedMemberView(main.Id, main.Id, "main", "Main") }
                .Concat(root.Members.Select(x => new MainOwnedMemberView(x.ContextId, x.ConversationId,
                    x.Role, x.DisplayName))).ToArray());
    }

    public object ReadActivity(JsonElement arguments, CancellationToken cancellationToken)
    {
        var names = arguments.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length is < 2 or > 4 || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Except(["mainConversationId", "contextId", "cursor", "limit"], StringComparer.Ordinal).Any())
            throw new ArgumentException("Activity requires an exact Main and context handle with an optional bound cursor.");
        var mainId = arguments.GetProperty("mainConversationId").GetString()!;
        var contextId = arguments.GetProperty("contextId").GetString()!;
        var main = Main(mainId);
        var root = workflows.OwnedContexts.Read(main.Id, main.ProfileId, main.ProjectId);
        var member = contextId == main.Id ? null : root.Members.SingleOrDefault(x => x.ContextId == contextId) ??
            throw new UnauthorizedAccessException("The activity filter is not a member of this Main.");
        var conversationId = member?.ConversationId ?? main.Id;
        if (member is not null && workflows.OwnedContexts.Owner(conversationId) != member)
            throw new UnauthorizedAccessException("The context ownership binding changed.");
        long after = 0; long? snapshot = null;
        if (arguments.TryGetProperty("cursor", out var cursor) && cursor.ValueKind != JsonValueKind.Null)
        {
            string[] expected = ["mainConversationId", "contextId", "rootRevision", "afterSequence", "snapshotSequence"];
            var cursorNames = cursor.EnumerateObject().Select(x => x.Name).ToArray();
            if (cursorNames.Length != expected.Length ||
                cursorNames.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
                cursorNames.Except(expected, StringComparer.Ordinal).Any() ||
                cursor.GetProperty("mainConversationId").GetString() != mainId ||
                cursor.GetProperty("contextId").GetString() != contextId ||
                cursor.GetProperty("rootRevision").GetInt64() != root.Revision)
                throw new UnauthorizedAccessException("The activity cursor belongs to a different Main, member or ownership generation.");
            after = cursor.GetProperty("afterSequence").GetInt64();
            var selectedSnapshot = cursor.GetProperty("snapshotSequence");
            // Only the explicit server-returned tail shape uses null to capture one new snapshot.
            snapshot = selectedSnapshot.ValueKind == JsonValueKind.Null ? null : selectedSnapshot.GetInt64();
        }
        var limit = arguments.TryGetProperty("limit", out var count) ? count.GetInt32() : 40;
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException("limit");
        var page = workflows.Journal.ReadPage(conversationId, after, limit, snapshot, cancellationToken);
        return new MainOwnedActivityPage(1, mainId, contextId, root.Revision,
            page.Events.Where(item => item.Projection.Human == HumanVisibility.Activity).ToArray(),
            page.SnapshotSequence, page.RetentionGap,
            page.NextAfterSequence is { } next
                ? new(mainId, contextId, root.Revision, next, page.SnapshotSequence) : null,
            page.Events.Count, page.NextAfterSequence is null
                ? new(mainId, contextId, root.Revision, page.SnapshotSequence, null) : null);
    }

    public WorkflowRun? TryGet(string runId) => workflows.Store.TryGet(runId);

    public WorkflowQuestionSet ReadSourceOwnerAnswer(string runId, string questionSetId, long questionRevision) =>
        workflows.Store.ReadCollaborationOwnerAnswer(runId, questionSetId, questionRevision);

    public IReadOnlyList<string> ChildRunIdsForView(string parentRunId) => workflows.Store.ChildRunIdsForView(parentRunId);

    public Task<WorkflowRun> SubmitAsync(CollaborationTask task, CancellationToken cancellationToken)
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (task.TargetProjectId is not null) input["projectId"] = task.TargetProjectId;
        if (task.Request.ResponseSharePercent is { } share) input["responseSharePercent"] = share;
        if (task.SourceRunId is { } sourceFolderRunId)
        {
            var source = workflows.Store.Get(sourceFolderRunId).Submission;
            if (source.ConversationId != task.Request.SourceConversationId || source.ProfileId != task.SourceProfileId ||
                source.ProjectId != task.SourceProjectId)
                throw new UnauthorizedAccessException("The accepted source folder lineage changed.");
            var target = workflows.Store.ReadOriginalConversationSubmission(
                ConversationWorkflowModule.DefinitionId, task.Request.TargetConversationId);
            var directory = CoordinationWorkflowBinding.CollaborationDirectory(source, task.TargetProjectId, target);
            if (directory is not null) input["workingDirectory"] = directory;
        }
        var request = new SubmitWorkflowRequest(
            task.AttemptId,
            task.Request.TargetConversationId,
            task.TargetProfileId,
            ConversationWorkflowModule.DefinitionId,
            ConversationWorkflowModule.DefinitionVersion,
            task.Request.Instruction,
            "Complete the delegated work using only this target conversation's context and admitted capabilities. Return a compact result, typed resource revisions, evidence, and unresolved findings. Do not request or infer the source transcript, hidden prompts, paths, plugin settings, or owner authority.",
            CerebralJson.Element(input),
            task.Request.StepLimit);
        Func<AgentProfile, AgentProfile>? restrict = null;
        if (task.ProjectGrantId is { } grantId)
        {
            var grant = collaboration.GetGrant(grantId);
            if (grant.RevokedAt is not null || grant.Revision != task.ProjectGrantRevision ||
                !grant.TargetProfileIds.Contains(task.TargetProfileId, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("Cross-project collaboration grant changed before dispatch.");
            // The first checkpoint transfers reasoning and bounded results only. Cross-project effects stay
            // disabled until typed resource handlers can recheck the grant before every dispatch.
            restrict = profile =>
            {
                profile.AllowedTools.Clear();
                profile.AllowedRoots.Clear();
                profile.Capabilities.Clear();
                return profile;
            };
        }
        if (IsOwnedCoordinator(task.Request.SourceConversationId, task.Request.TargetConversationId))
        {
            var sourceRunId = task.SourceRunId ?? throw new UnauthorizedAccessException("An owned coordinator requires its exact source run.");
            var frozen = new CoordinatorSourceBinding(task.AttemptId, sourceRunId, task.Request.SourceConversationId);
            workflows.BindCoordinatorSource(frozen);
            var originalRestrict = restrict;
            restrict = profile => WorkerWorkflows.IntersectCoordinationPolicy(
                originalRestrict is null ? profile : originalRestrict(profile),
                workflows.CoordinationSourcePolicy(sourceRunId));
        }
        return workflows.SubmitServiceAsync(request, "cerebral.collaboration", restrict, cancellationToken);
    }

    public WorkflowRun Answer(CollaborationTask task, CollaborationAnswerRequest request) =>
        workflows.Answer(new WorkflowAnswerRequest(request.RequestId, RequiredRun(task),
            request.QuestionSetId, request.ExpectedQuestionRevision, request.Answers), null);

    public WorkflowRun Steer(CollaborationTask task, CollaborationSteerRequest request)
    {
        workflows.Steer(new WorkflowSteeringRequest(request.RequestId, RequiredRun(task),
            request.Text, request.AssignmentText), null);
        return workflows.Store.Get(RequiredRun(task));
    }

    public WorkflowRun Cancel(CollaborationTask task) => scheduler.CancelWorkflow(RequiredRun(task));

    public async Task<WorkflowRun> ReconcileAsync(CollaborationTask task, CancellationToken cancellationToken)
    {
        var run = workflows.Store.Get(RequiredRun(task));
        return run.Effect?.State == WorkflowEffectState.Unknown
            ? await scheduler.ReconcileWorkflowAsync(run.Id, cancellationToken).ConfigureAwait(false)
            : run;
    }

    private static string RequiredRun(CollaborationTask task) => task.TargetRunId ??
        throw new WorkflowConflictException("Collaboration attempt has not been attached to a target workflow.");
}

/// <summary>Core local one-hop task-envelope service. Plugins may present it but cannot replace its queue or authority checks.</summary>
public sealed class CollaborationService
{
    private readonly CollaborationStore store;
    private readonly ICollaborationWorkflowGateway gateway;
    private readonly ICollaborationCommunicationGate communication;
    private readonly ConversationCommunicationStore? permissions;
    private readonly EventHub events;

    public CollaborationService(CollaborationStore store, ICollaborationWorkflowGateway gateway,
        ICollaborationCommunicationGate communication, EventHub events,
        ConversationCommunicationStore? permissions = null)
    {
        this.store = store;
        this.gateway = gateway;
        this.communication = communication;
        this.permissions = permissions;
        this.events = events;
    }

    internal sealed record CoordinateRequest(string RequestId, string Instruction, int StepLimit = 64,
        int? ResponseSharePercent = null);

    internal bool SupportsMainOwnedActions => gateway is IMainOwnedCollaborationGateway;

    private IMainOwnedCollaborationGateway OwnedGateway => gateway as IMainOwnedCollaborationGateway ??
        throw new InvalidOperationException("This host does not support explicitly owned Main contexts.");

    private void RequireCommunication(string sourceId, string targetId)
    {
        if (gateway is IMainOwnedCollaborationGateway owned && owned.IsOwnedCoordinator(sourceId, targetId)) return;
        communication.RequireAllowed(sourceId, targetId);
    }

    internal async Task<CollaborationTask> CoordinateFromWorkflowAsync(CoordinateRequest request,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var source = SourceToolEffect(context, CollaborationToolExtension.CoordinateTool);
        var saved = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(source.Effect!.Arguments);
        if (!ConversationRolePolicy.IsMain(source.Submission) || context.Profile.Role != "lead" ||
            !context.Profile.Capabilities.Contains("agents.collaborate", StringComparer.Ordinal) ||
            saved.Arguments.Deserialize<CoordinateRequest>(CerebralJson.Options) != request)
            throw new UnauthorizedAccessException("Coordination requires the exact accepted Main handoff effect.");
        return await CoordinateCoreAsync(request, context, source, cancellationToken).ConfigureAwait(false);
    }

    // Intrinsic ownership is admitted from a persisted native host effect, never a synthetic
    // cross-chat capability. Semantic tools retain their separate profile/pair authorization.
    internal WorkflowRun RequireOwnedCommandSource(ToolExecutionContext context, string commandId,
        JsonElement arguments)
    {
        if (commandId is not (WorkerWorkflowEffects.OwnedCoordinateCommand or WorkerWorkflowEffects.OwnedRelayCommand))
            throw new UnauthorizedAccessException("This command is not an owned Main action.");
        var source = SourceEffect(context, commandId);
        if (!ConversationRolePolicy.IsMain(source.Submission) || context.Profile.Role != "lead" ||
            source.Effect!.State != WorkflowEffectState.Dispatched ||
            !JsonElement.DeepEquals(source.Effect.Arguments, arguments))
            throw new UnauthorizedAccessException("The exact dispatched owner Main action is required.");
        return source;
    }

    internal Task<CollaborationTask> CoordinateOwnedFromWorkflowAsync(CoordinateRequest request,
        ToolExecutionContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        var source = RequireOwnedCommandSource(context, WorkerWorkflowEffects.OwnedCoordinateCommand, arguments);
        if (WorkerWorkflows.Read<CoordinateRequest>(source.Effect!.Arguments) != request)
            throw new UnauthorizedAccessException("The saved owned assignment changed.");
        return CoordinateCoreAsync(request, context, source, cancellationToken);
    }

    private Task<CollaborationTask> CoordinateCoreAsync(CoordinateRequest request,
        ToolExecutionContext context, WorkflowRun source, CancellationToken cancellationToken)
    {
        var target = OwnedGateway.PrepareCoordinator(source.Submission);
        var envelope = new CollaborationTaskRequest(request.RequestId, context.SessionId, target.Id,
            request.Instruction, [], [], StepLimit: request.StepLimit,
            ResponseSharePercent: request.ResponseSharePercent);
        return SubmitCoreAsync(envelope, context.Profile, source.Id, cancellationToken, target);
    }

    public static IReadOnlyList<WorkerWorkflows.OperationDescriptor> OperationDescriptors { get; } = BuildOperations();
    public static IReadOnlyList<string> Operations { get; } =
        Array.AsReadOnly(OperationDescriptors.Select(item => item.Id).ToArray());

    public void Authorize(string operation, AgentProfile? actor, JsonElement arguments)
    {
        if (actor is null) return;
        if (operation.StartsWith("collaboration.grants.", StringComparison.Ordinal) ||
            operation.StartsWith("collaboration.permissions.", StringComparison.Ordinal) ||
            operation is "collaboration.list" or "collaboration.recover" or
                "collaboration.view.list" or "collaboration.view.graph" or
                "collaboration.view.members" or "collaboration.view.activity")
            throw new UnauthorizedAccessException("Collaboration grant management and global inspection require the owner interface.");
        if (operation == "collaboration.submit")
        {
            if (!actor.Capabilities.Contains("agents.collaborate", StringComparer.Ordinal))
                throw new UnauthorizedAccessException("This agent profile lacks agents.collaborate.");
            var request = WorkerWorkflows.Read<CollaborationTaskRequest>(arguments);
            var source = gateway.GetConversation(request.SourceConversationId);
            if (source.ProfileId != actor.Id)
                throw new UnauthorizedAccessException("A calling agent can delegate only from its own conversation.");
            if (store.IsTargetConversation(request.SourceConversationId))
                throw new UnauthorizedAccessException("The first collaboration checkpoint is one hop; a target attempt cannot delegate another collaboration task.");
            return;
        }
        var taskId = arguments.TryGetProperty("taskId", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : arguments.TryGetProperty("id", out value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        if (taskId is null) throw new ArgumentException("Collaboration task identity is required.");
        var task = store.Get(taskId);
        if (operation is not "collaboration.cancel")
            RequireCommunication(task.Request.SourceConversationId, task.Request.TargetConversationId);
        if (operation is "collaboration.answer" or "collaboration.steer" or "collaboration.reconcile")
            throw new UnauthorizedAccessException("Collaboration owner decisions and reconciliation require the owner interface.");
        var readOnly = operation is "collaboration.get" or "collaboration.status" or
            "collaboration.events" or "collaboration.result";
        if (task.SourceProfileId == actor.Id || readOnly && task.TargetProfileId == actor.Id) return;
        throw new UnauthorizedAccessException("The calling agent is not a participant in this collaboration task.");
    }

    public async Task<object> ExecuteAsync(string operation, JsonElement arguments, AgentProfile? actor,
        CancellationToken cancellationToken)
    {
        Authorize(operation, actor, arguments);
        switch (operation)
        {
            case "collaboration.submit":
                return await SubmitAsync(WorkerWorkflows.Read<CollaborationTaskRequest>(arguments),
                    actor, cancellationToken).ConfigureAwait(false);
            case "collaboration.get":
            case "collaboration.status":
                return store.Get(Text(arguments, "id"));
            case "collaboration.list":
                return store.List(Integer(arguments, "limit", 40, 1, 200),
                    NullableLong(arguments, "beforeRow"), NullableLong(arguments, "snapshotRow"));
            case "collaboration.view.members":
                if (arguments.EnumerateObject().Count() != 1)
                    throw new ArgumentException("The member view accepts only mainConversationId.");
                return OwnedGateway.ReadMembers(Text(arguments, "mainConversationId"));
            case "collaboration.view.activity":
                return OwnedGateway.ReadActivity(arguments, cancellationToken);
            case "collaboration.view.graph":
                return ViewGraph(arguments);
            case "collaboration.view.list":
                return ViewList(arguments);
            case "collaboration.events":
                return store.ReadEvents(Text(arguments, "taskId"),
                    Long(arguments, "afterSequence", 0, 0, long.MaxValue),
                    Integer(arguments, "limit", 40, 1, CollaborationContract.MaximumEventPage),
                    NullableLong(arguments, "snapshotSequence"));
            case "collaboration.result":
                return store.ReadResult(Text(arguments, "taskId"),
                    Long(arguments, "expectedTargetRunRevision", null, 1, long.MaxValue),
                    Integer(arguments, "offset", 0, 0, int.MaxValue),
                    Integer(arguments, "maximumBytes", 16 * 1024, 256, 64 * 1024));
            case "collaboration.answer":
                return Answer(WorkerWorkflows.Read<CollaborationAnswerRequest>(arguments), actor);
            case "collaboration.steer":
                return Steer(WorkerWorkflows.Read<CollaborationSteerRequest>(arguments));
            case "collaboration.cancel":
                return Cancel(WorkerWorkflows.Read<CollaborationCancelRequest>(arguments));
            case "collaboration.reconcile":
                return await ReconcileAsync(WorkerWorkflows.Read<CollaborationReconcileRequest>(arguments),
                    cancellationToken).ConfigureAwait(false);
            case "collaboration.adopt":
                var adoption = WorkerWorkflows.Read<CollaborationAdoptRequest>(arguments);
                var adopting = store.Get(adoption.TaskId);
                RequireCommunication(adopting.Request.SourceConversationId,
                    adopting.Request.TargetConversationId);
                return store.Adopt(adoption);
            case "collaboration.grants.list":
                return store.ListGrants();
            case "collaboration.grants.save":
                return store.SaveGrant(WorkerWorkflows.Read<CollaborationProjectGrantRequest>(arguments));
            case "collaboration.grants.revoke":
                return store.RevokeGrant(Text(arguments, "id"),
                    Long(arguments, "expectedRevision", null, 1, long.MaxValue));
            case "collaboration.permissions.list":
                return PermissionStore().List(
                    arguments.TryGetProperty("afterPair", out var after) && after.ValueKind != JsonValueKind.Null
                        ? WorkerWorkflows.Read<ConversationCommunicationPair>(after) : null,
                    Integer(arguments, "limit", 32, 1, ConversationCommunicationContract.MaximumPageSize),
                    arguments.TryGetProperty("pendingOnly", out var pending) && pending.ValueKind == JsonValueKind.True,
                    arguments.TryGetProperty("conversationId", out var selected) && selected.ValueKind == JsonValueKind.String
                        ? selected.GetString() : null, cancellationToken);
            case "collaboration.permissions.directory":
                return PermissionStore().ReadDirectory(
                    arguments.TryGetProperty("afterConversationId", out var afterId) && afterId.ValueKind == JsonValueKind.String
                        ? afterId.GetString() : null,
                    Integer(arguments, "limit", 32, 1, ConversationCommunicationContract.MaximumPageSize),
                    cancellationToken);
            case "collaboration.permissions.get":
                return PermissionStore().Get(Text(arguments, "sourceConversationId"),
                    Text(arguments, "targetConversationId"), cancellationToken);
            case "collaboration.permissions.request":
                return RequestCommunication(WorkerWorkflows.Read<ConversationCommunicationRequest>(arguments),
                    cancellationToken);
            case "collaboration.permissions.decide":
                var decision = PermissionStore().SetAllowed(
                    WorkerWorkflows.Read<ConversationCommunicationSetAllowedRequest>(arguments),
                    cancellationToken);
                events.Publish("collaboration.permission.updated", decision.Pair.FirstConversationId,
                    new { decision.Pair, decision.Allowed, decision.Revision,
                        pendingRequestId = decision.PendingRequest?.RequestId });
                return decision;
            case "collaboration.recover":
                return Recover();
            default:
                throw new ArgumentException("Unknown collaboration command.");
        }
    }

    public async Task<CollaborationTask> SubmitAsync(CollaborationTaskRequest request,
        AgentProfile? actor, CancellationToken cancellationToken)
        => await SubmitCoreAsync(request, actor, null, cancellationToken).ConfigureAwait(false);

    internal async Task<CollaborationTask> SubmitFromWorkflowAsync(CollaborationTaskRequest request,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var sourceRun = SourceToolEffect(context, CollaborationToolExtension.SubmitTool);
        var saved = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(sourceRun.Effect!.Arguments);
        if (request.SourceConversationId != context.SessionId ||
            !JsonElement.DeepEquals(saved.Arguments, CerebralJson.Element(request)))
            throw new UnauthorizedAccessException("Collaboration requires the exact source workflow tool effect.");
        if (ConversationRolePolicy.IsMain(sourceRun.Submission) &&
            !OwnedGateway.IsOwnedCoordinator(context.SessionId, request.TargetConversationId))
            throw new UnauthorizedAccessException("Main hands execution to its owned coordinator using collaboration.coordinate.");
        return await SubmitCoreAsync(request, context.Profile, sourceRun.Id, cancellationToken).ConfigureAwait(false);
    }

    internal ConversationCommunicationPermission RequestCommunicationFromWorkflow(
        ConversationCommunicationRequest request, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var sourceRun = SourceToolEffect(context, CollaborationToolExtension.PermissionRequestTool);
        var saved = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(sourceRun.Effect!.Arguments);
        if (request.SourceConversationId != context.SessionId ||
            !JsonElement.DeepEquals(saved.Arguments, CerebralJson.Element(request)))
            throw new UnauthorizedAccessException("The communication request is not bound to this source chat.");
        return RequestCommunication(request, cancellationToken);
    }

    internal ConversationCommunicationPermission GetCommunicationForWorkflow(string targetConversationId,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        _ = SourceToolEffect(context, CollaborationToolExtension.PermissionGetTool);
        return PermissionStore().Get(context.SessionId, targetConversationId, cancellationToken);
    }

    internal ConversationCommunicationDirectoryPage ReadCommunicationDirectoryForWorkflow(
        string? afterConversationId, int limit, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        _ = SourceToolEffect(context, CollaborationToolExtension.PermissionDirectoryTool);
        return PermissionStore().ReadDirectory(afterConversationId, limit, cancellationToken);
    }

    private ConversationCommunicationPermission RequestCommunication(
        ConversationCommunicationRequest request, CancellationToken cancellationToken)
    {
        _ = gateway.GetConversation(request.SourceConversationId);
        _ = gateway.GetConversation(request.TargetConversationId);
        var pending = PermissionStore().Request(request, cancellationToken);
        events.Publish("collaboration.permission.updated", request.SourceConversationId,
            new { pending.Pair, pending.Allowed, pending.Revision,
                pendingRequestId = pending.PendingRequest?.RequestId });
        return pending;
    }

    private ConversationCommunicationStore PermissionStore() => permissions ??
        throw new InvalidOperationException("Conversation communication permissions are unavailable.");

    internal CollaborationTask RelaySourceOwnerAnswer(string taskId, ToolExecutionContext context)
    {
        var sourceRun = SourceToolEffect(context, CollaborationToolExtension.RelayAnswerTool);
        var savedCall = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(sourceRun.Effect!.Arguments);
        if (!context.Profile.Capabilities.Contains("agents.collaborate", StringComparer.Ordinal) ||
            !JsonElement.DeepEquals(savedCall.Arguments, CerebralJson.Element(new { taskId })))
            throw new UnauthorizedAccessException("The saved relay target or collaboration grant changed.");
        return RelaySourceOwnerAnswerCore(taskId, context, sourceRun, ownedOnly: false);
    }

    internal CollaborationTask RelayOwnedSourceOwnerAnswer(string taskId, ToolExecutionContext context)
    {
        var sourceRun = RequireOwnedCommandSource(context, WorkerWorkflowEffects.OwnedRelayCommand,
            CerebralJson.Element(new { taskId }));
        return RelaySourceOwnerAnswerCore(taskId, context, sourceRun, ownedOnly: true);
    }

    private CollaborationTask RelaySourceOwnerAnswerCore(string taskId, ToolExecutionContext context,
        WorkflowRun sourceRun, bool ownedOnly)
    {
        var state = sourceRun.State;
        if (state.ValueKind != JsonValueKind.Object ||
            !state.TryGetProperty("pendingCollaboration", out var savedBinding))
            throw new WorkflowConflictException("The source conversation has no pending collaboration question.");
        var binding = savedBinding.Deserialize<CollaborationQuestionBinding>(CerebralJson.Options) ??
            throw new WorkflowConflictException("The saved source question binding is invalid.");
        if (binding.TaskId != taskId || binding.SourceQuestionSetId is null ||
            binding.SourceQuestionRevision is null || binding.TargetQuestionSetId.Length == 0 ||
            binding.TargetQuestionRevision < 1 || binding.TargetRunRevision < 1)
            throw new WorkflowConflictException("The source question identity is incomplete or foreign.");
        var task = store.Get(taskId);
        if (task.SourceRunId != sourceRun.Id || task.Request.SourceConversationId != sourceRun.Submission.ConversationId ||
            task.SourceProfileId != sourceRun.Submission.ProfileId ||
            task.AttemptId != binding.AttemptId || task.TargetRunId != binding.TargetRunId)
            throw new WorkflowConflictException("The collaboration source or attempt changed.");
        if (ownedOnly && !OwnedGateway.IsOwnedCoordinator(task.Request.SourceConversationId,
            task.Request.TargetConversationId))
            throw new UnauthorizedAccessException("The native answer relay cannot target an independent chat.");
        RequireCommunication(task.Request.SourceConversationId, task.Request.TargetConversationId);
        var sourceAnswers = gateway.ReadSourceOwnerAnswer(sourceRun.Id, binding.SourceQuestionSetId,
            binding.SourceQuestionRevision.Value);
        var requestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            task.Id + ":" + binding.SourceQuestionSetId + ":" + binding.SourceQuestionRevision.Value)))
            .ToLowerInvariant();
        var answer = new CollaborationAnswerRequest(requestId, task.Id, binding.TaskRevision,
            task.AttemptId, binding.TargetRunRevision, binding.TargetQuestionSetId,
            binding.TargetQuestionRevision, sourceAnswers.Answers);
        if (store.TryAnswerRetry(answer) is { } accepted) return accepted;
        if (task.Questions is not { } targetQuestions ||
            task.Revision != binding.TaskRevision ||
            task.Status != CollaborationTaskStatus.Waiting ||
            task.TargetRunRevision != binding.TargetRunRevision ||
            targetQuestions.Id != binding.TargetQuestionSetId ||
            targetQuestions.Revision != binding.TargetQuestionRevision ||
            !JsonElement.DeepEquals(CerebralJson.Element(sourceAnswers.Fields),
                CerebralJson.Element(targetQuestions.Fields)))
            throw new WorkflowConflictException("The target question changed before the owner answer was relayed.");
        return Answer(answer, null);
    }

    private WorkflowRun SourceToolEffect(ToolExecutionContext context, string toolName)
    {
        var source = SourceEffect(context, WorkerWorkflowEffects.ToolCommand);
        var saved = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(source.Effect!.Arguments);
        if (saved.Name != toolName)
            throw new UnauthorizedAccessException("The saved effect names a different collaboration tool.");
        return source;
    }

    private WorkflowRun SourceEffect(ToolExecutionContext context, string commandId)
    {
        if (!context.EnforceProfileScope || string.IsNullOrWhiteSpace(context.EffectId))
            throw new UnauthorizedAccessException("Collaboration requires a host-owned source effect.");
        var sourceRun = gateway.TryGet(context.JobId) ??
            throw new UnauthorizedAccessException("The source workflow is unavailable.");
        if (sourceRun.Id != context.JobId || sourceRun.Submission.RequestId != context.JobId ||
            sourceRun.Submission.ConversationId != context.SessionId ||
            sourceRun.Submission.ProfileId != context.Profile.Id ||
            sourceRun.Submission.ParentRunId is not null ||
            sourceRun.Submission.Caller?.Kind != EventActorKind.User ||
            sourceRun.IsTerminal || sourceRun.CancelRequested ||
            sourceRun.Effect is not { } effect || effect.Id != context.EffectId ||
            effect.CommandId != commandId || effect.CommandVersion != 1 ||
            effect.State is not (WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown))
            throw new UnauthorizedAccessException("The source workflow no longer owns this effect.");
        return sourceRun;
    }

    private async Task<CollaborationTask> SubmitCoreAsync(CollaborationTaskRequest request,
        AgentProfile? actor, string? sourceRunId, CancellationToken cancellationToken,
        ConversationSummary? preparedOwnedTarget = null)
    {
        // Only the private owned path supplies a host-prepared target after exact native
        // source admission. Reprove ownership before separating it from independent authority.
        var owned = preparedOwnedTarget is not null && sourceRunId is not null &&
            OwnedGateway.IsOwnedCoordinator(request.SourceConversationId, request.TargetConversationId);
        if (preparedOwnedTarget is not null && !owned)
            throw new UnauthorizedAccessException("The prepared coordinator no longer belongs to this source Main.");
        if (actor is not null && !owned &&
            !actor.Capabilities.Contains("agents.collaborate", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("This agent profile lacks agents.collaborate.");
        var source = gateway.GetConversation(request.SourceConversationId);
        var target = preparedOwnedTarget ?? gateway.GetConversation(request.TargetConversationId);
        if (actor is not null && source.ProfileId != actor.Id)
            throw new UnauthorizedAccessException("A calling agent can delegate only from its own conversation.");
        if (store.IsTargetConversation(request.SourceConversationId))
            throw new UnauthorizedAccessException("The first collaboration checkpoint is one hop; a target attempt cannot delegate another collaboration task.");
        RequireCommunication(source.Id, target.Id);
        var task = store.Submit(request, source, target, request.ProjectGrantId, sourceRunId);
        return await DispatchAsync(task, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<CollaborationTask> Recover()
    {
        var recovered = new List<CollaborationTask>();
        long? before = null;
        long? snapshot = null;
        do
        {
            var page = store.List(200, before, snapshot);
            snapshot ??= page.SnapshotRow;
            foreach (var task in page.Tasks)
            {
                if (task.TargetRunId is null)
                {
                    recovered.Add(task);
                    continue;
                }
                if (gateway.TryGet(task.TargetRunId) is not { } run)
                {
                    recovered.Add(task.Status == CollaborationTaskStatus.ReconcileRequired
                        ? task
                        : store.MarkReconcileRequired(task.Id, task.Revision,
                            "The linked target workflow is unavailable; inspect retained state before retrying."));
                    continue;
                }
                if (run.Revision < task.TargetRunRevision) { recovered.Add(task); continue; }
                recovered.Add(store.Synchronize(task.Id, task.Revision, run));
            }
            before = page.NextBeforeRow;
        } while (before is not null);
        return recovered.AsReadOnly();
    }

    private CollaborationViewTaskPage ViewList(JsonElement arguments)
    {
        var fields = arguments.EnumerateObject().Select(field => field.Name).ToArray();
        if (fields.Length is < 2 or > 4 || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Except(["sourceConversationId", "limit", "beforeRow", "snapshotRow"], StringComparer.Ordinal).Any() ||
            !fields.Contains("sourceConversationId", StringComparer.Ordinal) || !fields.Contains("limit", StringComparer.Ordinal))
            throw new ArgumentException("Collaboration view list requires bounded source coordinates.");
        var source = gateway.GetConversation(Text(arguments, "sourceConversationId"));
        var page = store.ListForSource(source.Id, Integer(arguments, "limit", 20, 1, 40),
            NullableLong(arguments, "beforeRow"), NullableLong(arguments, "snapshotRow"));
        if (page.Tasks.Any(task => task.Request.SourceConversationId != source.Id ||
            task.SourceProfileId != source.ProfileId || task.SourceProjectId != source.ProjectId))
            throw new InvalidDataException("A collaboration task no longer matches its source conversation.");
        return new(page.Tasks.Select(task => new CollaborationViewTask(task.Id, task.Revision,
            task.Status, task.Request.TargetConversationId, task.TargetProfileId, task.TargetProjectId,
            task.TargetRunId, task.TargetRunRevision, task.UpdatedAt)
            { SourceRunId = task.SourceRunId }).ToArray(),
            page.SnapshotRow, page.NextBeforeRow);
    }

    private CollaborationViewGraph ViewGraph(JsonElement arguments)
    {
        var fields = arguments.EnumerateObject().Select(field => field.Name).ToArray();
        if (fields.Length != 3 || fields.Distinct(StringComparer.Ordinal).Count() != 3 ||
            fields.Except(["taskId", "childOffset", "limit"], StringComparer.Ordinal).Any())
            throw new ArgumentException("Collaboration view requires taskId, childOffset and limit exactly once.");
        var task = store.Get(Text(arguments, "taskId"));
        var offset = Integer(arguments, "childOffset", 0, 0, CollaborationContract.MaximumViewChildren);
        var limit = Integer(arguments, "limit", 16, 1, CollaborationContract.MaximumViewChildren);
        CollaborationViewGraph Incomplete(string reason) => new(1, task.Id, task.Revision, task.Status,
            task.Request.SourceConversationId, task.SourceProfileId, task.SourceProjectId,
            task.Request.TargetConversationId, task.TargetProfileId, task.TargetProjectId,
            null, [], 0, null, false, false, reason) { SourceRunId = task.SourceRunId };

        try
        {
            var source = gateway.GetConversation(task.Request.SourceConversationId);
            var target = gateway.GetConversation(task.Request.TargetConversationId);
            if (source.Id != task.Request.SourceConversationId || source.ProfileId != task.SourceProfileId ||
                source.ProjectId != task.SourceProjectId || target.Id != task.Request.TargetConversationId ||
                target.ProfileId != task.TargetProfileId || target.ProjectId != task.TargetProjectId)
                return Incomplete("conversation-lineage-changed");
        }
        catch (KeyNotFoundException) { return Incomplete("conversation-unavailable"); }
        if (task.TargetRunId is null) return Incomplete("coordinator-not-attached");
        var coordinator = gateway.TryGet(task.TargetRunId);
        if (coordinator is null) return Incomplete("coordinator-unavailable");
        if (coordinator.Id != task.TargetRunId || coordinator.Submission.RequestId != coordinator.Id ||
            coordinator.Submission.ConversationId != task.Request.TargetConversationId ||
            coordinator.Submission.ProfileId != task.TargetProfileId ||
            coordinator.Submission.ProjectId != task.TargetProjectId ||
            coordinator.Submission.DefinitionId != ConversationWorkflowModule.DefinitionId ||
            coordinator.Submission.DefinitionVersion != ConversationWorkflowModule.DefinitionVersion ||
            coordinator.Submission.ParentRunId is not null ||
            coordinator.Submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.collaboration" })
            return Incomplete("coordinator-lineage-changed");
        if (gateway is not ICollaborationViewGateway viewGateway)
            return Incomplete("child-view-unavailable");
        var ids = viewGateway.ChildRunIdsForView(coordinator.Id);
        if (ids.Count > CollaborationContract.MaximumViewChildren ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            return Incomplete("child-limit-invalid");
        var children = new List<CollaborationViewRun>(ids.Count);
        var conversations = new HashSet<string>(StringComparer.Ordinal)
            { task.Request.SourceConversationId, task.Request.TargetConversationId };
        foreach (var id in ids)
        {
            var child = gateway.TryGet(id);
            if (child is null) return Incomplete("child-unavailable");
            if (child.Id != id || child.Submission.RequestId != id ||
                child.Submission.ParentRunId != coordinator.Id ||
                child.Submission.ProjectId != task.TargetProjectId ||
                child.Submission.DefinitionId != ConversationWorkflowModule.DefinitionId ||
                child.Submission.DefinitionVersion != ConversationWorkflowModule.DefinitionVersion ||
                child.Submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.coordination" } ||
                !conversations.Add(child.Submission.ConversationId) &&
                    !(gateway is IMainOwnedCollaborationGateway owned &&
                      owned.IsOwnedCoordinator(task.Request.SourceConversationId, task.Request.TargetConversationId) &&
                      owned.IsOwnedWorker(task.Request.SourceConversationId, child.Submission)))
                return Incomplete("child-lineage-changed");
            children.Add(ViewRun(child));
        }
        if (offset > children.Count) throw new ArgumentOutOfRangeException(nameof(offset));
        var next = offset + limit < children.Count ? offset + limit : (int?)null;
        return new(1, task.Id, task.Revision, task.Status, task.Request.SourceConversationId,
            task.SourceProfileId, task.SourceProjectId, task.Request.TargetConversationId,
            task.TargetProfileId, task.TargetProjectId, ViewRun(coordinator),
            children.Skip(offset).Take(limit).ToArray(), children.Count, next, true,
            task.TargetRunRevision == coordinator.Revision && task.Status.ToString() == coordinator.Status.ToString(), null)
            { SourceRunId = task.SourceRunId };
    }

    private static CollaborationViewRun ViewRun(WorkflowRun run) => new(run.Id,
        run.Submission.ConversationId, run.Submission.ProfileId, run.Submission.ProjectId,
        run.Submission.ParentRunId, run.Status, run.Revision);

    private async Task<CollaborationTask> DispatchAsync(CollaborationTask task,
        CancellationToken cancellationToken)
    {
        if (task.TargetRunId is not null) return task;
        RequireCommunication(task.Request.SourceConversationId, task.Request.TargetConversationId);
        var run = gateway.TryGet(task.AttemptId) ??
            await gateway.SubmitAsync(task, cancellationToken).ConfigureAwait(false);
        var attached = store.AttachAttempt(task.Id, task.Revision, run);
        Publish(attached);
        return attached;
    }

    private CollaborationTask Answer(CollaborationAnswerRequest request, AgentProfile? actor)
    {
        // The target workflow records answers as user decisions. An agent cannot use the
        // collaboration route to manufacture that provenance, including on an exact retry.
        if (actor is not null)
            throw new UnauthorizedAccessException("Collaboration questions require an owner answer.");
        if (store.TryAnswerRetry(request) is { } retry) return retry;
        var task = Expected(request.TaskId, request.ExpectedTaskRevision);
        RequireCommunication(task.Request.SourceConversationId, task.Request.TargetConversationId);
        if (task.AttemptId != request.AttemptId || task.TargetRunId != request.AttemptId ||
            task.TargetRunRevision != request.ExpectedTargetRunRevision ||
            task.Status != CollaborationTaskStatus.Waiting || task.Questions is not { } questions ||
            questions.Id != request.QuestionSetId || questions.Revision != request.ExpectedQuestionRevision)
            throw new WorkflowConflictException("The collaboration attempt or pending question changed.");
        var currentRun = gateway.TryGet(request.AttemptId);
        if (currentRun is null || currentRun.Id != task.TargetRunId ||
            currentRun.Submission.ConversationId != task.Request.TargetConversationId ||
            currentRun.Submission.ProfileId != task.TargetProfileId ||
            currentRun.Revision != request.ExpectedTargetRunRevision ||
            currentRun.Status != WorkflowStatus.Waiting ||
            currentRun.Questions?.Id != request.QuestionSetId ||
            currentRun.Questions.Revision != request.ExpectedQuestionRevision)
            throw new WorkflowConflictException("The target workflow changed before the answer.");
        var run = gateway.Answer(task, request);
        task = store.RecordAnswer(request);
        var updated = store.Synchronize(task.Id, task.Revision, run);
        Publish(updated);
        return updated;
    }

    private CollaborationTask Steer(CollaborationSteerRequest request)
    {
        if (store.TrySteeringRetry(request) is { } retry) return retry;
        var task = Expected(request.TaskId, request.ExpectedTaskRevision);
        RequireCommunication(task.Request.SourceConversationId, task.Request.TargetConversationId);
        var run = gateway.Steer(task, request);
        task = store.RecordSteering(request);
        var updated = store.Synchronize(task.Id, task.Revision, run);
        Publish(updated);
        return updated;
    }

    private CollaborationTask Cancel(CollaborationCancelRequest request)
    {
        var task = Expected(request.TaskId, request.ExpectedTaskRevision);
        task = store.RequestCancel(task.Id, task.Revision);
        var run = gateway.Cancel(task);
        var updated = store.Synchronize(task.Id, task.Revision, run);
        Publish(updated);
        return updated;
    }

    private async Task<CollaborationTask> ReconcileAsync(CollaborationReconcileRequest request,
        CancellationToken cancellationToken)
    {
        var task = Expected(request.TaskId, request.ExpectedTaskRevision);
        RequireCommunication(task.Request.SourceConversationId, task.Request.TargetConversationId);
        if (task.TargetRunId is null)
            return await DispatchAsync(task, cancellationToken).ConfigureAwait(false);
        var run = await gateway.ReconcileAsync(task, cancellationToken).ConfigureAwait(false);
        var updated = store.Synchronize(task.Id, task.Revision, run);
        Publish(updated);
        return updated;
    }

    private CollaborationTask Expected(string id, long revision)
    {
        var task = store.Get(id);
        if (task.Revision != revision)
            throw new WorkflowConflictException($"Collaboration task changed: expected revision {revision}, actual {task.Revision}.");
        return task;
    }

    private void Publish(CollaborationTask task) =>
        events.Publish("collaboration.updated", task.Id,
            new { task.Id, task.Revision, task.Status, task.WaitingReason });

    private static IReadOnlyList<WorkerWorkflows.OperationDescriptor> BuildOperations()
    {
        static JsonElement Schema(string fields, string required)
        {
            using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{" + fields +
                "},\"required\":[" + required + "],\"additionalProperties\":false}");
            return document.RootElement.Clone();
        }
        const string id = "\"id\":{\"type\":\"string\"}";
        const string taskId = "\"taskId\":{\"type\":\"string\"}";
        const string revision = "\"expectedTaskRevision\":{\"type\":\"integer\",\"minimum\":1}";
        return Array.AsReadOnly(new[]
        {
            new WorkerWorkflows.OperationDescriptor("collaboration.submit", 1,
                "Submit one durable local task envelope to an independent target conversation. Exact request retries return the same task.",
                Schema("\"requestId\":{\"type\":\"string\"},\"sourceConversationId\":{\"type\":\"string\"},\"targetConversationId\":{\"type\":\"string\"},\"instruction\":{\"type\":\"string\"},\"inputReferences\":{\"type\":\"array\",\"maxItems\":32},\"dependencyTaskIds\":{\"type\":\"array\",\"maxItems\":32},\"parentTaskId\":{\"type\":[\"string\",\"null\"]},\"stepLimit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":256},\"responseSharePercent\":{\"type\":[\"integer\",\"null\"],\"minimum\":5,\"maximum\":50},\"projectGrantId\":{\"type\":[\"string\",\"null\"]}",
                    "\"requestId\",\"sourceConversationId\",\"targetConversationId\",\"instruction\",\"inputReferences\",\"dependencyTaskIds\""))
                { RequiredCapabilities = ["agents.collaborate"] },
            new WorkerWorkflows.OperationDescriptor("collaboration.get", 1, "Read one durable collaboration task.", Schema(id, "\"id\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.status", 1, "Read one durable collaboration task status.", Schema(id, "\"id\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.list", 1, "Inspect bounded collaboration tasks without a plugin.",
                Schema("\"limit\":{\"type\":\"integer\"},\"beforeRow\":{\"type\":[\"integer\",\"null\"]},\"snapshotRow\":{\"type\":[\"integer\",\"null\"]}", ""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.view.members", 1,
                "List exact explicitly owned context handles for one Main; communication targets are not ownership.",
                Schema("\"mainConversationId\":{\"type\":\"string\"}", "\"mainConversationId\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.view.activity", 1,
                "Read one bounded event page for an exact Main-owned member with a Main/member-bound cursor.",
                Schema("\"mainConversationId\":{\"type\":\"string\"},\"contextId\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"cursor\":{\"type\":[\"object\",\"null\"]}", "\"mainConversationId\",\"contextId\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.view.graph", 1,
                "Inspect one bounded, verified coordinator and worker run graph without content.",
                Schema(taskId + ",\"childOffset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":64}",
                    "\"taskId\",\"childOffset\",\"limit\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.view.list", 1,
                "List bounded task coordinates for one exact owner conversation without task content.",
                Schema("\"sourceConversationId\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":40},\"beforeRow\":{\"type\":[\"integer\",\"null\"]},\"snapshotRow\":{\"type\":[\"integer\",\"null\"]}",
                    "\"sourceConversationId\",\"limit\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.events", 1, "Read a bounded immutable task-event page.",
                Schema(taskId + ",\"afterSequence\":{\"type\":\"integer\"},\"limit\":{\"type\":\"integer\"},\"snapshotSequence\":{\"type\":[\"integer\",\"null\"]}", "\"taskId\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.result", 1, "Read a bounded UTF-8 page from the exact terminal target result.",
                Schema(taskId + ",\"expectedTargetRunRevision\":{\"type\":\"integer\",\"minimum\":1},\"offset\":{\"type\":\"integer\"},\"maximumBytes\":{\"type\":\"integer\"}", "\"taskId\",\"expectedTargetRunRevision\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.answer", 2, "Submit an owner answer to the exact waiting target attempt and question revision.",
                Schema("\"requestId\":{\"type\":\"string\"}," + taskId + "," + revision + ",\"attemptId\":{\"type\":\"string\"},\"expectedTargetRunRevision\":{\"type\":\"integer\",\"minimum\":1},\"questionSetId\":{\"type\":\"string\"},\"expectedQuestionRevision\":{\"type\":\"integer\",\"minimum\":1},\"answers\":{\"type\":\"object\"}", "\"requestId\",\"taskId\",\"expectedTaskRevision\",\"attemptId\",\"expectedTargetRunRevision\",\"questionSetId\",\"expectedQuestionRevision\",\"answers\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.steer", 1, "Append bounded steering to the target attempt.",
                Schema("\"requestId\":{\"type\":\"string\"}," + taskId + "," + revision + ",\"text\":{\"type\":\"string\"},\"assignmentText\":{\"type\":[\"string\",\"null\"]}", "\"requestId\",\"taskId\",\"expectedTaskRevision\",\"text\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.cancel", 1, "Persist cancellation intent before signalling the target workflow.",
                Schema(taskId + "," + revision, "\"taskId\",\"expectedTaskRevision\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.reconcile", 1, "Reconcile or recover one exact collaboration attempt.",
                Schema(taskId + "," + revision, "\"taskId\",\"expectedTaskRevision\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.adopt", 1, "Adopt an exact completed result into the source conversation as a durable event.",
                Schema("\"requestId\":{\"type\":\"string\"}," + taskId + "," + revision + ",\"attemptId\":{\"type\":\"string\"},\"expectedTargetRunRevision\":{\"type\":\"integer\",\"minimum\":1}", "\"requestId\",\"taskId\",\"expectedTaskRevision\",\"attemptId\",\"expectedTargetRunRevision\"")),
            new WorkerWorkflows.OperationDescriptor("collaboration.grants.list", 1, "List explicit cross-project collaboration grants.", Schema("", ""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.grants.save", 1, "Create one explicit cross-project collaboration grant.",
                Schema("\"id\":{\"type\":\"string\"},\"sourceProjectId\":{\"type\":\"string\"},\"targetProjectId\":{\"type\":\"string\"},\"targetProfileIds\":{\"type\":\"array\"},\"capabilities\":{\"type\":\"array\"}", "\"id\",\"sourceProjectId\",\"targetProjectId\",\"targetProfileIds\",\"capabilities\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.grants.revoke", 1, "Revoke one exact cross-project collaboration grant revision.",
                Schema(id + ",\"expectedRevision\":{\"type\":\"integer\",\"minimum\":1}", "\"id\",\"expectedRevision\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.permissions.list", 1,
                "Owner-only bounded communication permission and pending-request page.",
                Schema("\"conversationId\":{\"type\":[\"string\",\"null\"]},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":128},\"pendingOnly\":{\"type\":\"boolean\"},\"afterPair\":{\"type\":[\"object\",\"null\"]}", ""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.permissions.directory", 1,
                "Owner-only bounded conversation identity directory without content.",
                Schema("\"afterConversationId\":{\"type\":[\"string\",\"null\"]},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":128}", ""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.permissions.get", 1,
                "Owner-only exact pair permission and revision.",
                Schema("\"sourceConversationId\":{\"type\":\"string\"},\"targetConversationId\":{\"type\":\"string\"}", "\"sourceConversationId\",\"targetConversationId\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.permissions.request", 1,
                "Owner-requested permission review; requesting cannot allow communication.",
                Schema("\"requestId\":{\"type\":\"string\"},\"sourceConversationId\":{\"type\":\"string\"},\"targetConversationId\":{\"type\":\"string\"},\"expectedRevision\":{\"type\":\"integer\",\"minimum\":0}", "\"requestId\",\"sourceConversationId\",\"targetConversationId\",\"expectedRevision\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.permissions.decide", 1,
                "Owner-only CAS allow, deny, or revoke one conversation pair.",
                Schema("\"pair\":{\"type\":\"object\"},\"expectedRevision\":{\"type\":\"integer\",\"minimum\":0},\"allowed\":{\"type\":\"boolean\"},\"pendingRequestId\":{\"type\":[\"string\",\"null\"]}", "\"pair\",\"expectedRevision\",\"allowed\""), true),
            new WorkerWorkflows.OperationDescriptor("collaboration.recover", 1, "Reconcile persisted task projections against their existing target workflows after restart.", Schema("", ""), true)
        });
    }

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text ? text : throw new ArgumentException($"Missing string '{name}'.");

    private static string? Optional(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() : null;

    private static int Integer(JsonElement input, string name, int fallback, int minimum, int maximum)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        var number = value.GetInt32();
        return number >= minimum && number <= maximum ? number :
            throw new ArgumentOutOfRangeException(name);
    }

    private static long Long(JsonElement input, string name, long? fallback, long minimum, long maximum)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return fallback ?? throw new ArgumentException($"Missing integer '{name}'.");
        var number = value.GetInt64();
        return number >= minimum && number <= maximum ? number :
            throw new ArgumentOutOfRangeException(name);
    }

    private static long? NullableLong(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt64() : null;
}
