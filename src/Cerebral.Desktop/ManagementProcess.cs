using System.Diagnostics;
using System.IO;
using Cerebral.Core;
using Microsoft.Win32;

namespace Cerebral.Desktop;

public static class ManagementProcess
{
    public static string WorkerPath => ApplicationPath("Worker");

    /// <summary>The running payload root. Source workspace discovery only locates explicitly built sibling applications.</summary>
    public static string SourceProgramDirectory => InstallationPaths.InstallDirectory;

    /// <summary>Installed management requires registered identity, not merely a complete copied payload.</summary>
    public static bool UsesRegisteredInstallation => RegisteredRootMatches(InstallationPaths.InstallDirectory,
        RegisteredInstallRoot(), InstallationPaths.IsPortable);

    internal static bool RegisteredRootMatches(string runningRoot, string? registeredRoot, bool portable) =>
        !portable && registeredRoot is not null &&
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(runningRoot)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredRoot)), StringComparison.OrdinalIgnoreCase);

    private static string? RegisteredInstallRoot()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"Software\Pegasus\Cerebral", false);
            return key?.GetValue("InstallDir") is string value && Path.IsPathFullyQualified(value) && !value.Contains('\0')
                ? Path.GetFullPath(value) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return null; }
    }

    private static string? SourceProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Pegasus.Cerebral.slnx"))) return directory.FullName;
        return null;
    }

    public static bool UsesInstalledLayout
    {
        get
        {
            var shared = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Cerebral.Worker.exe"));
            var deployed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Worker", "Cerebral.Worker.exe"));
            return File.Exists(shared) || File.Exists(deployed);
        }
    }

    public static string ApplicationPath(string name)
    {
        if (name is not ("Worker" or "Integrator" or "Orchestrator")) throw new ArgumentException("Unknown Cerebral application.");
        var shared = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"Cerebral.{name}.exe"));
        if (File.Exists(shared)) return shared;
        // Accept the pre-0.3 isolated application layout long enough for the current
        // program generation to stop itself or launch Configuration during an update.
        var deployed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", name, $"Cerebral.{name}.exe"));
        if (File.Exists(deployed)) return deployed;
        // Source builds retain the normal .NET output folders; do not require a publish or installer.
        if (SourceProjectDirectory() is { } sourceDirectory)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Release";
            if (configuration is not ("Debug" or "Release")) configuration = "Release";
            var target = name == "Worker" ? "net10.0" : "net10.0-windows";
            var candidate = Path.Combine(sourceDirectory, "src", $"Cerebral.{name}", "bin", configuration, target, $"Cerebral.{name}.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Build Cerebral.{name} locally or install the complete application layout.", shared);
    }

    public static void OpenApplication(string name)
    {
        var path = ApplicationPath(name);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)!, CreateNoWindow = true };
        var paths = DesktopInstancePaths.Current;
        if (paths.Portable && PortablePackageAdmission.IsValidatedAt(paths.InstallDirectory) &&
            name is ("Integrator" or "Orchestrator"))
            start.ArgumentList.Add("--portable-package");
        if (paths.SourceLocal)
        {
            start.Environment[DesktopInstancePaths.SourceDataEnvironmentVariable] = paths.DataDirectory;
            start.Environment[DesktopInstancePaths.SourceProgramEnvironmentVariable] = paths.InstallDirectory;
            start.Environment[ConnectionStore.ProcessLocalDataEnvironmentVariable] = paths.ConnectionDataDirectory;
        }
        else
        {
            start.Environment.Remove(DesktopInstancePaths.SourceDataEnvironmentVariable);
            start.Environment.Remove(DesktopInstancePaths.SourceProgramEnvironmentVariable);
            start.Environment.Remove(ConnectionStore.ProcessLocalDataEnvironmentVariable);
        }
        using var process = Process.Start(start) ?? throw new IOException($"Could not start Cerebral.{name}.");
    }

    public static async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        if (!File.Exists(WorkerPath)) throw new FileNotFoundException("The management executable is unavailable. Build or install the complete deployment layout.", WorkerPath);
        var start = new ProcessStartInfo(WorkerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("manage"); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the management command.");
        var output = ReadBoundedAsync(process.StandardOutput, token); var errors = ReadBoundedAsync(process.StandardError, token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { throw new OperationCanceledException("Management is still running independently; inspect service status before retrying.", token); }
        var text = (await output) + Environment.NewLine + (await errors);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Management exited with code {process.ExitCode}. {text}");
        return string.IsNullOrWhiteSpace(text) ? "Management operation completed." : text.Trim();
    }

    /// <summary>Runs one bounded management action through Windows elevation. Output is deliberately
    /// not captured across the elevation boundary; callers verify the resulting protected state.</summary>
    public static async Task RunElevatedAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Elevated Cerebral management requires Windows.");
        if (!UsesInstalledLayout || !UsesRegisteredInstallation)
            throw new InvalidOperationException("Installed Worker setup requires the registered installation's complete payload. Copied or source binaries cannot register themselves as the installed service.");
        var start = new ProcessStartInfo(WorkerPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(WorkerPath)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("manage");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start elevated Cerebral management.");
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("Elevated management may still be running independently. Inspect the installed binding and service before retrying.", token);
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Elevated Cerebral management exited with code {process.ExitCode}. The installed service remains stopped until its state is reviewed.");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[4096]; int count; var omitted = false;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            var available = Math.Max(0, 128_000 - result.Length); result.Append(buffer, 0, Math.Min(count, available));
            omitted |= count > available;
        }
        if (omitted) result.AppendLine("\n[additional output omitted]"); return result.ToString();
    }
}
