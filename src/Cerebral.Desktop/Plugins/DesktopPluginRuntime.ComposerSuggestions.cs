namespace Cerebral.Desktop.Plugins;

public sealed partial class DesktopPluginRuntime
{
    public async Task<string?> SuggestComposerAsync(string latestVisibleAssistantResponse, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        if (disposed || host.Surface != DesktopPluginSurface.Orchestrator ||
            string.IsNullOrWhiteSpace(latestVisibleAssistantResponse) ||
            latestVisibleAssistantResponse.Length > DesktopComposerSuggestionContract.MaximumResponseCharacters) return null;
        // Ambiguous ownership is disabled rather than combining suggestions or running multiple agents.
        var candidates = loaded.Values.Where(item => item.Plugin is IDesktopComposerSuggestionPlugin).Take(2).ToArray();
        if (candidates.Length != 1) return null;
        var owner = candidates[0];
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(45));
        linked.Token.ThrowIfCancellationRequested();
        var result = await ((IDesktopComposerSuggestionPlugin)owner.Plugin).SuggestAsync(latestVisibleAssistantResponse, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        owner.Invocations.RequireActive();
        return DesktopComposerSuggestionContract.ValidSuggestion(result) ? result : null;
    }
}
