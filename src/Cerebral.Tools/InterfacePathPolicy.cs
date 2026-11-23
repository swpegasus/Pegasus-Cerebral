using Cerebral.Core;

namespace Cerebral.Tools;

// Presentation uses the same saved project grants and path confinement as direct tools.
public static class InterfacePathPolicy
{
    public static IReadOnlyList<string> AllowedKinds(AgentProfile profile) =>
        AllowedKinds(profile, allowHelper: false);

    private static IReadOnlyList<string> AllowedKinds(AgentProfile profile, bool allowHelper)
    {
        if (profile.Role is not ("lead" or "helper") || !allowHelper && profile.Role != "lead" ||
            profile.WorkerId != "local" || !profile.Capabilities.Contains("filesystem.read", StringComparer.Ordinal)) return [];
        var kinds = new List<string>();
        if (ToolPolicy.AllowsTool(profile, "file.read")) kinds.Add("file");
        if (ToolPolicy.AllowsTool(profile, "file.list")) kinds.Add("folder");
        if (OperatingSystem.IsWindows() && ToolPolicy.AllowsTool(profile, "file.read") &&
            ToolPolicy.AllowsTool(profile, "process.run") && profile.Capabilities.Contains("process.execute", StringComparer.Ordinal)) kinds.Add("application");
        if (OperatingSystem.IsWindows() && ToolPolicy.AllowsTool(profile, "file.read") &&
            ToolPolicy.AllowsTool(profile, "process.run") && profile.Capabilities.Contains("process.execute", StringComparer.Ordinal) &&
            profile.Capabilities.Contains("desktop.plugins.manage", StringComparer.Ordinal)) kinds.Add("plugin");
        return kinds;
    }

    public static string Validate(AgentProfile profile, string kind, string path, bool fullAccess = false) =>
        ValidatePresentation(profile, kind, path, fullAccess).Path;

    public static InterfacePresentationPath ValidatePresentation(AgentProfile profile, string kind, string path, bool fullAccess = false)
    {
        if (!AllowedKinds(profile).Contains(kind, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("This local lead profile does not grant interface presentation of " + kind + ".");
        return ValidatePresentationPath(new(profile, "interface", "interface", FullAccess: fullAccess), kind, path);
    }

    /// <summary>Check local lead/helper plugin eligibility and scoped package metadata only.
    /// The Worker must separately validate durable root/own-conversation or approved-QA authority.</summary>
    public static InterfacePresentationPath ValidateNativePluginPresentation(ToolExecutionContext context, string path)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!AllowedKinds(context.Profile, allowHelper: true).Contains("plugin", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("This local profile does not grant native plugin presentation.");
        return ValidatePresentationPath(context, "plugin", path);
    }

    private static InterfacePresentationPath ValidatePresentationPath(ToolExecutionContext context, string kind, string path)
    {
        if (path.Length > 32768 || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Interface requests require an absolute path inside a selected project root.");
        // Metadata inspection needs read access; retain the host's actual path ceiling and flags.
        var exact = ToolPolicy.PathInScope(path, context, ScopedPathAccess.Read, mustExist: true);
        string? generatedSourceRoot = null;
        if (kind == "folder")
        {
            if (!Directory.Exists(exact)) throw new DirectoryNotFoundException("The requested folder does not exist.");
        }
        else
        {
            if (!File.Exists(exact)) throw new FileNotFoundException("The requested file does not exist.");
            if (kind == "application" && !exact.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only an existing local .exe beneath a selected project root, explicit folder grant, or active Full Access can be presented as an application.");
            if (kind == "plugin")
            {
                if (!Path.GetFileName(exact).Equals(NativeDesktopPluginPackage.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("A native plugin presentation requires the package's exact desktop-plugin.json beneath a selected project root, explicit folder grant, or active Full Access.");
                NativeDesktopPluginPackage.Inspect(exact);
                generatedSourceRoot = PluginScaffoldToolExtension.ValidateDesktopPresentationSource(context, exact);
            }
        }
        return new(exact, generatedSourceRoot);
    }
}

public sealed record InterfacePresentationPath(string Path, string? GeneratedSourceRoot);
