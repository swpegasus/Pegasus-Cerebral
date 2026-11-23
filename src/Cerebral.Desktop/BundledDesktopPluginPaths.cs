using System.IO;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Resolves immutable bundled plug-in code independently from each plug-in's writable data directory.</summary>
public static class BundledDesktopPluginPaths
{
    public static string DirectoryPath => Path.Combine(InstallationPaths.InstallDirectory, "Plugins", "Desktop");

    /// <summary>Only fresh optional installs use this policy; the catalog preserves existing choices and removal markers.</summary>
    public static bool? EnableOnFirstInstall(string manifestPath) => NativeDesktopPluginPackage.Inspect(manifestPath).Id switch
    {
        "pegasus.appearance" or "pegasus.collaboration" or "pegasus.goal-card" => false,
        _ => null
    };
}
