namespace Cerebral.Tools;

public static class TextEdits
{
    public static string ReplaceOne(string content, string oldText, string newText)
    {
        if (oldText.Length == 0) throw new ArgumentException("Edit requires nonempty oldText.");
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            throw new ArgumentException("Edit requires newText to differ from oldText; an unchanged replacement is not a source edit.");
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
        {
            // Only bridge a uniform CRLF/LF representation difference. Never fuzz whitespace or code.
            var lf = content.Replace("\r\n", "\n");
            var uniformCrLf = content.Contains("\r\n") && !lf.Contains('\r') && lf.Count(x => x == '\n') == content.Count(x => x == '\r');
            var uniformLf = !content.Contains('\r');
            if (uniformCrLf || uniformLf)
            {
                var ending = uniformCrLf ? "\r\n" : "\n";
                oldText = oldText.Replace("\r\n", "\n").Replace("\n", ending);
                newText = newText.Replace("\r\n", "\n").Replace("\n", ending);
                index = content.IndexOf(oldText, StringComparison.Ordinal);
            }
        }
        if (index < 0)
            throw new ArgumentException("Edit rejected before mutation: oldText has 0 exact matches. Copy the exact current text from a bounded file.read; correct oldText or use file.write for a complete-file replacement. No file changed.");
        if (content.IndexOf(oldText, index + oldText.Length, StringComparison.Ordinal) >= 0)
            throw new ArgumentException("Edit rejected before mutation: oldText has multiple exact matches. Include enough surrounding text to identify one occurrence. No file changed.");
        // Bridging line endings can turn distinct operands into the same replacement.
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            throw new ArgumentException("Edit rejected before mutation: newline normalization makes the replacement unchanged. No file changed.");
        return content[..index] + newText + content[(index + oldText.Length)..];
    }

    /// <summary>Replaces an in-bounds range of physical LF-only lines. Terminators belong to
    /// their lines; a terminal LF does not introduce another empty line.</summary>
    internal static string ReplaceLines(string content, int startLine, int lineCount, string newText)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(newText);
        if (startLine is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(startLine), "Line edit startLine must be between 1 and 1000000.");
        if (lineCount is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(lineCount), "Line edit lineCount must be between 1 and 512.");
        if (content.Contains('\r') || content.Contains('\0'))
            throw new ArgumentException("Line edit requires LF-only source without NUL characters.", nameof(content));
        if (newText.Contains('\r') || newText.Contains('\0'))
            throw new ArgumentException("Line edit requires LF-only replacement text without NUL characters.", nameof(newText));

        var start = 0;
        for (var line = 1; line < startLine; line++)
        {
            var newline = content.IndexOf('\n', start);
            if (newline < 0)
                throw new ArgumentOutOfRangeException(nameof(startLine), "Line edit startLine is beyond the existing physical lines.");
            start = newline + 1;
        }
        if (start >= content.Length)
            throw new ArgumentOutOfRangeException(nameof(startLine), "Line edit requires an existing physical line; a terminal LF is not an extra line.");

        var end = start;
        for (var line = 0; line < lineCount; line++)
        {
            if (end >= content.Length)
                throw new ArgumentOutOfRangeException(nameof(lineCount), "Line edit range extends beyond the existing physical lines; ranges are not clamped.");
            var newline = content.IndexOf('\n', end);
            end = newline < 0 ? content.Length : newline + 1;
        }

        // Keep a following source line separate, and retain an existing terminal LF. Empty
        // replacement text deletes the selected lines and does not add a new separator.
        if (newText.Length > 0 && newText[^1] != '\n' &&
            (end < content.Length || content[end - 1] == '\n'))
            newText += "\n";
        var result = content[..start] + newText + content[end..];
        if (string.Equals(result, content, StringComparison.Ordinal))
            throw new ArgumentException("Line edit rejected before mutation: the replacement is unchanged. No file changed.", nameof(newText));
        return result;
    }
}
