using System.IO;
using Microsoft.Win32;

namespace Cerebral.Desktop;

/// <summary>Owns the current user's opt-in Cerebral tray launch registration.</summary>
public static class WindowsStartupRegistration
{
    public const string ValueName = "PegasusCerebral";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string ExecutablePath => Path.Combine(Cerebral.Core.InstallationPaths.InstallDirectory,
        "Applications", "Desktop", "Cerebral.Orchestrator.exe");

    public static string Command => $"\"{ExecutablePath}\" --tray";

    public static bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(ExecutablePath) &&
        !Cerebral.Core.InstallationPaths.IsPortable && ManagementProcess.UsesRegisteredInstallation;

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string value &&
            value.Equals(Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows startup registration is available only on Windows.");
        if (enabled && !IsAvailable) throw new InvalidOperationException("Install Pegasus Cerebral before enabling automatic startup.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new IOException("The current user's Windows startup settings could not be opened.");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}
