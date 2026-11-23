using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Worker;
using Cerebral.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class NativeCortexContinuityTests
{
    internal const string Space = "project.native-continuity-fixture";
    private const string RecordKind = "native-cortex-continuity";

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-native-continuity-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            checks++; Console.WriteLine("PASS: " + description);
        }
        try
        {
            var off = Fixture.Create(root, "off", enabled: false);
            var old = off.Submit(); off.Manager.Observe(old); off.Manager.Recover();
            await DrainAsync(off.Manager);
            Check(off.Manager.Get(old.Submission.ConversationId) is null && off.Tools.Calls.Count == 0,
                "opt-out and older null-binding snapshots create no native continuity record or provider call");
            off.SetConsent(true); off.Manager.Observe(old); await DrainAsync(off.Manager);
            Check(off.Tools.Calls.Count == 0 && off.Manager.Get(old.Submission.ConversationId) is null,
                "enabling continuity after admission cannot add writes to an already accepted native turn");

            foreach (var missing in new[] { "mcp.cortex", "cortex.write", "cortex_start", "cortex_task", "cortex_finish" })
            {
                var denied = Fixture.Create(root, "denied-" + missing.Replace('.', '-'));
                var run = denied.Submit();
                denied.Profile.Capabilities.Remove(missing); denied.Profile.AllowedTools.Remove("mcp.cortex." + missing);
                denied.Store.Put("agent", denied.Profile.Id, denied.Profile); denied.Manager.Observe(run);
                await denied.Manager.ProcessNextAsync(default);
                Check(denied.Tools.Calls.Count == 0 && denied.Manager.Get(run.Submission.ConversationId)?.Status == "unavailable" &&
                    denied.Workflows.Get(run.Id).Status == WorkflowStatus.Queued,
                    "revoked " + missing + " grant prevents provider I/O and preserves independent native work");
            }

            var created = Fixture.Create(root, "created");
            var text = string.Concat(Enumerable.Repeat("λ😀 bounded objective ", 400));
            var admitted = created.Submit(userText: text);
            created.Manager.Observe(admitted); created.Manager.Observe(admitted);
            Check(created.Manager.Get(admitted.Submission.ConversationId)?.Pending.Count == 1,
                "duplicate committed notifications coalesce one durable started milestone");
            await created.Manager.ProcessNextAsync(default);
            var create = created.Tools.Calls.Last();
            Check(created.Tools.AttemptingObserved && created.Manager.Get(admitted.Submission.ConversationId) is
                    { TaskRevision: 1, PendingOperation: null, LastOperation.Status: "confirmed" },
                "task creation saves the exact intent and attempting boundary in SQLite before mutation and retains its confirmed receipt");
            Check(Text(create, "taskId") == admitted.Submission.ConversationId && create.Context.SessionId == admitted.Submission.ConversationId &&
                Text(created.Tools.Calls[0], "phaseId") == admitted.Id &&
                created.Tools.Calls[0].Call.Arguments.GetProperty("namespaces").EnumerateArray().Select(x => x.GetString()).SequenceEqual([Space, "knowledge.shared"]),
                "native task identity is ConversationId with a source RunId phase and exact write-first namespace order");
            Check(Bytes(Text(create, "title")) <= 180 && Bytes(Text(create, "content")) <= 4096 &&
                Bytes(Text(created.Tools.Calls[0], "intent")) <= 1024 && Bytes(Text(create, "idempotencyKey")) <= 128,
                "creation/title/intent/key remain bounded in UTF-8 without splitting supplementary characters");
            Check(created.Tools.Calls.All(call => call.Context.EnforceProfileScope && !call.Context.FullAccess &&
                    call.Context.Cortex is { KnowledgeWriteApproved: false, ProjectId: "project" }),
                "automatic task continuity uses frozen/current project grants without Full Access or knowledge approval");
            var completed = created.Complete(admitted, new { answer = "MODEL_RESULT_SENTINEL", privateProof = false });
            created.Manager.Observe(completed); created.Manager.Observe(completed); await DrainAsync(created.Manager);
            var finish = created.Tools.Calls.Last();
            Check(Text(finish, "taskAction") == "update" && Text(finish, "phaseId") == completed.Id &&
                finish.Call.Arguments.GetProperty("expectedRevision").GetInt32() == 1 &&
                created.Manager.Get(completed.Submission.ConversationId)?.TaskRevision == 2,
                "a successful native turn updates its ongoing conversation task rather than completing it");
            Check(finish.Call.Arguments.GetProperty("knowledge").GetArrayLength() == 0 &&
                !finish.Call.Arguments.TryGetProperty("blockedReason", out _) && Bytes(Text(finish, "evidence")) <= 2000 &&
                Text(finish, "taskContent").Contains("Model-reported result (verify against service evidence)", StringComparison.Ordinal),
                "task-only finish exports a bounded labeled result and host evidence with no knowledge batch or blocking reason");
            var sent = string.Join("", created.Tools.Calls.Select(x => x.Call.Arguments.GetRawText()));
            Check(!sent.Contains("PRIVATE_ASSIGNMENT_SENTINEL", StringComparison.Ordinal) &&
                !sent.Contains("PRIVATE_STATE_SENTINEL", StringComparison.Ordinal) &&
                !sent.Contains("PRIVATE_ATTACHMENT_SENTINEL", StringComparison.Ordinal) &&
                created.Journal.GetRevision(completed.Submission.ConversationId) > 0,
                "continuity leaves full assignment/state/attachments and canonical conversation events local");
            var previousCalls = created.Tools.Calls.Count;
            created.RestartManager(); created.Manager.Recover(); await DrainAsync(created.Manager);
            Check(created.Tools.Calls.Count == previousCalls &&
                created.Manager.Get(completed.Submission.ConversationId)?.LastOperation?.PayloadHash is { Length: 64 },
                "SQLite recovery deduplicates a confirmed terminal milestone without a provider replay");

            var waiting = Fixture.Create(root, "waiting");
            var questionRun = waiting.Submit(); waiting.Manager.Observe(questionRun); await DrainAsync(waiting.Manager);
            questionRun = waiting.Workflows.TryClaim(questionRun.Id)!;
            questionRun = waiting.Workflows.ApplyStep(questionRun.Id, questionRun.Revision,
                new(Element(new { }), new AskWorkflowQuestions([new("answer", "Required owner choice")])));
            waiting.Manager.Observe(questionRun); waiting.Manager.Observe(questionRun); await DrainAsync(waiting.Manager);
            Check(Text(waiting.Tools.Calls.Last(), "taskAction") == "update" &&
                Text(waiting.Tools.Calls.Last(), "checkpointSubject") == "Cerebral waiting-user" &&
                waiting.Tools.Tasks[questionRun.Submission.ConversationId].Status == "open",
                "waiting for a question records one milestone while the ongoing Cortex task stays open");

            var hierarchy = Fixture.Create(root, "hierarchy");
            var parent = hierarchy.Submit("parent", "parent-conversation");
            var child = hierarchy.Submit("child", "child-conversation", parent.Id);
            hierarchy.Manager.Observe(parent); hierarchy.Manager.Observe(child); await DrainAsync(hierarchy.Manager);
            parent = hierarchy.Workflows.TryClaim(parent.Id)!;
            parent = hierarchy.Workflows.ApplyStep(parent.Id, parent.Revision,
                new(Element(new { }), new WaitForWorkflowRuns([child.Id])));
            hierarchy.Manager.Observe(parent); await DrainAsync(hierarchy.Manager);
            Check(hierarchy.Manager.Get(child.Submission.ConversationId)?.LastOperation?.ParentRunId == parent.Id &&
                hierarchy.Tools.Calls.Where(IsMutation).All(call => Text(call, "taskId") == call.Context.SessionId) &&
                !hierarchy.Tools.Calls.Any(call => call.Call.Arguments.TryGetProperty("action", out var action) &&
                    action.GetString() is "dependencyAdd" or "dependencyRemove") &&
                Text(hierarchy.Tools.Calls.Last(), "checkpointSubject") == "Cerebral waiting-children",
                "root/helper tasks keep local parent lineage and child waits without misusing Cortex dependency IDs");

            child = hierarchy.Complete(child); hierarchy.Manager.Observe(child);
            foreach (var collected in hierarchy.Workflows.ResumeCompletedDependencies()) hierarchy.Manager.Observe(collected);
            await DrainAsync(hierarchy.Manager);
            Check(hierarchy.Tools.Calls.Any(call => call.Call.Name.EndsWith("cortex_finish", StringComparison.Ordinal) &&
                    Text(call, "checkpointSubject") == "Cerebral children-collected"),
                "confirmed native child collection creates a host milestone without tool-by-tool checkpoint spam");

            // CortexV2McpApplication.Success removes null compact optionals, including task.
            // Mirror its actual projection rather than requiring the artificial task:null fixture.
            var omitted = Fixture.Create(root, "omitted-start-task"); var omittedRun = omitted.Submit();
            var compactAbsent = new
            {
                schema = "cortex.start.compact/1", ns = new[] { Space, "knowledge.shared" },
                mode = "snapshot", cursor = "fixture-context-cursor", items = Array.Empty<object>(), more = false
            };
            omitted.Tools.OnCall = (call, _) => Task.FromResult<ToolResult?>(
                call.Call.Name.EndsWith("cortex_start", StringComparison.Ordinal) ? Reply(compactAbsent) : null);
            omitted.Manager.Observe(omittedRun); await omitted.Manager.ProcessNextAsync(default);
            Check(!Element(compactAbsent).TryGetProperty("task", out _) &&
                omitted.Tools.Calls.Count == 2 && omitted.Tools.Calls.Count(IsMutation) == 1 &&
                Text(omitted.Tools.Calls.Last(), "action") == "create" &&
                omitted.Manager.Get(omittedRun.Submission.ConversationId) is
                    { TaskRevision: 1, TaskReady: true, PendingOperation: null, LastOperation.Status: "confirmed" },
                "the provider-produced compact snapshot with omitted optional task creates once and confirms the exact receipt");

            var explicitNull = Fixture.Create(root, "explicit-null-start-task"); var explicitNullRun = explicitNull.Submit();
            explicitNull.Tools.OnCall = (call, _) => Task.FromResult<ToolResult?>(
                call.Call.Name.EndsWith("cortex_start", StringComparison.Ordinal) ? Reply(new
                {
                    schema = "cortex.start.compact/1", ns = call.Call.Arguments.GetProperty("namespaces"),
                    mode = "snapshot", task = (object?)null
                }) : null);
            explicitNull.Manager.Observe(explicitNullRun); await explicitNull.Manager.ProcessNextAsync(default);
            Check(explicitNull.Tools.Calls.Count(IsMutation) == 1 &&
                explicitNull.Manager.Get(explicitNullRun.Submission.ConversationId)?.LastOperation?.Status == "confirmed",
                "explicit null task remains a supported compact snapshot absence");

            foreach (var (field, value, index) in new (string Field, string? Value)[]
            {
                ("schema", null), ("schema", "\"cortex.start.compact/2\""), ("schema", "[]"),
                ("mode", null), ("mode", "\"unknown\""), ("mode", "{}"),
                ("ns", null), ("ns", "[\"knowledge.shared\",\"" + Space + "\"]"), ("ns", "[true]"),
                ("task", "\"\""), ("task", "false"), ("task", "1"), ("task", "[]"), ("task", "{}"),
                ("task", JsonSerializer.Serialize(new { ns = "different.namespace", id = "conversation", rev = 7, status = "open" })),
                ("task", JsonSerializer.Serialize(new { ns = Space, id = "different-task", rev = 7, status = "open" })),
                ("task", JsonSerializer.Serialize(new { ns = Space, id = "conversation", rev = 0, status = "open" })),
                ("task", JsonSerializer.Serialize(new { ns = Space, id = "conversation", rev = 7, status = "unknown" }))
            }.Select((shape, index) => (shape.Field, shape.Value, Index: index)))
            {
                var invalidStart = Fixture.Create(root, "invalid-start-" + index); var invalidRun = invalidStart.Submit();
                var payload = JsonSerializer.SerializeToNode(compactAbsent)!.AsObject();
                if (value is null) payload.Remove(field); else payload[field] = JsonNode.Parse(value);
                invalidStart.Tools.OnCall = (call, _) => Task.FromResult<ToolResult?>(
                    call.Call.Name.EndsWith("cortex_start", StringComparison.Ordinal) ? Reply(payload) : null);
                invalidStart.Manager.Observe(invalidRun); await invalidStart.Manager.ProcessNextAsync(default);
                Check(invalidStart.Tools.Calls.Count == 1 && invalidStart.Tools.Calls.Count(IsMutation) == 0 &&
                    invalidStart.Manager.Get(invalidRun.Submission.ConversationId) is
                        { Status: "unavailable", TaskReady: false, PendingOperation: null, Pending.Count: 1 },
                    "malformed compact start " + field + "/" + index + " fails before mutation and retains the undispatched milestone");
            }

            foreach (var lifecycle in new[] { "open", "assigned", "claimed", "completed", "blocked" })
            {
                var resumed = Fixture.Create(root, "compact-" + lifecycle); var run = resumed.Submit();
                resumed.Tools.Tasks[run.Submission.ConversationId] = new(7, lifecycle);
                resumed.Manager.Observe(run); await DrainAsync(resumed.Manager);
                var writes = resumed.Tools.Calls.Where(IsMutation).ToArray();
                Check(resumed.Manager.Get(run.Submission.ConversationId)?.TaskRevision == (lifecycle is "completed" or "blocked" ? 9 : 8) &&
                    !writes.Any(call => call.Call.Arguments.TryGetProperty("action", out var action) && action.GetString() == "create") &&
                    writes.All(call => call.Call.Arguments.GetProperty("expectedRevision").GetInt32() >= 7),
                    "installed compact ns/id/rev/status start recovers an existing " + lifecycle + " task with exact revision writes");
            }
            var unchanged = Fixture.Create(root, "unchanged-start"); var unchangedRun = unchanged.Submit();
            unchanged.Tools.StartMode = "unchanged"; unchanged.Tools.Tasks[unchangedRun.Submission.ConversationId] = new(7, "open");
            unchanged.Manager.Observe(unchangedRun); await DrainAsync(unchanged.Manager);
            Check(unchanged.Tools.Calls.Count == 3 && Text(unchanged.Tools.Calls[1], "action") == "get" &&
                unchanged.Manager.Get(unchangedRun.Submission.ConversationId)?.TaskRevision == 8,
                "unchanged compact start requires an exact task get rather than inferring task absence");
            var unchangedWrong = Fixture.Create(root, "unchanged-wrong-exact-task"); var unchangedWrongRun = unchangedWrong.Submit();
            unchangedWrong.Tools.StartMode = "unchanged";
            unchangedWrong.Tools.OnCall = (call, _) => Task.FromResult<ToolResult?>(
                call.Call.Arguments.TryGetProperty("action", out var action) && action.GetString() == "get" ? Reply(new
                {
                    effectiveNamespaces = new[] { Space },
                    task = new { @namespace = Space, taskId = "different-task", revision = 7, status = "open" }
                }) : null);
            unchangedWrong.Manager.Observe(unchangedWrongRun); await unchangedWrong.Manager.ProcessNextAsync(default);
            Check(unchangedWrong.Tools.Calls.Count == 2 && unchangedWrong.Tools.Calls.Count(IsMutation) == 0 &&
                unchangedWrong.Manager.Get(unchangedWrongRun.Submission.ConversationId) is
                    { Status: "unavailable", TaskReady: false, PendingOperation: null },
                "unchanged mode with omitted task cannot create when its exact lookup returns the wrong identity");

            var outage = Fixture.Create(root, "safe-read-outage"); var outageRun = outage.Submit(userText: "ORIGINAL_SAFE_RETRY_OBJECTIVE");
            outage.Manager.Observe(outageRun);
            outage.Tools.OnCall = (_, _) => Task.FromResult<ToolResult?>(new(false, "fixture safe read-only outage"));
            await outage.Manager.ProcessNextAsync(default);
            var originalMilestone = outage.Manager.Get(outageRun.Submission.ConversationId)!.Pending.Single();
            var outageCalls = outage.Tools.Calls.Count; await outage.Manager.ProcessNextAsync(default);
            Check(outage.Manager.Get(outageRun.Submission.ConversationId) is { Status: "unavailable", Pending.Count: 1, PendingOperation: null } &&
                    outage.Tools.Calls.Count == outageCalls,
                "a safe read-only failure retains the original bounded milestone without repeated calls in one host lifetime");
            outage.Tools.OnCall = null; outage.RestartManager(); outage.Manager.Recover(); await DrainAsync(outage.Manager);
            Check(outage.Manager.Get(outageRun.Submission.ConversationId)?.TaskRevision == 1 &&
                Text(outage.Tools.Calls.Last(), "content").Contains(originalMilestone.Objective, StringComparison.Ordinal) &&
                outage.Tools.Calls.Count(IsMutation) == 1,
                "a new host safely recovers a read-only outage with the original start objective and one new create intent");

            foreach (var bad in new[] { "namespace", "task", "revision", "truncated", "disposition" })
            {
                var invalid = Fixture.Create(root, "receipt-" + bad); var run = invalid.Submit();
                invalid.Tools.OnCall = (call, _) => Task.FromResult<ToolResult?>(IsMutation(call) ? BadReceipt(call, bad) : null);
                invalid.Manager.Observe(run); await invalid.Manager.ProcessNextAsync(default);
                Check(invalid.Manager.Get(run.Submission.ConversationId) is { Status: "uncertain", PendingOperation.Status: "uncertain" } &&
                    invalid.Workflows.Get(run.Id).Status == WorkflowStatus.Queued,
                    "an invalid " + bad + " mutation receipt remains uncertain without corrupting the native run");
            }
            var idempotent = Fixture.Create(root, "idempotent");
            idempotent.Tools.MutationDisposition = "idempotent"; var idem = idempotent.Submit();
            idempotent.Manager.Observe(idem); await DrainAsync(idempotent.Manager);
            Check(idempotent.Manager.Get(idem.Submission.ConversationId)?.TaskRevision == 1,
                "a complete exact idempotent receipt confirms its expected revision");

            var lost = Fixture.Create(root, "lost"); var lostRun = lost.Submit();
            lost.Tools.CommitThenThrow = true; lost.Manager.Observe(lostRun); await lost.Manager.ProcessNextAsync(default);
            var unconfirmed = lost.Manager.Get(lostRun.Submission.ConversationId)!;
            var frozen = unconfirmed.PendingOperation!.Arguments.GetRawText();
            var key = unconfirmed.PendingOperation.Id;
            Check(unconfirmed.Status == "uncertain" && lost.Tools.Tasks[lostRun.Submission.ConversationId].Revision == 1,
                "a lost reply retains the exact dispatched payload even when the deterministic provider committed it");
            lost.Tools.CommitThenThrow = false;
            lostRun = lost.Complete(lostRun, new { result = "current matching fields are insufficient" });
            lost.Manager.Observe(lostRun);
            var next = lost.Submit("next", lostRun.Submission.ConversationId); lost.Manager.Observe(next);
            lost.RestartManager(); lost.Manager.Recover(); await DrainAsync(lost.Manager);
            var reads = lost.Tools.Calls.Where(x => x.Call.Arguments.TryGetProperty("action", out var action) &&
                action.GetString() == "history").ToArray();
            Check(lost.Tools.Calls.Count(IsMutation) == 1 && lost.Manager.Get(next.Submission.ConversationId) is { Status: "uncertain" } &&
                lost.Manager.Get(next.Submission.ConversationId)!.PendingOperation!.Arguments.GetRawText() == frozen &&
                lost.Manager.Get(next.Submission.ConversationId)!.PendingOperation!.Id == key,
                "later turns and host restart cannot abandon or replay an unconfirmed operation");
            Check(reads.Length >= 1 && reads.All(call => call.Call.Arguments.GetProperty("firstRevision").GetInt32() == 1 &&
                    call.Call.Arguments.GetProperty("lastRevision").GetInt32() == 1 &&
                    call.Call.Arguments.GetProperty("limit").GetInt32() == 1 &&
                    call.Call.Arguments.GetProperty("maxUtf8Bytes").GetInt32() == 4096) &&
                lost.Manager.Get(next.Submission.ConversationId)!.Message.Contains("cannot prove", StringComparison.Ordinal),
                "bounded exact task/history reads retain uncertainty because history has no exact operation/payload identity");
            var inspections = lost.Tools.Calls.Count; await lost.Manager.ProcessNextAsync(default);
            Check(lost.Tools.Calls.Count == inspections, "an unresolved write does not cause repeated inspection or retry polling in one host lifetime");

            var interrupted = Fixture.Create(root, "interrupted"); var interruptRun = interrupted.Submit();
            interrupted.Manager.Observe(interruptRun);
            interrupted.Tools.OnCall = (call, _) =>
            {
                if (IsMutation(call))
                {
                    var state = interrupted.Manager.Get(interruptRun.Submission.ConversationId)!;
                    state.PendingOperation!.Status = "attempting";
                    interrupted.Store.Put(RecordKind, state.ConversationId, state);
                    throw new IOException("fixture host interruption after dispatch");
                }
                return Task.FromResult<ToolResult?>(null);
            };
            await interrupted.Manager.ProcessNextAsync(default);
            var recovering = interrupted.Manager.Get(interruptRun.Submission.ConversationId)!;
            recovering.Status = "attempting"; recovering.PendingOperation!.Status = "attempting";
            interrupted.Store.Put(RecordKind, recovering.ConversationId, recovering); interrupted.Tools.OnCall = null;
            interrupted.RestartManager(); interrupted.Manager.Recover(); await DrainAsync(interrupted.Manager);
            Check(interrupted.Tools.Calls.Count(IsMutation) == 1 &&
                interrupted.Manager.Get(interruptRun.Submission.ConversationId)?.Status == "uncertain",
                "a persisted attempting boundary recovers as uncertain and never re-executes the mutation");

            var cancelled = Fixture.Create(root, "cancelled"); var cancelRun = cancelled.Submit();
            cancelled.Manager.Observe(cancelRun);
            using (var token = new CancellationTokenSource())
            {
                token.Cancel();
                await ExpectCancellationAsync(() => cancelled.Manager.ProcessNextAsync(token.Token));
            }
            Check(cancelled.Tools.Calls.Count == 0 && cancelled.Manager.Get(cancelRun.Submission.ConversationId)?.PendingOperation is null,
                "cancellation before maintenance dispatch causes no provider write");
            using (var token = new CancellationTokenSource())
            {
                cancelled.Tools.OnCall = (call, _) =>
                {
                    if (IsMutation(call)) { token.Cancel(); throw new OperationCanceledException(token.Token); }
                    return Task.FromResult<ToolResult?>(null);
                };
                await ExpectCancellationAsync(() => cancelled.Manager.ProcessNextAsync(token.Token));
            }
            Check(cancelled.Manager.Get(cancelRun.Submission.ConversationId) is { Status: "uncertain", PendingOperation.Status: "uncertain" } &&
                cancelled.Tools.Calls.Count(IsMutation) == 1,
                "cancellation after the saved dispatch boundary retains an unconfirmed write");

            foreach (var change in new[] { "consent", "provider", "read", "write", "profile" })
            {
                var revoked = Fixture.Create(root, "binding-" + change); var run = revoked.Submit();
                revoked.Manager.Observe(run);
                revoked.Tools.OnCall = (call, _) =>
                {
                    if (call.Call.Name.EndsWith("cortex_start", StringComparison.Ordinal)) revoked.Revoke(change);
                    return Task.FromResult<ToolResult?>(null);
                };
                await revoked.Manager.ProcessNextAsync(default);
                Check(revoked.Tools.Calls.Count == 1 && revoked.Tools.Calls.Count(IsMutation) == 0 &&
                    revoked.Manager.Get(run.Submission.ConversationId)?.Status == "unavailable",
                    "a " + change + " binding race during start prevents any mutation");
            }
            var late = Fixture.Create(root, "confirmed-old-binding"); var lateRun = late.Submit();
            late.Tools.OnCall = (call, _) =>
            {
                if (IsMutation(call)) late.SetConsent(false);
                return Task.FromResult<ToolResult?>(null);
            };
            late.Manager.Observe(lateRun); await late.Manager.ProcessNextAsync(default);
            Check(late.Manager.Get(lateRun.Submission.ConversationId) is
                { Status: "checkpointed-old-binding", TaskRevision: 1, LastOperation.Status: "confirmed", Pending.Count: 0 },
                "a late confirmed write records its exact old target while revoked consent prevents later checkpoints");

            var durability = Fixture.Create(root, "sqlite-boundary"); var durableRun = durability.Submit();
            durability.Manager.Observe(durableRun); durability.FailAttemptingPersistence(true);
            await durability.Manager.ProcessNextAsync(default);
            Check(durability.Tools.Calls.Count(IsMutation) == 0 && durability.Workflows.Get(durableRun.Id).Status == WorkflowStatus.Queued,
                "a real SQLite failure saving the attempting boundary prevents external mutation");
            var prepared = durability.Manager.Get(durableRun.Submission.ConversationId)!.PendingOperation!;
            var preparedBytes = prepared.Arguments.GetRawText();
            Check(prepared.Status == "prepared" && durability.Manager.Get(durableRun.Submission.ConversationId)!.Pending.Count == 1,
                "SQLite dispatch-boundary failure retains its known-undispatched immutable prepared intent");
            durability.FailAttemptingPersistence(false); durability.RestartManager(); durability.Manager.Recover();
            await DrainAsync(durability.Manager);
            Check(durability.Manager.Get(durableRun.Submission.ConversationId)?.TaskRevision == 1 &&
                durability.Manager.Get(durableRun.Submission.ConversationId)!.LastOperation!.Arguments.GetRawText() == preparedBytes &&
                durability.Tools.Calls.Count(IsMutation) == 1 && durability.Tools.Calls.Count == 2,
                "restart dispatches the exact previously prepared bytes once without a duplicate start/read or payload rebuild");

            var recovery = Fixture.Create(root, "paged-recovery");
            for (var i = 0; i < 45; i++) recovery.Complete(recovery.Submit("run-" + i, "conversation-" + i));
            recovery.Manager.Recover();
            await DrainAsync(recovery.Manager, maximum: 150);
            Check(recovery.Tools.Tasks.Count == 45 && recovery.Manager.Get("conversation-0")?.TaskRevision == 2,
                "bounded startup discovery passes the viewport page and recovers terminal conversations from actual SQLite runs");

            await CheckModelOwnershipAsync(root, Check);
            await CheckOwnerInspectionAsync(root, Check);
            Console.WriteLine($"Native Cortex continuity: {checks} checks passed with deterministic tool replies and temporary SQLite state; no installed Cortex provider/model was invoked.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside its temporary root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static async Task CheckModelOwnershipAsync(string root, Action<bool, string> check)
    {
        var fixture = Fixture.Create(root, "model-ownership");
        var run = fixture.Submit();
        var models = new NeverModels();
        var bridge = new WorkerWorkflowEffects(fixture.Store, fixture.Configuration, models, fixture.Tools);
        WorkflowCommandContext Context(string effect, object args, WorkflowSubmission? submission = null) =>
            new(effect, submission ?? run.Submission, Element(args)) { RunId = run.Id };
        var catalog = await bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CatalogCommand && x.Descriptor.Version == 1)
            .ExecuteAsync(Context("catalog", new WorkerWorkflowEffects.CatalogArguments()), default);
        var catalogData = catalog.Data!.Value.Deserialize<WorkerWorkflowEffects.CatalogData>(CerebralJson.Options)!;
        check(catalog.State == WorkflowEffectState.Succeeded && catalogData.Tools.All(x =>
                x.Name is not ("mcp.cortex.cortex_start" or "mcp.cortex.cortex_task" or "mcp.cortex.cortex_finish")),
            "the opted-in model catalog does not expose host-owned start/task/finish lifecycle tools");
        foreach (var name in new[] { "cortex_start", "cortex_task", "cortex_finish" })
        {
            var tool = "mcp.cortex." + name; var calls = fixture.Tools.Calls.Count;
            var direct = await bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.ToolCommand)
                .ExecuteAsync(Context("direct-" + name, new WorkerWorkflowEffects.ToolArguments(tool, Element(new { }))), default);
            var generation = await bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1)
                .ExecuteAsync(Context("generate-" + name, new WorkerWorkflowEffects.GenerateArguments([], ToolNames: [tool])), default);
            check(direct.State == WorkflowEffectState.Failed && generation.State == WorkflowEffectState.Failed &&
                fixture.Tools.Calls.Count == calls && models.Preparations == 0,
                "direct/explicit model catalog routes cannot claim host-owned " + name);
            fixture.Tools.GenerationSink = tool;
            var sink = await bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.ToolCommand)
                .ExecuteAsync(Context("sink-" + name, new WorkerWorkflowEffects.ToolArguments("fixture.plan", Element(new { }))), default);
            var data = sink.Data!.Value.Deserialize<WorkerWorkflowEffects.ToolData>(CerebralJson.Options)!;
            check(sink.State == WorkflowEffectState.Failed && data.Generation is null && !data.FollowUpRequired &&
                fixture.Tools.Calls.Count == calls + 1 && models.Preparations == 0,
                "a successful provider plan cannot acquire a generated-result " + name + " sink");
        }
        fixture.Tools.GenerationSink = null;
        fixture.Manager.Observe(run); await DrainAsync(fixture.Manager);
        check(fixture.Manager.Get(run.Submission.ConversationId)?.TaskRevision == 1,
            "the explicit host coordinator still dispatches lifecycle tools through the normal granted dispatcher");
        var old = fixture.Submit("old", "old-conversation", settingsSnapshot: Element(fixture.Snapshot(withConsent: false)));
        var legacyDirect = await bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.ToolCommand)
            .ExecuteAsync(new("legacy-direct", old.Submission,
                Element(new WorkerWorkflowEffects.ToolArguments("mcp.cortex.cortex_start", Element(new
                { taskId = old.Submission.ConversationId, namespaces = new[] { Space, "knowledge.shared" } })))) { RunId = old.Id }, default);
        check(legacyDirect.State == WorkflowEffectState.Succeeded,
            "a legacy null-binding native snapshot retains its separately granted explicit lifecycle route");
    }

    private static async Task CheckOwnerInspectionAsync(string root, Action<bool, string> check)
    {
        var fixture = Fixture.Create(root, "owner-inspection"); var run = fixture.Submit();
        fixture.Manager.Observe(run); await DrainAsync(fixture.Manager);
        var events = new EventHub();
        await using var models = new ModelRuntime(fixture.Store, fixture.Configuration.Get, events);
        await using var concreteTools = new ToolDispatcher(fixture.Configuration.Get);
        var host = new WorkerWorkflows(fixture.Store, fixture.Configuration, events, [], [], fixture.Tools, journal: fixture.Journal);
        using var scheduler = new JobScheduler(fixture.Store, fixture.Configuration, new NeverAgentExecutor(), models, events,
            NullLogger<JobScheduler>.Instance, host);
        var commands = new CommandDispatcher(fixture.Store, fixture.Configuration, models, scheduler, concreteTools, events,
            recordedTools: fixture.Tools, workflows: host);
        var calls = fixture.Tools.Calls.Count;
        var owner = await commands.ExecuteAsync(new("cortex.native-continuity.get", Element(new { conversationId = run.Submission.ConversationId })));
        check(owner.Success && CerebralJson.Read<NativeCortexContinuity.State>(owner.Data).TaskRevision == 1 &&
            fixture.Tools.Calls.Count == calls, "owner native continuity inspection returns exact saved state without provider I/O");
        var savedBefore = Element(fixture.Manager.Get(run.Submission.ConversationId)!).GetRawText();
        var inspected = await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
            Element(new { conversationId = run.Submission.ConversationId })));
        check(inspected.Success && inspected.Data.GetProperty("providerTaskRevision").GetInt32() == 1 &&
            inspected.Data.GetProperty("sourceRunId").GetString() == run.Id &&
            !inspected.Data.GetProperty("writeReconciled").GetBoolean() && fixture.Tools.Calls.Count == calls + 1 &&
            !IsMutation(fixture.Tools.Calls[^1]) &&
            Text(fixture.Tools.Calls[^1], "taskId") == run.Submission.ConversationId &&
            Text(fixture.Tools.Calls[^1], "namespace") == Space,
            "owner native task inspection performs one exact scoped read without mutation or reconciliation");
        check(Element(fixture.Manager.Get(run.Submission.ConversationId)!).GetRawText() == savedBefore,
            "provider inspection leaves all saved continuity state unchanged");
        calls = fixture.Tools.Calls.Count;
        fixture.Store.Put("session", "legacy-inspection", new TaskSession
            { Id = "legacy-inspection", ProfileId = fixture.Profile.Id, Title = "Legacy read fixture" });
        var deniedAgentRead = await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
            Element(new { conversationId = run.Submission.ConversationId }), fixture.Profile.Id));
        check(!deniedAgentRead.Success && fixture.Tools.Calls.Count == calls,
            "an ordinary agent cannot invoke native provider inspection");
        using (var duplicateArguments = JsonDocument.Parse("{\"conversationId\":\"a\",\"conversationId\":\"b\"}"))
            check(!(await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
                duplicateArguments.RootElement))).Success && fixture.Tools.Calls.Count == calls,
                "duplicate native inspection coordinates are rejected before provider I/O");
        foreach (var id in new[] { "missing-native-binding", "legacy-inspection" })
            check(!(await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
                Element(new { conversationId = id })))).Success && fixture.Tools.Calls.Count == calls,
                "missing or legacy task inspection is rejected before provider I/O: " + id);
        check(!(await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
            Element(new { conversationId = run.Submission.ConversationId, taskId = "another" })))).Success &&
            fixture.Tools.Calls.Count == calls, "caller-supplied task coordinates cannot replace the native binding");
        foreach (var bad in new[] { "namespace", "task", "revision", "truncated", "failed", "oversized" })
        {
            fixture.Tools.OnCall = (call, _) => Task.FromResult<ToolResult?>(bad switch
            {
                "failed" => new(false, "fixture provider failure"),
                "oversized" => new(true, new string('x', 20 * 1024)),
                _ => Reply(new { effectiveNamespaces = new[] { bad == "namespace" ? "other" : Space },
                    task = new { @namespace = Space, taskId = bad == "task" ? "other" : run.Submission.ConversationId,
                        revision = bad == "revision" ? 0 : 1, status = "open" } }) with { Truncated = bad == "truncated" }
            });
            var rejected = await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
                Element(new { conversationId = run.Submission.ConversationId })));
            check(!rejected.Success && Element(fixture.Manager.Get(run.Submission.ConversationId)!).GetRawText() == savedBefore,
                "invalid provider inspection preserves native pending/write state: " + bad);
        }
        fixture.Tools.OnCall = (call, _) =>
        {
            fixture.Profile.Capabilities.Remove("cortex.write"); fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile);
            return Task.FromResult<ToolResult?>(Reply(new { effectiveNamespaces = new[] { Space },
                task = new { @namespace = Space, taskId = run.Submission.ConversationId, revision = 1, status = "open" } }));
        };
        check(!(await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
            Element(new { conversationId = run.Submission.ConversationId })))).Success &&
            Element(fixture.Manager.Get(run.Submission.ConversationId)!).GetRawText() == savedBefore,
            "revocation during provider I/O rejects a stale inspection without changing saved write state");
        fixture.Tools.OnCall = null;
        calls = fixture.Tools.Calls.Count;
        check(!(await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
            Element(new { conversationId = run.Submission.ConversationId })))).Success && fixture.Tools.Calls.Count == calls,
            "current grant revocation rejects native inspection before provider dispatch");
        fixture.Profile.Capabilities.Add("cortex.write"); fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            check(!(await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
                Element(new { conversationId = run.Submission.ConversationId })), cancelled.Token)).Success &&
                fixture.Tools.Calls.Count == calls, "cancelled native inspection makes no provider call");
        }
        var unresolved = fixture.Manager.Get(run.Submission.ConversationId)!;
        unresolved.Status = "uncertain";
        unresolved.PendingOperation = CerebralJson.Read<NativeCortexContinuity.Operation>(Element(unresolved.LastOperation!));
        unresolved.PendingOperation.Id = "fixture-unconfirmed"; unresolved.PendingOperation.Status = "uncertain";
        fixture.Store.Put("native-cortex-continuity", run.Submission.ConversationId, unresolved);
        var unresolvedBefore = Element(unresolved).GetRawText();
        var observational = await commands.ExecuteAsync(new("cortex.native-continuity.inspect",
            Element(new { conversationId = run.Submission.ConversationId })));
        check(observational.Success && !observational.Data.GetProperty("writeReconciled").GetBoolean() &&
            Element(fixture.Manager.Get(run.Submission.ConversationId)!).GetRawText() == unresolvedBefore &&
            !IsMutation(fixture.Tools.Calls[^1]), "a successful read cannot settle or replay an uncertain saved write");
        calls = fixture.Tools.Calls.Count;
        var actor = await commands.ExecuteAsync(new("cortex.native-continuity.get",
            Element(new { conversationId = run.Submission.ConversationId }), fixture.Profile.Id));
        fixture.Profile.Capabilities.Add("administration"); fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile);
        var administrator = await commands.ExecuteAsync(new("cortex.native-continuity.get",
            Element(new { conversationId = run.Submission.ConversationId }), fixture.Profile.Id));
        check(!actor.Success && !administrator.Success && fixture.Tools.Calls.Count == calls,
            "agent and administration profiles cannot invoke owner-only native continuity inspection");
        {
            const string operation = "cortex.native-continuity.inspect";
            var denied = await commands.ExecuteAsync(new(operation,
                Element(new { conversationId = run.Submission.ConversationId }), fixture.Profile.Id));
            check(!denied.Success && fixture.Tools.Calls.Count == calls,
                "administration profiles cannot invoke the native provider inspection owner route");
        }
        fixture.Store.Put("session", "legacy-inspection", new TaskSession
            { Id = "legacy-inspection", ProfileId = fixture.Profile.Id, Title = "Legacy read fixture" });
        fixture.Store.Put("cortex-continuity", "legacy-inspection", new CortexContinuityState
            { SessionId = "legacy-inspection", TaskId = "legacy-inspection", Status = "legacy-checkpointed", Revision = 4 });
        var legacy = await commands.ExecuteAsync(new("cortex.continuity.get", Element(new { sessionId = "legacy-inspection" })));
        check(legacy.Success && CerebralJson.Read<CortexContinuityState>(legacy.Data) is
                { SessionId: "legacy-inspection", Status: "legacy-checkpointed", Revision: 4 },
            "the existing legacy continuity command retains its original state shape and session coordinate");
    }

    private sealed class NeverAgentExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Unexpected fixture agent execution.");
    }

    internal static async Task DrainAsync(NativeCortexContinuity manager, int maximum = 80)
    {
        for (var count = 0; count < maximum; count++)
            if (!await manager.ProcessNextAsync(default)) return;
        throw new InvalidOperationException("Fixture exceeded its bounded native continuity drain.");
    }
    private static async Task ExpectCancellationAsync(Func<Task<bool>> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation.");
    }
    internal static JsonElement Element(object value) => CerebralJson.Element(value);
    internal static string Text(RecordedCall call, string name) => call.Call.Arguments.GetProperty(name).GetString() ?? "";
    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);
    internal static bool IsMutation(RecordedCall call) => call.Call.Name.StartsWith("mcp.cortex.", StringComparison.Ordinal) &&
        CortexOperationPolicy.IsMutation(call.Call.Name, call.Call.Arguments);
    internal static ToolResult Reply(object payload) => new(true, JsonSerializer.Serialize(new { structuredContent = payload }));
    private static ToolResult BadReceipt(RecordedCall call, string bad)
    {
        var expected = call.Call.Arguments.TryGetProperty("expectedRevision", out var previous) ? previous.GetInt32() : 0;
        return Reply(new { effectiveNamespaces = new[] { bad == "namespace" ? "different.namespace" : Space },
            disposition = bad == "disposition" ? "rejected" : "appended",
            task = new { @namespace = Space, taskId = bad == "task" ? "different-task" : Text(call, "taskId"),
                revision = bad == "revision" ? expected + 3 : expected + 1, status = "open" },
            knowledge = Array.Empty<object>() }) with { Truncated = bad == "truncated" };
    }

    internal sealed record RecordedCall(ToolCall Call, ToolExecutionContext Context);
    internal sealed record ProviderTask(int Revision, string Status);

    internal sealed class Fixture
    {
        internal StateStore Store { get; private set; } = null!;
        internal Configuration Configuration { get; private set; } = null!;
        internal ProjectKnowledge Projects { get; private set; } = null!;
        internal AgentProfile Profile { get; private set; } = null!;
        internal Transport Tools { get; private set; } = null!;
        internal EventJournal Journal { get; private set; } = null!;
        internal WorkflowStore Workflows { get; private set; } = null!;
        internal NativeCortexContinuity Manager { get; private set; } = null!;
        internal string ProjectId => "project";
        private ModelRecord model = null!;
        internal static Fixture Create(string root, string name, bool enabled = true)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var result = new Fixture { Store = new(Path.Combine(directory, "state")) };
            var events = new EventHub(); result.Configuration = new(result.Store);
            var host = result.Configuration.Get();
            host.CortexNamespaces = ["knowledge.shared", Space]; host.CortexWriteNamespaces = [Space];
            host.McpServers = [new() { Id = "cortex", Executable = Environment.ProcessPath!, Enabled = true, ViaUserConnector = false }];
            result.Configuration.Save(host);
            result.Store.Put("navigation", "sidebar", new NavigationState
                { Nodes = [new() { Id = "project", Kind = "project", Title = "Native continuity fixture", Path = directory }] });
            result.Projects = new(result.Store, result.Configuration, events);
            result.Projects.Save(new() { ProjectId = "project", CortexServerId = "cortex", ReadNamespaces = [Space, "knowledge.shared"],
                WriteNamespace = Space, AutomaticContinuity = enabled });
            result.Profile = new() { Id = "lead", Name = "Native continuity fixture", Role = "lead", ModelId = "fixture",
                AllowedRoots = [directory], AllowedTools = ["mcp.cortex.cortex_start", "mcp.cortex.cortex_task", "mcp.cortex.cortex_finish", "fixture.plan"],
                Capabilities = ["mcp.cortex", "cortex.write", "fixture"], FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 512, ReasoningMode = "off" };
            result.Store.Put("agent", result.Profile.Id, result.Profile);
            result.model = new() { Id = "fixture", Name = "Fixture metadata only", Path = Path.Combine(directory, "fixture.gguf"),
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192 };
            result.Store.Put("model", result.model.Id, result.model);
            result.Journal = new(Path.Combine(result.Store.DirectoryPath, "engine.db"));
            result.Workflows = new(result.Journal); result.Tools = new();
            result.Manager = new(result.Store, result.Configuration, result.Tools, events, result.Workflows, result.Journal);
            result.Tools.Attempting = id => result.Manager.Get(id)?.PendingOperation?.Status == "attempting";
            return result;
        }
        internal WorkerExecutionSnapshot.Snapshot Snapshot(bool withConsent = true)
        {
            var settings = Projects.Get(ProjectId); var scope = Projects.ScopeFor(ProjectId);
            var profile = CerebralJson.Read<AgentProfile>(Element(Profile));
            return new(WorkerExecutionSnapshot.Version, profile, "", "", WorkerExecutionSnapshot.Bind(model),
                Tools.Descriptors, profile.Capabilities.ToArray(), profile.AllowedTools.ToArray(), profile.AllowedRoots.ToArray(), false,
                WorkerExecutionSnapshot.RoutingFingerprint(Configuration.Get()), [], [], CortexScope: scope,
                NativeContinuity: withConsent && settings.AutomaticContinuity ? new(settings.Revision) : null);
        }
        internal WorkflowRun Submit(string requestId = "run", string conversationId = "conversation", string? parentRunId = null,
            JsonElement? settingsSnapshot = null, string userText = "Verify bounded native continuity.")
        {
            var submission = new WorkflowSubmission(requestId, conversationId, Profile.Id, "fixture:continuity", "1.0.0",
                userText, "PRIVATE_ASSIGNMENT_SENTINEL", Element(new { privateState = "PRIVATE_STATE_SENTINEL" }),
                settingsSnapshot ?? Element(Snapshot()),
                Caller: parentRunId is null ? null : new(EventActorKind.Service, "cerebral.coordination"))
            { ProjectId = ProjectId, ParentRunId = parentRunId };
            return Workflows.Submit(submission);
        }
        internal WorkflowRun Complete(WorkflowRun run, object? result = null)
        {
            if (run.Status == WorkflowStatus.Queued) run = Workflows.TryClaim(run.Id)!;
            return Workflows.ApplyStep(run.Id, run.Revision,
                new(Element(new { state = "PRIVATE_STATE_SENTINEL" }), new CompleteWorkflow(Element(result ?? new { answer = "fixture" }))));
        }
        internal void SetConsent(bool enabled)
        {
            var settings = Projects.Get(ProjectId); settings.AutomaticContinuity = enabled; Projects.Save(settings);
        }
        internal void RestartManager()
        {
            Store = new(Store.DirectoryPath); Configuration = new(Store); Projects = new(Store, Configuration, new());
            Manager = new(Store, Configuration, Tools, new(), Workflows, Journal);
        }
        internal void Revoke(string change)
        {
            if (change == "consent") { SetConsent(false); return; }
            if (change == "profile")
            {
                Profile.Capabilities.Remove("cortex.write"); Store.Put("agent", Profile.Id, Profile); return;
            }
            var host = Configuration.Get();
            if (change == "provider") host.McpServers[0].Arguments.Add("--changed-fixture");
            else if (change == "read") host.CortexNamespaces = ["knowledge.shared"];
            else host.CortexWriteNamespaces = [];
            Configuration.Save(host);
        }
        internal void FailAttemptingPersistence(bool enabled)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Store.DirectoryPath, "cerebral.db") }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = enabled ? """
                CREATE TRIGGER fixture_native_attempt_failure BEFORE UPDATE ON records
                WHEN NEW.kind='native-cortex-continuity' AND json_extract(NEW.body,'$.status')='attempting'
                BEGIN SELECT RAISE(ABORT,'fixture attempting persistence failure'); END;
                """ : "DROP TRIGGER fixture_native_attempt_failure;";
            command.ExecuteNonQuery();
        }
    }

    internal sealed class Transport : IToolDispatcher
    {
        internal List<RecordedCall> Calls { get; } = [];
        internal Dictionary<string, ProviderTask> Tasks { get; } = new(StringComparer.Ordinal);
        internal Func<RecordedCall, CancellationToken, Task<ToolResult?>>? OnCall { get; set; }
        internal Func<string, bool>? Attempting { get; set; }
        internal bool AttemptingObserved { get; private set; }
        internal bool CommitThenThrow { get; set; }
        internal string MutationDisposition { get; set; } = "appended";
        internal string StartMode { get; set; } = "snapshot";
        internal string? GenerationSink { get; set; }
        internal IReadOnlyList<ToolDescriptor> Descriptors { get; } =
            new[] { "cortex_start", "cortex_task", "cortex_finish" }.Select(name =>
                new ToolDescriptor("mcp.cortex." + name, "Deterministic native continuity fixture.", Element(new { type = "object" }), "mcp.cortex"))
            .Append(new("fixture.plan", "Fixture generated plan.", Element(new { type = "object" }), "fixture")).ToArray();
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); return Task.FromResult(Descriptors);
        }
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var recorded = new RecordedCall(call, context); Calls.Add(recorded);
            if (IsMutation(recorded)) AttemptingObserved |= Attempting?.Invoke(context.SessionId) == true;
            if (OnCall is not null && await OnCall(recorded, token) is { } intercepted) return intercepted;
            if (call.Name == "fixture.plan")
                return new(true, "fixture successful plan", Generation: GenerationSink is null ? null :
                    new("Produce bounded fixture text.", GenerationSink, Element(new { taskContent = "" }), "taskContent", MaxTokens: 256));
            var id = Text(recorded, "taskId"); Tasks.TryGetValue(id, out var task);
            if (call.Name.EndsWith("cortex_start", StringComparison.Ordinal))
            {
                if (task is null || StartMode == "unchanged")
                    return Reply(new
                    {
                        schema = "cortex.start.compact/1", ns = call.Arguments.GetProperty("namespaces"),
                        mode = StartMode, cursor = "fixture-context-cursor", items = Array.Empty<object>(), more = false
                    });
                return Reply(new
                {
                    schema = "cortex.start.compact/1", ns = call.Arguments.GetProperty("namespaces"),
                    mode = StartMode, cursor = "fixture-context-cursor",
                    task = new { ns = Space, id, rev = task.Revision, status = task.Status },
                    items = Array.Empty<object>(), more = false
                });
            }
            if (TextOrNull(recorded, "action") == "get")
                return Reply(new { effectiveNamespaces = new[] { Space },
                    task = new { @namespace = Space, taskId = id, revision = task?.Revision ?? 1, status = task?.Status ?? "open" } });
            if (TextOrNull(recorded, "action") == "history")
                return Reply(new { effectiveNamespaces = new[] { Space }, disposition = "history",
                    history = new[] { new { @namespace = Space, taskId = id,
                        revision = call.Arguments.GetProperty("firstRevision").GetInt32(), status = "open",
                        @event = "updated", actor = "fixture", contentChanged = true, contentHash = "matching-fields-do-not-prove-payload",
                        phaseId = context.JobId, checkpointSubject = "Cerebral started" } } });
            var prior = call.Arguments.TryGetProperty("expectedRevision", out var previous) ? previous.GetInt32() : 0;
            Tasks[id] = new(prior + 1, "open");
            if (CommitThenThrow) throw new IOException("fixture committed write lost its reply");
            return Reply(new { effectiveNamespaces = new[] { Space }, disposition = MutationDisposition,
                task = new { @namespace = Space, taskId = id, revision = prior + 1, status = "open" }, knowledge = Array.Empty<object>() });
        }
        private static string? TextOrNull(RecordedCall call, string name) =>
            call.Call.Arguments.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private sealed class NeverModels : IModelRuntimeAdapter
    {
        internal int Preparations;
        public ModelAdapterDescriptor Descriptor { get; } = new("native-continuity-no-model", "1.0.0", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            Preparations++; throw new InvalidOperationException("This fixture must not prepare a model.");
        }
    }
}
