using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

public interface IAgentExecutor
{
    Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
    Func<bool>? ForegroundPending { get; set; }
    Task RunAsync(AgentJob job, CancellationToken token);
}

public sealed partial class JobScheduler(StateStore store, Configuration configuration, IAgentExecutor engine, ModelRuntime runtime, EventHub events, ILogger<JobScheduler> logger, WorkerWorkflows? workflows = null,
    IResourcePauseModelControl? resourceModels = null, IResourcePausePeerProbe? resourcePeers = null) : BackgroundService
{
    private const string PluginTestScanKey = "maintenance:plugin-tests";
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    private readonly ConcurrentDictionary<string, Task> executions = new();
    private CancellationToken workflowStopping;
    private SourceRecoveryFence? sourceRecoveryFence;

    internal void AttachSourceRecoveryFence(SourceRecoveryFence fence)
    {
        if (sourceRecoveryFence is not null) throw new InvalidOperationException("source_recovery_scheduler_already_bound");
        sourceRecoveryFence = fence;
    }
    private sealed class Steering { public string Id { get; set; } = ""; public string JobId { get; set; } = ""; public string Prompt { get; set; } = ""; public int Sequence { get; set; } public bool Delivered { get; set; } }
    private sealed record QuestionProjection(string SessionId, SessionMessage Message, bool Changed);
    public static bool IsActive(AgentJob job) => job.Status is "queued" or "running" or "waiting-human" or "waiting-helper" or "remote-waiting" or "remote-unconfirmed";
    public List<AgentJob> Queue() => store.ActiveJobs().OrderBy(Priority).ThenBy(x => x.CreatedAt).ToList();
    public PeerQueueMetrics QueueMetrics()
    {
        var active = store.ActiveJobs();
        var runningIds = running.Keys.Where(x => !x.StartsWith("workflow:", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var workflowRunning = running.Keys.Count(x => x.StartsWith("workflow:", StringComparison.Ordinal));
        var settings = configuration.Get();
        return new(settings.MaxConcurrentJobs, settings.ReservedOwnerSlots,
            workflowRunning + active.Count(x => x.PeerGrantId is null && runningIds.Contains(x.Id)),
            (workflows?.Store.ListReady().Count ?? 0) + active.Count(x => x.PeerGrantId is null && IsRunnable(x) && !runningIds.Contains(x.Id)),
            active.Count(x => x.PeerGrantId is not null && runningIds.Contains(x.Id)),
            active.Count(x => x.PeerGrantId is not null && IsRunnable(x) && !runningIds.Contains(x.Id)));
    }
    public List<AgentJob> History(int? limit = null, int? days = null)
    {
        var settings = configuration.Get();
        return store.JobHistory(limit ?? settings.JobHistoryLimit, days ?? settings.JobHistoryDays);
    }
    public bool ForegroundPending() => OwnerAdmissionPending();
    private int ForegroundRunningCount => running.Keys.Count(key => key != WorkerWorkflows.IdleCompactionExecutionKey);
    private bool OwnerAdmissionPending() => workflows?.Store.ListReadyPage(false, 1).Runs.Count > 0 ||
        store.ActiveJobs().Any(x => x.PeerGrantId is null && x.Kind != "background" && IsRunnable(x) && !running.ContainsKey(x.Id));
    public AgentJob Get(string id) => store.Get<AgentJob>("job", id) ?? throw new ArgumentException("Job not found.");
    private static void ValidateText(string value, int max, string label) { if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Contains('\0')) throw new ArgumentException($"{label} text is empty, too large, or contains NUL."); }
    private void Save(AgentJob job)
    {
        store.Locked(() => {
            var saved = store.Get<AgentJob>("job", job.Id);
            job.CancelRequested |= saved?.CancelRequested == true;
            if (!job.CancelRequested && job.Status == "waiting-human" && saved?.Status == "queued" &&
                !store.List<UserQuestion>("question").Any(x => x.JobId == job.Id && x.Required && x.Answer is null))
            { job.Status = "queued"; job.WaitingReason = "Answer received."; }
            if (saved is not null)
                foreach (var evidence in saved.Evidence)
                {
                    var current = job.Evidence.FirstOrDefault(x => x.Id == evidence.Id);
                    if (current is null) job.Evidence.Add(evidence);
                    else if (evidence.Resolution is not null) current.Resolution = evidence.Resolution;
                }
            job.Evidence = job.Evidence.OrderBy(x => x.At).TakeLast(100).ToList();
            var failedApplication = job.Evidence.LastOrDefault(x => x.Kind == "application-exit" && !x.Success && x.Resolution is null);
            if (failedApplication is not null)
            {
                job.Outcome = "failed"; job.Error = failedApplication.Summary;
                if (job.Status == "completed") job.Status = "failed";
            }
            store.Put("job", job.Id, job); return true;
        });
        events.Publish("job.updated", job.Id, job);
    }

    public AgentJob Submit(SubmitJobRequest request)
    {
        using var maintenance = workflows?.BeginForegroundAdmission();
        if (request.PeerGrantId is not null) throw new UnauthorizedAccessException("Peer ownership is assigned by the authenticated service endpoint.");
        return SubmitCore(request, null);
    }
    public AgentJob SubmitPeer(SubmitJobRequest request, PeerAccessGrant grant)
    {
        return WithResourceAdmission(paused =>
        {
            if (paused) throw new InvalidOperationException(PeerAccessControl.UnavailableAssignmentMessage);
            if (request.PeerGrantId is not null) throw new UnauthorizedAccessException("Peer ownership is assigned by the authenticated service endpoint.");
            return SubmitCore(request, grant.Id);
        });
    }
    private AgentJob SubmitCore(SubmitJobRequest request, string? requestedPeerGrantId) => store.Locked(() =>
    {
        var id = request.RequestId ?? Guid.NewGuid().ToString("N"); Configuration.ValidateId(id);
        AgentTokenLimits.ValidateResponseShare(request.ResponseSharePercent, nameof(request.ResponseSharePercent));
        if (store.Get<AgentJob>("cancelled-request", id) is not null) throw new InvalidOperationException("Request was cancelled before admission; use a new identifier for different work.");
        var existing = store.Get<AgentJob>("job", id);
        if (existing is not null)
        {
            var expectedPeerGrantId = requestedPeerGrantId ?? (request.ParentJobId is { } existingParentId
                ? store.Get<AgentJob>("job", existingParentId)?.PeerGrantId : null);
            var existingSession = store.GetSessionMetadata(request.SessionId ?? existing.SessionId);
            var requestedShare = request.ResponseSharePercent ?? existingSession?.ResponseSharePercent;
            if (existing.PeerGrantId != expectedPeerGrantId || existing.ProfileId != request.ProfileId || existing.Prompt != request.Prompt || existing.VisiblePrompt != request.VisiblePrompt ||
                existing.WorkflowSource != request.WorkflowSource || existing.Kind != request.Kind || existing.ParentJobId != request.ParentJobId ||
                existing.ResponseSharePercent != requestedShare || (request.SessionId is not null && existing.SessionId != request.SessionId) ||
                (request.ProjectId is not null && store.GetSessionMetadata(existing.SessionId)?.ProjectId != request.ProjectId))
                throw new ArgumentException("Request identifier already belongs to different work.");
            return existing;
        }
        string? inheritedProjectId = null;
        int? inheritedTurnSequence = null;
        int? inheritedResponseShare = null;
        string? inheritedPeerGrantId = null;
        if (request.ParentJobId is { } parentId)
        {
            var parent = store.Get<AgentJob>("job", parentId) ?? throw new ArgumentException("Parent assignment not found.");
            if (parent.CancelRequested || !IsActive(parent)) throw new InvalidOperationException("Parent assignment stopped; no further helpers may be admitted.");
            var parentSession = store.GetSessionMetadata(parent.SessionId);
            inheritedProjectId = parentSession?.ProjectId;
            inheritedTurnSequence = parent.RootTurnSequence ?? store.GetLatestUserRequestSequence(parent.SessionId) ?? parent.StartSequence;
            inheritedResponseShare = parent.ResponseSharePercent;
            inheritedPeerGrantId = parent.PeerGrantId;
            if (request.ProjectId is not null && request.ProjectId != inheritedProjectId) throw new ArgumentException("Helpers inherit the parent conversation project.");
        }
        var peerGrantId = requestedPeerGrantId ?? inheritedPeerGrantId;
        if (requestedPeerGrantId is not null && inheritedPeerGrantId is not null && requestedPeerGrantId != inheritedPeerGrantId)
            throw new UnauthorizedAccessException("A helper cannot change its paired-capacity owner.");
        if (request.Kind is not ("agent" or "background" or "compact")) throw new ArgumentException("Unknown job kind.");
        ValidateText(request.Prompt, 1_000_000, "Task");
        if ((request.VisiblePrompt is null) != (request.WorkflowSource is null))
            throw new ArgumentException("Private workflow context requires both visible user text and its plugin source.");
        if (request.VisiblePrompt is not null)
        {
            ValidateText(request.VisiblePrompt, 1_000_000, "Visible task");
            Configuration.ValidateId(request.WorkflowSource!);
        }
        var settings = configuration.Get();
        var activeJobs = store.ActiveJobs();
        PeerAccessGrant? peerGrant = null;
        if (peerGrantId is null)
        {
            if (activeJobs.Count(x => x.PeerGrantId is null) + (workflows?.Store.CountActive() ?? 0) >= settings.QueueCapacity)
                throw new InvalidOperationException("Owner queue is full. Finish or cancel existing assignments.");
        }
        else
        {
            peerGrant = store.Get<PeerAccessGrant>("peer-grant", peerGrantId) ?? throw new UnauthorizedAccessException("Peer grant is unavailable.");
            if (peerGrant.RevokedAt is not null || peerGrant.Draining || peerGrant.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
                throw new UnauthorizedAccessException("Peer grant no longer accepts new work.");
            if (!peerGrant.SharedProfileIds.Contains(request.ProfileId, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("The selected receiver profile is not shared by this peer grant.");
            if (activeJobs.Count(x => x.PeerGrantId is not null) >= settings.PeerQueueCapacity)
                throw new InvalidOperationException("Peer queue is full. Finish or cancel existing paired assignments.");
            if (request.ProjectId is not null) throw new UnauthorizedAccessException("Coordinator project identifiers are not portable peer inputs.");
        }
        var current = store.Get<AgentProfile>("agent", request.ProfileId) ?? throw new ArgumentException("Agent profile not found.");
        Configuration.ValidateProfile(current);
        if (peerGrantId is not null && (current.WorkerId != "local" || current.ModelWorkerId != "local"))
            throw new UnauthorizedAccessException("Paired assignments must execute one hop on a receiver-owned local profile.");
        var selectedProfile = request.DelegatedProfile is null ? current : RestrictDelegatedProfile(current, request.DelegatedProfile);
        var profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(selectedProfile));
        if (profile.WorkerId == "local" && profile.ModelWorkerId == "local")
        {
            var localModel = store.Get<ModelRecord>("model", profile.ModelId) ??
                throw new InvalidOperationException($"Agent '{profile.Name}' selects local model '{profile.ModelId}', but that model is not registered on this service.");
            ModelCapabilityPolicy.EnsureConfigured(profile, localModel);
            // Resolve only this detached admission before applying the response share below.
            // Later model growth must not enlarge an accepted ordinary, helper or compact job.
            var budget = AgentBudgetPolicy.Resolve(profile, localModel);
            profile.ContextTokens = budget.ContextTokens;
            profile.ResponseTokens = budget.ResponseTokens;
            profile.ReasoningMode = budget.ReasoningMode;
            profile.ReasoningTokens = budget.ReasoningTokens;
            profile.FollowsModelBudgets = false;
        }
        if (request.InitialMessages is not null && request.DelegatedProfile is null) throw new ArgumentException("Initial task context requires a delegated grant snapshot.");
        if (request.SessionId is not null) Configuration.ValidateId(request.SessionId);
        var session = request.SessionId is null ? null : store.GetSessionMetadata(request.SessionId);
        var isNewSession = session is null;
        var projects = new ProjectKnowledge(store, configuration, events);
        var projectId = request.ProjectId ?? inheritedProjectId;
        if (request.DelegatedProfile is not null && projectId is not null) throw new ArgumentException("Remote project bindings require an explicit worker-local mapping; coordinator project IDs are not accepted.");
        if (projectId is not null) projects.Project(projectId);
        if (session is not null && projectId is not null && session.ProjectId != projectId)
            throw new InvalidOperationException("This conversation belongs to a different project. Use the idle conversation Change project command first.");
        if (session is null)
        {
            if (request.SessionId is not null && request.DelegatedProfile is null) throw new ArgumentException("Session not found.");
            var titleText = request.VisiblePrompt ?? request.Prompt;
            session = new() { Id = request.SessionId ?? Guid.NewGuid().ToString("N"), ProfileId = profile.Id, ProjectId = projectId, Title = request.Title ?? titleText[..Math.Min(70, titleText.Length)] };
            if (request.InitialMessages is { } messages)
            {
                if (messages.Count > 1000 || Encoding.UTF8.GetByteCount(CerebralJson.Element(messages).GetRawText()) > 1_000_000 || (request.InitialCheckpoint?.Length ?? 0) > 32768) throw new ArgumentException("Delegated context exceeds the bounded transfer limit.");
                if (messages.Any(x => x.Role is not ("user" or "assistant") || x.Content.Contains('\0'))) throw new ArgumentException("Delegated context contains an invalid message.");
                session.Messages = messages.Where(x => !x.ExcludedFromContext).Select((x, index) => new SessionMessage { Sequence = index + 1, Role = x.Role, Content = x.Content, ToolName = x.ToolName, At = x.At, Origin = x.Origin }).ToList();
                session.Checkpoint = request.InitialCheckpoint;
            }
        }
        else if (request.InitialMessages is not null) throw new InvalidOperationException("Existing remote context cannot be overwritten by initial context.");
        if (session.ProfileId != profile.Id) throw new ArgumentException("This conversation belongs to a different agent profile.");
        if (store.ActiveJobs().Any(x => x.SessionId == session.Id)) throw new InvalidOperationException("This conversation has active or unconfirmed work. Reconcile or cancel it first.");
        var responseShare = request.ResponseSharePercent ?? session.ResponseSharePercent ?? inheritedResponseShare;
        AgentTokenLimits.ValidateResponseShare(responseShare, nameof(request.ResponseSharePercent));
        if (responseShare is { } share) profile.ResponseTokens = AgentTokenLimits.ResolveResponseShare(profile, share);
        session.ResponseSharePercent = responseShare;
        var visiblePrompt = request.VisiblePrompt ?? request.Prompt;
        var job = new AgentJob { Id = id, ProfileId = profile.Id, Profile = profile, SessionId = session.Id, Prompt = request.Prompt,
            VisiblePrompt = request.VisiblePrompt, WorkflowSource = request.WorkflowSource, ParentJobId = request.ParentJobId,
            RootTurnSequence = inheritedTurnSequence, Kind = request.Kind, WorkerId = profile.WorkerId, PeerGrantId = peerGrantId,
            WaitingReason = "Waiting for an execution slot.", ResponseSharePercent = responseShare };
        if (isNewSession)
        {
            if (request.Kind != "compact" && request.InitialMessages is null)
                session.Messages.Add(new() { Sequence = 1, Content = visiblePrompt });
            job.StartSequence = session.Messages.LastOrDefault(x => x.Role == "user" && x.Origin == "user")?.Sequence ?? 0;
            session.Revision++;
            store.Put("session", session.Id, session);
        }
        else if (request.Kind != "compact" && request.InitialMessages is null)
        {
            // Persist any accepted conversation setting first, then append one normalized message row. A long
            // conversation is never reconstructed merely to accept its next user turn.
            store.Put("session", session.Id, session);
            var appended = store.Append(session.Id, "user", visiblePrompt);
            session.Revision = appended.Revision;
            job.StartSequence = appended.Sequence;
        }
        else
        {
            job.StartSequence = store.GetLatestUserRequestSequence(session.Id) ?? 0;
            session.Revision++;
            store.Put("session", session.Id, session);
        }
        if (session.ProjectId is not null) projects.Project(session.ProjectId);
        if (isNewSession && request.ParentJobId is null && projectId is not null) projects.ValidateGrouping(session.Id, projectId);
        if (isNewSession && request.ParentJobId is null && projectId is not null) projects.GroupConversation(session);
        Save(job); events.Publish("session.updated", job.Id, new { id = session.Id }); return job;
    });

    public static AgentProfile RestrictDelegatedProfile(AgentProfile receiver, AgentProfile granted)
    {
        if (receiver.Id != granted.Id || receiver.WorkerId != "local" || granted.WorkerId != "local") throw new UnauthorizedAccessException("Delegation requires a matching worker profile with local execution.");
        var effective = CerebralJson.Read<AgentProfile>(CerebralJson.Element(receiver));
        effective.AllowedTools = IntersectTools(receiver.AllowedTools, granted.AllowedTools);
        effective.Capabilities = receiver.Capabilities.Intersect(granted.Capabilities, StringComparer.Ordinal).ToList();
        var compare = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (granted.AllowedRoots.Any(x => !receiver.AllowedRoots.Contains(x, compare))) throw new UnauthorizedAccessException("Delegated roots must match explicitly configured worker-local roots.");
        effective.AllowedRoots = granted.AllowedRoots.Distinct(compare).ToList();
        effective.MaxSteps = Math.Min(receiver.MaxSteps, granted.MaxSteps); effective.MaxSeconds = Math.Min(receiver.MaxSeconds, granted.MaxSeconds);
        Configuration.ValidateProfile(effective); return effective;
    }
    public static List<string> IntersectTools(IEnumerable<string> left, IEnumerable<string> right)
    {
        var a = left.ToList(); var b = right.ToList();
        if (a.Contains("*", StringComparer.Ordinal)) return b.Distinct(StringComparer.Ordinal).ToList();
        if (b.Contains("*", StringComparer.Ordinal)) return a.Distinct(StringComparer.Ordinal).ToList();
        return a.Intersect(b, StringComparer.Ordinal).ToList();
    }
    public AgentJob Cancel(string id) => store.Locked(() =>
    {
        Configuration.ValidateId(id);
        var job = store.Get<AgentJob>("job", id);
        if (job is null)
        {
            var tombstone = new AgentJob { Id = id, Status = "cancelled", CancelRequested = true, CancellationTombstone = true, FinishedAt = DateTimeOffset.UtcNow };
            store.Put("cancelled-request", id, tombstone); return tombstone;
        }
        // Children and presented applications can outlive a finished parent.
        StopPresentations(new HashSet<string>([job.Id], StringComparer.Ordinal));
        foreach (var child in store.List<AgentJob>("job").Where(x => x.ParentJobId == id)) Cancel(child.Id);
        if (!IsActive(job)) return job; job.CancelRequested = true;
        if (job.WorkerId != "local" && job.RemoteJobId is not null) { job.Status = "remote-unconfirmed"; job.NextReconcileAt = DateTimeOffset.UtcNow; job.RemoteFailures = 0; job.WaitingReason = "Remote cancellation requested; confirmation pending."; }
        else if (!running.ContainsKey(id)) { job.Status = "cancelled"; job.WaitingReason = null; job.FinishedAt = DateTimeOffset.UtcNow; }
        Save(job); if (running.TryGetValue(id, out var cts)) cts.Cancel();
        // Descendants were cancelled before updating this parent.
        return job;
    });
    public AgentJob Reconcile(string id) => store.Locked(() =>
    {
        var job = Get(id); if (job.WorkerId == "local" || job.RemoteJobId is null || !IsActive(job)) throw new InvalidOperationException("Only active remote work can be reconciled.");
        if (running.ContainsKey(id)) throw new InvalidOperationException("Remote reconciliation is already running.");
        job.RemoteFailures = 0; job.NextReconcileAt = DateTimeOffset.UtcNow; job.Status = "remote-unconfirmed"; job.WaitingReason = "Explicit remote reconciliation requested."; Save(job); return job;
    });
    public AgentJob Steer(string id, string prompt, string? requestId = null)
    {
        ValidateText(prompt, 1_000_000, "Steering");
        var snapshot = Get(id);
        var key = requestId ?? Guid.NewGuid().ToString("N"); Configuration.ValidateId(key); key = snapshot.Id + "." + key;
        var changed = false;
        var result = store.LinearizedSession(snapshot.SessionId, () => store.Locked(() =>
        {
            var job = Get(id);
            if (job.SessionId != snapshot.SessionId) throw new InvalidDataException("Job conversation identity changed.");
            if (!IsActive(job)) throw new InvalidOperationException("Submit a new message to continue a finished job.");
            var prior = store.Get<Steering>("steering", key);
            if (prior is not null)
            {
                if (prior.Prompt != prompt) throw new InvalidOperationException("Steering identifier belongs to different text.");
                return job;
            }
            var session = store.Append(job.SessionId, "user", prompt);
            store.Put("steering", key, new Steering { Id = key, JobId = id, Prompt = prompt, Sequence = session.Sequence, Delivered = job.WorkerId == "local" });
            if (job.WorkerId != "local" && job.RemoteJobId is not null) { job.NextReconcileAt = DateTimeOffset.UtcNow; Save(job); }
            changed = true;
            return job;
        }));
        if (changed) events.Publish("session.updated", id, new { id = result.SessionId });
        return result;
    }
    public UserQuestion Answer(string id, string answer)
    {
        ValidateText(answer, 100_000, "Answer");
        var snapshot = store.Get<UserQuestion>("question", id) ?? throw new ArgumentException("Question not found.");
        var askingJob = Get(snapshot.JobId);
        var linearizedSessions = new HashSet<string>(StringComparer.Ordinal) { snapshot.SessionId, RootJob(askingJob).SessionId };
        string? projectedSessionId = null; string? eventJobId = null; string? answeredSessionId = null;
        var questionChanged = false; var sessionChanged = false;
        var result = store.LinearizedSessions(linearizedSessions, () => store.Locked(() =>
        {
            var question = store.Get<UserQuestion>("question", id) ?? throw new ArgumentException("Question not found.");
            if (question.JobId != snapshot.JobId || question.SessionId != snapshot.SessionId)
                throw new InvalidDataException("Question ownership changed.");
            if (question.Answer is not null && !question.Assumed && question.Answer != answer)
                throw new InvalidOperationException("Question already answered. Send a correction to the conversation.");
            var job = Get(question.JobId); var active = IsActive(job);
            var root = RootJob(job);
            if (!linearizedSessions.Contains(root.SessionId))
                throw new InvalidDataException("Delegated question ancestry changed.");
            eventJobId = job.Id; answeredSessionId = question.SessionId;
            var newerActive = active ? null : FindActiveQuestionContinuation(job, root);
            if (newerActive is not null && question.Required)
                throw new InvalidOperationException("This required question belongs to finished work. Send the decision to the active assignment instead of applying it to the old operation.");

            if (question.Answer is null || question.Assumed)
            {
                question.Answer = answer;
                question.Assumed = false;
                if (question.RemoteQuestionId is not null) question.RemoteAnswerDelivered = false;
                store.Put("question", id, question); questionChanged = true;
            }

            var context = ProjectQuestionState(question, job, delegated: false);
            sessionChanged = context.Changed;
            var leadContext = ProjectDelegatedQuestionState(question, job);
            if (leadContext?.Changed == true) projectedSessionId = leadContext.SessionId;
            if (newerActive is not null)
            {
                // Optional answers enrich the currently active turn. A deterministic steering
                // record carries the same session message to a reused remote conversation.
                var delivery = newerActive.SessionId == context.SessionId ? context : leadContext ?? context;
                QueueQuestionContextForActiveJob(newerActive, question, delivery.Message);
            }
            else if (!active)
            {
                // Late enrichment belongs to the next turn. Never restart finished work or reuse stale remote context.
                ClearRemoteMappings(context.SessionId);
                if (leadContext is not null) ClearRemoteMappings(leadContext.SessionId);
            }
            else if (question.RemoteQuestionId is not null && !question.RemoteAnswerDelivered)
            { job.NextReconcileAt = DateTimeOffset.UtcNow; if (!running.ContainsKey(job.Id)) job.Status = "remote-waiting"; Save(job); }
            else if (job.Status == "waiting-human" && !store.List<UserQuestion>("question").Any(x => x.JobId == job.Id && x.Required && x.Answer is null)) { job.Status = "queued"; job.WaitingReason = "Answer received; waiting for a slot."; Save(job); }
            return question;
        }));
        if (questionChanged) events.Publish("question.updated", eventJobId, result);
        if (sessionChanged) events.Publish("session.updated", eventJobId, new { id = answeredSessionId });
        if (projectedSessionId is not null) events.Publish("session.updated", eventJobId, new { id = projectedSessionId });
        return result;
    }

    private AgentJob? FindActiveQuestionContinuation(AgentJob askingJob, AgentJob root)
    {
        return store.ActiveJobs().Where(x => x.Id != askingJob.Id &&
                (x.SessionId == askingJob.SessionId || (x.SessionId == root.SessionId && x.ParentJobId is null)))
            .OrderByDescending(x => x.SessionId == root.SessionId && x.ParentJobId is null)
            .ThenByDescending(x => x.CreatedAt).FirstOrDefault();
    }

    private void ClearRemoteMappings(string sessionId)
    {
        var session = store.GetSessionMetadata(sessionId) ?? throw new InvalidDataException("Question conversation is missing.");
        if (session.RemoteSessionIds.Count == 0) return;
        session.RemoteSessionIds.Clear(); store.Put("session", session.Id, session);
    }

    private AgentJob RootJob(AgentJob job)
    {
        var current = job; var visited = new HashSet<string>(StringComparer.Ordinal) { current.Id };
        while (current.ParentJobId is { } parentId)
        {
            if (!visited.Add(parentId)) throw new InvalidDataException("Delegated job ancestry contains a cycle.");
            current = store.Get<AgentJob>("job", parentId) ?? throw new InvalidDataException("Delegated question owner is missing its parent job.");
        }
        return current;
    }

    private QuestionProjection ProjectQuestionState(UserQuestion question, AgentJob askingJob, bool delegated)
    {
        if (question.Answer is null) throw new InvalidDataException("A question without an answer cannot be projected into conversation context.");
        var sessionId = delegated ? RootJob(askingJob).SessionId : question.SessionId;
        var session = store.Get<TaskSession>("session", sessionId) ?? throw new InvalidDataException("Question conversation is missing.");
        var asker = string.IsNullOrWhiteSpace(askingJob.Profile.Name) ? askingJob.ProfileId : askingJob.Profile.Name;
        var content = question.Assumed
            ? delegated
                ? $"Question from {asker}:\n{question.Prompt}\n\nService assumption:\n{question.Answer}\n\nThis is an assumption, not user approval; a later explicit answer replaces it."
                : $"Optional clarification proceeded with a service assumption.\nQuestion: {question.Prompt}\nAssumption: {question.Answer}\nThis is an assumption, not user approval; a later explicit answer replaces it."
            : delegated
                ? $"Question from {asker}:\n{question.Prompt}\n\nYour answer:\n{question.Answer}"
                : "Answer to: " + question.Prompt + "\n" + question.Answer;
        var role = "user";
        var origin = question.Assumed ? "service" : "question-answer";
        var matches = session.Messages.Where(x => x.SourceQuestionId == question.Id).ToList();
        if (matches.Count > 1) throw new InvalidDataException("Question context contains duplicate source projections.");
        var existing = matches.SingleOrDefault();
        if (existing is not null)
        {
            if (existing.Role == role && existing.Origin == origin && existing.Content == content)
                return new(session.Id, existing, false);
            if (existing.Origin == "question-answer" && question.Assumed)
                throw new InvalidDataException("An explicit user answer cannot be replaced by a service assumption.");
            if (existing.Origin == "question-answer" && !question.Assumed)
                throw new InvalidDataException("Saved explicit question context does not match the recorded answer.");
            existing.Role = role; existing.Origin = origin; existing.Content = content; existing.At = DateTimeOffset.UtcNow;
            session.Revision++; store.Put("session", session.Id, session);
            return new(session.Id, existing, true);
        }
        var message = new SessionMessage
        {
            Sequence = session.Messages.Count == 0 ? 1 : session.Messages[^1].Sequence + 1,
            Role = role, Origin = origin, SourceQuestionId = question.Id, Content = content
        };
        session.Messages.Add(message); session.Revision++; store.Put("session", session.Id, session);
        return new(session.Id, message, true);
    }

    private void QueueQuestionContextForActiveJob(AgentJob activeJob, UserQuestion question, SessionMessage message)
    {
        if (activeJob.WorkerId == "local") return;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(question.Id))).ToLowerInvariant();
        var kind = question.Assumed ? "question-assumption" : "question-answer";
        var id = activeJob.Id + "." + kind + "." + digest;
        if (!question.Assumed)
        {
            var assumptionId = activeJob.Id + ".question-assumption." + digest;
            var pendingAssumption = store.Get<Steering>("steering", assumptionId);
            if (pendingAssumption is { Delivered: false }) store.Delete("steering", assumptionId);
        }
        var existing = store.Get<Steering>("steering", id);
        if (existing is null)
        {
            store.Put("steering", id, new Steering { Id = id, JobId = activeJob.Id, Prompt = message.Content, Sequence = message.Sequence });
        }
        else if (existing.JobId != activeJob.Id || existing.Prompt != message.Content || existing.Sequence != message.Sequence)
        {
            throw new InvalidDataException("Saved question answer delivery does not match the active assignment.");
        }
        if (activeJob.Status is "remote-waiting" or "remote-unconfirmed")
        {
            activeJob.NextReconcileAt = DateTimeOffset.UtcNow; Save(activeJob);
        }
    }

    private QuestionProjection? ProjectDelegatedQuestionState(UserQuestion question, AgentJob askingJob)
    {
        var current = RootJob(askingJob);
        if (current.SessionId == question.SessionId) return null;
        return ProjectQuestionState(question, askingJob, delegated: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // The explicit recovery host starts this service while admission remains closed. Even
        // startup recovery and maintenance must wait; the ordinary work hold is narrower.
        if (sourceRecoveryFence is { } fence) await fence.WaitForReleaseAsync(stop).ConfigureAwait(false);
        engine.SubmitHelper = Submit; engine.ForegroundPending = ForegroundPending;
        workflowStopping = stop;
        if (workflows is { } startupWorkflows)
            store.WithTransactionFreeGate(() =>
                startupWorkflows.Store.RecoverInterrupted(WorkflowIdleExpiryCutoff(DateTimeOffset.UtcNow)));
        foreach (var model in store.List<ModelRecord>("model")) { model.State = "unloaded"; store.Put("model", model.Id, model); }
        store.Locked(() =>
        {
            foreach (var job in store.ActiveJobs().Where(x => x.Status == "running"))
            {
                if (job.ExecutionStartedAt is { } start && job.WorkerId == "local") job.ActiveSeconds += Math.Clamp((DateTimeOffset.UtcNow - start).TotalSeconds, 0, job.Profile.MaxSeconds);
                job.ExecutionStartedAt = null; job.Status = job.WorkerId != "local" && job.RemoteJobId is not null ? "remote-unconfirmed" : "interrupted";
                job.Error = "Service stopped during execution. Inspect saved effects or reconcile remote work before continuing.";
                if (job.Status == "remote-unconfirmed") { job.NextReconcileAt = DateTimeOffset.UtcNow; job.RemoteFailures = 0; } Save(job);
            }
            return true;
        });
        var continuity = workflows?.NativeContinuity;
        var continuityRecovered = false;
        if (continuity is not null)
        {
            try { continuity.Recover(); continuityRecovered = true; }
            catch (Exception error) when (IsWorkflowRunnerFailure(error))
            {
                logger.LogError("Native Cortex continuity recovery disabled maintenance ({FailureKind}). Inspect the saved continuity status before restarting maintenance.",
                    WorkflowRunnerFailureKind(error));
            }
        }
        using var continuityStopping = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task<bool>? continuityTask = null;
        var continuityNextScan = DateTimeOffset.MinValue;
        var idleScan = DateTimeOffset.MinValue;
        var pluginTestScan = DateTimeOffset.MinValue;
        var nextWorkflowExpiryScan = DateTimeOffset.MinValue;
        var presentations = new InterfaceRequests(store, events);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    foreach (var pair in executions.Where(x => x.Value.IsCompleted)) executions.TryRemove(pair.Key, out _);
                    if (workflows?.Engine is { } decisionEngine && await decisionEngine.RecoverAsync(stop).ConfigureAwait(false))
                        events.Publish("engine.updated", null, decisionEngine.Status());
                    ExpirePeerAssignments(); ProcessQuestionTimeouts(); ResumePresentationFailures(); ResumeParents(); QueueBackground();
                    var now = DateTimeOffset.UtcNow;
                    if (workflows is { } activeWorkflows && now >= nextWorkflowExpiryScan)
                    {
                        nextWorkflowExpiryScan = now.AddMinutes(1);
                        var expired = store.WithTransactionFreeGate(() =>
                            WorkflowIdleExpiryCutoff(now) is { } cutoff
                                ? activeWorkflows.Store.ExpireIdleInterrupted(cutoff) : []);
                        foreach (var run in expired) activeWorkflows.NotifyWorkflowUpdated(run);
                        if (expired.Count > 0) nextWorkflowExpiryScan = now.AddSeconds(1);
                    }
                    var candidates = store.ActiveJobs().Where(IsRunnable).OrderBy(Priority).ThenBy(x => x.CreatedAt).ToArray();
                    foreach (var candidate in candidates.Where(x => x.PeerGrantId is null && Priority(x) <= 1)) StartLegacy(candidate, stop);
                    if (workflows is not null && DateTimeOffset.UtcNow - pluginTestScan >= TimeSpan.FromSeconds(2))
                    {
                        if (StartPluginTestScan(presentations, stop)) pluginTestScan = DateTimeOffset.UtcNow;
                    }
                    if (workflows is not null)
                        foreach (var resumed in workflows.Store.ResumeCompletedDependencies()) workflows.Publish(resumed);
                    if (OwnerAdmissionPending()) workflows?.InterruptIdleCompaction();
                    StartWorkflows(stop);
                    foreach (var candidate in candidates.Where(x => x.PeerGrantId is null && Priority(x) > 1)) StartLegacy(candidate, stop);
                    foreach (var candidate in FairPeerOrder(candidates.Where(x => x.PeerGrantId is not null))) StartLegacy(candidate, stop);
                    StartIdleCompaction(stop);
                    // Cortex maintenance has no model/queue slot and performs external work only in its own task.
                    if (continuityTask is { IsCompleted: true })
                    {
                        if (!await continuityTask.ConfigureAwait(false)) continuityNextScan = DateTimeOffset.UtcNow.AddSeconds(2);
                        continuityTask = null;
                    }
                    if (continuityRecovered && continuityTask is null && DateTimeOffset.UtcNow >= continuityNextScan && !stop.IsCancellationRequested)
                        continuityTask = Task.Run(() => RunNativeContinuityMaintenanceAsync(continuity!, continuityStopping.Token), CancellationToken.None);
                    if (DateTimeOffset.UtcNow - idleScan > TimeSpan.FromSeconds(10))
                    {
                        idleScan = DateTimeOffset.UtcNow;
                        await runtime.UnloadIdleAsync(stop,
                            id => workflows?.HasActiveLocalModelWork(id) == true,
                            workflows?.Engine);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Queue error: {Message}", ex.Message); events.Publish("error", null, new { message = ex.Message }); }
                try { await Task.Delay(250, stop); } catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try { continuityStopping.Cancel(); }
            catch (AggregateException error) { logger.LogError("Native Cortex continuity cancellation callback failed ({FailureKind}).", WorkflowRunnerFailureKind(error)); }
            foreach (var cts in running.Values)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* The operation just finished. */ }
                catch (AggregateException error) { logger.LogError("Execution cancellation callback failed ({FailureKind}).", WorkflowRunnerFailureKind(error)); }
            }
            var settling = executions.Values.ToList();
            if (continuityTask is not null) settling.Add(continuityTask);
            try { await Task.WhenAll(settling).ConfigureAwait(false); }
            finally { workflows?.DiscardIdleCompactionState(); }
        }
    }

    private async Task<bool> RunNativeContinuityMaintenanceAsync(NativeCortexContinuity continuity, CancellationToken stop)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(stop);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await continuity.ProcessNextAsync(bounded.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            logger.LogWarning("Native Cortex continuity maintenance reached its 30-second deadline. Inspect the saved continuity status before retrying any uncertain operation.");
            return false;
        }
        catch (Exception error) when (IsWorkflowRunnerFailure(error))
        {
            logger.LogError("Native Cortex continuity maintenance failed ({FailureKind}). Inspect the saved continuity status for the affected operation.",
                WorkflowRunnerFailureKind(error));
            return false;
        }
    }

    private void StartIdleCompaction(CancellationToken stop)
    {
        if (workflows is null) return;
        WithResourceAdmission(paused => store.Locked(() =>
        {
            if (IntentionalHoldBlocksAdmission() || workflows.Engine?.IsPaused == true ||
                paused && workflows.IdleCompactionUsesLocalModel || OwnerAdmissionPending() ||
                running.Keys.Any(key => key != WorkerWorkflows.IdleCompactionExecutionKey))
            { workflows.InterruptIdleCompaction(); return false; }
            if (running.Count >= configuration.Get().MaxConcurrentJobs ||
                executions.ContainsKey(WorkerWorkflows.IdleCompactionExecutionKey)) return false;
            var cancellation = workflows.TryClaimIdleCompaction(stop, paused);
            if (cancellation is null) return false;
            running[WorkerWorkflows.IdleCompactionExecutionKey] = cancellation;
            executions[WorkerWorkflows.IdleCompactionExecutionKey] = Task.Run(async () =>
            {
                _ = await workflows.RunIdleCompactionAsync(cancellation).ConfigureAwait(false);
                // Provider/runtime quarantine retains uncertain capacity. Global owner scheduling must
                // remain available to unrelated routes; this does not release any provider reservation.
                running.TryRemove(WorkerWorkflows.IdleCompactionExecutionKey, out _);
            }, CancellationToken.None);
            return true;
        }));
    }

    private bool StartPluginTestScan(InterfaceRequests presentations, CancellationToken stop)
    {
        if (workflows is null || executions.ContainsKey(PluginTestScanKey)) return false;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executions[PluginTestScanKey] = Task.Run(async () =>
        {
            await ready.Task.ConfigureAwait(false);
            try { await presentations.ProcessPluginTestsAsync(workflows, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogError("Plugin test continuation scan: {Message}", error.Message);
                events.Publish("error", null, new { message = "Plugin test continuation scan: " + error.Message });
            }
        }, CancellationToken.None);
        ready.SetResult();
        return true;
    }

    private static bool IsRunnable(AgentJob job) => job.Status == "queued" || (job.Status is "remote-waiting" or "remote-unconfirmed" && job.NextReconcileAt is { } next && next <= DateTimeOffset.UtcNow);
    private void ExpirePeerAssignments()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = store.List<PeerAccessGrant>("peer-grant")
            .Where(grant => grant.ExpiresAt is { } expiry && expiry <= now)
            .Select(grant => grant.Id).ToHashSet(StringComparer.Ordinal);
        if (expired.Count == 0) return;
        foreach (var job in store.ActiveJobs().Where(job => job.PeerGrantId is { } id && expired.Contains(id)).ToList())
            Cancel(job.Id);
    }
    private static IEnumerable<AgentJob> FairPeerOrder(IEnumerable<AgentJob> candidates)
    {
        var grants = candidates.GroupBy(x => x.PeerGrantId!, StringComparer.Ordinal)
            .Select(group => new Queue<AgentJob>(group.OrderBy(Priority).ThenBy(x => x.CreatedAt)))
            .OrderBy(queue => queue.Peek().CreatedAt).ToList();
        while (grants.Count > 0)
        {
            foreach (var queue in grants.ToArray())
            {
                yield return queue.Dequeue();
                if (queue.Count == 0) grants.Remove(queue);
            }
        }
    }
    private void StartLegacy(AgentJob candidate, CancellationToken stop)
    {
        if (workflows?.Engine?.IsPaused == true) return;
        var claim = Claim(candidate.Id, stop); if (claim is null) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executions[claim.Value.Job.Id] = Task.Run(async () => { await ready.Task; await ExecuteJobAsync(claim.Value.Job, claim.Value.Cancellation, stop); }, CancellationToken.None);
        ready.SetResult();
    }

    private void StartWorkflows(CancellationToken stop)
    {
        if (workflows is null || workflows.Engine?.IsPaused == true) return;
        // Separate Engine pages prevent an older page of background rows from
        // hiding later owner work. A bounded incomplete foreground scan never
        // grants its residual slots to background work.
        if (!StartWorkflowClass(false, stop)) return;
        if (ForegroundRunningCount < configuration.Get().MaxConcurrentJobs)
            StartWorkflowClass(true, stop);
    }

    private DateTimeOffset? WorkflowIdleExpiryCutoff(DateTimeOffset now)
    {
        if (IntentionalHoldBlocksAdmission() || workflows?.Engine?.IsPaused == true) return null;
        var days = store.ReadWorkflowIdleExpiryDays();
        return days > 0 ? now.AddDays(-days) : null;
    }

    private bool StartWorkflowClass(bool sourceBoundMarkerPresent, CancellationToken stop)
    {
        if (workflows is null) return true;
        long afterRow = 0;
        for (var pageNumber = 0; pageNumber < 4; pageNumber++)
        {
            var page = workflows.Store.ListReadyPage(sourceBoundMarkerPresent, 100, afterRow);
            foreach (var candidate in page.Runs)
            {
                if (sourceBoundMarkerPresent)
                {
                    try { workflows.RequireSourceBoundSchedulerCandidate(candidate); }
                    catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                        JsonException or KeyNotFoundException or FormatException or
                        UnauthorizedAccessException or ArgumentException)
                    {
                        try { workflows.Publish(workflows.Store.Hold(candidate.Id, candidate.Revision,
                            "The saved source-bound host marker is invalid. Repair the exact accepted run before resuming.")); }
                        catch (WorkflowConflictException) { }
                        continue;
                    }
                }
                if (TryStartWorkflowCandidate(candidate, stop) == WorkflowCandidateStart.CapacityBlocked)
                    return false;
                if (ForegroundRunningCount >= configuration.Get().MaxConcurrentJobs) return false;
            }
            if (page.NextAfterRow is not { } next) return true;
            afterRow = next;
        }
        return false;
    }

    private enum WorkflowCandidateStart { Started, ResourceIneligible, CapacityBlocked }

    private WorkflowCandidateStart TryStartWorkflowCandidate(WorkflowRun candidate, CancellationToken stop)
    {
        if (workflows is null) return WorkflowCandidateStart.ResourceIneligible;
            var key = "workflow:" + candidate.Id;
            var admitted = WithResourceAdmission<(WorkflowCandidateStart Outcome, CancellationTokenSource? Token)>(
                paused => store.Locked<(WorkflowCandidateStart Outcome, CancellationTokenSource? Token)>(() =>
            {
                if (ForegroundRunningCount >= configuration.Get().MaxConcurrentJobs)
                    return (WorkflowCandidateStart.CapacityBlocked, null);
                if (IntentionalHoldBlocksAdmission() || workflows.Engine?.IsPaused == true ||
                    paused && WorkflowUsesLocalGpu(candidate) || running.ContainsKey(key))
                    return (WorkflowCandidateStart.ResourceIneligible, null);
                var token = CancellationTokenSource.CreateLinkedTokenSource(stop);
                if (running.TryAdd(key, token))
                {
                    ObserveAcceptedWorkflow(key, token, candidate.Submission);
                    return (WorkflowCandidateStart.Started, token);
                }
                token.Dispose();
                return (WorkflowCandidateStart.ResourceIneligible, null);
            }));
            if (admitted.Token is not { } cts) return admitted.Outcome;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            executions[key] = Task.Run(async () =>
            {
                await ready.Task;
                try { workflows.Publish(await workflows.Runtime.RunAsync(candidate.Id, cts.Token)); }
                catch (Exception error) when (IsWorkflowRunnerFailure(error))
                { await SettleWorkflowRunnerExitAsync(candidate.Id, error).ConfigureAwait(false); }
                finally { store.Locked(() => { running.TryRemove(key, out _); ObserveRetiredExecution(key, cts); ReleaseIntentionalStop(key, cts); cts.Dispose(); return true; }); }
            }, CancellationToken.None);
            ready.SetResult();
            return WorkflowCandidateStart.Started;
    }

    private static bool IsWorkflowRunnerFailure(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static string WorkflowRunnerFailureKind(Exception error) => error is SqliteException sqlite
        ? $"SQLite error {sqlite.SqliteErrorCode} ({sqlite.SqliteExtendedErrorCode})"
        : error.GetType().Name;

    private async Task SettleWorkflowRunnerExitAsync(string id, Exception error)
    {
        if (workflows is null) return;
        var reason = $"Workflow runner exited before its saved state could be confirmed ({WorkflowRunnerFailureKind(error)}). " +
            "The existing operation was not repeated. Inspect its receipt before continuing.";
        logger.LogError("Workflow {WorkflowId}: {Message}", id, reason);
        string? effectId = null;
        var capturedEffect = false;
        Exception? persistenceError = null;
        // The handler has exited, but the scheduler retains its slot through this finite recovery
        // attempt. Retry only the state transition, using a fresh revision; never execute the effect.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var run = workflows.Store.Get(id);
                if (run.IsTerminal) return;
                if (capturedEffect && run.Effect?.Id != effectId)
                    throw new WorkflowConflictException("The pending operation changed before runner-exit recovery.");
                effectId = run.Effect?.Id; capturedEffect = true;
                workflows.Publish(workflows.Store.Fail(id, run.Revision, reason));
                return;
            }
            catch (Exception failure) when (failure is WorkflowConflictException ||
                failure is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                persistenceError = failure;
                if (attempt < 2)
                {
                    logger.LogWarning("Workflow {WorkflowId}: runner-exit recovery retry {Attempt} ({FailureKind}).",
                        id, attempt + 1, WorkflowRunnerFailureKind(failure));
                    await Task.Delay(100 * (attempt + 1)).ConfigureAwait(false);
                }
            }
            catch (Exception failure) when (IsWorkflowRunnerFailure(failure))
            { persistenceError = failure; break; }
        }
        var message = $"Workflow runner exited, but its recovery state could not be saved ({WorkflowRunnerFailureKind(persistenceError!)}). " +
            "The existing operation was not repeated. Inspect service storage and the saved receipt before restarting work.";
        logger.LogError("Workflow {WorkflowId}: {Message}", id, message);
        events.Publish("error", id, new { message, workflowId = id, effectId, recoveryPersisted = false });
    }

    public WorkflowRun CancelWorkflow(string id)
    {
        if (workflows is null) throw new InvalidOperationException("Workflow engine is unavailable.");
        // Persist the decision before notifying a live handler. The handler owns the actual effect outcome.
        var run = workflows.Store.Cancel(id);
        foreach (var cancelledId in workflows.Store.ChildRunIds(id).Prepend(id))
        {
            var token = store.Locked(() => running.TryGetValue("workflow:" + cancelledId, out var cts) ? cts : null);
            try { token?.Cancel(); } catch (ObjectDisposedException) { /* Execution finished after the lookup. */ }
        }
        return workflows.Publish(run);
    }

    public Task<WorkflowRun> ReconcileWorkflowAsync(string id, CancellationToken callerToken)
    {
        if (workflows is null) throw new InvalidOperationException("Workflow engine is unavailable.");
        var key = "workflow:" + id;
        var cts = AdmitWorkflowReconciliation(id, callerToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await ready.Task;
            try { return workflows.Publish(await workflows.Runtime.ReconcileAsync(id, cts.Token)); }
            catch (Exception error) when (IsWorkflowRunnerFailure(error))
            {
                await SettleWorkflowRunnerExitAsync(id, error).ConfigureAwait(false);
                throw;
            }
            finally { RetireWorkflowReconciliation(key, cts); }
        }, CancellationToken.None);
        executions[key] = task; ready.SetResult();
        return task;
    }

    private CancellationTokenSource AdmitWorkflowReconciliation(string id, CancellationToken callerToken)
    {
        if (workflows is null) throw new InvalidOperationException("Workflow engine is unavailable.");
        var key = "workflow:" + id;
        return WithResourceAdmission<CancellationTokenSource>(paused => store.Locked(() =>
        {
            workflowStopping.ThrowIfCancellationRequested(); callerToken.ThrowIfCancellationRequested();
            if (IntentionalHoldBlocksAdmission())
                throw new WorkflowConflictException("Intentional work hold blocks reconciliation admission; resolve its owner marker first.");
            var run = workflows.Store.Get(id);
            if (run.IsTerminal || run.Effect?.State != WorkflowEffectState.Unknown)
                throw new WorkflowConflictException("Only an uncertain effect can be reconciled.");
            if (paused && WorkflowUsesLocalGpu(run))
                throw new WorkflowConflictException("Local GPU admission is paused while this installation reserves its resources.");
            if (running.Count >= configuration.Get().MaxConcurrentJobs || running.ContainsKey(key))
                throw new WorkflowConflictException("No execution slot is available or this workflow is already being inspected. Query its state before retrying.");
            var token = CancellationTokenSource.CreateLinkedTokenSource(workflowStopping, callerToken);
            if (running.TryAdd(key, token)) { ObserveAcceptedWorkflow(key, token, run.Submission); return token; }
            token.Dispose(); throw new WorkflowConflictException("This workflow already has an execution slot.");
        }));
    }

    private void RetireWorkflowReconciliation(string key, CancellationTokenSource cts) =>
        store.Locked(() => { running.TryRemove(key, out _); ObserveRetiredExecution(key, cts); ReleaseIntentionalStop(key, cts); cts.Dispose(); return true; });

    public Task<WorkflowRun> ReconcileReceiptLookupAsync(WorkflowReceiptLookupRequest request,
        CancellationToken callerToken)
    {
        if (workflows is null) throw new InvalidOperationException("Workflow engine is unavailable.");
        // Refuse stale or non-file-edit requests before reserving a slot. Admission is rechecked
        // after reservation and at the engine's commit point because settings can change meanwhile.
        _ = workflows.RequireReceiptLookup(workflows.Store.Get(request.Id), request);
        var key = "workflow:" + request.Id;
        var cts = AdmitWorkflowReconciliation(request.Id, callerToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await ready.Task;
            try
            {
                var hint = workflows.RequireReceiptLookup(workflows.Store.Get(request.Id), request);
                using var scope = workflows.BeginReceiptLookup(hint);
                return workflows.Publish(await workflows.Runtime.ReconcileConfirmedAsync(request.Id,
                    request.ExpectedRevision, request.EffectId,
                    current => { _ = workflows.RequireReceiptLookup(current, request); }, cts.Token));
            }
            // This path may only inspect the exact job; a refusal never changes the saved Unknown
            // effect through the scheduler's normal runner-exit settlement.
            finally { RetireWorkflowReconciliation(key, cts); }
        }, CancellationToken.None);
        executions[key] = task; ready.SetResult();
        return task;
    }
    private (AgentJob Job, CancellationTokenSource Cancellation)? Claim(string id, CancellationToken stop) =>
        WithResourceAdmission<(AgentJob, CancellationTokenSource)?>(paused => store.Locked<(AgentJob, CancellationTokenSource)?>(() =>
        {
            if (IntentionalHoldBlocksAdmission() || workflows?.Engine?.IsPaused == true) return null;
            var job = Get(id); var settings = configuration.Get();
            if (!IsRunnable(job) || UsesLocalGpu(job) && paused || (job.PeerGrantId is null ? ForegroundRunningCount : running.Count) >= settings.MaxConcurrentJobs || running.ContainsKey(id) || (job.Kind == "background" && ForegroundPending())) return null;
            if (job.PeerGrantId is { } grantId)
            {
                if (OwnerAdmissionPending()) return null;
                var grant = store.Get<PeerAccessGrant>("peer-grant", grantId);
                if (grant is null || grant.RevokedAt is not null) return null;
                var peerRunningForGrant = running.Keys.Count(key => store.Get<AgentJob>("job", key)?.PeerGrantId == grantId);
                if (peerRunningForGrant >= Math.Max(1, grant.MaxConcurrentJobs)) return null;
            }
            if (job.WorkerId == "local" && (job.CancelRequested || job.ActiveSeconds >= job.Profile.MaxSeconds || job.StepCount >= job.Profile.MaxSteps))
            {
                job.Status = job.CancelRequested ? "cancelled" : "failed"; job.Error = job.CancelRequested ? "Cancelled before execution." : "Persistent execution budget exhausted."; job.FinishedAt = DateTimeOffset.UtcNow; Save(job); return null;
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stop); if (!running.TryAdd(id, cts)) { cts.Dispose(); return null; }
            ObserveAcceptedJob(job, cts);
            job.Status = "running"; job.WaitingReason = null; job.Error = null; job.ExecutionStartedAt = DateTimeOffset.UtcNow; Save(job); return (job, cts);
        }));
    private static int Priority(AgentJob job)
    {
        var local = job.CancelRequested ? -1 : job.Kind == "compact" ? 0 : job.Kind == "background" ? 3 : job.Profile.Role == "lead" ? 1 : 2;
        return job.PeerGrantId is null ? local : local + 10;
    }
    private static IDisposable EnterJobInferenceObservation(AgentJob job)
    {
        InferenceInvocation? invocation = null;
        try
        {
            // Use the accepted snapshot, never a later live profile revision or a fabricated workflow identity.
            // A legacy session identifier does not establish a workflow conversation coordinate.
            if (job.Profile.Id == job.ProfileId)
                invocation = new(job.Id, job.Kind == "compact" ? "compaction" : "task-generation",
                    new(job.Id, job.Profile.Id, job.Profile.Revision));
        }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { }
        return WorkerInferenceInvocation.Enter(invocation);
    }

    private async Task ExecuteJobAsync(AgentJob job, CancellationTokenSource cts, CancellationToken stop)
    {
        var watch = Stopwatch.StartNew(); if (job.WorkerId == "local") cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.001, job.Profile.MaxSeconds - job.ActiveSeconds)));
        try
        {
            if (job.WorkerId == "local")
            {
                // Capability acquisition may itself measure or prepare; keep it in this accepted invocation.
                using var invocation = EnterJobInferenceObservation(job);
                using var readiness = await runtime.AcquireRequiredCapabilitiesAsync(job.Profile, cts.Token);
                await engine.RunAsync(job, cts.Token);
            }
            else await ExecuteRemoteAsync(job, cts.Token);
            if (job.Status == "running") { job.Status = job.Outcome is "failed" or "incomplete" ? job.Outcome : "completed"; job.FinishedAt = DateTimeOffset.UtcNow; }
        }
        catch (OperationCanceledException)
        {
            var intentionallyHeld = IsIntentionalStop(job.Id, cts);
            if (job.WorkerId != "local" && job.RemoteJobId is not null)
            {
                job.CancelRequested |= Get(job.Id).CancelRequested;
                if (job.CancelRequested && !stop.IsCancellationRequested) await ConfirmRemoteCancellationAsync(job);
                else
                {
                    Unconfirmed(job, "Coordinator stopped or request interrupted; remote execution state is unconfirmed.");
                    if (intentionallyHeld) job.NextReconcileAt = null; // Explicit jobs.reconcile may inspect it after hold release.
                }
            }
            else { job.Status = stop.IsCancellationRequested || intentionallyHeld ? "interrupted" : "cancelled"; job.Error = "Execution stopped. Inspect interrupted tool effects before retrying."; job.FinishedAt = DateTimeOffset.UtcNow; }
        }
        catch (Exception ex)
        {
            if (job.WorkerId != "local" && job.RemoteJobId is not null) Unconfirmed(job, ex.Message); else { job.Status = "failed"; job.Error = ex.Message; job.FinishedAt = DateTimeOffset.UtcNow; }
        }
        finally
        {
            store.Locked(() =>
            {
                job.CancelRequested |= Get(job.Id).CancelRequested;
                if (job.WorkerId == "local") { job.ActiveSeconds += watch.Elapsed.TotalSeconds; if (job.CancelRequested) { job.Status = "cancelled"; job.FinishedAt = DateTimeOffset.UtcNow; } }
                if (job.Status == "waiting-human" && !store.List<UserQuestion>("question").Any(x => x.JobId == job.Id && x.Required && x.Answer is null)) { job.Status = "queued"; job.WaitingReason = "Answer received."; }
                job.ExecutionStartedAt = null; Save(job); running.TryRemove(job.Id, out _); ObserveRetiredExecution(job.Id, cts); ReleaseIntentionalStop(job.Id, cts); cts.Dispose(); return true;
            });
        }
    }
    private void ResumeParents() => store.Locked(() =>
    {
        foreach (var parent in store.ActiveJobs().Where(x => x.Status == "waiting-helper" && !running.ContainsKey(x.Id)))
        {
            var children = store.List<AgentJob>("job").Where(x => x.ParentJobId == parent.Id && !parent.ConsumedHelperJobIds.Contains(x.Id, StringComparer.Ordinal)).ToList();
            if (children.Count == 0 || children.Any(IsActive)) continue;
            foreach (var child in children)
            {
                var content = child.Result ?? child.Error ?? "No result was returned.";
                foreach (var item in child.Evidence)
                    if (!parent.Evidence.Any(x => x.Id == item.Id)) parent.Evidence.Add(item);
                var evidence = System.Text.Json.JsonSerializer.Serialize(child.Evidence.TakeLast(16), CerebralJson.Options);
                store.Append(parent.SessionId, "user", $"Helper {child.ProfileId}, job {child.Id}, state {child.Status}, outcome {child.Outcome ?? child.Status}:\n{content[..Math.Min(content.Length, 10000)]}\nService-recorded evidence (wrapper exits describe that wrapper only):\n{evidence}\nIntegrate these results. Complete remaining requested actions using your own tools and presentation capability; a helper's environment limitations do not remove the lead's capabilities.", origin: "helper");
                parent.ConsumedHelperJobIds.Add(child.Id);
            }
            parent.Status = "queued"; parent.WaitingReason = "Helper results received."; Save(parent);
        }
        return true;
    });
    private void QueueBackground() => store.Locked(() =>
    {
        var settings = configuration.Get(); if (!settings.BackgroundTasksEnabled || ForegroundPending() || store.ActiveJobs().Any(x => x.Status is "queued" or "running")) return false;
        var now = DateTimeOffset.UtcNow;
        var item = settings.BackgroundTasks.FirstOrDefault(x => x.Enabled && (x.NextRunAt is null || x.NextRunAt <= now) && (x.LastJobId is null || store.Get<AgentJob>("job", x.LastJobId) is not { } previous || !IsActive(previous)));
        if (item is null) return false;
        try { var job = Submit(new() { ProfileId = item.ProfileId, Prompt = item.Prompt, Title = item.Name, Kind = "background" }); job.Profile.MaxSeconds = Math.Min(job.Profile.MaxSeconds, item.MaxRunMinutes * 60); Save(job); item.LastJobId = job.Id; item.LastStartedAt = now; }
        catch (Exception ex) { events.Publish("error", null, new { message = "Background task " + item.Name + ": " + ex.Message }); }
        item.NextRunAt = now.AddMinutes(item.IntervalMinutes); configuration.Save(settings); return true;
    });

    private async Task ExecuteRemoteAsync(AgentJob job, CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token); attempt.CancelAfter(TimeSpan.FromSeconds(20));
        var worker = configuration.Get().Workers.FirstOrDefault(x => x.Id == job.WorkerId) ?? throw new ArgumentException("Assigned worker is not configured.");
        using var client = new CerebralClient(worker);
        if (job.RemoteSubmission?.DelegatedProfile is { } priorGrant)
        {
            var owner = store.Get<AgentProfile>("agent", job.ProfileId);
            if (owner is null || priorGrant.Capabilities.Any(x => !owner.Capabilities.Contains(x, StringComparer.Ordinal)) || priorGrant.AllowedTools.Any(x => !owner.AllowedTools.Contains("*", StringComparer.Ordinal) && !owner.AllowedTools.Contains(x, StringComparer.Ordinal)))
            {
                job.CancelRequested = true; job.WaitingReason = "Owning profile grants were revoked; requesting remote cancellation."; Save(job);
            }
        }
        if (job.CancelRequested) { await ConfirmRemoteCancellationAsync(job); return; }
        await ValidateRemoteIdentityAsync(client, worker, attempt.Token);
        if (job.RemoteSubmission is null)
        {
            var remoteProfileId = job.Profile.RemoteProfileId ?? job.ProfileId;
            AgentProfile grant;
            if (!string.IsNullOrWhiteSpace(worker.PeerToken))
            {
                var catalog = await client.GetAsync<PeerCatalog>("peer.catalog", cancellationToken: attempt.Token);
                var contribution = catalog.Agents.FirstOrDefault(x => x.Id == remoteProfileId) ?? throw new InvalidOperationException("The selected receiver profile is not shared by this peer.");
                // The receiver resolves its own model, tools and roots. The coordinator can only narrow the
                // contribution, and never serializes a Windows path as remote authority.
                grant = CerebralJson.Read<AgentProfile>(CerebralJson.Element(job.Profile));
                grant.Id = contribution.Id; grant.Name = contribution.Name; grant.Purpose = contribution.Purpose;
                grant.Role = contribution.Role; grant.ModelId = contribution.ModelId; grant.WorkerId = "local"; grant.ModelWorkerId = "local";
                grant.RemoteProfileId = null; grant.AllowedRoots = [];
                grant.Capabilities = contribution.Capabilities.Intersect(job.Profile.Capabilities, StringComparer.Ordinal).ToList();
            }
            else
            {
                var receiver = (await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: attempt.Token)).FirstOrDefault(x => x.Id == remoteProfileId) ?? throw new InvalidOperationException("Configure the selected profile on the worker first.");
                if (receiver.WorkerId != "local") throw new InvalidOperationException("The worker profile must execute locally.");
                // Receiver roots are an explicit machine binding. Never reinterpret Windows roots on ARM Linux.
                grant = CerebralJson.Read<AgentProfile>(CerebralJson.Element(receiver));
                grant.Capabilities = receiver.Capabilities.Intersect(job.Profile.Capabilities, StringComparer.Ordinal).ToList(); grant.AllowedTools = IntersectTools(receiver.AllowedTools, job.Profile.AllowedTools);
                grant.MaxSteps = Math.Min(receiver.MaxSteps, job.Profile.MaxSteps); grant.MaxSeconds = Math.Min(receiver.MaxSeconds, job.Profile.MaxSeconds);
            }
            store.Locked(() =>
            {
                var session = store.Get<TaskSession>("session", job.SessionId)!;
                new ProjectKnowledge(store, configuration, events).RestrictUnmappedRemoteGrant(session, grant);
                var first = !session.RemoteSessionIds.TryGetValue(job.WorkerId, out var remoteSession);
                remoteSession ??= Guid.NewGuid().ToString("N"); session.RemoteSessionIds[job.WorkerId] = remoteSession; job.RemoteSessionId = remoteSession; job.RemoteJobId = job.Id;
                if (session.ProjectId is not null)
                {
                    session.Messages.Add(new() { Sequence = session.Messages.Count == 0 ? 1 : session.Messages[^1].Sequence + 1, Role = "user", Origin = "service", Content = "This assignment executes on a remote worker without a project mapping. Coordinator project paths are not applied there; the selected Cortex provider is disabled for this remote assignment until a project mapping exists. Only explicitly configured worker roots and the remaining native tools are used." });
                    session.Revision++;
                }
                job.RemoteSubmission = new() { RequestId = job.RemoteJobId, SessionId = remoteSession, ProfileId = remoteProfileId, Prompt = job.Prompt, Title = "Remote: " + job.Id, Kind = job.Kind, ResponseSharePercent = job.ResponseSharePercent, DelegatedProfile = grant, InitialCheckpoint = first ? session.Checkpoint : null, InitialMessages = first ? session.Messages.Where(x => !x.ExcludedFromContext && x.Sequence > session.CheckpointThroughSequence).ToList() : null };
                if (first) foreach (var steering in store.List<Steering>("steering").Where(x => x.JobId == job.Id && !x.Delivered && job.RemoteSubmission.InitialMessages!.Any(message => message.Sequence == x.Sequence))) { steering.Delivered = true; store.Put("steering", steering.Id, steering); }
                store.Put("session", session.Id, session); Save(job); return true;
            });
        }
        var remote = await client.CommandAsync("jobs.get", new { id = job.RemoteJobId }, cancellationToken: attempt.Token);
        if (remote.Success) ValidateRemoteJob(job, CerebralJson.Read<AgentJob>(remote.Data));
        if (!remote.Success)
        {
            var missing = string.IsNullOrWhiteSpace(worker.PeerToken) ? "Job not found." : PeerAccessControl.UnavailableAssignmentMessage;
            if (remote.Error != missing) throw new IOException(remote.Error);
            if (job.RemoteConfirmed) throw new InvalidOperationException("Previously accepted remote job disappeared; automatic replay is prohibited.");
            await client.GetAsync<AgentJob>("jobs.submit", job.RemoteSubmission, cancellationToken: attempt.Token);
        }
        job.RemoteConfirmed = true; Save(job);
        var current = await client.GetAsync<AgentJob>("jobs.get", new { id = job.RemoteJobId }, cancellationToken: attempt.Token);
        ValidateRemoteJob(job, current);
        job.RemoteSessionId = current.SessionId; job.ActiveSeconds = current.ActiveSeconds; job.StepCount = current.StepCount; job.RemoteFailures = 0;
        job.Outcome = current.Outcome; job.Evidence = current.Evidence.TakeLast(100).ToList();
        await SynchronizeQuestionsAsync(job, client, IsActive(current), attempt.Token);
        if (!IsActive(current))
        {
            store.Locked(() =>
            {
                job.Status = current.Status; job.Result = current.Result; job.Error = current.Error;
                if (HasPendingRemoteFollowUp(job))
                {
                    job.Error = "Remote work finished before pending input could be delivered. The input remains in the local conversation for a follow-up.";
                    var session = store.GetSessionMetadata(job.SessionId)!; session.RemoteSessionIds.Clear(); store.Put("session", session.Id, session);
                }
                job.FinishedAt = current.FinishedAt; job.NextReconcileAt = null; store.Append(job.SessionId, "assistant", current.Result ?? $"Remote task {current.Status}: {current.Error}"); Save(job); return true;
            });
            return;
        }
        await ForwardSteeringAsync(job, client, attempt.Token);
        job.Status = "remote-waiting"; job.WaitingReason = "Remote " + current.Status + (current.WaitingReason is null ? "" : ": " + current.WaitingReason); job.NextReconcileAt = DateTimeOffset.UtcNow.AddSeconds(current.Status == "waiting-human" ? 3 : 1);
    }
    internal bool HasPendingRemoteFollowUp(AgentJob job) =>
        store.List<Steering>("steering").Any(x => x.JobId == job.Id && !x.Delivered) ||
        store.List<UserQuestion>("question").Any(x => x.JobId == job.Id && x.Answer is not null && !x.Assumed && !x.RemoteAnswerDelivered);

    private async Task ForwardSteeringAsync(AgentJob job, CerebralClient client, CancellationToken token)
    {
        foreach (var item in store.List<Steering>("steering").Where(x => x.JobId == job.Id && !x.Delivered).Take(32))
        {
            var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(item.Id))).ToLowerInvariant();
            var result = await client.CommandAsync("jobs.steer", new { id = job.RemoteJobId, prompt = item.Prompt, requestId = id }, cancellationToken: token);
            if (!result.Success) throw new IOException(result.Error); item.Delivered = true; store.Put("steering", item.Id, item);
        }
    }
    private async Task SynchronizeQuestionsAsync(AgentJob job, CerebralClient client, bool forwardAnswers, CancellationToken token)
    {
        var questions = await client.GetAsync<List<UserQuestion>>("questions.list", new { sessionId = job.RemoteSessionId }, cancellationToken: token);
        foreach (var remote in questions.Where(x => x.JobId == job.RemoteJobId).Take(64))
        {
            var local = SynchronizeRemoteQuestion(job, remote);
            if (forwardAnswers && local.Answer is not null && !local.Assumed && !local.RemoteAnswerDelivered)
            {
                var forwardedAnswer = local.Answer;
                var response = await client.CommandAsync("questions.answer",
                    new { id = local.RemoteQuestionId, answer = forwardedAnswer }, cancellationToken: token);
                if (!response.Success) throw new IOException(response.Error);
                UserQuestion? updated = null;
                var changed = store.LinearizedSession(job.SessionId, () => store.Locked(() =>
                {
                    var current = store.Get<UserQuestion>("question", local.Id) ?? throw new InvalidDataException("Relayed question disappeared after its remote answer was accepted.");
                    if (current.JobId != job.Id || current.SessionId != job.SessionId || current.RemoteQuestionId != remote.Id || current.Answer != forwardedAnswer)
                        throw new InvalidDataException("Relayed question identity or answer changed during remote delivery.");
                    if (current.RemoteAnswerDelivered) return false;
                    current.RemoteAnswerDelivered = true; store.Put("question", current.Id, current); updated = current; return true;
                }));
                if (changed) events.Publish("question.updated", job.Id, updated);
            }
        }
    }

    internal UserQuestion SynchronizeRemoteQuestion(AgentJob job, UserQuestion remote)
    {
        ValidateRemoteQuestion(job, remote);
        var id = job.Id + "." + remote.Id;
        var changed = false;
        var changedSessions = new HashSet<string>(StringComparer.Ordinal);
        var rootSessionId = RootJob(job).SessionId;
        var local = store.LinearizedSessions([job.SessionId, rootSessionId], () => store.Locked(() =>
        {
            var owner = Get(job.Id);
            ValidateRemoteQuestion(owner, remote);
            var root = RootJob(owner);
            if (owner.SessionId != job.SessionId || root.SessionId != rootSessionId)
                throw new InvalidDataException("Remote question assignment ancestry changed during synchronization.");
            var current = store.Get<UserQuestion>("question", id);
            if (current is null)
            {
                changed = true;
                current = new UserQuestion
                {
                    Id = id, JobId = job.Id, SessionId = job.SessionId,
                    Prompt = remote.Prompt, Reason = remote.Reason, Choices = remote.Choices.ToList(),
                    Required = remote.Required, CanAssume = remote.CanAssume, Assumption = remote.Assumption,
                    DeadlineAt = remote.DeadlineAt,
                    CreatedAt = remote.CreatedAt, KnowledgeProposalId = remote.KnowledgeProposalId,
                    RemoteQuestionId = remote.Id
                };
            }
            if (current.Id != id || current.JobId != job.Id || current.SessionId != job.SessionId || current.RemoteQuestionId != remote.Id ||
                current.Prompt != remote.Prompt || current.Reason != remote.Reason || current.Required != remote.Required ||
                current.CanAssume != remote.CanAssume || current.Assumption != remote.Assumption || current.CreatedAt != remote.CreatedAt ||
                current.KnowledgeProposalId != remote.KnowledgeProposalId || !current.Choices.SequenceEqual(remote.Choices, StringComparer.Ordinal))
                throw new InvalidDataException("Remote question immutable fields changed; no relayed question state was adopted.");
            if (remote.DeadlineAt is { } remoteDeadline && current.DeadlineAt != remoteDeadline)
            {
                current.DeadlineAt = remoteDeadline; changed = true;
            }
            if (remote.Answer is not null)
            {
                if (remote.Assumed)
                {
                    if (current.Answer is null || current.Assumed)
                    {
                        if (current.Answer != remote.Answer) { current.Answer = remote.Answer; changed = true; }
                        if (!current.Assumed) { current.Assumed = true; changed = true; }
                        if (!current.RemoteAnswerDelivered) { current.RemoteAnswerDelivered = true; changed = true; }
                    }
                    else if (current.RemoteAnswerDelivered)
                    {
                        // Equal text is not delivery proof: the worker only applied its own automatic
                        // assumption, while the coordinator still owes the explicit user answer.
                        current.RemoteAnswerDelivered = false; changed = true;
                    }
                }
                else
                {
                    if (current.Answer is not null && !current.Assumed && current.Answer != remote.Answer)
                        throw new InvalidDataException("Remote explicit answer conflicts with the coordinator explicit answer.");
                    if (current.Answer is null || current.Assumed)
                    {
                        current.Answer = remote.Answer; current.Assumed = false; changed = true;
                    }
                    if (!current.RemoteAnswerDelivered) { current.RemoteAnswerDelivered = true; changed = true; }
                }
            }
            if (changed) store.Put("question", id, current);
            if (current.Answer is not null)
            {
                var context = ProjectQuestionState(current, owner, delegated: false);
                if (context.Changed) changedSessions.Add(context.SessionId);
                var leadContext = ProjectDelegatedQuestionState(current, owner);
                if (leadContext?.Changed == true) changedSessions.Add(leadContext.SessionId);
                var continuation = FindActiveQuestionContinuation(owner, root);
                if (continuation is not null)
                {
                    var delivery = continuation.SessionId == context.SessionId ? context : leadContext ?? context;
                    QueueQuestionContextForActiveJob(continuation, current, delivery.Message);
                }
            }
            return current;
        }));
        if (changed) events.Publish("question.updated", job.Id, local);
        foreach (var sessionId in changedSessions) events.Publish("session.updated", job.Id, new { id = sessionId });
        return local;
    }

    private static void ValidateRemoteQuestion(AgentJob job, UserQuestion remote)
    {
        if (job.RemoteJobId is null || job.RemoteSessionId is null || remote.JobId != job.RemoteJobId || remote.SessionId != job.RemoteSessionId)
            throw new InvalidDataException("Remote question does not belong to the owning assignment and conversation.");
        Configuration.ValidateId(remote.Id);
        if (string.IsNullOrWhiteSpace(remote.Prompt) || remote.Prompt.Length > 100_000 || remote.Prompt.Contains('\0') ||
            remote.Reason is { Length: > 100_000 } || remote.Reason?.Contains('\0') == true ||
            remote.Choices is null || remote.Choices.Count > 5 ||
            remote.Choices.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100_000 || x.Contains('\0')) ||
            remote.Answer is { Length: > 100_000 } || remote.Answer?.Contains('\0') == true || remote.CreatedAt == default)
            throw new InvalidDataException("Remote question contains invalid or unbounded text or metadata.");
        if (remote.KnowledgeProposalId is not null) Configuration.ValidateId(remote.KnowledgeProposalId);
        if (remote.Required && (remote.CanAssume || remote.Assumption is not null || remote.DeadlineAt is not null || remote.Assumed) ||
            !remote.CanAssume && (remote.Assumption is not null || remote.DeadlineAt is not null || remote.Assumed) ||
            remote.CanAssume && (string.IsNullOrWhiteSpace(remote.Assumption) || remote.Assumption.Length > AgentPrompt.MaxQuestionAssumptionCharacters || remote.Assumption.Contains('\0')) ||
            remote.Assumed && (remote.Answer is null || remote.Answer != remote.Assumption))
            throw new InvalidDataException("Remote question assumption state is invalid.");
    }
    private async Task ConfirmRemoteCancellationAsync(AgentJob job)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var worker = configuration.Get().Workers.First(x => x.Id == job.WorkerId); using var client = new CerebralClient(worker);
            await ValidateRemoteIdentityAsync(client, worker, timeout.Token);
            var before = await client.CommandAsync("jobs.get", new { id = job.RemoteJobId }, cancellationToken: timeout.Token);
            var missing = string.IsNullOrWhiteSpace(worker.PeerToken) ? "Job not found." : PeerAccessControl.UnavailableAssignmentMessage;
            if (!before.Success && before.Error == missing && !job.RemoteConfirmed)
            {
                var cancellation = await client.GetAsync<AgentJob>("jobs.cancel", new { id = job.RemoteJobId }, cancellationToken: timeout.Token);
                if (cancellation.Id != job.RemoteJobId || !cancellation.CancellationTombstone) throw new InvalidOperationException("Remote absence is not a durable cancellation acknowledgement.");
                job.Status = "cancelled"; job.FinishedAt = DateTimeOffset.UtcNow; job.NextReconcileAt = null; return;
            }
            if (!before.Success) throw new IOException(before.Error); ValidateRemoteJob(job, CerebralJson.Read<AgentJob>(before.Data));
            var result = await client.CommandAsync("jobs.cancel", new { id = job.RemoteJobId }, cancellationToken: timeout.Token);
            if (!result.Success) throw new IOException(result.Error);
            var remote = await client.GetAsync<AgentJob>("jobs.get", new { id = job.RemoteJobId }, cancellationToken: timeout.Token);
            ValidateRemoteJob(job, remote);
            if (!IsActive(remote)) { job.Status = remote.Status; job.Result = remote.Result; job.Error = remote.Error; job.FinishedAt = remote.FinishedAt; job.NextReconcileAt = null; return; }
            Unconfirmed(job, "Remote cancellation requested, but remote work remains active.");
        }
        catch (Exception ex) { Unconfirmed(job, "Remote cancellation is unconfirmed: " + ex.Message); }
    }
    private static void ValidateRemoteJob(AgentJob owner, AgentJob remote)
    {
        var remoteProfileId = owner.RemoteSubmission?.ProfileId ?? owner.Profile.RemoteProfileId ?? owner.ProfileId;
        if (remote.Id != owner.RemoteJobId || remote.ProfileId != remoteProfileId || remote.Prompt != owner.Prompt || (owner.RemoteSessionId is not null && remote.SessionId != owner.RemoteSessionId)) throw new InvalidOperationException("Remote job identity does not match the owning assignment; no mutation or result adoption is permitted.");
    }
    private static async Task ValidateRemoteIdentityAsync(CerebralClient client, WorkerRegistration worker, CancellationToken token)
    {
        PeerHostInfo identity;
        if (string.IsNullOrWhiteSpace(worker.PeerToken))
        {
            var owner = await client.GetAsync<HostInfo>("health", cancellationToken: token);
            identity = new(owner.Product, owner.Version, owner.Protocol, owner.Name, owner.Architecture, owner.OperatingSystem, owner.Coordinator);
        }
        else identity = await client.GetAsync<PeerHostInfo>("health", cancellationToken: token);
        if (identity.Product != "Pegasus Cerebral" || identity.Protocol != 1) throw new InvalidOperationException("Worker identity or protocol mismatch.");
    }
    private static void Unconfirmed(AgentJob job, string error)
    {
        job.Status = "remote-unconfirmed"; job.Error = error; job.RemoteFailures++;
        job.NextReconcileAt = job.RemoteFailures < 3 ? DateTimeOffset.UtcNow.AddSeconds(job.RemoteFailures == 1 ? 5 : 15) : null;
        job.WaitingReason = job.NextReconcileAt is null ? "Automatic reconciliation exhausted. Use jobs.reconcile or jobs.cancel after checking the worker. This session remains reserved." : "Waiting for bounded remote reconciliation.";
    }
}
