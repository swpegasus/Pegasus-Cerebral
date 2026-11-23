using System.Security.Cryptography;
using System.Text;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    private static void ValidateCollaborationAdmission(SqliteConnection connection,
        SqliteTransaction transaction, WorkflowSubmission submission)
    {
        var ownedCoordinator = CollaborationStore.IsOwnedCoordinatorConversation(connection, transaction, submission.ConversationId);
        if (ownedCoordinator && (submission.DefinitionId != ConversationWorkflowContract.DefinitionId ||
            submission.DefinitionVersion != ConversationWorkflowContract.DefinitionVersion))
            throw new UnauthorizedAccessException("An internal coordinator retains its exact built-in workflow binding.");
        if (submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.collaboration" })
        {
            if (ownedCoordinator)
                throw new UnauthorizedAccessException("An internal coordinator context accepts only its Main's bound collaboration attempt.");
            return;
        }
        using var query = EventJournal.Command(connection, transaction,
            "SELECT payload FROM collaboration_tasks WHERE attempt_id=$attempt;");
        query.Parameters.AddWithValue("$attempt", submission.RequestId);
        var task = query.ExecuteScalar() is string json ? Deserialize<CollaborationTask>(json) : null;
        if (task is null || task.AttemptId != submission.RequestId || task.TargetRunId is not null ||
            task.Request.TargetConversationId != submission.ConversationId ||
            task.TargetProfileId != submission.ProfileId || task.TargetProjectId != submission.ProjectId ||
            submission.ParentRunId is not null || !CommunicationAllowed(connection, transaction, task))
            throw new UnauthorizedAccessException("The collaboration attempt or current pair permission changed before target admission.");
        if (task.SourceRunId is { } sourceRunId)
        {
            var source = Required(connection, transaction, sourceRunId);
            if (source.Submission.ConversationId != task.Request.SourceConversationId ||
                source.Submission.ProfileId != task.SourceProfileId || source.IsTerminal || source.CancelRequested)
                throw new UnauthorizedAccessException("The collaboration source workflow changed before target admission.");
        }
    }

    public long JournalRevision(string conversationId) => journal.GetRevision(conversationId);
    private static void ValidateParent(SqliteConnection connection, SqliteTransaction transaction, WorkflowSubmission submission)
    {
        if (submission.ParentRunId is not { } parentId) return;
        Identifier(parentId, nameof(submission.ParentRunId));
        var parent = Required(connection, transaction, parentId);
        if (parent.IsTerminal || parent.CancelRequested || parent.Submission.ParentRunId is not null ||
            parent.Submission.ConversationId == submission.ConversationId || parent.Submission.ProjectId != submission.ProjectId ||
            submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.coordination" })
            throw Conflict("Child admission requires a live root parent, separate context, matching project and host provenance.");
        using var count = EventJournal.Command(connection, transaction,
            "SELECT count(*) FROM workflow_runs WHERE json_extract(submission,'$.parentRunId')=$parent;");
        count.Parameters.AddWithValue("$parent", parentId);
        if ((long)count.ExecuteScalar()! >= 64) throw Conflict("Parent reached its bounded total child limit.");
    }

    private static void ValidateChildren(SqliteConnection connection, SqliteTransaction transaction, WorkflowRun parent,
        IReadOnlyList<string> children)
    {
        if (children is null || children.Count is < 1 or > 8 || children.Distinct(StringComparer.Ordinal).Count() != children.Count)
            throw new ArgumentException("A child barrier requires one to eight distinct run identities.");
        foreach (var id in children)
        {
            var child = Required(connection, transaction, id);
            if (child.Submission.ParentRunId != parent.Id &&
                !BoundCollaboration(connection, transaction, parent, child, out _))
                throw Conflict("A workflow can collect only its own host-admitted children.");
        }
    }

    private static bool BoundCollaboration(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun source, WorkflowRun target, out CollaborationTask? task)
    {
        using var query = EventJournal.Command(connection, transaction,
            "SELECT payload FROM collaboration_tasks WHERE target_run_id=$run;");
        query.Parameters.AddWithValue("$run", target.Id);
        task = query.ExecuteScalar() is string json ? Deserialize<CollaborationTask>(json) : null;
        return task is not null && task.SourceRunId == source.Id && task.TargetRunId == target.Id &&
            task.AttemptId == target.Id && task.Request.SourceConversationId == source.Submission.ConversationId &&
            task.SourceProfileId == source.Submission.ProfileId &&
            task.Request.TargetConversationId == target.Submission.ConversationId &&
            task.TargetProfileId == target.Submission.ProfileId &&
            target.Submission.ParentRunId is null &&
            target.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.collaboration" };
    }

    private static bool CommunicationAllowed(SqliteConnection connection, SqliteTransaction transaction,
        CollaborationTask task)
    {
        if (CollaborationStore.IsOwnedCoordinatorConversation(connection, transaction, task.Request.TargetConversationId))
            return CollaborationStore.IsOwnedCoordinatorRoute(connection, transaction, task);
        var source = task.Request.SourceConversationId;
        var target = task.Request.TargetConversationId;
        var first = StringComparer.Ordinal.Compare(source, target) < 0 ? source : target;
        var second = first == source ? target : source;
        using var query = EventJournal.Command(connection, transaction, """
            SELECT allowed FROM conversation_communication_permissions
            WHERE first_id=$first AND second_id=$second;
            """);
        query.Parameters.AddWithValue("$first", first);
        query.Parameters.AddWithValue("$second", second);
        return query.ExecuteScalar() is long allowed && allowed == 1;
    }

    /// <summary>Fresh child snapshots and parent wake commit together. No inference, polling effect or second queue.</summary>
    public IReadOnlyList<WorkflowRun> ResumeCompletedDependencies(int limit = 32)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        return journal.Transact((connection, transaction) =>
        {
            var candidates = new List<string>();
            using (var query = EventJournal.Command(connection, transaction, """
                SELECT p.id FROM workflow_runs p WHERE p.status='Waiting'
                    AND json_array_length(json_extract(p.payload,'$.waitingForRunIds')) > 0
                    AND (NOT EXISTS (SELECT 1 FROM json_each(p.payload,'$.waitingForRunIds') dependency
                        LEFT JOIN workflow_runs child ON child.id=dependency.value
                        WHERE child.id IS NULL OR child.status NOT IN ('Completed','Failed','Cancelled'))
                      OR EXISTS (SELECT 1 FROM json_each(p.payload,'$.waitingForRunIds') dependency
                        JOIN workflow_runs child ON child.id=dependency.value
                        JOIN collaboration_tasks task ON task.target_run_id=child.id
                        WHERE child.status='Waiting' AND json_extract(child.payload,'$.questions') IS NOT NULL
                            AND json_extract(task.payload,'$.sourceRunId')=p.id)
                      OR (json_extract(p.payload,'$.localChildQuestionRelay') IS NULL
                        AND json_extract(p.payload,'$.questions') IS NULL
                        AND EXISTS (SELECT 1 FROM json_each(p.payload,'$.waitingForRunIds') dependency
                            JOIN workflow_runs child ON child.id=dependency.value
                            WHERE child.status='Waiting' AND json_extract(child.payload,'$.questions') IS NOT NULL
                                AND json_extract(child.submission,'$.parentRunId')=p.id))
                      OR (json_extract(p.payload,'$.localChildQuestionRelay') IS NOT NULL
                        AND EXISTS (SELECT 1 FROM workflow_runs child
                            WHERE child.id=json_extract(p.payload,'$.localChildQuestionRelay.childRunId')
                                AND (child.revision<>json_extract(p.payload,'$.localChildQuestionRelay.childRunRevision')
                                    OR child.status<>'Waiting'))))
                    AND NOT EXISTS (SELECT 1 FROM json_each(p.payload,'$.waitingForRunIds') dependency
                        JOIN collaboration_tasks task ON task.target_run_id=dependency.value
                        LEFT JOIN conversation_communication_permissions permission
                            ON permission.first_id=CASE WHEN task.source_conversation_id < task.target_conversation_id
                                THEN task.source_conversation_id ELSE task.target_conversation_id END
                            AND permission.second_id=CASE WHEN task.source_conversation_id < task.target_conversation_id
                                THEN task.target_conversation_id ELSE task.source_conversation_id END
                        LEFT JOIN conversation_owned_coordinators owned
                            ON owned.main_conversation_id=task.source_conversation_id
                                AND owned.coordinator_conversation_id=task.target_conversation_id
                        WHERE json_extract(task.payload,'$.sourceRunId')=p.id
                            AND COALESCE(permission.allowed,0)<>1 AND owned.coordinator_conversation_id IS NULL)
                    ORDER BY p.rowid LIMIT $limit;
                """))
            {
                query.Parameters.AddWithValue("$limit", limit);
                using var reader = query.ExecuteReader();
                while (reader.Read()) candidates.Add(reader.GetString(0));
            }
            var resumed = new List<WorkflowRun>();
            foreach (var id in candidates)
            {
                var parent = Required(connection, transaction, id);
                if (parent.CancelRequested) continue;
                ValidateChildren(connection, transaction, parent, parent.WaitingForRunIds);
                var children = parent.WaitingForRunIds.Select(child => Required(connection, transaction, child)).ToArray();
                // SQL limits candidates; the exact current task/source/ownership or pair gate
                // remains authoritative before either questions or results can be delivered.
                if (children.Any(child => BoundCollaboration(connection, transaction, parent, child, out var task) &&
                    task is not null && !CommunicationAllowed(connection, transaction, task)))
                    continue;
                var superseded = false;
                if (parent.LocalChildQuestionRelay is { } prior)
                {
                    var target = children.SingleOrDefault(child => child.Id == prior.ChildRunId);
                    var currentQuestions = target?.Questions;
                    if (target is null || target.Status != WorkflowStatus.Waiting || target.CancelRequested ||
                        target.Revision != prior.ChildRunRevision ||
                        currentQuestions is null ||
                        currentQuestions.Id != prior.ChildQuestionSetId ||
                        currentQuestions.Revision != prior.ChildQuestionRevision ||
                        LocalQuestionFieldsSha(currentQuestions.Fields) != prior.FieldsSha256)
                    {
                        // The old Main form and any partial answers remain in the journal.
                        // An explicit activity record explains why a refreshed form changed.
                        Append(connection, transaction, parent, "workflow.questions.superseded",
                            new(EventActorKind.Service, "cerebral.engine"),
                            [new StructuredContentPart("workflow.questions.superseded",
                                Element(new { parentQuestionSetId = prior.ParentQuestionSetId,
                                    childRunId = prior.ChildRunId,
                                    reason = "The child question changed or finished through another owner route." }))],
                            new(ModelInclusion.Required, HumanVisibility.Activity),
                            new(EventSourceKind.Service, "cerebral.engine"));
                        parent = parent with { Questions = null, LocalChildQuestionRelay = null,
                            WaitingReason = "The child question changed; inspect the current child outcome." };
                        superseded = true;
                    }
                }
                if (children.Any(child => !child.IsTerminal))
                {
                    foreach (var child in children.Where(child => child.Status == WorkflowStatus.Waiting && child.Questions is not null))
                    {
                        if (!BoundCollaboration(connection, transaction, parent, child, out var task) || task is null ||
                            task.IsTerminal || child.Questions!.Fields.Count == 0 ||
                            !CommunicationAllowed(connection, transaction, task))
                            continue;
                        task = CollaborationStore.SynchronizeBoundRun(connection, transaction, task, child);
                        resumed.Add(Save(connection, transaction, parent with { Status = WorkflowStatus.Queued,
                            WaitingForRunIds = [], WaitingReason = null,
                            Signal = new(WorkflowSignalKind.DependencyResults, Element(new
                            {
                                clarification = new { taskId = task.Id, task.Revision, task.AttemptId,
                                    targetRunId = child.Id, targetRunRevision = child.Revision,
                                    questionSet = child.Questions }
                            })) }, "collaboration.clarification"));
                        break;
                    }
                    // Local delegated children have no collaboration task or pair grant. Their
                    // exact host-admitted parent identity is the only route to the owner's form.
                    var projectedLocal = false;
                    if (parent.LocalChildQuestionRelay is null && parent.Questions is null)
                    {
                        foreach (var child in children)
                        {
                            if (child.Status != WorkflowStatus.Waiting || child.CancelRequested ||
                                child.Questions is not { Fields.Count: > 0 } ||
                                child.Submission.ParentRunId != parent.Id ||
                                child.Submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.coordination" })
                                continue;
                            var childQuestions = child.Questions;
                            var ownerQuestions = new WorkflowQuestionSet(Guid.NewGuid().ToString("N"), 1,
                                Freeze(childQuestions.Fields), Freeze(childQuestions.Answers));
                            var binding = new LocalChildQuestionRelay(parent.Id, child.Id, child.Revision,
                                childQuestions.Id, childQuestions.Revision, ownerQuestions.Id,
                                ownerQuestions.Revision, LocalQuestionFieldsSha(childQuestions.Fields),
                                LocalQuestionAnswersSha(childQuestions.Answers),
                                Freeze(parent.WaitingForRunIds), child.Submission.Caller);
                            var projected = parent with { Questions = ownerQuestions,
                                LocalChildQuestionRelay = binding,
                                WaitingReason = "Waiting for your answer to a child agent's question." };
                            Append(connection, transaction, projected, "workflow.questions",
                                new(EventActorKind.Service, "cerebral.engine"),
                                [new StructuredContentPart("workflow.questions", Element(ownerQuestions))],
                                new(ModelInclusion.Required), new(EventSourceKind.Service, "cerebral.engine"));
                            resumed.Add(Save(connection, transaction, projected, "children.question"));
                            projectedLocal = true;
                            break;
                        }
                    }
                    if (superseded && !projectedLocal)
                        resumed.Add(Save(connection, transaction, parent, "children.question.superseded"));
                    continue;
                }
                if (children.Any(child => BoundCollaboration(connection, transaction, parent, child, out var task) &&
                    task is not null && !CommunicationAllowed(connection, transaction, task)))
                    continue;
                foreach (var child in children)
                    if (BoundCollaboration(connection, transaction, parent, child, out var task) && task is not null)
                        _ = CollaborationStore.SynchronizeBoundRun(connection, transaction, task, child);
                var results = children.Select(child =>
                {
                    var full = child.Result?.GetRawText() ?? "";
                    var text = child.Result is { } result && result.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        (result.TryGetProperty("text", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ||
                         result.TryGetProperty("summary", out value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                        ? value.GetString()! : child.Error ?? full;
                    var excerpt = text[..Math.Min(text.Length, 2048)];
                    // Do not split a surrogate pair at the bounded excerpt boundary.
                    if (excerpt.Length != 0 && char.IsHighSurrogate(excerpt[^1])) excerpt = excerpt[..^1];
                    var sealedOutcome = Element(new { child.Id, child.Revision, child.Status, child.Result, child.Error }).GetRawText();
                    return new { runId = child.Id, child.Revision, profileId = child.Submission.ProfileId, child.Status,
                        summary = excerpt, truncated = excerpt.Length < text.Length,
                        outcomeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sealedOutcome))).ToLowerInvariant() };
                }).ToArray();
                resumed.Add(Save(connection, transaction, parent with { Status = WorkflowStatus.Queued,
                    WaitingForRunIds = [], WaitingReason = null,
                    Signal = new(WorkflowSignalKind.DependencyResults, Element(new { children = results })) }, "children.collected"));
            }
            return resumed.AsReadOnly();
        });
    }

    private static string LocalQuestionFieldsSha(IReadOnlyList<WorkflowQuestion> fields) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Element(fields).GetRawText())))
            .ToLowerInvariant();

    private static string LocalQuestionAnswersSha(IReadOnlyDictionary<string, string> answers) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Element(answers
            .OrderBy(answer => answer.Key, StringComparer.Ordinal)
            .Select(answer => new { answer.Key, answer.Value }).ToArray()).GetRawText())))
            .ToLowerInvariant();

    public IReadOnlyList<string> ChildRunIds(string parentRunId) => journal.Transact((connection, transaction) =>
    {
        Identifier(parentRunId, nameof(parentRunId));
        using var query = EventJournal.Command(connection, transaction,
            "SELECT id FROM workflow_runs WHERE json_extract(submission,'$.parentRunId')=$parent ORDER BY rowid LIMIT 64;");
        query.Parameters.AddWithValue("$parent", parentRunId);
        using var reader = query.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return (IReadOnlyList<string>)ids.AsReadOnly();
    });

    /// <summary>Read the complete admitted child set, including a corruption sentinel beyond the host limit.</summary>
    public IReadOnlyList<string> ChildRunIdsForView(string parentRunId) => journal.Transact((connection, transaction) =>
    {
        Identifier(parentRunId, nameof(parentRunId));
        using var query = EventJournal.Command(connection, transaction,
            "SELECT id FROM workflow_runs WHERE json_extract(submission,'$.parentRunId')=$parent ORDER BY rowid LIMIT 65;");
        query.Parameters.AddWithValue("$parent", parentRunId);
        using var reader = query.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return (IReadOnlyList<string>)ids.AsReadOnly();
    });
}
