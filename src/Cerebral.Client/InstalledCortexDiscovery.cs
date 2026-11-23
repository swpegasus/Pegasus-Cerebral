using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Cerebral.Client;

public sealed record InstalledCortexLocation(string InstallDirectory, string DataDirectory, string? ReleaseVersion = null)
{
    public string ExecutablePath => Path.Combine(InstallDirectory, "PegasusCortex.exe");
    public string PaoPath => Path.Combine(InstallDirectory, "bin", "pao.exe");
    public string NativeHostPath => Path.Combine(InstallDirectory, "bin", "host", "PegasusCortex.Host.exe");
}

/// <summary>Machine-installed registration only; no Codex, plugin, PATH or default-folder guessing.</summary>
public static class InstalledCortexDiscovery
{
    public const string RegistryPath = @"SOFTWARE\Pegasus\Cortex";

    public static IReadOnlyList<string> FindInstalled()
    {
        var installed = ReadRegistered();
        return installed is null ? [] : [ValidateExplicitPath(installed.ExecutablePath)];
    }

    /// <summary>Resolve the registered Cortex tray host, separately from its MCP/CLI executable.</summary>
    public static string? FindInstalledTray() => FindInstalledTray(ReadRegistered());

    // Explicit registration values support bounded path checks without changing the machine binding.
    public static string? FindInstalledTray(InstalledCortexLocation? installed)
    {
        if (installed is null) return null;
        RequireDirectoryPath(installed.InstallDirectory);
        var tray = installed.NativeHostPath;
        RejectLinks(tray);
        if (!File.Exists(tray))
            throw new InvalidDataException("The registered Cortex tray is missing. Repair Pegasus Cortex before opening its settings.");
        return tray;
    }

    public static InstalledCortexLocation? ReadRegistered()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = machine.OpenSubKey(RegistryPath, writable: false);
        string? Read(string name)
        {
            var value = registration?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) return null;
            if (registration!.GetValueKind(name) != RegistryValueKind.String || value is not string text)
                throw new InvalidDataException($"The Cortex {name} registration must be REG_SZ. Repair Cortex before continuing.");
            return text;
        }
        var location = FromRegistration(Read("InstallLocation"), Read("DataLocation"));
        return location is null ? null : location with { ReleaseVersion = Read("ReleaseVersion") };
    }

    // Explicit values exercise the real path rules in tests without changing machine registration.
    public static InstalledCortexLocation? FromRegistration(string? installDirectory, string? dataDirectory)
    {
        if (installDirectory is null)
        {
            if (dataDirectory is not null) throw new InvalidDataException("Cortex data registration has no installation binding. Repair Cortex first.");
            return null;
        }
        var install = RequireDirectoryPath(installDirectory);
        // This is Cortex's declared legacy layout, used only when DataLocation is absent.
        var data = RequireDirectoryPath(dataDirectory ?? Path.Combine(install, "state"));
        RejectLinks(install); RejectLinks(data);
        return new(install, data);
    }

    public static string ValidateExplicitPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose the absolute installed PegasusCortex.exe path.");
        var fileName = Path.GetFileName(path);
        if (!fileName.Equals("PegasusCortex.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an existing installed PegasusCortex.exe. Cortex is installed separately.");
        var directory = RequireDirectoryPath(Path.GetDirectoryName(path)!);
        path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            throw new ArgumentException("Choose an existing installed PegasusCortex.exe. Cortex is installed separately.");
        RejectLinks(path);
        return path;
    }

    public static string ValidateSeparateDirectory(InstalledCortexLocation cortex, string directory)
    {
        var destination = RequireDirectoryPath(directory);
        var destinationIdentity = ComparisonDirectory(destination);
        if (Overlaps(destinationIdentity, ComparisonDirectory(RequireDirectoryPath(cortex.InstallDirectory))) ||
            Overlaps(destinationIdentity, ComparisonDirectory(RequireDirectoryPath(cortex.DataDirectory))))
            throw new InvalidDataException("Choose a Cerebral directory separate from Cortex's program and data directories; neither product may contain the other.");
        return destination;
    }

    /// <summary>Validate one bounded local directory and resolve existing short-name aliases for identity comparisons.</summary>
    public static string ValidateLocalDirectory(string directory) => ComparisonDirectory(RequireDirectoryPath(directory));

    private static bool Overlaps(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static string RequireDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Any(char.IsControl) || path.Contains('"') ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Cortex and Cerebral installation paths must be bounded, absolute local directories.");
        var root = Path.GetPathRoot(path)!;
        var segments = path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments) RequireSegment(segment);
        // GetFullPath may expand an existing DOS short name. Validate the lexical
        // form here without changing the spelling owned by Cortex registration.
        var canonical = root.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar) +
            string.Join(Path.DirectorySeparatorChar, segments);
        var supplied = path.TrimEnd(Path.DirectorySeparatorChar);
        if (segments.Length == 0 || !canonical.Equals(supplied, StringComparison.OrdinalIgnoreCase) || File.Exists(supplied))
            throw new InvalidDataException("Use a canonical product directory, not a drive root, file or path containing relative segments.");
        return supplied;
    }

    // Resolve names only for comparisons. The recorded spelling remains the source
    // of launch paths because Cortex owns its installation registration binding.
    private static string ComparisonDirectory(string path)
    {
        RejectLinks(path);
        var missing = new Stack<string>();
        var existing = path;
        while (true)
        {
            var attributes = ExistingAttributes(existing);
            if (attributes is not null)
            {
                if ((attributes.Value & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("An installation path ancestor is an existing file.");
                break;
            }
            missing.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing)
                ?? throw new InvalidDataException("The installation path has no accessible existing directory ancestor.");
        }
        if (OperatingSystem.IsWindows())
        {
            var expanded = new StringBuilder(261);
            var length = GetLongPathName(existing, expanded, expanded.Capacity);
            if (length == 0)
                throw new IOException("The installation directory identity could not be resolved.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            if (length >= expanded.Capacity)
                throw new InvalidDataException("The resolved installation directory exceeds the supported path length.");
            existing = Path.GetFullPath(expanded.ToString());
            RejectLinks(existing);
        }
        while (missing.Count > 0) existing = Path.Combine(existing, missing.Pop());
        if (existing.Length > 240)
            throw new InvalidDataException("The resolved installation directory exceeds the supported path length.");
        return existing.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void RequireSegment(string segment)
    {
        if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
            throw new InvalidDataException("Installation paths must not contain Windows filename aliases or invalid characters.");
        var stem = segment.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789\u00b9\u00b2\u00b3".Contains(stem[3]))
            throw new InvalidDataException("Installation paths must not contain reserved Windows device names.");
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // Access errors are not evidence that a directory is absent.
    }

    internal static void RejectLinks(string path)
    {
        for (string? item = path; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if (ExistingAttributes(item) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The Cortex or Cerebral installation path must not traverse filesystem links.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, int capacity);
}
