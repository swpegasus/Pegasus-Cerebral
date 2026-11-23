using System.Text.Json;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    public const string OwnedCoordinateCommand = ModuleId + ":coordinate-owned";
    public const string OwnedRelayCommand = ModuleId + ":relay-owned-answer";
    internal Func<CollaborationService>? OwnedCoordinationService { get; init; }
    public sealed record MainHostActionsData(int SchemaVersion, string SourceRunId,
        bool Coordinate, bool RelayOwnerAnswer);

    // This is support discovery for two fixed native contracts, never an execution grant.
    // The registry freezes handlers, so compare their registered contracts rather than instances.
    internal bool SupportsOwnedCoordination(WorkflowRegistry registry)
    {
        foreach (var id in new[] { OwnedCoordinateCommand, OwnedRelayCommand })
        {
            var expected = Commands.SingleOrDefault(command => command.Descriptor.Id == id &&
                command.Descriptor.Version == 1);
            if (expected is null || !registry.TryGetCommand(id, 1, out var registered) ||
                !JsonElement.DeepEquals(Element(expected.Descriptor), Element(registered.Descriptor)))
                return false;
        }
        return true;
    }

    private MainHostActionsData? DescribeMainHostActions(WorkflowSubmission accepted)
    {
        if (!ConversationRolePolicy.IsMain(accepted)) return null;
        var (_, current) = Resolve(accepted); // Auth/routing failures keep the normal fail-closed path.
        var available = false;
        if (current.Role == "lead" && workflowHost is not null && OwnedCoordinationService is not null)
        {
            try
            {
                available = SupportsOwnedCoordination(workflowHost().Registry) &&
                    OwnedCoordinationService() is { SupportsMainOwnedActions: true };
            }
            catch (Exception error) when (IsOrdinary(error))
            {
                // An unavailable owned service is explicit in this run's support record.
                available = false;
            }
        }
        return new(1, accepted.RequestId, available, available);
    }

    private sealed record OwnedRelayArguments(string TaskId);
    private static readonly JsonElement OwnedCoordinateInput = Schema("""
        {"type":"object","properties":{"requestId":{"type":"string","minLength":1,"maxLength":128},"instruction":{"type":"string","minLength":1,"maxLength":32768},"stepLimit":{"type":"integer","minimum":1,"maximum":256},"responseSharePercent":{"oneOf":[{"type":"integer","minimum":5,"maximum":50},{"type":"null"}]}},"required":["requestId","instruction"],"additionalProperties":false}
        """);
    private static readonly JsonElement OwnedRelayInput = Schema("""
        {"type":"object","properties":{"taskId":{"type":"string","minLength":1,"maxLength":128}},"required":["taskId"],"additionalProperties":false}
        """);
    private static readonly JsonElement OwnedActionOutput = Schema("""
        {"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"},"truncated":{"type":"boolean"},"evidence":{},"followUpRequired":{"type":"boolean"}},"required":["name","content","truncated","evidence","followUpRequired"],"additionalProperties":false}
        """);

    private sealed class OwnedCoordinateHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(OwnedCoordinateCommand, 1,
            "Assign work to this authenticated Main's own coordinator under its accepted execution grants.",
            OwnedCoordinateInput, OwnedActionOutput, [], WorkflowReplayPolicy.Never, TimeoutSeconds: 600);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token) =>
            owner.OwnedCoordinationAsync(context, relay: false, token);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            Task.FromResult(UnknownOwnedAction());
    }

    private sealed class OwnedRelayHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(OwnedRelayCommand, 1,
            "Relay the saved owner answer only to the exact waiting task in this Main's owned coordinator.",
            OwnedRelayInput, OwnedActionOutput, [], WorkflowReplayPolicy.Never, TimeoutSeconds: 60);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token) =>
            owner.OwnedCoordinationAsync(context, relay: true, token);
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            Task.FromResult(UnknownOwnedAction());
    }

    private static WorkflowCommandResult UnknownOwnedAction() => new(WorkflowEffectState.Unknown,
        Error: "The owned action has no confirmed receipt. Inspect the exact source effect and task; it was not replayed.");

    private async Task<WorkflowCommandResult> OwnedCoordinationAsync(WorkflowCommandContext context,
        bool relay, CancellationToken token)
    {
        CollaborationService service;
        ToolExecutionContext execution;
        CollaborationService.CoordinateRequest? assignment = null;
        string? taskId = null;
        try
        {
            token.ThrowIfCancellationRequested();
            WorkflowSchema.Validate(context.Arguments, relay ? OwnedRelayInput : OwnedCoordinateInput);
            if (!ConversationRolePolicy.IsMain(context.Submission) ||
                context.RunId != context.Submission.RequestId)
                throw new AdmissionException("Only the exact native owner Main can invoke an owned action.");
            var (_, profile) = Resolve(context.Submission);
            service = OwnedCoordinationService?.Invoke() ??
                throw new AdmissionException("The owned coordination host is unavailable.");
            execution = new(profile, context.Submission.ConversationId, context.Submission.RequestId,
                EffectId: context.EffectId) { EnforceProfileScope = true };
            var source = service.RequireOwnedCommandSource(execution,
                relay ? OwnedRelayCommand : OwnedCoordinateCommand, context.Arguments);
            if (!JsonElement.DeepEquals(CerebralJson.Element(source.Submission),
                CerebralJson.Element(context.Submission)))
                throw new AdmissionException("The owned action submission does not match its persisted source.");
            if (relay) taskId = Read<OwnedRelayArguments>(context.Arguments).TaskId;
            else assignment = Read<CollaborationService.CoordinateRequest>(context.Arguments);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(WorkflowEffectState.Cancelled, Error: "The owned action was cancelled before dispatch.");
        }
        catch (Exception error) when (IsOrdinary(error))
        {
            return new(WorkflowEffectState.Failed, Error: PreparationError(error));
        }

        // No semantic tool or generic cross-chat capability is granted here. Only these two
        // native actions are admitted; the gateway captures the original source execution scope.
        // Once dispatch begins, an unconfirmed outcome stays Unknown and is never re-executed.
        try
        {
            token.ThrowIfCancellationRequested();
            CollaborationTask task;
            string name;
            object content;
            if (relay)
            {
                task = service.RelayOwnedSourceOwnerAnswer(taskId!, execution);
                name = CollaborationToolExtension.RelayAnswerTool;
                content = new { taskId = task.Id, task.AttemptId, task.TargetRunId,
                    task.TargetRunRevision, task.Revision, task.Status };
            }
            else
            {
                task = await service.CoordinateOwnedFromWorkflowAsync(assignment!, execution, context.Arguments, token).ConfigureAwait(false);
                if (task.SourceRunId != context.RunId || task.TargetRunId != task.AttemptId)
                    throw new InvalidDataException("The owned coordinator receipt has a different source or attempt.");
                name = CollaborationToolExtension.CoordinateTool;
                content = new { taskId = task.Id, task.AttemptId, task.TargetRunId, task.Revision,
                    sourceRunId = task.SourceRunId, task.Status };
            }
            var evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded,
                "cerebral.collaboration", context.EffectId, task.TargetRunId);
            return new(WorkflowEffectState.Succeeded, Element(new ToolData(name,
                CerebralJson.Element(content).GetRawText(), false, evidence)));
        }
        catch (Exception error) when (IsOrdinary(error))
        {
            return UnknownOwnedAction();
        }
    }
}
