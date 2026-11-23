using System.Windows.Controls;
using System.Windows.Input;

namespace Cerebral.Orchestrator;

internal static class ComposerSuggestionInput
{
    internal static bool TryAccept(TextBox composer, Key key, ModifierKeys modifiers, bool compositionActive,
        bool alreadyHandled, ComposerSuggestionState state, ComposerSuggestionAnchor? anchor, bool available)
    {
        if (alreadyHandled || key != Key.Tab || modifiers != ModifierKeys.None || compositionActive || anchor is null) return false;
        if (!state.Take(anchor, available && composer.Text.Length == 0 && composer.IsEnabled && !composer.IsReadOnly, out var text)) return false;
        // A normal text edit is the only effect. In particular this helper has no submission or focus callback.
        composer.Text = text!; composer.CaretIndex = composer.Text.Length;
        return true;
    }
}
