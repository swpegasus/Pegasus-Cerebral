using System.Reflection;
using System.Runtime.ExceptionServices;
using Cerebral.Tools;

/// <summary>Pure physical-line splice fixtures. No filesystem, provider, model, native runtime,
/// workflow or network operation occurs.</summary>
internal static class RangeTextEditTests
{
    public static Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Range text edit: " + label);
            checks++;
        }
        var method = typeof(TextEdits).GetMethod("ReplaceLines", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, types: [typeof(string), typeof(int), typeof(int), typeof(string)], modifiers: null)
            ?? throw new InvalidOperationException("Range text edit: the internal pure helper is unavailable.");
        string Edit(string source, int startLine, int lineCount, string replacement)
        {
            try { return (string)method.Invoke(null, [source, startLine, lineCount, replacement])!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        void Refuse(string source, int startLine, int lineCount, string replacement, string label)
        {
            try { _ = Edit(source, startLine, lineCount, replacement); }
            catch (ArgumentException) { Check(true, label); return; }
            throw new InvalidOperationException("Range text edit accepted invalid input: " + label);
        }

        Check(!method.IsPublic && method.IsAssembly &&
              typeof(TextEdits).GetMethod("ReplaceLines", BindingFlags.Static | BindingFlags.Public) is null,
            "range splicing remains internal to Tools and does not add a public SDK method");
        Check(Edit("A\nB\nC", 1, 1, "X") == "X\nB\nC",
            "first-line replacement preserves the following lines and their exact separators");
        Check(Edit("A\nB\nC", 2, 1, "X") == "A\nX\nC",
            "middle-line replacement adds the separator needed before a surviving suffix");
        Check(Edit("A\nB\nC", 3, 1, "X") == "A\nB\nX",
            "an unterminated last line remains unterminated when replacement omits LF");
        Check(Edit("A\nB\nC\n", 3, 1, "X") == "A\nB\nX\n",
            "a terminated last line retains its LF when replacement omits it");
        Check(Edit("A\nB\nC", 3, 1, "X\n") == "A\nB\nX\n",
            "an explicit replacement LF may terminate a previously unterminated EOF");
        Check(Edit("A\nB\nC", 2, 1, "X\n") == "A\nX\nC",
            "an explicit replacement separator is not doubled before the suffix");
        Check(Edit("A\nB\nC\nD", 2, 2, "X\nY") == "A\nX\nY\nD",
            "a multi-line replacement substitutes the entire selected physical range");
        Check(Edit("A\nB\nC", 2, 2, "X\nY") == "A\nX\nY",
            "a range ending at unterminated EOF does not gain a synthetic terminator");
        Check(Edit("A\nB\nC\n", 1, 3, "X") == "X\n",
            "whole-file replacement retains the selected final terminator");
        Check(Edit("A\nB\nC", 1, 3, "X") == "X",
            "whole-file replacement of unterminated text remains unterminated");
        Check(Edit("A\nB\nC", 1, 1, "") == "B\nC",
            "deleting the first line also removes its terminator");
        Check(Edit("A\nB\nC", 2, 1, "") == "A\nC",
            "deleting a middle line joins the untouched prefix and suffix without an extra blank");
        Check(Edit("A\nB\nC\n", 3, 1, "") == "A\nB\n",
            "deleting a terminated last line adds no replacement separator");
        Check(Edit("A\nB\nC", 1, 3, "") == "",
            "deleting every existing physical line produces empty text");
        Check(Edit("\n", 1, 1, "X") == "X\n",
            "a newline-only file contains one replaceable terminated empty line");
        Check(Edit("A\n\n", 2, 1, "X") == "A\nX\n",
            "a second LF represents a real second empty line rather than an EOF artifact");
        Check(Edit("A\n\n", 2, 1, "") == "A\n",
            "deleting the real final blank line preserves the preceding terminator");
        Check(Edit("  keep\t\n\told  \n  suffix\t", 2, 1, "\tnew  ") == "  keep\t\n\tnew  \n  suffix\t",
            "leading and trailing whitespace outside and inside replacement text stays exact");
        Check(Edit("é😀\nβ\n終", 2, 1, "雪😀") == "é😀\n雪😀\n終",
            "Unicode scalars and surrogate pairs do not change physical-line coordinates or untouched text");
        Check(Edit("same\nsame\nsame", 2, 1, "different") == "same\ndifferent\nsame",
            "a positional range can distinguish identical source lines without oldText matching");

        Refuse("", 1, 1, "X", "an empty file has zero physical lines and is not an insertion target");
        Refuse("A\n", 2, 1, "X", "a terminal LF does not create another editable physical line");
        Refuse("A\n\n", 3, 1, "X", "a real final blank line still has no additional phantom successor");
        Refuse("A\nB", 1, 3, "X", "a range extending past unterminated EOF is rejected rather than clamped");
        Refuse("A\nB\n", 2, 2, "X", "a range extending past terminated EOF is rejected rather than clamped");
        Refuse("A", 0, 1, "X", "startLine zero is rejected");
        Refuse("A", -1, 1, "X", "negative startLine is rejected");
        Refuse(new string('\n', 1_000_000) + "A", 1_000_001, 1, "X",
            "startLine above the declared limit is rejected even when that physical line exists");
        Refuse("A", int.MaxValue, 1, "X", "large startLine cannot overflow into an admitted range");
        Refuse("A", 1, 0, "X", "lineCount zero cannot request implicit insertion");
        Refuse("A", 1, -1, "X", "negative lineCount is rejected");
        Refuse(new string('\n', 513), 1, 513, "X",
            "lineCount above 512 is rejected even when all requested physical lines exist");
        Refuse("A", 1, int.MaxValue, "X", "large lineCount cannot overflow into an admitted range");
        Refuse("A\r\nB", 1, 1, "X", "CRLF source is rejected rather than silently normalized");
        Refuse("A\nB\r\n", 1, 1, "X", "CR outside the selected range still rejects the entire source");
        Refuse("A\rB", 1, 1, "X", "bare CR source is rejected");
        Refuse("A\nB", 1, 1, "X\r\n", "CRLF replacement is rejected rather than normalized");
        Refuse("A\nB", 1, 1, "X\r", "bare CR replacement is rejected");
        Refuse("A\nB\0", 1, 1, "X", "NUL outside the selected range still rejects the entire source");
        Refuse("A\nB", 1, 1, "X\0", "NUL in replacement is rejected");
        Refuse("A\nB", 1, 1, "A", "an automatically terminated unchanged non-EOF replacement is rejected");
        Refuse("A\nB\n", 2, 1, "B", "an automatically terminated unchanged EOF replacement is rejected");
        Refuse("A\nB\n", 2, 1, "B\n", "an explicitly identical terminated replacement is rejected");
        Refuse("A\nB", 2, 1, "B", "an identical unterminated EOF replacement is rejected");
        Refuse("A\n\n", 2, 1, "\n", "an unchanged real blank line is rejected");

        var maximumCountSource = new string('\n', 512);
        Check(Edit(maximumCountSource, 1, 512, "X") == "X\n",
            "the maximum positive line count is accepted for 512 actual terminated lines");
        var maximumStartPrefix = new string('\n', 999_999);
        Check(Edit(maximumStartPrefix + "last", 1_000_000, 1, "next") == maximumStartPrefix + "next",
            "the maximum startLine is accepted when that physical line actually exists");

        Console.WriteLine($"Range text edit: {checks} pure deterministic checks passed.");
        return Task.CompletedTask;
    }
}
