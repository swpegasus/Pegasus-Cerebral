using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

/// <summary>Explicit deployment mapping; never inferred from a Worker, current directory or PATH.</summary>
internal sealed record ConfigurationLaunchEntry(string Path, string Sha256)
{
    internal static ConfigurationLaunchEntry Parse(string path, string sha256)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.Any(char.IsControl) ||
            path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Configuration entry must be an explicit absolute local application path.");
        var full = System.IO.Path.GetFullPath(path);
        var name = System.IO.Path.GetFileName(full);
        if (!name.Equals("Cerebral.Integrator.dll", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Cerebral.ConfigurationHost.dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Configuration entry must name a supported managed Cerebral Configuration application.");
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(System.IO.Path.GetDirectoryName(full)!);
        if (sha256 is null || sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Configuration entry requires its exact SHA-256 deployment pin.");
        return new(full, sha256.ToLowerInvariant());
    }

    internal Task<ConfigurationLaunchSubmission> LaunchAsync(ExistingWorkerDesktopOptions target, CancellationToken token) =>
        LaunchAsync(target, token, dispatchStart: null, ownerBinding: null);

    internal async Task<ConfigurationLaunchSubmission> LaunchAsync(ExistingWorkerDesktopOptions target, CancellationToken token,
        Func<Func<ConfigurationLaunchSubmission>, ConfigurationLaunchSubmission>? dispatchStart,
        ConfigurationManagedPluginOwnerBinding? ownerBinding = null)
    {
        target = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", target.Endpoint.PipeName, target.Endpoint.InstanceId,
            target.WorkerDataDirectory, target.DesktopDataDirectory, target.InstallationId])!;
        ownerBinding?.RequireForChild(target);
        // Revalidate the mapping at use, including link checks. A stale/missing entry is an error;
        // it never falls back to installed configuration, discovery, HTTP or a different Worker.
        var current = Parse(Path, Sha256);
        var file = new FileInfo(current.Path);
        if (!file.Exists) throw new FileNotFoundException("The configured Configuration application is missing. Refresh the application mapping before opening Settings.");
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The configured Configuration application cannot be a linked file.");
        using var input = new FileStream(current.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(input, token);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(current.Sha256)))
            throw new InvalidDataException("The configured Configuration application has changed. Refresh the application mapping before opening Settings.");
        string? assembly;
        try { assembly = AssemblyName.GetAssemblyName(current.Path).Name; }
        catch (BadImageFormatException error) { throw new InvalidDataException("The configured entry is not a supported managed Configuration application.", error); }
        if (assembly is not ("Cerebral.Integrator" or "Cerebral.ConfigurationHost"))
            throw new InvalidDataException("The configured entry is not a supported Cerebral Configuration application.");
        var start = BuildStartInfo(current.Path, target, ResolveManagedHost(), ownerBinding);
        token.ThrowIfCancellationRequested();
        // Run the ordinary Program exactly once. It owns absent→primary and present→activation
        // through its existing fixed-binding singleton, including refusing another Worker owner.
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var startCalled = 0;
            ConfigurationLaunchSubmission Start()
            {
                if (Interlocked.Exchange(ref startCalled, 1) != 0)
                    throw new InvalidOperationException("This Configuration launch attempt has already been submitted.");
                token.ThrowIfCancellationRequested();
                var process = Process.Start(start) ?? throw new IOException("Configuration could not be started.");
                try { return new ConfigurationLaunchSubmission(process, current.Path); }
                catch { process.Dispose(); throw; }
            }
            // A semantic caller dispatches its last identity/selection check and this
            // exact start together. The immutable target and validated argv stay here.
            return dispatchStart is null ? Start() : dispatchStart(Start);
        }, token);
    }

    internal static ProcessStartInfo BuildStartInfo(string entry, ExistingWorkerDesktopOptions target, string managedHost)
        => BuildStartInfo(entry, target, managedHost, ownerBinding: null);

    internal static ProcessStartInfo BuildStartInfo(string entry, ExistingWorkerDesktopOptions target, string managedHost,
        ConfigurationManagedPluginOwnerBinding? ownerBinding)
    {
        // The caller supplies the already validated fixed binding; normal Configuration parses it
        // again before preference access, instance ownership, discovery or any Worker request.
        ownerBinding?.RequireForChild(target);
        var start = new ProcessStartInfo(managedHost)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetDirectoryName(entry)!
        };
        foreach (var value in new[] { entry, "--attach-existing", target.Endpoint.PipeName, target.Endpoint.InstanceId,
                     target.WorkerDataDirectory, target.DesktopDataDirectory, target.InstallationId }) start.ArgumentList.Add(value);
        ownerBinding?.AppendArguments(start.ArgumentList);
        return start;
    }

    internal static string ResolveManagedHost() => SemanticControlIdentity.ResolveManagedHost();
}

/// <summary>A bounded observed launch handle; disposing it never terminates the standalone app.</summary>
internal sealed class ConfigurationLaunchSubmission : IDisposable
{
    internal Process Process { get; }
    public int ProcessId { get; }
    public long ProcessStartUtcTicks { get; }
    public string EntryPath { get; }
    internal ConfigurationLaunchSubmission(Process process, string entryPath)
    {
        Process = process; ProcessId = process.Id;
        ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks; EntryPath = entryPath;
    }
    public void Dispose() => Process.Dispose();
}
