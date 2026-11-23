using Microsoft.Win32;

namespace Cerebral.Core;

/// <summary>Separates the running payload from registered durable data; portable data is an explicit marker opt-in.</summary>
public static class InstallationPaths
{
    public const string HomeEnvironmentVariable = "PEGASUS_CEREBRAL_HOME";

    private static readonly Lazy<string?> PortableRoot = new(() => ResolvePortableRoot(AppContext.BaseDirectory));

    private enum RegistrationState { Absent, Complete, Partial, Unavailable }

    private sealed record RegistrationProjection(RegistrationState State, string? InstallDirectory,
        string? BaseDirectory, string? DataDirectory, string? UserDataDirectory);

    /// <summary>The stable writable product-data base. Moving program files never moves this data.</summary>
    public static string BaseDirectory => PortableRoot.Value ?? EnvironmentPath(HomeEnvironmentVariable) ?? RegisteredPath("BaseDir") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral");

    /// <summary>The currently executing payload, never redirected to another binary generation by registry metadata.</summary>
    public static string InstallDirectory => PortableRoot.Value ?? InferInstallDirectory(AppContext.BaseDirectory);

    /// <summary>Service-owned mutable data. A configured home supplies its Data child.</summary>
    public static string DataDirectory => PortableChild("Data") ?? EnvironmentHomeChild("Data") ?? RegisteredPath("DataDir") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral");

    /// <summary>Bundled tools belong to this running payload. Explicit tool settings may still select other roots.</summary>
    public static string ToolsDirectory => Path.Combine(InstallDirectory, "Tools");

    /// <summary>Durable per-user settings and plugin-owned data, independent from replaceable plugin code.</summary>
    public static string CurrentUserDataDirectory
    {
        get
        {
            if (PortableChild("UserData") is { } portable) return portable;
            if (EnvironmentHomeChild(Path.Combine("Users", UserFolderName())) is { } home) return home;
            if (RegisteredPath("UserDataDir") is { } configured) return Path.Combine(configured, UserFolderName());
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral");
        }
    }

    public static bool IsPortable => PortableRoot.Value is not null;

