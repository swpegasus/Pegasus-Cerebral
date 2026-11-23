using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Routing;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

/// <summary>Bounded live active-run view. Continue after the returned row; terminal rows may disappear between pages.</summary>
public sealed record WorkflowRunPage(IReadOnlyList<WorkflowRun> Runs, long? NextAfterRow);
internal sealed record WorkflowStepCommit(WorkflowRun Run, CommittedOutputContext? Output);

/// <summary>Owns workflow state and its canonical events in one local transaction. Never executes effects.</summary>
public sealed partial class WorkflowStore(EventJournal journal)
{
    private const int MaximumRunBytes = 1024 * 1024;
    // This reason is already persisted on interrupted runs from earlier releases.
    internal const string InterruptedWaitReason =
        "Worker execution was interrupted before the next effect. The accepted work can be resumed.";
    // Completion retains data twice (effect and signal), error text three times (also run.Error),
    // and the provider ID twice. JSON can escape one character as six bytes. Reserve the maximum
    // legal additions plus room for wrappers, counters and the terminal state before effects escape.
    private const int MaximumResultErrorCharacters = 8192;
    private const int MaximumProviderReceiptCharacters = 1024;
    private const int MaximumOutcomeExpansionBytes = 2 * WorkflowContract.MaximumStateBytes +
        3 * MaximumResultErrorCharacters * 6 + 2 * MaximumProviderReceiptCharacters * 6 + 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public WorkflowRun Submit(WorkflowSubmission submission) => Submit(submission, null, null);

    public WorkflowRun Submit(WorkflowSubmission submission, ConversationIdleCheckpoint? idleCheckpoint,
        string? settingsFingerprint) => SubmitCore(submission, idleCheckpoint, settingsFingerprint, captureRootUserInput: false).Run;

    /// <summary>Captures only the first committed root User message; no optional callback runs in the transaction.</summary>
    public WorkflowSubmissionCommitResult SubmitWithCommittedRootUserInput(WorkflowSubmission submission,
        ConversationIdleCheckpoint? idleCheckpoint = null, string? settingsFingerprint = null) =>
        SubmitCore(submission, idleCheckpoint, settingsFingerprint, captureRootUserInput: true);

    private WorkflowSubmissionCommitResult SubmitCore(WorkflowSubmission submission,
        ConversationIdleCheckpoint? idleCheckpoint, string? settingsFingerprint, bool captureRootUserInput)
    {
        ValidateSubmission(submission);
        var accepted = Freeze(submission);
        return journal.Transact((connection, transaction) =>
        {
            var existing = Read(connection, transaction, accepted.RequestId);
            if (existing is not null)
            {
                RequireEquivalentRetainedSubmission(existing.Submission, accepted);
                return new WorkflowSubmissionCommitResult(existing, null);
            }
            using var active = EventJournal.Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM workflow_runs WHERE conversation_id=$conversation
                    AND status NOT IN ('Completed','Failed','Cancelled'));
                """);
            active.Parameters.AddWithValue("$conversation", accepted.ConversationId);
            if ((long)active.ExecuteScalar()! != 0)
                throw Conflict("Conversation already has active work. Answer, cancel or finish its current run before submitting another.");
            var boundProjectId = BindConversation(connection, transaction, accepted.ConversationId,
                accepted.ProfileId, accepted.ProjectId);
            accepted = accepted with { ProjectId = boundProjectId };
            ValidateCollaborationAdmission(connection, transaction, accepted);
            if (idleCheckpoint is not null && settingsFingerprint is not null)
                TryAdoptIdleCheckpoint(connection, transaction, accepted, idleCheckpoint, settingsFingerprint);
            accepted = accepted with { ConversationSequence = EventJournal.ReadRevision(connection, transaction, accepted.ConversationId) };
            var now = DateTimeOffset.UtcNow;
            ValidateParent(connection, transaction, accepted);
            var run = new WorkflowRun { Id = accepted.RequestId, Submission = accepted, Revision = 1, CreatedAt = now, UpdatedAt = now };
            var actor = accepted.Caller ?? new(EventActorKind.User);
            // The immediate journal transaction serializes this absence check with both the
            // message append and run insert. A nonzero sequence may contain only Service/system history.
            var firstRootUser = captureRootUserInput && accepted.ParentRunId is null && actor.Kind == EventActorKind.User &&
                !HasEarlierRootUserMessage(connection, transaction, accepted.ConversationId, accepted.ConversationSequence);
            var message = Append(connection, transaction, run, CerebralEventTypes.Message, actor,
                [new TextContentPart(accepted.UserText), .. accepted.AcceptedContent],
                new(ModelInclusion.Required,
                    accepted.IsolatedText is not null ? HumanVisibility.Hidden :
                    actor is { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" } &&
                    accepted.DefinitionId == ConversationWorkflowContract.DefinitionId &&
                    accepted.RequestId.StartsWith("plugin-tests-", StringComparison.Ordinal) &&
                    Guid.TryParseExact(accepted.RequestId["plugin-tests-".Length..], "N", out _)
                        ? HumanVisibility.Activity : HumanVisibility.Transcript),
                actor.Kind switch
                {
                    EventActorKind.Agent => new(EventSourceKind.Agent, AgentId: actor.Id),
                    EventActorKind.Service => new(EventSourceKind.Service, actor.Id),
                    _ => new(EventSourceKind.User)
                });
            if (!string.IsNullOrEmpty(accepted.AssignmentText))
                Append(connection, transaction, run, "workflow.assignment", new(EventActorKind.Service, "cerebral.engine"),
                    [new TextContentPart(accepted.AssignmentText)], new(ModelInclusion.Required, HumanVisibility.Hidden),
                    new(EventSourceKind.Service, "cerebral.engine"));
            Write(connection, transaction, run, insert: true);
            ConsumeConversationCompaction(connection, transaction, accepted, now);
            Transition(connection, transaction, run, "submitted");
            var receipt = firstRootUser ? new CommittedRootUserInputReceipt(accepted.RequestId, accepted.ConversationId,
                accepted.ProfileId, accepted.DefinitionId, accepted.DefinitionVersion, accepted.ProjectId,
                accepted.ConversationSequence, message.Id, message.TurnId, message.Sequence, message.Type,
                message.Actor.Kind, message.Provenance.Source, accepted.ParentRunId, accepted.UserText) : null;
            // Transact commits before returning this value. Rejection/rollback exposes no receipt.
            return new WorkflowSubmissionCommitResult(Freeze(run), receipt);
        });
    }

    private static bool HasEarlierRootUserMessage(SqliteConnection connection, SqliteTransaction transaction,
        string conversationId, long beforeMessageSequence)
    {
        // Follow event actor and retained run lineage, without a definition filter. An unbound
        // User Message conservatively counts as prior root input; only a positively joined child
        // is excluded. Reading only workflow runs or checking adjacency cannot prove absence.
        using var prior = EventJournal.Command(connection, transaction, """
            SELECT EXISTS(
                SELECT 1 FROM conversation_events e
                LEFT JOIN workflow_runs r ON r.id=json_extract(e.payload,'$.turnId')
                    AND r.conversation_id=e.conversation_id
                WHERE e.conversation_id=$conversation AND e.sequence<=$before
                    AND json_extract(e.payload,'$.type')=$message
                    AND json_extract(e.payload,'$.actor.kind')='User'
                    AND (r.id IS NULL OR json_extract(r.submission,'$.parentRunId') IS NULL));
            """);
        prior.Parameters.AddWithValue("$conversation", conversationId);
        prior.Parameters.AddWithValue("$before", beforeMessageSequence);
        prior.Parameters.AddWithValue("$message", CerebralEventTypes.Message);
        return (long)prior.ExecuteScalar()! != 0;
    }

    /// <summary>Read-only use of the same complete immutable-input test used at the durable RID commit.</summary>
    public static void RequireEquivalentRetainedSubmission(WorkflowSubmission retained, WorkflowSubmission candidate)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ValidateSubmission(candidate);
        var accepted = Freeze(candidate) with { ConversationSequence = 0, ProjectId = retained.ProjectId };
        if (!Equivalent(retained with { ConversationSequence = 0 }, accepted))
            throw Conflict("Request ID already belongs to different workflow input.");
    }

    internal static string? BindConversation(SqliteConnection connection, SqliteTransaction transaction,
        string conversationId, string profileId, string? projectId)
    {
        Identifier(conversationId, nameof(conversationId)); Identifier(profileId, nameof(profileId));
        if (projectId is not null) Identifier(projectId, nameof(projectId));
        using var owner = EventJournal.Command(connection, transaction,
            "SELECT profile_id,project_id FROM workflow_conversations WHERE conversation_id=$id;");
        owner.Parameters.AddWithValue("$id", conversationId);
        string? existingProfile = null;
        string? existingProject = null;
        {
            using var reader = owner.ExecuteReader();
            if (reader.Read())
            {
                existingProfile = reader.GetString(0);
                existingProject = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        if (existingProfile is not null)
        {
            if (existingProfile != profileId)
                throw Conflict("Conversation is already bound to another workflow profile.");
            if (existingProject is not null)
            {
                if (projectId is not null && existingProject != projectId)
                    throw Conflict("Conversation is already bound to another project.");
                return existingProject;
            }
            if (existingProject is null && projectId is not null)
            {
                using var priorRuns = EventJournal.Command(connection, transaction,
                    "SELECT EXISTS(SELECT 1 FROM workflow_runs WHERE conversation_id=$id);");
                priorRuns.Parameters.AddWithValue("$id", conversationId);
                if ((long)priorRuns.ExecuteScalar()! == 0)
                {
                    using var bindProject = EventJournal.Command(connection, transaction,
                        "UPDATE workflow_conversations SET project_id=$project WHERE conversation_id=$id AND project_id IS NULL;");
                    bindProject.Parameters.AddWithValue("$project", projectId);
                    bindProject.Parameters.AddWithValue("$id", conversationId);
                    bindProject.ExecuteNonQuery();
                    return projectId;
                }
                throw Conflict("An unbound conversation with retained work requires an explicit owner project binding.");
            }
            // Migrated conversations remain unbound until an explicit owner binding operation is added.
            return null;
        }
        using var bind = EventJournal.Command(connection, transaction,
            "INSERT INTO workflow_conversations(conversation_id,profile_id,project_id) VALUES($id,$profile,$project);");
        bind.Parameters.AddWithValue("$id", conversationId);
        bind.Parameters.AddWithValue("$profile", profileId);
        bind.Parameters.AddWithValue("$project", projectId is null ? DBNull.Value : projectId);
        bind.ExecuteNonQuery();
        return projectId;
    }

    public WorkflowRun Get(string id) => TryGet(id) ?? throw new KeyNotFoundException("Workflow run was not found.");
    public WorkflowRun? TryGet(string id)
    {
        Identifier(id, nameof(id));
        using var connection = journal.Open();
        return Read(connection, null, id);
    }

    /// <summary>Read one retained result within its run, without loading the event history.</summary>
    public WorkflowCommandResult GetEffectResult(string runId, string effectId) => GetEffectResult(runId, effectId, out _);

    /// <summary>The revision and result come from one SQLite statement snapshot, including during reconciliation.</summary>
    public WorkflowCommandResult GetEffectResult(string runId, string effectId, out long runRevision)
    {
        Identifier(runId, nameof(runId)); Identifier(effectId, nameof(effectId));
        // Data has its own 128 KiB bound; error/provider fields may expand to six JSON bytes per character.
        const int maximumReceiptBytes = WorkflowContract.MaximumStateBytes +
            6 * (MaximumResultErrorCharacters + MaximumProviderReceiptCharacters) + 1024;
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT length(CAST(e.result AS BLOB)),
                CASE WHEN length(CAST(e.result AS BLOB)) <= $maximum THEN e.result END, r.revision
            FROM workflow_effect_results e JOIN workflow_runs r ON r.id=e.run_id
            WHERE e.run_id=$run AND e.effect_id=$effect;
            """);
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$effect", effectId);
        command.Parameters.AddWithValue("$maximum", maximumReceiptBytes);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Workflow effect result was not found for this run.");
        if (reader.GetInt64(0) > maximumReceiptBytes || reader.IsDBNull(1))
            throw new InvalidDataException("Stored workflow receipt exceeds the supported size.");
        runRevision = reader.GetInt64(2);
        try
        {
            var result = Deserialize<WorkflowCommandResult>(reader.GetString(1));
            ValidateResult(result);
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Stored workflow receipt violates the result contract.", error);
        }
    }

