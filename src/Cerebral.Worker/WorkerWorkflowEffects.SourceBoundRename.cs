using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private sealed class SourceBoundConditionalRenameHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(
            SourceBoundConditionalRenameContract.CommandId,
            SourceBoundConditionalRenameContract.CommandVersion,
            "Conditionally rename one exact committed source conversation under host-frozen provenance and an existing revision receipt.",
            Schema("""
                {"type":"object","properties":{"rename":{"type":"object","properties":{
                "requestId":{"type":"string","minLength":1,"maxLength":128},
                "definitionId":{"type":"string","minLength":1,"maxLength":256},
                "conversationId":{"type":"string","minLength":1,"maxLength":128},
                "expectedTitleRevision":{"type":"integer","minimum":0},
                "title":{"type":"string","minLength":1,"maxLength":320}},
                "required":["requestId","definitionId","conversationId","expectedTitleRevision","title"],
                "additionalProperties":false}},"required":["rename"],"additionalProperties":false}
                """),
            Schema("""
                {"type":"object","properties":{
                "disposition":{"enum":["applied","alreadyApplied","skipped"]},
                "receipt":{"oneOf":[{"type":"object","properties":{
                "conversationId":{"type":"string"},"title":{"type":"string"},
                "titleRevision":{"type":"integer","minimum":1}},
                "required":["conversationId","title","titleRevision"],"additionalProperties":false},{"type":"null"}]},
                "skipCode":{"oneOf":[{"type":"string","maxLength":128},{"type":"null"}]}},
                "required":["disposition","receipt","skipCode"],"additionalProperties":false}
                """),
            [], WorkflowReplayPolicy.Never, TimeoutSeconds: 30);

        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = Read<SourceBoundConditionalRenameArguments>(context.Arguments);
            var host = owner.workflowHost?.Invoke()
                ?? throw new InvalidOperationException("The source-bound workflow host is unavailable.");
            // Engine's guarded transaction is the only mutation. Any failure after
            // dispatch without an exact receipt remains Unknown in WorkflowRuntime.
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded,
                Element(host.RenameConversationFromSourceEffect(context, arguments))));
        }

        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = Read<SourceBoundConditionalRenameArguments>(context.Arguments);
            var host = owner.workflowHost?.Invoke()
                ?? throw new InvalidOperationException("The source-bound workflow host is unavailable.");
            var receipt = host.ReadSourceBoundRenameEffectReceipt(context, arguments);
            return Task.FromResult(receipt is null
                ? new WorkflowCommandResult(WorkflowEffectState.Unknown,
                    Error: "The exact guarded rename receipt is absent; no mutation was replayed.")
                : new WorkflowCommandResult(WorkflowEffectState.Succeeded, Element(receipt)));
        }
    }
}
