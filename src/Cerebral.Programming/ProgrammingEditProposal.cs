using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.Programming;

// Transient request data only. Durable preparation retains Fingerprint, never NewText.
internal sealed record ProgrammingEditProposal(int StartLine, int LineCount, string NewText)
{
    internal const int MaximumTextBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static ProgrammingEditProposal Read(JsonElement args, bool closedShape = true)
    {
        string[] fields = ["startLine", "lineCount", "newText"];
        if (args.ValueKind != JsonValueKind.Object ||
            closedShape && (args.EnumerateObject().Count() != fields.Length ||
                args.EnumerateObject().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal))) ||
            args.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != args.EnumerateObject().Count())
            throw new ArgumentException("proposedEdit requires only startLine, lineCount and newText, without duplicate fields.");
        if (!args.TryGetProperty("startLine", out var start) || start.ValueKind != JsonValueKind.Number ||
            !start.TryGetInt32(out var first) || first is < 1 or > 1_000_000 ||
            !args.TryGetProperty("lineCount", out var count) || count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt32(out var length) || length is < 1 or > 512)
            throw new ArgumentException("The proposed range requires startLine 1..1000000 and lineCount 1..512.");
        if (!args.TryGetProperty("newText", out var text) || text.ValueKind != JsonValueKind.String)
            throw new ArgumentException("The proposed range requires string newText; an empty string deletes the selected lines.");
        string replacement;
        try
        {
            replacement = text.GetString()!;
            if (Utf8.GetByteCount(replacement) > MaximumTextBytes)
                throw new ArgumentException("The proposed range newText exceeds 64 KiB of strict UTF-8.");
        }
        catch (Exception error) when (error is EncoderFallbackException or InvalidOperationException)
        {
            throw new ArgumentException("The proposed range newText must be well-formed Unicode.", error);
        }
        return new(first, length, replacement);
    }

    // Use the same lexical full-path identity as the scoped reader and harness after admission.
    // This does not resolve links, widen scope or replace the current preimage check.
    internal ProgrammingEditFingerprint Fingerprint(string path, string sourceSha256) =>
        new(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)), sourceSha256.ToLowerInvariant(), StartLine, LineCount,
            Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(NewText))).ToLowerInvariant());
}

internal sealed record ProgrammingEditFingerprint(string Path, string SourceSha256, int StartLine,
    int LineCount, string NewTextSha256)
{
    internal bool Matches(ProgrammingEditFingerprint? other) => other is not null &&
        string.Equals(Path, other.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        SourceSha256 == other.SourceSha256 && StartLine == other.StartLine &&
        LineCount == other.LineCount && NewTextSha256 == other.NewTextSha256;

    internal bool Matches(JsonElement value)
    {
        string[] fields = ["path", "sourceSha256", "startLine", "lineCount", "newTextSha256"];
        return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == fields.Length &&
            value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields) &&
            value.GetProperty("path").ValueKind == JsonValueKind.String &&
            string.Equals(Path, value.GetProperty("path").GetString(),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            value.GetProperty("sourceSha256").ValueKind == JsonValueKind.String &&
            SourceSha256.Equals(value.GetProperty("sourceSha256").GetString(), StringComparison.OrdinalIgnoreCase) &&
            value.GetProperty("startLine").TryGetInt32(out var start) && start == StartLine &&
            value.GetProperty("lineCount").TryGetInt32(out var count) && count == LineCount &&
            value.GetProperty("newTextSha256").ValueKind == JsonValueKind.String &&
            NewTextSha256.Equals(value.GetProperty("newTextSha256").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
