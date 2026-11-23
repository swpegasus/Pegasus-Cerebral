using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationWorkflowLiveTests
{
    private static async Task VerifySteeringAsync(WorkerWorkflows workflows, SteeringGateModule gate, AgentProfile profile,
        string conversationId, string project, CancellationToken cancellationToken, Action<bool, string> check)
    {
        var oldMarker = "obsolete-" + Guid.NewGuid().ToString("N")[..8];
        var newMarker = "updated-" + Guid.NewGuid().ToString("N")[..8];
        var hiddenMarker = "private-" + Guid.NewGuid().ToString("N")[..8];
        var accepted = await workflows.SubmitAsync(new(Guid.NewGuid().ToString("N"), conversationId, profile.Id,
            ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
            "Reply exactly " + oldMarker + ". Nothing else. No tools.", null,
            CerebralJson.Element(new { workingDirectory = project, responseSharePercent = 20 })), null, cancellationToken);
        var running = workflows.Runtime.RunAsync(accepted.Id, cancellationToken);
        WorkflowSteeringReceipt receipt;
        var request = new WorkflowSteeringRequest(Guid.NewGuid().ToString("N"), accepted.Id,
            "Correction: discard the obsolete reply. Reply exactly " + newMarker + " followed by the private steering label. No tools.",
            "The private steering label is " + hiddenMarker + ". Include it only because this correction explicitly requests it.");
        try
        {
            await gate.Ready.Task.WaitAsync(cancellationToken);
            receipt = workflows.Steer(request, null);
            check(workflows.Store.Get(accepted.Id).Effect?.State == WorkflowEffectState.Dispatched,
                "owner steering was saved while a real generation result was still in flight");
            check(workflows.Steer(request, null) == receipt, "identical steering retry returns its original receipt");
        }
        finally { gate.Release.TrySetResult(); }
        var finished = await running;
        Print(workflows.Journal, conversationId, accepted.Submission.ConversationSequence);
        Console.WriteLine("STEERED CHAT: " + finished.Status + "; " + (finished.Error ?? Summary(finished)));
        check(finished.Status == WorkflowStatus.Completed && Summary(finished).Contains(newMarker, StringComparison.Ordinal) &&
            Summary(finished).Contains(hiddenMarker, StringComparison.Ordinal) && !Summary(finished).Contains(oldMarker, StringComparison.Ordinal),
            "real model answers corrected visible and hidden steering instead of the obsolete generation");
        var retained = Events(workflows.Journal, conversationId);
        check(!retained.Any(item => item.Type == CerebralEventTypes.ToolCall && item.Data is { } data &&
            CerebralJson.Read<ToolCallData>(data).ToolName == WorkerWorkflowEffects.ToolCommand),
            "superseded model response produced no project tool execution");
        check(retained.Any(item => item.Type == "workflow.steering.assignment" &&
            item.Projection.Human == HumanVisibility.Hidden),
            "private steering is saved separately from visible conversation text");
        var summary = workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, conversationId)!;
        var title = workflows.RenameConversation(new(Guid.NewGuid().ToString("N"), ConversationWorkflowModule.DefinitionId,
            conversationId, summary.TitleRevision, "Steering acceptance"), null);
        check(title.TitleRevision == summary.TitleRevision + 1 &&
            workflows.Store.GetConversation(ConversationWorkflowModule.DefinitionId, conversationId)?.Title == title.Title,
            "owner rename saves a separate conversation title after the actual model result");
        check(workflows.Steer(request, null) == receipt, "completed work still returns the original steering receipt");
    }

    // Only delays acknowledgement; the installed model receives and generates the actual first
    // request. This makes arrival of owner input deterministic without substituting model output.
    private sealed class SteeringGateModule(WorkerWorkflowEffects inner) : IWorkflowModule
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int held;
        public string Id => inner.Id;
        public IReadOnlyList<IWorkflowDefinition> Workflows => inner.Workflows;
        public IReadOnlyList<IWorkflowCommandHandler> Commands => inner.Commands.Select(command =>
            command.Descriptor.Id == "cerebral.host:generate" ? (IWorkflowCommandHandler)new Gate(command, this) : command).ToArray();
        private sealed class Gate(IWorkflowCommandHandler inner, SteeringGateModule owner) : IWorkflowCommandHandler
        {
            public WorkflowCommandDescriptor Descriptor => inner.Descriptor;
            public string? GetFailureBudgetKey(WorkflowCommandContext context) => inner.GetFailureBudgetKey(context);
            public async Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
            {
                var result = await inner.ExecuteAsync(context, cancellationToken);
                if (Interlocked.Exchange(ref owner.held, 1) == 0)
                {
                    owner.Ready.TrySetResult();
                    await owner.Release.Task.WaitAsync(cancellationToken);
                }
                return result;
            }
            public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
                inner.ReconcileAsync(context, cancellationToken);
        }
    }
}
