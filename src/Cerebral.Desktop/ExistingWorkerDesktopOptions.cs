using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>A fixed existing Worker binding and independent desktop storage; parsing has no effects.</summary>
public sealed record ExistingWorkerDesktopOptions(LocalWorkerEndpoint Endpoint, string WorkerDataDirectory,
    string DesktopDataDirectory, string InstallationId)
{
    public static ExistingWorkerDesktopOptions? FromArguments(string[] args)
    {
        if (!args.Contains("--attach-existing", StringComparer.Ordinal)) return null;
        if (args.Length != 6 || args[0] != "--attach-existing" || args.Skip(1).Any(x => x.StartsWith("--", StringComparison.Ordinal)))
            throw new ArgumentException("Use --attach-existing PIPE INSTANCE WORKER_DATA DESKTOP_STATE INSTALLATION_ID with no other startup options.");
        var endpoint = new LocalWorkerEndpoint(args[1], args[2]);
        endpoint.Validate();
        var worker = NormalizeLocalDirectory(args[3]);
        var desktop = NormalizeLocalDirectory(args[4]);
        if (!Directory.Exists(worker)) throw new ArgumentException("The existing Worker's data directory must already exist.");
        if (Overlaps(worker, desktop)) throw new ArgumentException("Private desktop state must be separate from Worker data.");
        if (OverlapsNormalUserData(desktop, InstallationPaths.CurrentUserDataDirectory))
            throw new ArgumentException("Use a private desktop state directory outside the normal Cerebral user data.");
        if (string.IsNullOrWhiteSpace(args[5]) || args[5].Length > 256 || args[5].Any(char.IsControl))
            throw new ArgumentException("Supply the exact existing Worker installation identity.");
        return new(endpoint, worker, desktop, args[5]);
    }

    public static string NormalizeLocalDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) ||
            directory.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Attach-existing paths must be absolute local directories.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase) ||
            full.AsSpan(2).Contains(':')) throw new ArgumentException("Use a specific local directory, not a drive root or alternate stream.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Attach-existing storage cannot traverse links.");
        return full;
    }

    private static bool Overlaps(string first, string second) =>
        ScopedFilePolicy.IsWithin(first, second) || ScopedFilePolicy.IsWithin(second, first);

    internal static bool OverlapsNormalUserData(string candidate, string selectedUserRoot)
    {
        var legacyUserRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pegasus", "Cerebral");
        return Overlaps(candidate, legacyUserRoot) || Overlaps(candidate, selectedUserRoot);
    }

    // The Worker identity is shared by desktop components and independent desktop state roots.
    public DesktopApplicationIdentity ApplicationIdentity => DesktopApplicationIdentity.Application(
        "attach-existing:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Endpoint.PipeName + "\n" + Endpoint.InstanceId + "\n" + WorkerDataDirectory.ToUpperInvariant() + "\n" + InstallationId))));

    public string GetApplicationId(string component)
    {
        // Match DesktopApplicationInstance's identifier alphabet without opening any instance handles.
        if (string.IsNullOrWhiteSpace(component) || component.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '.' and not '-'))
            throw new ArgumentException("The component may contain only ASCII letters, digits, periods, and hyphens.", nameof(component));
        return "Pegasus.Cerebral." + component + ".Attached." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DesktopDataDirectory.ToUpperInvariant())));
    }
}

/// <summary>The immutable endpoint and Worker identity captured by an attached desktop window.</summary>
public sealed record ExistingLocalWorkerAttachment(LocalWorkerEndpoint Endpoint, string WorkerDataDirectory, string InstallationId);
