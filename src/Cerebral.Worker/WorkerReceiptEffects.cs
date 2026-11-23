using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    public const string ReceiptCommand = ModuleId + ":receipt";
    public sealed record ReceiptArguments(string EffectId, int Offset = 0, int Limit = 4096, long? ExpectedRevision = null);

    private sealed class ReceiptHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(ReceiptCommand, 1,
            "Read one exact saved result within this workflow only. JSON fragments use UTF-8 byte offsets; continue uncertain results with expectedRevision. This never replays the original operation.",
            Schema("""
                {"type":"object","properties":{"effectId":{"type":"string","minLength":1,"maxLength":128},
                "offset":{"type":"integer","minimum":0,"default":0},"limit":{"type":"integer","minimum":4,"maximum":16384,"default":4096},
                "expectedRevision":{"oneOf":[{"type":"integer","minimum":1},{"type":"null"}]}},"required":["effectId"],"additionalProperties":false}
                """),
            Schema("""
                {"type":"object","properties":{"runId":{"type":"string"},"effectId":{"type":"string"},"state":{"type":"string"},
                "providerReceiptId":{"oneOf":[{"type":"string"},{"type":"null"}]},"errorPreview":{"oneOf":[{"type":"string"},{"type":"null"}]},"runRevision":{"type":"integer"},
                "offset":{"type":"integer"},"limit":{"type":"integer"},"totalBytes":{"type":"integer"},"json":{"type":"string"},
                "nextOffset":{"oneOf":[{"type":"integer"},{"type":"null"}]},"truncated":{"type":"boolean"}},
                "required":["runId","effectId","state","providerReceiptId","errorPreview","runRevision","offset","limit","totalBytes","json","nextOffset","truncated"],"additionalProperties":false}
                """), [], WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 15);

        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult(owner.ReadReceipt(context, cancellationToken));
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult(owner.ReadReceipt(context, cancellationToken));
    }

    private WorkflowCommandResult ReadReceipt(WorkflowCommandContext context, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(context.RunId))
                throw new UnauthorizedAccessException("Receipt lookup requires a host-bound workflow run.");
            var input = Read<ReceiptArguments>(context.Arguments);
            var journal = new EventJournal(Path.Combine(store.DirectoryPath, "engine.db"));
            var receipts = new WorkflowStore(journal);
            var run = receipts.Get(context.RunId);
            if (run.Submission.RequestId != context.Submission.RequestId ||
                run.Submission.ConversationId != context.Submission.ConversationId ||
                run.Submission.ProfileId != context.Submission.ProfileId)
                throw new UnauthorizedAccessException("Receipt lookup does not match the accepted workflow owner.");
            var page = WorkflowReceiptReader.Read(receipts,
                new(context.RunId, input.EffectId, input.Offset, input.Limit, input.ExpectedRevision),
                result => WorkerContextContributionProjection.ProjectReceipt(receipts, context.RunId, input.EffectId, result, journal, cancellationToken,
                    workflowHost is null ? null : run => workflowHost().HistoricalContextAdmissions(run)));
            // Reading a Failed/Unknown receipt succeeded. Its saved outcome remains inside the page;
            // it must not become an instruction to replay the original operation.
            return new(WorkflowEffectState.Succeeded, Element(page));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(WorkflowEffectState.Cancelled, Error: "Receipt lookup was cancelled; no tool was replayed.");
        }
        catch (Exception error) when (IsOrdinary(error))
        {
            var message = error switch
            {
                UnauthorizedAccessException => "Receipt lookup is restricted to its host-bound accepted workflow.",
                KeyNotFoundException => "Workflow effect result was not found for this run.",
                WorkflowConflictException => error.Message,
                JsonException => "Receipt arguments must contain only effectId, offset, limit and optional expectedRevision.",
                ArgumentException => error.Message,
                InvalidDataException => "The stored workflow receipt is invalid or exceeds the supported bound.",
                _ => "The saved workflow receipt could not be read. No original operation was replayed."
            };
            return new(WorkflowEffectState.Failed, Error: message);
        }
    }
}
