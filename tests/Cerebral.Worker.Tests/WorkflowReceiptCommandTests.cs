using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class WorkflowReceiptCommandTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-receipt-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        Exception? testFailure = null;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; }
        try
        {
            var state = new StateStore(Path.Combine(root, "data"));
            var config = new Configuration(state); var events = new EventHub();
            var lead = new AgentProfile { Id = "lead", Name = "Lead" };
            var other = new AgentProfile { Id = "other", Name = "Other", Capabilities = ["administration"] };
            state.Put("agent", lead.Id, lead); state.Put("agent", other.Id, other);
            var host = new WorkerWorkflows(state, config, events, [], []);
            await using var models = new ModelRuntime(state, config.Get, events);
            await using var tools = new ToolDispatcher(config.Get);
            using var scheduler = new JobScheduler(state, config, new NoModelExecutor(), models, events,
                NullLogger<JobScheduler>.Instance, host);
            var dispatcher = new CommandDispatcher(state, config, models, scheduler, tools, events, workflows: host);
            Task<CommandResult> Read(object input, string? actor = "lead") =>
                dispatcher.ExecuteAsync(new("jobs.workflow.receipt", CerebralJson.Element(input), actor));
            var content = string.Concat(Enumerable.Repeat("é中 ", 2000)) + "🦄 end";
            var error = new string('x', 255) + "🦄 later detail";
            var first = Receipt(host, "one", lead.Id,
                new(WorkflowEffectState.Failed, CerebralJson.Element(new { stdout = content, exitCode = 1 }), error, "provider-one"));
            var second = Receipt(host, "two", lead.Id,
                new(WorkflowEffectState.Succeeded, CerebralJson.Element(new { text = "second" })));
            var id = first.Id; var effectId = first.Effect!.Id;
            var response = await Read(new { runId = id, effectId });
            Check(response.Success, "dispatcher accepts owner-profile receipt coordinates");
            var initial = CerebralJson.Read<WorkflowReceiptPage>(response.Data);
            Check(initial is { Limit: 4096, Offset: 0, Truncated: true, State: WorkflowEffectState.Failed } &&
                initial.NextOffset > 0 && Encoding.UTF8.GetByteCount(initial.Json) <= 4096 && initial.TotalBytes > 4096 &&
                initial.ProviderReceiptId == "provider-one", "default receipt page is bounded and exposes outcome metadata");
            Check(initial.ErrorPreview?.Length == 255 && !char.IsSurrogate(initial.ErrorPreview[^1]),
                "compact error preview does not split a Unicode surrogate pair");
            Check(!(await Read(new { runId = id, effectId }, other.Id)).Success &&
                (await Read(new { runId = id, effectId }, null)).Success,
                "administration capability cannot read another profile's run; authenticated owner can");
            var foreign = await Read(new { runId = second.Id, effectId });
            var absent = await Read(new { runId = second.Id, effectId = "missing" });
            Check(!foreign.Success && !absent.Success && foreign.Error == absent.Error,
                "same profile cannot splice another run's effect ID or probe its existence");
            Check(!(await Read(new { runId = id })).Success &&
                !(await Read(new { effectId })).Success &&
                !(await Read(new { runId = id, effectId, caller = "owner" })).Success,
                "required coordinates and strict input fields are enforced");
            Check(!(await Read(new { runId = id, effectId, limit = 16385 })).Success &&
                !(await Read(new { runId = id, effectId, limit = 3 })).Success &&
                !(await Read(new { runId = id, effectId, offset = -1 })).Success &&
                !(await Read(new { runId = id, effectId, offset = initial.TotalBytes + 1 })).Success,
                "page limits and invalid offsets are rejected rather than silently expanded");
            var maximum = await Read(new { runId = id, effectId, limit = 16384 });
            Check(maximum.Success && Encoding.UTF8.GetByteCount(CerebralJson.Read<WorkflowReceiptPage>(maximum.Data).Json) <= 16384,
                "maximum page respects the 16 KiB fragment limit");
            var assembled = new StringBuilder(); var offset = 0; var pages = 0;
            while (true)
            {
                var pageResponse = await Read(new { runId = id, effectId, offset, limit = 257 });
                if (!pageResponse.Success) throw new Exception(pageResponse.Error);
                var page = CerebralJson.Read<WorkflowReceiptPage>(pageResponse.Data);
                if (page.Offset != offset || Encoding.UTF8.GetByteCount(page.Json) > 257 || page.Json.Contains('\ufffd'))
                    throw new Exception("Receipt page broke the UTF-8 byte contract.");
                assembled.Append(page.Json); pages++;
                if (page.NextOffset is not { } next) { Check(!page.Truncated, "last receipt page explicitly finishes"); break; }
                if (next <= offset || next - offset != Encoding.UTF8.GetByteCount(page.Json))
                    throw new Exception("Receipt cursor did not advance by the returned UTF-8 bytes.");
                offset = next;
                if (pages > 200) throw new Exception("Receipt pagination did not terminate.");
            }
            using var decoded = JsonDocument.Parse(assembled.ToString());
            Check(decoded.RootElement.GetProperty("data").GetProperty("stdout").GetString() == content &&
                decoded.RootElement.GetProperty("error").GetString() == error && pages > 1,
                "concatenated bounded Unicode fragments recover the exact saved result and full error");
            var allBytes = Encoding.UTF8.GetBytes(assembled.ToString());
            var continuation = Array.FindIndex(allBytes, x => (x & 0xc0) == 0x80);
            Check(continuation > 0 && !(await Read(new { runId = id, effectId, offset = continuation })).Success,
                "offset inside a UTF-8 sequence is rejected");
            var end = await Read(new { runId = id, effectId, offset = initial.TotalBytes });
            Check(end.Success && CerebralJson.Read<WorkflowReceiptPage>(end.Data) is { Json: "", Truncated: false, NextOffset: null },
                "exact end offset returns an explicit empty final page");
            var claimed = host.Store.TryClaim(id)!;
            host.Store.ApplyStep(id, claimed.Revision, new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject)));
            Check((await Read(new { runId = id, effectId, offset = initial.NextOffset!.Value })).Success,
                "confirmed receipt can be paged after the run advances without a revision pin");

            var pending = Receipt(host, "pending", lead.Id,
                new(WorkflowEffectState.Unknown, CerebralJson.Element(new { text = content }), "Awaiting provider receipt."));
            var pendingId = pending.Effect!.Id;
            var pendingFirst = CerebralJson.Read<WorkflowReceiptPage>((await Read(new { runId = pending.Id, effectId = pendingId })).Data);
            Check(!(await Read(new { runId = pending.Id, effectId = pendingId, offset = pendingFirst.NextOffset!.Value })).Success &&
                (await Read(new { runId = pending.Id, effectId = pendingId, offset = pendingFirst.NextOffset.Value, expectedRevision = pendingFirst.RunRevision })).Success,
                "uncertain continuation requires its saved revision");
            host.Store.CompleteEffect(pending.Id, pendingId,
                new(WorkflowEffectState.Succeeded, CerebralJson.Element(new { text = "reconciled" })));
            Check(!(await Read(new { runId = pending.Id, effectId = pendingId, offset = pendingFirst.NextOffset.Value, expectedRevision = pendingFirst.RunRevision })).Success,
                "reconciliation invalidates old uncertain page cursor before returning changed content");
            var description = await dispatcher.ExecuteAsync(new("sdk.describe", CerebralJson.Element(new { id = "jobs.workflow.receipt" }), lead.Id));
            Check(description.Success && description.Data.GetProperty("inputSchema").GetProperty("required").GetArrayLength() == 2,
                "receipt lookup is discoverable with the exact coordinate schema");

            var bridge = new WorkerWorkflowEffects(state, config, models, tools);
            var handler = bridge.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ReceiptCommand);
            var receiptContext = new WorkflowCommandContext("host-read", first.Submission, CerebralJson.Element(new { effectId })) { RunId = first.Id };
            var typedArguments = CerebralJson.Element(new WorkerWorkflowEffects.ReceiptArguments(effectId));
            WorkflowSchema.Validate(typedArguments, handler.Descriptor.InputSchema);
            Check((await handler.ExecuteAsync(receiptContext with { Arguments = typedArguments }, default)).State == WorkflowEffectState.Succeeded,
                "typed receipt requests may serialize the omitted revision as null");
            var effectRead = await handler.ExecuteAsync(receiptContext, default);
            var effectPage = effectRead.Data?.Deserialize<WorkflowReceiptPage>(CerebralJson.Options);
            Check(effectRead.State == WorkflowEffectState.Succeeded && effectPage?.State == WorkflowEffectState.Failed &&
                effectPage.Json == initial.Json,
                "host receipt action reuses the pager and preserves the saved failed outcome as data");
            Check((await handler.ExecuteAsync(receiptContext with { RunId = null }, default)).State == WorkflowEffectState.Failed,
                "host receipt action does not infer a missing runtime run binding");
            foreach (var altered in new[] { first.Submission with { RequestId = "different" },
                first.Submission with { ConversationId = "different" }, first.Submission with { ProfileId = other.Id } })
                Check((await handler.ExecuteAsync(receiptContext with { Submission = altered }, default)).State == WorkflowEffectState.Failed,
                    "host receipt action rejects mismatched accepted request, conversation or profile identity");
            Check((await handler.ExecuteAsync(receiptContext with { RunId = second.Id }, default)).State == WorkflowEffectState.Failed &&
                (await handler.ExecuteAsync(receiptContext with { Arguments = CerebralJson.Element(new { effectId, runId = second.Id }) }, default)).State == WorkflowEffectState.Failed,
                "neither forged host binding nor model-supplied runId redirects a receipt lookup");
            var effectForeign = await handler.ExecuteAsync(receiptContext with { Arguments = CerebralJson.Element(new { effectId = second.Effect!.Id }) }, default);
            var effectMissing = await handler.ExecuteAsync(receiptContext with { Arguments = CerebralJson.Element(new { effectId = "missing" }) }, default);
            Check(effectForeign.State == WorkflowEffectState.Failed && effectForeign.Error == effectMissing.Error,
                "host receipt action cannot disclose a same-profile other-run result");
            Check((await handler.ExecuteAsync(receiptContext with { Arguments = CerebralJson.Element(new { effectId, limit = 16385 }) }, default)).State == WorkflowEffectState.Failed &&
                (await handler.ExecuteAsync(receiptContext with { Arguments = CerebralJson.Element(new { effectId, offset = continuation }) }, default)).State == WorkflowEffectState.Failed,
                "host receipt action retains byte limits and Unicode offset validation");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check((await handler.ExecuteAsync(receiptContext, cancelled.Token)).State == WorkflowEffectState.Cancelled,
                "cancelled receipt lookup confirms no original command was replayed");
            var savedRevision = host.Store.Get(first.Id).Revision;
            var reconciledRead = await handler.ReconcileAsync(receiptContext, default);
            Check(reconciledRead.State == WorkflowEffectState.Succeeded && host.Store.Get(first.Id).Revision == savedRevision &&
                host.Store.GetEffectResult(first.Id, effectId).State == WorkflowEffectState.Failed,
                "receipt reconciliation only rereads existing evidence and never changes the original outcome");
            Console.WriteLine($"Workflow receipt commands: {checks} local dispatcher/SQLite integration assertions passed. No model or PAO execution.");
        }
        catch (Exception error) { testFailure = error; throw; }
        finally
        {
            // StateStore pools its short-lived connections. All fixture owners above are already
            // disposed; release the idle pool handles before removing this exact owned directory.
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (Exception cleanupError) when (testFailure is not null)
            {
                throw new AggregateException("Receipt test failed and its temporary database cleanup also failed.", testFailure, cleanupError);
            }
        }
    }

    private static WorkflowRun Receipt(WorkerWorkflows host, string id, string profileId, WorkflowCommandResult result)
    {
        host.Store.Submit(new(id, id, profileId, "example.receipt:review", "1.0.0", "Read receipt.", null,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject));
        var run = host.Store.TryClaim(id)!;
        var descriptor = new WorkflowCommandDescriptor("example.receipt:read", 1, "Read fixture",
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, []);
        run = host.Store.ApplyStep(id, run.Revision, new(WorkflowContract.EmptyObject,
            new InvokeWorkflowCommand(descriptor.Id, 1, WorkflowContract.EmptyObject)), descriptor);
        run = host.Store.DispatchEffect(id, run.Revision);
        return host.Store.CompleteEffect(id, run.Effect!.Id, result);
    }

    private sealed class NoModelExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("This test must not execute models.");
    }
}
