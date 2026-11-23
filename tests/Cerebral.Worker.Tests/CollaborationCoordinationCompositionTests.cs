using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Actual Collaboration, workflow admission, default-engine planning and SQLite composition.
/// The remote model route is a metadata-only loopback fixture; no model is loaded or invoked.
/// </summary>
internal static class CollaborationCoordinationCompositionTests
{
    private const string ProjectId = "composition-project";
    private const string RemoteWorkerId = "remoteGB10";
    private const string RemoteModelId = "remote-composition-model";
    private const string RemoteInstallationId = "remote-gb10-fixture-installation";

    public static async Task<int> RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "cerebral-collaboration-composition-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var remote = builder.Build();
        var calls = new ConcurrentQueue<string>();
        var accessKey = new string('k', 48);
        Configuration? configuration = null;
        remote.MapPost("/api/command", async (HttpRequest request) =>
        {
            if (request.Headers["X-Cerebral-Key"].ToString() != accessKey) return Results.Unauthorized();
            var command = await request.ReadFromJsonAsync<CommandRequest>(CerebralJson.Options, request.HttpContext.RequestAborted);
            if (command is null) return Results.Json(CommandResult.Fail("Missing fixture command."), CerebralJson.Options);
            calls.Enqueue(command.Operation);
            object value = command.Operation switch
            {
                "health" => new HostInfo("Pegasus Cerebral", "fixture", 1, "Remote GB10 fixture", "Arm64", "Linux", false,
                    "/srv/cerebral", InstallationId: RemoteInstallationId),
                "worker.management.status" => new WorkerManagementStatus(RemoteInstallationId, "assigned",
                    configuration?.Get().InstallationId ?? "missing-controller", "Composition fixture", null),
                "models.inspect" when command.Arguments.GetProperty("id").GetString() == RemoteModelId => RemoteModel(),
                _ => throw new InvalidOperationException("Unexpected remote fixture operation: " + command.Operation)
            };
            return Results.Json(CommandResult.Ok(value), CerebralJson.Options);
        });

        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                checks++;
                return;
            }
            throw new InvalidOperationException(message);
        }
        async Task RejectAsync(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                checks++;
                return;
            }
            throw new InvalidOperationException(message);
        }

        try
        {
            await remote.StartAsync();
            var state = new StateStore(Path.Combine(root, "state"));
            configuration = new Configuration(state);
            var settings = configuration.Get();
            settings.Coordinator = true;
            settings.QueueCapacity = 100;
            settings.MaxConcurrentJobs = 1;
            settings.DefaultModelId = "local-composition-model";
            settings.DefaultModelWorkerId = "local";
            settings.Workers = [new WorkerRegistration
            {
                Id = RemoteWorkerId,
                Name = "Remote GB10 fixture",
                Url = remote.Urls.Single(),
                AccessKey = accessKey
            }];
            configuration.Save(settings);
            state.Put("model", "local-composition-model", new ModelRecord
            {
                Id = "local-composition-model",
                Name = "Local composition metadata",
                Path = Path.Combine(root, "not-loaded.gguf"),
                AutomaticContextSizing = false,
                ContextTokens = 8192,
                MaximumContextTokens = 8192,
                RecommendedContextTokens = 8192
            });

            var sourceProfile = Profile("composition-root", "lead", "local-composition-model", "local",
                ["agents.collaborate"]);
            var targetProfile = Profile("composition-orchestrator", "lead", RemoteModelId, RemoteWorkerId,
                ["agents.delegate", "agents.collaborate"]);
            state.Put("agent", sourceProfile.Id, sourceProfile);
            state.Put("agent", targetProfile.Id, targetProfile);
            for (var index = 1; index <= 7; index++)
            {
                var helper = Profile("composition-helper-" + index, "helper", RemoteModelId, RemoteWorkerId, []);
                helper.Instructions = "Return only HELPER_" + index + "_EVIDENCE for this isolated assignment.";
                state.Put("agent", helper.Id, helper);
            }

            var engineRoot = Path.Combine(root, "Engines");
            EngineHostTests.Pack(engineRoot, "A", true);
            ConfigureMaximumChildren(engineRoot, "A", 6);
            await using var engine = new EngineController(engineRoot, Path.Combine(root, "EngineState"));
            await engine.InitializeAsync();
            Check(engine.SupportsWork("cerebral.agents.plan", 1),
                "The actual default engine exposes the assignment-plan contract.");

            var model = new NeverModels();
            var tools = new NoTools();
            var events = new EventHub();
            WorkerWorkflows? workflows = null;
            var effects = new WorkerWorkflowEffects(state, configuration, model, tools, workflowHost: () => workflows!);
            workflows = new WorkerWorkflows(state, configuration, events,
                [new ConversationWorkflowModule(), effects],
                [ConversationWorkflowModule.PackageBinding,
                    new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])],
                tools, engine: engine);
            await using var runtime = new ModelRuntime(state, configuration.Get, events);
            using var scheduler = new JobScheduler(state, configuration, new NoLegacyExecutor(), runtime, events,
                NullLogger<JobScheduler>.Instance, workflows);
            var collaborationStore = new CollaborationStore(workflows.Journal);
            var collaboration = new CollaborationService(collaborationStore,
                new WorkerCollaborationWorkflowGateway(workflows, scheduler, collaborationStore),
                new AllowAllCollaborationCommunicationGate(), events);

            SeedConversation(workflows.Store, "composition-source", sourceProfile.Id);
            SeedConversation(workflows.Store, "composition-target", targetProfile.Id);
            SeedConversation(workflows.Store, "composition-copy-target", targetProfile.Id);
            SeedConversation(workflows.Store, "composition-limit-target", targetProfile.Id);
            SeedConversation(workflows.Store, "composition-cancel-target", targetProfile.Id);
            var communication = new ConversationCommunicationStore(workflows.Journal);
            foreach (var permissionTargetConversationId in new[] { "composition-target", "composition-copy-target", "composition-limit-target", "composition-cancel-target" })
                communication.SetAllowed(new(new("composition-source", permissionTargetConversationId), 0, true));

            var requestedCopies = Enumerable.Range(1, 6).Select(index =>
                new WorkerWorkflowEffects.AgentAssignment("composition-helper-1", 1, "copy-" + index)).ToArray();
            Check(WorkerWorkflowEffects.RequestedMultiplicityContains(requestedCopies, requestedCopies) &&
                !WorkerWorkflowEffects.RequestedMultiplicityContains(requestedCopies,
                    [.. requestedCopies, new("composition-helper-1", 1, "copy-7")]) &&
                !WorkerWorkflowEffects.RequestedMultiplicityContains(requestedCopies,
                    [new("composition-helper-1", 2, "stale-revision")]),
                "Engine plan membership is bounded by requested profile-and-revision multiplicity, not profile presence alone.");

            var submitted = await collaboration.SubmitAsync(new CollaborationTaskRequest(
                "composition-submit", "composition-source", "composition-target",
                "Coordinate six isolated specialists and return their attributed evidence.", [], [], StepLimit: 32),
                sourceProfile, CancellationToken.None);
            var target = workflows.Store.Get(submitted.TargetRunId!);
            var targetSnapshot = WorkerExecutionSnapshot.Read(target.Submission.SettingsSnapshot);
            Check(target.Submission.ParentRunId is null && target.Submission.ProjectId == ProjectId &&
                target.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.collaboration" } &&
                targetSnapshot.Profile is { Id: "composition-orchestrator", Role: "lead", WorkerId: "local", ModelWorkerId: RemoteWorkerId } &&
                targetSnapshot.Capabilities.Contains("agents.delegate", StringComparer.Ordinal),
                "Collaboration creates an independent top-level same-project local lead with its frozen remote model route.");

            var six = Assignments(6, state);
            var accepted = await InvokeDelegateAsync(workflows, effects, target, six);
            Check(accepted.Result.State == WorkflowEffectState.Succeeded,
                "The existing First Mate admits the engine-approved six-helper wave: " + accepted.Result.Error);
            var children = workflows.Store.ChildRunIds(target.Id).Select(workflows.Store.Get).ToArray();
            Check(children.Length == 6 && accepted.Result.Data!.Value.GetProperty("runIds").GetArrayLength() == 6,
                "Exactly six durable helper runs are created.");
            var viewArguments = CerebralJson.Element(new { taskId = submitted.Id, childOffset = 0, limit = 4 });
            collaboration.Authorize("collaboration.view.graph", null, viewArguments);
            Reject(() => collaboration.Authorize("collaboration.view.graph", sourceProfile, viewArguments),
                "Only the owner can inspect the collaboration navigation graph.");
            var firstView = (CollaborationViewGraph)await collaboration.ExecuteAsync("collaboration.view.graph",
                viewArguments, null, CancellationToken.None);
            var secondView = (CollaborationViewGraph)await collaboration.ExecuteAsync("collaboration.view.graph",
                CerebralJson.Element(new { taskId = submitted.Id, childOffset = 4, limit = 4 }),
                null, CancellationToken.None);
            Check(firstView.Version == 1 && firstView.Complete && !firstView.Synchronized &&
                firstView.Coordinator is { RunId: var coordinatorId, ParentRunId: null } && coordinatorId == target.Id &&
                firstView.ChildCount == 6 && firstView.Workers.Count == 4 && firstView.NextChildOffset == 4 &&
                secondView.Workers.Count == 2 && secondView.NextChildOffset is null &&
                firstView.Workers.Concat(secondView.Workers).Select(item => item.RunId)
                    .SequenceEqual(children.Select(item => item.Id)) &&
                firstView.Workers.Concat(secondView.Workers).All(item =>
                    item.ParentRunId == target.Id && item.ProjectId == ProjectId),
                "Owner view pages verify the accepted coordinator and all six exact helper lineages: " +
                CerebralJson.Element(firstView).GetRawText() + " / " + CerebralJson.Element(secondView).GetRawText());
            Check(!CerebralJson.Element(firstView).GetRawText().Contains("Coordinate six isolated", StringComparison.Ordinal),
                "Navigation graph excludes the accepted request and transcript content.");
            await RejectAsync(() => collaboration.ExecuteAsync("collaboration.view.graph",
                CerebralJson.Element(new { taskId = submitted.Id, childOffset = 0, limit = 4, sourceConversationId = "foreign" }),
                null, CancellationToken.None), "Owner graph rejects extra caller-supplied lineage fields.");
            Check(children.Select(child => child.Submission.ConversationId)
                    .Append(target.Submission.ConversationId).Append("composition-source")
                    .Distinct(StringComparer.Ordinal).Count() == 8 &&
                children.All(child => child.Submission.ProjectId == ProjectId && child.Submission.ParentRunId == target.Id &&
                    child.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.coordination" }),
                "Source, target and six helper contexts remain independent while retaining exact project lineage.");
            Check(children.All(child =>
            {
                var snapshot = WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot);
                return snapshot.Profile.WorkerId == "local" && snapshot.Profile.ModelWorkerId == RemoteWorkerId &&
                    snapshot.Model is { ModelWorkerId: RemoteWorkerId, ModelWorkerInstallationId: RemoteInstallationId } &&
                    !snapshot.Capabilities.Contains("agents.delegate", StringComparer.Ordinal) &&
                    !snapshot.Capabilities.Contains("agents.collaborate", StringComparer.Ordinal) &&
                    !snapshot.Profile.Capabilities.Contains("agents.delegate", StringComparer.Ordinal) &&
                    !snapshot.Profile.Capabilities.Contains("agents.collaborate", StringComparer.Ordinal);
            }), "Every local helper freezes the verified remoteGB10 model origin and strips recursive authority.");

            var frozenChildren = children.ToDictionary(child => child.Id,
                child => CerebralJson.Element(child).GetRawText(), StringComparer.Ordinal);
            foreach (var helperId in children.Select(child => child.Submission.ProfileId))
            {
                var changed = state.Get<AgentProfile>("agent", helperId)!;
                changed.Revision++;
                changed.Instructions = "MUTATED after admission; this must not change an accepted child snapshot.";
                state.Put("agent", helperId, changed);
            }
            var callsBeforeRetry = calls.Count;
            var repeated = await accepted.Handler.ExecuteAsync(accepted.Context, CancellationToken.None);
            Check(repeated.State == WorkflowEffectState.Succeeded &&
                JsonElement.DeepEquals(repeated.Data!.Value, accepted.Result.Data!.Value) &&
                calls.Count == callsBeforeRetry &&
                workflows.Store.ChildRunIds(target.Id).Count == 6 &&
                children.All(child => frozenChildren[child.Id] == CerebralJson.Element(workflows.Store.Get(child.Id)).GetRawText()),
                "A lost-response retry reuses frozen helper identities and settings without another remote metadata lookup.");

            Reject(() => workflows.Store.Submit(children[0].Submission with
            {
                RequestId = "composition-grandchild",
                ConversationId = "composition-grandchild-context",
                ParentRunId = children[0].Id
            }), "The durable ParentRunId guard must reject a grandchild.");
            var nested = await InvokeDelegateAgainstChildAsync(effects, children[0], six);
            Check(nested.State == WorkflowEffectState.Failed && workflows.Store.ChildRunIds(children[0].Id).Count == 0,
                "The First Mate command itself refuses delegation from a child run.");

            workflows.Store.CompleteEffect(target.Id, accepted.Context.EffectId, accepted.Result);
            var targetClaim = workflows.Store.TryClaim(target.Id)!;
            workflows.Store.ApplyStep(targetClaim.Id, targetClaim.Revision,
                new(WorkflowContract.EmptyObject, new WaitForWorkflowRuns(children.Select(child => child.Id).ToArray())));
            for (var index = 0; index < children.Length; index++)
            {
                var child = workflows.Store.TryClaim(children[index].Id)!;
                var success = index != 2;
                workflows.Store.ApplyStep(child.Id, child.Revision, new(WorkflowContract.EmptyObject,
                    new CompleteWorkflow(CerebralJson.Element(new { summary = "HELPER_" + (index + 1) + "_EVIDENCE" }),
                        success, success ? null : "Specialist 3 reported an attributed fixture failure.")));
            }
            var resumed = workflows.Store.ResumeCompletedDependencies().Single(run => run.Id == target.Id);
            var outcomes = resumed.Signal.Data.GetProperty("children").EnumerateArray().ToArray();
            Check(outcomes.Length == 6 && outcomes.Count(item => item.GetProperty("status").GetString() == "Completed") == 5 &&
                outcomes.Single(item => item.GetProperty("profileId").GetString() == "composition-helper-3")
                    .GetProperty("status").GetString() == "Failed" &&
                outcomes.All(item => item.GetProperty("outcomeSha256").GetString() is { Length: 64 } digest && digest.All(char.IsAsciiHexDigit)),
                "Dependency collection preserves six attributed sealed outcomes, including the exact failed specialist.");
            var completion = workflows.Store.TryClaim(target.Id)!;
            var targetResult = CerebralJson.Element(new
            {
                text = "Five specialists completed; composition-helper-3 failed with retained evidence.",
                failedProfileId = "composition-helper-3",
                helperOutcomes = outcomes
            });
            workflows.Store.ApplyStep(completion.Id, completion.Revision,
                new(WorkflowContract.EmptyObject, new CompleteWorkflow(targetResult)));

            await RejectAsync(async () =>
            {
                _ = await collaboration.ExecuteAsync("collaboration.reconcile",
                    CerebralJson.Element(new CollaborationReconcileRequest(submitted.Id, submitted.Revision + 1)),
                    null, CancellationToken.None);
            }, "A stale collaboration reconciliation revision must be rejected.");
            var reconciled = (CollaborationTask)await collaboration.ExecuteAsync("collaboration.reconcile",
                CerebralJson.Element(new CollaborationReconcileRequest(submitted.Id, submitted.Revision)),
                null, CancellationToken.None);
            Check(reconciled.Status == CollaborationTaskStatus.Completed && reconciled.AttemptId == target.Id &&
                reconciled.TargetRunId == target.Id && reconciled.TargetRunRevision == workflows.Store.Get(target.Id).Revision &&
                JsonElement.DeepEquals(reconciled.Result!.Value, targetResult),
                "Exact reconciliation freezes the terminal orchestrator result and run revision on the Collaboration envelope.");
            var settledView = (CollaborationViewGraph)await collaboration.ExecuteAsync("collaboration.view.graph",
                CerebralJson.Element(new { taskId = reconciled.Id, childOffset = 0, limit = 64 }),
                null, CancellationToken.None);
            Check(settledView.Complete && settledView.Synchronized &&
                settledView.TaskStatus == CollaborationTaskStatus.Completed &&
                settledView.Coordinator?.Status == WorkflowStatus.Completed && settledView.Workers.Count == 6,
                "Owner graph reports completed only after task and exact coordinator revisions agree.");
            var targetRunRevision = reconciled.TargetRunRevision ??
                throw new InvalidOperationException("Reconciled target revision is unavailable.");
            Reject(() => collaborationStore.Adopt(new CollaborationAdoptRequest("composition-adopt-stale", reconciled.Id,
                    reconciled.Revision, "foreign-attempt", targetRunRevision)),
                "Adoption must reject a foreign target attempt.");
            var adopted = collaborationStore.Adopt(new CollaborationAdoptRequest("composition-adopt", reconciled.Id,
                reconciled.Revision, reconciled.AttemptId, targetRunRevision));
            var adoptedRetry = collaborationStore.Adopt(new CollaborationAdoptRequest("composition-adopt", reconciled.Id,
                reconciled.Revision, reconciled.AttemptId, targetRunRevision));
            var sourceEvents = workflows.Journal.ReadPage("composition-source", 0, 100).Events
                .Where(item => item.Type == "collaboration.result" && item.CorrelationId == reconciled.Id).ToArray();
            Check(adopted.AdoptedAt is not null && adoptedRetry.Revision == adopted.Revision && sourceEvents.Length == 1 &&
                sourceEvents[0].Provenance is { Source: EventSourceKind.Service, SourceId: "cerebral.collaboration", JobId: var adoptedRun } &&
                adoptedRun == target.Id,
                "Exact adoption is idempotent and appends one source-visible result attributed to the target run.");

            await RejectAsync(() => collaboration.SubmitAsync(new CollaborationTaskRequest(
                    "composition-nested-collaboration", "composition-target", "composition-cancel-target",
                    "Attempt a second collaboration hop.", [], []), targetProfile, CancellationToken.None),
                "A Collaboration target cannot submit a second Collaboration hop even when it retains agents.collaborate.");

            var copyTask = await collaboration.SubmitAsync(new CollaborationTaskRequest(
                "composition-copy-submit", "composition-source", "composition-copy-target",
                "Run six isolated instances of one exact specialist profile.", [], []), sourceProfile, CancellationToken.None);
            var copyTarget = workflows.Store.Get(copyTask.TargetRunId!);
            var copies = await InvokeDelegateAsync(workflows, effects, copyTarget,
                RepeatedAssignments(6, "composition-helper-1", state));
            Check(copies.Result.State == WorkflowEffectState.Succeeded,
                "Six assignments may reuse one exact admitted helper profile: " + copies.Result.Error);
            var copyChildren = workflows.Store.ChildRunIds(copyTarget.Id).Select(workflows.Store.Get).ToArray();
            Check(copyChildren.Length == 6 && copyChildren.Select(child => child.Id).Distinct(StringComparer.Ordinal).Count() == 6 &&
                copyChildren.Select(child => child.Submission.ConversationId).Distinct(StringComparer.Ordinal).Count() == 6 &&
                copyChildren.Select((child, index) => child.Submission.ProfileId == "composition-helper-1" &&
                    child.Submission.UserText == "Return only COPY_" + (index + 1) +
                        "_EVIDENCE from isolated specialist instance " + (index + 1) + "." &&
                    WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot).Profile.ModelWorkerId == RemoteWorkerId).All(value => value),
                "Each copy receives its own run and conversation while retaining the same frozen remote-bound helper identity.");
            var copiesBeforeReconcile = copyChildren.ToDictionary(child => child.Id,
                child => CerebralJson.Element(child).GetRawText(), StringComparer.Ordinal);
            var callsBeforeReconcile = calls.Count;
            var reconciledCopies = await copies.Handler.ReconcileAsync(copies.Context, CancellationToken.None);
            Check(reconciledCopies.State == WorkflowEffectState.Succeeded &&
                JsonElement.DeepEquals(reconciledCopies.Data!.Value, copies.Result.Data!.Value) &&
                calls.Count == callsBeforeReconcile && workflows.Store.ChildRunIds(copyTarget.Id).Count == 6 &&
                copyChildren.All(child => copiesBeforeReconcile[child.Id] == CerebralJson.Element(workflows.Store.Get(child.Id)).GetRawText()),
                "Exact reconciliation returns the six existing copy identities without replay or another remote metadata lookup.");
            workflows.Store.CompleteEffect(copyTarget.Id, copies.Context.EffectId, copies.Result);
            var copyClaim = workflows.Store.TryClaim(copyTarget.Id)!;
            workflows.Store.ApplyStep(copyClaim.Id, copyClaim.Revision,
                new(WorkflowContract.EmptyObject, new WaitForWorkflowRuns(copyChildren.Select(child => child.Id).ToArray())));
            for (var index = 0; index < copyChildren.Length; index++)
            {
                var child = workflows.Store.TryClaim(copyChildren[index].Id)!;
                var success = index != 3;
                workflows.Store.ApplyStep(child.Id, child.Revision, new(WorkflowContract.EmptyObject,
                    new CompleteWorkflow(success
                            ? CerebralJson.Element(new { summary = "COPY_" + (index + 1) + "_EVIDENCE" })
                            : WorkflowContract.EmptyObject,
                        success, success ? null : "Copy 4 retained its attributed fixture failure.")));
            }
            var resumedCopies = workflows.Store.ResumeCompletedDependencies().Single(run => run.Id == copyTarget.Id);
            var copyOutcomes = resumedCopies.Signal.Data.GetProperty("children").EnumerateArray().ToArray();
            Check(copyOutcomes.Length == 6 &&
                copyOutcomes.Select(item => item.GetProperty("runId").GetString()).Distinct(StringComparer.Ordinal).Count() == 6 &&
                copyOutcomes.All(item => item.GetProperty("profileId").GetString() == "composition-helper-1") &&
                copyOutcomes.Count(item => item.GetProperty("status").GetString() == "Failed") == 1 &&
                copyOutcomes.Single(item => item.GetProperty("status").GetString() == "Failed")
                    .GetProperty("summary").GetString()?.Contains("Copy 4", StringComparison.Ordinal) == true,
                "The dependency barrier attributes six same-profile results by run identity and preserves the one failed copy.");
            var copyCompletion = workflows.Store.TryClaim(copyTarget.Id)!;
            workflows.Store.ApplyStep(copyCompletion.Id, copyCompletion.Revision,
                new(WorkflowContract.EmptyObject, new CompleteWorkflow(CerebralJson.Element(new
                {
                    text = "Five copies completed; one attributed copy failed.", helperOutcomes = copyOutcomes
                }))));

            var limitedTask = await collaboration.SubmitAsync(new CollaborationTaskRequest(
                "composition-limit-submit", "composition-source", "composition-limit-target",
                "Attempt an oversized helper wave.", [], []), sourceProfile, CancellationToken.None);
            var limitedTarget = workflows.Store.Get(limitedTask.TargetRunId!);
            var limited = await InvokeDelegateAsync(workflows, effects, limitedTarget,
                RepeatedAssignments(7, "composition-helper-1", state));
            Check(limited.Result.State == WorkflowEffectState.Failed &&
                limited.Result.Error?.Contains("child limit of 6", StringComparison.Ordinal) == true &&
                workflows.Store.ChildRunIds(limitedTarget.Id).Count == 0,
                "The configured default engine counts copies independently and rejects a seventh before any child admission.");
            _ = (CollaborationTask)await collaboration.ExecuteAsync("collaboration.cancel",
                CerebralJson.Element(new CollaborationCancelRequest(limitedTask.Id, limitedTask.Revision)),
                sourceProfile, CancellationToken.None);

            var cancellationTask = await collaboration.SubmitAsync(new CollaborationTaskRequest(
                "composition-cancel-submit", "composition-source", "composition-cancel-target",
                "Hold six helpers until source cancellation.", [], []), sourceProfile, CancellationToken.None);
            var cancellationTarget = workflows.Store.Get(cancellationTask.TargetRunId!);
            var cancellationWave = await InvokeDelegateAsync(workflows, effects, cancellationTarget,
                Assignments(6, state, useAcceptedRevisions: false));
            Check(cancellationWave.Result.State == WorkflowEffectState.Succeeded,
                "The cancellation fixture first admits a complete six-helper wave.");
            var cancellationChildren = workflows.Store.ChildRunIds(cancellationTarget.Id).Select(workflows.Store.Get).ToArray();
            workflows.Store.CompleteEffect(cancellationTarget.Id, cancellationWave.Context.EffectId, cancellationWave.Result);
            var cancellationClaim = workflows.Store.TryClaim(cancellationTarget.Id)!;
            workflows.Store.ApplyStep(cancellationClaim.Id, cancellationClaim.Revision,
                new(WorkflowContract.EmptyObject,
                    new WaitForWorkflowRuns(cancellationChildren.Select(child => child.Id).ToArray())));
            var cancelled = (CollaborationTask)await collaboration.ExecuteAsync("collaboration.cancel",
                CerebralJson.Element(new CollaborationCancelRequest(cancellationTask.Id, cancellationTask.Revision)),
                sourceProfile, CancellationToken.None);
            Check(cancelled.Status == CollaborationTaskStatus.Cancelled && cancelled.CancelRequested &&
                workflows.Store.Get(cancellationTarget.Id) is { Status: WorkflowStatus.Cancelled, CancelRequested: true } &&
                cancellationChildren.All(child => workflows.Store.Get(child.Id) is { Status: WorkflowStatus.Cancelled, CancelRequested: true }),
                "Existing Collaboration cancellation atomically cancels the top-level target and all queued direct helpers.");

            Check(model.Preparations == 0 && tools.Executions == 0 &&
                calls.Any(operation => operation == "models.inspect") &&
                calls.All(operation => operation is "health" or "worker.management.status" or "models.inspect"),
                "The fixture verifies remote metadata only; no model load, inference or semantic tool operation occurs.");
            Console.WriteLine($"Collaboration/First Mate composition checks passed: {checks} " +
                "(actual default engine, Collaboration/coordination services and SQLite; loopback remote metadata, no inference)." );
            return checks;
        }
        finally
        {
            await remote.StopAsync();
            SqliteConnection.ClearAllPools();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(root) != temp ||
                !Path.GetFileName(root).StartsWith("cerebral-collaboration-composition-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe composition fixture cleanup target.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentProfile Profile(string id, string role, string modelId, string modelWorkerId,
        IReadOnlyList<string> capabilities) => new()
    {
        Id = id,
        Revision = 1,
        Name = id,
        Purpose = "Deterministic hierarchy composition fixture",
        Instructions = role == "lead" ? "Coordinate only the bounded admitted assignments." : "Return only bounded assignment evidence.",
        Role = role,
        ModelId = modelId,
        WorkerId = "local",
        ModelWorkerId = modelWorkerId,
        FollowsDefaultModel = false,
        FollowsModelBudgets = false,
        AutomaticCompaction = false,
        ContextTokens = 8192,
        ResponseTokens = 1024,
        MaxSteps = 32,
        MaxSeconds = 60,
        AllowedRoots = [],
        AllowedTools = [],
        Capabilities = capabilities.ToList()
    };

    private static ModelRecord RemoteModel() => new()
    {
        Id = RemoteModelId,
        Name = "Remote composition metadata",
        Path = "/srv/cerebral/models/composition.gguf",
        State = "loaded",
        AutomaticContextSizing = false,
        ContextTokens = 8192,
        MaximumContextTokens = 8192,
        RecommendedContextTokens = 8192,
        ParallelRequests = 6,
        GpuLayers = -1
    };

    private static void ConfigureMaximumChildren(string root, string slot, int maximumChildren)
    {
        var directory = Path.Combine(root, slot);
        var settingsPath = Path.Combine(directory, "engine-settings.ini");
        File.WriteAllText(settingsPath, "[settings]\nmaximum-children=" + maximumChildren + "\n", new UTF8Encoding(false));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settingsPath))).ToLowerInvariant();
        var manifestPath = Path.Combine(directory, "engine.ini");
        var lines = File.ReadAllLines(manifestPath);
        var index = Array.FindIndex(lines, line => line.StartsWith("configuration-sha256=", StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException("Fixture engine manifest lacks its configuration hash.");
        lines[index] = "configuration-sha256=" + hash;
        File.WriteAllLines(manifestPath, lines, new UTF8Encoding(false));
    }

    private static JsonElement Assignments(int count, StateStore state, bool useAcceptedRevisions = true) =>
        CerebralJson.Element(new
        {
            assignments = Enumerable.Range(1, count).Select(index =>
            {
                var id = "composition-helper-" + index;
                var revision = state.Get<AgentProfile>("agent", id)!.Revision;
                return new
                {
                    profileId = id,
                    profileRevision = useAcceptedRevisions ? 1 : revision,
                    instruction = "Return only HELPER_" + index + "_EVIDENCE from the isolated specialist context."
                };
            }).ToArray()
        });

    private static JsonElement RepeatedAssignments(int count, string profileId, StateStore state)
    {
        var revision = state.Get<AgentProfile>("agent", profileId)!.Revision;
        return CerebralJson.Element(new
        {
            assignments = Enumerable.Range(1, count).Select(index => new
            {
                profileId,
                profileRevision = revision,
                instruction = "Return only COPY_" + index + "_EVIDENCE from isolated specialist instance " + index + "."
            }).ToArray()
        });
    }

    private static async Task<Invocation> InvokeDelegateAsync(WorkerWorkflows workflows,
        WorkerWorkflowEffects effects, WorkflowRun parent, JsonElement arguments)
    {
        var handler = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.DelegateCommand);
        WorkflowSchema.Validate(arguments, handler.Descriptor.InputSchema);
        var claimed = workflows.Store.TryClaim(parent.Id) ?? throw new InvalidOperationException("Target run was not claimable.");
        var prepared = workflows.Store.ApplyStep(parent.Id, claimed.Revision,
            new(WorkflowContract.EmptyObject,
                new InvokeWorkflowCommand(handler.Descriptor.Id, handler.Descriptor.Version, arguments)), handler.Descriptor);
        var dispatched = workflows.Store.DispatchEffect(parent.Id, prepared.Revision);
        var context = new WorkflowCommandContext(dispatched.Effect!.Id, dispatched.Submission, arguments) { RunId = parent.Id };
        return new(handler, context, await handler.ExecuteAsync(context, CancellationToken.None));
    }

    private static async Task<WorkflowCommandResult> InvokeDelegateAgainstChildAsync(WorkerWorkflowEffects effects,
        WorkflowRun child, JsonElement arguments)
    {
        var handler = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.DelegateCommand);
        return await handler.ExecuteAsync(new WorkflowCommandContext("nested-child-effect", child.Submission, arguments)
            { RunId = child.Id }, CancellationToken.None);
    }

    private static void SeedConversation(WorkflowStore store, string conversationId, string profileId)
    {
        var run = store.Submit(new WorkflowSubmission("seed-" + conversationId, conversationId, profileId,
            ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
            "Seed same-project conversation binding.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject,
            Caller: new EventActor(EventActorKind.User, "owner")) { ProjectId = ProjectId });
        var claimed = store.TryClaim(run.Id) ?? throw new InvalidOperationException("Fixture seed was not claimable.");
        store.ApplyStep(claimed.Id, claimed.Revision,
            new(WorkflowContract.EmptyObject, new CompleteWorkflow(CerebralJson.Element(new { summary = "Seed complete." }))));
    }

    private sealed record Invocation(IWorkflowCommandHandler Handler, WorkflowCommandContext Context,
        WorkflowCommandResult Result);

    private sealed class NeverModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("composition-fixture", "1.0.0", ["text"]);
        public int Preparations;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            Preparations++;
            throw new InvalidOperationException("No model preparation is permitted in this fixture.");
        }
    }

    private sealed class NoTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public int Executions;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Executions++;
            throw new InvalidOperationException("No semantic tool execution is permitted in this fixture.");
        }
    }

    private sealed class NoLegacyExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("No legacy agent execution is permitted in this fixture.");
    }
}