    /// <summary>The installer-owned Projects root for this exact registered installation and data store.</summary>
    public static string? RegisteredProjectsDirectory(string installDirectory, string dataDirectory)
    {
        if (IsPortable || !OperatingSystem.IsWindows()) return null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"Software\Pegasus\Cerebral", false);
            if (key is null) return null;
            return ResolveRegisteredProjectsDirectory(key.GetValue("InstallDir"), key.GetValue("DataDir"),
                key.GetValue("ProjectsDir"), installDirectory, dataDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            throw new InvalidDataException("The installed Projects registration could not be read. Review it through Cerebral Setup.");
        }
    }

    // A missing/unmatched installation is different from a matched installation
    // whose authoritative Projects selection cannot safely be used.
    private static string? ResolveRegisteredProjectsDirectory(object? registeredInstall, object? registeredData,
        object? registeredProjects, string installDirectory, string dataDirectory)
    {
        if (registeredInstall is not string install || registeredData is not string data ||
            !Path.IsPathFullyQualified(install) || !Path.IsPathFullyQualified(data) ||
            !SameDirectory(install, installDirectory) || !SameDirectory(data, dataDirectory)) return null;
        if (registeredProjects is not string projects || string.IsNullOrWhiteSpace(projects) ||
            projects.Length > 2048 || projects.Any(char.IsControl) || !Path.IsPathFullyQualified(projects))
            throw new InvalidDataException("The installed Projects registration is missing or invalid. Review it through Cerebral Setup.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(projects));
    }

    private static bool SameDirectory(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static string? PortableChild(string name) => PortableRoot.Value is { } root ? Path.Combine(root, name) : null;

    private static string? EnvironmentHomeChild(string name)
    {
        var home = EnvironmentPath(HomeEnvironmentVariable);
        return home is null ? null : Path.Combine(home, name);
    }

    private static string InferInstallDirectory(string applicationDirectory)
    {
        var current = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(current);
        var parent = Path.GetDirectoryName(current);
        if ((name is "Worker" or "Desktop") &&
            parent is not null && Path.GetFileName(parent).Equals("Applications", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(parent) ?? parent;
        return name is "Worker" or "Integrator" or "Orchestrator" ? Path.GetDirectoryName(current)! : current;
    }

    private static string? ResolvePortableRoot(string applicationDirectory)
    {
        try
        {
            var app = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidates = new List<string>();
            for (var candidate = app; candidates.Count < 3 && !string.IsNullOrWhiteSpace(candidate); candidate = Path.GetDirectoryName(candidate))
                if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidates.Add(candidate);
            foreach (var candidate in candidates)
            {
                var marker = Path.Combine(candidate!, "installation.ini");
                if (!File.Exists(marker) || new FileInfo(marker).Length > 65_536) continue;
                var section = "";
                var product = "";
                var portable = "";
                foreach (var raw in File.ReadLines(marker).Take(512))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] is ';' or '#') continue;
                    if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                    if (!section.Equals("Cerebral", StringComparison.OrdinalIgnoreCase)) continue;
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    var key = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim();
                    if (key.Equals("Product", StringComparison.OrdinalIgnoreCase)) product = value;
                    else if (key.Equals("Portable", StringComparison.OrdinalIgnoreCase)) portable = value;
                }
                if (product.Equals("PegasusCerebral", StringComparison.Ordinal) && portable == "1") return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    private static string? EnvironmentPath(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : null;
    }

    // The installer defaults are a registration contract, not an implicit writer-path migration.
    // An unregistered legacy process keeps its existing local profile until that profile is reviewed.
    private static RegistrationProjection ProjectRegistration(bool keyPresent, object? install, object? baseDirectory,
        object? dataDirectory, object? userDataDirectory, bool unavailable = false)
    {
        if (unavailable) return new(RegistrationState.Unavailable, null, null, null, null);
        if (!keyPresent)
        {
            var program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pegasus", "Cerebral");
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pegasus", "Cerebral");
            return new(RegistrationState.Absent, program, data, data, Path.Combine(data, "Users"));
        }
        var registeredInstall = NormalizeRegisteredRoot(install);
        var registeredBase = NormalizeRegisteredRoot(baseDirectory);
        var registeredData = NormalizeRegisteredRoot(dataDirectory);
        var registeredUserData = NormalizeRegisteredRoot(userDataDirectory);
        return registeredInstall is null || registeredBase is null || registeredData is null || registeredUserData is null
            ? new(RegistrationState.Partial, null, null, null, null)
            : new(RegistrationState.Complete, registeredInstall, registeredBase, registeredData, registeredUserData);
    }

    private static string? NormalizeRegisteredRoot(object? value)
    {
        if (value is not string raw || string.IsNullOrWhiteSpace(raw) || raw.Length > 2048 ||
            raw.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '|' or '?' or '*') ||
            raw.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(raw) || raw.AsSpan(2).Contains(':')) return null;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            return full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase)
                ? null : full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static string? RegisteredStringValue(RegistryValueKind kind, object? value) =>
        kind == RegistryValueKind.String ? value as string : null;

    private static RegistrationProjection ReadRegistration()
    {
        if (!OperatingSystem.IsWindows()) return ProjectRegistration(false, null, null, null, null);
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"Software\Pegasus\Cerebral", false);
            string? ReadRoot(string name)
            {
                if (key is null) return null;
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return value is null ? null : RegisteredStringValue(key.GetValueKind(name), value);
            }
            return key is null
                ? ProjectRegistration(false, null, null, null, null)
                : ProjectRegistration(true, ReadRoot("InstallDir"), ReadRoot("BaseDir"),
                    ReadRoot("DataDir"), ReadRoot("UserDataDir"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return ProjectRegistration(false, null, null, null, null, unavailable: true); }
    }

    private static string? RegisteredPath(string name) => SelectRegisteredPath(ReadRegistration(), name);

    private static string? SelectRegisteredPath(RegistrationProjection registration, string name) => registration.State switch
    {
        RegistrationState.Complete => name switch
        {
            "BaseDir" => registration.BaseDirectory,
            "DataDir" => registration.DataDirectory,
            "UserDataDir" => registration.UserDataDirectory,
            _ => throw new ArgumentException("Unknown Cerebral data root.", nameof(name))
        },
        RegistrationState.Absent => null,
        RegistrationState.Partial => throw new InvalidDataException("Cerebral's registered data roots are incomplete or invalid. Review them through Cerebral Setup before opening stored state."),
        RegistrationState.Unavailable => throw new InvalidDataException("Cerebral's registered data roots could not be read. Review registry access before opening stored state."),
        _ => throw new InvalidOperationException("Unknown Cerebral registration state.")
    };

    private static string UserFolderName()
    {
        var raw = Environment.UserDomainName + "." + Environment.UserName;
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var value = new string(raw.Select(x => invalid.Contains(x) || char.IsControl(x) ? '_' : x).ToArray()).Trim();
        return string.IsNullOrEmpty(value) ? "current-user" : value;
    }
}
