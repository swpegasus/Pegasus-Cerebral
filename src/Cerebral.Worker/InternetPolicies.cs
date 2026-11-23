using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Tools;

namespace Cerebral.Worker;

internal static class InternetJson
{
    private static readonly JsonSerializerOptions Strict = new(CerebralJson.Options)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
      RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 16 };
    internal static T Read<T>(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > 65536)
            throw new ArgumentException("Internet policy input must be a bounded object.");
        Check(value);
        return value.Deserialize<T>(Strict) ?? throw new ArgumentException("Internet policy input is empty.");
    }
    private static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { if (!names.Add(field.Name)) throw new ArgumentException("Duplicate Internet input field."); Check(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) Check(child);
    }
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options))).ToLowerInvariant();
}

/// <summary>Owner policy and exact no-send consent requests in the existing StateStore transaction boundary.</summary>
internal sealed class InternetPolicies
{
    private const string GlobalKind = "internet-policy", MainKind = "internet-main", RequestKind = "internet-request",
        DecisionKind = "internet-decision", ArchiveKind = "internet-request-archive";
    private readonly StateStore store; private readonly Configuration configuration;
    private readonly WorkerWorkflows workflows; private readonly EventHub events;
    private readonly Func<RegisteredToolCatalog> catalog;
    private sealed record DecisionReceipt(InternetDecisionRequest Input, InternetApprovalRequest Request);
    internal InternetPolicies(StateStore store, Configuration configuration, WorkerWorkflows workflows,
        EventHub events, Func<RegisteredToolCatalog> catalog)
    { this.store = store; this.configuration = configuration; this.workflows = workflows; this.events = events; this.catalog = catalog; }
    private T? Stored<T>(string kind, string id) where T : class
    {
        var raw = store.Get<JsonElement>(kind, id);
        return raw.ValueKind == JsonValueKind.Undefined ? null : InternetJson.Read<T>(raw);
    }
    private string Installation => configuration.Get().InstallationId;
    internal static bool Managed(string name) => InternetAuthorityContract.ManagedToolIds.Contains(name, StringComparer.Ordinal);
    internal InternetGlobalSnapshot Global() => store.Locked(() =>
    {
        var registered = catalog();
        var tools = registered.Tools.Where(x => Managed(x.Descriptor.Name))
            .Select(x => new InternetToolCatalogItem(x.Descriptor.Name, x.Descriptor.Capability, x.SourceFingerprint, x.Source)).ToArray();
        var policy = Stored<InternetGlobalPolicy>(GlobalKind, "global") ??
            new(1, Installation, 0, "ask", new(), tools.Select(x => new InternetToolRule(x.ToolId, x.DescriptorFingerprint)).ToArray(),
                null, "default-ask");
        Validate(policy);
        return new InternetGlobalSnapshot(InternetAuthorityContract.Id, 1, policy, InternetJson.Hash(policy),
            registered.Generation, tools);
    });
    private void Validate(InternetGlobalPolicy policy)
    {
        if (policy.SchemaVersion != 1 || policy.WorkerInstallationId != Installation || policy.Revision < 0 ||
            policy.Mode is not ("off" or "ask" or "allow") || policy.Revision > 0 && policy.UpdatedAt is null ||
            policy.AllowedTools is null || policy.AllowedTools.Any(x => x is null) || policy.AllowedTools.Count > InternetAuthorityContract.ManagedToolIds.Count ||
            policy.AllowedTools.Select(x => x.ToolId).Distinct(StringComparer.Ordinal).Count() != policy.AllowedTools.Count ||
            policy.AllowedTools.Any(x => !Managed(x.ToolId) || !InternetPolicyRules.Hex(x.DescriptorFingerprint)))
            throw new InvalidDataException("The stored global Internet authority is invalid.");
        InternetPolicyRules.Validate(policy.Limits);
    }
    private InternetMainPolicy ReadMain(string mainId)
    {
        var policy = Stored<InternetMainPolicy>(MainKind, mainId) ??
            new(1, Installation, mainId, 0, "inherit", null, null, null, null, null, "inherited");
        if (policy.SchemaVersion != 1 || policy.WorkerInstallationId != Installation || policy.MainConversationId != mainId ||
            policy.Revision < 0 || policy.Choice is not ("inherit" or "ask" or "allow" or "deny") ||
            policy.Revision > 0 && policy.UpdatedAt is null ||
            policy.Choice == "allow" && (policy.ApprovedGlobalRevision is null or < 0 ||
                policy.ApprovedGlobalFingerprint is not { } hash || !InternetPolicyRules.Hex(hash)) ||
            policy.Choice != "allow" && (policy.ApprovedGlobalRevision is not null || policy.ApprovedGlobalFingerprint is not null) ||
            policy.AllowedTools is { } ids && (ids.Count > 6 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count || ids.Any(x => !Managed(x))))
            throw new InvalidDataException("The stored Main Internet authority is invalid.");
        if (policy.Limits is not null) InternetPolicyRules.Validate(policy.Limits);
        return policy;
    }
    internal InternetMainSnapshot Main(string mainId) => store.Locked(() =>
    {
        var root = workflows.InternetRoot(mainId); var global = Global(); var main = ReadMain(mainId);
        var effective = InternetPolicyRules.Intersect(global.Policy.Limits, main.Limits);
        var tools = global.Policy.AllowedTools.Where(rule =>
            (main.AllowedTools is null || main.AllowedTools.Contains(rule.ToolId, StringComparer.Ordinal)) &&
            global.ManagedTools.Any(tool => tool.ToolId == rule.ToolId && tool.DescriptorFingerprint == rule.DescriptorFingerprint)).ToArray();
        var state = global.Policy.Mode == "off" || main.Choice == "deny" || root.AdmissionClosed ? "denied" :
            main.Choice == "allow" && main.ApprovedGlobalRevision == global.Policy.Revision &&
                main.ApprovedGlobalFingerprint == global.PolicyFingerprint ? "allowed" :
            main.Choice == "ask" || main.Choice == "allow" || global.Policy.Mode == "ask" ? "ask" : "allowed";
        string? reason = global.Policy.Mode == "off" ? "internet-global-off" :
            main.Choice == "deny" ? "internet-main-denied" : root.AdmissionClosed ? "internet-main-closed" :
            state == "ask" ? main.Choice == "allow" ? "internet-approval-stale" : "internet-owner-approval-required" : null;
        if (tools.Length == 0) { state = "denied"; reason = "internet-no-current-managed-tool"; }
        if (effective.AllowedDomains is { Count: 0 }) { state = "denied"; reason = "internet-no-allowed-domain"; }
        return new InternetMainSnapshot(InternetAuthorityContract.Id, 1, Installation, mainId, root.Revision,
            global.Policy, global.PolicyFingerprint, main, state, reason, effective, tools, global.CatalogGeneration, global.ManagedTools);
    });
    internal InternetGlobalSnapshot SaveGlobal(InternetGlobalSaveRequest input)
    {
        var result = store.Locked(() =>
        {
            var current = Global();
            if (input.ExpectedRevision != current.Policy.Revision || input.ExpectedCatalogGeneration != current.CatalogGeneration)
                throw new InvalidOperationException("The global Internet authority or managed tool catalog changed. Refresh before saving.");
            if (input.Mode is not ("off" or "ask" or "allow")) throw new ArgumentException("Invalid Internet mode.");
            InternetPolicyRules.Validate(input.Limits);
            if (input.AllowedTools is null || input.AllowedTools.Any(x => x is null) || input.AllowedTools.Count > 6 ||
                input.AllowedTools.Select(x => x.ToolId).Distinct(StringComparer.Ordinal).Count() != input.AllowedTools.Count ||
                input.AllowedTools.Any(rule => !current.ManagedTools.Any(tool => tool.ToolId == rule.ToolId &&
                    tool.DescriptorFingerprint == rule.DescriptorFingerprint)))
                throw new ArgumentException("Select exact current managed Internet tools.");
            var policy = new InternetGlobalPolicy(1, Installation, checked(current.Policy.Revision + 1), input.Mode,
                input.Limits, input.AllowedTools.ToArray(), DateTimeOffset.UtcNow, "installation-owner/configuration");
            Validate(policy); store.Put(GlobalKind, "global", policy); return Global();
        });
        InternetRuntime.Invalidate(store); events.Publish("internet.policy.updated", null,
            new { result.Policy.Revision, scope = "global", contractId = InternetAuthorityContract.Id }); return result;
    }
    internal InternetMainSnapshot SaveMain(InternetMainSaveRequest input)
    {
        var result = store.Locked(() =>
        {
            var current = Main(input.MainConversationId);
            Require(input.ExpectedGlobalRevision, input.ExpectedGlobalFingerprint, input.ExpectedRevision,
                input.ExpectedRootRevision, current);
            if (input.ExpectedCatalogGeneration != current.CatalogGeneration ||
                input.Choice is not ("inherit" or "ask" or "allow" or "deny"))
                throw new InvalidOperationException("The Internet review changed or contains an unsupported choice.");
            InternetPolicyRules.Narrow(current.GlobalPolicy.Limits, input.Limits);
            if (input.AllowedTools is not null && (input.AllowedTools.Count > 6 ||
                input.AllowedTools.Distinct(StringComparer.Ordinal).Count() != input.AllowedTools.Count ||
                input.AllowedTools.Except(current.GlobalPolicy.AllowedTools.Select(x => x.ToolId), StringComparer.Ordinal).Any()))
                throw new ArgumentException("Main tools may only restrict the current global set.");
            if (input.Choice == "allow" && current.GlobalPolicy.Mode == "off")
                throw new UnauthorizedAccessException("Global Internet Off cannot be overridden by a chat.");
            var policy = new InternetMainPolicy(1, Installation, input.MainConversationId, checked(current.Policy.Revision + 1),
                input.Choice, input.Limits, input.AllowedTools?.ToArray(), input.Choice == "allow" ? current.GlobalPolicy.Revision : null,
                input.Choice == "allow" ? current.GlobalPolicyFingerprint : null, DateTimeOffset.UtcNow, "installation-owner/chat-properties");
            store.Put(MainKind, input.MainConversationId, policy); return Main(input.MainConversationId);
        });
        InternetRuntime.Invalidate(store, input.MainConversationId);
        events.Publish("internet.policy.updated", null, new { mainConversationId = input.MainConversationId,
            revision = result.Policy.Revision, scope = "main", contractId = InternetAuthorityContract.Id }); return result;
    }
    private static void Require(long globalRevision, string fingerprint, long mainRevision, long rootRevision,
        InternetMainSnapshot current)
    {
        if (globalRevision != current.GlobalPolicy.Revision || fingerprint != current.GlobalPolicyFingerprint ||
            mainRevision != current.Policy.Revision || rootRevision != current.RootRevision)
            throw new InvalidOperationException("The exact global, Main or ownership revision changed. Refresh before deciding.");
    }
    internal ManagedInternetPolicyState RequireAllowed(ManagedInternetAuthority authority)
    {
        var current = Main(authority.MainConversationId);
        if (current.WorkerInstallationId != authority.WorkerInstallationId || current.EffectiveState != "allowed")
            throw new InternetAccessException(current.BlockedReason ?? "internet-denied", "Current owner Internet authority does not permit this request.");
        if (!current.EffectiveTools.Any(x => x.ToolId == authority.ToolName && x.DescriptorFingerprint == authority.ToolFingerprint))
            throw new InternetAccessException("internet-tool-denied", "The exact current network tool/provider is not permitted.");
        return new(current.GlobalPolicy.Limits, current.EffectiveLimits, current.GlobalPolicy.Revision, current.Policy.Revision);
    }
    internal InternetOwnerAction? Preflight(ManagedInternetAuthority authority, long rootRevision,
        string argumentsSha256, string? destinationOrigin)
    {
        InternetApprovalRequest? created = null;
        var action = store.Locked(() =>
        {
            var current = Main(authority.MainConversationId);
            if (current.RootRevision != rootRevision) throw new InternetAccessException("internet-owner-stale", "The owning Main changed before Internet admission.");
            if (!current.EffectiveTools.Any(x => x.ToolId == authority.ToolName && x.DescriptorFingerprint == authority.ToolFingerprint))
                throw new InternetAccessException("internet-tool-denied", "This exact managed network tool is outside the current owner ceiling.");
            if (destinationOrigin is not null && (!Uri.TryCreate(destinationOrigin, UriKind.Absolute, out var destination) ||
                !InternetPolicyRules.Allows(current.EffectiveLimits, destination)))
                throw new InternetAccessException("internet-domain-denied", "The destination is outside the owner Internet ceiling; no approval was requested.");
            if (current.EffectiveState == "allowed") return null;
            if (current.EffectiveState != "ask")
                throw new InternetAccessException(current.BlockedReason ?? "internet-denied", "The current owner Internet policy denies this operation.");
            var requestId = InternetJson.Hash(new { authority.RunId, authority.EffectId });
            var request = ReadRequest(requestId);
            if (request is null)
            {
                if (store.List<InternetApprovalRequest>(RequestKind).Count >= 512)
                    throw new InternetAccessException("internet-request-capacity", "The bounded Internet review ledger is full; review retained requests.");
                var digest = InternetJson.Hash(new { authority, argumentsSha256, rootRevision,
                    global = current.GlobalPolicy.Revision, current.GlobalPolicyFingerprint, main = current.Policy.Revision,
                    scope = "main-managed-internet", current.EffectiveLimits, current.EffectiveTools });
                request = new(1, requestId, 1, Installation, authority.MainConversationId, authority.ContextId,
                    authority.ConversationId, authority.RunId, authority.EffectId, authority.ToolName, authority.ToolFingerprint,
                    argumentsSha256, rootRevision, current.GlobalPolicy.Revision, current.GlobalPolicyFingerprint, current.Policy.Revision,
                    digest, "main-managed-internet", current.EffectiveLimits, current.EffectiveTools, destinationOrigin, "pending", DateTimeOffset.UtcNow);
                store.Put(RequestKind, requestId, request); created = request;
            }
            if (request.RunId != authority.RunId || request.EffectId != authority.EffectId ||
                request.ArgumentsSha256 != argumentsSha256 || request.MainConversationId != authority.MainConversationId ||
                request.ToolFingerprint != authority.ToolFingerprint || request.Status != "pending")
                throw new InternetAccessException("internet-request-stale", "The retained Internet review does not match this exact no-send attempt.");
            return new InternetOwnerAction(1, "internet-owner-approval", request.RequestId, request.RequestDigest,
                authority.RunId, authority.EffectId, authority.ToolName, argumentsSha256, true);
        });
        if (created is not null) events.Publish("internet.request.updated", authority.RunId,
            new { created.RequestId, created.MainConversationId, created.Revision, created.Status, contractId = InternetAuthorityContract.Id });
        return action;
    }
    internal static string Field(string requestId) => "internet-" + requestId;
    private InternetApprovalRequest? ReadRequest(string id) =>
        Stored<InternetApprovalRequest>(RequestKind, id) ?? Stored<InternetApprovalRequest>(ArchiveKind, id);
    internal InternetApprovalRequest Retire(InternetRequestRetireRequest input)
    {
        Configuration.ValidateId(input.RequestId);
        var result = store.Locked(() =>
        {
            var request = ReadRequest(input.RequestId) ?? throw new KeyNotFoundException("The Internet request was not found.");
            if (request.RequestDigest != input.RequestDigest) throw new InvalidOperationException("The request digest changed.");
            if (request.Status == "retired" && request.Revision == input.ExpectedRevision + 1) return request;
            if (request.Revision != input.ExpectedRevision || !workflows.Store.Get(request.RunId).IsTerminal)
                throw new InvalidOperationException("Only an exact terminal-source Internet request may retire; stop/finish its work first.");
            var archived = request with { Revision = checked(request.Revision + 1), Status = "retired", ResumeState = "source-terminal" };
            store.Put(ArchiveKind, request.RequestId, archived);
            store.Delete(RequestKind, request.RequestId); // Same transaction; the full original request is preserved in the archive.
            return archived;
        });
        events.Publish("internet.request.updated", result.RunId,
            new { result.RequestId, result.MainConversationId, result.Revision, result.Status, contractId = InternetAuthorityContract.Id });
        return result;
    }
    internal InternetApprovalRequest GetRequest(string id)
    {
        Configuration.ValidateId(id);
        return ProjectRequest(ReadRequest(id) ?? throw new KeyNotFoundException("The Internet request was not found."));
    }
    private InternetApprovalRequest ProjectRequest(InternetApprovalRequest request)
    {
        if (request.SchemaVersion != 1 || request.WorkerInstallationId != Installation ||
            !InternetPolicyRules.Hex(request.RequestId) || !InternetPolicyRules.Hex(request.RequestDigest))
            throw new InvalidDataException("The retained Internet request is invalid.");
        if (request.Status == "retired") return request with { ResumeState = "source-terminal" };
        var run = workflows.Store.Get(request.RunId);
        if (run.IsTerminal) return request with { ResumeState = "source-terminal" };
        // Review the exact top Main question, so its existing authenticated coordinator/worker relay
        // resumes the same child question chain. A direct child answer could strand its waiting parent.
        var ownerRun = workflows.InternetQuestionOwner(run.Submission, run.Id);
        var question = ownerRun.Questions;
        if (question is not null && question.Fields.Count == 1 && question.Fields[0].Id == Field(request.RequestId))
            return request with { QuestionRunId = request.QuestionRunId ?? ownerRun.Id,
                QuestionSetId = request.QuestionSetId ?? question.Id,
                QuestionRevision = request.QuestionRevision ?? question.Revision,
                ResumeState = question.Answers.ContainsKey(Field(request.RequestId)) ? "answered" :
                    ownerRun.Status == WorkflowStatus.Waiting && !ownerRun.CancelRequested ? "ready" : "not-ready" };
        return request with { ResumeState = run.IsTerminal || ownerRun.IsTerminal ? "source-terminal" : "not-ready" };
    }

