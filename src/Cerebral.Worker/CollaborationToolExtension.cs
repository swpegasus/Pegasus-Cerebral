using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker;

/// <summary>Agent entry for a collaboration task, bound to the host's saved source tool effect.</summary>
public sealed class CollaborationToolExtension(Func<CollaborationService> service) : IToolExtension, IToolRecoveryExtension
{
    public const string SubmitTool = "collaboration.submit";
    public const string CoordinateTool = "collaboration.coordinate";
    public const string RelayAnswerTool = "collaboration.relay-answer";
    public const string PermissionRequestTool = "collaboration.permissions.request";
    public const string PermissionGetTool = "collaboration.permissions.get";
    public const string PermissionDirectoryTool = "collaboration.permissions.directory";
    private const string Provider = "cerebral.collaboration";

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(CoordinateTool,
            "Hand an authorized execution or research goal to this Main chat's own coordinator. It plans, delegates and verifies; exact questions return here. No target chat or profile is invented or adopted.",
            CerebralJson.Element(new { type = "object", properties = new {
                requestId = new { type = "string", minLength = 1, maxLength = 128 },
                instruction = new { type = "string", minLength = 1, maxLength = 32768 },
                stepLimit = new { type = "integer", minimum = 1, maximum = 256 },
                responseSharePercent = new { type = new[] { "integer", "null" }, minimum = 5, maximum = 50 } },
                required = new[] { "requestId", "instruction" }, additionalProperties = false }), "agents.collaborate"),
        new(SubmitTool,
            "Delegate one task to another conversation. This source assignment waits for the target; any target clarification is returned here for the owner to answer.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new
                {
                    requestId = new { type = "string" },
                    sourceConversationId = new { type = "string" },
                    targetConversationId = new { type = "string" },
                    instruction = new { type = "string" },
                    inputReferences = new { type = "array", maxItems = CollaborationContract.MaximumReferences },
                    dependencyTaskIds = new { type = "array", maxItems = CollaborationContract.MaximumDependencies },
                    parentTaskId = new { type = new[] { "string", "null" } },
                    stepLimit = new { type = "integer", minimum = 1, maximum = 256 },
                    responseSharePercent = new { type = new[] { "integer", "null" } },
                    projectGrantId = new { type = new[] { "string", "null" } }
                },
                required = new[] { "requestId", "sourceConversationId", "targetConversationId", "instruction", "inputReferences", "dependencyTaskIds" },
                additionalProperties = false
            }), "agents.collaborate"),
        new(RelayAnswerTool,
            "Relay the exact owner-answered question in this source conversation to its bound waiting target. Answers are read from the saved owner form, never from tool arguments.",
            CerebralJson.Element(new { type = "object",
                properties = new { taskId = new { type = "string" } },
                required = new[] { "taskId" }, additionalProperties = false }), "agents.collaborate"),
        new(PermissionRequestTool,
            "Ask the owner to allow communication between this source chat and one target chat. A request never grants permission.",
            CerebralJson.Element(new { type = "object",
                properties = new { requestId = new { type = "string" },
                    sourceConversationId = new { type = "string" },
                    targetConversationId = new { type = "string" },
                    expectedRevision = new { type = "integer", minimum = 0 } },
                required = new[] { "requestId", "sourceConversationId", "targetConversationId", "expectedRevision" },
                additionalProperties = false }), "agents.collaborate"),
        new(PermissionGetTool,
            "Read current permission and revision for this source chat and one target chat.",
            CerebralJson.Element(new { type = "object",
                properties = new { targetConversationId = new { type = "string" } },
                required = new[] { "targetConversationId" }, additionalProperties = false }),
            "agents.collaborate"),
        new(PermissionDirectoryTool,
            "List bounded conversation IDs, profiles and projects for choosing a communication target. No titles or chat content are returned.",
            CerebralJson.Element(new { type = "object",
                properties = new { afterConversationId = new { type = new[] { "string", "null" } },
                    limit = new { type = "integer", minimum = 1,
                        maximum = ConversationCommunicationContract.MaximumPageSize } },
                required = Array.Empty<string>(), additionalProperties = false }),
            "agents.collaborate")
    ];

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (call.Name == PermissionRequestTool)
        {
            var permissionRequest = call.Arguments.Deserialize<ConversationCommunicationRequest>(CerebralJson.Options) ??
                throw new ArgumentException("A communication request is required.");
            var pending = service().RequestCommunicationFromWorkflow(permissionRequest, context, cancellationToken);
            return Success(context, pending);
        }
        if (call.Name == PermissionGetTool)
        {
            if (call.Arguments.ValueKind != JsonValueKind.Object ||
                !call.Arguments.TryGetProperty("targetConversationId", out var target) ||
                target.ValueKind != JsonValueKind.String || call.Arguments.EnumerateObject().Count() != 1)
                throw new ArgumentException("An exact target conversation is required.");
            return Success(context, service().GetCommunicationForWorkflow(target.GetString()!,
                context, cancellationToken));
        }
        if (call.Name == PermissionDirectoryTool)
        {
            var after = call.Arguments.TryGetProperty("afterConversationId", out var cursor) &&
                cursor.ValueKind == JsonValueKind.String ? cursor.GetString() : null;
            var limit = call.Arguments.TryGetProperty("limit", out var count) && count.TryGetInt32(out var value)
                ? value : 32;
            return Success(context, service().ReadCommunicationDirectoryForWorkflow(after, limit,
                context, cancellationToken));
        }
        if (call.Name == RelayAnswerTool)
        {
            if (call.Arguments.ValueKind != JsonValueKind.Object ||
                !call.Arguments.TryGetProperty("taskId", out var taskId) ||
                taskId.ValueKind != JsonValueKind.String ||
                call.Arguments.EnumerateObject().Count() != 1)
                throw new ArgumentException("The relay requires only the exact task ID.");
            var answered = service().RelaySourceOwnerAnswer(taskId.GetString()!, context);
            return new(true, CerebralJson.Element(new
            {
                taskId = answered.Id, answered.AttemptId, answered.TargetRunId,
                answered.TargetRunRevision, answered.Revision, answered.Status
            }).GetRawText())
            { Evidence = new(ToolExecutionOutcome.Succeeded, Provider, context.EffectId, answered.TargetRunId) };
        }
        if (call.Name == CoordinateTool)
        {
            var coordinate = call.Arguments.Deserialize<CollaborationService.CoordinateRequest>(CerebralJson.Options) ??
                throw new ArgumentException("A coordinator assignment is required.");
            var ownedTask = await service().CoordinateFromWorkflowAsync(coordinate, context, cancellationToken).ConfigureAwait(false);
            if (ownedTask.SourceRunId != context.JobId || ownedTask.TargetRunId != ownedTask.AttemptId)
                throw new InvalidDataException("The owned coordinator attempt is not attached to this Main effect.");
            return new(true, CerebralJson.Element(new { taskId = ownedTask.Id, ownedTask.AttemptId,
                ownedTask.TargetRunId, ownedTask.Revision, sourceRunId = ownedTask.SourceRunId, ownedTask.Status }).GetRawText())
            { Evidence = new(ToolExecutionOutcome.Succeeded, Provider, context.EffectId, ownedTask.TargetRunId) };
        }
        if (call.Name != SubmitTool) throw new ArgumentException("Unknown collaboration tool.");
        var request = call.Arguments.Deserialize<CollaborationTaskRequest>(CerebralJson.Options) ??
            throw new ArgumentException("A collaboration task request is required.");
        var task = await service().SubmitFromWorkflowAsync(request, context, cancellationToken).ConfigureAwait(false);
        if (task.SourceRunId != context.JobId || task.TargetRunId != task.AttemptId)
            throw new InvalidDataException("The collaboration attempt is not attached to this source workflow.");
        return new(true, CerebralJson.Element(new
        {
            taskId = task.Id, task.AttemptId, task.TargetRunId, task.Revision,
            sourceRunId = task.SourceRunId, task.Status
        }).GetRawText())
        { Evidence = new(ToolExecutionOutcome.Succeeded, Provider, context.EffectId, task.TargetRunId) };
    }

    public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence? previousEvidence, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolResult(false,
            "The collaboration tool receipt is unconfirmed. Inspect the exact saved task and source effect; do not submit another request.")
        { Evidence = new(ToolExecutionOutcome.Unknown, Provider, context.EffectId) });

    private static ToolResult Success(ToolExecutionContext context, object value) =>
        new(true, CerebralJson.Element(value).GetRawText())
        { Evidence = new(ToolExecutionOutcome.Succeeded, Provider, context.EffectId) };
}
