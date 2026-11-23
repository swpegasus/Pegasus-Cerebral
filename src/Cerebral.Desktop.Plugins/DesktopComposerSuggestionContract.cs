namespace Cerebral.Desktop.Plugins;

/// <summary>
/// Optional, advisory completion for an empty main-user composer. The host supplies only the
/// latest final response body it displayed, never a conversation, activity stream or host handle.
/// Returning null declines. The host owns cancellation, presentation, identity checks and Tab;
/// a contribution cannot accept, send, focus or authorize anything through this contract.
/// </summary>
public interface IDesktopComposerSuggestionPlugin
{
    Task<string?> SuggestAsync(string latestVisibleAssistantResponse, CancellationToken cancellationToken);
}

public static class DesktopComposerSuggestionContract
{
    public const int MaximumResponseCharacters = 64_000;
    public const int MaximumSuggestionCharacters = 2_048;

    public static bool ValidSuggestion(string? text) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= MaximumSuggestionCharacters &&
        !text.Any(character => char.IsControl(character) && character is not ('\r' or '\n'));
}
