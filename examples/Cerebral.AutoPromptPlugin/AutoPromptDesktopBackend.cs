using Cerebral.Desktop.Plugins;

namespace Cerebral.AutoPromptPlugin;

internal sealed class AutoPromptDesktopBackend(IDesktopIsolatedTextGenerationHost host) : IAutoPromptGenerationBackend
{
    public async Task<string?> GenerateAsync(AutoPromptGeneration request, CancellationToken token)
    {
        var result = await host.GenerateIsolatedTextAsync(new(request.ProfileId, request.ProfileRevision, "autoprompt",
            request.SystemInstruction, request.LatestVisibleAssistantResponse, request.MaximumOutputTokens), token);
        token.ThrowIfCancellationRequested();
        if (result.State != DesktopOperationState.Completed)
            throw new InvalidOperationException(result.Message ?? "AutoPrompt is unavailable. Check the current profile, loaded model and work hold.");
        return result.Text;
    }
}
