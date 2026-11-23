using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

/// <summary>
/// Optional host-owned Cortex checkpoints. Native workflow state remains authoritative. A lost write
/// reply is retained across turns and restarts; task/history reads cannot prove its exact payload.
/// </summary>
public sealed class NativeCortexContinuity
{
    private const string RecordKind = "native-cortex-continuity";
    private const int DiscoveryPageSize = 40;
    private const int MaximumPendingMilestones = 8;
    private readonly StateStore legacy;
    private readonly Configuration configuration;
    private readonly IToolDispatcher tools;
    private readonly EventHub events;
    private readonly WorkflowStore workflows;
    private readonly EventJournal journal;
    private readonly SemaphoreSlim dispatch = new(1, 1);
    private readonly string recoveryGeneration = Guid.NewGuid().ToString("N");
    private long? discoveryBefore, discoverySnapshot;
    private bool discovering;

    public NativeCortexContinuity(StateStore legacy, Configuration configuration, IToolDispatcher tools,
        EventHub events, WorkflowStore workflows, EventJournal journal)
    {
        this.legacy = legacy; this.configuration = configuration; this.tools = tools;
        this.events = events; this.workflows = workflows; this.journal = journal;
    }

    // These records are private host persistence, not workflow/plugin SDK contracts or model context.
    public sealed class State
    {
        public string ConversationId { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public int ProjectSettingsRevision { get; set; }
        public CortexAccessScope Scope { get; set; } = new("", "", [], null, true);
        public string BindingKey { get; set; } = "";
        public int? TaskRevision { get; set; }
        public bool TaskReady { get; set; }
        public string LastRunId { get; set; } = "";
        public long LastAdmissionSequence { get; set; }
        public string LastObservedSourceKey { get; set; } = "";
        public long SourceSequence { get; set; }
        public List<Milestone> Pending { get; set; } = [];
        public Operation? PendingOperation { get; set; }
        public Operation? LastOperation { get; set; }
        public string Status { get; set; } = "queued";
        public string Message { get; set; } = "";
        public string? ReconciliationGeneration { get; set; }
        public string? SafeRetryGeneration { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed record Milestone(string SourceKey, string RunId, long RunRevision, string? ParentRunId,
        string Kind, string Title, string Objective, string ReportedResult, string Evidence);

    public sealed class Operation
    {
        public string Id { get; set; } = "";
        public string SourceKey { get; set; } = "";
        public string SourceRunId { get; set; } = "";
        public long SourceRunRevision { get; set; }
        public string? ParentRunId { get; set; }
        public string Tool { get; set; } = "";
        public string Kind { get; set; } = "";
        public JsonElement Arguments { get; set; }
        public string PayloadHash { get; set; } = "";
        public int ExpectedRevision { get; set; }
        public string Namespace { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public int ProjectSettingsRevision { get; set; }
        public string ProviderFingerprint { get; set; } = "";
        public string Status { get; set; } = "prepared";
        public DateTimeOffset PreparedAt { get; set; }
        public DateTimeOffset? DispatchedAt { get; set; }
        public int? ResultRevision { get; set; }
        public string? ReceiptHash { get; set; }
    }

    public State? Get(string conversationId) => legacy.Get<State>(RecordKind, conversationId);

    /// <summary>Owner-only bounded provider inspection. It never settles or replays saved writes.</summary>
    public async Task<object> InspectAsync(string conversationId, CancellationToken cancellationToken)
    {
        Configuration.ValidateId(conversationId);
        var captured = legacy.Locked(() =>
        {
            var state = Get(conversationId) ??
                throw new ArgumentException("This conversation has no native Cortex continuity binding.");
            // Hold the local record lock only while resolving current consent and grants. This also
            // prevents project default creation if consent disappears between synchronous checks.
            return (State: state, Context: Context(state, state.LastRunId));
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var reply = await tools.ExecuteAsync(new("mcp." + captured.State.Scope.ServerId + ".cortex_task",
            CerebralJson.Element(new { action = "get", @namespace = captured.State.Scope.WriteNamespace,
                taskId = conversationId, maxUtf8Bytes = 4096 })), captured.Context, timeout.Token).ConfigureAwait(false);
        if (!reply.Success || reply.Truncated || System.Text.Encoding.UTF8.GetByteCount(reply.Content) > 16 * 1024)
            throw new IOException("Cortex did not return a complete bounded native task inspection.");
        var payload = CortexContext.Payload(reply.Content);
        ValidateEcho(payload, captured.State);
        var task = payload.GetProperty("task");
        var providerRevision = TaskRevision(task, captured.State);
        legacy.Locked(() =>
        {
            var current = Get(conversationId) ??
                throw new InvalidOperationException("Native continuity binding disappeared during inspection.");
            if (current.BindingKey != captured.State.BindingKey || current.LastRunId != captured.State.LastRunId)
                throw new InvalidOperationException("Native continuity binding changed during inspection. Refresh before using this reply.");
            _ = Context(current, current.LastRunId);
            return true;
        });
        return new { conversationId, captured.State.ProjectId, sourceRunId = captured.State.LastRunId,
            captured.State.BindingKey, savedStatus = captured.State.Status,
            savedTaskRevision = captured.State.TaskRevision, providerTaskRevision = providerRevision,
            providerTask = task.Clone(), writeReconciled = false };
    }

    /// <summary>Postcommit notification only. Reload the canonical run; never accept caller-supplied execution facts.</summary>
    public void Observe(WorkflowRun notification)
    {
        try
        {
            var run = workflows.Get(notification.Id);
            if (run is null || run.Submission.ConversationId != notification.Submission.ConversationId) return;
            var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
            if (snapshot.NativeContinuity is not { ProjectSettingsRevision: > 0 } consent ||
                snapshot.CortexScope is not { WriteNamespace: not null } scope ||
                run.Submission.ProjectId is null || scope.ProjectId != run.Submission.ProjectId ||
                scope.ProviderFingerprint is null or "unavailable" ||
                !scope.ReadNamespaces.Contains(scope.WriteNamespace, StringComparer.Ordinal)) return;
            var binding = BindingKey(scope, consent.ProjectSettingsRevision);
            var milestone = Capture(run);
            var sourceSequence = journal.GetRevision(run.Submission.ConversationId);
            legacy.Locked(() =>
            {
                var state = Get(run.Submission.ConversationId);
                if (state is not null && (run.Submission.ConversationSequence < state.LastAdmissionSequence ||
                    run.Submission.ConversationSequence == state.LastAdmissionSequence && state.LastRunId != run.Id)) return false;
                state ??= new State { ConversationId = run.Submission.ConversationId };
                var newRun = state.LastRunId != run.Id;
                var protectedWrite = state.PendingOperation is { Status: "attempting" or "uncertain" };
                if (state.BindingKey != binding)
                {
                    // A new admission cannot abandon an unresolved write to an old provider/namespace.
                    if (protectedWrite) return false;
                    state.ProjectId = scope.ProjectId; state.ProjectSettingsRevision = consent.ProjectSettingsRevision;
                    state.Scope = scope with { KnowledgeWriteApproved = false }; state.BindingKey = binding;
                    state.TaskRevision = null; state.TaskReady = false; state.Pending.Clear();
                    state.PendingOperation = null;
                }
                if (!newRun && state.LastObservedSourceKey == milestone.SourceKey) return false;
                if (newRun)
                {
                    Add(state, Capture(run, started: true));
                    state.LastRunId = run.Id; state.LastAdmissionSequence = run.Submission.ConversationSequence;
                }
                if (milestone.Kind != "started") Add(state, milestone);
                state.LastObservedSourceKey = milestone.SourceKey; state.SourceSequence = sourceSequence;
                if (!protectedWrite) state.Status = "queued";
                Persist(state);
                return true;
            });
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { Diagnostic(notification.Id, "Native Cortex observation was not saved: " + Failure(error)); }
    }

    /// <summary>Starts bounded discovery. No provider I/O or whole-history hydration occurs here.</summary>
    public void Recover()
    {
        discoveryBefore = null; discoverySnapshot = null; discovering = true;
    }

    /// <summary>At most one external candidate and one bounded metadata page; no model/scheduler slot is owned.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await dispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? conversationId = null, failureBindingKey = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            conversationId = NextCandidate();
            if (conversationId is null)
            {
                if (!discovering) return false;
                var page = workflows.ListRecentPage(DiscoveryPageSize, discoveryBefore, discoverySnapshot);
                foreach (var summary in page.Runs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (workflows.Get(summary.Id) is { } run) Observe(run);
                }
                discoverySnapshot = page.SnapshotRow; discoveryBefore = page.NextBeforeRow;
                discovering = page.NextBeforeRow is not null;
                return page.Runs.Count != 0;
            }
            var state = Get(conversationId)!;
            failureBindingKey = state.BindingKey;
            if (state.PendingOperation is { Status: "attempting" })
            {
                Change(conversationId, current =>
                {
                    current.PendingOperation!.Status = "uncertain"; current.Status = "uncertain";
                    current.Message = "An interrupted Cortex write has no confirmed receipt. It will not be replayed.";
                    current.ReconciliationGeneration = null;
                });
                return true;
            }
            if (state.PendingOperation is { Status: "uncertain" })
            {
                await InspectUncertainAsync(state, cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (state.Pending.Count == 0) return false;
            state = ChangeBound(state, current => current.SafeRetryGeneration = recoveryGeneration);
            var milestone = state.Pending[0];
            _ = Context(state, milestone.RunId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            if (state.PendingOperation is { Status: "prepared" } saved)
            {
                var source = state.Pending.Single(x => x.SourceKey == saved.SourceKey);
                await DispatchPreparedAsync(state, source, saved, timeout.Token).ConfigureAwait(false);
                return true;
            }
            if (!state.TaskReady)
            {
                var spaces = new[] { state.Scope.WriteNamespace! }.Concat(state.Scope.ReadNamespaces)
                    .Distinct(StringComparer.Ordinal).ToArray();
                var started = await ReadAsync(state, milestone.RunId, "cortex_start", new
                {
                    taskId = state.ConversationId, namespaces = spaces, intent = milestone.Objective,
                    phaseId = milestone.RunId, maxUtf8Bytes = 4096, format = "compact"
                }, timeout.Token).ConfigureAwait(false);
                if (!started.TryGetProperty("ns", out var echo) || echo.ValueKind != JsonValueKind.Array ||
                    !echo.EnumerateArray().Select(x => x.GetString()).SequenceEqual(spaces))
                    throw new InvalidDataException("Cortex start did not echo the exact ordered namespaces.");
                if (!started.TryGetProperty("schema", out var schema) || schema.GetString() != "cortex.start.compact/1" ||
                    !started.TryGetProperty("mode", out var mode) || mode.GetString() is not ("snapshot" or "unchanged"))
                    throw new InvalidDataException("Cortex start omitted its supported compact snapshot contract.");
                var unchanged = mode.GetString() == "unchanged";
                var taskPresent = started.TryGetProperty("task", out var task);
                // Compact snapshot/1 omits the optional task when CurrentTask is null.
                // Absence is meaningful only after the exact schema, mode and ordered scope checks.
                // Unchanged mode still requires the exact task lookup below.
                if (!unchanged && taskPresent && task.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                    throw new InvalidDataException("Cortex start reported an invalid optional task.");
                var hasTask = taskPresent && task.ValueKind == JsonValueKind.Object;
                if (unchanged)
                {
                    var existing = await ReadAsync(state, milestone.RunId, "cortex_task", new
                    {
                        action = "get", @namespace = state.Scope.WriteNamespace, taskId = state.ConversationId, maxUtf8Bytes = 4096
                    }, timeout.Token).ConfigureAwait(false);
                    ValidateEcho(existing, state); task = existing.GetProperty("task"); hasTask = true;
                }
                _ = Context(state, milestone.RunId);
                if (hasTask)
                {
                    var revision = TaskRevision(task, state, compact: !unchanged);
                    var lifecycle = task.GetProperty("status").GetString();
                    if (lifecycle is not ("open" or "assigned" or "claimed" or "completed" or "blocked"))
                        throw new InvalidOperationException("The Cortex task lifecycle requires explicit owner resolution.");
                    state = ChangeBound(state, current => { current.TaskRevision = revision; current.TaskReady = true; });
                    if (lifecycle is "completed" or "blocked")
                    {
                        await MutateAsync(state, milestone, "reopen", timeout.Token).ConfigureAwait(false);
                        return true;
                    }
                }
                else
                {
                    await MutateAsync(state, milestone, "create", timeout.Token).ConfigureAwait(false);
                    return true;
                }
            }
            await MutateAsync(state, milestone, "finish", timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (conversationId is not null) SetFailure(conversationId, "Native Cortex maintenance was cancelled.", failureBindingKey);
            throw;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (conversationId is not null) SetFailure(conversationId, Failure(error), failureBindingKey);
            else Diagnostic(null, "Native Cortex maintenance is unavailable: " + Failure(error));
            return false;
        }
        finally { dispatch.Release(); }
    }

    private async Task MutateAsync(State state, Milestone milestone, string kind, CancellationToken token)
    {
        // All mutation bytes and coordinates are frozen before the uncertainty boundary. No later
        // observation, model output, provider discovery, or recovery fills in a different payload.
        var expected = kind == "create" ? 0 : state.TaskRevision ?? throw new InvalidOperationException("A task revision is required.");
        var key = "cerebral-native-" + ExecutionReliability.Fingerprint(kind, CerebralJson.Element(new
        { state.BindingKey, state.ConversationId, milestone.SourceKey, expected }));
        object body = kind switch
        {
            "create" => new { action = "create", @namespace = state.Scope.WriteNamespace, taskId = state.ConversationId,
                title = milestone.Title, content = Brief(milestone), idempotencyKey = key, phaseId = milestone.RunId },
            "reopen" => new { action = "reopen", @namespace = state.Scope.WriteNamespace, taskId = state.ConversationId,
                expectedRevision = expected, idempotencyKey = key, phaseId = milestone.RunId,
                reason = "A new explicitly admitted Cerebral turn is continuing this conversation." },
            _ => new { @namespace = state.Scope.WriteNamespace, taskId = state.ConversationId,
                expectedRevision = expected, taskAction = "update", taskContent = Brief(milestone),
                checkpoint = "Cerebral run " + milestone.RunId + " reached " + milestone.Kind + ".",
                checkpointSubject = "Cerebral " + milestone.Kind, phaseId = milestone.RunId,
                evidence = milestone.Evidence, knowledge = Array.Empty<object>(), maxUtf8Bytes = 4096, idempotencyKey = key }
        };
        var arguments = CerebralJson.Element(body);
        var tool = kind == "finish" ? "cortex_finish" : "cortex_task";
        CortexOperationPolicy.ValidateExplicitMutation(tool, arguments, state.ConversationId);
        var operation = new Operation
        {
            Id = key, SourceKey = milestone.SourceKey, SourceRunId = milestone.RunId, SourceRunRevision = milestone.RunRevision,
            ParentRunId = milestone.ParentRunId, Tool = tool, Kind = kind, Arguments = arguments,
            PayloadHash = ExecutionReliability.Fingerprint(tool, arguments), ExpectedRevision = expected,
            Namespace = state.Scope.WriteNamespace!, TaskId = state.ConversationId, ProjectId = state.ProjectId,
            ProjectSettingsRevision = state.ProjectSettingsRevision, ProviderFingerprint = state.Scope.ProviderFingerprint!,
            PreparedAt = DateTimeOffset.UtcNow
        };
        state = ChangeBound(state, current =>
        {
            if (!current.Pending.Any(x => x.SourceKey == milestone.SourceKey) || current.PendingOperation is not null)
                throw new InvalidOperationException("The pending Cortex milestone changed before preparation.");
            current.PendingOperation = operation;
        });
        await DispatchPreparedAsync(state, milestone, operation, token).ConfigureAwait(false);
    }

    private async Task DispatchPreparedAsync(State state, Milestone milestone, Operation operation, CancellationToken token)
    {
        if (operation.TaskId != state.ConversationId || operation.Namespace != state.Scope.WriteNamespace ||
            operation.ProjectId != state.ProjectId || operation.ProjectSettingsRevision != state.ProjectSettingsRevision ||
            operation.ProviderFingerprint != state.Scope.ProviderFingerprint || operation.SourceRunId != milestone.RunId ||
            operation.SourceKey != milestone.SourceKey || operation.ParentRunId != milestone.ParentRunId ||
            operation.PayloadHash != ExecutionReliability.Fingerprint(operation.Tool, operation.Arguments))
            throw new InvalidDataException("The saved Cortex intent does not match its immutable source and target.");
        CortexOperationPolicy.ValidateExplicitMutation(operation.Tool, operation.Arguments, state.ConversationId);
        _ = Context(state, milestone.RunId);
        token.ThrowIfCancellationRequested();
        state = ChangeBound(state, current =>
        {
            if (current.PendingOperation?.Id != operation.Id || current.PendingOperation.Status != "prepared")
                throw new InvalidOperationException("The Cortex intent changed before dispatch.");
            current.PendingOperation.Status = "attempting"; current.PendingOperation.DispatchedAt = DateTimeOffset.UtcNow;
            current.Status = "attempting"; current.Message = "A bounded native task checkpoint is awaiting an exact Cortex receipt.";
        });
        // No StateStore/engine SQLite lock survives this asynchronous provider call.
        var context = Context(state, milestone.RunId) with { EffectId = operation.Id };
        var result = await tools.ExecuteAsync(new("mcp." + state.Scope.ServerId + "." + operation.Tool, operation.Arguments), context, token).ConfigureAwait(false);
        result = CortexOperationPolicy.ConfirmMutation(operation.Tool, operation.Arguments, result);
        if (!result.Success || result.Truncated)
            throw new IOException("Cortex did not return a complete, confirmed mutation receipt.");
        var receipt = CortexContext.Payload(result.Content);
        ValidateEcho(receipt, state);
        var revision = TaskRevision(receipt.GetProperty("task"), state);
        if (revision != checked(operation.ExpectedRevision + 1))
            throw new InvalidDataException("Cortex did not confirm the exact resulting task revision.");
        var currentBinding = BindingAvailable(state, milestone.RunId);
        var saved = Change(state.ConversationId, current =>
        {
            if (current.PendingOperation?.Id != operation.Id)
                throw new InvalidOperationException("The saved Cortex intent is unavailable after its reply.");
            var confirmed = current.PendingOperation; confirmed.Status = "confirmed"; confirmed.ResultRevision = revision;
            confirmed.ReceiptHash = ExecutionReliability.Fingerprint("receipt", receipt);
            current.LastOperation = confirmed; current.PendingOperation = null; current.TaskRevision = revision; current.TaskReady = true;
            // Creating already records the started milestone; reopening retains it for a task-only update.
            if (operation.Kind != "reopen") current.Pending.RemoveAll(x => x.SourceKey == milestone.SourceKey);
            if (!currentBinding) current.Pending.Clear();
            current.Status = !currentBinding ? "checkpointed-old-binding" : current.Pending.Count == 0 ? "checkpointed" : "queued";
            current.Message = "Cortex confirmed revision " + revision + " for " + confirmed.Namespace + "/" + confirmed.TaskId + ".";
        });
        events.Publish("cortex.continuity.updated", saved.LastRunId, saved);
    }

    private async Task InspectUncertainAsync(State state, CancellationToken token)
    {
        var operation = state.PendingOperation!;
        // One bounded inspection per host recovery, even when the provider is unavailable. The live
        // history contract has no idempotency key/payload identity, so matching fields never clear uncertainty.
        state = Change(state.ConversationId, current => current.ReconciliationGeneration = recoveryGeneration);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var current = await ReadAsync(state, operation.SourceRunId, "cortex_task", new
            {
                action = "get", @namespace = operation.Namespace, taskId = operation.TaskId, maxUtf8Bytes = 4096
            }, timeout.Token).ConfigureAwait(false);
            ValidateEcho(current, state); _ = TaskRevision(current.GetProperty("task"), state);
            var expected = checked(operation.ExpectedRevision + 1);
            var history = await ReadAsync(state, operation.SourceRunId, "cortex_task", new
            {
                action = "history", @namespace = operation.Namespace, taskId = operation.TaskId,
                firstRevision = expected, lastRevision = expected, limit = 1, maxUtf8Bytes = 4096
            }, timeout.Token).ConfigureAwait(false);
            ValidateEcho(history, state);
            if (!history.TryGetProperty("disposition", out var disposition) || disposition.GetString() != "history" ||
                !history.TryGetProperty("history", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1)
                throw new InvalidDataException("Cortex omitted the bounded exact task history.");
            foreach (var row in rows.EnumerateArray())
                if (TaskRevision(row, state) != expected)
                    throw new InvalidDataException("Cortex history returned another revision.");
            Change(state.ConversationId, value => value.Message =
                "Read-only Cortex inspection cannot prove this operation's exact payload/idempotency identity. The write remains unconfirmed and will not be replayed.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Change(state.ConversationId, current => current.Message =
                "The saved write remains unconfirmed; bounded read-only inspection was unavailable. " + Failure(error));
        }
    }

    private ToolExecutionContext Context(State state, string runId)
    {
        var run = workflows.Get(runId) ?? throw new InvalidOperationException("The continuity source run is unavailable.");
        if (run.Submission.ConversationId != state.ConversationId || run.Submission.ProjectId != state.ProjectId)
            throw new UnauthorizedAccessException("The continuity source conversation/project changed.");
        var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
        if (snapshot.NativeContinuity?.ProjectSettingsRevision != state.ProjectSettingsRevision ||
            snapshot.CortexScope is null || BindingKey(snapshot.CortexScope, state.ProjectSettingsRevision) != state.BindingKey)
            throw new UnauthorizedAccessException("This turn has no matching frozen continuity consent.");
        var settings = legacy.Get<ProjectKnowledgeSettings>("project-knowledge", state.ProjectId);
        if (settings is not { AutomaticContinuity: true } || settings.Revision != state.ProjectSettingsRevision ||
            settings.CortexServerId != state.Scope.ServerId || settings.WriteNamespace != state.Scope.WriteNamespace ||
            state.Scope.ReadNamespaces.Any(space => !settings.ReadNamespaces.Contains(space, StringComparer.Ordinal)))
            throw new UnauthorizedAccessException("Project continuity consent was revoked or changed.");
        var projects = new ProjectKnowledge(legacy, configuration, events);
        _ = projects.Project(state.ProjectId); projects.Validate(settings);
        var scope = WorkflowReferenceRouting.EffectiveCortexScope(legacy, configuration, snapshot, state.ProjectId)!;
        if (scope.ServerId != state.Scope.ServerId || scope.ProviderFingerprint != state.Scope.ProviderFingerprint ||
            scope.WriteNamespace != state.Scope.WriteNamespace || scope.WriteNamespace is null ||
            !scope.ReadNamespaces.Contains(scope.WriteNamespace, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The accepted Cortex write/provider scope is no longer available.");
        var provider = configuration.Get().McpServers.SingleOrDefault(x => x.Id == scope.ServerId);
        if (provider is not { Enabled: true, RequiresConfiguration: false } ||
            CortexOperationPolicy.ProviderFingerprint(provider) != state.Scope.ProviderFingerprint)
            throw new UnauthorizedAccessException("The accepted Cortex provider is no longer available.");
        var profile = WorkerExecutionSnapshot.EffectiveProfile(legacy, snapshot, run.Submission.ProfileId);
        if (!profile.Capabilities.Contains("mcp." + scope.ServerId, StringComparer.Ordinal) ||
            !profile.Capabilities.Contains("cortex.write", StringComparer.Ordinal) ||
            new[] { "cortex_start", "cortex_task", "cortex_finish" }.Any(name =>
                !WorkerExecutionSnapshot.AllowsTool(profile, "mcp." + scope.ServerId + "." + name)))
            throw new UnauthorizedAccessException("Native continuity requires frozen and current start/task/finish plus cortex.write grants.");
        return new(profile, state.ConversationId, run.Id, Cortex: scope with { KnowledgeWriteApproved = false }, FullAccess: false)
            { EnforceProfileScope = true };
    }

    private bool BindingAvailable(State state, string runId)
    {
        try { _ = Context(state, runId); return true; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return false; }
    }

    private async Task<JsonElement> ReadAsync(State state, string runId, string tool, object arguments, CancellationToken token)
    {
        var result = await tools.ExecuteAsync(new("mcp." + state.Scope.ServerId + "." + tool, CerebralJson.Element(arguments)),
            Context(state, runId), token).ConfigureAwait(false);
        if (!result.Success || result.Truncated) throw new IOException("Cortex did not return a complete bounded read.");
        return CortexContext.Payload(result.Content);
    }

    private string? NextCandidate()
    {
        // Bounded native-only projection; StateStore.List would hydrate every unrelated legacy record.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(legacy.DirectoryPath, "cerebral.db"), Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 5 }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM records WHERE kind=$kind AND (
                json_extract(body,'$.status') IN ('queued','attempting') OR
                (json_extract(body,'$.status')='uncertain' AND
                    COALESCE(json_extract(body,'$.reconciliationGeneration'),'') <> $generation) OR
                (json_extract(body,'$.status')='unavailable' AND json_array_length(body,'$.pending') > 0 AND
                    COALESCE(json_extract(body,'$.safeRetryGeneration'),'') <> $generation))
            ORDER BY rowid LIMIT 1;
            """;
        command.Parameters.AddWithValue("$kind", RecordKind);
        command.Parameters.AddWithValue("$generation", recoveryGeneration);
        return command.ExecuteScalar() as string;
    }

    private State Change(string conversationId, Action<State> mutation) => legacy.Locked(() =>
    {
        var current = Get(conversationId) ?? throw new InvalidOperationException("Native continuity state is unavailable.");
        mutation(current); Persist(current); return current;
    });

    private State ChangeBound(State expected, Action<State> mutation) => Change(expected.ConversationId, current =>
    {
        if (current.BindingKey != expected.BindingKey) throw new InvalidOperationException("The continuity binding changed before mutation.");
        mutation(current);
    });

    private void Persist(State state)
    {
        state.UpdatedAt = DateTimeOffset.UtcNow;
        if (state.Pending.Count > MaximumPendingMilestones ||
            JsonSerializer.SerializeToUtf8Bytes(state, CerebralJson.Options).Length > 128 * 1024)
            throw new InvalidDataException("Native continuity state exceeds its bounded private record.");
        legacy.Put(RecordKind, state.ConversationId, state);
    }

    private void SetFailure(string conversationId, string message, string? bindingKey = null)
    {
        try
        {
            var state = Change(conversationId, current =>
            {
                if (bindingKey is not null && current.BindingKey != bindingKey) return;
                if (current.PendingOperation is { Status: "attempting" or "uncertain" } operation)
                {
                    operation.Status = "uncertain"; current.Status = "uncertain";
                }
                else
                {
                    // No dispatch occurred. Retain the exact bounded source/prepared payload for one
                    // safe retry after a host restart; unavailable records are not polled in this host.
                    current.Status = "unavailable"; current.SafeRetryGeneration = recoveryGeneration;
                }
                current.Message = CortexContext.BoundUtf8(message, 1000);
            });
            events.Publish("cortex.continuity.updated", state.LastRunId, state);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { Diagnostic(null, "Native Cortex state could not be updated; an already saved intent remains authoritative. " + Failure(error)); }
    }

    private static void Add(State state, Milestone milestone)
    {
        if (state.Pending.Any(x => x.SourceKey == milestone.SourceKey)) return;
        // Preserve an in-flight source and coalesce older, undispatched observations of the same kind.
        state.Pending.RemoveAll(x => x.Kind == milestone.Kind && x.RunId == milestone.RunId &&
            x.SourceKey != state.PendingOperation?.SourceKey);
        if (state.Pending.Count == MaximumPendingMilestones)
        {
            var first = state.Pending[0];
            var discard = state.Pending.FindIndex(x => x.SourceKey != state.PendingOperation?.SourceKey &&
                (state.TaskReady || x.SourceKey != first.SourceKey || x.Kind != "started"));
            if (discard < 0) return;
            state.Pending.RemoveAt(discard);
        }
        state.Pending.Add(milestone);
    }

    private static Milestone Capture(WorkflowRun run, bool started = false)
    {
        var kind = started || run.Status is WorkflowStatus.Queued or WorkflowStatus.Running ? "started" :
            run.Status == WorkflowStatus.Waiting && run.Questions is not null ? "waiting-user" :
            run.Status == WorkflowStatus.Waiting && run.WaitingForRunIds.Count != 0 ? "waiting-children" :
            run.Status == WorkflowStatus.Waiting && run.Effect?.State == WorkflowEffectState.Unknown ? "unconfirmed-effect" :
            run.Status == WorkflowStatus.Waiting ? "waiting" : run.Status.ToString().ToLowerInvariant();
        if (!started && run.Signal.Kind == WorkflowSignalKind.DependencyResults &&
            run.Status is WorkflowStatus.Queued or WorkflowStatus.Running)
            kind = "children-collected";
        var detail = kind == "waiting-user" ? run.Questions!.Id : kind == "waiting-children"
            ? string.Join(",", run.WaitingForRunIds) : kind == "unconfirmed-effect" ? run.Effect!.Id :
            kind == "children-collected" ? ExecutionReliability.Fingerprint(kind, run.Signal.Data) : "";
        var key = run.Id + ":" + kind + ":" + detail;
        var result = started ? "No final result at admission." : run.IsTerminal && run.Result is { } reported
            ? reported.GetRawText() : run.Error ?? run.WaitingReason ?? "No final result.";
        var evidence = CerebralJson.Element(new
        {
            runId = run.Id, revision = run.Revision, status = run.Status.ToString(), run.Submission.ParentRunId,
            effectId = run.Effect?.Id, effectState = run.Effect?.State.ToString(),
            failedEffects = run.FailedEffects.Take(8).Select(x => new { commandId = x.Key, count = x.Value }).ToArray(),
            waitingChildren = run.WaitingForRunIds.Take(8).ToArray()
        }).GetRawText();
        return new(key, run.Id, run.Revision, run.Submission.ParentRunId, kind,
            CortexContext.BoundUtf8("Cerebral: " + run.Submission.UserText.Replace('\r', ' ').Replace('\n', ' '), 180),
            CortexContext.BoundUtf8(run.Submission.UserText, 1024), CortexContext.BoundUtf8(result, 1500),
            CortexContext.BoundUtf8("Host workflow state (model output is not execution proof): " + evidence, 2000));
    }

    private static string Brief(Milestone milestone) => CortexContext.BoundUtf8(
        "Cerebral conversation turn: " + milestone.RunId + "; milestone: " + milestone.Kind +
        "\nLocal parent run: " + (milestone.ParentRunId ?? "none") +
        "\nObjective excerpt: " + milestone.Objective +
        "\nModel-reported result (verify against service evidence): " + milestone.ReportedResult, 4096);

    private static string BindingKey(CortexAccessScope scope, int revision) =>
        ExecutionReliability.Fingerprint("native-continuity-binding", CerebralJson.Element(new
        { scope.ProjectId, revision, scope.ServerId, scope.ReadNamespaces, scope.WriteNamespace, scope.RequireKnowledgeApproval, scope.ProviderFingerprint }));

    private static void ValidateEcho(JsonElement receipt, State expected)
    {
        if (!receipt.TryGetProperty("effectiveNamespaces", out var spaces) || spaces.ValueKind != JsonValueKind.Array ||
            spaces.GetArrayLength() != 1 || spaces[0].ValueKind != JsonValueKind.String ||
            spaces[0].GetString() != expected.Scope.WriteNamespace)
            throw new InvalidDataException("Cortex did not echo the exact task namespace.");
    }

    private static int TaskRevision(JsonElement task, State state, bool compact = false)
    {
        if (task.GetProperty(compact ? "ns" : "namespace").GetString() != state.Scope.WriteNamespace ||
            task.GetProperty(compact ? "id" : "taskId").GetString() != state.ConversationId ||
            !task.GetProperty(compact ? "rev" : "revision").TryGetInt32(out var revision) || revision < 1)
            throw new InvalidDataException("Cortex returned another task coordinate or an invalid revision.");
        return revision;
    }

    private static string Failure(Exception error) => CortexContext.BoundUtf8(
        error is OperationCanceledException ? "The bounded request was interrupted." : error.Message, 700);

    private void Diagnostic(string? runId, string message) =>
        events.Publish("cortex.continuity.updated", runId, new { status = "unavailable", message = CortexContext.BoundUtf8(message, 1000) });
}
