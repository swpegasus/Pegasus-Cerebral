namespace Cerebral.BrowserPlugin;

internal sealed record BrowserRuntimeSelection(string Executable, string Generation, string Source);

/// <summary>
/// Resolves only the system-wide Microsoft Edge installation that Windows normally supplies.
/// It never uses a user profile, downloads a browser, searches PATH, or accepts an agent path.
/// </summary>
internal static class BrowserSystemRuntime
{
    internal static BrowserRuntimeSelection Resolve(
        BrowserResources resources,
        BrowserResourceState state,
        string? testExecutable = null,
        Func<string?>? systemEdgeResolver = null)
    {
        if (testExecutable is not null)
            return new(Path.GetFullPath(testExecutable), "existing-browser-compatibility-test", "test-only");

        if (state.Active is not null)
            return new(resources.Validate(state.Active), state.Active.Generation, "activated-resource");

        var edge = (systemEdgeResolver ?? FindEdgeExecutable)();
        if (edge is not null)
            return new(edge, "system-edge", "windows-system-browser");

        throw new BrowserRejectedException("runtime-missing",
            "Microsoft Edge was not found in its system install location. Stage and activate a reviewed Chromium resource generation before opening a tab.");
    }

    internal static string? FindEdgeExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var root in ProgramFilesRoots())
        {
            var candidate = Path.GetFullPath(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        return roots
            .Where(static value => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
