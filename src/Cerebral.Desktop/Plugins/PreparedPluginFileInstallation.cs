using System.IO;

namespace Cerebral.Desktop.Plugins;

/// <summary>Private process-failure transaction for bounded plugin state files, including explicit removal.
/// It is not a crash-recovery journal; failed rollback retains recovery paths and can be retried.</summary>
internal sealed class PreparedPluginFileInstallation : IDisposable
{
    private sealed class PendingFile(string destination, string? stage, string previous, byte[]? publishedHash)
    {
        public string Destination { get; } = destination;
        public string? Stage { get; } = stage;
        public string Previous { get; } = previous;
        public byte[]? PublishedHash { get; } = publishedHash;
        public bool PreviousMoved { get; set; }
        public bool Published { get; set; }
    }

    private readonly List<PendingFile> files = [];
    private bool published;
    private bool committed;
    private bool disposed;

    internal PreparedPluginFileInstallation(IReadOnlyList<(string Destination, byte[]? Contents)> replacements)
    {
        if (replacements.Count > 16) throw new ArgumentException("A plugin state transaction is limited to sixteen files.");
        var destinations = replacements.Select(item => Path.GetFullPath(item.Destination)).ToArray();
        if (destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinations.Length)
            throw new ArgumentException("Plugin state transaction destinations must be distinct.");
        var transaction = Guid.NewGuid().ToString("N");
        try
        {
            for (var index = 0; index < replacements.Count; index++)
            {
                var contents = replacements[index].Contents;
                if (contents?.Length > CerebralPluginPackageContract.MaximumSettingsBytes)
                    throw new InvalidDataException("Plugin state exceeded its bounded document size.");
                var destination = destinations[index];
                CerebralPluginPackage.RejectLinks(destination);
                if (Directory.Exists(destination)) throw new IOException("The plugin state destination is a directory: " + destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CerebralPluginPackage.RejectLinks(destination);
                var stage = contents is null ? null : destination + ".cerebral-staging-" + transaction;
                var previous = destination + ".cerebral-previous-" + transaction;
                CerebralPluginPackage.RejectLinks(previous);
                if (File.Exists(previous) || Directory.Exists(previous)) throw new IOException("A plugin state recovery path already exists.");
                var item = new PendingFile(destination, stage, previous,
                    contents is null ? null : System.Security.Cryptography.SHA256.HashData(contents));
                files.Add(item);
                if (stage is not null)
                {
                    CerebralPluginPackage.RejectLinks(stage);
                    using var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    file.Write(contents!);
                    file.Flush(flushToDisk: true);
                }
            }
        }
        catch
        {
            CleanupStages();
            throw;
        }
    }

    public void Publish()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (published) throw new InvalidOperationException("Plugin state files were already published.");
        try
        {
            foreach (var item in files)
            {
                CerebralPluginPackage.RejectLinks(item.Destination);
                CerebralPluginPackage.RejectLinks(item.Previous);
                if (Directory.Exists(item.Destination)) throw new IOException("The plugin state destination became a directory.");
                if (File.Exists(item.Destination))
                {
                    File.Move(item.Destination, item.Previous);
                    item.PreviousMoved = true;
                }
                if (item.Stage is not null)
                {
                    CerebralPluginPackage.RejectLinks(item.Stage);
                    File.Move(item.Stage, item.Destination);
                }
                item.Published = true;
            }
            published = true;
        }
        catch (Exception publicationError)
        {
            try { Rollback(); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Plugin state publication failed and rollback was incomplete.", publicationError, rollbackError);
            }
            throw;
        }
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!published) throw new InvalidOperationException("Publish plugin state files before committing them.");
        committed = true;
        // Locked cleanup data does not turn a completed selection into a rollback claim.
        CleanupBackups();
    }

    private void Rollback()
    {
        var failures = new List<Exception>();
        for (var index = files.Count - 1; index >= 0; index--)
        {
            var item = files[index];
            try
            {
                if (item.PreviousMoved)
                {
                    CerebralPluginPackage.RejectLinks(item.Previous);
                    if (!File.Exists(item.Previous)) throw new IOException("Prior plugin state is missing: " + item.Previous);
                }
                CerebralPluginPackage.RejectLinks(item.Destination);
                if (item.Published)
                {
                    if (Directory.Exists(item.Destination)) throw new IOException("A plugin state rollback destination became a directory.");
                    if (File.Exists(item.Destination))
                    {
                        // Owner writes or substituted files are conflicts, not rollback scratch data.
                        using (var input = File.OpenRead(item.Destination))
                            if (item.PublishedHash is null || input.Length > CerebralPluginPackageContract.MaximumSettingsBytes ||
                                !System.Security.Cryptography.SHA256.HashData(input).SequenceEqual(item.PublishedHash))
                                throw new IOException("Published plugin state changed; preserve it and resolve recovery at " + item.Destination);
                        File.Delete(item.Destination);
                    }
                    // A later restore failure must not delete an unrelated file on a recovery retry.
                    item.Published = false;
                }
                if (item.PreviousMoved)
                {
                    File.Move(item.Previous, item.Destination);
                    item.PreviousMoved = false;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add(error); }
        }
        published = false;
        if (failures.Count != 0)
            throw new IOException("Plugin state rollback was incomplete. Recovery paths: " +
                string.Join("; ", files.Select(item => item.Stage + "; " + item.Previous)), new AggregateException(failures));
    }

    private void CleanupStages()
    {
        foreach (var item in files)
            if (item.Stage is not null && File.Exists(item.Stage)) File.Delete(item.Stage);
    }

    private void CleanupBackups()
    {
        foreach (var item in files)
            try
            {
                CerebralPluginPackage.RejectLinks(item.Previous);
                if (File.Exists(item.Previous)) File.Delete(item.Previous);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (!committed) Rollback();
        CleanupStages();
        if (committed) CleanupBackups();
        disposed = true;
    }
}
