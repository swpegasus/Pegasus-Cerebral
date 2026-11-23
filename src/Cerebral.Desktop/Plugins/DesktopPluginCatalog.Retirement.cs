using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Desktop.Plugins;

/// <summary>Host-private removal outcome. A returned result confirms logical retirement;
/// pending cleanup concerns only inactive code bytes, never the plugin's saved data.</summary>
public sealed record DesktopPluginRemovalResult(string Id, bool CleanupPending, string? CleanupError);
public sealed record DesktopPluginCleanupBatch(IReadOnlyList<DesktopPluginRemovalResult> Results, string? NextAfterId);

public sealed partial class DesktopPluginCatalog
{
    private bool IsRemoved(string id)
    {
        var path = RemovedPath(id); CheckAncestors(path);
        if (Directory.Exists(path)) throw new InvalidDataException("The native plugin removal setting is a directory.");
        return File.Exists(path);
    }

    /// <summary>Retires the catalog selection before separately reclaiming inactive package bytes.
    /// Calling again for an already-retired ID retries cleanup without restoring or executing code.</summary>
    public DesktopPluginRemovalResult RemoveWithResult(string id)
    {
        ValidateId(id);
        using var publication = BeginPublication();
        var retired = IsRemoved(id);
        if (!retired)
        {
            if (ReadEnabled(id)) throw new InvalidOperationException("Disable the native plugin before removing its package.");
            RequireNoEnabledDependents(id);
            _ = List().SingleOrDefault(item => item.Id == id) ?? throw new DirectoryNotFoundException("The native plugin is not installed.");
        }
        // Validate every exact target before changing visibility; never reclaim linked storage.
        foreach (var directory in PackageRetirementDirectories(id)) _ = PackageFiles(directory);
        if (!retired)
        {
            // The marker is first, so interrupted publication fails closed even while old
            // pointer/legacy bytes survive. Existing file-transaction rollback handles process
            // failures; this is not a crash-recovery journal or an atomic directory deletion.
            using var state = new PreparedPluginFileInstallation([
                (RemovedPath(id), System.Text.Encoding.UTF8.GetBytes("removed")),
                (ActivePath(id), null),
                (Path.Combine(RootDirectory, ".settings", id + ".json"), null)
            ]);
            state.Publish(); state.Commit();
        }
        string? error = null;
        try
        {
            foreach (var directory in PackageRetirementDirectories(id))
            {
                var cleanupError = TryDeleteInactivePackage(id, directory); error ??= cleanupError;
            }
            var store = PackageStore(id); CheckAncestors(store);
            if (Directory.Exists(store))
            {
                if (!Directory.EnumerateFileSystemEntries(store).Any()) Directory.Delete(store, false);
                else error ??= "Native package cleanup is deferred because inactive storage still contains entries.";
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { error ??= "Native package cleanup is deferred because inactive storage is unavailable or changed."; }
        return new(id, error is not null, error);
    }

    /// <summary>A bounded, cursor-based maintenance pass for logically removed IDs only.
    /// No active package selection, enablement, or saved plugin data is changed.</summary>
    public DesktopPluginCleanupBatch CleanupRemovedPackages(int limit = 32, string? afterId = null)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterId is not null) ValidateId(afterId);
        using var publication = BeginPublication();
        var settings = Path.Combine(RootDirectory, ".settings"); CheckAncestors(settings);
        if (!Directory.Exists(settings)) return new([], null);
        var ids = Directory.EnumerateFiles(settings, "*.removed", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path)[..^".removed".Length])
            .Where(id => afterId is null || StringComparer.Ordinal.Compare(id, afterId) > 0)
            .Order(StringComparer.Ordinal).Take(limit + 1).ToArray();
        var results = new List<DesktopPluginRemovalResult>();
        foreach (var id in ids.Take(limit))
        {
            ValidateId(id);
            try { results.Add(RemoveWithResult(id)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                results.Add(new(id, true,
                    "Retired native package cleanup is deferred because inactive storage is unavailable or changed."));
            }
        }
        return new(results.AsReadOnly(), ids.Length > limit ? results[^1].Id : null);
    }

    private void DetachRemovedLegacy(string id)
    {
        if (!IsRemoved(id)) return;
        var legacy = Path.Combine(RootDirectory, id); CheckAncestors(legacy);
        if (!Directory.Exists(legacy)) return;
        _ = PackageFiles(legacy);
        var retired = Path.Combine(RootDirectory, ".retired-" + id + "-" + Guid.NewGuid().ToString("N"));
        CheckAncestors(retired);
        // Detach the complete legacy namespace before Install clears its removal marker.
        // If a consumer blocks this atomic rename, reject the reinstall with the marker and
        // whole old folder intact. A pointer-publication interruption cannot revive old bytes.
        Directory.Move(legacy, retired);
    }

