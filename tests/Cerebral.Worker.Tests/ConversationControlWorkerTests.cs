using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ConversationControlWorkerTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-chat-controls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var checks = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        try
        {
            var store = new StateStore(root); var configuration = new Configuration(store); var events = new EventHub();
            var profile = new AgentProfile { Id = "controls", Name = "Control fixture", Capabilities = ["administration"] };
            store.Put("agent", profile.Id, profile);
            await using var models = new ModelRuntime(store, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get);
            var effects = new WorkerWorkflowEffects(store, configuration, models, tools);
            var workflows = new WorkerWorkflows(store, configuration, events, [new ConversationWorkflowModule(), effects],
                [ConversationWorkflowModule.PackageBinding, new(effects.Id, WorkerWorkflowEffects.ModuleVersion, true, [])]);
            using var scheduler = new JobScheduler(store, configuration, new NoModelExecutor(), models, events,
                NullLogger<JobScheduler>.Instance, workflows);
            var dispatcher = new CommandDispatcher(store, configuration, models, scheduler, tools, events, workflows: workflows);
            async Task<CommandResult> Command(string operation, object input, string? actor = null) =>
                await dispatcher.ExecuteAsync(new(operation, CerebralJson.Element(input), actor));
            var request = new SubmitWorkflowRequest("controls-run", "controls-chat", profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Original request", null, WorkflowContract.EmptyObject);
            var submitted = await Command("jobs.workflow.submit", request);
            Check(submitted.Success, "owner accepts ordinary conversation: " + submitted.Error);
            Check(workflows.Registry.GetDefinition(request.DefinitionId, request.DefinitionVersion).Descriptor.SupportsSteering,
                "harness capability survives workflow adaptation");
            var preparedRevision = workflows.Store.Get(request.RequestId).Revision;
            var preparedReject = new WorkflowPreparedRejectRequest("control-reject", request.RequestId,
                preparedRevision, "unowned-effect", new string('0', 64), "Owner review", "Choose a new action.");
            var preparedPermit = new WorkflowPreparedPermitRequest("control-permit", request.RequestId,
                preparedRevision, "unowned-effect", new string('0', 64), "Owner review");
            Check(!(await Command("jobs.workflow.prepared.reject", preparedReject, profile.Id)).Success &&
                !(await Command("jobs.workflow.prepared.permit-once", preparedPermit, profile.Id)).Success &&
                workflows.Store.Get(request.RequestId).Revision == preparedRevision,
                "administrative agent cannot invoke either owner-only prepared-effect control or mutate the run");
            var steer = new WorkflowSteeringRequest("control-steer", request.RequestId, "New owner direction", "Hidden plugin context");
            Check(!(await Command("jobs.workflow.steer", steer, profile.Id)).Success, "administrative agent cannot impersonate owner steering");
            var accepted = await Command("jobs.workflow.steer", steer);
            Check(accepted.Success, "shared owner command saves steering: " + accepted.Error);
            Check(CerebralJson.Read<WorkflowSteeringReceipt>(accepted.Data).Sequence == 1, "receipt identifies first durable steering");
            Check((await Command("jobs.workflow.steer", steer)).Success &&
                workflows.Store.Get(request.RequestId).Steering.Count == 1, "same command retry is idempotent");
            Check(!(await Command("jobs.workflow.steer", steer with { Text = "Different" })).Success, "reused request cannot change direction");
            Check(!(await Command("jobs.workflow.steer", new { steer.RequestId, steer.RunId, steer.Text, caller = "owner" })).Success,
                "transport cannot inject caller fields");
            var rename = new ConversationRenameRequest("control-rename", request.DefinitionId, request.ConversationId, 0, "Friendly chat name");
            Check(!(await Command("conversations.rename", rename, profile.Id)).Success, "administrative agent cannot rename owner navigation");
            Check((await Command("conversations.rename", rename)).Success, "owner title save succeeds");
            Check((await Command("conversations.rename", rename)).Success, "lost rename receipt retries");
            var summary = CerebralJson.Read<ConversationSummary>((await Command("conversations.get", new { id = request.ConversationId })).Data);
            Check(summary.Title == rename.Title && summary.TitleRevision == 1, "title revision reaches compact navigation summary");
            Check(!(await Command("conversations.rename", rename with { RequestId = "stale-rename" })).Success, "stale revision rejects");
            Check(!(await Command("conversations.rename", rename with { RequestId = "foreign", DefinitionId = "other:workflow" })).Success,
                "rename cannot target another workflow surface");
            Check(workflows.Store.Get(request.RequestId).Submission.UserText == request.UserText &&
                workflows.Store.Get(request.RequestId).Status == WorkflowStatus.Queued, "controls do not rewrite original input or bypass scheduler");
            var claimed = workflows.Store.TryClaim(request.RequestId) ?? throw new InvalidOperationException("Fixture run was not claimable.");
            var completed = workflows.Store.ApplyStep(request.RequestId, claimed.Revision,
                new(claimed.State, new CompleteWorkflow(WorkflowContract.EmptyObject)));
            Check(completed.Status == WorkflowStatus.Completed, "fixture completes before owner compaction admission");
            var compact = new ConversationCompactionRequest("control-compact", request.DefinitionId, request.ConversationId,
                workflows.Journal.GetRevision(request.ConversationId));
            Check(!(await Command("conversations.compact", compact, profile.Id)).Success,
                "administrative agent cannot impersonate owner compaction");
            var compacted = await Command("conversations.compact", compact);
            Check(compacted.Success, "shared owner command saves one-time compaction: " + compacted.Error);
            Check(CerebralJson.Read<ConversationCompactionReceipt>(compacted.Data).RequestId == compact.RequestId,
                "compaction command returns its stable receipt");
            Check((await Command("conversations.compact", compact)).Success &&
                workflows.Store.GetPendingConversationCompaction(request.ConversationId)?.RequestId == compact.RequestId,
                "same compaction command retry is idempotent");
            var next = request with { RequestId = "controls-next", UserText = "Continue with the real owner request." };
            var resumed = await Command("jobs.workflow.submit", next);
            Check(resumed.Success && CerebralJson.Read<WorkflowRun>(resumed.Data).Submission.ConversationCompactionRequestId == compact.RequestId &&
                workflows.Store.GetPendingConversationCompaction(request.ConversationId) is null,
                "next real Chat submission atomically consumes the saved compaction request");
            foreach (var operation in new[] { "jobs.workflow.steer", "jobs.workflow.prepared.reject",
                "jobs.workflow.prepared.permit-once", "conversations.rename", "conversations.compact" })
            {
                Check((await Command("sdk.describe", new { id = operation })).Success, "owner discovers selected control contract");
                Check(!(await Command("sdk.describe", new { id = operation }, profile.Id)).Success, "owner control schema is absent from agent discovery");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"Conversation controls: {checks} shared-dispatcher and local SQLite checks passed; no model inference.");
    }

    private sealed class NoModelExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Unexpected model execution.");
    }
}
