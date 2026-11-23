using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Resolves the exact public desktop SDK exposed to generated plugin source.</summary>
internal static class DesktopPluginSdkBinding
{
    internal const string AssemblyName = "Cerebral.Desktop.Plugins.dll";

    internal static string? Resolve(string? explicitPath, bool localRun, bool installedPipe,
        string? installDirectory = null, bool groupedPortable = false)
    {
        if (explicitPath is not null)
        {
            if (!localRun || !Path.IsPathFullyQualified(explicitPath) || !File.Exists(explicitPath) ||
                !Path.GetFileName(explicitPath).Equals(AssemblyName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"An explicit desktop SDK binding requires --local-run and the exact absolute {AssemblyName} path.");
            return Path.GetFullPath(explicitPath);
        }

        if (!installedPipe && !groupedPortable) return null;
        var root = installDirectory ?? InstallationPaths.InstallDirectory;
        var candidate = Path.GetFullPath(Path.Combine(root, "Applications", "Desktop", AssemblyName));
        return File.Exists(candidate) ? candidate : null;
    }
}
