using System.IO.Compression;
using System.Text;

namespace Cerebral.Core;

/// <summary>
/// Bounds a content archive before a caller admits its own file layout. Explicit ZIP directory
/// entries are rejected; parent directories are inferred from file paths, as in plugin packages.
/// The retained read-only archive is also the only source used during extraction.
/// </summary>
public sealed class BoundedContentZipArchive : IDisposable
{
    public const int MaximumEntries = NativeDesktopPluginPackage.MaximumEntries;
    public const int MaximumDirectories = NativeDesktopPluginPackage.MaximumDirectories;
    public const int MaximumDepth = NativeDesktopPluginPackage.MaximumDepth;
    public const long MaximumExpandedBytes = NativeDesktopPluginPackage.MaximumExpandedBytes;
    private const int MaximumTargetPathChars = 240;
    private const int MaximumSegmentUtf8Bytes = 255;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ZipArchive archive;
    private readonly IReadOnlyList<ZipArchiveEntry> files;
    private bool disposed;

    private BoundedContentZipArchive(ZipArchive archive, IReadOnlyList<ZipArchiveEntry> files)
    {
        this.archive = archive;
        this.files = files;
    }

    public IReadOnlyList<ZipArchiveEntry> Files
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return files;
        }
    }

    public static BoundedContentZipArchive OpenRead(string path)
    {
        var source = ZipFile.OpenRead(Path.GetFullPath(path));
        try { return new BoundedContentZipArchive(source, Validate(source)); }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<ZipArchiveEntry> Validate(ZipArchive source)
    {
        if (source.Entries.Count is < 1 or > MaximumEntries)
            throw new InvalidDataException($"A content archive must contain 1–{MaximumEntries:N0} bounded files.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ZipArchiveEntry>();
        long expanded = 0;
        foreach (var entry in source.Entries)
        {
            var name = entry.FullName;
            // Explicit directory entries were never admitted by the existing plugin path rule.
            if (string.IsNullOrWhiteSpace(name) || name.EndsWith('/') || name.Contains('\\') ||
                name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Archive entries must use contained forward-slash file paths.");
            var parts = name.Split('/');
            if (parts.Length > MaximumDepth)
                throw new InvalidDataException($"Archive paths may contain at most {MaximumDepth} segments.");
            foreach (var part in parts) ValidateSegment(part);
            for (var index = 1; index < parts.Length; index++) directories.Add(string.Join('/', parts.Take(index)));
            if (directories.Count > MaximumDirectories)
                throw new InvalidDataException($"Archives may contain at most {MaximumDirectories:N0} directories.");
            if (!names.Add(name)) throw new InvalidDataException("Archive entry names must be unique without regard to case.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archives cannot contain links or reparse points.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException($"The expanded archive exceeds {MaximumExpandedBytes / 1048576:N0} MiB.");
            files.Add(entry);
        }
        if (names.Overlaps(directories))
            throw new InvalidDataException("An archive file also names a parent directory.");
        return files.AsReadOnly();
    }

    private static void ValidateSegment(string part)
    {
        if (part.EndsWith(' ') || part.EndsWith('.') ||
            part.Any(ch => ch < ' ' || ch is '<' or '>' or '"' or '|' or '?' or '*'))
            throw new InvalidDataException("Archive paths contain a Windows-aliased or invalid filename.");
        int utf8Bytes;
        try { utf8Bytes = StrictUtf8.GetByteCount(part); }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("Archive paths contain invalid UTF-16 text.", error);
        }
        if (utf8Bytes > MaximumSegmentUtf8Bytes)
            throw new InvalidDataException("An archive path segment exceeds 255 UTF-8 bytes.");
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                 stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
            throw new InvalidDataException("Archive paths contain a reserved Windows device name.");
    }

    /// <summary>Reads a member of this retained archive without allocating beyond the caller's byte cap.</summary>
    public byte[] ReadEntryBytes(ZipArchiveEntry entry, int maximumBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (!files.Any(candidate => ReferenceEquals(candidate, entry)))
            throw new InvalidDataException("The entry does not belong to this validated archive.");
        if (entry.Length > maximumBytes)
            throw new InvalidDataException("The archive entry exceeds its read limit.");
        using var output = new MemoryStream();
        CopyBounded(entry, output, maximumBytes);
        return output.ToArray();
    }

    /// <summary>Extracts to an existing, empty, caller-owned temporary directory.</summary>
    public void ExtractToNewDirectory(string destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        ScopedFilePolicy.RejectLinks(root);
        if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("Archive extraction requires an existing empty temporary directory.");

        // Validate every final path before making even a parent directory.
        var targets = files.Select(entry =>
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (target.Length > MaximumTargetPathChars)
                throw new InvalidDataException("Archive extraction target exceeds 240 UTF-16 characters.");
            if (target.Equals(root, ScopedFilePolicy.PathComparison) || !ScopedFilePolicy.IsWithin(target, root))
                throw new InvalidDataException("Archive extraction escaped its temporary directory.");
            return (Entry: entry, Target: target);
        }).ToArray();

        long expanded = 0;
        foreach (var item in targets)
        {
            var target = item.Target;
            var parent = Path.GetDirectoryName(target)!;
            ScopedFilePolicy.RejectLinks(parent);
            Directory.CreateDirectory(parent);
            ScopedFilePolicy.RejectLinks(target);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            expanded = checked(expanded + CopyBounded(item.Entry, output, MaximumExpandedBytes - expanded));
        }
    }

    private static long CopyBounded(ZipArchiveEntry entry, Stream output, long maximum)
    {
        using var input = entry.Open();
        var buffer = new byte[64 * 1024];
        long copied = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            copied = checked(copied + count);
            if (copied > entry.Length || copied > maximum)
                throw new InvalidDataException("An archive entry exceeded its declared or allowed expanded size.");
            output.Write(buffer, 0, count);
        }
        if (copied != entry.Length)
            throw new InvalidDataException("An archive entry did not match its declared expanded size.");
        return copied;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        archive.Dispose();
    }
}
