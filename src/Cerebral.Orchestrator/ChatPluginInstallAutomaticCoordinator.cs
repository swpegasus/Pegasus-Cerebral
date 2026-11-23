using System.IO;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

internal sealed record ChatPluginInstallOwnerBinding(string ConversationId, string? ProjectId,
    string ProfileId, string ConnectionIdentity, long ConnectionVersion, long SelectionVersion, bool LocalNative, long ProfileRevision = 0);
internal sealed record ChatPluginInstallApproval(string RequestId, string ConversationId, string ProfileId,
    string? ProjectId, long PermissionRevision, string DecisionId, string PackageFingerprint);
internal enum ChatPluginAutomaticInstallResult { ManualReview, WaitingForSourceCompletion, WaitingForOwnerChat, NormalAttemptInProgress, FinalOutcomeConfirmed, OutcomeUncertain }

/// <summary>Bounded event/connection/selection opportunities; no polling or replay loop.</summary>
internal sealed class ChatPluginInstallAutomaticCoordinator(
    Func<ChatPluginInstallOwnerBinding?> current,
    Func<string, string, CancellationToken, Task<JsonElement>> readAuthorization,
    Func<InterfaceRequest, ChatPluginInstallApproval, CancellationToken, Task<ChatPluginInstallApplyResult>> applyNormal,
    Func<string, bool>? hasRetainedOutcome = null)
{
    private readonly Dictionary<string, (string Stamp, ChatPluginAutomaticInstallResult Result)> observed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatPluginAutomaticInstallResult> attempted = new(StringComparer.Ordinal);
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    private ChatPluginInstallOwnerBinding? observationOwner;

    internal async Task<ChatPluginAutomaticInstallResult> HandleAsync(InterfaceRequest request, bool legitimateOpportunity,
        CancellationToken token, string opportunity = "interface-event")
    {
        if (!legitimateOpportunity || request.Kind != "plugin" || request.Source != "workflow" ||
            request.Status != "pending" || request.ClaimId is not null) return ChatPluginAutomaticInstallResult.ManualReview;
        var captured = current();
        if (captured is not { LocalNative: true } || request.SessionId != captured.ConversationId ||
            request.ProfileId != captured.ProfileId) return ChatPluginAutomaticInstallResult.WaitingForOwnerChat;
        if (observationOwner != captured)
        {
            observed.Clear(); observationOwner = captured;
        }
        try { if (hasRetainedOutcome?.Invoke(request.Id) == true) return ChatPluginAutomaticInstallResult.OutcomeUncertain; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return ChatPluginAutomaticInstallResult.OutcomeUncertain; }
        // Admission spans the authorization await as well as normal Apply. Diagnostic cache
        // eviction and a newer offered opportunity cannot admit the same request concurrently.
        if (!inFlight.Add(request.Id)) return ChatPluginAutomaticInstallResult.NormalAttemptInProgress;
        try
        {
            if (attempted.TryGetValue(request.Id, out var priorAttempt)) return priorAttempt;
            if (observed.TryGetValue(request.Id, out var prior) && prior.Stamp == opportunity) return prior.Result;
            // Cache a bounded read per real opportunity; a Running response must not consume an attempt.
            observed[request.Id] = (opportunity, ChatPluginAutomaticInstallResult.ManualReview);
            Trim(observed);
            ChatPluginInstallApproval? approval;
            try
            {
                token.ThrowIfCancellationRequested();
                var body = await readAuthorization(request.Id, request.SessionId, token);
                token.ThrowIfCancellationRequested();
                if (current() != captured) return ChatPluginAutomaticInstallResult.WaitingForOwnerChat;
                approval = ReadApproval(body, request, captured);
                if (approval is null)
                {
                    var result = body.GetProperty("reason").GetString() == "source-run-not-completed"
                        ? ChatPluginAutomaticInstallResult.WaitingForSourceCompletion : ChatPluginAutomaticInstallResult.ManualReview;
                    observed[request.Id] = (opportunity, result); return result;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                return ChatPluginAutomaticInstallResult.ManualReview;
            }
            if (current() != captured) return ChatPluginAutomaticInstallResult.WaitingForOwnerChat;
            try
            {
                var outcome = await applyNormal(request, approval, token);
                if (outcome is null || outcome.RequestId != request.Id)
                    throw new InvalidDataException("The normal pipeline returned no correlated attempt classification.");
                if (outcome.State == ChatPluginInstallApplyState.NotStarted)
                {
                    var result = outcome.NotStartedReason switch
                    {
                        ChatPluginInstallNotStartedReason.WaitingForOwnerChat => ChatPluginAutomaticInstallResult.WaitingForOwnerChat,
                        ChatPluginInstallNotStartedReason.WaitingForSourceCompletion => ChatPluginAutomaticInstallResult.WaitingForSourceCompletion,
                        _ => ChatPluginAutomaticInstallResult.ManualReview
                    };
                    // Cache this finite opportunity, without consuming a future legitimate one.
                    observed[request.Id] = (opportunity, result);
                    return result;
                }
                var final = outcome.State == ChatPluginInstallApplyState.FinalOutcomeConfirmed
                    ? ChatPluginAutomaticInstallResult.FinalOutcomeConfirmed : ChatPluginAutomaticInstallResult.OutcomeUncertain;
                attempted[request.Id] = final; Trim(attempted);
                return final;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                attempted[request.Id] = ChatPluginAutomaticInstallResult.OutcomeUncertain; Trim(attempted);
                return ChatPluginAutomaticInstallResult.OutcomeUncertain;
            }
        }
        finally { inFlight.Remove(request.Id); }
    }

    private static void Trim<T>(Dictionary<string, T> values)
    {
        if (values.Count > 128) values.Remove(values.Keys.First());
    }

    internal static ChatPluginInstallApproval? ReadApproval(JsonElement body, InterfaceRequest request,
        ChatPluginInstallOwnerBinding selected)
    {
        ChatPluginInstallJson.Fields(body, "schemaVersion", "requestId", "conversationId", "profileId", "projectId",
            "automaticInstallAllowed", "permissionRevision", "ownerDecisionId", "packageFingerprint", "reason");
        if (body.GetProperty("schemaVersion").GetInt32() != 1 ||
            body.GetProperty("requestId").GetString() != request.Id ||
            body.GetProperty("conversationId").GetString() != selected.ConversationId ||
            body.GetProperty("profileId").GetString() != selected.ProfileId ||
            body.GetProperty("projectId").ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
            body.GetProperty("projectId").GetString() != selected.ProjectId)
            throw new InvalidDataException("Automatic-install permission belongs to a different request/chat/owner.");
        if (!body.GetProperty("automaticInstallAllowed").GetBoolean()) return null;
        var revision = body.GetProperty("permissionRevision").GetInt64();
        var decision = body.GetProperty("ownerDecisionId").GetString();
        var fingerprint = body.GetProperty("packageFingerprint").GetString();
        if (revision < 1 || !Guid.TryParseExact(decision, "N", out var parsed) || parsed == Guid.Empty ||
            fingerprint is not { Length: 64 } || fingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            fingerprint != request.PackageFingerprint || body.GetProperty("reason").GetString() != "owner-chat-permission")
            throw new InvalidDataException("Automatic-install permission has an invalid revision/provenance/package binding.");
        return new(request.Id, selected.ConversationId, selected.ProfileId, selected.ProjectId, revision, decision!, fingerprint);
    }
}
