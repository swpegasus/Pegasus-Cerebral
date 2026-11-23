using Cerebral.Client;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class InstallerProjectPathTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: Windows installer project paths"); return; }
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-installer-path-validation");
        var data = Path.Combine(root, "Data");
        var install = Path.Combine(root, "Application");
        var cortex = new InstalledCortexLocation(Path.Combine(root, "Cortex"), Path.Combine(root, "CortexData"));
        string Validate(string path) => ManagementCommands.ValidateInstallerProjectDirectory(path, data, install, cortex);
        void Reject(string path)
        {
            try { Validate(path); throw new InvalidOperationException("Unsafe project path accepted: " + path); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or UnauthorizedAccessException) { checks++; }
        }
        Check(Validate(Path.Combine(data, "Projects")) == InstalledCortexDiscovery.ValidateLocalDirectory(Path.Combine(data, "Projects")),
            "The default data/Projects path must be accepted without creating it.");
        Check(Validate(Path.Combine(root, "Separate projects")) == InstalledCortexDiscovery.ValidateLocalDirectory(Path.Combine(root, "Separate projects")),
            "A dedicated project directory outside service data must be accepted.");
        Reject(data);
        Reject(root);
        Reject(install);
        Reject(Path.Combine(install, "Worker", "Projects"));
        Reject(Path.Combine(install, "Integrator", "Projects"));
        Reject(Path.Combine(install, "Orchestrator", "Projects"));
        Reject(cortex.InstallDirectory);
        Reject(Path.Combine(cortex.DataDirectory, "Projects"));
        Reject(Path.GetPathRoot(root)!);
        Reject(@"relative\Projects");
        Reject(@"\\server\share\Projects");
        Reject(Path.Combine(root, "..", "Projects"));
        Reject(Path.Combine(root, "CON"));
        Reject(Path.Combine(root, "Projects."));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Reject(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AnyProduct", "Projects"));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // Invalid argument shapes fail before Cortex lookup, store creation, service/ACL operations or process execution.
        foreach (var args in new[]
        {
            new[] { "manage", "project-path", "--data" },
            new[] { "manage", "project-path", "--data", data, "--data", data, "--directory", install },
            new[] { "manage", "set-project-path", "--data", data, "--directory", install, "--unknown", "value" },
            new[] { "manage", "set-project-path", "--data", data, "--directory", "" },
            new[] { "manage", "project-path", "--projects", Path.Combine(data, "Projects") }
        })
            Check(await ManagementCommands.TryRunAsync(args) == 1, "Invalid installer path command arguments must return a failure.");
        Console.WriteLine($"PASS: {checks} installer project-path checks (read-only validation; no service, ACL or installer execution).");
    }
}
