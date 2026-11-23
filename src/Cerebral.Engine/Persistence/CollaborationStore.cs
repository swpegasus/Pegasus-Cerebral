using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

/// <summary>
/// Durable identity and adoption boundary for one-hop collaboration. This store owns no executor;
/// target work remains in the existing workflow queue.
/// </summary>
public sealed partial class CollaborationStore(EventJournal journal)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public CollaborationTask Submit(CollaborationTaskRequest request, ConversationSummary source,
        ConversationSummary target, string? projectGrantId, string? sourceRunId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ValidateRequest(request);
        if (projectGrantId != request.ProjectGrantId)
            throw new ArgumentException("Collaboration grant identity must be part of the exact task request.");
        if (source.Id != request.SourceConversationId || target.Id != request.TargetConversationId)
            throw new ArgumentException("Resolved conversation identity does not match the task request.");
        if (source.Id == target.Id)
            throw new ArgumentException("A collaboration target must be a different conversation.");
        if (sourceRunId is not null) Identifier(sourceRunId, nameof(sourceRunId));
        var accepted = Freeze(request);
        return journal.Transact((connection, transaction) =>
        {
            if (ReadTaskByRequest(connection, transaction, accepted.RequestId) is { } prior)
            {
                if (!Equivalent(prior.Request, accepted) || prior.SourceProfileId != source.ProfileId ||
                    prior.TargetProfileId != target.ProfileId || prior.SourceProjectId != source.ProjectId ||
                    prior.TargetProjectId != target.ProjectId || prior.ProjectGrantId != projectGrantId ||
                    prior.SourceRunId != sourceRunId)
                    throw Conflict("Request ID already belongs to different collaboration work.");
                return prior;
            }

            var routeGrant = ValidateRoute(connection, transaction, accepted, source.ProjectId,
                target.ProjectId, target.ProfileId, projectGrantId);
            foreach (var dependencyId in accepted.DependencyTaskIds)
            {
                var dependency = RequiredTask(connection, transaction, dependencyId);
                if (dependency.Status != CollaborationTaskStatus.Completed)
                    throw Conflict("All collaboration dependencies must be completed before admission.");
            }
            if (accepted.ParentTaskId is { } parentId)
                _ = RequiredTask(connection, transaction, parentId);

            var now = DateTimeOffset.UtcNow;
            var task = new CollaborationTask
            {
                Id = Guid.NewGuid().ToString("N"),
                Request = accepted,
                AttemptId = Guid.NewGuid().ToString("N"),
                SourceProfileId = source.ProfileId,
                SourceRunId = sourceRunId,
                TargetProfileId = target.ProfileId,
                SourceProjectId = source.ProjectId,
                TargetProjectId = target.ProjectId,
                ProjectGrantId = projectGrantId,
                ProjectGrantRevision = routeGrant?.Revision,
                CreatedAt = now,
                UpdatedAt = now
            };
            WriteTask(connection, transaction, task, insert: true);
            AppendEvent(connection, transaction, task, "submitted",
                Element(new { task.AttemptId, sameProject = source.ProjectId == target.ProjectId, projectGrantId }));
            return Freeze(task);
        });
    }

    public CollaborationTask Get(string id)
    {
        Identifier(id, nameof(id));
        using var connection = journal.Open();
        return ReadTask(connection, null, id) ?? throw new KeyNotFoundException("Collaboration task was not found.");
    }

    public CollaborationTask? TryGet(string id)
    {
        Identifier(id, nameof(id));
        using var connection = journal.Open();
        return ReadTask(connection, null, id);
    }

    public bool IsTargetConversation(string conversationId)
    {
        Identifier(conversationId, nameof(conversationId));
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null,
            "SELECT EXISTS(SELECT 1 FROM collaboration_tasks WHERE target_conversation_id=$conversation);");
        command.Parameters.AddWithValue("$conversation", conversationId);
        return (long)command.ExecuteScalar()! != 0;
    }

    public CollaborationTask AttachAttempt(string taskId, long expectedRevision, WorkflowRun run) =>
        Mutate(taskId, expectedRevision, "attempt.attached", current =>
        {
            if (run.Id != current.AttemptId || run.Submission.ConversationId != current.Request.TargetConversationId ||
                run.Submission.ProfileId != current.TargetProfileId)
                throw Conflict("Target workflow identity does not match the collaboration attempt.");
            return Project(current, run) with { TargetRunId = run.Id };
        });

    public CollaborationTask Synchronize(string taskId, long expectedRevision, WorkflowRun run) =>
        Mutate(taskId, expectedRevision, "attempt.synchronized", current =>
        {
            if (current.TargetRunId is null || run.Id != current.TargetRunId || run.Id != current.AttemptId)
                throw Conflict("Target workflow does not belong to this collaboration attempt.");
            if (current.TargetRunRevision is { } saved && run.Revision < saved)
                throw Conflict("Stale target workflow state was rejected.");
            return Project(current, run);
        });

    internal static CollaborationTask SynchronizeBoundRun(SqliteConnection connection,
        SqliteTransaction transaction, CollaborationTask current, WorkflowRun run)
    {
        if (current.TargetRunId != run.Id || current.AttemptId != run.Id ||
            current.Request.TargetConversationId != run.Submission.ConversationId ||
            current.TargetProfileId != run.Submission.ProfileId ||
            current.TargetRunRevision is { } saved && saved > run.Revision ||
            run.Status is not (WorkflowStatus.Waiting or WorkflowStatus.Completed or
                WorkflowStatus.Failed or WorkflowStatus.Cancelled) ||
            run.Status == WorkflowStatus.Waiting && run.Questions is null)
            throw Conflict("The bound collaboration run changed before source delivery.");
        var projected = Project(current, run);
        if (Equivalent(current, projected)) return current;
        var updated = projected with { Revision = checked(current.Revision + 1), UpdatedAt = DateTimeOffset.UtcNow };
        WriteTask(connection, transaction, updated);
        AppendEvent(connection, transaction, updated,
            run.Status == WorkflowStatus.Waiting ? "question.relayed" : "attempt.synchronized",
            Element(new { current.SourceRunId, run.Id, run.Revision,
                questionSetId = run.Questions?.Id, questionRevision = run.Questions?.Revision }));
        return updated;
    }

    public CollaborationTask RequestCancel(string taskId, long expectedRevision) =>
        Mutate(taskId, expectedRevision, "cancel.requested", current =>
        {
            if (current.IsTerminal) return current;
            return current with { CancelRequested = true, Status = CollaborationTaskStatus.Cancelling };
        }, allowEquivalent: true);

    public CollaborationTask MarkReconcileRequired(string taskId, long expectedRevision, string reason) =>
        Mutate(taskId, expectedRevision, "reconcile.required", current =>
            current with { Status = CollaborationTaskStatus.ReconcileRequired, WaitingReason = reason });

    public CollaborationTask? TryAnswerRetry(CollaborationAnswerRequest request) =>
        TrySignalRetry(request.TaskId, request.RequestId, "answer.accepted", request);

    public CollaborationTask RecordAnswer(CollaborationAnswerRequest request) =>
        RecordSignal(request.TaskId, request.ExpectedTaskRevision, request.RequestId, "answer.accepted", request);

    public CollaborationTask? TrySteeringRetry(CollaborationSteerRequest request) =>
        TrySignalRetry(request.TaskId, request.RequestId, "steer.accepted", request);

    public CollaborationTask RecordSteering(CollaborationSteerRequest request) =>
        RecordSignal(request.TaskId, request.ExpectedTaskRevision, request.RequestId, "steer.accepted", request);

    public CollaborationTask Adopt(CollaborationAdoptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId));
        Identifier(request.TaskId, nameof(request.TaskId));
        Identifier(request.AttemptId, nameof(request.AttemptId));
        if (request.ExpectedTaskRevision < 1 || request.ExpectedTargetRunRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(request));

        return journal.Transact((connection, transaction) =>
        {
            var current = RequiredTask(connection, transaction, request.TaskId);
            if (current.AdoptionRequestId is not null)
            {
                if (current.AdoptionRequestId == request.RequestId && current.AttemptId == request.AttemptId &&
                    current.TargetRunRevision == request.ExpectedTargetRunRevision)
                    return current;
                throw Conflict("The collaboration result was already adopted by another request.");
            }
            Expected(current, request.ExpectedTaskRevision);
            if (current.AttemptId != request.AttemptId || current.TargetRunRevision != request.ExpectedTargetRunRevision)
                throw Conflict("Stale collaboration result identity or revision was rejected.");
            if (current.Status != CollaborationTaskStatus.Completed || current.Result is null)
                throw Conflict("Only a completed target result can be adopted.");
            if (current.ProjectGrantId is { } grantId)
            {
                var grant = ReadGrant(connection, transaction, grantId) ??
                    throw new UnauthorizedAccessException("Collaboration project grant is unavailable.");
                if (grant.RevokedAt is not null || grant.Revision != current.ProjectGrantRevision)
                    throw new UnauthorizedAccessException("Collaboration project grant changed before result adoption.");
            }

            var profile = CollaborationAdoptionProfile.Read(connection, transaction, current);
            var data = new JsonObject
            {
                ["taskId"] = current.Id,
                ["attemptId"] = current.AttemptId,
                ["targetConversationId"] = current.Request.TargetConversationId,
                ["targetRunRevision"] = current.TargetRunRevision
            };
            if (profile is not null)
            {
                data["targetProfileId"] = profile.Id;
                if (profile.Name is not null) data["targetProfileName"] = profile.Name;
                if (profile.Role is not null) data["targetProfileRole"] = profile.Role;
            }
            var eventId = "collab-" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(current.Id + "\n" + request.RequestId))).ToLowerInvariant()[..48];
            var now = DateTimeOffset.UtcNow;
            var item = new CerebralEvent(eventId, current.Request.SourceConversationId, current.Id, 0,
                "collaboration.result", new(EventActorKind.Service, "cerebral.collaboration"),
                [new TextContentPart(ResultText(current.Result.Value))],
                new(ModelInclusion.Evidence, HumanVisibility.Transcript, RetentionPolicy.Durable),
                new(EventSourceKind.Service, "cerebral.collaboration", AgentId: profile?.Id, JobId: current.TargetRunId),
                now, Element(data), CorrelationId: current.Id);
            var appended = EventJournal.AppendInTransaction(connection, transaction, item,
                EventJournal.ReadRevision(connection, transaction, item.ConversationId)).Event;
            var adopted = current with
            {
                Revision = checked(current.Revision + 1),
                AdoptionRequestId = request.RequestId,
                AdoptedAt = now,
                AdoptedSourceRevision = appended.Sequence,
                UpdatedAt = now
            };
            WriteTask(connection, transaction, adopted);
            AppendEvent(connection, transaction, adopted, "result.adopted",
                Element(new { request.RequestId, sourceRevision = appended.Sequence }));
            return Freeze(adopted);
        });
    }

    public CollaborationTaskEventPage ReadEvents(string taskId, long afterSequence = 0, int limit = 40,
        long? snapshotSequence = null)
    {
        Identifier(taskId, nameof(taskId));
        if (afterSequence < 0 || limit is < 1 or > CollaborationContract.MaximumEventPage ||
            snapshotSequence is < 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = journal.Open();
        _ = ReadTask(connection, null, taskId) ?? throw new KeyNotFoundException("Collaboration task was not found.");
        using var snapshot = EventJournal.Command(connection, null,
            "SELECT COALESCE(MAX(sequence),0) FROM collaboration_events WHERE task_id=$task;");
        snapshot.Parameters.AddWithValue("$task", taskId);
        var current = (long)snapshot.ExecuteScalar()!;
        var bound = snapshotSequence ?? current;
        if (bound > current) throw new ArgumentException("The requested collaboration event snapshot is unavailable.");
        using var command = EventJournal.Command(connection, null, """
            SELECT payload FROM collaboration_events
            WHERE task_id=$task AND sequence>$after AND sequence<=$snapshot
            ORDER BY sequence LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$snapshot", bound);
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = command.ExecuteReader();
        var events = new List<CollaborationTaskEvent>(limit);
        long? next = null;
        while (reader.Read())
        {
            var item = Deserialize<CollaborationTaskEvent>(reader.GetString(0));
            if (events.Count == limit) { next = events[^1].Sequence; break; }
            events.Add(item);
        }
        return new(events.AsReadOnly(), bound, next);
    }

    public CollaborationTaskListPage List(int limit = 40, long? beforeRow = null, long? snapshotRow = null) =>
        ListCore(limit, beforeRow, snapshotRow, null);

    public CollaborationTaskListPage ListForSource(string sourceConversationId, int limit = 40,
        long? beforeRow = null, long? snapshotRow = null)
    {
        Identifier(sourceConversationId, nameof(sourceConversationId));
        return ListCore(limit, beforeRow, snapshotRow, sourceConversationId);
    }

    private CollaborationTaskListPage ListCore(int limit, long? beforeRow, long? snapshotRow,
        string? sourceConversationId)
    {
        if (limit is < 1 or > 200 || beforeRow is < 1 || snapshotRow is < 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = journal.Open();
        using var snapshot = EventJournal.Command(connection, null, "SELECT COALESCE(MAX(rowid),0) FROM collaboration_tasks;");
        var current = (long)snapshot.ExecuteScalar()!;
        var bound = snapshotRow ?? current;
        if (bound > current) throw new ArgumentException("The requested collaboration snapshot is unavailable.");
        using var command = EventJournal.Command(connection, null, """
            SELECT rowid,payload FROM collaboration_tasks
            WHERE rowid<=$snapshot AND ($before IS NULL OR rowid<$before)
                AND ($source IS NULL OR source_conversation_id=$source)
            ORDER BY rowid DESC LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$snapshot", bound);
        command.Parameters.AddWithValue("$before", beforeRow is { } before ? before : DBNull.Value);
        command.Parameters.AddWithValue("$source", sourceConversationId is { } source ? source : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = command.ExecuteReader();
        var tasks = new List<CollaborationTask>(limit);
        long? next = null;
        long last = 0;
        while (reader.Read())
        {
            if (tasks.Count == limit) { next = last; break; }
            last = reader.GetInt64(0);
            tasks.Add(Deserialize<CollaborationTask>(reader.GetString(1)));
        }
        return new(tasks.AsReadOnly(), bound, next);
    }

    public CollaborationResultPage ReadResult(string taskId, long expectedTargetRunRevision, int offset = 0,
        int maximumBytes = 16 * 1024)
    {
        var task = Get(taskId);
        if (task.TargetRunRevision != expectedTargetRunRevision || task.Result is null)
            throw Conflict("The requested target result revision is stale or unavailable.");
        if (offset < 0 || maximumBytes is < 256 or > 64 * 1024)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var text = task.Result.Value.GetRawText();
        var bytes = Encoding.UTF8.GetBytes(text);
        if (offset > bytes.Length || offset < bytes.Length && (bytes[offset] & 0xC0) == 0x80)
            throw new ArgumentException("Result offset is not a UTF-8 boundary.", nameof(offset));
        var end = Math.Min(bytes.Length, checked(offset + maximumBytes));
        while (end < bytes.Length && end > offset && (bytes[end] & 0xC0) == 0x80) end--;
        if (end == offset && end < bytes.Length)
        {
            end = Math.Min(bytes.Length, offset + 4);
            while (end < bytes.Length && (bytes[end] & 0xC0) == 0x80) end++;
        }
        var content = Encoding.UTF8.GetString(bytes, offset, end - offset);
        return new(task.Id, task.AttemptId, expectedTargetRunRevision, offset, content,
            end < bytes.Length ? end : null, end == bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public CollaborationProjectGrant SaveGrant(CollaborationProjectGrantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGrantRequest(request);
        var accepted = Freeze(request);
        return journal.Transact((connection, transaction) =>
        {
            if (ReadGrant(connection, transaction, accepted.Id) is { } prior)
            {
                if (prior.SourceProjectId != accepted.SourceProjectId ||
                    prior.TargetProjectId != accepted.TargetProjectId ||
                    !prior.TargetProfileIds.SequenceEqual(accepted.TargetProfileIds, StringComparer.Ordinal) ||
                    !prior.Capabilities.SequenceEqual(accepted.Capabilities, StringComparer.Ordinal))
                    throw Conflict("Grant ID already belongs to a different project route.");
                return prior;
            }
            var grant = new CollaborationProjectGrant(accepted.Id, accepted.SourceProjectId,
                accepted.TargetProjectId, accepted.TargetProfileIds, accepted.Capabilities, 1, DateTimeOffset.UtcNow);
            WriteGrant(connection, transaction, grant, insert: true);
            return Freeze(grant);
        });
    }

    public CollaborationProjectGrant RevokeGrant(string id, long expectedRevision)
    {
        Identifier(id, nameof(id));
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        return journal.Transact((connection, transaction) =>
        {
            var current = ReadGrant(connection, transaction, id) ??
                throw new KeyNotFoundException("Collaboration project grant was not found.");
            if (current.RevokedAt is not null) return current;
            if (current.Revision != expectedRevision) throw Conflict("Collaboration project grant changed.");
            var updated = current with { Revision = current.Revision + 1, RevokedAt = DateTimeOffset.UtcNow };
            WriteGrant(connection, transaction, updated);
            return Freeze(updated);
        });
    }

    public CollaborationProjectGrant GetGrant(string id)
    {
        Identifier(id, nameof(id));
        using var connection = journal.Open();
        return ReadGrant(connection, null, id) ??
            throw new KeyNotFoundException("Collaboration project grant was not found.");
    }

    public IReadOnlyList<CollaborationProjectGrant> ListGrants()
    {
        using var connection = journal.Open();
        using var command = EventJournal.Command(connection, null,
            "SELECT payload FROM collaboration_project_grants ORDER BY id COLLATE BINARY;");
        using var reader = command.ExecuteReader();
        var result = new List<CollaborationProjectGrant>();
        while (reader.Read()) result.Add(Deserialize<CollaborationProjectGrant>(reader.GetString(0)));
        return result.AsReadOnly();
    }

    private CollaborationTask Mutate(string taskId, long expectedRevision, string eventType,
        Func<CollaborationTask, CollaborationTask> transform, bool allowEquivalent = false, JsonElement? data = null)
    {
        Identifier(taskId, nameof(taskId));
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        return journal.Transact((connection, transaction) =>
        {
            var current = RequiredTask(connection, transaction, taskId);
            Expected(current, expectedRevision);
            var candidate = Freeze(transform(current));
            if (Equivalent(current, candidate))
            {
                if (!allowEquivalent) return current;
                candidate = current;
            }
            var updated = candidate with
            {
                Revision = checked(current.Revision + 1),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            ValidateTask(updated);
            WriteTask(connection, transaction, updated);
            AppendEvent(connection, transaction, updated, eventType, data ?? Element(new { }));
            return Freeze(updated);
        });
    }

    private CollaborationTask? TrySignalRetry<T>(string taskId, string requestId, string type, T request)
    {
        Identifier(taskId, nameof(taskId));
        Identifier(requestId, nameof(requestId));
        using var connection = journal.Open();
        var current = ReadTask(connection, null, taskId) ??
            throw new KeyNotFoundException("Collaboration task was not found.");
        if (ReadSignal(connection, null, taskId, type, requestId) is not { } signal) return null;
        if (SignalHash(signal) != Fingerprint(request))
            throw Conflict("Collaboration signal request ID already belongs to different input.");
        return current;
    }

    private CollaborationTask RecordSignal<T>(string taskId, long expectedRevision, string requestId,
        string type, T request)
    {
        Identifier(taskId, nameof(taskId));
        Identifier(requestId, nameof(requestId));
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var fingerprint = Fingerprint(request);
        return journal.Transact((connection, transaction) =>
        {
            var current = RequiredTask(connection, transaction, taskId);
            if (ReadSignal(connection, transaction, taskId, type, requestId) is { } signal)
            {
                if (SignalHash(signal) != fingerprint)
                    throw Conflict("Collaboration signal request ID already belongs to different input.");
                return current;
            }
            Expected(current, expectedRevision);
            var updated = current with
            {
                Revision = checked(current.Revision + 1),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            WriteTask(connection, transaction, updated);
            AppendEvent(connection, transaction, updated, type,
                Element(new { requestId, requestSha256 = fingerprint }));
            return Freeze(updated);
        });
    }

    private static CollaborationTask Project(CollaborationTask current, WorkflowRun run)
    {
        if (run.Result is { } result &&
            Encoding.UTF8.GetByteCount(result.GetRawText()) > CollaborationContract.MaximumResultUtf8Bytes)
            throw new InvalidDataException("Target result exceeds the collaboration result bound.");
        return current with
        {
            TargetRunRevision = run.Revision,
            Status = run.Status switch
            {
                WorkflowStatus.Queued => CollaborationTaskStatus.Queued,
                WorkflowStatus.Running => CollaborationTaskStatus.Running,
                WorkflowStatus.Waiting => CollaborationTaskStatus.Waiting,
                WorkflowStatus.Cancelling => CollaborationTaskStatus.Cancelling,
                WorkflowStatus.Completed => CollaborationTaskStatus.Completed,
                WorkflowStatus.Failed => CollaborationTaskStatus.Failed,
                WorkflowStatus.Cancelled => CollaborationTaskStatus.Cancelled,
                _ => throw new InvalidDataException("Unsupported workflow status.")
            },
            Questions = run.Questions is null ? null : Freeze(run.Questions),
            Result = run.Result?.Clone(),
            WaitingReason = run.WaitingReason,
            Error = run.Error,
            CancelRequested = current.CancelRequested || run.CancelRequested
        };
    }

    private static CollaborationProjectGrant? ValidateRoute(SqliteConnection connection,
        SqliteTransaction transaction, CollaborationTaskRequest request, string? sourceProjectId,
        string? targetProjectId, string targetProfileId, string? projectGrantId)
    {
        if (sourceProjectId == targetProjectId)
        {
            if (projectGrantId is not null)
                throw new ArgumentException("Same-project collaboration does not accept a cross-project grant.");
            foreach (var reference in request.InputReferences)
                if (reference.ProjectId != sourceProjectId)
                    throw new UnauthorizedAccessException("A same-project task may reference only its project.");
            return null;
        }
        if (sourceProjectId is null || targetProjectId is null || projectGrantId is null)
            throw new UnauthorizedAccessException("Cross-project collaboration requires two bound projects and an explicit grant.");
        var grant = ReadGrant(connection, transaction, projectGrantId) ??
            throw new UnauthorizedAccessException("Collaboration project grant was not found.");
        if (grant.RevokedAt is not null || grant.SourceProjectId != sourceProjectId ||
            grant.TargetProjectId != targetProjectId ||
            !grant.Capabilities.Contains(CollaborationCapabilities.ExecuteTask, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Collaboration project grant does not admit this route.");
        if (!grant.TargetProfileIds.Contains(targetProfileId, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Collaboration project grant does not admit the target profile.");
        foreach (var reference in request.InputReferences)
        {
            if (reference.ProjectId is var project && project != sourceProjectId && project != targetProjectId)
                throw new UnauthorizedAccessException("Referenced resource belongs to neither admitted project.");
            var capability = reference.Access == CollaborationResourceAccess.Write
                ? CollaborationCapabilities.WriteResource : CollaborationCapabilities.ReadResource;
            if (!grant.Capabilities.Contains(capability, StringComparer.Ordinal))
                throw new UnauthorizedAccessException($"Collaboration grant lacks '{capability}'.");
            if (reference.Access == CollaborationResourceAccess.Write && reference.ProjectId != targetProjectId)
                throw new UnauthorizedAccessException("Cross-project writes must target a typed target-project resource.");
            if (reference.Access == CollaborationResourceAccess.Write)
                throw new NotSupportedException("The first local collaboration checkpoint does not dispatch cross-project write effects.");
        }
        return grant;
    }

    private static void ValidateRequest(CollaborationTaskRequest request)
    {
        Identifier(request.RequestId, nameof(request.RequestId));
        Identifier(request.SourceConversationId, nameof(request.SourceConversationId));
        Identifier(request.TargetConversationId, nameof(request.TargetConversationId));
        Text(request.Instruction, nameof(request.Instruction), CollaborationContract.MaximumInstructionUtf8Bytes);
        if (request.StepLimit is < 1 or > WorkflowContract.MaximumSteps)
            throw new ArgumentOutOfRangeException(nameof(request.StepLimit));
        if (request.ResponseSharePercent is < 5 or > 50)
            throw new ArgumentOutOfRangeException(nameof(request.ResponseSharePercent));
        if (request.InputReferences is null || request.InputReferences.Count > CollaborationContract.MaximumReferences)
            throw new ArgumentException("Collaboration input references exceed the bound.", nameof(request));
        if (request.DependencyTaskIds is null || request.DependencyTaskIds.Count > CollaborationContract.MaximumDependencies)
            throw new ArgumentException("Collaboration dependencies exceed the bound.", nameof(request));
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in request.DependencyTaskIds)
        {
            Identifier(id, "dependencyTaskId");
            if (id == request.RequestId || !dependencies.Add(id))
                throw new ArgumentException("Collaboration dependencies must be distinct and cannot reference the task itself.");
        }
        if (request.ParentTaskId is { } parent)
        {
            Identifier(parent, nameof(request.ParentTaskId));
            if (parent == request.RequestId) throw new ArgumentException("A collaboration task cannot parent itself.");
        }
        if (request.ProjectGrantId is { } grantId) Identifier(grantId, nameof(request.ProjectGrantId));
        foreach (var reference in request.InputReferences)
        {
            if (reference is null) throw new ArgumentException("Collaboration resource references cannot be null.");
            Identifier(reference.ProjectId, "resource.projectId");
            Dotted(reference.Kind, "resource.kind");
            Identifier(reference.Id, "resource.id");
            if (reference.Revision < 1) throw new ArgumentOutOfRangeException("resource.revision");
            if (LooksLikePath(reference.Id))
                throw new ArgumentException("Collaboration resources use opaque typed IDs, not filesystem paths.");
        }
    }

    private static void ValidateTask(CollaborationTask task)
    {
        ValidateRequest(task.Request);
        Identifier(task.Id, nameof(task.Id));
        Identifier(task.AttemptId, nameof(task.AttemptId));
        Identifier(task.SourceProfileId, nameof(task.SourceProfileId));
        if (task.SourceRunId is not null) Identifier(task.SourceRunId, nameof(task.SourceRunId));
        Identifier(task.TargetProfileId, nameof(task.TargetProfileId));
        if (task.Revision < 1 || task.AttemptNumber < 1)
            throw new InvalidDataException("Collaboration task identity or revision is invalid.");
        if (task.Result is { } result &&
            Encoding.UTF8.GetByteCount(result.GetRawText()) > CollaborationContract.MaximumResultUtf8Bytes)
            throw new InvalidDataException("Collaboration result exceeds its bound.");
    }

    private static void ValidateGrantRequest(CollaborationProjectGrantRequest request)
    {
        Identifier(request.Id, nameof(request.Id));
        Identifier(request.SourceProjectId, nameof(request.SourceProjectId));
        Identifier(request.TargetProjectId, nameof(request.TargetProjectId));
        if (request.SourceProjectId == request.TargetProjectId)
            throw new ArgumentException("A cross-project grant requires different projects.");
        if (request.TargetProfileIds is null || request.TargetProfileIds.Count is < 1 or > 32 ||
            request.TargetProfileIds.Distinct(StringComparer.Ordinal).Count() != request.TargetProfileIds.Count)
            throw new ArgumentException("Grant target profiles must contain one to thirty-two distinct IDs.");
        foreach (var profileId in request.TargetProfileIds) Identifier(profileId, "targetProfileId");
        if (request.Capabilities is null || request.Capabilities.Count is < 1 or > 16 ||
            request.Capabilities.Distinct(StringComparer.Ordinal).Count() != request.Capabilities.Count)
            throw new ArgumentException("Grant capabilities must be one to sixteen distinct values.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            CollaborationCapabilities.ExecuteTask,
            CollaborationCapabilities.ReadResource,
            CollaborationCapabilities.WriteResource
        };
        foreach (var capability in request.Capabilities)
            if (!allowed.Contains(capability)) throw new ArgumentException($"Unknown collaboration capability '{capability}'.");
        if (!request.Capabilities.Contains(CollaborationCapabilities.ExecuteTask, StringComparer.Ordinal))
            throw new ArgumentException("A collaboration grant must include task.execute.");
    }

    private static CollaborationTask RequiredTask(SqliteConnection connection, SqliteTransaction transaction, string id) =>
        ReadTask(connection, transaction, id) ?? throw new KeyNotFoundException("Collaboration task was not found.");

    private static CollaborationTask? ReadTask(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = EventJournal.Command(connection, transaction,
            "SELECT payload FROM collaboration_tasks WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? Deserialize<CollaborationTask>(json) : null;
    }

    private static CollaborationTask? ReadTaskByRequest(SqliteConnection connection,
        SqliteTransaction? transaction, string requestId)
    {
        using var command = EventJournal.Command(connection, transaction,
            "SELECT payload FROM collaboration_tasks WHERE request_id=$request;");
        command.Parameters.AddWithValue("$request", requestId);
        return command.ExecuteScalar() is string json ? Deserialize<CollaborationTask>(json) : null;
    }

    private static void WriteTask(SqliteConnection connection, SqliteTransaction transaction,
        CollaborationTask task, bool insert = false)
    {
        ValidateTask(task);
        using var command = EventJournal.Command(connection, transaction, insert ? """
            INSERT INTO collaboration_tasks(
                id,request_id,attempt_id,source_conversation_id,target_conversation_id,source_project_id,target_project_id,
                target_run_id,status,revision,payload)
            VALUES($id,$request,$attempt,$source,$target,$sourceProject,$targetProject,$run,$status,$revision,$payload);
            """ : """
            UPDATE collaboration_tasks SET target_run_id=$run,status=$status,revision=$revision,payload=$payload
            WHERE id=$id AND revision=$expected;
            """);
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$run", task.TargetRunId is null ? DBNull.Value : task.TargetRunId);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$revision", task.Revision);
        command.Parameters.AddWithValue("$payload", Serialize(task));
        if (insert)
        {
            command.Parameters.AddWithValue("$attempt", task.AttemptId);
            command.Parameters.AddWithValue("$request", task.Request.RequestId);
            command.Parameters.AddWithValue("$source", task.Request.SourceConversationId);
            command.Parameters.AddWithValue("$target", task.Request.TargetConversationId);
            command.Parameters.AddWithValue("$sourceProject", task.SourceProjectId is null ? DBNull.Value : task.SourceProjectId);
            command.Parameters.AddWithValue("$targetProject", task.TargetProjectId is null ? DBNull.Value : task.TargetProjectId);
        }
        else command.Parameters.AddWithValue("$expected", task.Revision - 1);
        if (command.ExecuteNonQuery() != 1)
            throw Conflict(insert ? "Collaboration task already exists." : "Collaboration task changed.");
    }

    private static void AppendEvent(SqliteConnection connection, SqliteTransaction transaction,
        CollaborationTask task, string type, JsonElement data)
    {
        Dotted(type, nameof(type));
        using var sequence = EventJournal.Command(connection, transaction,
            "SELECT COALESCE(MAX(sequence),0)+1 FROM collaboration_events WHERE task_id=$task;");
        sequence.Parameters.AddWithValue("$task", task.Id);
        var next = (long)sequence.ExecuteScalar()!;
        var item = new CollaborationTaskEvent(task.Id, next, type, task.Status, DateTimeOffset.UtcNow, data.Clone());
        using var insert = EventJournal.Command(connection, transaction,
            "INSERT INTO collaboration_events(task_id,sequence,payload) VALUES($task,$sequence,$payload);");
        insert.Parameters.AddWithValue("$task", task.Id);
        insert.Parameters.AddWithValue("$sequence", next);
        insert.Parameters.AddWithValue("$payload", Serialize(item));
        insert.ExecuteNonQuery();
    }

    private static CollaborationTaskEvent? ReadSignal(SqliteConnection connection,
        SqliteTransaction? transaction, string taskId, string type, string requestId)
    {
        using var command = EventJournal.Command(connection, transaction, """
            SELECT payload FROM collaboration_events
            WHERE task_id=$task
              AND json_extract(payload,'$.type')=$type
              AND json_extract(payload,'$.data.requestId')=$request
            ORDER BY sequence LIMIT 1;
            """);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$request", requestId);
        return command.ExecuteScalar() is string json ? Deserialize<CollaborationTaskEvent>(json) : null;
    }

    private static string SignalHash(CollaborationTaskEvent signal) =>
        signal.Data.TryGetProperty("requestSha256", out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: 64 } hash ? hash :
            throw new InvalidDataException("Collaboration signal receipt is incomplete.");

    private static string Fingerprint<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();

    private static CollaborationProjectGrant? ReadGrant(SqliteConnection connection,
        SqliteTransaction? transaction, string id)
    {
        using var command = EventJournal.Command(connection, transaction,
            "SELECT payload FROM collaboration_project_grants WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? Deserialize<CollaborationProjectGrant>(json) : null;
    }

    private static void WriteGrant(SqliteConnection connection, SqliteTransaction transaction,
        CollaborationProjectGrant grant, bool insert = false)
    {
        using var command = EventJournal.Command(connection, transaction, insert ? """
            INSERT INTO collaboration_project_grants(id,source_project_id,target_project_id,revision,payload)
            VALUES($id,$source,$target,$revision,$payload);
            """ : """
            UPDATE collaboration_project_grants SET revision=$revision,payload=$payload
            WHERE id=$id AND revision=$expected;
            """);
        command.Parameters.AddWithValue("$id", grant.Id);
        command.Parameters.AddWithValue("$revision", grant.Revision);
        command.Parameters.AddWithValue("$payload", Serialize(grant));
        if (insert)
        {
            command.Parameters.AddWithValue("$source", grant.SourceProjectId);
            command.Parameters.AddWithValue("$target", grant.TargetProjectId);
        }
        else command.Parameters.AddWithValue("$expected", grant.Revision - 1);
        if (command.ExecuteNonQuery() != 1)
            throw Conflict(insert ? "Collaboration project grant already exists." : "Collaboration project grant changed.");
    }

    private static void Expected(CollaborationTask task, long expectedRevision)
    {
        if (task.Revision != expectedRevision)
            throw Conflict($"Collaboration task changed: expected revision {expectedRevision}, actual {task.Revision}.");
    }

    private static string ResultText(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.String) return Bounded(result.GetString() ?? "");
        if (result.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "text", "message", "summary" })
                if (result.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                    return Bounded(property.GetString() ?? "");
        }
        return Bounded(result.GetRawText());
    }

    private static string Bounded(string value)
    {
        const int maximum = 32 * 1024;
        return value.Length <= maximum ? value : value[..maximum] + "\n[Result text truncated; inspect the bounded result pages.]";
    }

    private static bool LooksLikePath(string value) =>
        Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal) ||
        value.StartsWith("/", StringComparison.Ordinal);

    private static void Identifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Contains('\0') ||
            value.Any(char.IsControl))
            throw new ArgumentException($"Invalid {name}.");
    }

    private static void Dotted(string value, string name)
    {
        Identifier(value, name);
        if (value.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ':')))
            throw new ArgumentException($"Invalid {name}.");
    }

    private static void Text(string value, string name, int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') ||
            Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
            throw new ArgumentException($"{name} is empty or exceeds its bound.");
    }

    private static WorkflowConflictException Conflict(string message) => new(message);
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException($"Stored {typeof(T).Name} is empty.");
    private static T Freeze<T>(T value) => Deserialize<T>(Serialize(value));
    private static bool Equivalent<T>(T left, T right) =>
        JsonNode.DeepEquals(JsonNode.Parse(Serialize(left)), JsonNode.Parse(Serialize(right)));
}
