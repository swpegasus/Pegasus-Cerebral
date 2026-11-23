using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class CollaborationTaskTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }

        var root = Path.Combine(Path.GetTempPath(), "cerebral-collaboration-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "engine.db");
            var journal = new EventJournal(database);
            var store = new CollaborationStore(journal);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                connection.Open();
                using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                Check((long)version.ExecuteScalar()! == 6 && EventJournal.SchemaVersion >= 6,
                    "event journal initializes the collaboration schema before checkpoint source-map adoption");
            }
            CheckSchemaFiveUpgrade(root, Check);

            var gateway = new FakeGateway(
                new ConversationSummary("source-a", "Source", "lead", "project-a", "source-turn", WorkflowStatus.Completed, DateTimeOffset.UtcNow),
                new ConversationSummary("target-a", "Target A", "helper-a", "project-a", "target-a-turn", WorkflowStatus.Completed, DateTimeOffset.UtcNow),
                new ConversationSummary("target-b", "Target B", "helper-b", "project-b", "target-b-turn", WorkflowStatus.Completed, DateTimeOffset.UtcNow),
                new ConversationSummary("target-c", "Target C", "helper-c", "project-a", "target-c-turn", WorkflowStatus.Completed, DateTimeOffset.UtcNow));
            var service = new CollaborationService(store, gateway,
                new AllowAllCollaborationCommunicationGate(), new EventHub());
            var sourceActor = new AgentProfile
            {
                Id = "lead", Name = "Lead", Role = "lead", ModelId = "fixture",
                Capabilities = ["agents.collaborate"]
            };
            var unrelated = new AgentProfile
            {
                Id = "other", Name = "Other", Role = "helper", ModelId = "fixture",
                Capabilities = ["administration", "agents.collaborate"]
            };
            var targetActor = new AgentProfile
            {
                Id = "helper-a", Name = "Helper", Role = "helper", ModelId = "fixture"
            };
            var noCapability = new AgentProfile
            {
                Id = "lead", Name = "Lead", Role = "lead", ModelId = "fixture"
            };

            var request = new CollaborationTaskRequest("task-same", "source-a", "target-a",
                "Inspect the target project and return a compact finding.", [], [], StepLimit: 20,
                ResponseSharePercent: 30);
            service.Authorize("collaboration.submit", sourceActor, CerebralJson.Element(request));
            Reject(() => service.Authorize("collaboration.submit", noCapability, CerebralJson.Element(request)),
                "agents.collaborate is required for agent submission", Check);

            var submitted = await service.SubmitAsync(request, sourceActor, CancellationToken.None);
            Check(submitted.Id != request.RequestId && submitted.Request.RequestId == request.RequestId &&
                submitted.TargetRunId == submitted.AttemptId &&
                submitted.SourceProfileId == "lead" && submitted.TargetProfileId == "helper-a" &&
                submitted.SourceProjectId == "project-a" && submitted.TargetProjectId == "project-a" &&
                submitted.Revision == 2 && submitted.Status == CollaborationTaskStatus.Queued,
                "same-project submit resolves identities and attaches one existing-queue attempt");
            Check(gateway.SubmitCount == 1 && gateway.Runs.Count == 1,
                "collaboration creates one target workflow and no second execution queue");
            var boundRequest = request with { RequestId = "bound-task", TargetConversationId = "target-c" };
            var sourceRun = new WorkflowRun
            {
                Id = "source-workflow",
                Submission = new WorkflowSubmission("source-workflow", "source-a", "lead",
                    ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion,
                    "Delegate the requested inspection.", null, WorkflowContract.EmptyObject,
                    WorkflowContract.EmptyObject, Caller: new EventActor(EventActorKind.User, "owner")),
                Revision = 1, Status = WorkflowStatus.Running,
                Effect = new WorkflowEffect("source-effect", WorkerWorkflowEffects.ToolCommand, 1,
                    CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments(
                        CollaborationToolExtension.SubmitTool, CerebralJson.Element(boundRequest))),
                    WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            var boundGateway = new FakeGateway(
                new ConversationSummary("source-a", "Source", "lead", "project-a", "source-turn",
                    WorkflowStatus.Completed, DateTimeOffset.UtcNow),
                new ConversationSummary("target-c", "Target C", "helper-c", "project-a", "target-turn",
                    WorkflowStatus.Completed, DateTimeOffset.UtcNow));
            boundGateway.Runs.Add(sourceRun.Id, sourceRun);
            var boundService = new CollaborationService(
                new CollaborationStore(new EventJournal(Path.Combine(root, "bound-engine.db"))),
                boundGateway, new AllowAllCollaborationCommunicationGate(), new EventHub());
            var sourceContext = new ToolExecutionContext(sourceActor, "source-a", sourceRun.Id,
                EffectId: "source-effect") { EnforceProfileScope = true };
            await RejectAsync(() => boundService.SubmitFromWorkflowAsync(boundRequest,
                    sourceContext with { EffectId = "wrong-effect" }, CancellationToken.None),
                "foreign source effect cannot bind a collaboration task", Check);
            var boundTask = await boundService.SubmitFromWorkflowAsync(boundRequest, sourceContext,
                CancellationToken.None);
            Check(boundTask.SourceRunId == sourceRun.Id && boundTask.TargetRunId == boundTask.AttemptId &&
                boundGateway.SubmitCount == 1, "trusted source effect binds one target attempt to the Main run");
            var boundGraph = (CollaborationViewGraph)await boundService.ExecuteAsync("collaboration.view.graph",
                CerebralJson.Element(new { taskId = boundTask.Id, childOffset = 0, limit = 8 }),
                null, CancellationToken.None);
            Check(boundGraph.Complete && boundGraph.SourceRunId == sourceRun.Id,
                "owner graph exposes only the proven source run for a bound handoff");
            await RejectAsync(() => boundService.SubmitAsync(boundRequest, sourceActor, CancellationToken.None),
                "direct agent retry cannot claim a workflow-bound task", Check);
            var foreignChild = gateway.Runs[submitted.AttemptId] with
            {
                Id = "foreign-child",
                Submission = gateway.Runs[submitted.AttemptId].Submission with
                {
                    RequestId = "foreign-child", ConversationId = "foreign-context",
                    ParentRunId = "another-parent",
                    Caller = new EventActor(EventActorKind.Service, "cerebral.coordination")
                }
            };
            gateway.Runs.Add(foreignChild.Id, foreignChild);
            gateway.ChildIds = [foreignChild.Id];
            var foreignView = (CollaborationViewGraph)await service.ExecuteAsync("collaboration.view.graph",
                CerebralJson.Element(new { taskId = submitted.Id, childOffset = 0, limit = 8 }),
                null, CancellationToken.None);
            Check(!foreignView.Complete && foreignView.Workers.Count == 0 && foreignView.Coordinator is null &&
                foreignView.IncompleteReason == "child-lineage-changed",
                "A foreign child edge cannot appear as a worker or completed team view.");
            gateway.ChildIds = []; gateway.Runs.Remove(foreignChild.Id);

            var retry = await service.SubmitAsync(request, sourceActor, CancellationToken.None);
            Check(retry.Id == submitted.Id && retry.AttemptId == submitted.AttemptId &&
                retry.TargetRunId == submitted.TargetRunId && gateway.SubmitCount == 1,
                "exact submit retry returns the same task attempt and run");
            await RejectAsync(() => service.SubmitAsync(request with { Instruction = "Different work" },
                    sourceActor, CancellationToken.None),
                "request ID conflict rejects changed collaboration work", Check);

            service.Authorize("collaboration.get", targetActor,
                CerebralJson.Element(new { id = submitted.Id }));
            Reject(() => service.Authorize("collaboration.get", unrelated,
                    CerebralJson.Element(new { id = submitted.Id })),
                "unrelated administrative profile cannot inspect a task", Check);
            Reject(() => service.Authorize("collaboration.cancel", targetActor,
                    CerebralJson.Element(new { taskId = submitted.Id, expectedTaskRevision = submitted.Revision })),
                "target participant cannot control source-owned task", Check);

            var questionSet = new WorkflowQuestionSet("questions-1", 1,
                [new WorkflowQuestion("choice", "Choose", true, ["one", "two"])],
                new Dictionary<string, string>());
            gateway.Replace(submitted.AttemptId, run => run with
            {
                Revision = 2, Status = WorkflowStatus.Waiting, Questions = questionSet,
                WaitingReason = "Waiting for source answer."
            });
            await RejectAsync(() => service.ExecuteAsync("collaboration.reconcile",
                    CerebralJson.Element(new CollaborationReconcileRequest(submitted.Id, submitted.Revision)),
                    sourceActor, CancellationToken.None),
                "source agent cannot reconcile an uncertain target effect", Check);
            var waiting = (CollaborationTask)await service.ExecuteAsync("collaboration.reconcile",
                CerebralJson.Element(new CollaborationReconcileRequest(submitted.Id, submitted.Revision)),
                null, CancellationToken.None);
            Check(waiting.Status == CollaborationTaskStatus.Waiting &&
                waiting.Questions?.Id == questionSet.Id && waiting.TargetRunRevision == 2,
                "target question is projected with its exact revision");

            var answerRequest = new CollaborationAnswerRequest("answer-1", waiting.Id, waiting.Revision,
                    waiting.AttemptId, waiting.TargetRunRevision!.Value, questionSet.Id, questionSet.Revision,
                    new Dictionary<string, string> { ["choice"] = "one" });
            Reject(() => service.Authorize("collaboration.answer", sourceActor,
                    CerebralJson.Element(answerRequest)),
                "source agent cannot claim a user answer", Check);
            await RejectAsync(() => service.ExecuteAsync("collaboration.answer",
                    CerebralJson.Element(answerRequest), sourceActor, CancellationToken.None),
                "direct agent invocation cannot bypass owner answer authorization", Check);
            foreach (var stale in new[]
            {
                answerRequest with { AttemptId = "another-attempt" },
                answerRequest with { ExpectedTargetRunRevision = waiting.TargetRunRevision!.Value + 1 },
                answerRequest with { QuestionSetId = "another-question-set" },
                answerRequest with { ExpectedQuestionRevision = questionSet.Revision + 1 }
            })
                await RejectAsync(() => service.ExecuteAsync("collaboration.answer",
                        CerebralJson.Element(stale), null, CancellationToken.None),
                    "stale attempt or question cannot reach the target workflow", Check);
            gateway.Replace(submitted.AttemptId, run => run with { Revision = run.Revision + 1 });
            await RejectAsync(() => service.ExecuteAsync("collaboration.answer",
                    CerebralJson.Element(answerRequest), null, CancellationToken.None),
                "unsynchronized target run revision rejects an owner answer", Check);
            gateway.Replace(submitted.AttemptId, run => run with { Revision = waiting.TargetRunRevision!.Value });
            Check(gateway.AnswerCount == 0, "rejected answers have no target workflow signal");
            var answered = (CollaborationTask)await service.ExecuteAsync("collaboration.answer",
                CerebralJson.Element(answerRequest),
                null, CancellationToken.None);
            Check(answered.Status == CollaborationTaskStatus.Running && answered.Questions is null &&
                gateway.AnswerCount == 1, "source answer resumes the existing target workflow");
            var answerRetry = (CollaborationTask)await service.ExecuteAsync("collaboration.answer",
                CerebralJson.Element(answerRequest), null, CancellationToken.None);
            Check(answerRetry.Revision == answered.Revision && gateway.AnswerCount == 1,
                "lost answer response retry returns the saved task without repeating the target signal");
            await RejectAsync(() => service.ExecuteAsync("collaboration.answer",
                    CerebralJson.Element(answerRequest with
                    {
                        Answers = new Dictionary<string, string> { ["choice"] = "two" }
                    }), null, CancellationToken.None),
                "answer request ID cannot be reused for changed input", Check);

            var steerRequest = new CollaborationSteerRequest("steer-1", answered.Id, answered.Revision,
                "Keep the result concise.");
            await RejectAsync(() => service.ExecuteAsync("collaboration.steer",
                    CerebralJson.Element(steerRequest), sourceActor, CancellationToken.None),
                "agent steering cannot be recorded as owner direction", Check);
            var steered = (CollaborationTask)await service.ExecuteAsync("collaboration.steer",
                CerebralJson.Element(steerRequest), null, CancellationToken.None);
            var steerRetry = (CollaborationTask)await service.ExecuteAsync("collaboration.steer",
                CerebralJson.Element(steerRequest), null, CancellationToken.None);
            Check(steerRetry.Revision == steered.Revision && gateway.SteerCount == 1,
                "lost steering response retry returns the saved task without repeating the target signal");
            await RejectAsync(() => service.ExecuteAsync("collaboration.steer",
                CerebralJson.Element(steerRequest with { Text = "Different direction." }),
                null, CancellationToken.None),
                "steering request ID cannot be reused for changed input", Check);

            gateway.Replace(submitted.AttemptId, run => run with
            {
                Revision = 4, Status = WorkflowStatus.Completed,
                Result = CerebralJson.Element(new
                {
                    text = "Target finding.",
                    artifacts = new[] { new { kind = "document", id = "artifact-1", revision = 3 } },
                    unresolved = Array.Empty<string>()
                }),
                UpdatedAt = DateTimeOffset.UtcNow
            });
            var completed = (CollaborationTask)await service.ExecuteAsync("collaboration.reconcile",
                CerebralJson.Element(new CollaborationReconcileRequest(steered.Id, steered.Revision)),
                null, CancellationToken.None);
            Check(completed.Status == CollaborationTaskStatus.Completed &&
                completed.TargetRunRevision == 4 && completed.Result is not null,
                "completed target result is frozen on the task envelope");

            var page = store.ReadResult(completed.Id, 4, 0, 256);
            Check(page.Complete && page.Content.Contains("Target finding.", StringComparison.Ordinal) &&
                page.Sha256.Length == 64, "result page is bounded and revision-addressed");
            Reject(() => store.ReadResult(completed.Id, 3, 0, 256),
                "stale result revision is rejected", Check);

            var adopted = store.Adopt(new CollaborationAdoptRequest("adopt-1", completed.Id,
                completed.Revision, completed.AttemptId, 4));
            var adoptedRetry = store.Adopt(new CollaborationAdoptRequest("adopt-1", completed.Id,
                completed.Revision, completed.AttemptId, 4));
            Check(adopted.AdoptedAt is not null && adopted.AdoptedSourceRevision is > 0 &&
                adoptedRetry.Revision == adopted.Revision,
                "result adoption is durable and exact retry is idempotent");
            var sourceEvents = journal.ReadPage("source-a", 0, 20);
            Check(sourceEvents.Events.Count(item => item.Type == "collaboration.result") == 1 &&
                sourceEvents.Events.Single(item => item.Type == "collaboration.result").Content
                    .OfType<TextContentPart>().Single().Text == "Target finding.",
                "adoption appends one sanitized result event to the source conversation");
            Reject(() => store.Adopt(new CollaborationAdoptRequest("adopt-other", completed.Id,
                    adopted.Revision, completed.AttemptId, 4)),
                "a different adoption request cannot replace the adopted result", Check);

            var cancelRequest = new CollaborationTaskRequest("task-cancel", "source-a", "target-c",
                "Wait until cancelled.", [], []);
            var cancellable = await service.SubmitAsync(cancelRequest, sourceActor, CancellationToken.None);
            var cancelled = (CollaborationTask)await service.ExecuteAsync("collaboration.cancel",
                CerebralJson.Element(new CollaborationCancelRequest(cancellable.Id, cancellable.Revision)),
                sourceActor, CancellationToken.None);
            Check(cancelled.Status == CollaborationTaskStatus.Cancelled && cancelled.CancelRequested &&
                gateway.CancelCount == 1, "cancellation persists intent and signals the existing workflow");

            var grant = store.SaveGrant(new CollaborationProjectGrantRequest("grant-a-b",
                "project-a", "project-b", ["helper-b"],
                [CollaborationCapabilities.ExecuteTask, CollaborationCapabilities.ReadResource]));
            var grantRetry = store.SaveGrant(new CollaborationProjectGrantRequest("grant-a-b",
                "project-a", "project-b", ["helper-b"],
                [CollaborationCapabilities.ExecuteTask, CollaborationCapabilities.ReadResource]));
            Check(grant.Revision == 1 && grantRetry.Revision == grant.Revision,
                "cross-project grant save has exact retry semantics");

            var crossRequest = new CollaborationTaskRequest("task-cross", "source-a", "target-b",
                "Review the bounded reference and return findings.",
                [new CollaborationResourceReference("project-a", "document", "doc-1", 2)], []);
            await RejectAsync(() => service.SubmitAsync(crossRequest, sourceActor, CancellationToken.None),
                "cross-project submit without a grant is rejected", Check);
            crossRequest = crossRequest with { ProjectGrantId = grant.Id };
            var cross = await service.SubmitAsync(crossRequest, sourceActor, CancellationToken.None);
            Check(cross.ProjectGrantId == grant.Id && cross.ProjectGrantRevision == grant.Revision &&
                cross.TargetProfileId == "helper-b" && cross.TargetProjectId == "project-b",
                "cross-project submit freezes exact grant and target identities");

            var writeRequest = new CollaborationTaskRequest("task-cross-write", "source-a", "target-b",
                "Write a bounded resource.",
                [new CollaborationResourceReference("project-b", "document", "doc-2", 1,
                    CollaborationResourceAccess.Write)], []);
            writeRequest = writeRequest with { ProjectGrantId = grant.Id };
            await RejectAsync(() => service.SubmitAsync(writeRequest, sourceActor, CancellationToken.None),
                "cross-project write effects remain closed in the first checkpoint", Check);

            var revoked = store.RevokeGrant(grant.Id, grant.Revision);
            Check(revoked.RevokedAt is not null && revoked.Revision == 2,
                "cross-project grant revocation is revisioned");

            var nested = new CollaborationTaskRequest("task-nested", "target-a", "target-c",
                "Attempt nested delegation.", [], []);
            await RejectAsync(() => service.SubmitAsync(nested, targetActor, CancellationToken.None),
                "collaboration target cannot recursively delegate in the one-hop checkpoint", Check);

            var beforeRestart = store.Get(cancelled.Id);
            var missingRequest = new CollaborationTaskRequest("task-missing", "source-a", "target-c",
                "Persist an attempt whose linked run becomes unavailable.", [], []);
            var missing = await service.SubmitAsync(missingRequest, sourceActor, CancellationToken.None);
            gateway.Remove(missing.AttemptId);
            var reopened = new CollaborationStore(new EventJournal(database));
            var recoveredService = new CollaborationService(reopened, gateway,
                new AllowAllCollaborationCommunicationGate(), new EventHub());
            var recovered = recoveredService.Recover();
            Check(reopened.Get(beforeRestart.Id).Status == beforeRestart.Status &&
                recovered.Any(item => item.Id == beforeRestart.Id),
                "restart reopens durable task, attempt, cancellation, and result state");
            Check(reopened.Get(missing.Id).Status == CollaborationTaskStatus.ReconcileRequired,
                "restart marks a missing linked workflow for explicit reconciliation without replaying it");
            var firstViewPage = (CollaborationViewTaskPage)await recoveredService.ExecuteAsync("collaboration.view.list",
                CerebralJson.Element(new { sourceConversationId = "source-a", limit = 1 }),
                null, CancellationToken.None);
            var secondViewPage = (CollaborationViewTaskPage)await recoveredService.ExecuteAsync("collaboration.view.list",
                CerebralJson.Element(new { sourceConversationId = "source-a", limit = 1,
                    beforeRow = firstViewPage.NextBeforeRow, snapshotRow = firstViewPage.SnapshotRow }),
                null, CancellationToken.None);
            Check(firstViewPage.Tasks.Count == 1 && secondViewPage.Tasks.Count == 1 &&
                firstViewPage.Tasks[0].TaskId != secondViewPage.Tasks[0].TaskId &&
                firstViewPage.Tasks[0].TargetRunId is not null &&
                !CerebralJson.Element(firstViewPage).GetRawText().Contains("Persist an attempt", StringComparison.Ordinal),
                "Owner source-chat task pages use a complete snapshot cursor without instruction content.");
            Reject(() => recoveredService.Authorize("collaboration.view.list", sourceActor,
                CerebralJson.Element(new { sourceConversationId = "source-a", limit = 1 })),
                "Agent profiles cannot enumerate owner collaboration views", Check);
            var missingView = (CollaborationViewGraph)await recoveredService.ExecuteAsync("collaboration.view.graph",
                CerebralJson.Element(new { taskId = missing.Id, childOffset = 0, limit = 8 }),
                null, CancellationToken.None);
            Check(!missingView.Complete && !missingView.Synchronized && missingView.Coordinator is null &&
                missingView.IncompleteReason == "coordinator-unavailable",
                "A missing coordinator stays unavailable and cannot become an adopted chat.");

            var eventPage = reopened.ReadEvents(submitted.Id, 0, 2);
            Check(eventPage.Events.Count == 2 && eventPage.SnapshotSequence >= 4 &&
                eventPage.NextAfterSequence is not null,
                "task events use stable bounded snapshot pages");
            var nextPage = reopened.ReadEvents(submitted.Id, eventPage.NextAfterSequence!.Value, 20,
                eventPage.SnapshotSequence);
            Check(nextPage.Events.Count > 0 &&
                eventPage.Events.Concat(nextPage.Events).Select(item => item.Sequence)
                    .SequenceEqual(eventPage.Events.Concat(nextPage.Events).Select(item => item.Sequence).Order()),
                "task event continuation is ordered and snapshot-bound");

            Check(CollaborationService.OperationDescriptors.Single(item => item.Id == "collaboration.submit")
                    .RequiredCapabilities.SequenceEqual(["agents.collaborate"]) &&
                CollaborationService.OperationDescriptors.Single(item => item.Id == "collaboration.grants.save").OwnerOnly,
                "SDK descriptors expose capability-filtered submit and owner-only grants");

            var workflowJournal = new EventJournal(Path.Combine(root, "workflow-project.db"));
            var workflowStore = new WorkflowStore(workflowJournal);
            var projectRun = workflowStore.Submit(new WorkflowSubmission("project-turn", "project-chat", "lead",
                "fixture.workflow", "1.0.0", "Hello", null, WorkflowContract.EmptyObject,
                WorkflowContract.EmptyObject, Caller: new EventActor(EventActorKind.User, "owner"))
                { ProjectId = "project-a" });
            var summary = workflowStore.GetConversation("fixture.workflow", projectRun.Submission.ConversationId);
            Check(summary?.ProjectId == "project-a",
                "conversation project identity comes from the host-frozen binding");

            await CheckProductionCommandBoundaryAsync(root, Check);
            checks += await CollaborationCoordinationCompositionTests.RunAsync();

            Console.WriteLine($"Collaboration task checks passed: {checks}.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void CheckSchemaFiveUpgrade(string root, Action<bool, string> check)
    {
        var database = Path.Combine(root, "schema-five.db");
        _ = new EventJournal(database);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE collaboration_events;
                DROP TABLE collaboration_tasks;
                DROP TABLE collaboration_project_grants;
                ALTER TABLE workflow_conversations DROP COLUMN project_id;
                PRAGMA user_version = 5;
                """;
            downgrade.ExecuteNonQuery();
        }

        _ = new EventJournal(database);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            connection.Open();
            using var verify = connection.CreateCommand();
            verify.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM pragma_table_info('workflow_conversations') WHERE name='project_id'),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='collaboration_tasks'),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='collaboration_project_grants');
                """;
            using var reader = verify.ExecuteReader();
            reader.Read();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            check(reader.GetInt64(0) == 1 && reader.GetInt64(1) == 1 && reader.GetInt64(2) == 1 &&
                (long)version.ExecuteScalar()! == 6,
                "schema 5 upgrades in place to host project bindings and collaboration tables");
        }
    }

    private static async Task CheckProductionCommandBoundaryAsync(string root, Action<bool, string> check)
    {
        var state = new StateStore(Path.Combine(root, "production-boundary"));
        var configuration = new Configuration(state);
        configuration.Get();
        var events = new EventHub();
        var sourceProfile = new AgentProfile
        {
            Id = "command-lead", Name = "Command lead", Role = "lead", ModelId = "fixture",
            Capabilities = ["agents.collaborate"]
        };
        var targetProfile = new AgentProfile
        {
            Id = "command-helper", Name = "Command helper", Role = "helper", ModelId = "fixture"
        };
        var crossTargetProfile = new AgentProfile
        {
            Id = "command-cross-helper", Name = "Cross-project helper", Role = "helper", ModelId = "fixture",
            Capabilities = ["filesystem.read"], AllowedTools = ["*"], AllowedRoots = [root]
        };
        state.Put("agent", sourceProfile.Id, sourceProfile);
        state.Put("agent", targetProfile.Id, targetProfile);
        state.Put("agent", crossTargetProfile.Id, crossTargetProfile);
        await using var models = new ModelRuntime(state, configuration.Get, events);
        await using var tools = new ToolDispatcher(configuration.Get);
        var effects = new WorkerWorkflowEffects(state, configuration, models, tools);
        var workflows = new WorkerWorkflows(state, configuration, events,
            [new ConversationWorkflowModule(), effects],
            [ConversationWorkflowModule.PackageBinding,
                new(effects.Id, WorkerWorkflowEffects.ModuleVersion, true, [])], tools);
        using var scheduler = new JobScheduler(state, configuration, new NoModelExecutor(), models, events,
            NullLogger<JobScheduler>.Instance, workflows);
        var collaborationStore = new CollaborationStore(workflows.Journal);
        var collaboration = new CollaborationService(collaborationStore,
            new WorkerCollaborationWorkflowGateway(workflows, scheduler, collaborationStore),
            new AllowAllCollaborationCommunicationGate(), events);
        var dispatcher = new CommandDispatcher(state, configuration, models, scheduler, tools, events,
            workflows: workflows, collaboration: collaboration);

        SeedConversation(workflows.Store, "command-source", sourceProfile.Id, "command-project");
        SeedConversation(workflows.Store, "command-target", targetProfile.Id, "command-project");
        SeedConversation(workflows.Store, "command-cross-target", crossTargetProfile.Id, "other-project");
        var communication = new ConversationCommunicationStore(workflows.Journal);
        foreach (var target in new[] { "command-target", "command-cross-target" })
            communication.SetAllowed(new(new("command-source", target), 0, true));

        async Task<CommandResult> Command(string operation, object input, string? actor = null) =>
            await dispatcher.ExecuteAsync(new(operation, CerebralJson.Element(input), actor));

        var request = new CollaborationTaskRequest("command-submit", "command-source", "command-target",
            "Return one bounded finding through the production collaboration adapter.", [], []);
        var submittedResult = await Command("collaboration.submit", request, sourceProfile.Id);
        check(submittedResult.Success, "command dispatcher routes collaboration through the production adapter: " + submittedResult.Error);
        var submitted = CerebralJson.Read<CollaborationTask>(submittedResult.Data);
        var targetRun = workflows.Store.Get(submitted.TargetRunId!);
        check(targetRun.Id == submitted.AttemptId && targetRun.Submission.Caller is
                { Kind: EventActorKind.Service, Id: "cerebral.collaboration" } &&
              targetRun.Submission.ProjectId == "command-project" && workflows.Store.CountActive() == 1,
            "production adapter submits one service-owned target run to the existing workflow queue");

        var retry = CerebralJson.Read<CollaborationTask>((await Command("collaboration.submit", request, sourceProfile.Id)).Data);
        check(retry.Id == submitted.Id && retry.AttemptId == submitted.AttemptId &&
              workflows.Store.CountActive() == 1,
            "production command retry reuses the exact task and target attempt");

        var discover = await Command("sdk.discover", new { }, sourceProfile.Id);
        check(discover.Success && discover.Data.GetProperty("operations").EnumerateArray()
                .Any(item => item.GetProperty("id").GetString() == "collaboration.submit"),
            "capability-filtered SDK discovery exposes collaboration commands to the source agent");
        var targetDiscover = await Command("sdk.discover", new { }, targetProfile.Id);
        check(targetDiscover.Success && !targetDiscover.Data.GetProperty("operations").EnumerateArray()
                .Any(item => item.GetProperty("id").GetString() == "collaboration.submit"),
            "SDK discovery hides collaboration submission from profiles without agents.collaborate");
        var describe = await Command("sdk.describe", new { id = "collaboration.submit" }, sourceProfile.Id);
        check(describe.Success && describe.Data.GetProperty("inputSchema").GetProperty("properties")
                .TryGetProperty("targetConversationId", out _),
            "SDK describe returns the core collaboration schema through the real dispatcher");

        var grant = collaborationStore.SaveGrant(new CollaborationProjectGrantRequest("command-cross-grant",
            "command-project", "other-project", [crossTargetProfile.Id],
            [CollaborationCapabilities.ExecuteTask, CollaborationCapabilities.ReadResource]));
        var crossRequest = new CollaborationTaskRequest("command-cross-submit", "command-source",
            "command-cross-target", "Return a bounded cross-project finding without effects.", [], [])
            { ProjectGrantId = grant.Id };
        var crossResult = await Command("collaboration.submit", crossRequest, sourceProfile.Id);
        check(crossResult.Success, "production adapter admits the exact revisioned cross-project route: " + crossResult.Error);
        var crossTask = CerebralJson.Read<CollaborationTask>(crossResult.Data);
        var crossSnapshot = WorkerExecutionSnapshot.Read(workflows.Store.Get(crossTask.TargetRunId!).Submission.SettingsSnapshot);
        check(crossSnapshot.Profile.AllowedTools.Count == 0 && crossSnapshot.Profile.AllowedRoots.Count == 0 &&
              crossSnapshot.Profile.Capabilities.Count == 0 && crossSnapshot.Tools.Count == 0,
            "production cross-project admission strips tools, roots, capabilities, and executable descriptors");
    }

    private static void SeedConversation(WorkflowStore store, string conversationId, string profileId,
        string projectId)
    {
        var run = store.Submit(new WorkflowSubmission("seed-" + conversationId, conversationId, profileId,
            ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
            "Seed conversation binding.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject,
            Caller: new EventActor(EventActorKind.User, "owner")) { ProjectId = projectId });
        var claimed = store.TryClaim(run.Id) ?? throw new InvalidOperationException("Fixture seed was not claimable.");
        store.ApplyStep(claimed.Id, claimed.Revision,
            new WorkflowStep(WorkflowContract.EmptyObject,
                new CompleteWorkflow(CerebralJson.Element(new { summary = "Seed complete." }))));
    }

    private static void Reject(Action action, string name, Action<bool, string> check)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            check(true, name);
            return;
        }
        throw new Exception("FAIL: " + name);
    }

    private static async Task RejectAsync(Func<Task> action, string name, Action<bool, string> check)
    {
        try { await action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            check(true, name);
            return;
        }
        throw new Exception("FAIL: " + name);
    }

    private sealed class NoModelExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("No model execution is expected in this test.");
    }

    private sealed class FakeGateway(params ConversationSummary[] conversations) : ICollaborationWorkflowGateway, ICollaborationViewGateway
    {
        private readonly Dictionary<string, ConversationSummary> conversations =
            conversations.ToDictionary(item => item.Id, StringComparer.Ordinal);
        public Dictionary<string, WorkflowRun> Runs { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> ChildIds { get; set; } = [];
        public int SubmitCount { get; private set; }
        public int AnswerCount { get; private set; }
        public int SteerCount { get; private set; }
        public int CancelCount { get; private set; }

        public ConversationSummary GetConversation(string conversationId) =>
            conversations.TryGetValue(conversationId, out var item) ? item :
                throw new KeyNotFoundException("Fixture conversation was not found.");

        public WorkflowRun? TryGet(string runId) => Runs.GetValueOrDefault(runId);
        public WorkflowQuestionSet ReadSourceOwnerAnswer(string runId, string questionSetId, long questionRevision) =>
            throw new NotSupportedException("This fake does not simulate owner workflow answers.");
        public IReadOnlyList<string> ChildRunIdsForView(string parentRunId) => ChildIds;

        public Task<WorkflowRun> SubmitAsync(CollaborationTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCount++;
            var now = DateTimeOffset.UtcNow;
            var run = new WorkflowRun
            {
                Id = task.AttemptId,
                Submission = new WorkflowSubmission(task.AttemptId, task.Request.TargetConversationId,
                    task.TargetProfileId, ConversationWorkflowContract.DefinitionId,
                    ConversationWorkflowContract.DefinitionVersion, task.Request.Instruction, null,
                    WorkflowContract.EmptyObject, WorkflowContract.EmptyObject,
                    Caller: new EventActor(EventActorKind.Service, "cerebral.collaboration"))
                    { ProjectId = task.TargetProjectId },
                Revision = 1,
                Status = WorkflowStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now
            };
            Runs.Add(run.Id, run);
            return Task.FromResult(run);
        }

        public WorkflowRun Answer(CollaborationTask task, CollaborationAnswerRequest request)
        {
            var run = Required(task);
            if (run.Questions?.Id != request.QuestionSetId ||
                run.Questions.Revision != request.ExpectedQuestionRevision)
                throw new WorkflowConflictException("Fixture question changed.");
            AnswerCount++;
            return Runs[run.Id] = run with
            {
                Revision = run.Revision + 1,
                Status = WorkflowStatus.Running,
                Questions = null,
                WaitingReason = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        public WorkflowRun Steer(CollaborationTask task, CollaborationSteerRequest request)
        {
            var run = Required(task);
            SteerCount++;
            return Runs[run.Id] = run with
            {
                Revision = run.Revision + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        public WorkflowRun Cancel(CollaborationTask task)
        {
            var run = Required(task);
            CancelCount++;
            return Runs[run.Id] = run with
            {
                Revision = run.Revision + 1,
                Status = WorkflowStatus.Cancelled,
                CancelRequested = true,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        public Task<WorkflowRun> ReconcileAsync(CollaborationTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Required(task));
        }

        public void Replace(string id, Func<WorkflowRun, WorkflowRun> replace) =>
            Runs[id] = replace(Runs[id]);

        public void Remove(string id) => Runs.Remove(id);

        private WorkflowRun Required(CollaborationTask task) =>
            task.TargetRunId is { } id && Runs.TryGetValue(id, out var run) ? run :
                throw new WorkflowConflictException("Fixture target run is unavailable.");
    }
}

internal sealed class AllowAllCollaborationCommunicationGate : ICollaborationCommunicationGate
{
    public void RequireAllowed(string sourceConversationId, string targetConversationId) { }
}
