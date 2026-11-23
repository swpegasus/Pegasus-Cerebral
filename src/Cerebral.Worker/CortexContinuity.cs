using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Optional bounded task checkpoints in the installed Cortex; Cerebral remains the execution owner.</summary>
public sealed class CortexContinuity(StateStore store, Configuration configuration, IToolDispatcher tools, EventHub events)
{
    private readonly ProjectKnowledge projects = new(store, configuration, events);
    public CortexContinuityState? Get(string sessionId) => store.Get<CortexContinuityState>("cortex-continuity", sessionId);

    public async Task<CortexContinuityState?> BeginAsync(AgentJob job, AgentProfile profile, CancellationToken token)
    {
        var session = store.GetSessionMetadata(job.SessionId)!;
        if (session.ProjectId is null || job.Kind == "compact") return null;
        var settings = projects.Get(session.ProjectId);
        if (!settings.AutomaticContinuity) return null;
        var previous = Get(session.Id);
        if (previous is { Status: "uncertain" or "attempting" } && previous.JobId == job.Id)
        {
            Save(previous, "uncertain", "The previous task write has an unconfirmed outcome. This assignment will not replay it. Inspect Cortex before starting new work.");
            return previous;
        }
        var state = new CortexContinuityState { SessionId = session.Id, TaskId = session.Id, JobId = job.Id,
            ProjectId = session.ProjectId, SettingsRevision = settings.Revision, ServerId = settings.CortexServerId,
            Namespace = settings.WriteNamespace ?? "" };
        var attemptingWrite = false;
        try
        {
            projects.Validate(settings);
            var context = projects.ContextFor(session, profile, job.Id);
            RequireGrants(profile, state);
            var provider = configuration.Get().McpServers.Single(x => x.Id == state.ServerId);
            state.ProviderFingerprint = CortexOperationPolicy.ProviderFingerprint(provider);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var spaces = new[] { state.Namespace }.Concat(context.Cortex!.ReadNamespaces).Distinct(StringComparer.Ordinal).ToArray();
            var started = await Call(state, "cortex_start", new { taskId = state.TaskId, namespaces = spaces,
                intent = CortexContext.BoundUtf8(job.Prompt, 1024), phaseId = job.Id, maxUtf8Bytes = 4096, format = "compact" }, context, timeout.Token);
            if (!started.TryGetProperty("ns", out var echoed) || echoed.ValueKind != JsonValueKind.Array ||
                !echoed.EnumerateArray().Select(x => x.GetString()).SequenceEqual(spaces))
                throw new InvalidDataException("Cortex start did not echo the selected ordered namespaces.");
            var unchanged = started.TryGetProperty("mode", out var mode) && mode.GetString() == "unchanged";
            var hasTask = started.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object;
            if (unchanged)
            {
                var existing = await Call(state, "cortex_task", new { action = "get", @namespace = state.Namespace,
                    taskId = state.TaskId, maxUtf8Bytes = 4096 }, context, timeout.Token);
                ValidateEcho(existing, state);
                task = existing.GetProperty("task"); hasTask = true;
            }
            if (!CurrentBinding(state)) throw new InvalidOperationException("Project knowledge settings changed while Cortex continuity was reading; no task write was attempted.");
            if (hasTask)
            {
                state.Revision = TaskRevision(task, state, compact: !unchanged);
                var status = task.GetProperty("status").GetString();
                if (status is "completed" or "blocked")
                {
                    attemptingWrite = true;
                    Save(state, "attempting", "Reopening the existing Cerebral task in Cortex.");
                    var reopened = await Call(state, "cortex_task", new { action = "reopen", @namespace = state.Namespace,
                        taskId = state.TaskId, expectedRevision = state.Revision, idempotencyKey = Key(job, "reopen", state.Revision),
                        reason = "A Cerebral conversation is continuing with an explicit assignment.", phaseId = job.Id }, context, timeout.Token);
                    state.Revision = ConfirmTask(reopened, state);
                }
                else if (status is not ("open" or "assigned" or "claimed")) throw new InvalidOperationException("Cortex task is " + status + "; its lifecycle must be resolved explicitly.");
            }
            else
            {
                attemptingWrite = true;
                Save(state, "attempting", "Creating the Cerebral conversation task in Cortex.");
                var created = await Call(state, "cortex_task", new { action = "create", @namespace = state.Namespace,
                    taskId = state.TaskId, title = CortexContext.BoundUtf8("Cerebral: " + session.Title, 180),
                    content = Brief(job, session, "started"), idempotencyKey = "cerebral-create-" + ExecutionReliability.Fingerprint("task-create", CerebralJson.Element(new { state.Namespace, state.TaskId })), phaseId = job.Id }, context, timeout.Token);
                state.Revision = ConfirmTask(created, state);
            }
            if (!CurrentBinding(state))
            {
                Save(state, attemptingWrite ? "started-old-binding" : "unavailable",
                    attemptingWrite ? "Cortex confirmed the task write for the previous binding. Target: " + state.Namespace + "/" + state.TaskId + ". This assignment will not checkpoint that binding."
                        : "Project knowledge settings changed while Cortex continuity was reading. No task write was attempted.");
                return state;
            }
            state.Status = "ready"; state.Message = "Installed Cortex task is bound to this Cerebral conversation.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Save(state, attemptingWrite ? "uncertain" : "unavailable", "Cortex continuity was interrupted; inspect any unconfirmed task write before retrying."); throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { state.Status = attemptingWrite ? "uncertain" : "unavailable"; state.Message = Failure(ex); }
        Save(state, state.Status, state.Message);
        return state;
    }

    public async Task CheckpointAsync(AgentJob job, AgentProfile profile, CortexContinuityState? began, string outcome)
    {
        if (began is not { Status: "ready", Revision: not null }) return;
        var state = began;
        try
        {
            if (!CurrentBinding(state)) throw new InvalidOperationException("Project knowledge settings changed; the old Cortex checkpoint was not applied.");
            var session = store.GetSessionMetadata(job.SessionId)!;
            var context = projects.ContextFor(session, profile, job.Id);
            RequireGrants(profile, state);
            var action = outcome == "completed" ? "complete" : outcome == "waiting-human" ? "block" : "update";
            var body = new { @namespace = state.Namespace, taskId = state.TaskId, expectedRevision = state.Revision,
                taskAction = action, taskContent = Brief(job, session, outcome),
                checkpoint = "Cerebral assignment " + job.Id + " reached " + outcome + ".",
                checkpointSubject = "Cerebral " + outcome, phaseId = job.Id,
                evidence = CortexContext.BoundUtf8(ExecutionReliability.EvidenceBrief(store.Get<AgentJob>("job", job.Id)!.Evidence), 2000),
                blockedReason = action == "block" ? CortexContext.BoundUtf8(job.WaitingReason ?? "Waiting for the user in Cerebral.", 800) : null,
                knowledge = Array.Empty<object>(), maxUtf8Bytes = 4096,
                idempotencyKey = Key(job, "finish-" + outcome, state.Revision) };
            // Persist the uncertain boundary before the external call. Never replay an unfinished write blindly.
            var arguments = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body, CerebralJson.Options))!.AsObject();
            if (action != "block") arguments.Remove("blockedReason");
            Save(state, "attempting", "Saving a bounded task-only checkpoint to the installed Cortex.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await Call(state, "cortex_finish", arguments, context, timeout.Token);
            state.Revision = ConfirmTask(reply, state);
            Save(state, CurrentBinding(state) ? "checkpointed" : "checkpointed-old-binding",
                "Cortex confirmed the task checkpoint for " + outcome + ". Target: " + state.Namespace + "/" + state.TaskId + ".");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Save(state, state.Status == "attempting" ? "uncertain" : "unavailable", Failure(ex)); }
    }

