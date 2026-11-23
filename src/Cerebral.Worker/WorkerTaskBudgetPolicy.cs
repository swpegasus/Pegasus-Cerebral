using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    public sealed record TaskBudgetData(int SchemaVersion, string RunId, string ProfileId,
        int ProfileRevision, string Origin, int MaxContextTokens, int MaxResponseTokens,
        int CurrentContextTokens, int DefaultContextTokens, int DefaultResponseTokens,
        bool ContextSelectable, bool ResponseSelectable, bool AllowSelection);

    private static string? TaskBudgetOrigin(WorkflowSubmission submission) =>
        submission.ParentRunId is not null &&
            submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.coordination" } ? "worker" :
        submission.ParentRunId is null &&
            submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.collaboration" } ? "coordinator" :
        submission.ParentRunId is null &&
            (submission.Caller is null or { Kind: EventActorKind.User }) ? "owner" : null;

    internal TaskBudgetData? DescribeTaskBudget(WorkflowSubmission submission, string runId)
    {
        var frozen = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Profile;
        var policy = frozen.TaskBudgetPolicy;
        var origin = TaskBudgetOrigin(submission);
        if (policy is not { SchemaVersion: 1 } || origin is null ||
            submission.LocalGeneration is not null) return null;
        var currentFit = frozen.ContextTokens;
        if (frozen.ModelWorkerId == "local" && store.Get<ModelRecord>("model", frozen.ModelId) is { } model)
            currentFit = model.ContextTokens;
        var childContext = origin == "worker" && submission.Input.TryGetProperty("maxContextTokens", out var childMaxContext)
            ? childMaxContext.GetInt32() : (int?)null;
        var childOutput = origin == "worker" && submission.Input.TryGetProperty("maxResponseTokens", out var childMaxOutput)
            ? childMaxOutput.GetInt32() : (int?)null;
        var share = submission.Input.TryGetProperty("responseSharePercent", out var selectedShare)
            ? selectedShare.GetInt32() : (int?)null;
        var maxContext = Math.Min(policy.MaxContextTokens ?? frozen.ContextTokens, childContext ?? int.MaxValue);
        var maxOutput = Math.Min(policy.MaxResponseTokens ??
            (share is null ? frozen.ResponseTokens : Math.Min(131072, maxContext / 2)),
            childOutput ?? int.MaxValue);
        var autonomous = policy.AllowAutonomousSelection && origin != "owner";
        // A typed task context replaces the unused profile default for share calculation.
        // Keep the admission's default pair internally consistent for Conversation to read.
        var requestedContext = submission.Input.TryGetProperty("requestedContextTokens", out var selectedContext)
            ? selectedContext.GetInt32() : (int?)null;
        var requestedOutput = submission.Input.TryGetProperty("requestedResponseTokens", out var selectedOutput)
            ? selectedOutput.GetInt32() : (int?)null;
        var defaultContext = requestedContext ?? (autonomous && policy.MaxContextTokens is not null
            ? Math.Min(currentFit, maxContext) : Math.Min(frozen.ContextTokens, maxContext));
        var defaultOutput = requestedOutput ?? (share is { } percent
            ? (int)((long)defaultContext * percent / 100)
            : Math.Min(frozen.ResponseTokens, maxOutput));
        var reasoning = frozen.ReasoningMode == "on" ? frozen.ReasoningTokens : 0;
        if (maxContext < AgentTokenLimits.MinimumContextTokens ||
            maxOutput < AgentTokenLimits.MinimumResponseTokens ||
            requestedOutput is not null && share is not null ||
            defaultContext < AgentTokenLimits.MinimumContextTokens || defaultContext > maxContext ||
            defaultContext > currentFit ||
            defaultOutput < AgentTokenLimits.MinimumResponseTokens || defaultOutput > maxOutput ||
            defaultOutput > AgentTokenLimits.MaximumVisibleResponse(defaultContext,
                frozen.ReasoningMode, reasoning))
            throw new AdmissionException("The task budget policy cannot fit the current model and required framing.");
        return new(1, runId, frozen.Id, frozen.Revision, origin, maxContext, maxOutput,
            currentFit, defaultContext, defaultOutput, policy.MaxContextTokens is not null,
            policy.MaxResponseTokens is not null && share is null,
            autonomous && (policy.MaxContextTokens is not null ||
                policy.MaxResponseTokens is not null && share is null));
    }

    internal (int Context, int Output) RequireTaskBudget(WorkflowSubmission submission,
        AgentProfile current, GenerateArguments args, ModelRecord model, int reasoningTokens)
    {
        var frozen = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Profile;
        if (TaskBudgetOrigin(submission) is null || frozen.TaskBudgetPolicy is not { SchemaVersion: 1 } accepted ||
            current.TaskBudgetPolicy is not { SchemaVersion: 1 } owner || submission.LocalGeneration is not null)
            throw new AdmissionException("This accepted run has no current owner task budget policy.");
        if (args.ContextLimit is not { } context || args.ResponseLimit is not { } output ||
            args.ResponseSharePercent is not null || args.HostReasoningTokens is not null)
            throw new AdmissionException("Version-four generation requires exact total context and visible output allowances.");
        var input = submission.Input;
        int? ChildLimit(string name) => submission.ParentRunId is not null && input.TryGetProperty(name, out var value)
            ? value.GetInt32() : null;
        var share = input.TryGetProperty("responseSharePercent", out var selectedShare)
            ? selectedShare.GetInt32() : (int?)null;
        var acceptedContext = Math.Min(accepted.MaxContextTokens ?? frozen.ContextTokens,
            ChildLimit("maxContextTokens") ?? int.MaxValue);
        var acceptedOutput = Math.Min(accepted.MaxResponseTokens ??
            (share is null ? frozen.ResponseTokens : Math.Min(131072, acceptedContext / 2)),
            ChildLimit("maxResponseTokens") ?? int.MaxValue);
        var currentContext = owner.MaxContextTokens ?? current.ContextTokens;
        var currentOutput = owner.MaxResponseTokens ??
            (share is null ? current.ResponseTokens : Math.Min(131072, currentContext / 2));
        if (context is < 512 or > 1048576 || output is < 128 or > 131072 ||
            context > acceptedContext || context > currentContext || context > model.ContextTokens ||
            output > acceptedOutput || output > currentOutput || output >= context ||
            accepted.MaxContextTokens is null && context != frozen.ContextTokens ||
            accepted.MaxResponseTokens is null && share is null && output != frozen.ResponseTokens)
            throw new AdmissionException("The selected budget exceeds the accepted owner, child, or current model bound.");
        var typedContext = input.TryGetProperty("requestedContextTokens", out var selectedContext)
            ? selectedContext.GetInt32() : (int?)null;
        var typedOutput = input.TryGetProperty("requestedResponseTokens", out var selectedOutput)
            ? selectedOutput.GetInt32() : (int?)null;
        var defaultOutput = share is { } percent ? (int)((long)context * percent / 100) :
            Math.Min(frozen.ResponseTokens, acceptedOutput);
        var origin = TaskBudgetOrigin(submission);
        if (typedContext is { } pinnedContext && context != pinnedContext ||
            typedOutput is { } pinnedOutput && output != pinnedOutput ||
            origin == "owner" && typedContext is null && context != Math.Min(frozen.ContextTokens, acceptedContext) ||
            origin == "owner" && typedOutput is null && output != defaultOutput ||
            !accepted.AllowAutonomousSelection && typedContext is null && context != Math.Min(frozen.ContextTokens, acceptedContext) ||
            !accepted.AllowAutonomousSelection && typedOutput is null && output != defaultOutput ||
            !owner.AllowAutonomousSelection && typedContext is null && context != Math.Min(frozen.ContextTokens, acceptedContext) ||
            !owner.AllowAutonomousSelection && typedOutput is null && output != defaultOutput ||
            share is not null && output != defaultOutput)
            throw new AdmissionException("This run cannot replace an owner-pinned or disabled budget dimension.");
        if (output > AgentTokenLimits.MaximumVisibleResponse(context,
                reasoningTokens == 0 ? "off" : "on", reasoningTokens))
            throw new AdmissionException("The selected context leaves insufficient input, thinking, and response headroom.");
        return (context, output);
    }
}
