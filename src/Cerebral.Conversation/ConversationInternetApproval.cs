using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const string InternetReview = "cerebral.host:internet-review";
    private sealed record InternetReviewData(string RequestId, string RequestDigest, string OriginalEffectId,
        string ArgumentsSha256, bool Allowed);
    private static WorkflowStep ReceiveInternetRequirement(HarnessState state, WorkflowEffect effect,
        ToolData data, InternetOwnerAction action, string runId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(effect.Arguments.GetRawText()))).ToLowerInvariant();
        if (effect.State != WorkflowEffectState.Failed || action.SchemaVersion != 1 ||
            action.Kind != "internet-owner-approval" || !action.NoEffect || action.SourceRunId != runId ||
            action.EffectId != effect.Id || action.ToolName != state.PendingName ||
            action.ArgumentsSha256 != hash || !InternetContractValidation.Hex(action.ApprovalRequestId) ||
            !InternetContractValidation.Hex(action.RequestDigest) || data.Content.Length != 0 || data.Truncated || data.Evidence is not null ||
            data.OwnerAction is not null || data.Generation is not null || data.Verification is not null ||
            data.FollowUpRequired || data.RequiredSuccessfulTools is { Count: > 0 })
            return Finish(state, false, "The Internet owner requirement did not match its exact no-send tool effect. No request was sent or replayed.");
        var questions = new[] { new WorkflowQuestion("internet-" + action.ApprovalRequestId,
            "Review this Main chat's managed web access", Choices: ["reviewed"])
        {
            Description = "Owner Internet approval " + action.ApprovalRequestId +
                " is pending. Use the native Internet review to Allow or Deny its exact scope. " +
                "This question waits without sending a request. An answer alone does not grant access."
        } };
        var asking = state with { Phase = "questions", PendingQuestions = questions,
            PendingInternetRequirement = action, InternetToolArguments = state.PendingArguments,
            Repairs = 0, ActionCorrections = null };
        return Fits(asking) ? new(Element(asking), new AskWorkflowQuestions(questions)) : ContextFull(state);
    }
    private static WorkflowStep ReviewInternetAnswer(HarnessState state, WorkflowQuestionSet questions)
    {
        var pending = state.PendingInternetRequirement!;
        var field = "internet-" + pending.ApprovalRequestId;
        if (questions.Fields.Count != 1 || questions.Fields[0].Id != field ||
            !questions.Answers.TryGetValue(field, out var answer) || answer != "reviewed" ||
            state.InternetToolArguments is null)
            return Finish(state, false, "The owner Internet answer did not match the pending native review.");
        var args = Element(new { requestId = pending.ApprovalRequestId, requestDigest = pending.RequestDigest,
            originalEffectId = pending.EffectId, argumentsSha256 = pending.ArgumentsSha256 });
        return Invoke(state with { Phase = "internet-review", PendingArguments = args, PendingQuestions = null }, InternetReview, args);
    }
    private static WorkflowStep ReceiveInternetReview(HarnessState state, WorkflowEffect effect)
    {
        MatchPending(state, effect);
        var pending = state.PendingInternetRequirement ??
            throw new ArgumentException("No pending Internet requirement.");
        var data = effect.State == WorkflowEffectState.Succeeded
            ? effect.Result?.Deserialize<InternetReviewData>(Json) : null;
        if (data is null || data.RequestId != pending.ApprovalRequestId || data.RequestDigest != pending.RequestDigest ||
            data.OriginalEffectId != pending.EffectId || data.ArgumentsSha256 != pending.ArgumentsSha256 ||
            state.InternetToolArguments is not { } original)
            return Finish(state, false, "The saved owner Internet decision was not confirmed. No network request was sent or replayed.");
        if (!data.Allowed)
            return Finish(state with { PendingInternetRequirement = null, InternetToolArguments = null }, false,
                "The owner denied managed web access, or its authority is no longer current. No network request was sent.");
        // The first tool effect is proven no-send. Only a confirmed owner decision may request
        // this new effect; the host rechecks current authority again at dispatch and every send.
        var next = state with { Phase = "tool", PendingArguments = original, PendingName = pending.ToolName,
            PendingInternetRequirement = null, InternetToolArguments = null };
        return Fits(next) ? Invoke(next, Tool, original) : ContextFull(state);
    }
}
