using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    public const string DelegateCommand = "cerebral.host:delegate";
    public sealed record HelperWorkflow(string DefinitionId, string DefinitionVersion, string Description);
    public sealed record HelperReasoningPolicy(bool Allowed, int MinimumTokens, int MaximumTokens, int DefaultTokens);
    public sealed record HelperTaskBudgetPolicy(int? MaxContextTokens, int? MaxResponseTokens,
        bool AllowAutonomousSelection, int? CurrentFitContextTokens);
    public sealed record HelperEntry(string Id, int Revision, string Name, string Purpose)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HelperWorkflow? Workflow { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HelperReasoningPolicy? ReasoningAdjustment { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ConfiguredContextTokens { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HelperTaskBudgetPolicy? TaskBudgetPolicy { get; init; }
    }
    public sealed record AgentAssignment(string ProfileId, int ProfileRevision, string Instruction)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WorkerContextId { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WorkerName { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RequestedReasoningTokens { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RequestedContextTokens { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RequestedResponseTokens { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxContextTokens { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxResponseTokens { get; init; }
    }
    public sealed record AgentAssignments(IReadOnlyList<AgentAssignment> Assignments);
    private sealed record PreparedAgentPlan(string ParentRunId, IReadOnlyList<AgentAssignment> Assignments,
        IReadOnlyList<CoordinationWorkflowBinding>? Workflows = null,
        IReadOnlyList<OwnedConversationContext>? Contexts = null);

    private bool CanDelegate(WorkflowSubmission submission, AgentProfile current) =>
        workflowHost is not null && workflowHost().CanAssignWorkers(submission, current) &&
        workflowHost().Engine?.SupportsWork("cerebral.agents.plan", 1) == true;

    private IReadOnlyList<HelperEntry>? AvailableHelpers(WorkflowSubmission submission, AgentProfile current)
    {
        if (!CanDelegate(submission, current)) return null;
        var host = workflowHost!();
        var helpers = new List<HelperEntry>();
        // Execution stays in this coordinator's journal. A helper's model can be remote.
        foreach (var profile in store.List<AgentProfile>("agent")
            .Where(profile => profile.Role == "helper" && profile.WorkerId == "local" && profile.Id != current.Id)
            .OrderBy(profile => profile.Id, StringComparer.Ordinal))
        {
            try
            {
                var binding = host.ResolveHelperWorkflow(profile, submission.ProjectId);
                var description = host.Registry.GetDefinition(binding.DefinitionId, binding.DefinitionVersion).Descriptor.Description;
                helpers.Add(new(profile.Id, profile.Revision, Clip(profile.Name, 128), Clip(profile.Purpose, 256))
                {
                    Workflow = profile.PluginSettings.ContainsKey(CoordinationWorkflowBinding.SettingsKey)
                        ? new(binding.DefinitionId, binding.DefinitionVersion, Clip(description, 256)) : null,
                    ReasoningAdjustment = DescribeHelperReasoningPolicy(profile, binding),
                    ConfiguredContextTokens = profile.ContextTokens,
                    TaskBudgetPolicy = profile.TaskBudgetPolicy is { SchemaVersion: 1 } policy
                        ? new(policy.MaxContextTokens, policy.MaxResponseTokens,
                            policy.AllowAutonomousSelection,
                            profile.ModelWorkerId == "local" && store.Get<ModelRecord>("model", profile.ModelId) is { } model
                                ? model.ContextTokens : null) : null
                });
                if (helpers.Count == 16) break;
            }
            catch (Exception error) when (IsOrdinary(error))
            {
                // A broken optional helper must not break catalog discovery or ordinary chat.
                // Explicit selection still receives its exact preflight rejection below.
            }
        }
        return helpers;
    }

    private HelperReasoningPolicy? DescribeHelperReasoningPolicy(AgentProfile profile, CoordinationWorkflowBinding binding)
    {
        if (binding.DefinitionId != ConversationWorkflowModule.DefinitionId ||
            binding.DefinitionVersion != ConversationWorkflowModule.DefinitionVersion ||
            !profile.AllowReasoningAdjustment)
            return new(false, 0, 0, 0);
        // Remote model capacity is not a synchronous catalog fact. Omit it rather than
        // presenting a guessed ceiling; selection still receives current admission.
        if (profile.ModelWorkerId != "local" || store.Get<ModelRecord>("model", profile.ModelId) is not { } model)
            return null;
        try
        {
            var budget = AgentBudgetPolicy.Resolve(profile, model,
                model.AutomaticContextSizing ? model.ContextTokens : null);
            var currentCeiling = profile.ReasoningTokenCeiling == 0
                ? profile.ReasoningTokens : profile.ReasoningTokenCeiling;
            var acceptedCeiling = profile.ReasoningTokenCeiling == 0
                ? budget.ReasoningTokens : profile.ReasoningTokenCeiling;
            var contextCeiling = budget.ContextTokens - AgentTokenLimits.MinimumResponseTokens -
                AgentTokenLimits.ContextSafetyTokens - AgentTokenLimits.MinimumPromptTokens - 1;
            var maximum = Math.Min(Math.Min(currentCeiling, acceptedCeiling), contextCeiling);
            var minimum = budget.ReasoningMode == "on" ? AgentTokenLimits.MinimumReasoningTokens : 0;
            var defaults = budget.ReasoningMode == "on" ? budget.ReasoningTokens : 0;
            return maximum < AgentTokenLimits.MinimumReasoningTokens || defaults > maximum
                ? new(false, 0, 0, 0)
                : new(true, minimum, maximum, defaults);
        }
        catch (ArgumentException) { return null; }
    }

    private sealed class DelegateHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(DelegateCommand, 1,
            "Create bounded isolated child contexts from admitted helper profiles; the parent subsequently yields its slot.",
            Schema("""{"type":"object","properties":{"assignments":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"object","properties":{"profileId":{"type":"string","minLength":1,"maxLength":128},"profileRevision":{"type":"integer","minimum":0},"instruction":{"type":"string","minLength":1,"maxLength":8192},"workerContextId":{"type":"string","minLength":1,"maxLength":128},"workerName":{"type":"string","minLength":1,"maxLength":128},"requestedReasoningTokens":{"type":"integer","minimum":0,"maximum":32768},"requestedContextTokens":{"type":"integer","minimum":512,"maximum":1048576},"requestedResponseTokens":{"type":"integer","minimum":128,"maximum":131072},"maxContextTokens":{"type":"integer","minimum":512,"maximum":1048576},"maxResponseTokens":{"type":"integer","minimum":128,"maximum":131072}},"required":["profileId","profileRevision","instruction"],"additionalProperties":false}}},"required":["assignments"],"additionalProperties":false}"""),
            Schema("""{"type":"object","properties":{"workerContexts":{"type":"array","maxItems":8},"runIds":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"string"}}},"required":["runIds"],"additionalProperties":false}"""),
            // Owned lineage and independent profile delegation are alternative authorities.
            // ValidateAssignments and child commit both recheck the applicable exact route.
            [], WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 60);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token) => owner.DelegateAsync(context, false, token);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) => owner.DelegateAsync(context, true, token);
    }

    private IReadOnlyList<CoordinationWorkflowBinding> ValidateAssignments(WorkflowSubmission submission,
        AgentProfile profile, IReadOnlyList<AgentAssignment> assignments)
    {
        if (!CanDelegate(submission, profile))
            throw new AdmissionException("This root profile or engine does not admit delegation.");
        if (assignments is null || assignments.Count is < 1 or > 8 || assignments.Any(item => item is null))
            throw new ArgumentException("Delegate one to eight admitted helper assignments.");
        var ownedCoordinator = workflowHost!().OwnedContexts.Owner(submission.ConversationId);
        if (ownedCoordinator is not null && (ownedCoordinator.Role != "coordinator" ||
            submission.Caller is not { Kind: Cerebral.Contracts.EventActorKind.Service, Id: "cerebral.collaboration" }))
            throw new AdmissionException("Worker reuse requires an authenticated owned coordinator.");
        var bindings = new List<CoordinationWorkflowBinding>(assignments.Count);
        var selectedContexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in assignments)
        {
            var helper = store.Get<AgentProfile>("agent", item.ProfileId);
            if (string.IsNullOrWhiteSpace(item.Instruction) || Encoding.UTF8.GetByteCount(item.Instruction) > 8192 ||
                helper is null || helper.Id == profile.Id || helper.Role != "helper" || helper.WorkerId != "local" ||
                helper.Revision != item.ProfileRevision)
                throw new AdmissionException("A helper revision changed or the bounded assignment is invalid. Refresh the helper catalog.");
            if (item.WorkerName is { } label && (string.IsNullOrWhiteSpace(label) || label.Length > 128 || label.Any(char.IsControl)))
                throw new AdmissionException("Worker names must be bounded readable labels.");
            if (item.WorkerContextId is { } handle)
            {
                if (ownedCoordinator is null || !selectedContexts.Add(handle))
                    throw new AdmissionException("A worker context cannot be borrowed or assigned twice in one wave.");
                var root = workflowHost!().Store.GetConversation(ConversationWorkflowModule.DefinitionId, ownedCoordinator.MainConversationId) ??
                    throw new AdmissionException("The owning Main is unavailable.");
                var member = workflowHost().OwnedContexts.Require(root.Id, root.ProfileId, root.ProjectId, handle);
                if (member.Role != "worker" || member.ProfileId != helper.Id)
                    throw new AdmissionException("The worker context has a different admitted profile or role.");
            }
            else if (ownedCoordinator is null && item.WorkerName is not null)
                throw new AdmissionException("Named persistent workers require an explicitly Main-owned coordinator.");
            var binding = workflowHost!().ResolveHelperWorkflow(helper, submission.ProjectId)
                .InheritConversationWorkingDirectory(submission);
            WorkflowSchema.Validate(binding.Input, workflowHost().Registry
                .GetDefinition(binding.DefinitionId, binding.DefinitionVersion).Descriptor.InputSchema);
            if (item.RequestedReasoningTokens is not null || item.RequestedContextTokens is not null ||
                item.RequestedResponseTokens is not null || item.MaxContextTokens is not null ||
                item.MaxResponseTokens is not null)
            {
                if (binding.DefinitionId != ConversationWorkflowModule.DefinitionId ||
                    binding.DefinitionVersion != ConversationWorkflowModule.DefinitionVersion)
                    throw new AdmissionException("Typed child budget requests require the built-in Conversation workflow.");
                var fields = binding.Input.EnumerateObject().ToDictionary(field => field.Name,
                    field => (object)field.Value.Clone(), StringComparer.Ordinal);
                void Add(string name, int? value)
                {
                    if (value is null) return;
                    if (fields.ContainsKey(name))
                        throw new AdmissionException("The child owner binding already selected " + name + ".");
                    fields[name] = value.Value;
                }
                Add("requestedReasoningTokens", item.RequestedReasoningTokens);
                Add("requestedContextTokens", item.RequestedContextTokens);
                Add("requestedResponseTokens", item.RequestedResponseTokens);
                Add("maxContextTokens", item.MaxContextTokens);
                Add("maxResponseTokens", item.MaxResponseTokens);
                binding = binding with { Input = Element(fields) };
                WorkflowSchema.Validate(binding.Input, workflowHost().Registry
                    .GetDefinition(binding.DefinitionId, binding.DefinitionVersion).Descriptor.InputSchema);
            }
            bindings.Add(binding);
        }
        return bindings;
    }

    internal static int? ChildRequestedReasoningTokens(AgentAssignment item, CoordinationWorkflowBinding binding)
    {
        if (item.RequestedReasoningTokens is { } selected) return selected;
        return binding.DefinitionId == ConversationWorkflowModule.DefinitionId &&
            binding.DefinitionVersion == ConversationWorkflowModule.DefinitionVersion &&
            binding.Input.TryGetProperty("requestedReasoningTokens", out var configured)
                ? configured.GetInt32() : null;
    }

    private async Task ValidateRequestedReasoningPoliciesAsync(WorkflowSubmission submission,
        IReadOnlyList<AgentAssignment> assignments, IReadOnlyList<CoordinationWorkflowBinding> bindings,
        CancellationToken token)
    {
        if (bindings.Count != assignments.Count)
            throw new AdmissionException("The selected child workflow bindings do not match the assignment wave.");
        for (var index = 0; index < assignments.Count; index++)
        {
            var item = assignments[index];
            var binding = bindings[index];
            var requested = ChildRequestedReasoningTokens(item, binding);
            var budgetRequested = new[] { "requestedContextTokens", "requestedResponseTokens", "maxContextTokens", "maxResponseTokens" }
                .Any(name => binding.Input.TryGetProperty(name, out _));
            token.ThrowIfCancellationRequested();
            var helper = store.Get<AgentProfile>("agent", item.ProfileId);
            if (requested is null && !budgetRequested && helper?.TaskBudgetPolicy is null) continue;
            if (helper is null || helper.Revision != item.ProfileRevision)
                throw new WorkflowConflictException("The selected helper revision changed before thinking-policy admission.");
            // Capture the same effective model budget used by child admission, including remote
            // model resolution. An assignment never inherits its parent's thinking policy.
            var snapshot = WorkerExecutionSnapshot.Read(await WorkerExecutionSnapshot.CaptureAsync(
                store, helper, configuration, tools, [], token, projectId: submission.ProjectId).ConfigureAwait(false));
            if (snapshot.Model is null)
                throw new AdmissionException("The selected helper has no admitted model for a typed thinking request.");
            if (requested is not null)
                _ = WorkerReasoningAdjustment.Resolve(snapshot.Profile, helper, requested,
                    snapshot.Profile.ContextTokens);
            if (budgetRequested || helper.TaskBudgetPolicy is not null)
            {
                var policy = helper.TaskBudgetPolicy ?? throw new AdmissionException("The selected helper has no owner task budget policy.");
                if (policy.SchemaVersion != 1)
                    throw new AdmissionException("The selected helper task budget policy has an unsupported version.");
                int? Field(string name) => binding.Input.TryGetProperty(name, out var value) ? value.GetInt32() : null;
                var contextCap = Field("maxContextTokens") ?? policy.MaxContextTokens ?? snapshot.Profile.ContextTokens;
                var share = Field("responseSharePercent");
                var outputCap = Field("maxResponseTokens") ?? policy.MaxResponseTokens ??
                    (share is null ? snapshot.Profile.ResponseTokens : Math.Min(131072, contextCap / 2));
                var context = Field("requestedContextTokens");
                var output = Field("requestedResponseTokens");
                var currentFit = helper.ModelWorkerId == "local" &&
                    store.Get<ModelRecord>("model", helper.ModelId) is { } localModel
                    ? localModel.ContextTokens : snapshot.Profile.ContextTokens;
                var defaultContext = policy.AllowAutonomousSelection && policy.MaxContextTokens is not null
                    ? Math.Min(currentFit, contextCap) : Math.Min(snapshot.Profile.ContextTokens, contextCap);
                // A typed child context is already fixed before generation. Percentage output
                // belongs to that selected window, not an unused larger profile default.
                var effectiveContext = context ?? defaultContext;
                var defaultOutput = share is { } percent ? (int)((long)effectiveContext * percent / 100)
                    : Math.Min(snapshot.Profile.ResponseTokens, outputCap);
                if (contextCap > (policy.MaxContextTokens ?? snapshot.Profile.ContextTokens) ||
                    outputCap > (policy.MaxResponseTokens ??
                        (share is null ? snapshot.Profile.ResponseTokens : Math.Min(131072, contextCap / 2))) ||
                    context is not null && policy.MaxContextTokens is null ||
                    output is not null && policy.MaxResponseTokens is null ||
                    output is not null && share is not null ||
                    context is { } selectedContext && selectedContext > contextCap ||
                    context is { } selectedFit && selectedFit > currentFit ||
                    output is { } selectedOutput && selectedOutput > outputCap ||
                    defaultOutput > outputCap ||
                    (output ?? defaultOutput) >= effectiveContext ||
                    (output ?? defaultOutput) > AgentTokenLimits.MaximumVisibleResponse(
                        effectiveContext, snapshot.Profile.ReasoningMode,
                        requested ?? (snapshot.Profile.ReasoningMode == "on" ? snapshot.Profile.ReasoningTokens : 0)))
                    throw new AdmissionException("The child task budget exceeds its own owner policy or assigned bounds.");
            }
        }
    }

    private PreparedAgentPlan CommitAgentPlan(WorkerWorkflows host, WorkflowCommandContext context,
        WorkflowRun parent, IReadOnlyList<AgentAssignment> planned, EngineController.DecisionLease decision)
    {
        // Scheduler admission reads engine state under StateStore. Acquire its gate first, but
        // open and commit the complete transaction inside the engine epoch fence. A failed SQLite
        // COMMIT must leave the decision uncommitted. Child admission happens after both gates release.
        return store.WithTransactionFreeGate(() => decision.Commit(committedDecision => store.Locked(() =>
        {
            var current = host.Store.Get(parent.Id);
            if (current.Revision != parent.Revision || current.CancelRequested)
                throw new WorkflowConflictException("Parent changed during agent planning.");
            (_, var latestProfile) = Resolve(context.Submission);
            var bindings = ValidateAssignments(context.Submission, latestProfile, planned);
            IReadOnlyList<OwnedConversationContext>? contexts = null;
            if (host.OwnedContexts.Owner(parent.Submission.ConversationId) is { Role: "coordinator" } coordinator)
            {
                var root = host.Store.GetConversation(ConversationWorkflowModule.DefinitionId, coordinator.MainConversationId) ??
                    throw new AdmissionException("The owning Main is unavailable.");
                host.OwnedContexts.Require(root.Id, root.ProfileId, root.ProjectId, coordinator.ContextId);
                contexts = planned.Select((item, index) => host.OwnedContexts.Reserve(root.Id, root.ProfileId,
                    root.ProjectId, item.ProfileId, bindings[index].DefinitionId, bindings[index].DefinitionVersion,
                    "worker", item.WorkerName ?? (item.WorkerContextId is { } handle
                        ? host.OwnedContexts.Require(root.Id, root.ProfileId, root.ProjectId, handle).DisplayName
                        : "Worker"), item.WorkerContextId)).ToArray();
            }
            var plan = new PreparedAgentPlan(parent.Id, planned.ToArray(), bindings, contexts);
            store.Put("coordination-plan", context.EffectId, plan);
            return plan;
        })));
    }

    private async Task<WorkflowCommandResult> DelegateAsync(WorkflowCommandContext context, bool reconcile, CancellationToken token)
    {
        var host = workflowHost?.Invoke() ?? throw new AdmissionException("Coordination host is unavailable.");
        var parentId = context.RunId ?? throw new AdmissionException("Host parent identity is required.");
        var key = "coordination-plan";
        var prepared = store.Get<PreparedAgentPlan>(key, context.EffectId);
        var ids = new List<string>();
        CoordinationWorkflowBinding BindingAt(PreparedAgentPlan plan, int index) =>
            plan.Workflows?[index] ?? new(ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                context.Submission.ProjectId is { } project ? Element(new { projectId = project }) : WorkflowContract.EmptyObject);
        try
        {
            var (_, profile) = Resolve(context.Submission);
            var requested = Read<AgentAssignments>(context.Arguments).Assignments;
            var preflightedHere = false;
            if (prepared is null)
            {
                if (reconcile) return new(WorkflowEffectState.Failed, Error: "No committed delegation plan exists; no children were admitted.");
                var requestedBindings = ValidateAssignments(context.Submission, profile, requested);
                // Model resolution can involve a remote worker. Finish it before holding the
                // engine decision lease; the plan below may only select these exact requests.
                await ValidateRequestedReasoningPoliciesAsync(context.Submission, requested,
                    requestedBindings, token).ConfigureAwait(false);
                if (host.Store.ChildRunIds(parentId).Count + requested.Count > 64)
                    throw new AdmissionException("This parent has reached its bounded total child limit.");
                var parent = host.Store.Get(parentId);
                using var decision = await host.Engine!.EvaluateAsync(parent.Id, parent.Revision,
                    "cerebral.agents.plan", 1, Element(new { assignments = requested }),
                    _ => throw new InvalidOperationException("Agent planning has no host feature fallback."), token).ConfigureAwait(false);
                if (decision.Decision.Payload.TryGetProperty("rejectedReason", out var rejected))
                    throw new AdmissionException(rejected.ValueKind == JsonValueKind.String && rejected.GetString() is { Length: > 0 and <= 2048 } reason
                        ? reason : "The engine rejected this delegation plan.");
                var planned = Read<AgentAssignments>(decision.Decision.Payload).Assignments;
                (_, profile) = Resolve(context.Submission);
                ValidateAssignments(context.Submission, profile, planned);
                if (!RequestedMultiplicityContains(requested, planned))
                    throw new AdmissionException("Engine plan selected a helper outside this requested wave.");
                preflightedHere = true;
                prepared = CommitAgentPlan(host, context, parent, planned, decision);
            }
            if (prepared.ParentRunId != parentId) throw new AdmissionException("Delegation receipt belongs to another parent.");
            if (prepared.Contexts is not null && prepared.Contexts.Count != prepared.Assignments.Count)
                throw new AdmissionException("The committed worker context bindings are invalid.");
            if (prepared.Workflows is not null && prepared.Workflows.Count != prepared.Assignments.Count)
                throw new AdmissionException("The committed helper workflow bindings are invalid.");
            var validated = false;
            if (!preflightedHere && !reconcile && Enumerable.Range(0, prepared.Assignments.Count)
                .Any(index => host.Store.TryGet("child-" + context.EffectId + "-" + index) is null))
            {
                (_, profile) = Resolve(context.Submission);
                var currentBindings = ValidateAssignments(context.Submission, profile, prepared.Assignments);
                for (var index = 0; index < currentBindings.Count; index++)
                {
                    var frozen = BindingAt(prepared, index);
                    if (currentBindings[index].DefinitionId != frozen.DefinitionId ||
                        currentBindings[index].DefinitionVersion != frozen.DefinitionVersion ||
                        !JsonElement.DeepEquals(currentBindings[index].Input, frozen.Input))
                        throw new WorkflowConflictException("The selected helper workflow changed after planning. Restore its accepted binding.");
                }
                await ValidateRequestedReasoningPoliciesAsync(context.Submission, prepared.Assignments,
                    currentBindings, token).ConfigureAwait(false);
                validated = true;
            }
            // Reconciliation retrieves exact existing child identities. It never creates missing work.
            for (var index = 0; index < prepared.Assignments.Count; index++)
            {
                var id = "child-" + context.EffectId + "-" + index;
                if (host.Store.TryGet(id) is { } existing)
                {
                    if (existing.Submission.ParentRunId != parentId ||
                        prepared.Contexts is not null && existing.Submission.ConversationId != prepared.Contexts[index].ConversationId)
                        throw new AdmissionException("Child identity belongs to another lineage or context.");
                    ids.Add(id); continue;
                }
                if (reconcile) return new(WorkflowEffectState.Unknown, Error: "Delegation admission was partial. Existing children are retained; missing work was not replayed.");
                token.ThrowIfCancellationRequested();
                (_, profile) = Resolve(context.Submission);
                if (!validated)
                {
                    var currentBindings = ValidateAssignments(context.Submission, profile, prepared.Assignments);
                    for (var i = 0; i < currentBindings.Count; i++)
                    {
                        var frozen = BindingAt(prepared, i);
                        if (currentBindings[i].DefinitionId != frozen.DefinitionId ||
                            currentBindings[i].DefinitionVersion != frozen.DefinitionVersion ||
                            !JsonElement.DeepEquals(currentBindings[i].Input, frozen.Input))
                            throw new WorkflowConflictException("The selected helper workflow changed after planning. Restore its accepted binding.");
                    }
                    validated = true;
                }
                var assignment = prepared.Assignments[index];
                var binding = BindingAt(prepared, index);
                var conversationId = id;
                if (prepared.Contexts is { } contexts)
                {
                    var frozen = contexts[index];
                    var root = host.Store.GetConversation(ConversationWorkflowModule.DefinitionId, frozen.MainConversationId) ??
                        throw new AdmissionException("The owning Main is unavailable.");
                    var currentContext = host.OwnedContexts.Require(root.Id, root.ProfileId, root.ProjectId, frozen.ContextId);
                    if (currentContext.ConversationId != frozen.ConversationId || currentContext.ProfileId != assignment.ProfileId ||
                        currentContext.DefinitionId != binding.DefinitionId || currentContext.DefinitionVersion != binding.DefinitionVersion)
                        throw new AdmissionException("The committed worker context identity changed.");
                    conversationId = frozen.ConversationId;
                }
                // Normal admission applies this helper's own policy and time limit independently of the parent.
                var child = await host.SubmitChildAsync(new(id, conversationId, assignment.ProfileId,
                    binding.DefinitionId, binding.DefinitionVersion, assignment.Instruction,
                    "Complete only this bounded assignment with your own role and context. Return a compact result with actual evidence and unresolved findings. Do not infer the parent transcript or delegate recursively.", binding.Input,
                    WorkflowContract.MaximumSteps), parentId, assignment.ProfileRevision, token).ConfigureAwait(false);
                ids.Add(child.Id);
            }
            var result = new Dictionary<string, object> { ["runIds"] = ids };
            if (prepared.Contexts is not null)
                result["workerContexts"] = prepared.Contexts.Select(x => new { x.ContextId, x.DisplayName }).ToArray();
            return new(WorkflowEffectState.Succeeded, Element(result), ProviderReceiptId: context.EffectId);
        }
        catch (Exception error) when (IsOrdinary(error))
        {
            // Once a child may exist, an unconfirmed wave cannot be reported as an ordinary failed retry.
            var any = prepared is not null && Enumerable.Range(0, prepared.Assignments.Count)
                .Any(index => host.Store.TryGet("child-" + context.EffectId + "-" + index) is not null);
            return new(any ? WorkflowEffectState.Unknown : token.IsCancellationRequested ? WorkflowEffectState.Cancelled : WorkflowEffectState.Failed,
                Error: any ? "Partial delegation retained for reconciliation: " + error.Message : error.Message,
                ProviderReceiptId: prepared is null ? null : context.EffectId);
        }
    }

    internal static bool RequestedMultiplicityContains(IReadOnlyList<AgentAssignment> requested,
        IReadOnlyList<AgentAssignment> planned)
    {
        var remaining = requested.GroupBy(item => (item.ProfileId, item.ProfileRevision,
                item.Instruction, item.WorkerContextId, item.WorkerName, item.RequestedReasoningTokens, item.RequestedContextTokens,
                item.RequestedResponseTokens, item.MaxContextTokens, item.MaxResponseTokens))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var item in planned)
        {
            var key = (item.ProfileId, item.ProfileRevision, item.Instruction, item.WorkerContextId, item.WorkerName, item.RequestedReasoningTokens,
                item.RequestedContextTokens, item.RequestedResponseTokens, item.MaxContextTokens, item.MaxResponseTokens);
            if (!remaining.TryGetValue(key, out var count) || count == 0) return false;
            remaining[key] = count - 1;
        }
        return true;
    }
}