    public IReadOnlyList<WorkflowRun> ListReady(int limit = 100) => ReadPage("status='Queued'", limit, 0).Runs;

    /// <summary>Partitions serialized candidates only; the host must validate every present marker.</summary>
    public WorkflowRunPage ListReadyPage(bool sourceBoundBackgroundCandidate, int limit = 100, long afterRow = 0) =>
        ReadPage(sourceBoundBackgroundCandidate
            ? "status='Queued' AND json_type(submission,'$.settingsSnapshot.sourceBoundBackground') IS NOT NULL"
            : "status='Queued' AND json_type(submission,'$.settingsSnapshot.sourceBoundBackground') IS NULL", limit, afterRow);
    public IReadOnlyList<WorkflowRun> ListActive(int limit = 200) => ListActivePage(limit).Runs;
    public WorkflowRunPage ListActivePage(int limit = 100, long afterRow = 0) =>
        ReadPage("status NOT IN ('Completed','Failed','Cancelled')", limit, afterRow);
    /// <summary>Inspect one frozen settings value at a time, without expanding activity/result history
    /// or stopping at a viewport page limit. The predicate must not mutate the journal.</summary>
    public bool AnyActiveSettings(Func<JsonElement, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null,
            "SELECT json_extract(submission,'$.settingsSnapshot') FROM workflow_runs WHERE status NOT IN ('Completed','Failed','Cancelled');");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) return true; // Uninspectable active work retains resources.
            using var document = JsonDocument.Parse(reader.GetString(0));
            if (predicate(document.RootElement)) return true;
        }
        return false;
    }
    public WorkflowRecentPage ListRecentPage(int limit = 40, long? beforeRow = null, long? snapshotRow = null)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (beforeRow is < 1 || snapshotRow is < 0) throw new ArgumentOutOfRangeException(nameof(beforeRow));
        using var connection = journal.Open();
        using var snapshot = EventJournal.Command(connection, null, "SELECT COALESCE(MAX(rowid),0) FROM workflow_runs;");
        var current = (long)snapshot.ExecuteScalar()!;
        var bound = snapshotRow ?? current;
        if (bound > current) throw new ArgumentException("The requested history snapshot is unavailable.");
        // Project only small display values in SQL; never hydrate every run's megabyte-sized context.
        using var command = EventJournal.Command(connection, null, """
            SELECT rowid,id,conversation_id,profile_id,status,revision,
                substr(json_extract(submission,'$.userText'),1,160),
                json_extract(submission,'$.definitionId'),json_extract(payload,'$.updatedAt')
            FROM workflow_runs WHERE rowid <= $snapshot AND ($before IS NULL OR rowid < $before)
            ORDER BY rowid DESC LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$snapshot", bound);
        command.Parameters.AddWithValue("$before", beforeRow is { } before ? before : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = command.ExecuteReader();
        var rows = new List<WorkflowRunSummary>(limit); long last = 0; var more = false;
        while (reader.Read())
        {
            if (rows.Count == limit) { more = true; break; }
            last = reader.GetInt64(0);
            var title = reader.GetString(6).Replace('\r', ' ').Replace('\n', ' ');
            rows.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), title,
                reader.GetString(7), Enum.Parse<WorkflowStatus>(reader.GetString(4)), reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new(rows.AsReadOnly(), bound, more ? last : null);
    }

    public int CountActive()
    {
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null,
            "SELECT COUNT(*) FROM workflow_runs WHERE status NOT IN ('Completed','Failed','Cancelled');");
        return checked((int)(long)command.ExecuteScalar()!);
    }

    /// <summary>
    /// Counts work that must keep a task-owned local Worker alive. A cancelled run with an uncertain
    /// prior effect stays active for conversation ownership and later reconciliation, but has no live
    /// handler to drain and therefore must not make graceful source updates impossible forever.
    /// </summary>
    public int CountShutdownBlocking()
    {
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, """
            SELECT COUNT(*)
            FROM workflow_runs
            WHERE status NOT IN ('Completed','Failed','Cancelled')
              AND NOT (
                  status='Waiting'
                  AND COALESCE(json_extract(payload,'$.cancelRequested'),0)=1
                  AND lower(json_extract(payload,'$.effect.state'))='unknown'
              );
            """);
        return checked((int)(long)command.ExecuteScalar()!);
    }
    public bool HasReady()
    {
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, "SELECT EXISTS(SELECT 1 FROM workflow_runs WHERE status='Queued');");
        return (long)command.ExecuteScalar()! != 0;
    }

    public WorkflowRun? TryClaim(string id) => journal.Transact((connection, transaction) =>
    {
        var run = Required(connection, transaction, id);
        if (run.Status != WorkflowStatus.Queued || run.CancelRequested) return null;
        return Save(connection, transaction, run with { Status = WorkflowStatus.Running, WaitingReason = null }, "claimed");
    });

    public WorkflowRun ApplyStep(string id, long expectedRevision, WorkflowStep step, WorkflowCommandDescriptor? command = null) =>
        ApplyStepWithOutput(id, expectedRevision, step, command, null).Run;

    internal WorkflowStepCommit ApplyStepWithOutput(string id, long expectedRevision, WorkflowStep step,
        WorkflowCommandDescriptor? command, ResponseOutputOutcome? response, WorkflowAuditCommit? audit = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        BoundedJson(step.State, nameof(step.State));
        return journal.Transact((connection, transaction) =>
        {
            var run = Expected(connection, transaction, id, expectedRevision);
            if (run.Status != WorkflowStatus.Running || run.CancelRequested) throw Conflict("Workflow is not running.");
            if (run.Effect?.State is WorkflowEffectState.Prepared or WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown)
                throw Conflict("The existing effect must finish before another transition.");
            if (run.Steps >= run.Submission.EffectiveStepLimit) throw Conflict("Workflow step budget is exhausted.");
            if (audit is not null)
            {
                if (audit.Request.RunId != run.Id || audit.Request.RunRevision != run.Revision ||
                    EventJournal.ReadRevision(connection, transaction, run.Submission.ConversationId) != audit.Request.ConversationRevision ||
                    !audit.Fresh())
                    throw Conflict("Audit evidence changed before conditional delivery; the assessment was discarded.");
                Append(connection, transaction, run, "workflow.audit", new(EventActorKind.Service, audit.Result.AdapterId),
                    [], new(ModelInclusion.OnDemand, HumanVisibility.Activity), new(EventSourceKind.Service, audit.Result.AdapterId),
                    Element(new { audit.Request.Purpose, audit.Request.RequestId, audit.Request.EngineEpoch,
                        audit.Request.InputSha256, audit.Request.ProposalSha256, audit.Result, held = audit.Hold }));
                if (audit.Hold)
                    return new WorkflowStepCommit(Save(connection, transaction, run with { Status = WorkflowStatus.Waiting,
                        WaitingReason = "Required audit held this checkpoint: " + audit.Result.Summary }, "audit.held"), null);
            }
            run = run with { State = step.State.Clone(), Steps = run.Steps + 1, Questions = null, Effect = null, Error = null };
            CommittedOutputContext? committedOutput = null;
            switch (step.Next)
            {
                case AskWorkflowQuestions ask:
                    ValidateQuestions(ask.Fields);
                    run = run with { Status = WorkflowStatus.Waiting, WaitingReason = "Waiting for your answers.",
                        Questions = new(Guid.NewGuid().ToString("N"), 1, Freeze(ask.Fields), new Dictionary<string, string>()) };
                    Append(connection, transaction, run, "workflow.questions", new(EventActorKind.Service, "cerebral.engine"),
                        [new StructuredContentPart("workflow.questions", Element(run.Questions))], new(ModelInclusion.Required), new(EventSourceKind.Service, "cerebral.engine"));
                    break;
                case InvokeWorkflowCommand invoke:
                    if (command is null || command.Id != invoke.CommandId || command.Version != invoke.CommandVersion)
                        throw new ArgumentException("The exact registered command descriptor is required.", nameof(command));
                    Identifier(invoke.CommandId, nameof(invoke.CommandId), maximum: 256);
                    if (!Enum.IsDefined(command.ReplayPolicy)) throw new ArgumentException("Unsupported replay policy.");
                    BoundedJson(invoke.Arguments, nameof(invoke.Arguments));
                    run = run with { Effect = new(Guid.NewGuid().ToString("N"), invoke.CommandId, invoke.CommandVersion,
                        invoke.Arguments.Clone(), command.ReplayPolicy, WorkflowEffectState.Prepared) };
                    Append(connection, transaction, run, CerebralEventTypes.ToolCall, FrozenSubmissionAgent(run.Submission),
                        [], new(ModelInclusion.Required, HumanVisibility.Activity), new(EventSourceKind.Service, "cerebral.engine"),
                        Element(new
                        {
                            callId = run.Effect.Id, toolName = invoke.CommandId, arguments = invoke.Arguments,
                            commandVersion = invoke.CommandVersion
                        }));
                    break;
                case CompleteWorkflow complete:
                    BoundedJson(complete.Result, nameof(complete.Result));
                    Text(complete.Error, nameof(complete.Error), 8192);
                    run = run with { Status = complete.Success ? WorkflowStatus.Completed : WorkflowStatus.Failed,
                        Result = complete.Result.Clone(), WaitingReason = null, Error = complete.Error };
                    var original = new ContentPart[] { new StructuredContentPart("workflow.result", complete.Result.Clone()) };
                    if (response is null)
                    {
                        var saved = Append(connection, transaction, run, "workflow.result", FrozenSubmissionAgent(run.Submission),
                            original, new(ModelInclusion.Evidence, run.Submission.IsolatedText is null ? HumanVisibility.Transcript : HumanVisibility.Hidden), new(EventSourceKind.Service, "cerebral.engine"));
                        committedOutput = new(run.Id, saved, saved, []);
                    }
                    else
                    {
                        ValidateResponse(response, run, original);
                        if (!response.Snapshot.Derived)
                        {
                            var saved = Append(connection, transaction, run, "workflow.result", FrozenSubmissionAgent(run.Submission),
                                response.Snapshot.Content, new(ModelInclusion.Evidence, response.Snapshot.HumanVisibility),
                                new(EventSourceKind.Service, "cerebral.engine"));
                            committedOutput = new(run.Id, saved, saved, response.Snapshot.InvokedHooks);
                        }
                        else
                        {
                            var source = Append(connection, transaction, run, "workflow.result.source", FrozenSubmissionAgent(run.Submission),
                                response.Snapshot.OriginalContent,
                                response.Snapshot.OriginalProjection with { Human = HumanVisibility.Hidden },
                                new(EventSourceKind.Service, "cerebral.engine"));
                            var presentation = Append(connection, transaction, run, "workflow.result", FrozenSubmissionAgent(run.Submission),
                                response.Snapshot.Content,
                                new(ModelInclusion.Never, response.Snapshot.HumanVisibility, response.Snapshot.OriginalProjection.Retention),
                                new(EventSourceKind.Service, "cerebral.engine"),
                                Element(new { contract = ResponseOutputHookContract.Id, version = ResponseOutputHookContract.Version,
                                    sourceEventId = source.Id, hooks = response.Snapshot.InvokedHooks }), source.Id);
                            committedOutput = new(run.Id, source, presentation, response.Snapshot.InvokedHooks);
                        }
                    }
                    break;
                case WaitForWorkflowRuns wait:
                    ValidateChildren(connection, transaction, run, wait.RunIds);
                    run = run with { Status = WorkflowStatus.Waiting, WaitingForRunIds = Freeze(wait.RunIds),
                        WaitingReason = "Waiting for child agents." };
                    break;
                default: throw new ArgumentException("Unsupported workflow action.", nameof(step));
            }
            return new WorkflowStepCommit(Save(connection, transaction, run, "advanced"), committedOutput);
        });
    }

    public WorkflowRun DispatchEffect(string id, long expectedRevision, int? executionMilliseconds = null,
        string? failureBudgetKey = null) => journal.Transact((connection, transaction) =>
    {
        var run = Expected(connection, transaction, id, expectedRevision);
        if (run.Status != WorkflowStatus.Running || run.CancelRequested || run.Effect?.State != WorkflowEffectState.Prepared)
            throw Conflict("The prepared effect is no longer eligible for dispatch.");
        var remaining = run.Submission.ActiveTimeLimitSeconds * 1000L - run.ActiveMilliseconds;
        var reservation = executionMilliseconds ?? (int)Math.Min(60_000, remaining);
        if (reservation < 1 || reservation > remaining || reservation > 3_600_000)
            throw Conflict("The workflow's active execution time budget is exhausted or invalid.");
        WorkflowPreparedInterventionRecord? consumedPermit = null;
        if (failureBudgetKey is not null)
        {
            Identifier(failureBudgetKey, nameof(failureBudgetKey));
            if (!run.Submission.FailureLimits.TryGetValue(failureBudgetKey, out var limit))
                throw Conflict("The command's failure budget was not admitted with this workflow.");
            if (run.FailedEffects.GetValueOrDefault(failureBudgetKey) >= limit)
            {
                var effectHash = WorkflowPreparedEffectIdentity.Sha256(run.Effect!);
                consumedPermit = run.PreparedInterventions?.SingleOrDefault(x =>
                    x.Kind == WorkflowPreparedInterventionKind.PermitOnce && !x.Consumed &&
                    x.EffectId == run.Effect.Id && x.EffectSha256 == effectHash &&
                    x.BudgetKey == failureBudgetKey);
                if (consumedPermit is null)
                    throw Conflict("The command's failed-attempt limit has been reached. User intervention is required.");
            }
        }
        // Reserve before effects escape. A crash cannot reset the time budget; an uncertain effect
        // conservatively retains its reservation until a known execution returns a measured duration.
        var dispatched = run with
        {
            ActiveMilliseconds = checked(run.ActiveMilliseconds + reservation),
            Effect = run.Effect with { State = WorkflowEffectState.Dispatched,
                ReservedExecutionMilliseconds = reservation, FailureBudgetKey = failureBudgetKey },
            PreparedInterventions = consumedPermit is null ? run.PreparedInterventions :
                run.PreparedInterventions!.Select(x => x.RequestId == consumedPermit.RequestId
                    ? x with { Consumed = true } : x).ToArray()
        };
        if (Encoding.UTF8.GetByteCount(Serialize(dispatched)) > MaximumRunBytes - MaximumOutcomeExpansionBytes)
            throw new ArgumentException("Workflow context and state leave insufficient space to retain the execution receipt. No command was dispatched. Reduce the accepted context or continuation state before starting a new request.");
        if (consumedPermit is not null)
            Append(connection, transaction, dispatched, "workflow.effect.permit-consumed",
                new(EventActorKind.Service, "cerebral.engine"), [],
                new(ModelInclusion.OnDemand, HumanVisibility.Activity),
                new(EventSourceKind.Service, "cerebral.engine"),
                Element(new { runId = dispatched.Id, effectId = dispatched.Effect!.Id,
                    permitRequestId = consumedPermit.RequestId, budgetKey = failureBudgetKey }));
        return Save(connection, transaction, dispatched, "effect.dispatched");
    });

    public WorkflowRun CompleteEffect(string id, string effectId, WorkflowCommandResult result, long? elapsedMilliseconds = null) =>
        CompleteEffectCore(id, effectId, result, elapsedMilliseconds, expectedRevision: null);

    /// <summary>
    /// Completes only a confirmed reconciliation of an exact inactive uncertain tuple. Receipt,
    /// cancellation terminalization, accounting, journal evidence and revision share one transaction.
    /// </summary>
    public WorkflowRun CompleteReconciledEffect(string id, long expectedRevision, string effectId,
        WorkflowCommandResult result, long? elapsedMilliseconds = null)
    {
        if (expectedRevision < 1 || expectedRevision == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(result);
        if (result.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed or WorkflowEffectState.Cancelled))
            throw new ArgumentException("Reconciliation requires a confirmed original outcome.", nameof(result));
        return CompleteEffectCore(id, effectId, result, elapsedMilliseconds, expectedRevision);
    }

    private WorkflowRun CompleteEffectCore(string id, string effectId, WorkflowCommandResult result,
        long? elapsedMilliseconds, long? expectedRevision)
    {
        Identifier(effectId, nameof(effectId));
        ValidateResult(result);
        if (elapsedMilliseconds is < 0) throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        return journal.Transact((connection, transaction) =>
        {
            var run = Required(connection, transaction, id);
            using var receipt = EventJournal.Command(connection, transaction, "SELECT run_id,result FROM workflow_effect_results WHERE effect_id=$id;");
            receipt.Parameters.AddWithValue("$id", effectId);
            using (var reader = receipt.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != id) throw Conflict("Effect belongs to another run.");
                    var previous = Deserialize<WorkflowCommandResult>(reader.GetString(1));
                    if (Equivalent(previous, result))
                    {
                        // Only the exact immediate confirmed commit can satisfy a fenced retry.
                        // An unrelated later revision or another current effect is not equivalent.
                        if (expectedRevision is { } completedRevision && (run.Revision != completedRevision + 1 ||
                            run.Effect?.Id != effectId || run.Effect.State != result.State))
                            throw Conflict("The expected reconciliation tuple changed after receipt completion.");
                        return run;
                    }
                    if (previous.State != WorkflowEffectState.Unknown) throw Conflict("Effect already has a different confirmed result.");
                }
            }
            if (run.IsTerminal)
                throw Conflict("Terminal workflow evidence cannot be replaced or reopened; retained receipts remain read-only.");
            if (expectedRevision is { } requiredRevision && (run.Revision != requiredRevision ||
                run.Status != WorkflowStatus.Waiting || run.Effect?.State != WorkflowEffectState.Unknown))
                throw Conflict("The expected inactive uncertain run/effect revision changed.");
            if (run.Effect?.Id != effectId || run.Effect.State is not (WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown))
                throw Conflict("Effect is not dispatched or awaiting reconciliation.");
            var charged = run.ActiveMilliseconds;
            if (elapsedMilliseconds is { } elapsed)
            {
                var measured = Math.Max(1, elapsed);
                if (result.State == WorkflowEffectState.Unknown) measured = Math.Max(measured, run.Effect.ReservedExecutionMilliseconds);
                charged = checked(charged - run.Effect.ReservedExecutionMilliseconds + measured);
            }
            var failures = new Dictionary<string, int>(run.FailedEffects, StringComparer.Ordinal);
            if (result.State == WorkflowEffectState.Failed && run.Effect.FailureBudgetKey is { } key)
                failures[key] = checked(failures.GetValueOrDefault(key) + 1);
            var effect = run.Effect with { State = result.State, Result = result.Data?.Clone(), Error = result.Error, ProviderReceiptId = result.ProviderReceiptId };
            var unknown = result.State == WorkflowEffectState.Unknown;
            var cancelled = !unknown && (run.CancelRequested || result.State == WorkflowEffectState.Cancelled);
            run = run with { Effect = effect, Signal = new(WorkflowSignalKind.EffectResult, Element(result)),
                ActiveMilliseconds = charged, FailedEffects = failures,
                Status = unknown ? WorkflowStatus.Waiting : cancelled ? WorkflowStatus.Cancelled : WorkflowStatus.Queued,
                WaitingReason = unknown ? "Effect outcome is uncertain; reconcile the existing operation before continuing." : null,
                Error = result.Error };
            CommitConversationCheckpoint(connection, transaction, run, effect, result);
            using var retain = EventJournal.Command(connection, transaction, """
                INSERT INTO workflow_effect_results(effect_id,run_id,result) VALUES($id,$run,$result)
                ON CONFLICT(effect_id) DO UPDATE SET result=excluded.result;
                """);
            retain.Parameters.AddWithValue("$id", effectId); retain.Parameters.AddWithValue("$run", id);
            retain.Parameters.AddWithValue("$result", Serialize(result)); retain.ExecuteNonQuery();
            Append(connection, transaction, run, CerebralEventTypes.ToolResult, new(EventActorKind.Tool, effect.CommandId),
                [new StructuredContentPart("workflow.command.result", Element(result))], new(ModelInclusion.Evidence, HumanVisibility.Activity),
                new(EventSourceKind.Tool, effect.CommandId), Element(new ToolResultData(effect.Id, effect.CommandId,
                    result.State == WorkflowEffectState.Succeeded, Error: result.Error, DurationMilliseconds: elapsedMilliseconds)));
            return Save(connection, transaction, run, unknown ? "effect.uncertain" : "effect.finished");
        });
    }

    public WorkflowRun Answer(WorkflowAnswerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId)); Identifier(request.RunId, nameof(request.RunId));
        Identifier(request.QuestionSetId, nameof(request.QuestionSetId));
        if (request.Answers is null || request.Answers.Count > 16) throw new ArgumentException("Supply at most 16 field answers.");
        foreach (var (key, value) in request.Answers) { Identifier(key, "answer key"); Text(value, "answer", 8192, required: true); }
        var accepted = Freeze(request);
        return journal.Transact((connection, transaction) =>
        {
            // A lost-response retry must succeed even after this question set or run has advanced.
            using var prior = EventJournal.Command(connection, transaction, "SELECT request,response FROM workflow_answers WHERE request_id=$id;");
            prior.Parameters.AddWithValue("$id", accepted.RequestId);
            using (var reader = prior.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (!Equivalent(Deserialize<WorkflowAnswerRequest>(reader.GetString(0)), accepted)) throw Conflict("Answer request ID already belongs to different input.");
                    return Deserialize<WorkflowRun>(reader.GetString(1));
                }
            }
            var run = Required(connection, transaction, accepted.RunId);
            var questions = run.Questions;
            if (run.Status != WorkflowStatus.Waiting || run.CancelRequested || questions is null || questions.Id != accepted.QuestionSetId || questions.Revision != accepted.ExpectedQuestionRevision)
                throw Conflict("Question set is stale, foreign, cancelled, or already answered.");
            var answers = new Dictionary<string, string>(questions.Answers, StringComparer.Ordinal);
            foreach (var (key, value) in accepted.Answers)
            {
                var field = questions.Fields.SingleOrDefault(x => x.Id == key) ?? throw Conflict("Answer names an unknown question field.");
                if (!WorkflowQuestionAnswers.IsValid(field, value)) throw new ArgumentException("Answer is not a valid selection of the allowed choices.");
                answers[key] = value;
            }
            var complete = questions.Fields.Where(x => x.Required).All(x => answers.ContainsKey(x.Id));
            if (accepted.Answers.Count == 0 && !complete)
                throw new ArgumentException("Required answers are still missing.");
            if (run.LocalChildQuestionRelay is { } localRelay)
                run = AnswerLocalChildQuestion(connection, transaction, run, questions, localRelay,
                    accepted, answers, complete);
            else
            {
                run = run with { Questions = questions with { Revision = checked(questions.Revision + 1), Answers = answers },
                    Status = complete ? WorkflowStatus.Queued : WorkflowStatus.Waiting,
                    Signal = complete ? new(WorkflowSignalKind.Answers, Element(answers)) : run.Signal,
                    WaitingReason = complete ? null : "Waiting for remaining answers." };
                Append(connection, transaction, run, "workflow.answers", new(EventActorKind.User),
                    [new StructuredContentPart("workflow.answers", Element(WorkflowQuestionAnswers.Values(questions.Fields, accepted.Answers)))], new(ModelInclusion.Required), new(EventSourceKind.User));
                run = Save(connection, transaction, run, complete ? "answers.completed" : "answers.partial");
            }
            using var record = EventJournal.Command(connection, transaction, "INSERT INTO workflow_answers(request_id,run_id,request,response) VALUES($id,$run,$request,$response);");
            record.Parameters.AddWithValue("$id", accepted.RequestId); record.Parameters.AddWithValue("$run", run.Id);
            record.Parameters.AddWithValue("$request", Serialize(accepted)); record.Parameters.AddWithValue("$response", Serialize(run)); record.ExecuteNonQuery();
            return run;
        });
    }

    private static WorkflowRun AnswerLocalChildQuestion(SqliteConnection connection,
        SqliteTransaction transaction, WorkflowRun parent, WorkflowQuestionSet sourceQuestions,
        LocalChildQuestionRelay binding, WorkflowAnswerRequest accepted,
        IReadOnlyDictionary<string, string> answers, bool complete)
    {
        if (parent.Submission.ParentRunId is not null || binding.ParentRunId != parent.Id ||
            binding.ParentQuestionSetId != sourceQuestions.Id ||
            binding.ParentQuestionRevision != sourceQuestions.Revision ||
            !parent.WaitingForRunIds.SequenceEqual(binding.WaitingForRunIds, StringComparer.Ordinal) ||
            !parent.WaitingForRunIds.Contains(binding.ChildRunId, StringComparer.Ordinal) ||
            binding.ChildCaller is not { Kind: EventActorKind.Service, Id: "cerebral.coordination" } ||
            LocalQuestionFieldsSha(sourceQuestions.Fields) != binding.FieldsSha256)
            throw Conflict("The parent child-question binding changed before the owner answer.");
        var child = Required(connection, transaction, binding.ChildRunId);
        var targetQuestions = child.Questions;
        if (parent.CancelRequested || child.CancelRequested || child.Status != WorkflowStatus.Waiting ||
            child.Revision != binding.ChildRunRevision ||
            child.Submission.ParentRunId != parent.Id || child.Submission.Caller != binding.ChildCaller ||
            targetQuestions is null ||
            targetQuestions.Id != binding.ChildQuestionSetId ||
            targetQuestions.Revision != binding.ChildQuestionRevision ||
            LocalQuestionFieldsSha(targetQuestions.Fields) != binding.FieldsSha256 ||
            LocalQuestionAnswersSha(targetQuestions.Answers) != binding.ChildAnswersSha256 ||
            targetQuestions.Answers.Any(answer => !answers.ContainsKey(answer.Key)))
            throw Conflict("The child question changed before the owner answer; inspect the saved parent form.");
        // A partial form stays visible on Main. Do not touch or implicitly merge it into the child.
        if (!complete)
        {
            var partial = parent with
            {
                Questions = sourceQuestions with { Revision = checked(sourceQuestions.Revision + 1), Answers = answers },
                LocalChildQuestionRelay = binding with { ParentQuestionRevision = checked(sourceQuestions.Revision + 1) },
                WaitingReason = "Waiting for remaining answers to the child agent's question."
            };
            Append(connection, transaction, partial, "workflow.answers", new(EventActorKind.User),
                [new StructuredContentPart("workflow.answers",
                    Element(WorkflowQuestionAnswers.Values(sourceQuestions.Fields, accepted.Answers)))],
                new(ModelInclusion.Required), new(EventSourceKind.User));
            return Save(connection, transaction, partial, "children.answers.partial");
        }
        var delivered = child with
        {
            Questions = targetQuestions with { Revision = checked(targetQuestions.Revision + 1), Answers = answers },
            Status = WorkflowStatus.Queued,
            Signal = new(WorkflowSignalKind.Answers, Element(answers)),
            WaitingReason = null
        };
        Append(connection, transaction, delivered, "workflow.answers", new(EventActorKind.User),
            [new StructuredContentPart("workflow.answers",
                Element(WorkflowQuestionAnswers.Values(targetQuestions.Fields, answers)))],
            new(ModelInclusion.Required), new(EventSourceKind.User));
        _ = Save(connection, transaction, delivered, "children.answers.delivered");
        var waiting = parent with { Questions = null, LocalChildQuestionRelay = null,
            WaitingReason = "Waiting for child agents." };
        Append(connection, transaction, waiting, "workflow.answers", new(EventActorKind.User),
            [new StructuredContentPart("workflow.answers",
                Element(WorkflowQuestionAnswers.Values(sourceQuestions.Fields, accepted.Answers)))],
            new(ModelInclusion.Required), new(EventSourceKind.User));
        return Save(connection, transaction, waiting, "children.answers.relayed");
    }

    public WorkflowRun Cancel(string id) => journal.Transact((connection, transaction) => CancelTree(connection, transaction, id));

    private static WorkflowRun CancelTree(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        var run = Required(connection, transaction, id);
        var childIds = new List<string>();
        using (var children = EventJournal.Command(connection, transaction,
                   "SELECT id FROM workflow_runs WHERE json_extract(submission,'$.parentRunId')=$id AND status NOT IN ('Completed','Failed','Cancelled');"))
        {
            children.Parameters.AddWithValue("$id", id);
            using var reader = children.ExecuteReader();
            while (reader.Read()) childIds.Add(reader.GetString(0));
        }
        foreach (var childId in childIds) CancelTree(connection, transaction, childId);
        if (run.IsTerminal || run.CancelRequested) return run;
        var dispatched = run.Effect?.State == WorkflowEffectState.Dispatched;
        var unknown = run.Effect?.State == WorkflowEffectState.Unknown;
        return Save(connection, transaction, run with { CancelRequested = true,
            Status = dispatched ? WorkflowStatus.Cancelling : unknown ? WorkflowStatus.Waiting : WorkflowStatus.Cancelled,
            WaitingReason = dispatched ? "Cancellation requested; waiting for the owned operation to stop." : unknown ? "Cancellation requested; the prior effect still requires reconciliation." : null,
            Effect = run.Effect?.State == WorkflowEffectState.Prepared ? run.Effect with { State = WorkflowEffectState.Cancelled } : run.Effect }, "cancel.requested");
    }

    public WorkflowRun Resume(string id, long expectedRevision) => journal.Transact((connection, transaction) =>
    {
        var run = Expected(connection, transaction, id, expectedRevision);
        if (run.Status != WorkflowStatus.Waiting || run.CancelRequested) throw Conflict("Workflow is not resumable.");
        if (run.Effect?.State is WorkflowEffectState.Unknown or WorkflowEffectState.Dispatched) throw Conflict("Reconcile the existing effect before resuming.");
        if (run.WaitingForRunIds.Count != 0) throw Conflict("Child agents have not been collected by the scheduler.");
        if (run.Questions is { } questions && questions.Fields.Any(x => x.Required && !questions.Answers.ContainsKey(x.Id)))
            throw Conflict("Required answers are still missing.");
        if (run.Steps >= run.Submission.EffectiveStepLimit && run.Effect?.State != WorkflowEffectState.Prepared)
            throw Conflict("Workflow step budget is exhausted.");
        return Save(connection, transaction, run with { Status = WorkflowStatus.Queued, WaitingReason = null, Error = null,
            Signal = run.Questions is { } answered ? new(WorkflowSignalKind.Answers, Element(answered.Answers)) : run.Signal }, "resumed");
    });

    /// <summary>Release only a safe host claim. Prepared intent retains its identity; an executing
    /// or uncertain effect can never be made ready merely to replace an engine.</summary>
    public WorkflowRun YieldForEnginePause(string id, long expectedRevision) => journal.Transact((connection, transaction) =>
    {
        var run = Expected(connection, transaction, id, expectedRevision);
        if (run.Status != WorkflowStatus.Running || run.CancelRequested) return run;
        if (run.Effect?.State is WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown)
            throw Conflict("An executing or uncertain effect cannot yield to engine activation.");
        return Save(connection, transaction, run with { Status = WorkflowStatus.Queued,
            WaitingReason = "Engine paused; the accepted work and prepared intent are retained." }, "engine.paused");
    });

    public WorkflowRun Hold(string id, long expectedRevision, string reason)
    {
        Text(reason, nameof(reason), 8192, required: true);
        return journal.Transact((connection, transaction) =>
        {
            var run = Expected(connection, transaction, id, expectedRevision);
            if (run.IsTerminal) return run;
            // A scheduler reaches Hold only after it no longer owns an executing handler. A
            // retained Dispatched marker is therefore an inactive, uncertain receipt and must be
            // made reconcilable; leaving it Dispatched strands the run with every recovery control
            // disabled.
            var effect = run.Effect?.State == WorkflowEffectState.Dispatched
                ? run.Effect with { State = WorkflowEffectState.Unknown }
                : run.Effect;
            return Save(connection, transaction, run with { Status = WorkflowStatus.Waiting,
                WaitingReason = reason, Effect = effect }, "held");
        });
    }

    public WorkflowRun Fail(string id, long expectedRevision, string error)
    {
        Text(error, nameof(error), 8192, required: true);
        return journal.Transact((connection, transaction) =>
        {
            var run = Expected(connection, transaction, id, expectedRevision);
            if (run.IsTerminal) return run;
            var uncertain = run.Effect?.State is WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown;
            return Save(connection, transaction, run with { Status = uncertain ? WorkflowStatus.Waiting : WorkflowStatus.Failed,
                Error = error, WaitingReason = uncertain ? "The effect may have executed; reconcile it before continuing." : null,
                Effect = uncertain ? run.Effect! with { State = WorkflowEffectState.Unknown } : run.Effect }, "failed");
        });
    }

    public int RecoverInterrupted() => RecoverInterrupted(idleCutoff: null);

    public int RecoverInterrupted(DateTimeOffset? idleCutoff) => journal.Transact((connection, transaction) =>
    {
        var recovered = 0;
        while (true)
        {
            // Do not materialize every interrupted run and its source input at once on startup.
            using var select = EventJournal.Command(connection, transaction, """
                SELECT payload FROM workflow_runs
                WHERE status IN ('Running','Cancelling')
                   OR (status='Waiting' AND json_extract(payload,'$.effect.state')='Dispatched')
                ORDER BY rowid LIMIT 4;
                """);
            var runs = new List<WorkflowRun>();
            using (var reader = select.ExecuteReader()) while (reader.Read()) runs.Add(Deserialize<WorkflowRun>(reader.GetString(0)));
            if (runs.Count == 0) break;
            foreach (var run in runs)
            {
                if (idleCutoff is { } cutoff && run.Status == WorkflowStatus.Running &&
                    CanExpireInterrupted(connection, transaction, run, cutoff))
                {
                    ExpireInterrupted(connection, transaction, run);
                    recovered++;
                    continue;
                }
                var uncertain = run.Effect?.State is WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown;
                Save(connection, transaction, run with { Status = uncertain ? WorkflowStatus.Waiting : run.CancelRequested ? WorkflowStatus.Cancelled : WorkflowStatus.Queued,
                    Effect = uncertain ? run.Effect! with { State = WorkflowEffectState.Unknown } : run.Effect,
                    WaitingReason = uncertain ? "Service restarted after dispatch; inspect the existing operation before continuing." : null }, "recovered");
                recovered++;
            }
        }
        return recovered;
    });

    public IReadOnlyList<WorkflowRun> ExpireIdleInterrupted(DateTimeOffset cutoff) =>
        journal.Transact<IReadOnlyList<WorkflowRun>>((connection, transaction) =>
        {
            using var select = EventJournal.Command(connection, transaction, """
                SELECT run.payload FROM workflow_runs run
                WHERE run.status='Waiting' AND json_extract(run.payload,'$.waitingReason')=$interrupted
                  AND julianday(json_extract(run.payload,'$.updatedAt')) > julianday('0001-01-01')
                  AND julianday(json_extract(run.payload,'$.updatedAt')) <= julianday($cutoff)
                  AND COALESCE(json_extract(run.payload,'$.cancelRequested'),0)=0
                  AND json_extract(run.payload,'$.questions') IS NULL
                  AND COALESCE(json_array_length(run.payload,'$.waitingForRunIds'),0)=0
                  AND lower(COALESCE(json_extract(run.payload,'$.effect.state'),'')) IN
                      ('','prepared','succeeded','failed','cancelled','rejectedbeforedispatch')
                  AND NOT EXISTS (SELECT 1 FROM workflow_runs child
                      WHERE json_extract(child.submission,'$.parentRunId')=run.id
                        AND child.status NOT IN ('Completed','Failed','Cancelled'))
                ORDER BY run.rowid LIMIT 8;
                """);
            select.Parameters.AddWithValue("$interrupted", InterruptedWaitReason);
            select.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O"));
            var candidates = new List<WorkflowRun>();
            using (var reader = select.ExecuteReader())
                while (reader.Read()) candidates.Add(Deserialize<WorkflowRun>(reader.GetString(0)));
            var expired = new List<WorkflowRun>();
            foreach (var run in candidates)
                if (CanExpireInterrupted(connection, transaction, run, cutoff))
                    expired.Add(ExpireInterrupted(connection, transaction, run));
            return expired;
        });

    private static bool CanExpireInterrupted(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun run, DateTimeOffset cutoff)
    {
        if (run.UpdatedAt == default || run.UpdatedAt > cutoff || run.CancelRequested ||
            run.Questions is not null || run.WaitingForRunIds.Count != 0 ||
            run.Effect?.State is not (null or WorkflowEffectState.Prepared or WorkflowEffectState.Succeeded or
                WorkflowEffectState.Failed or WorkflowEffectState.Cancelled or WorkflowEffectState.RejectedBeforeDispatch))
            return false;
        using var children = EventJournal.Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM workflow_runs
                WHERE json_extract(submission,'$.parentRunId')=$id
                  AND status NOT IN ('Completed','Failed','Cancelled'));
            """);
        children.Parameters.AddWithValue("$id", run.Id);
        return (long)children.ExecuteScalar()! == 0;
    }

    private static WorkflowRun ExpireInterrupted(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun run) => Save(connection, transaction, run with
        {
            Status = WorkflowStatus.Failed,
            Error = "This interrupted workflow expired after exceeding its configured idle time.",
            WaitingReason = null,
            Effect = run.Effect is { State: WorkflowEffectState.Prepared } effect
                ? effect with { State = WorkflowEffectState.Cancelled } : run.Effect
        }, "expired");

    private WorkflowRunPage ReadPage(string predicate, int limit, long afterRow)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterRow < 0) throw new ArgumentOutOfRangeException(nameof(afterRow));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null, $"SELECT rowid,payload FROM workflow_runs WHERE {predicate} AND rowid>$after ORDER BY rowid LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$after", afterRow);
        using var reader = command.ExecuteReader();
        var runs = new List<WorkflowRun>(); var bytes = 0; var lastRow = afterRow; var more = false;
        while (reader.Read())
        {
            var payload = reader.GetString(1); var length = Encoding.UTF8.GetByteCount(payload);
            if (length > MaximumRunBytes) throw new InvalidDataException("Stored workflow value exceeds the supported size.");
            if (runs.Count == limit || bytes + length > EventJournal.MaximumPageBytes) { more = true; break; }
            runs.Add(Deserialize<WorkflowRun>(payload));
            bytes += length; lastRow = reader.GetInt64(0);
        }
        return new(runs.AsReadOnly(), more ? lastRow : null);
    }

    private static WorkflowRun Required(SqliteConnection connection, SqliteTransaction transaction, string id) =>
        Read(connection, transaction, id) ?? throw new KeyNotFoundException("Workflow run was not found.");
    private static WorkflowRun Expected(SqliteConnection connection, SqliteTransaction transaction, string id, long revision)
    {
        var run = Required(connection, transaction, id);
        if (run.Revision != revision) throw Conflict($"Workflow changed: expected revision {revision}, actual {run.Revision}.");
        return run;
    }
    private static WorkflowRun? Read(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        Identifier(id, nameof(id));
        using var command = EventJournal.Command(connection, transaction, "SELECT payload FROM workflow_runs WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string payload ? Deserialize<WorkflowRun>(payload) : null;
    }
    private static WorkflowRun Save(SqliteConnection connection, SqliteTransaction transaction, WorkflowRun run, string reason)
    {
        run = run with { Revision = checked(run.Revision + 1), UpdatedAt = DateTimeOffset.UtcNow };
        Write(connection, transaction, run); Transition(connection, transaction, run, reason); return Freeze(run);
    }
    private static void Write(SqliteConnection connection, SqliteTransaction transaction, WorkflowRun run, bool insert = false)
    {
        var payload = Serialize(run);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumRunBytes) throw new ArgumentException("Workflow state exceeds the 1 MiB run bound.");
        using var command = EventJournal.Command(connection, transaction, insert ? """
            INSERT INTO workflow_runs(id,request_id,conversation_id,profile_id,status,revision,submission,payload)
            VALUES($id,$id,$conversation,$profile,$status,$revision,$submission,$payload);
            """ : "UPDATE workflow_runs SET status=$status,revision=$revision,payload=$payload WHERE id=$id;");
        command.Parameters.AddWithValue("$id", run.Id); command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$revision", run.Revision); command.Parameters.AddWithValue("$payload", payload);
        if (insert)
        {
            command.Parameters.AddWithValue("$conversation", run.Submission.ConversationId); command.Parameters.AddWithValue("$profile", run.Submission.ProfileId);
            command.Parameters.AddWithValue("$submission", Serialize(run.Submission));
        }
        if (command.ExecuteNonQuery() != 1) throw Conflict("Workflow no longer exists.");
    }
    private static void Transition(SqliteConnection connection, SqliteTransaction transaction, WorkflowRun run, string reason) =>
        Append(connection, transaction, run, CerebralEventTypes.WorkflowTransition, new(EventActorKind.Service, "cerebral.engine"), [],
            new(ModelInclusion.OnDemand, HumanVisibility.Activity, RetentionPolicy.Rolling), new(EventSourceKind.Service, "cerebral.engine"),
            Element(new { runId = run.Id, run.Revision, run.Status, reason, run.WaitingReason, run.Error, run.CancelRequested, run.Steps,
                run.ActiveMilliseconds, run.FailedEffects }));
    private static CerebralEvent Append(SqliteConnection connection, SqliteTransaction transaction, WorkflowRun run, string type, EventActor actor,
        IReadOnlyList<ContentPart> content, EventProjectionPolicy projection, EventProvenance provenance, JsonElement? data = null,
        string? parentEventId = null)
    {
        var item = new CerebralEvent(Guid.NewGuid().ToString("N"), run.Submission.ConversationId, run.Id, 0, type, actor,
            content, projection, provenance with { JobId = run.Id }, DateTimeOffset.UtcNow, data,
            ParentEventId: parentEventId, CorrelationId: run.Effect?.Id ?? run.Id);
        return EventJournal.AppendInTransaction(connection, transaction, item,
            EventJournal.ReadRevision(connection, transaction, item.ConversationId)).Event;
    }

    private static void ValidateResponse(ResponseOutputOutcome response, WorkflowRun run, IReadOnlyList<ContentPart> original)
    {
        if (response.Disposition != RouteDisposition.Continue || response.StoppedByHook is not null || response.Reason is not null)
            throw new ArgumentException("Only a continuing response route can be committed.", nameof(response));
        var snapshot = response.Snapshot ?? throw new ArgumentException("Response route has no snapshot.", nameof(response));
        if (snapshot.RequestId != run.Id || snapshot.ConversationId != run.Submission.ConversationId || snapshot.TurnId != run.Id)
            throw new ArgumentException("Response route identity does not match the workflow result.", nameof(response));
        if (snapshot.OriginalProjection.Model != ModelInclusion.Evidence ||
            snapshot.OriginalProjection.Human != HumanVisibility.Transcript ||
            snapshot.OriginalProjection.Retention != RetentionPolicy.Durable)
            throw new ArgumentException("Response route changed the host-owned source projection.", nameof(response));
        var expected = JsonSerializer.SerializeToElement<IReadOnlyList<ContentPart>>(original, Json);
        var actual = JsonSerializer.SerializeToElement<IReadOnlyList<ContentPart>>(snapshot.OriginalContent, Json);
        if (!JsonElement.DeepEquals(expected, actual))
            throw new ArgumentException("Response route does not preserve the original workflow result.", nameof(response));
        if (!Enum.IsDefined(snapshot.HumanVisibility) || snapshot.Content.IsDefault || snapshot.OriginalContent.IsDefault ||
            snapshot.InvokedHooks.IsDefault)
            throw new ArgumentException("Response route snapshot is incomplete.", nameof(response));
    }
    private static void ValidateSubmission(WorkflowSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        Identifier(submission.RequestId, nameof(submission.RequestId)); Identifier(submission.ConversationId, nameof(submission.ConversationId));
        Identifier(submission.ProfileId, nameof(submission.ProfileId)); Identifier(submission.DefinitionId, nameof(submission.DefinitionId), maximum: 256);
        Identifier(submission.DefinitionVersion, nameof(submission.DefinitionVersion));
        WorkflowAcceptedContent.Validate(submission.AcceptedContent);
        ArgumentNullException.ThrowIfNull(submission.UserText);
        Text(submission.UserText, nameof(submission.UserText), 128 * 1024, required: submission.AcceptedContent.Count == 0);
        Text(submission.AssignmentText, nameof(submission.AssignmentText), 128 * 1024);
        BoundedJson(submission.Input, nameof(submission.Input)); BoundedJson(submission.SettingsSnapshot, nameof(submission.SettingsSnapshot));
        submission.LocalGeneration?.Validate();
        if (submission.IsolatedText is { } isolated && (isolated.SchemaVersion != 1 || isolated.ProfileRevision < 1 ||
            submission.DefinitionId != Cerebral.Contracts.Models.IsolatedTextGenerationContract.DefinitionId ||
            submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.isolated-text" }))
            throw new ArgumentException("Isolated advisory work requires its exact host admission marker.");
        if (submission.StepLimit is < 1 or > WorkflowContract.MaximumSteps) throw new ArgumentOutOfRangeException(nameof(submission.StepLimit));
        if (submission.PolicyStepLimit is < 1 or > WorkflowContract.MaximumSteps) throw new ArgumentOutOfRangeException(nameof(submission.PolicyStepLimit));
        if (submission.ActiveTimeLimitSeconds is < 1 or > 86_400) throw new ArgumentOutOfRangeException(nameof(submission.ActiveTimeLimitSeconds));
        if (submission.FailureLimits is null || submission.FailureLimits.Count > 32) throw new ArgumentException("Failure limits must contain at most 32 categories.");
        foreach (var (key, limit) in submission.FailureLimits)
        {
            Identifier(key, "failure budget key");
            if (limit is < 1 or > 100) throw new ArgumentException("Failed-attempt limits must be 1–100.");
        }
        // The host may admit an internal service-owned attempt (for example, a collaboration target run).
        // Transport contracts do not expose Caller, so this cannot be selected by an external submitter.
        if (submission.Caller is { Kind: not (EventActorKind.User or EventActorKind.Agent or EventActorKind.Service) })
            throw new ArgumentException("Workflow caller must be an admitted user, agent, or host service.");
        if (submission.HostAddedRequiredSuccessfulTools is null || submission.HostAddedRequiredSuccessfulTools.Count > 8 ||
            submission.HostAddedRequiredSuccessfulTools.Distinct(StringComparer.Ordinal).Count() != submission.HostAddedRequiredSuccessfulTools.Count)
            throw new ArgumentException("Host-added completion requirements must contain at most eight distinct tool names.");
        foreach (var tool in submission.HostAddedRequiredSuccessfulTools)
            Identifier(tool, "host-added completion requirement", maximum: 256);
        if (submission.HostAddedRequiredSuccessfulTools.Count != 0 && submission.Caller?.Kind != EventActorKind.Service)
            throw new ArgumentException("Only an admitted host service may add completion-requirement provenance.");
        if (submission.HostPluginTestMountIds is null ||
            submission.HostPluginTestMountIds.Count > WorkflowContract.MaximumPluginTestMounts ||
            submission.HostPluginTestMountIds.Distinct(StringComparer.Ordinal).Count() != submission.HostPluginTestMountIds.Count)
            throw new ArgumentException($"Host plugin-test mounts must contain at most {WorkflowContract.MaximumPluginTestMounts} distinct IDs.");
        foreach (var mount in submission.HostPluginTestMountIds)
        {
            // Match the native receipt contract's character bound; Unicode mount names
            // remain valid without confusing characters with their UTF-8 byte length.
            if (string.IsNullOrWhiteSpace(mount) || mount.Length > 129 || mount.Any(char.IsControl))
                throw new ArgumentException("Host plugin-test mount IDs must be bounded and contain no control characters.");
        }
        if (submission.HostPluginTestMountIds.Count != 0 && submission.Caller?.Kind != EventActorKind.Service)
            throw new ArgumentException("Only an admitted host service may bind plugin-test mount provenance.");
        if (submission.ConversationCompactionRequestId is { } compactionRequest)
            Identifier(compactionRequest, nameof(submission.ConversationCompactionRequestId));
    }
    private static void ValidateQuestions(IReadOnlyList<WorkflowQuestion> fields)
    {
        if (fields is null || fields.Count is < 1 or > 16) throw new ArgumentException("Question set must contain 1–16 fields.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field); Identifier(field.Id, nameof(field.Id)); Text(field.Label, nameof(field.Label), 2048, required: true);
            if (!ids.Add(field.Id)) throw new ArgumentException("Question IDs must be unique.");
            if (field.Description is { } description) Text(description, "question description", 8192, required: true);
            if (field.Multiple && field.Choices is null) throw new ArgumentException("Multiple selection requires offered choices.");
            if (field.Choices is null) continue;
            if (field.Choices.Count is < 1 or > 32 || field.Choices.Distinct(StringComparer.Ordinal).Count() != field.Choices.Count)
                throw new ArgumentException("Choices must contain 1–32 unique values.");
            foreach (var choice in field.Choices) Text(choice, "choice", 1024, required: true);
        }
        if (Encoding.UTF8.GetByteCount(Serialize(fields)) > WorkflowContract.MaximumStateBytes) throw new ArgumentException("Question set is too large.");
    }
    internal static void ValidateResult(WorkflowCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed or WorkflowEffectState.Cancelled or WorkflowEffectState.Unknown))
            throw new ArgumentException("Command result must report a finished or explicitly uncertain outcome.");
        if (result.Data is { } data) BoundedJson(data, nameof(result.Data));
        if (result.ConversationCheckpoint is { } checkpoint)
        {
            if (result.State != WorkflowEffectState.Succeeded)
                throw new ArgumentException("Only a successful command result can carry a conversation checkpoint.", nameof(result));
            Cerebral.Contracts.Context.ContextValidation.ValidateWindow(new(checkpoint.ContextRevision,
                checkpoint.ExclusionRevision, [], checkpoint));
            if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > WorkflowContract.MaximumStateBytes)
                throw new ArgumentException("Command result and conversation checkpoint exceed the durable state limit.", nameof(result));
        }
        Text(result.Error, nameof(result.Error), MaximumResultErrorCharacters);
        Text(result.ProviderReceiptId, nameof(result.ProviderReceiptId), MaximumProviderReceiptCharacters);
    }
    private static void BoundedJson(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Undefined || JsonSerializer.SerializeToUtf8Bytes(value, Json).Length > WorkflowContract.MaximumStateBytes)
            throw new ArgumentException($"{name} must be defined JSON within {WorkflowContract.MaximumStateBytes} UTF-8 bytes.", name);
    }
    // A captured name is presentation data, never an authorization identity.
    // Read only the matching frozen profile; later profile renames cannot rewrite history.
    private static EventActor FrozenSubmissionAgent(WorkflowSubmission submission)
    {
        string? displayName = null;
        var snapshot = submission.SettingsSnapshot;
        if (snapshot.ValueKind == JsonValueKind.Object &&
            snapshot.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object &&
            profile.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            string.Equals(id.GetString(), submission.ProfileId, StringComparison.Ordinal) &&
            profile.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            var captured = name.GetString();
            if (!string.IsNullOrWhiteSpace(captured) && captured.Length <= 128 && !captured.Any(char.IsControl))
                displayName = captured;
        }
        return new(EventActorKind.Agent, submission.ProfileId, displayName);
    }

    private static void Identifier(string value, string name, int maximum = 128)
    {
        Text(value, name, maximum, required: true);
        if (value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)) throw new ArgumentException($"{name} must not contain whitespace or control characters.", name);
    }
    private static void Text(string? value, string name, int maximum, bool required = false)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) || value?.Length > maximum || value?.Contains('\0') == true)
            throw new ArgumentException($"{name} is missing, too large, or contains a null character.", name);
    }
    private static WorkflowConflictException Conflict(string message) => new(message);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumRunBytes) throw new InvalidDataException("Stored workflow value exceeds the supported size.");
        return JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("Stored workflow value is empty.");
    }
    private static T Freeze<T>(T value) => Deserialize<T>(Serialize(value));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    private static bool Equivalent<T>(T left, T right) => JsonNode.DeepEquals(JsonNode.Parse(Serialize(left)), JsonNode.Parse(Serialize(right)));
}
