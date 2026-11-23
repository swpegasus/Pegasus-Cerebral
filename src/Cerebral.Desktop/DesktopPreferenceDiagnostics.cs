using System.IO;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Process-selected UI and diagnostic paths with explicit private isolation.</summary>
public static class DesktopPreferenceDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string[] LegacyUiFiles = ["preferences.json", "string-preferences.json",
        "Orchestrator.window.json", "Integrator.window.json"];
    private static string? isolatedRoot;
    private static bool used;
    private static bool normalUiCompatibilityChecked;

    internal static string DefaultUiDirectory => Path.Combine(
        InstallationPaths.CurrentUserDataDirectory, "Ui");
    private static string LegacyUiDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral", "Ui");

    internal static string UiDirectory
    {
        get
        {
            lock (Gate)
            {
                used = true;
                if (isolatedRoot is { } privateRoot) return Path.Combine(privateRoot, "Ui");
                var paths = DesktopInstancePaths.Current;
                var selected = Path.Combine(paths.UserDataDirectory, "Ui");
                if (!normalUiCompatibilityChecked)
                {
                    if (!paths.SourceLocal && !paths.Portable &&
                        ShouldImportLegacyUi(paths, InstallationPaths.CurrentUserDataDirectory))
                        CopyKnownLegacyUiFiles(LegacyUiDirectory, selected);
                    normalUiCompatibilityChecked = true;
                }
                return selected;
            }
        }
    }

    public static string DiagnosticsDirectory
    {
        get
        {
            lock (Gate)
            {
                used = true;
                return ProjectDiagnosticsDirectory(isolatedRoot, () => DesktopInstancePaths.Current.UserDataDirectory);
            }
        }
    }

    internal static string ProjectDiagnosticsDirectory(string? privateRoot, Func<string> selectedUserRoot) =>
        Path.Combine(privateRoot ?? selectedUserRoot(), "Diagnostics");

    internal static bool ShouldImportLegacyUi(DesktopInstancePaths paths, string ordinaryUserRoot) =>
        !paths.SourceLocal && !paths.Portable &&
        paths.UserDataDirectory.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ordinaryUserRoot)),
            StringComparison.OrdinalIgnoreCase);

    // Only the four historic UI files cross to an already selected normal user root.
    internal static void CopyKnownLegacyUiFiles(string legacyDirectory, string selectedDirectory)
    {
        try
        {
            var selected = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(selectedDirectory);
            if (LegacyUiFiles.All(name => SelectedFileExists(Path.Combine(selected, name)))) return;
            var legacy = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(legacyDirectory);
            if (legacy.Equals(selected, StringComparison.OrdinalIgnoreCase)) return;
            var legacyAttributes = ExistingAttributes(legacy);
            if (legacyAttributes is null) return;
            if ((legacyAttributes.Value & FileAttributes.Directory) == 0 ||
                (legacyAttributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The previous UI directory is not an ordinary directory.");
            foreach (var name in LegacyUiFiles)
            {
                var destination = Path.Combine(selected, name);
                if (SelectedFileExists(destination)) continue;
                var source = Path.Combine(legacy, name);
                var sourceAttributes = ExistingAttributes(source);
                if (sourceAttributes is null) continue;
                RequireOrdinaryFile(sourceAttributes.Value);
                Directory.CreateDirectory(selected);
                _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(selected);
                var temporary = Path.Combine(selected, ".cerebral-ui-copy-" + Guid.NewGuid().ToString("N") + ".new");
                try
                {
                    File.Copy(source, temporary, overwrite: false);
                    try { File.Move(temporary, destination, overwrite: false); }
                    catch (IOException) { if (!SelectedFileExists(destination)) throw; }
                }
                finally { File.Delete(temporary); }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            throw new InvalidOperationException(
                "Cerebral could not preserve the existing UI preferences in the selected user data directory. Check access to both UI directories before continuing.", error);
        }
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool SelectedFileExists(string path)
    {
        var attributes = ExistingAttributes(path);
        if (attributes is null) return false;
        RequireOrdinaryFile(attributes.Value);
        return true;
    }

    private static void RequireOrdinaryFile(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("A known UI preference path is not an ordinary file.");
    }

    internal static string ConnectionPath(string normalPath)
    {
        lock (Gate) { used = true; return isolatedRoot is null ? normalPath : Path.Combine(isolatedRoot, "connection.json"); }
    }

    /// <summary>Must precede all preference/connection access. The caller owns a new empty temporary directory.</summary>
    public static void UseIsolatedProcess(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0'))
            throw new ArgumentException("Diagnostic preference isolation requires an absolute temporary directory.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("cerebral-", StringComparison.Ordinal) ||
            !Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
            throw new ArgumentException("Use a new empty owned cerebral- directory beneath the temporary directory.");
        SelectProcessRoot(full);
    }

    /// <summary>Selects an explicit private desktop state directory before any preference access.
    /// Unlike the temporary diagnostic lane, this directory may retain this interface's preferences.</summary>
    public static void UseProcessLocalDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.Contains('\0'))
            throw new ArgumentException("Private desktop state requires an absolute directory.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Create the selected private desktop directory first.");
        SelectProcessRoot(full);
    }

    private static void SelectProcessRoot(string full)
    {
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Diagnostic preference isolation cannot traverse links.");
        lock (Gate)
        {
            if (used || isolatedRoot is not null)
                throw new InvalidOperationException("Select diagnostic preferences once, before any preference access.");
            isolatedRoot = full;
        }
    }

    /// <summary>Focused real-file checks, followed by the isolated application in this same fresh process.</summary>
    public static int VerifyAndUseIsolatedProcess(string directory)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        Check(DefaultUiDirectory == Path.Combine(InstallationPaths.CurrentUserDataDirectory, "Ui"),
            "Normal UI storage must follow the configured user data root.");
        // Pure default projection does not initialize a store or make isolation too late.
        lock (Gate)
        {
            Check(!used && isolatedRoot is null, "The normal preference selection must be untouched before this diagnostic.");
        }
        var relativeRejected = false;
        try { UseIsolatedProcess("relative"); } catch (ArgumentException) { relativeRejected = true; }
        Check(relativeRejected, "A relative diagnostic root must be rejected without changing the selected store.");
        UseIsolatedProcess(directory);
        UiPreferenceStore.WriteBool("acceptance.isolated", true);
        UiPreferenceStore.WriteString("acceptance.isolated", "owned");
        Check(UiPreferenceStore.ReadBool("acceptance.isolated", false) &&
            UiPreferenceStore.ReadString("acceptance.isolated") == "owned", "Isolated values must round trip.");
        Check(File.Exists(Path.Combine(directory, "Ui", "preferences.json")) &&
            File.Exists(Path.Combine(directory, "Ui", "string-preferences.json")), "UI writes must stay in the owned root.");
        Check(ConnectionStore.FilePath == Path.Combine(Path.GetFullPath(directory), "connection.json"),
            "Connection storage must use the same owned root.");
        var initial = ConnectionStore.Read();
        _ = ConnectionStore.SaveFromIntegrator(new ConnectionPreferences { AutoStartLocalServices = false }, initial.Revision);
        Check(File.Exists(ConnectionStore.FilePath), "The isolated connection write must be durable.");
        var rejected = false;
        try { UseIsolatedProcess(directory); } catch (InvalidOperationException) { rejected = true; }
        // The directory is no longer empty; configuration must still fail, regardless of validation order.
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "A process cannot reconfigure its preference root.");
        return checks;
    }
}
