using System.Text.Json;

namespace Cerebral.BrowserPlugin;

/// <summary>Owner-staged, immutable generation. This descriptor never downloads or installs a browser.</summary>
public sealed record BrowserResource(string Generation, string Distribution, string Version, string Architecture,
    string PlaywrightVersion, string Source, string License, string Executable, string LicenseFile, string NoticesFile);
public sealed record BrowserResourceState(long Revision, BrowserResource? Active);

public sealed class BrowserResources
{
    private readonly string statePath;
    public string Root { get; }
    public BrowserResources(string dataDirectory)
    {
        Root = Path.GetFullPath(Path.Combine(dataDirectory, "runtime-generations"));
        statePath = Path.Combine(dataDirectory, "browser-resource.json");
    }
    public BrowserResourceState Read()
    {
        if (!File.Exists(statePath)) return new(0, null);
        if (new FileInfo(statePath).Length > 32768) throw new InvalidDataException("Browser resource state exceeds its bound.");
        using var json = JsonDocument.Parse(File.ReadAllText(statePath));
        return BrowserJson.Read<BrowserResourceState>(json.RootElement);
    }
    public string Validate(BrowserResource resource)
    {
        if (resource.Generation.Length is < 1 or > 80 || !resource.Generation.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            resource.Distribution is not ("chromium" or "edge-test-only") || resource.Version.Length is < 1 or > 80 ||
            resource.PlaywrightVersion != "1.63.0" || resource.Architecture != "win-x64" || !OperatingSystem.IsWindows() ||
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64 ||
            resource.License.Length is < 1 or > 100 || !Uri.TryCreate(resource.Source, UriKind.Absolute, out var source) ||
            source.Scheme != "https" || source.UserInfo.Length != 0)
            throw new ArgumentException("Browser generation identity, pinned SDK, provenance or architecture is invalid.");
        var root = Path.Combine(Root, resource.Generation);
        var executable = ConfinedFile(root, resource.Executable);
        _ = ConfinedFile(root, resource.LicenseFile); _ = ConfinedFile(root, resource.NoticesFile);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Browser generation needs an executable.");
        if (resource.Distribution != "chromium") throw new ArgumentException("Installed third-party browsers cannot be activated as redistributable Chromium resources.");
        return executable;
    }
    /// <summary>Caller holds the plugin lifecycle lock and has already rejected active browser jobs.</summary>
    public BrowserResourceState Activate(long expectedRevision, BrowserResource resource)
    {
        _ = Validate(resource);
        var current = Read();
        if (current.Revision != expectedRevision) throw new BrowserRejectedException("resource-conflict", "The browser resource selection changed; inspect it before retrying.");
        var next = new BrowserResourceState(checked(current.Revision + 1), resource);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary = statePath + ".pending";
        try { File.WriteAllText(temporary, BrowserJson.Text(next)); File.Move(temporary, statePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return next;
    }
    public static string ConfinedFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl))
            throw new ArgumentException("Browser resource paths must be relative ordinary files.");
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
        if (!path.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("A browser resource file is absent or leaves its generation.");
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = current switch
             { FileInfo file => file.Directory, DirectoryInfo directory => directory.Parent, _ => null })
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Browser resource paths cannot cross a reparse point.");
        return path;
    }
}