    private IReadOnlyList<string> PackageRetirementDirectories(string id)
    {
        ValidateId(id); CheckAncestors(RootDirectory);
        var result = new List<string>();
        var legacy = Path.Combine(RootDirectory, id); CheckAncestors(legacy);
        if (Directory.Exists(legacy)) result.Add(legacy);
        var store = PackageStore(id); CheckAncestors(store);
        if (Directory.Exists(store))
            result.AddRange(Directory.EnumerateDirectories(store, "*", SearchOption.TopDirectoryOnly));
        result.AddRange(RetiredPackageDirectories(id));
        return result;
    }

    private IEnumerable<string> RetiredPackageDirectories(string id)
    {
        ValidateId(id); CheckAncestors(RootDirectory);
        if (!Directory.Exists(RootDirectory)) return [];
        var prefix = ".retired-" + id + "-";
        return Directory.EnumerateDirectories(RootDirectory, prefix + "*", SearchOption.TopDirectoryOnly)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path)[prefix.Length..], "N", out _));
    }

    // Retain every handle until all file dispositions have succeeded. A mapped image, sharing
    // lock, missing delete permission, or read-only file cannot delete the manifest first and
    // strand its DLL. No FILE_FLAG_DELETE_ON_CLOSE: successful dispositions must be reversible
    // before closing handles. Closing the process during staging is not crash-atomic, but the
    // authoritative removal marker still prevents residual bytes from becoming an installation.
    private string? TryDeleteInactivePackage(string id, string directory)
    {
        if (IsActivationRetained(id, directory)) return "Native package bytes are retained by the last activation recovery record.";
        var files = PackageFiles(directory).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var directories = Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
        }).Prepend(directory).OrderByDescending(path => path.Length).ToArray();
        if (directories.Length > Cerebral.Core.NativeDesktopPluginPackage.MaximumDirectories)
            throw new IOException("Inactive native package directories exceeded the cleanup bound.");
        foreach (var path in directories) CheckAncestors(path);
        var handles = new List<SafeFileHandle>(files.Length);
        string? failure = null;
        try
        {
            foreach (var file in files)
            {
                CheckAncestors(file);
                var path = file.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + file[2..] : @"\\?\" + file;
                var handle = RetirementCreateFile(path, 0xC0010000, 0, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var code = Marshal.GetLastPInvokeError(); handle.Dispose();
                    throw new IOException("Inactive file cleanup access failed.", new Win32Exception(code));
                }
                handles.Add(handle);
                if (!RetirementGetFileInformation(handle, 9, out var attributes, 8))
                    throw new IOException("Inactive file cleanup inspection failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
                if ((attributes.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Inactive native package cleanup cannot traverse a link or directory file.");
                CheckAncestors(file);
            }
            failure = StageInactivePackageDeletion(handles);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var code = error.InnerException is Win32Exception native ? $" (Windows error {native.NativeErrorCode})" : "";
            failure = "Native package cleanup is deferred while inactive files are in use or access is denied" + code + ".";

        }
        finally { foreach (var handle in handles) handle.Dispose(); }
        if (failure is not null) return failure;
        // File deletion is committed. Only empty directories are removed from here; a newly
        // introduced file or blocked folder remains residue, and is never recursively deleted.
        foreach (var path in directories)
        {
            CheckAncestors(path);
            if (Directory.Exists(path)) Directory.Delete(path, false);
        }
        return null;
    }

    private static string? StageInactivePackageDeletion(IReadOnlyList<SafeFileHandle> handles)
    {
        var staged = new List<SafeFileHandle>(handles.Count);
        foreach (var handle in handles)
        {
            var disposition = new RetirementFileDisposition { DeleteFile = 1 };
            if (!RetirementSetFileInformation(handle, 4, ref disposition, 1))
            {
                var code = Marshal.GetLastPInvokeError();
                var rollbackFailed = false;
                foreach (var previous in staged)
                {
                    var restore = new RetirementFileDisposition();
                    if (!RetirementSetFileInformation(previous, 4, ref restore, 1)) rollbackFailed = true;
                }
                return rollbackFailed
                    ? "Native package cleanup is uncertain: a staged file deletion could not be withdrawn. The plugin remains logically removed; inspect inactive package storage before retrying."
                    : $"Native package cleanup is deferred while inactive files are in use or access is denied (Windows error {code}).";
            }
            staged.Add(handle);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetirementFileDisposition { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RetirementFileAttributes { public uint Attributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle RetirementCreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RetirementSetFileInformation(SafeFileHandle file, int infoClass,
        ref RetirementFileDisposition information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RetirementGetFileInformation(SafeFileHandle file, int infoClass,
        out RetirementFileAttributes information, uint size);
}
