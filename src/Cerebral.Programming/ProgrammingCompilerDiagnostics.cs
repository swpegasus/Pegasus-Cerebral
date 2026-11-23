using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Contracts;
using Cerebral.Core;

namespace Cerebral.Programming;

// Shared by Develop's observation/repair policy and the store-free context contribution.
// Completeness refers to intake/rendering, never compiler success or semantic repair.
internal sealed record ProgrammingCompilerDiagnostic(string Code, string Message, string? Path,
    int? Line, int? Column, string? ReportedProject);
internal sealed record ProgrammingCompilerBatch(IReadOnlyList<ProgrammingCompilerDiagnostic> Diagnostics,
    int? Total, bool Complete, string? Fingerprint, string? Reason)
{
    public int Shown => Diagnostics.Count;
}
internal static class ProgrammingCompilerDiagnostics
{
    internal const int MaximumInputCharacters = 64 * 1024;
    internal const int MaximumDiagnostics = 16;
    internal const int MaximumRowCharacters = 2048;
    private static readonly Regex Error = new(
        @"^(?:(?<origin>CSC|[^\r\n]+\([0-9]+(?:,[0-9]+){0,3}\))\s*:\s*)?error\s+(?<code>CS[0-9]{4})\s*:\s*(?<message>\S.*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex ErrorMarker = new(@"(?:^|:)\s*error\s+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Location = new(@"^(?<path>.+\.cs)\((?<line>[0-9]{1,7}),(?<column>[0-9]{1,7})\)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex MissingAssets = new(
        @"^.+\.targets\([0-9]+,[0-9]+\):\s*error NETSDK1004:\s*Assets file '(?<assets>.+[\\/]project\.assets\.json)' not found\. Run a NuGet package restore to generate this file\. \[(?<project>.+\.csproj)\]$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    internal static ProgrammingCompilerBatch Parse(string content, int exitCode, bool incomplete = false)
        => Parse(content, exitCode, incomplete, out _);

    // The extra output is populated only after the existing process-envelope completeness checks.
    // It lets the narrow SDK prerequisite use the same child-output admission as C# diagnostics.
    private static ProgrammingCompilerBatch Parse(string content, int exitCode, bool incomplete, out string? completeProcessText)
    {
        completeProcessText = null;
        if (content.Length > MaximumInputCharacters) return new([], null, false, null, "compiler-input-character-bound");
        var text = content;
        var reason = incomplete ? "partial-compiler-receipt" : null;
        if (content.TrimStart().StartsWith('{'))
        {
            try
            {
                var root = ProgrammingEvidence.Read(content);
                if (root.TryGetProperty("data", out var nested))
                {
                    if (root.TryGetProperty("partial", out var partial) && partial.ValueKind != JsonValueKind.False)
                        reason ??= "partial-process-envelope";
                    root = nested;
                }
                if (root.ValueKind != JsonValueKind.Object) return new([], null, false, null, "unsupported-process-envelope");
                foreach (var flag in new[] { "out_truncated", "err_truncated", "timed_out" })
                    if (root.TryGetProperty(flag, out var value) && value.ValueKind != JsonValueKind.False)
                        reason ??= "partial-or-timeout-process-envelope";
                var native = root.TryGetProperty("stdout", out var stdout);
                if ((!native && !root.TryGetProperty("out", out stdout)) || stdout.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty(native ? "exitCode" : "exit", out var code) || code.ValueKind != JsonValueKind.Number ||
                    !code.TryGetInt32(out var parsed) || parsed != exitCode)
                    return new([], null, false, null, "unsupported-process-envelope");
                var stderr = "";
                if (native)
                {
                    if (root.TryGetProperty("stderr", out var error))
                    {
                        if (error.ValueKind != JsonValueKind.String) return new([], null, false, null, "unsupported-process-envelope");
                        stderr = error.GetString()!;
                    }
                    else reason ??= "missing-process-stderr";
                }
                else
                {
                    // PAO's current complete child stream is err_tail; older receipts used err.
                    // Never borrow the wrapper's broker error or choose between conflicting aliases.
                    var hasTail = root.TryGetProperty("err_tail", out var tail);
                    var hasError = root.TryGetProperty("err", out var error);
                    if (hasTail && tail.ValueKind != JsonValueKind.String || hasError && error.ValueKind != JsonValueKind.String)
                        return new([], null, false, null, "unsupported-process-envelope");
                    if (hasTail && hasError && !string.Equals(tail.GetString(), error.GetString(), StringComparison.Ordinal))
                        return new([], null, false, null, "conflicting-process-stderr");
                    if (hasTail) stderr = tail.GetString()!;
                    else if (hasError) stderr = error.GetString()!;
                    else reason ??= "missing-process-stderr";
                }
                if (!native)
                    foreach (var flag in new[] { "out_truncated", "err_truncated", "timed_out" })
                        if (!root.TryGetProperty(flag, out _)) reason ??= "missing-process-completeness";
                text = stdout.GetString() + "\n" + stderr;
            }
            catch (Exception error) when (ProgrammingEvidence.Invalid(error))
            { return new([], null, false, null, "invalid-process-envelope"); }
        }
        else if (content.TrimStart().StartsWith('[')) return new([], null, false, null, "unsupported-process-envelope");
        if (reason is null && content.TrimStart().StartsWith('{')) completeProcessText = text;
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ProgrammingCompilerDiagnostic>();
        var scanComplete = reason is null;
        try
        {
            foreach (var original in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = original.Trim();
                var match = Error.Match(line);
                // The supported grammar itself selects rows; a narrower whitespace prefilter
                // could silently drop a supported error while still claiming a complete batch.
                if (!match.Success && !ErrorMarker.IsMatch(line)) continue;
                if (original.Length > MaximumRowCharacters)
                { scanComplete = false; reason ??= "compiler-row-character-bound"; continue; }
                if (!match.Success)
                { scanComplete = false; reason ??= "unsupported-compiler-diagnostic"; continue; }
                if (!signatures.Add(line)) continue;
                if (rows.Count == MaximumDiagnostics) { reason ??= "compiler-diagnostic-count-bound"; continue; }
                var detail = match.Groups["message"].Value;
                string? project = null;
                var suffix = detail.LastIndexOf(" [", StringComparison.Ordinal);
                if (suffix >= 0 && detail.EndsWith(".csproj]", StringComparison.OrdinalIgnoreCase))
                { project = detail[(suffix + 2)..^1]; detail = detail[..suffix]; }
                var location = Location.Match(match.Groups["origin"].Value);
                int? first = null, column = null;
                string? path = null;
                if (location.Success && int.TryParse(location.Groups["line"].Value, out var lineNumber) && lineNumber > 0 &&
                    int.TryParse(location.Groups["column"].Value, out var columnNumber) && columnNumber > 0)
                { path = location.Groups["path"].Value; first = lineNumber; column = columnNumber; }
                rows.Add(new(match.Groups["code"].Value, detail, path, first, column, project));
            }
        }
        catch (RegexMatchTimeoutException)
        { scanComplete = false; reason ??= "compiler-diagnostic-scan-timeout"; }
        if (signatures.Count == 0 && exitCode != 0)
        { scanComplete = false; reason ??= "no-supported-compiler-diagnostics"; }
        var total = scanComplete ? signatures.Count : (int?)null;
        var complete = scanComplete && rows.Count == signatures.Count;
        var fingerprint = complete && rows.Count != 0
            ? ProgrammingEvidence.Hash(JsonSerializer.Serialize(signatures.Order(StringComparer.Ordinal).ToArray(), ProgrammingEvidence.Json)) : null;
        return new(rows.AsReadOnly(), total, complete, fingerprint, complete ? null : reason ?? "incomplete-compiler-batch");
    }

    // A model assertion, wrapper error, raw text, partial process stream, or a different
    // project's missing assets cannot establish the prerequisite failure for this build.
    internal static string? MissingAssetsFingerprint(string content, int exitCode, string acceptedProject)
    {
        if (exitCode == 0 || content.Length > MaximumInputCharacters ||
            !Path.GetExtension(acceptedProject).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) return null;
        _ = Parse(content, exitCode, incomplete: false, out var childOutput);
        if (childOutput is null) return null;
        var expectedAssets = Path.Combine(Path.GetDirectoryName(acceptedProject)!, "obj", "project.assets.json");
        var found = false;
        try
        {
            foreach (var original in childOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = original.Trim();
                if (!ErrorMarker.IsMatch(line)) continue;
                if (original.Length > MaximumRowCharacters) return null;
                var match = MissingAssets.Match(line);
                if (!match.Success || !ProgrammingEvidence.SamePath(match.Groups["project"].Value, acceptedProject) ||
                    !ProgrammingEvidence.SamePath(match.Groups["assets"].Value, expectedAssets)) return null;
                found = true;
            }
        }
        catch (Exception error) when (error is RegexMatchTimeoutException or ArgumentException or IOException)
        { return null; }
        return found ? ProgrammingEvidence.Hash("NETSDK1004|" + Path.GetFullPath(acceptedProject) + "|" + expectedAssets) : null;
    }
}

internal static class ProgrammingEvidence
{
    internal static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };
    internal static JsonElement Read(string text, int depth = 32)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = depth });
        Unique(document.RootElement);
        return document.RootElement.Clone();
    }
    internal static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new ArgumentException("Duplicate evidence property."); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    internal static string Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString()! : throw new ArgumentException(key + " is required.");
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static bool Within(string path, string directory) => SamePath(path, directory) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static bool Invalid(Exception error) => error is JsonException or ArgumentException or InvalidOperationException or
        IOException or NotSupportedException or OverflowException;
}

