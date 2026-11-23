using System.Text;
using System.Text.Json;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher
{
    private static readonly UTF8Encoding RangeEditUtf8 = new(false, true);

    private static (int StartLine, int LineCount, string NewText, string ExpectedSha256) RangeEditArguments(JsonElement args)
    {
        if (args.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != args.EnumerateObject().Count())
            throw new ArgumentException("Range-edit arguments must not contain duplicate fields.");
        var sha = ToolPolicy.Text(args, "expectedSha256");
        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)) throw new ArgumentException("expectedSha256 must be the current whole-file SHA-256.");
        var text = ToolPolicy.Text(args, "newText");
        if (RangeEditUtf8.GetByteCount(text) > 64 * 1024) throw new ArgumentException("Range replacement exceeds 64 KiB UTF-8.");
        if (text.Contains('\r') || text.Contains('\0')) throw new ArgumentException("Range replacement supports LF-only text without NUL; no newline normalization is performed.");
        return (ToolPolicy.Number(args, "startLine", 0, 1, 1_000_000), ToolPolicy.Number(args, "lineCount", 0, 1, 512), text, sha);
    }

    private static async Task ValidateRangeEditSourceAsync(string path,
        (int StartLine, int LineCount, string NewText, string ExpectedSha256) range, CancellationToken ct)
    {
        var bytes = await ReadBoundedAsync(path, ct);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) throw new ArgumentException("Range editing supports UTF-8 without BOM only.");
        var source = RangeEditUtf8.GetString(bytes); // Reject malformed UTF-8 and UTF-16 instead of guessing.
        if (!ToolPolicy.Hash(bytes).Equals(range.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Preimage hash changed; read the current file before editing its range.");
        var updated = TextEdits.ReplaceLines(source, range.StartLine, range.LineCount, range.NewText);
        if (updated.StartsWith('\uFEFF') || RangeEditUtf8.GetByteCount(updated) > ToolPolicy.MaxInput)
            throw new ArgumentException("Range edit would exceed 1 MiB or create an unsupported BOM.");
        // This is a no-op/range/format precheck, not a second writer or a CAS guarantee.
        // The provider revalidates its captured preimage before its existing atomic publication.
    }
}
