using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Small, session-confined controls that an agent can apply to a later conversation assignment.</summary>
public sealed class ConversationToolExtension(StateStore store, EventHub events) : IToolExtension
{
    public const string ResponseBalanceToolName = "conversation.response-balance";
    private const string WorkflowResponseBalanceKind = "workflow-response-balance";
    private static readonly TimeSpan WorkflowResponseBalanceLifetime = TimeSpan.FromHours(24);

    internal sealed record WorkflowResponseBalance(string RequestId, string ConversationId, string ProfileId,
        int ResponseSharePercent, DateTimeOffset CreatedAt);

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ResponseBalanceToolName,
            "Set the response share for the next model generation in this durable assignment. Use 5-50 when the next model run needs more output or more input context. This does not enlarge the model context or change the model call already in progress.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new
                {
                    responseSharePercent = new
                    {
                        type = "integer",
                        minimum = AgentTokenLimits.MinimumResponseSharePercent,
                        maximum = AgentTokenLimits.MaximumResponseSharePercent
                    }
                },
                required = new[] { "responseSharePercent" }, additionalProperties = false
            }), "diagnostics")
    ];

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (call.Name != ResponseBalanceToolName) throw new ArgumentException("Unknown conversation tool.");
        if (!call.Arguments.TryGetProperty("responseSharePercent", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var share))
            throw new ArgumentException("Response balance must be an integer percentage.");
        AgentTokenLimits.ValidateResponseShare(share, "responseSharePercent");
        var session = store.Locked(() =>
        {
            var current = store.GetSessionMetadata(context.SessionId);
            if (current is null) return null;
            if (!string.Equals(current.ProfileId, context.Profile.Id, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The current agent does not own this conversation.");
            current.ResponseSharePercent = share;
            store.Put("session", current.Id, current);
            return current;
        });
        if (session is not null)
        {
            events.Publish("session.updated", null, new { id = session.Id, responseBalanceChanged = true });
            return Task.FromResult(new ToolResult(true,
                $"Response balance saved at {share}% for the next accepted assignment; the current assignment is unchanged.")
            {
                Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.conversation")
            });
        }

        if (!context.EnforceProfileScope || string.IsNullOrWhiteSpace(context.SessionId) ||
            string.IsNullOrWhiteSpace(context.JobId) || string.IsNullOrWhiteSpace(context.Profile.Id))
            throw new UnauthorizedAccessException("A host-owned durable assignment identity is required.");
        var now = DateTimeOffset.UtcNow;
        var pending = new WorkflowResponseBalance(context.JobId, context.SessionId, context.Profile.Id, share, now);
        store.Locked(() =>
        {
            foreach (var stale in store.List<WorkflowResponseBalance>(WorkflowResponseBalanceKind)
                         .Where(item => now - item.CreatedAt > WorkflowResponseBalanceLifetime))
                store.Delete(WorkflowResponseBalanceKind, stale.RequestId);
            store.Put(WorkflowResponseBalanceKind, pending.RequestId, pending);
            return true;
        });
        events.Publish("workflow.response-balance.updated", context.SessionId,
            new { requestId = context.JobId, responseBalanceChanged = true });
        return Task.FromResult(new ToolResult(true,
            $"Response balance staged at {share}% for the next model generation in this durable assignment; the current model call is unchanged.")
        {
            Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.conversation")
        });
    }

    internal static WorkflowResponseBalance? ReadWorkflowResponseBalance(StateStore store, WorkflowSubmission submission)
    {
        return store.Locked(() =>
        {
            var pending = store.Get<WorkflowResponseBalance>(WorkflowResponseBalanceKind, submission.RequestId);
            if (pending is null) return null;
            if (DateTimeOffset.UtcNow - pending.CreatedAt > WorkflowResponseBalanceLifetime)
            {
                store.Delete(WorkflowResponseBalanceKind, pending.RequestId);
                return null;
            }
            return pending.ConversationId == submission.ConversationId && pending.ProfileId == submission.ProfileId
                ? pending : null;
        });
    }

    internal static void ConsumeWorkflowResponseBalance(StateStore store, WorkflowSubmission submission,
        WorkflowResponseBalance expected) => _ = TryConsumeWorkflowResponseBalance(store, submission, expected);

    // Linearize the prepared request's preference with inference admission. A new or replaced
    // pending preference belongs to a later preparation; never silently generate with the stale one.
    internal static bool TryConsumeWorkflowResponseBalance(StateStore store, WorkflowSubmission submission,
        WorkflowResponseBalance? expected)
    {
        return store.Locked(() =>
        {
            var current = store.Get<WorkflowResponseBalance>(WorkflowResponseBalanceKind, submission.RequestId);
            if (current != expected) return false;
            if (current is null) return true;
            if (current.RequestId != submission.RequestId || current.ConversationId != submission.ConversationId ||
                current.ProfileId != submission.ProfileId ||
                DateTimeOffset.UtcNow - current.CreatedAt > WorkflowResponseBalanceLifetime) return false;
            store.Delete(WorkflowResponseBalanceKind, submission.RequestId);
            return true;
        });
    }
}
