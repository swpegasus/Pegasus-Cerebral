using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private ChatPluginInstallAutomaticCoordinator? chatPluginInstallAutomatic;
    private ChatPluginInstallDiscoveryGate? chatPluginInstallDiscovery;
    private long chatPluginInstallOpportunity;
    private readonly Dictionary<string, long> chatPluginInstallCompletedRuns = new(StringComparer.Ordinal);

    private ChatPluginInstallOwnerBinding? CurrentChatPluginInstallOwner()
    {
        if (!IsConnected || !ServiceIsLocal || !IsNativeChat || CurrentConversationId is not { } id ||
            string.IsNullOrWhiteSpace(ServiceConnectionIdentity) || interfaceOutcomeServiceKey is null) return null;
        try
        {
            return new(id, ConversationProjectId, ConversationProfile().Id, ServiceConnectionIdentity,
                knowledgeConnectionVersion, conversationSelectionVersion, LocalNative: true, ProfileRevision: ConversationProfile().Revision);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return null; }
    }

    private Task<ChatPluginAutomaticInstallResult> TryChatAuthorizedPluginOpportunityAsync(InterfaceRequest request, string opportunity)
    {
        chatPluginInstallAutomatic ??= new(CurrentChatPluginInstallOwner,
            async (id, chat, token) =>
            {
                using var owner = CreateIndependentClient();
                return await owner.GetAsync<JsonElement>("interface.install-authorization.get",
                    new { id, sessionId = chat }, cancellationToken: token);
            },
            ApplyChatAuthorizedPluginAsync,
            id => interfaceOutcomeServiceKey is { } key && interfaceOutcomeJournal.Read(key, id) is not null);
        return chatPluginInstallAutomatic.HandleAsync(request, legitimateOpportunity: true, Lifetime.Token, opportunity);
    }

    private bool ObserveChatPluginInstallCompletion(ServiceEvent item)
    {
        if (item.Type != "workflow.updated" || item.Data.ValueKind != JsonValueKind.Object ||
            !item.Data.TryGetProperty("conversationId", out var chat) || chat.GetString() != CurrentConversationId ||
            !item.Data.TryGetProperty("id", out var id) || id.GetString() is not { } runId || item.JobId != runId ||
            !item.Data.TryGetProperty("revision", out var revision) || !revision.TryGetInt64(out var next) ||
            !item.Data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<WorkflowStatus>(status.GetString(), ignoreCase: true, out var parsed) || parsed != WorkflowStatus.Completed)
            return false;
        if (chatPluginInstallCompletedRuns.TryGetValue(runId, out var prior) && next <= prior) return false;
        chatPluginInstallCompletedRuns[runId] = next;
        if (chatPluginInstallCompletedRuns.Count > 128) chatPluginInstallCompletedRuns.Remove(chatPluginInstallCompletedRuns.Keys.First());
        chatPluginInstallOpportunity = checked(chatPluginInstallOpportunity + 1);
        return true;
    }

    private Task TryChatAuthorizedPendingPluginsAsync(IReadOnlyList<InterfaceRequest> requests,
        IReadOnlySet<string> retainedOutcomeIds)
    {
        if (CurrentChatPluginInstallOwner() is not { } selected) return Task.CompletedTask;
        chatPluginInstallDiscovery ??= new(CurrentChatPluginInstallOwner,
            (request, opportunity, _) => TryChatAuthorizedPluginOpportunityAsync(request, opportunity),
            result =>
            {
                if (result == ChatPluginAutomaticInstallResult.WaitingForSourceCompletion)
                    SetStatus("Chat-authorized plugin installation is waiting for its source run to complete.");
            });
        return chatPluginInstallDiscovery.OfferAsync(selected, chatPluginInstallOpportunity,
            requests, retainedOutcomeIds, Lifetime.Token);
    }

    private Task<ChatPluginInstallApplyResult> ApplyChatAuthorizedPluginAsync(InterfaceRequest request, ChatPluginInstallApproval approval,
        CancellationToken token)
    {
        var selected = CurrentChatPluginInstallOwner();
        bool Current() => selected is not null && CurrentChatPluginInstallOwner() == selected;
        InterfaceReviewStateIdentity? identity = null;
        return ChatPluginInstallPreflight.RunAsync(request, Current,
            async () =>
            {
                if (selected is null || approval.RequestId != request.Id || approval.ConversationId != selected.ConversationId ||
                    approval.ProfileId != selected.ProfileId || approval.ProjectId != selected.ProjectId ||
                    approval.PackageFingerprint != request.PackageFingerprint)
                    throw new InvalidOperationException("The chat installation approval changed.");
                var sourceRun = await ReadPendingPluginSourceRunAsync(request);
                return new(sourceRun?.Status,
                    sourceRun is not null && PendingPluginSourceReview(request, sourceRun, selected.ProjectId).CanInstall);
            },
            () =>
            {
                var manifest = desktopPluginCatalog.Inspect(request.Path);
                var installed = desktopPluginCatalog.List().SingleOrDefault(item => item.Id == manifest.Id);
                identity = InterfaceReviewStateIdentity.Create(request, PluginBinding(), ServiceConnectionIdentity,
                    knowledgeConnectionVersion, InterfaceReviewAction(request, false), manifest, installed, null);
            },
            () => ApplyInterfaceRequest(request, nativePluginApproved: true,
                explicitOwnerSessionId: selected!.ConversationId, controlTestsAllowed: false, activateWindow: false,
                expectedReviewIdentity: identity, currentOwner: Current, reviewToken: token,
                chatInstallAuthorization: approval));
    }

    private async Task RequireChatPluginInstallEffectAsync(CerebralClient owner, InterfaceRequest request,
        string claimId, ChatPluginInstallApproval approval, CancellationToken token)
    {
        // A successful Worker recheck is this action's in-flight admission boundary. A later
        // owner Save(false) blocks subsequent admissions, but cannot retroactively cancel this one.
        // Reserved-candidate and immutable copy-time package checks remain mandatory afterward.
        var result = await owner.GetAsync<JsonElement>("interface.install-authorization.recheck", new
        {
            id = request.Id, sessionId = request.SessionId, claimId,
            permissionRevision = approval.PermissionRevision, decisionId = approval.DecisionId
        }, cancellationToken: token);
        ChatPluginInstallJson.Fields(result, "schemaVersion", "id", "sessionId", "claimId", "permissionRevision", "authorized");
        if (result.GetProperty("schemaVersion").GetInt32() != 1 ||
            result.GetProperty("id").GetString() != request.Id ||
            result.GetProperty("sessionId").GetString() != request.SessionId ||
            result.GetProperty("claimId").GetString() != claimId ||
            result.GetProperty("permissionRevision").GetInt64() != approval.PermissionRevision ||
            !result.GetProperty("authorized").GetBoolean())
            throw new InvalidOperationException("The owner no longer authorizes automatic installation for this exact claim.");
    }
}
