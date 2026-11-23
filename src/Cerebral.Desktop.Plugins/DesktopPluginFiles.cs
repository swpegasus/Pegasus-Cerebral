using System.IO;
using System.Text;

namespace Cerebral.Desktop.Plugins;

/// <summary>Optional, bounded text-file operations for plugin-owned mutable data.</summary>
public static class DesktopPluginFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Read strict UTF-8 without allocating for a file larger than the caller's limit.</summary>
    public static string ReadUtf8(string path, int maximumBytes)
    {
        ValidateLimit(maximumBytes);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximumBytes) throw new IOException("File exceeds the UTF-8 byte limit.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new IOException("File changed while reading.");
        return Utf8.GetString(bytes);
    }

    /// <summary>Replace only after a complete same-directory temporary write; failed writes preserve the old file.</summary>
    public static void WriteUtf8Atomically(string path, string text, int maximumBytes)
    {
        ValidateLimit(maximumBytes);
        ArgumentNullException.ThrowIfNull(text);
        if (Utf8.GetByteCount(text) > maximumBytes) throw new IOException("Text exceeds the UTF-8 byte limit.");
        var bytes = Utf8.GetBytes(text);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("No data directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            // Cleanup is best effort and must not mask the original write/replace error.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateLimit(int maximumBytes)
    {
        if (maximumBytes is < 0 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "A text-file limit must be 0–16 MiB.");
    }
}
