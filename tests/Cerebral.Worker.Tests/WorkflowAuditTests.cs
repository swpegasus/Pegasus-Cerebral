using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic advisor fixtures and SQLite delivery gates; no live auditor or Laya inference.</summary>
internal static class WorkflowAuditTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-audit-gates-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        try
        {
            var journal = new EventJournal(Path.Combine(root, "engine.db")); var store = new WorkflowStore(journal);
            var registry = new WorkflowRegistry([new Module()]);
            WorkflowRun Submit(string id) => store.Submit(new(id, id + "-context", "fixture", "fixture.audit:complete", "1.0.0",
                "Complete the test", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject));
            async Task<WorkflowRun> Run(string id, Fixture? adapter, bool required = false, int timeout = 1500)
            {
                Submit(id);
                var runtime = new WorkflowRuntime(store, registry, auditPipeline: new(adapter is null ? [] : [adapter]),
                    auditPolicy: _ => new(adapter?.Descriptor.Id, required, timeout));
                return await runtime.RunAsync(id);
            }
            string[] Audits(string id) => journal.ReadPage(id + "-context", limit: 100).Events
                .Where(item => item.Type == "workflow.audit").Select(item => item.Data!.Value.GetRawText()).ToArray();
            Check((await Run("optional-missing", null)).Status == WorkflowStatus.Completed, "Optional missing auditor allows deterministic completion.");
            Check(Audits("optional-missing").Single().Contains("unavailable", StringComparison.Ordinal), "Missing audit is visible and not fabricated.");
            Check((await Run("required-missing", null, true)).Status == WorkflowStatus.Waiting, "Required missing audit holds the checkpoint.");
            var accepted = new Fixture();
            Check((await Run("accepted", accepted)).Status == WorkflowStatus.Completed && accepted.Calls == 1, "Admitted bound advisory can be delivered once.");
            Check(Audits("accepted").Single().Contains("accepted", StringComparison.Ordinal), "Accepted assessment retains its attributed receipt.");
            var badHash = new Fixture { Transform = result => result with { InputSha256 = new string('b', 64) } };
            await Run("wrong-hash", badHash);
            Check(Audits("wrong-hash").Single().Contains("rejected", StringComparison.Ordinal), "Mismatched input digest is rejected.");
            var authority = new Fixture { Transform = result => result with { DirectActionAllowed = true } };
            Check((await Run("authority", authority, true)).Status == WorkflowStatus.Waiting, "Advisory cannot grant action authority.");
            var gap = new Fixture { Transform = result => result with { Disposition = "gap" } };
            Check((await Run("gap", gap, true)).Status == WorkflowStatus.Waiting, "Required audit gap retains an actionable hold.");
            var steering = new Fixture { OnReview = request =>
            {
                if (request.RunId == "steered" && store.Get(request.RunId).Steering.Count == 0)
                    store.Steer(new("steering-request", request.RunId, "New fixture direction"));
            } };
            await Run("steered", steering);
            Check(steering.Calls == 2 && Audits("steered").Length == 1, "Steering during inference discards stale assessment before fresh retry.");
            var generation = new Fixture { ReplaceOnDescriptorRead = 5 };
            await Run("generation", generation);
            Check(Audits("generation").All(receipt => !receipt.Contains("\"disposition\":\"accepted\"", StringComparison.Ordinal)),
                "Capability replacement at delivery cannot commit old assessment.");
            var slow = new Fixture { DelayMilliseconds = 30 };
            await Run("slow", slow, timeout: 5);
            Check(Audits("slow").Single().Contains("timed_out", StringComparison.Ordinal), "Noncooperative late result is a timeout, not an accepted assessment.");
            Check(slow.Returned, "Host awaits noncooperative provider exit instead of freeing a hidden inference slot.");

            var journalOnly = new Fixture();
            var appendedJournalOnly = false;
            journalOnly.OnReview = request =>
            {
                if (journalOnly.Calls != 1) return;
                var before = store.Get(request.RunId);
                var appended = journal.Append(new("audit-journal-only-event", request.ConversationId, request.RunId, 0,
                    "fixture.audit.evidence", new(EventActorKind.Service, "fixture.audit"),
                    [new TextContentPart("New bounded evidence without a workflow state transition.")],
                    new(ModelInclusion.Evidence, HumanVisibility.Activity), new(EventSourceKind.Service, "fixture.audit"),
                    DateTimeOffset.UtcNow), request.ConversationRevision);
                appendedJournalOnly = appended.Event.Sequence == request.ConversationRevision + 1 &&
                    store.Get(request.RunId).Revision == before.Revision && before.Revision == request.RunRevision;
            };
            var journalOnlyResult = await Run("journal-only", journalOnly);
            Check(appendedJournalOnly, "Fixture advances only the conversation journal while the audited run revision remains unchanged.");
            Check(journalOnlyResult.Status == WorkflowStatus.Completed && journalOnly.Requests.Count == 2 &&
                journalOnly.Requests[0].RunRevision == journalOnly.Requests[1].RunRevision &&
                journalOnly.Requests[1].ConversationRevision == journalOnly.Requests[0].ConversationRevision + 1,
                "A journal-only conflict discards the old assessment and retries with a fresh conversation revision.");
            var journalOnlyReceipt = Audits("journal-only").Single();
            Check(!journalOnlyReceipt.Contains(journalOnly.Requests[0].RequestId, StringComparison.Ordinal) &&
                journalOnlyReceipt.Contains(journalOnly.Requests[1].RequestId, StringComparison.Ordinal) &&
                journalOnlyReceipt.Contains("\"disposition\":\"accepted\"", StringComparison.Ordinal),
                "Only the fresh journal-bound assessment is committed; the first result has no accepted receipt.");

            var auditEntered = new TaskCompletionSource<WorkflowAuditRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAudit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = false;
            var noncooperative = new Fixture { OnReviewAsync = async (request, token) =>
            {
                auditEntered.TrySetResult(request);
                await releaseAudit.Task; // The fixture deliberately ignores cancellation until it returns.
                cancellationObserved = token.IsCancellationRequested;
            } };
            Submit("cancel-audit");
            var cancelledRuntime = new WorkflowRuntime(store, registry, auditPipeline: new([noncooperative]),
                auditPolicy: _ => new(noncooperative.Descriptor.Id, false, 30000));
            using var ownerCancellation = new CancellationTokenSource();
            var cancelledExecution = cancelledRuntime.RunAsync("cancel-audit", ownerCancellation.Token);
            try
            {
                await auditEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                store.Cancel("cancel-audit");
                ownerCancellation.Cancel();
                Check(!cancelledExecution.IsCompleted && !noncooperative.Returned,
                    "Cancellation does not release the runtime while a noncooperative auditor still owns its execution.");
                releaseAudit.TrySetResult();
                var cancelledResult = await cancelledExecution.WaitAsync(TimeSpan.FromSeconds(5));
                Check(noncooperative.Returned && cancellationObserved && noncooperative.Calls == 1,
                    "The fixture returns its late accepted result after observing the cancelled audit token.");
                Check(cancelledResult.Status == WorkflowStatus.Cancelled && cancelledResult.CancelRequested && cancelledResult.Result is null,
                    "The late advisory cannot overwrite the owner's terminal cancellation or publish a completion result.");
                Check(Audits("cancel-audit").Length == 0,
                    "No late accepted assessment is appended after owner cancellation.");
            }
            finally
            {
                releaseAudit.TrySetResult();
                ownerCancellation.Cancel();
                await cancelledExecution.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Console.WriteLine($"Workflow audit checks passed: {checks} (deterministic advisors and SQLite; no Laya/model inference).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-audit-gates-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(root, true);
        }
    }
    private sealed class Module : IWorkflowModule
    {
        public string Id => "fixture.audit";
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
    }
    private sealed class Definition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("fixture.audit:complete", "1.0.0", "Fixture completion",
            CerebralJson.Element(new { type = "object" }), []) { SupportsSteering = true };
        public WorkflowStep Advance(WorkflowRun run) => new(WorkflowContract.EmptyObject,
            new CompleteWorkflow(CerebralJson.Element(new { text = "Fixture completed" })));
    }
    private sealed class Fixture : IWorkflowAuditAdapter
    {
        private int reads;
        public int Calls, DelayMilliseconds, ReplaceOnDescriptorRead;
        public bool Returned;
        public Action<WorkflowAuditRequest>? OnReview;
        public Func<WorkflowAuditRequest, CancellationToken, Task>? OnReviewAsync;
        public Func<WorkflowAuditResult, WorkflowAuditResult>? Transform;
        public List<WorkflowAuditRequest> Requests { get; } = [];
        public WorkflowAuditDescriptor Descriptor
        {
            get
            {
                var replaced = ReplaceOnDescriptorRead > 0 && ++reads >= ReplaceOnDescriptorRead;
                return new("fixture.audit:advisor", "fixture.audit", "1.0.0", replaced ? 2 : 1,
                    new string(replaced ? 'b' : 'a', 64), ["component_evidence_check", "coordinator_decision_check"]);
            }
        }
        public async Task<WorkflowAuditResult> ReviewAsync(WorkflowAuditRequest request, CancellationToken token)
        {
            Calls++; Requests.Add(request); OnReview?.Invoke(request);
            if (OnReviewAsync is { } onReview) await onReview(request, token);
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds); // Deliberately ignores cancellation.
            Returned = true;
            var result = new WorkflowAuditResult(request.RequestId, "fixture.audit:advisor", 1, new string('a', 64),
                request.InputSha256, request.ProposalSha256, "accepted", "Deterministic fixture assessment.");
            return Transform?.Invoke(result) ?? result;
        }
    }
}
