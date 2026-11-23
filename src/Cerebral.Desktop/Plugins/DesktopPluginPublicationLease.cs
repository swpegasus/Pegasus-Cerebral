using System.IO;

namespace Cerebral.Desktop.Plugins;

/// <summary>Coordinates updated catalog readers and publishers through an OS-held file handle.
/// Empty lock-file presence is not ownership. Same-thread nested calls share only their current lease.
/// This does not fence older binaries or arbitrary native plugin filesystem access.</summary>
internal sealed class DesktopPluginPublicationLease : IDisposable
{
    private sealed class HeldLease(FileStream stream, bool publication)
    {
        public FileStream Stream { get; } = stream;
        public bool Publication { get; } = publication;
        public int References { get; set; } = 1;
    }

    [ThreadStatic] private static Dictionary<string, HeldLease>? held;
    private readonly string path;
    private readonly HeldLease lease;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private bool disposed;

    private DesktopPluginPublicationLease(string path, HeldLease lease)
    { this.path = path; this.lease = lease; }

    internal static DesktopPluginPublicationLease? TryAcquire(string root, bool publication)
    {
        var path = Path.Combine(Path.GetFullPath(root), ".settings", ".catalog-publication.lock");
        held ??= new(StringComparer.OrdinalIgnoreCase);
        if (held.TryGetValue(path, out var current))
        {
            if (publication && !current.Publication) return null; // No read-to-write upgrade.
            current.References++;
            return new(path, current);
        }
        var stream = TryOpen(path, publication);
        if (stream is null) return null;
        var entry = new HeldLease(stream, publication);
        held.Add(path, entry);
        return new(path, entry);
    }

    /// <summary>A separate reader handle for one asynchronous callback, without thread-local
    /// nesting. It can be released by the continuation thread and excludes cooperating publishers.</summary>
    internal static IDisposable? TryAcquireIndependentReader(string root) =>
        TryOpen(Path.Combine(Path.GetFullPath(root), ".settings", ".catalog-publication.lock"), false);

    private static FileStream? TryOpen(string path, bool publication)
    {
        CerebralPluginPackage.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CerebralPluginPackage.RejectLinks(path);
        FileStream? stream = null;
        try
        {
            // Readers share only read access; a publisher excludes every cooperating process.
            stream = new FileStream(path, FileMode.OpenOrCreate,
                publication ? FileAccess.ReadWrite : FileAccess.Read,
                publication ? FileShare.None : FileShare.Read);
            CerebralPluginPackage.RejectLinks(path);
            if (stream.Length != 0) throw new InvalidDataException("The catalog publication lock file must be empty.");
            var result = stream; stream = null; return result;
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
        finally { stream?.Dispose(); }
    }

    internal static IOException Busy() => new("The plugin catalog is being read or updated by another operation. Retry when that operation finishes; no candidate was published.");

    public void Dispose()
    {
        if (disposed) return;
        if (thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("A catalog publication lease must be released on its acquiring thread.");
        if (held is null || !held.TryGetValue(path, out var current) || !ReferenceEquals(current, lease))
            throw new InvalidOperationException("The catalog publication lease is no longer current.");
        if (--lease.References == 0)
        {
            held.Remove(path);
            lease.Stream.Dispose();
        }
        disposed = true;
    }
}
