using Cerebral.Desktop.Plugins;

namespace Cerebral.Dictation;

/// <summary>A dictated segment owns one captured text/caret span; ordinary selection is never permission to delete text.</summary>
internal sealed record DictatedTextInsertion(string Text, int Start, int Length)
{
    internal static string JoinSegments(string existing, string recognized)
    {
        var words = recognized.Trim();
        if (words.Length == 0) return existing;
        if (existing.Length == 0) return words;
        var previous = existing[^1];
        var space = !char.IsWhiteSpace(previous) && !IsClosingPunctuation(words[0]) && !IsOpeningPunctuation(previous);
        return existing + (space ? " " : "") + words;
    }

    internal static DictatedTextInsertion Capture(IDesktopTextEntry target)
    {
        var text = target.Text;
        var start = target.SelectionStart;
        var length = target.SelectionLength;
        if (start < 0 || length < 0 || start > text.Length - length)
            throw new InvalidOperationException("The dictation cursor is unavailable.");
        return new(text, start, length);
    }

    internal bool Matches(IDesktopTextEntry target) =>
        target.Text == Text && target.SelectionStart == Start && target.SelectionLength == Length;

    internal string Prepare(string recognized) => PrepareText(recognized.Trim());
    internal string PrepareReviewed(string reviewed) => PrepareText(reviewed);

    private string PrepareText(string words)
    {
        if (string.IsNullOrWhiteSpace(words)) return "";
        // InsertAsync replaces a selection. Include its exact original contents so selecting all
        // during pause/resume cannot discard a previous segment or text written by the user.
        var preserved = Text.Substring(Start, Length);
        var insertionPoint = Start + Length;
        var previous = insertionPoint > 0 ? Text[insertionPoint - 1] : '\0';
        var next = insertionPoint < Text.Length ? Text[insertionPoint] : '\0';
        var leading = previous != '\0' && !char.IsWhiteSpace(previous) &&
            !char.IsWhiteSpace(words[0]) && !IsClosingPunctuation(words[0]) && !IsOpeningPunctuation(previous);
        var trailing = next != '\0' && !char.IsWhiteSpace(next) && !char.IsWhiteSpace(words[^1]) &&
            !IsClosingPunctuation(next) && !IsOpeningPunctuation(words[^1]);
        return preserved + (leading ? " " : "") + words + (trailing ? " " : "");
    }

    private static bool IsOpeningPunctuation(char value) => value is '(' or '[' or '{' or '“' or '‘';
    private static bool IsClosingPunctuation(char value) => value is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '”' or '’';
}