    internal InternetRequestPage List(string mainId, string? after, int limit)
    {
        _ = workflows.InternetRoot(mainId);
        if (limit is < 1 or > 50 || after is not null && !InternetPolicyRules.Hex(after)) throw new ArgumentException("Invalid Internet request page.");
        var rows = store.List<InternetApprovalRequest>(RequestKind).Where(x => x.MainConversationId == mainId &&
            (after is null || StringComparer.Ordinal.Compare(x.RequestId, after) > 0)).OrderBy(x => x.RequestId, StringComparer.Ordinal).Take(limit + 1).ToArray();
        return new(InternetAuthorityContract.Id, 1, mainId, rows.Take(limit).Select(ProjectRequest).ToArray(),
            rows.Length > limit ? rows[limit - 1].RequestId : null);
    }
    internal InternetApprovalRequest Decide(InternetDecisionRequest input)
    {
        Configuration.ValidateId(input.DecisionId);
        var committed = store.Locked(() =>
        {
            var prior = Stored<DecisionReceipt>(DecisionKind, input.DecisionId);
            if (prior is not null)
            {
                if (InternetJson.Hash(prior.Input) != InternetJson.Hash(input)) throw new InvalidOperationException("The owner decision ID belongs to different input.");
                return (Request: prior.Request, Changed: false);
            }
            var request = GetRequest(input.RequestId); var current = Main(request.MainConversationId);
            if (request.Status != "pending" || request.Revision != input.ExpectedRequestRevision ||
                request.RequestDigest != input.RequestDigest || request.ResumeState != "ready" ||
                request.QuestionSetId != input.QuestionSetId || request.QuestionRevision != input.ExpectedQuestionRevision)
                throw new InvalidOperationException("The Internet request/question is stale or is not ready for owner review.");
            Require(input.ExpectedGlobalRevision, input.ExpectedGlobalFingerprint, input.ExpectedMainRevision, input.ExpectedRootRevision, current);
            if (request.GlobalRevision != current.GlobalPolicy.Revision || request.GlobalPolicyFingerprint != current.GlobalPolicyFingerprint ||
                !current.EffectiveTools.Any(x => x.ToolId == request.ToolName && x.DescriptorFingerprint == request.ToolFingerprint))
                throw new InvalidOperationException("The requested Internet scope changed. Review current Chat Properties; this request grants nothing.");
            // Another owner consent may have advanced this Main while siblings waited. The popup
            // still CASes the displayed CURRENT revisions; a retained request can only narrow its original scope.
            _ = InternetPolicyRules.Narrow(request.RequestedLimits, current.EffectiveLimits);
            if (current.EffectiveTools.Any(x => !request.RequestedTools.Contains(x)))
                throw new InvalidOperationException("The current Main scope exceeds this exact request; request fresh owner review.");
            // Revalidate exact effect ownership immediately before the owner record commits.
            var run = workflows.Store.Get(request.RunId);
            if (!run.State.TryGetProperty("pendingInternetRequirement", out var pendingRaw))
                throw new UnauthorizedAccessException("The source has no exact pending Internet requirement.");
            var pending = InternetJson.Read<InternetOwnerAction>(pendingRaw);
            if (pending.ApprovalRequestId != request.RequestId || pending.RequestDigest != request.RequestDigest ||
                pending.SourceRunId != request.RunId || pending.EffectId != request.EffectId ||
                pending.ArgumentsSha256 != request.ArgumentsSha256 || !pending.NoEffect)
                throw new UnauthorizedAccessException("The source pending Internet action does not match its request.");
            var identity = workflows.InternetAuthority(run.Submission, run.Id, request.EffectId, request.ToolName, request.ToolFingerprint, requireCurrentEffect: false);
            if (identity.Authority.MainConversationId != request.MainConversationId || identity.Authority.ContextId != request.ContextId)
                throw new UnauthorizedAccessException("The requester no longer belongs to the displayed Main.");
            if (input.Allowed && current.EffectiveState != "allowed")
            {
                if (current.GlobalPolicy.Mode == "off" || current.Policy.Choice == "deny")
                    throw new UnauthorizedAccessException("An Internet request cannot override an owner denial.");
                store.Put(MainKind, request.MainConversationId, current.Policy with {
                    Revision = checked(current.Policy.Revision + 1), Choice = "allow",
                    ApprovedGlobalRevision = current.GlobalPolicy.Revision, ApprovedGlobalFingerprint = current.GlobalPolicyFingerprint,
                    UpdatedAt = DateTimeOffset.UtcNow, Provenance = "installation-owner/internet-request/" + request.RequestId });
            }
            var decided = request with { Revision = checked(request.Revision + 1), Status = input.Allowed ? "approved" : "denied",
                DecisionId = input.DecisionId, Allowed = input.Allowed, QuestionSetId = input.QuestionSetId, QuestionRevision = input.ExpectedQuestionRevision, QuestionRunId = request.QuestionRunId };
            store.Put(RequestKind, request.RequestId, decided);
            store.Put(DecisionKind, input.DecisionId, new DecisionReceipt(input, decided)); return (Request: decided, Changed: true);
        });
        var saved = committed.Request;
        if (committed.Changed) InternetRuntime.Invalidate(store, saved.MainConversationId);
        // Different existing databases cannot commit atomically. The durable decision precedes an idempotent answer.
        // An identical decision retry resumes this same question; it never resends the original HTTP operation.
        try
        {
            workflows.Answer(new("internet-" + InternetJson.Hash(input.DecisionId), saved.QuestionRunId!,
                saved.QuestionSetId!, saved.QuestionRevision!.Value, new Dictionary<string, string> { [Field(saved.RequestId)] = "reviewed" }), null);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return saved with { ResumeState = "decision-saved-resume-pending" }; }
        events.Publish("internet.request.updated", saved.RunId,
            new { saved.RequestId, saved.MainConversationId, saved.Revision, saved.Status, contractId = InternetAuthorityContract.Id });
        return GetRequest(saved.RequestId);
    }
    internal bool Review(string requestId, string digest, string runId, string originalEffectId, string argumentsHash)
    {
        var request = ReadRequest(requestId) ?? throw new UnauthorizedAccessException("The Internet decision is unavailable.");
        if (request.RequestDigest != digest || request.RunId != runId || request.EffectId != originalEffectId ||
            request.ArgumentsSha256 != argumentsHash || request.Status != "approved" || request.Allowed != true)
            return false;
        var current = Main(request.MainConversationId);
        return current.EffectiveState == "allowed" &&
            current.EffectiveTools.Any(x => x.ToolId == request.ToolName && x.DescriptorFingerprint == request.ToolFingerprint) &&
            current.GlobalPolicy.Revision == request.GlobalRevision && current.GlobalPolicyFingerprint == request.GlobalPolicyFingerprint;
    }
}

/// <summary>One actual-send scheduler for this StateStore, shared by every Main and owned worker.</summary>
internal sealed class InternetRuntime
{
    private static readonly ConditionalWeakTable<StateStore, InternetRuntime> Instances = new();
    private readonly ManagedInternetBroker broker;
    private InternetRuntime(EventHub events, ToolDispatcher tools)
    {
        broker = new(_ => throw new InternetAccessException("internet-authority-missing", "A live host scope is required."),
            value => events.Publish("internet.activity", value.Authority.RunId,
            new { value.Authority.MainConversationId, value.Authority.RunId, value.Authority.EffectId, value.Authority.ToolName,
                value.State, value.ThrottledMilliseconds, value.Reason }));
        tools.OwnManagedInternetLifetime(broker);
    }
    internal static InternetRuntime For(StateStore store, EventHub events, ToolDispatcher tools) => Instances.GetValue(store, _ => new(events, tools));
    internal static void Invalidate(StateStore store, string? mainId = null)
    { if (Instances.TryGetValue(store, out var value)) value.broker.Invalidate(mainId); }
    internal IManagedInternetSession Open(ManagedInternetAuthority authority, Func<ManagedInternetPolicyState> resolve) =>
        broker.Open(authority, resolve);
}
