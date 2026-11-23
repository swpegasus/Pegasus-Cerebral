using Cerebral.Core;

namespace Cerebral.Orchestrator;

/// <summary>
/// UI-context discovery coalescing: one running pass and one latest distinct offered snapshot.
/// Offers during a pass return immediately because normal Apply may itself await a refresh.
/// Only offered completion/owner/request changes drive another pass; there is no polling.
/// </summary>
internal sealed class ChatPluginInstallDiscoveryGate(
    Func<ChatPluginInstallOwnerBinding?> currentOwner,
    Func<InterfaceRequest, string, CancellationToken, Task<ChatPluginAutomaticInstallResult>> handle,
    Action<ChatPluginAutomaticInstallResult>? report = null)
{
    private sealed record RequestIdentity(string Id, string SessionId, string ProfileId, string JobId,
        string? ClaimId, string? PackageFingerprint);

    private sealed record Pass(ChatPluginInstallOwnerBinding Owner, long Opportunity, InterfaceRequest[] Requests,
        RequestIdentity[] Identities, CancellationToken Token)
    {
        internal bool SameOpportunity(Pass other) => Owner == other.Owner && Opportunity == other.Opportunity &&
            Identities.SequenceEqual(other.Identities);
    }

    private bool running;
    private Pass? latestOffered;
    private Pass? pending;

    internal Task OfferAsync(ChatPluginInstallOwnerBinding owner, long opportunity,
        IReadOnlyList<InterfaceRequest> requests, IReadOnlySet<string> retainedOutcomeIds, CancellationToken token)
    {
        if (currentOwner() != owner || !owner.LocalNative || token.IsCancellationRequested) return Task.CompletedTask;
        var candidates = requests.Where(row => row.Kind == "plugin" && row.Source == "workflow" &&
                row.Status == "pending" && row.ClaimId is null && row.SessionId == owner.ConversationId &&
                row.ProfileId == owner.ProfileId && !retainedOutcomeIds.Contains(row.Id))
            .OrderBy(row => row.Id, StringComparer.Ordinal).Take(32).ToArray();
        var identities = candidates.Select(row => new RequestIdentity(row.Id, row.SessionId, row.ProfileId,
            row.JobId, row.ClaimId, row.PackageFingerprint)).ToArray();
        var offered = new Pass(owner, opportunity, candidates, identities, token);
        if (latestOffered is not null && latestOffered.SameOpportunity(offered)) return Task.CompletedTask;
        latestOffered = offered;
        pending = offered; // supersede an older queued snapshot, never the active claim/effect
        if (running) return Task.CompletedTask;
        running = true;
        return DrainAsync();
    }

    private async Task DrainAsync()
    {
        try
        {
            while (pending is { } pass)
            {
                pending = null;
                if (pass.Token.IsCancellationRequested || currentOwner() != pass.Owner) continue;
                foreach (var request in pass.Requests)
                {
                    if (pass.Token.IsCancellationRequested || currentOwner() != pass.Owner) break;
                    var result = await handle(request, "refresh-" + pass.Opportunity, pass.Token);
                    // The old await yielded. A newer legitimate opportunity owns the next pass
                    // and must re-read current source/permission rather than inherit this response.
                    if (pending is not null || currentOwner() != pass.Owner) break;
                    report?.Invoke(result);
                }
            }
        }
        finally { running = false; }
    }
}
