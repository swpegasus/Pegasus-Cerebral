using System.Security.Cryptography;
using System.Text;

namespace Cerebral.Core;

/// <summary>
/// Shared filesystem scope boundary for semantic tools that must inspect an admitted text file before
/// returning a service-owned plan. Callers still need their own operation-specific size and mutation rules.
/// </summary>
public static class ScopedFilePolicy
{
    public static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Keep the original compiled shape. Existing providers using a host path ceiling must
    // opt into Read explicitly; ambiguous old calls conservatively require write permission.
    public static string PathInScope(string input, ToolExecutionContext context, bool mustExist = false) =>
        PathInScope(input, context, ScopedPathAccess.Write, mustExist);

    public static string PathInScope(string input, ToolExecutionContext context, ScopedPathAccess access, bool mustExist = false)
    {
        if (string.IsNullOrWhiteSpace(input) || input.IndexOf('\0') >= 0) throw new ArgumentException("A nonempty path is required.");
        if (input.Split(['/', '\\']).Any(x => x == "..")) throw new UnauthorizedAccessException("Parent traversal is not permitted.");
        if (OperatingSystem.IsWindows() && (input.StartsWith("\\\\", StringComparison.Ordinal) || (input.Length > 2 && input.IndexOf(':', 2) >= 0)))
            throw new UnauthorizedAccessException("UNC, device paths, and alternate data streams are not permitted.");
        var path = Path.IsPathFullyQualified(input) ? Path.GetFullPath(input) :
            Path.GetFullPath(input, context.WorkingDirectory ?? throw new ArgumentException("Relative paths require an explicit working directory."));
        var roots = context.Profile.AllowedRoots.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).ToArray();
        if (context.PathAccess is null && !context.FullAccess && !roots.Any(root => IsWithin(path, root))) throw new UnauthorizedAccessException("Path is outside the profile's allowed roots.");
        RejectLinks(path);
        if (context.PathAccess is { } scope) ScopedPathAccessPolicy.Require(path, scope, access);
        if (mustExist && !File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("The scoped path does not exist: " + path, path);
        return path;
    }

    public static bool IsWithin(string path, string root) => path.Equals(root, PathComparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

    public static void RejectLinks(string path)
    {
        // Reject every existing ancestor, including links whose target is unavailable. Do not resolve through a link.
        for (var item = path; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Reparse points and symbolic links are not permitted in scoped paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static async Task<ScopedTextFile> ReadTextAsync(string input, ToolExecutionContext context,
        int maximumUtf8Bytes, CancellationToken cancellationToken)
    {
        if (maximumUtf8Bytes is < 1 or >= 64 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes), "Scoped text reads must remain below 64 KiB of UTF-8 text.");
        var path = PathInScope(input, context, ScopedPathAccess.Read, mustExist: true);
        if (!File.Exists(path)) throw new ArgumentException("Scoped text reads require an existing file.");
        RejectLinks(path);

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        // UTF-16 text can occupy about twice its UTF-8 size for the common ASCII source case. Bound
        // the raw read before allocation, then enforce the authoritative UTF-8 limit after decoding.
        var maximumStoredBytes = checked(maximumUtf8Bytes * 2L + 4);
        if (file.Length > maximumStoredBytes)
            throw new IOException($"The scoped text file exceeds the {maximumUtf8Bytes:N0}-byte UTF-8 limit.");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, cancellationToken);
        var text = Decode(bytes);
        var utf8Bytes = Encoding.UTF8.GetByteCount(text);
        if (utf8Bytes > maximumUtf8Bytes)
            throw new IOException($"The scoped text file exceeds the {maximumUtf8Bytes:N0}-byte UTF-8 limit.");
        return new(path, text, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), utf8Bytes, bytes.Length);
    }

    private static string Decode(byte[] bytes)
    {
        Encoding encoding;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) encoding = new UnicodeEncoding(false, true, true);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) encoding = new UnicodeEncoding(true, true, true);
        else encoding = new UTF8Encoding(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), true);
        var preamble = encoding.GetPreamble();
        var offset = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }
}

public sealed record ScopedTextFile(string Path, string Text, string Sha256, int Utf8Bytes, int StoredBytes);
