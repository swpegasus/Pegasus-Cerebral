namespace Cerebral.Worker;

/// <summary>Selects bundled service code from the current admitted payload layout.</summary>
internal static class WorkerPluginPaths
{
    internal static string ResolveServiceRoot(string? explicitPath, bool localRun, bool installedPipe,
        bool groupedPortable, string installDirectory, string workerBaseDirectory)
    {
        if (explicitPath is not null)
        {
            if (!localRun || !Path.IsPathFullyQualified(explicitPath))
                throw new ArgumentException("An explicit service plugin folder requires --local-run and an absolute path.");
            return explicitPath;
        }

        var root = installedPipe || groupedPortable ? installDirectory : workerBaseDirectory;
        return Path.Combine(root, "Plugins", "Service");
    }
}
