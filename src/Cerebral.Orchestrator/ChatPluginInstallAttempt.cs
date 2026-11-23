using System.IO;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

internal enum ChatPluginInstallApplyState { NotStarted, FinalOutcomeConfirmed, OutcomeUncertain }
internal enum ChatPluginInstallNotStartedReason { ManualReview, WaitingForOwnerChat, WaitingForSourceCompletion }

/// <summary>Private normal-pipeline evidence. A callback returning is not evidence of an effect or receipt.</summary>
internal sealed record ChatPluginInstallApplyResult
{
    internal string RequestId { get; }
    internal string? ClaimId { get; }
    internal ChatPluginInstallApplyState State { get; }
    internal ChatPluginInstallNotStartedReason NotStartedReason { get; }
    private ChatPluginInstallApplyResult(string id, string? claim, ChatPluginInstallApplyState state,
        ChatPluginInstallNotStartedReason reason = ChatPluginInstallNotStartedReason.ManualReview)
        => (RequestId, ClaimId, State, NotStartedReason) = (id, claim, state, reason);

    internal static ChatPluginInstallApplyResult NotStarted(string id,
        ChatPluginInstallNotStartedReason reason = ChatPluginInstallNotStartedReason.ManualReview) =>
        new(id, null, ChatPluginInstallApplyState.NotStarted, reason);

    internal static ChatPluginInstallApplyResult Uncertain(string id, string? claim = null) =>
        new(id, claim, ChatPluginInstallApplyState.OutcomeUncertain);

    // Call only after the normal interface.complete reply (and normal journal confirmation for plugins).
    internal static ChatPluginInstallApplyResult Confirmed(InterfaceRequest expected, string claim, InterfaceRequest confirmed)
    {
        if (!Guid.TryParseExact(claim, "N", out var parsed) || parsed == Guid.Empty ||
            confirmed.ClaimId != claim || confirmed.Status is not ("completed" or "failed") ||
            confirmed.Id != expected.Id || confirmed.SessionId != expected.SessionId ||
            confirmed.JobId != expected.JobId || confirmed.ProfileId != expected.ProfileId ||
            confirmed.Kind != expected.Kind || confirmed.Source != expected.Source ||
            confirmed.Path != expected.Path || confirmed.CreatedAt != expected.CreatedAt ||
            confirmed.PackageFingerprint != expected.PackageFingerprint)
            throw new InvalidDataException("A correlated normal claim and final outcome are required.");
        return new(expected.Id, claim, ChatPluginInstallApplyState.FinalOutcomeConfirmed);
    }
}

internal sealed record ChatPluginInstallSourceCheck(WorkflowStatus? Status, bool Eligible);

/// <summary>Only this bounded preflight may classify its own failure as proven zero effect.</summary>
internal static class ChatPluginInstallPreflight
{
    internal static async Task<ChatPluginInstallApplyResult> RunAsync(InterfaceRequest request, Func<bool> currentOwner,
        Func<Task<ChatPluginInstallSourceCheck>> readSource, Action prepareReview,
        Func<Task<ChatPluginInstallApplyResult>> applyNormal)
    {
        try
        {
            if (!currentOwner()) return ChatPluginInstallApplyResult.NotStarted(request.Id, ChatPluginInstallNotStartedReason.WaitingForOwnerChat);
            var source = await readSource();
            if (!currentOwner()) return ChatPluginInstallApplyResult.NotStarted(request.Id, ChatPluginInstallNotStartedReason.WaitingForOwnerChat);
            if (source.Status != WorkflowStatus.Completed)
                return ChatPluginInstallApplyResult.NotStarted(request.Id,
                    source.Status is WorkflowStatus.Queued or WorkflowStatus.Running or WorkflowStatus.Waiting
                        ? ChatPluginInstallNotStartedReason.WaitingForSourceCompletion : ChatPluginInstallNotStartedReason.ManualReview);
            if (!source.Eligible) return ChatPluginInstallApplyResult.NotStarted(request.Id);
            prepareReview();
            if (!currentOwner()) return ChatPluginInstallApplyResult.NotStarted(request.Id, ChatPluginInstallNotStartedReason.WaitingForOwnerChat);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return ChatPluginInstallApplyResult.NotStarted(request.Id);
        }
        // No catch here: after normal-pipeline admission, lack of a journal is never proof of no effect.
        return await applyNormal();
    }
}
