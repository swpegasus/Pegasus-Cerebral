using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Pure readable-source projection regressions. No file access, provider dispatch,
/// workflow execution, model, native runtime or network operation occurs.</summary>
public static class ToolReadableProjectionTests
{
    public static Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Readable tool projection: " + label);
            checks++;
        }

        const string path = @"C:\Fixture\source.cs";
        var hash = new string('a', 64);
        string Envelope(string content, int startLine, int totalLines) => JsonSerializer.Serialize(new
        {
            path,
            sha256 = hash,
            startLine,
            totalLines,
            content
        });
        ToolReadableProjection Project(string content, int startLine, int totalLines) =>
            ToolModelProjection.ReadableProjection("file.read", Envelope(content, startLine, totalLines));
        static string Source(ToolReadableProjection projection)
        {
            var marker = Environment.NewLine + "Source:" + Environment.NewLine;
            var position = projection.Text.IndexOf(marker, StringComparison.Ordinal);
            if (position < 0) throw new InvalidOperationException("Readable tool projection has no source header.");
            return projection.Text[(position + marker.Length)..];
        }
        static bool NoContinuation(ToolReadableProjection projection) =>
            projection.Text.Contains("Continuation: none; this requested source range is complete.", StringComparison.Ordinal) &&
            !projection.Text.Contains("Continuation: call file.read", StringComparison.Ordinal);

        // Preserve the producer's existing coordinates: its Split-based total includes the
        // terminal empty segment. Core must display all returned normalized text at those coordinates.
        var tailLines = Enumerable.Range(150, 26).Select(line => "source-line-" + line).ToArray();
        var tailSource = string.Join('\n', tailLines) + "\n";
        var tailEnvelope = Envelope(tailSource, 150, 176);
        var tail = ToolModelProjection.ReadableProjection("file.read", tailEnvelope);
        Check(tail.Complete && tail.Text.Contains("Displayed source lines: 150-176 of 176", StringComparison.Ordinal),
            "a terminal LF occupies its returned coordinate and does not leave a phantom unread tail");
        Check(Source(tail) == tailSource,
            "the entire returned tail, including its final LF, is displayed without clipping");
        Check(NoContinuation(tail),
            "the complete terminal tail requests no additional line 176 read");
        Check(tail.Text.Contains("Path: " + path, StringComparison.Ordinal) &&
              tail.Text.Contains("SHA-256: " + hash, StringComparison.Ordinal),
            "readable source retains the exact path and whole-file revision hash");

        var crlfTail = Project(string.Join("\r\n", tailLines) + "\r\n", 150, 176);
        Check(crlfTail.Complete && crlfTail.Text.Contains("Displayed source lines: 150-176 of 176", StringComparison.Ordinal),
            "CRLF normalization retains the same provider line coordinates as LF");
        Check(Source(crlfTail) == tailSource && !Source(crlfTail).Contains('\r'),
            "CRLF source is displayed as the full normalized LF body, including its final delimiter");
        Check(NoContinuation(crlfTail),
            "a fully displayed CRLF tail has no phantom continuation");

        const string blankSource = "alpha\n\nbeta\n\n";
        var blanks = Project(blankSource, 1, 5);
        Check(blanks.Complete && Source(blanks) == blankSource &&
              blanks.Text.Contains("Displayed source lines: 1-5 of 5", StringComparison.Ordinal) && NoContinuation(blanks),
            "interior and multiple terminal blank segments remain present and correctly counted");
        var unterminated = Project("alpha\nbeta", 1, 2);
        Check(unterminated.Complete && unterminated.Text.Contains("Displayed source lines: 1-2 of 2", StringComparison.Ordinal) &&
              NoContinuation(unterminated),
            "an unterminated final source line does not acquire an extra coordinate");
        Check(Source(unterminated) == "alpha\nbeta",
            "an unterminated returned body does not acquire a synthetic newline");

        // The native producer describes an empty string as one Split-based segment, not zero.
        var empty = Project("", 1, 1);
        Check(empty.Complete && Source(empty) == "" && NoContinuation(empty) &&
              empty.Text.Contains("Displayed source lines: 1-1 of 1", StringComparison.Ordinal),
            "an empty native-provider body stays empty and needs no follow-up");
        var newlineOnly = Project("\n", 1, 2);
        Check(newlineOnly.Complete && Source(newlineOnly) == "\n" && NoContinuation(newlineOnly) &&
              newlineOnly.Text.Contains("Displayed source lines: 1-2 of 2", StringComparison.Ordinal),
            "a newline-only body preserves both returned empty segments");

        // Complete describes the returned projection. Later file lines are only available;
        // their existence does not require another read of a fully displayed requested window.
        var firstWindow = Project("first line", 1, 2);
        Check(firstWindow.Complete && Source(firstWindow) == "first line" &&
              firstWindow.Text.Contains("Additional file lines 2-2 exist outside this returned range; read only if the accepted request needs them.", StringComparison.Ordinal) &&
              !firstWindow.Text.Contains("Continuation: call file.read", StringComparison.Ordinal),
            "a complete one-line requested window does not demand the next file line");
        var partial = Project("alpha\n", 10, 20);
        Check(partial.Complete && partial.Text.Contains("Displayed source lines: 10-11 of 20", StringComparison.Ordinal),
            "a complete returned page may still cover only part of the file");
        Check(partial.Text.Contains("Additional file lines 12-20 exist outside this returned range; read only if the accepted request needs them.", StringComparison.Ordinal) &&
              !partial.Text.Contains("Continuation: call file.read", StringComparison.Ordinal),
            "later file lines are descriptive after all returned segments, including the final blank segment");
        Check(Source(partial) == "alpha\n",
            "a mid-file page retains its final returned empty segment");

        // Each row is exactly 1000 ASCII characters. Five rows fit the 6 KiB serialized
        // projection allowance; six rows plus the required metadata do not.
        var largeLines = Enumerable.Range(41, 8).Select(line => $"row-{line:D2}:" + new string('x', 993)).ToArray();
        var bounded = Project(string.Join('\n', largeLines), 41, 48);
        Check(!bounded.Complete && bounded.Text.Contains("Displayed source lines: 41-45 of 48", StringComparison.Ordinal),
            "whole-line clipping reports the exact last line actually displayed");
        Check(Source(bounded) == string.Join('\n', largeLines.Take(5)),
            "the bounded display contains exactly five intact rows without a partial sixth row");
        Check(bounded.Text.Contains("Continuation: call file.read with startLine=46 and lineCount=3.", StringComparison.Ordinal),
            "clipped output points at the first undisplayed row with the exact remaining count");
        Check(JsonSerializer.SerializeToUtf8Bytes(bounded.Text).Length <= 6 * 1024,
            "the clipped readable source and its metadata fit the serialized UTF-8 display allowance");

        // Escaped non-ASCII scalars and literal backslashes fit as decoded UTF-8, but their
        // serialized representation must still respect the same display allowance.
        var unicodeUnit = string.Concat(Enumerable.Repeat("é😀\\", 50));
        var unicodeLines = Enumerable.Range(70, 8).Select(line => $"unicode-{line:D2}: " + unicodeUnit).ToArray();
        var unicodeSource = string.Join('\n', unicodeLines);
        var unicode = Project(unicodeSource, 70, 77);
        Check(!unicode.Complete && unicode.Text.Contains("Displayed source lines: 70-74 of 77", StringComparison.Ordinal) &&
              unicode.Text.Contains("Continuation: call file.read with startLine=75 and lineCount=3.", StringComparison.Ordinal),
            "Unicode and escaping pressure produce an exact whole-line continuation");
        Check(Source(unicode) == string.Join('\n', unicodeLines.Take(5)),
            "Unicode scalars and literal backslashes remain unchanged within each displayed row");
        Check(Encoding.UTF8.GetByteCount(unicodeSource) < 6 * 1024 &&
              JsonSerializer.SerializeToUtf8Bytes(unicodeSource).Length > 6 * 1024 &&
              JsonSerializer.SerializeToUtf8Bytes(unicode.Text).Length <= 6 * 1024,
            "the Unicode fixture exercises the serialized byte bound rather than only decoded text size");

        var plugin = ToolModelProjection.ReadableProjection("fixture.file.read", tailEnvelope);
        Check(plugin.Complete && plugin.Text == tailEnvelope,
            "a plugin-owned tool keeps its original JSON payload instead of inheriting file.read presentation");
        Check(ToolModelProjection.ReadablePayload("file.read", tailEnvelope) == tail.Text,
            "the compatibility readable-payload API returns the same complete terminal body");

        Console.WriteLine($"Tool readable projection: {checks} pure deterministic checks passed.");
        return Task.CompletedTask;
    }
}