    private bool CurrentBinding(CortexContinuityState state)
    {
        var session = store.GetSessionMetadata(state.SessionId);
        var provider = configuration.Get().McpServers.SingleOrDefault(x => x.Id == state.ServerId);
        return session?.ProjectId == state.ProjectId && projects.Get(state.ProjectId).Revision == state.SettingsRevision &&
            provider is { Enabled: true } && CortexOperationPolicy.ProviderFingerprint(provider) == state.ProviderFingerprint;
    }
    private static void RequireGrants(AgentProfile profile, CortexContinuityState state)
    {
        if (!profile.Capabilities.Contains("mcp." + state.ServerId) || !profile.Capabilities.Contains("cortex.write") ||
            new[] { "cortex_start", "cortex_task", "cortex_finish" }.Any(x => !profile.AllowedTools.Contains("*") && !profile.AllowedTools.Contains("mcp." + state.ServerId + "." + x)))
            throw new UnauthorizedAccessException("Automatic Cortex continuity requires explicit start/task/finish and cortex.write grants. Configure them in Integrator.");
    }
    private async Task<JsonElement> Call(CortexContinuityState state, string tool, object args, ToolExecutionContext context, CancellationToken token)
    {
        if (!CurrentBinding(state)) throw new InvalidOperationException("The Cortex project or provider changed before dispatch; this operation was not sent.");
        if (context.Cortex is { } scope) context = context with { Cortex = scope with { ProviderFingerprint = state.ProviderFingerprint } };
        var result = await tools.ExecuteAsync(new("mcp." + state.ServerId + "." + tool, CerebralJson.Element(args)), context, token);
        if (!result.Success || result.Truncated) throw new IOException("Cortex " + tool + " did not return a complete success: " + ExecutionReliability.Clip(result.Content, 700));
        return CortexContext.Payload(result.Content);
    }
    private static void ValidateEcho(JsonElement receipt, CortexContinuityState expected)
    {
        if (!receipt.TryGetProperty("effectiveNamespaces", out var spaces) || spaces.ValueKind != JsonValueKind.Array ||
            !spaces.EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { expected.Namespace }))
            throw new InvalidDataException("Cortex did not echo the exact task namespace.");
    }
    private static int ConfirmTask(JsonElement receipt, CortexContinuityState expected)
    {
        ValidateEcho(receipt, expected);
        if (!receipt.TryGetProperty("disposition", out var disposition) || disposition.GetString() is not ("appended" or "idempotent"))
            throw new InvalidDataException("Cortex did not confirm the task mutation. Resolve any revision or idempotency conflict before retrying.");
        var revision = TaskRevision(receipt.GetProperty("task"), expected);
        if (revision != checked((expected.Revision ?? 0) + 1))
            throw new InvalidDataException("Cortex did not confirm the expected resulting task revision.");
        return revision;
    }
    internal static int TaskRevision(JsonElement task, CortexContinuityState expected, bool compact = false)
    {
        var ns = task.GetProperty(compact ? "ns" : "namespace").GetString();
        var id = task.GetProperty(compact ? "id" : "taskId").GetString();
        var revision = task.GetProperty(compact ? "rev" : "revision").GetInt32();
        if (ns != expected.Namespace || id != expected.TaskId || revision < 1)
            throw new InvalidDataException("Cortex returned a different task coordinate or invalid revision.");
        return revision;
    }
    private static string Key(AgentJob job, string phase, int? revision) =>
        "cerebral-" + ExecutionReliability.Fingerprint(phase, CerebralJson.Element(new { job.Id, job.StepCount, revision }));
    private static string Brief(AgentJob job, TaskSession session, string status) =>
        "Cerebral conversation: " + CortexContext.BoundUtf8(session.Title, 180) + "\nAssignment: " + job.Id + "; state: " + status +
        "\nObjective excerpt: " + CortexContext.BoundUtf8(job.Prompt, 1100) +
        "\nModel-reported result (verify against service evidence): " + CortexContext.BoundUtf8(job.Result ?? job.Error ?? job.WaitingReason ?? "No final result yet.", 1500);
    private static string Failure(Exception ex) => "Cortex continuity is unavailable or unconfirmed: " +
        CortexContext.BoundUtf8(ex is OperationCanceledException ? "The bounded request timed out." : ex.Message, 1000) +
        " Cerebral retains its conversation and execution outcome; no automatic write retry was made.";
    private void Save(CortexContinuityState state, string status, string message)
    {
        state.Status = status; state.Message = message; state.At = DateTimeOffset.UtcNow;
        store.Put("cortex-continuity", state.SessionId, state);
        if (status != "attempting") store.Append(state.SessionId, "user", "Cortex continuity: " + status + ". " + message, origin: "service");
        events.Publish("cortex.continuity.updated", state.JobId, state);
        events.Publish("session.updated", state.JobId, new { id = state.SessionId });
    }
}
