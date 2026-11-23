using System.Diagnostics;
using System.IO;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Desktop;

public sealed record LocalWorkerConnection(LocalWorkerEndpoint Endpoint, string DataDirectory, HostInfo Host)
{
    public ConnectionPreferences ToPreferences(bool autoStart = true) => ConnectionPreferences.ForLocalPipe(Endpoint, DataDirectory, autoStart);
}

/// <summary>Version-one fixed local managed-Worker target. The caller must qualify and hold its
/// code cohort; paths and a matching generation alone do not attest the running managed DLL.</summary>
public sealed record ManagedLocalWorkerLaunchTarget(int Version, string HostPath, string ManagedEntryPointPath,
    string EnginesDirectory, string ServicePluginsDirectory, string? DesktopSdkPath = null);

/// <summary>Version-one fixed apphost Worker target. The caller must qualify and hold both the
/// executable image and its adjacent managed payload; path/generation matching is not code attestation.</summary>
public sealed record AppHostLocalWorkerLaunchTarget(int Version, string AppHostPath, string ManagedEntryPointPath,
    string EnginesDirectory, string ServicePluginsDirectory, string? DesktopSdkPath = null);

/// <summary>Exact discovery generation, supplied only from previously qualified launch provenance.</summary>
public sealed record LocalWorkerGeneration(int Version, LocalWorkerEndpoint Endpoint, int ProcessId, long ProcessStartUtcTicks);

public sealed record LocalWorkerLaunchResult(int Version, LocalWorkerConnection Connection,
    LocalWorkerGeneration Generation, bool Started);

public static partial class LocalDevelopment
{
    /// <summary>Uses protected installed enrollment but a signed-in user's exact process and pipe.
    /// This mode never starts or adopts the Windows service.</summary>
    public static async Task<LocalWorkerConnection?> DiscoverInstalledUserPipeAsync(CancellationToken token)
    {
        var enrollment = RequireInstalledUserEnrollment();
        var data = LocalWorkerDiscovery.NormalizeDataDirectory(enrollment.DataDirectory);
        if (!Directory.Exists(data)) return null;
        var record = LocalWorkerDiscovery.Read(data);
        if (record is null) { LocalWorkerDiscovery.AssertDataAvailable(data); return null; }
        RequireInstalledUserRecord(enrollment, record);
        using var process = LocalWorkerDiscovery.FindProcess(record);
        if (process is null) { LocalWorkerDiscovery.AssertDataAvailable(data); return null; }
        return await ProbePipeAsync(record, token).ConfigureAwait(false);
    }

    public static CerebralClient CreateInstalledUserClient()
    {
        var enrollment = RequireInstalledUserEnrollment();
        var record = LocalWorkerDiscovery.Read(enrollment.DataDirectory)
            ?? throw new InvalidOperationException("The installed user Worker has no discovery record.");
        RequireInstalledUserRecord(enrollment, record);
        using var process = LocalWorkerDiscovery.FindProcess(record)
            ?? throw new InvalidOperationException("The installed user Worker generation is no longer running.");
        return CerebralClient.ForInstalledUserWorker(ExpectedInstalledUserOwner(enrollment, record));
    }

    public static void RequireInstalledServiceToUserTransition()
    {
        var desktop = RequireInstalledUserEnrollment();
        var service = InstalledWorkerBindingStore.ReadRegistered()
            ?? throw new UnauthorizedAccessException("The previous installed service enrollment is unavailable.");
        InstalledUserWorkerAdmission.RequireLegacyTransition(service, desktop);
        if (!InstalledWorkerBinding.PathEquals(DesktopInstancePaths.Current.DataDirectory,
                desktop.DataDirectory, true))
            throw new UnauthorizedAccessException("The selected desktop data root differs from protected enrollment.");
    }

    public static async Task StopInstalledUserPipeAsync(CancellationToken token)
    {
        var enrollment = RequireInstalledUserEnrollment();
        var record = LocalWorkerDiscovery.Read(enrollment.DataDirectory)
            ?? throw new InvalidOperationException("The installed user Worker has no discovery record.");
        RequireInstalledUserRecord(enrollment, record);
        await StopPipeAsync(record.Endpoint, token, record.DataDirectory).ConfigureAwait(false);
    }

    public static async Task<LocalWorkerLaunchResult> StartInstalledUserPipeAsync(CancellationToken token)
    {
        var enrollment = RequireInstalledUserEnrollment();
        var existing = await DiscoverInstalledUserPipeAsync(token).ConfigureAwait(false);
        if (existing is not null)
        {
            var current = LocalWorkerDiscovery.Read(enrollment.DataDirectory)
                ?? throw new InvalidOperationException("The installed desktop Worker generation changed during discovery.");
            RequireInstalledUserRecord(enrollment, current);
            if (current.Endpoint != existing.Endpoint)
                throw new InvalidOperationException("The installed desktop Worker changed during discovery.");
            return new(1, existing, GenerationOf(current), Started: false);
        }
        var root = InstallationPaths.InstallDirectory;
        var workerDirectory = Path.GetDirectoryName(enrollment.WorkerExecutable)!;
        var desktopSdk = Path.Combine(root, "Applications", "Desktop", DesktopPluginSdkBindingName);
        var target = NormalizeAppHostTarget(new AppHostLocalWorkerLaunchTarget(1,
            enrollment.WorkerExecutable, Path.Combine(workerDirectory, "Cerebral.Worker.dll"),
            Path.Combine(workerDirectory, "Engines"), Path.Combine(root, "Plugins", "Service"),
            File.Exists(desktopSdk) ? desktopSdk : null));
        var result = await StartPipeLifecycleAsync(token, enrollment.DataDirectory, target.ExecutablePath,
            target.ServicePluginsDirectory, target.DesktopSdkPath, target, null, enrollment).ConfigureAwait(false);
        return new(1, result.Connection, result.Generation
            ?? throw new InvalidOperationException("Installed user startup returned without its exact generation."), result.Started);
    }

    private const string DesktopPluginSdkBindingName = "Cerebral.Desktop.Plugins.dll";

    private static InstalledUserWorkerBinding RequireInstalledUserEnrollment()
    {
        InstalledUserWorkerAdmission.RequireOrdinaryUserToken();
        var enrollment = InstalledWorkerBindingStore.ReadRegisteredUser()
            ?? throw new UnauthorizedAccessException("The installed desktop Worker enrollment is unavailable.");
        InstalledUserWorkerAdmission.RequireOwnerClient(enrollment, InstalledWorkerWindows.ReadCurrentProcessIdentity());
        return enrollment;
    }

    private static void RequireInstalledUserRecord(InstalledUserWorkerBinding enrollment, LocalWorkerDiscoveryRecord record)
    {
        if (!InstalledWorkerBinding.PathEquals(record.DataDirectory, enrollment.DataDirectory, true) ||
            !InstalledWorkerBinding.PathEquals(record.Executable, enrollment.WorkerExecutable, false))
            throw new UnauthorizedAccessException("The discovered desktop Worker does not match protected enrollment.");
        InstalledUserWorkerAdmission.RequireEndpoint(enrollment, record.Endpoint);
    }

    private static LocalWorkerExpectedOwner ExpectedInstalledUserOwner(InstalledUserWorkerBinding enrollment,
        LocalWorkerDiscoveryRecord record)
    {
        RequireInstalledUserRecord(enrollment, record);
        var current = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        InstalledUserWorkerAdmission.RequireOwnerClient(enrollment, current);
        return new(record.Endpoint, record.ProcessId, record.ProcessStartUtcTicks, record.Executable,
            current.AccountSid, current.SessionId, record.DataDirectory,
            [record.Executable, "--installed-user-pipe", "--data", record.DataDirectory,
                "--pipe-name", record.Endpoint.PipeName, "--pipe-instance", record.Endpoint.InstanceId]);
    }

    private static CerebralClient CreateClientForRecord(LocalWorkerDiscoveryRecord record)
    {
        if (record.Endpoint.PipeName.StartsWith("Pegasus.Cerebral.User.", StringComparison.Ordinal))
        {
            var enrollment = RequireInstalledUserEnrollment();
            return CerebralClient.ForInstalledUserWorker(ExpectedInstalledUserOwner(enrollment, record));
        }
        return new CerebralClient(record.Endpoint);
    }

    /// <summary>Returns only a verified live generation. Missing/stopped discovery returns null;
    /// uncertain identity or an unresponsive existing process never authorizes starting another.</summary>
    public static async Task<LocalWorkerConnection?> DiscoverPipeAsync(CancellationToken token, string? dataDirectory = null)
    {
        var data = LocalWorkerDiscovery.NormalizeDataDirectory(dataDirectory ?? DataDirectory);
        if (!Directory.Exists(data)) return null;
        var record = LocalWorkerDiscovery.Read(data);
        if (record is null) { LocalWorkerDiscovery.AssertDataAvailable(data); return null; }
        using var process = LocalWorkerDiscovery.FindProcess(record);
        if (process is null) { LocalWorkerDiscovery.AssertDataAvailable(data); return null; }
        return await ProbePipeAsync(record, token).ConfigureAwait(false);
    }

    /// <summary>Prove the selected same-user pipe is served by this exact Worker image and generation.
    /// Used before binding a portable native Cortex client to its immutable package executable.</summary>
    public static async Task RequireExactPipeWorkerAsync(LocalWorkerEndpoint endpoint, string dataDirectory,
        string executable, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Exact local Worker pipe identity requires Windows.");
        ArgumentNullException.ThrowIfNull(endpoint); endpoint.Validate();
        var data = LocalWorkerDiscovery.NormalizeDataDirectory(dataDirectory);
        var worker = Path.GetFullPath(executable);
        var record = LocalWorkerDiscovery.Read(data)
            ?? throw new InvalidOperationException("The selected portable Worker has no current discovery record.");
        if (record.Endpoint != endpoint || !record.Executable.Equals(worker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected portable pipe is not served by this package's exact Worker image.");
        using var process = LocalWorkerDiscovery.FindProcess(record)
            ?? throw new InvalidOperationException("The selected portable Worker generation is no longer running.");
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using (var pipe = await InstalledWorkerWindows.ConnectClientAsync(endpoint.PipeName, deadline.Token).ConfigureAwait(false))
        {
            var peer = InstalledWorkerWindows.ObserveServerProcess(pipe);
            if (peer.ProcessId != record.ProcessId ||
                DateTime.FromFileTimeUtc(peer.CreationTimeFileTime).Ticks != record.ProcessStartUtcTicks ||
                !peer.Executable.Equals(worker, StringComparison.OrdinalIgnoreCase) ||
                peer.AccountSid != owner.AccountSid || peer.SessionId != owner.SessionId)
                throw new UnauthorizedAccessException("The portable pipe server is not this signed-in Worker's exact process generation.");
        }
        var connection = await ProbePipeAsync(record, deadline.Token).ConfigureAwait(false);
        if (connection.Endpoint != endpoint || LocalWorkerDiscovery.Read(data) != record)
            throw new InvalidOperationException("The portable Worker changed during native connection preflight.");
    }

    public static Task<LocalWorkerConnection> StartPipeAsync(CancellationToken token, string? dataDirectory = null,
        string? desktopSdkPath = null) =>
        StartPipeCoreAsync(token, dataDirectory ?? DataDirectory, null, null, desktopSdkPath);

    /// <summary>Starts one fixed managed Worker through the existing discovery/control lifecycle.
    /// A live generation requires exact prior provenance; absence of provenance never permits reuse.
    /// Supplying a prior generation is reuse-only: its disappearance does not authorize replacement.
    /// This operation may create discovery/control files and contact the Worker; it is not inspection.</summary>
    public static async Task<LocalWorkerLaunchResult> StartPipeAsync(ManagedLocalWorkerLaunchTarget target,
        CancellationToken token, string? dataDirectory = null, LocalWorkerGeneration? expectedExistingGeneration = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local Worker pipes require Windows.");
        var managed = NormalizeManagedTarget(target);
        if (expectedExistingGeneration is not null) ValidateManagedGeneration(expectedExistingGeneration);
        var result = await StartPipeLifecycleAsync(token, dataDirectory ?? DataDirectory, managed.ExecutablePath,
            managed.ServicePluginsDirectory, managed.DesktopSdkPath, managed, expectedExistingGeneration).ConfigureAwait(false);
        return new(1, result.Connection, result.Generation
            ?? throw new InvalidOperationException("Managed startup returned without its exact generation."), result.Started);
    }

    /// <summary>Starts one fixed apphost Worker through the same qualified discovery/control lifecycle.
    /// A live generation requires exact prior provenance for this executable; supplying a prior
    /// generation is reuse-only, and an uncertain launch never enters the legacy diagnostic route.</summary>
    public static async Task<LocalWorkerLaunchResult> StartPipeAsync(AppHostLocalWorkerLaunchTarget target,
        CancellationToken token, string? dataDirectory = null, LocalWorkerGeneration? expectedExistingGeneration = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local Worker pipes require Windows.");
        var apphost = NormalizeAppHostTarget(target);
        if (expectedExistingGeneration is not null) ValidateManagedGeneration(expectedExistingGeneration);
        var result = await StartPipeLifecycleAsync(token, dataDirectory ?? DataDirectory, apphost.ExecutablePath,
            apphost.ServicePluginsDirectory, apphost.DesktopSdkPath, apphost, expectedExistingGeneration).ConfigureAwait(false);
        return new(1, result.Connection, result.Generation
            ?? throw new InvalidOperationException("Apphost startup returned without its exact generation."), result.Started);
    }

    // Kept internal: diagnostic acceptance can select a rebuilt executable and an empty plugin root,
    // without changing normal application settings or depending on the installed deployment layout.
    internal static Task<LocalWorkerConnection> StartPipeForDiagnosticsAsync(string executablePath, string dataDirectory,
        string pluginDirectory, CancellationToken token) => StartPipeCoreAsync(token, dataDirectory, executablePath, pluginDirectory, null);

    private static async Task<LocalWorkerConnection> StartPipeCoreAsync(CancellationToken token, string dataDirectory,
        string? executablePath, string? pluginDirectory, string? desktopSdkPath)
    {
        var result = await StartPipeLifecycleAsync(token, dataDirectory, executablePath, pluginDirectory, desktopSdkPath,
            null, null).ConfigureAwait(false);
        return result.Connection;
    }

    private sealed record LocalPipeStartResult(LocalWorkerConnection Connection, LocalWorkerGeneration? Generation, bool Started);

    private sealed record QualifiedLocalWorkerLaunchTarget(string ExecutablePath, string ManagedEntryPointPath,
        string EnginesDirectory, string ServicePluginsDirectory, string? DesktopSdkPath, bool UsesManagedHost);

    private static async Task<LocalPipeStartResult> StartPipeLifecycleAsync(CancellationToken token, string dataDirectory,
        string? executablePath, string? pluginDirectory, string? desktopSdkPath,
        QualifiedLocalWorkerLaunchTarget? qualified, LocalWorkerGeneration? expectedExistingGeneration,
        InstalledUserWorkerBinding? installedUserEnrollment = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local Worker pipes require Windows.");
        if (pluginDirectory is not null && (!Path.IsPathFullyQualified(pluginDirectory) || !Directory.Exists(pluginDirectory)))
            throw new ArgumentException("Diagnostic startup requires an existing absolute service-plugin directory.");
        if (desktopSdkPath is not null && (!Path.IsPathFullyQualified(desktopSdkPath) || !File.Exists(desktopSdkPath) ||
            !Path.GetFileName(desktopSdkPath).Equals("Cerebral.Desktop.Plugins.dll", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Source-local startup requires the exact absolute Cerebral.Desktop.Plugins.dll path.");
        var data = LocalWorkerDiscovery.NormalizeDataDirectory(dataDirectory);
        if (installedUserEnrollment is not null)
        {
            InstalledUserWorkerAdmission.RequireOwnerClient(installedUserEnrollment,
                InstalledWorkerWindows.ReadCurrentProcessIdentity());
            if (!InstalledWorkerBinding.PathEquals(data, installedUserEnrollment.DataDirectory, true) ||
                qualified is null || !InstalledWorkerBinding.PathEquals(qualified.ExecutablePath,
                    installedUserEnrollment.WorkerExecutable, false))
                throw new UnauthorizedAccessException("The installed desktop Worker target differs from protected enrollment.");
        }
        Directory.CreateDirectory(data);
        await using var launchLock = await AcquireLaunchLockAsync(data, token).ConfigureAwait(false);
        if (qualified is null)
        {
            var existing = await DiscoverPipeAsync(token, data).ConfigureAwait(false);
            if (existing is not null) return new(existing, null, false);
        }
        else
        {
            // Do not let legacy Discover accept an arbitrary live process before this fence.
            var prior = LocalWorkerDiscovery.Read(data);
            if (prior is not null)
            {
                if (installedUserEnrollment is not null) RequireInstalledUserRecord(installedUserEnrollment, prior);
                using var priorProcess = LocalWorkerDiscovery.FindProcess(prior);
                if (priorProcess is not null)
                {
                    if (installedUserEnrollment is not null)
                    {
                        var installedConnection = await ProbePipeAsync(prior, token).ConfigureAwait(false);
                        return new(installedConnection, GenerationOf(prior), false);
                    }
                    if (expectedExistingGeneration is null || !MatchesQualifiedGeneration(prior, qualified, expectedExistingGeneration))
                        throw new InvalidOperationException("The existing Worker lacks matching managed launch provenance. No replacement was started.");
                    var connection = await ProbePipeAsync(prior, token).ConfigureAwait(false);
                    return new(connection, GenerationOf(prior), false);
                }
            }
            if (expectedExistingGeneration is not null)
                throw new InvalidOperationException("The previously qualified Worker generation is no longer live. No replacement was started.");
        }
        executablePath ??= ManagementProcess.WorkerPath;
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath) ||
            !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an existing absolute Worker executable for local startup.");
        var executable = Path.GetFullPath(executablePath);
        // A verified dead generation is replaceable only while no Worker owns the data lock.
        LocalWorkerDiscovery.AssertDataAvailable(data);
        if (qualified is not null) token.ThrowIfCancellationRequested();
        var endpoint = new LocalWorkerEndpoint(installedUserEnrollment is null
            ? "Pegasus.Cerebral.Local." + Guid.NewGuid().ToString("N")
            : "Pegasus.Cerebral.User." + installedUserEnrollment.BindingId,
            Guid.NewGuid().ToString("N"));
        var record = new LocalWorkerDiscoveryRecord(1, LocalWorkerProtocol.Product, endpoint, data, executable, 0, 0);
        LocalWorkerDiscovery.Write(record);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(qualified?.ManagedEntryPointPath ?? executable)!
        };
        if (qualified is { UsesManagedHost: true }) start.ArgumentList.Add(qualified.ManagedEntryPointPath);
        foreach (var argument in new[] { installedUserEnrollment is null ? "--local-run" : "--installed-user-pipe",
            "--data", data, "--pipe-name", endpoint.PipeName, "--pipe-instance", endpoint.InstanceId })
            start.ArgumentList.Add(argument);
        if (installedUserEnrollment is null)
        {
            if (pluginDirectory is not null) { start.ArgumentList.Add("--service-plugins"); start.ArgumentList.Add(pluginDirectory); }
            if (desktopSdkPath is not null) { start.ArgumentList.Add("--desktop-sdk"); start.ArgumentList.Add(Path.GetFullPath(desktopSdkPath)); }
            if (qualified is not null) { start.ArgumentList.Add("--engines"); start.ArgumentList.Add(qualified.EnginesDirectory); }
        }
        Process process;
        try
        {
            if (qualified is not null) token.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new IOException("The local Worker did not start.");
        }
        catch
        {
            // Preserve the legacy executable path's cleanup. A qualified launch invocation is never
            // replayed on uncertain failure; keep its original zero-PID intent for diagnosis.
            if (qualified is null) LocalWorkerDiscovery.RemoveIfCurrent(record);
            throw;
        }
        using (process)
        {
            // Write the intent before launch, then its exact PID/start identity. A launcher crash in
            // between leaves an explicit uncertain intent, rather than permission to blindly retry.
            record = record with { ProcessId = process.Id, ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks };
            LocalWorkerDiscovery.Write(record);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                        throw new IOException($"The local Worker exited with code {process.ExitCode}. Inspect {Path.Combine(data, "local-worker.log")} before restarting.");
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        var connection = await ProbePipeAsync(record, probe.Token).ConfigureAwait(false);
                        return new(connection, GenerationOf(record), true);
                    }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                    // Health is side-effect free. A cancelled health read can be queried again;
                    // no mutation is ever replayed by this startup loop.
                    catch (LocalWorkerCommandUncertainException) when (probe.IsCancellationRequested && !timeout.IsCancellationRequested) { }
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException error)
            {
                throw new InvalidOperationException("Startup wait ended. The recorded Worker may still be starting. Use Discover/Connect to this generation; it was not killed or restarted.", error);
            }
        }
    }

    private static QualifiedLocalWorkerLaunchTarget NormalizeManagedTarget(ManagedLocalWorkerLaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Version != 1) throw new ArgumentException("The managed Worker launch target version is unsupported.", nameof(target));
        return new(
            NormalizeManagedPath(target.HostPath, false, "dotnet.exe"),
            NormalizeManagedPath(target.ManagedEntryPointPath, false, "Cerebral.Worker.dll"),
            NormalizeManagedPath(target.EnginesDirectory, true, null),
            NormalizeManagedPath(target.ServicePluginsDirectory, true, null),
            target.DesktopSdkPath is null ? null : NormalizeManagedPath(target.DesktopSdkPath, false, "Cerebral.Desktop.Plugins.dll"),
            UsesManagedHost: true);
    }

    private static QualifiedLocalWorkerLaunchTarget NormalizeAppHostTarget(AppHostLocalWorkerLaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Version != 1) throw new ArgumentException("The apphost Worker launch target version is unsupported.", nameof(target));
        var executable = NormalizeManagedPath(target.AppHostPath, false, "Cerebral.Worker.exe");
        var managedEntryPoint = NormalizeManagedPath(target.ManagedEntryPointPath, false, "Cerebral.Worker.dll");
        if (!string.Equals(Path.GetDirectoryName(executable), Path.GetDirectoryName(managedEntryPoint), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An apphost requires its adjacent Cerebral.Worker.dll payload.", nameof(target));
        return new(executable, managedEntryPoint,
            NormalizeManagedPath(target.EnginesDirectory, true, null),
            NormalizeManagedPath(target.ServicePluginsDirectory, true, null),
            target.DesktopSdkPath is null ? null : NormalizeManagedPath(target.DesktopSdkPath, false, "Cerebral.Desktop.Plugins.dll"),
            UsesManagedHost: false);
    }

    private static string NormalizeManagedPath(string path, bool directory, string? requiredFileName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl) ||
            path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') ||
            path.AsSpan(2).Contains(':'))
            throw new ArgumentException("Managed startup requires specific absolute local paths without alternate streams.");
        var full = LocalWorkerDiscovery.NormalizeDataDirectory(path);
        if (directory ? !Directory.Exists(full) : !File.Exists(full))
            throw new ArgumentException("Managed startup requires existing host, Worker, Engine and service-plugin paths.");
        for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Managed startup does not follow linked code or extension paths.");
        if (requiredFileName is not null && !Path.GetFileName(full).Equals(requiredFileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The managed startup file name is unsupported.");
        return full;
    }

    private static void ValidateManagedGeneration(LocalWorkerGeneration expectedExistingGeneration)
    {
        if (expectedExistingGeneration.Version != 1 || expectedExistingGeneration.Endpoint is null || expectedExistingGeneration.ProcessId <= 0 ||
            expectedExistingGeneration.ProcessStartUtcTicks <= 0 || expectedExistingGeneration.ProcessStartUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentException("Supply an exact version-one managed Worker generation.", nameof(expectedExistingGeneration));
        expectedExistingGeneration.Endpoint.Validate();
    }

    private static bool MatchesQualifiedGeneration(LocalWorkerDiscoveryRecord record, QualifiedLocalWorkerLaunchTarget target,
        LocalWorkerGeneration generation) => record.Endpoint == generation.Endpoint && record.ProcessId == generation.ProcessId &&
        record.ProcessStartUtcTicks == generation.ProcessStartUtcTicks &&
        record.Executable.Equals(target.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static LocalWorkerGeneration GenerationOf(LocalWorkerDiscoveryRecord record) =>
        new(1, record.Endpoint, record.ProcessId, record.ProcessStartUtcTicks);

    /// <summary>Stops only the exact saved generation after process, pipe and data ownership checks.</summary>
    public static async Task StopPipeAsync(LocalWorkerEndpoint endpoint, CancellationToken token, string? dataDirectory = null)
    {
        endpoint.Validate();
        var data = LocalWorkerDiscovery.NormalizeDataDirectory(dataDirectory ?? DataDirectory);
        if (!Directory.Exists(data)) throw new InvalidOperationException("The selected local Worker has no discovery record.");
        await using var launchLock = await AcquireLaunchLockAsync(data, token).ConfigureAwait(false);
        var record = LocalWorkerDiscovery.Read(data) ?? throw new InvalidOperationException("The selected local Worker has no discovery record.");
        if (record.Endpoint != endpoint) throw new InvalidOperationException("The selected pipe is an older or different Worker generation. Reconnect before stopping it.");
        using var process = LocalWorkerDiscovery.FindProcess(record);
        if (process is null)
        {
            LocalWorkerDiscovery.AssertDataAvailable(data);
            LocalWorkerDiscovery.RemoveIfCurrent(record);
            return;
        }
        _ = await ProbePipeAsync(record, token).ConfigureAwait(false);
        using var client = CreateClientForRecord(record);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var result = await client.CommandAsync("service.stop-local", cancellationToken: timeout.Token).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "The local Worker refused shutdown.");
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error)
        {
            throw new InvalidOperationException("Shutdown was requested but exit is not yet confirmed. The Worker and discovery record were retained; inspect this generation before another action.", error);
        }
        LocalWorkerDiscovery.AssertDataAvailable(data);
        LocalWorkerDiscovery.RemoveIfCurrent(record);
    }

    private static async Task<LocalWorkerConnection> ProbePipeAsync(LocalWorkerDiscoveryRecord record, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = CreateClientForRecord(record);
        var host = await client.GetAsync<HostInfo>("health", cancellationToken: timeout.Token).ConfigureAwait(false);
        ValidateHost(host);
        if (!host.LocalRun || !Path.GetFullPath(host.DataDirectory).Equals(record.DataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pipe Worker reports a different run mode or data directory. No process was started or stopped.");
        if (LocalWorkerDiscovery.Read(record.DataDirectory) != record)
            throw new InvalidOperationException("The local Worker generation changed during connection. Reconnect to the saved generation.");
        using var process = LocalWorkerDiscovery.FindProcess(record);
        if (process is null) throw new IOException("The recorded Worker exited during connection.");
        return new(record.Endpoint, record.DataDirectory, host);
    }

    private static async Task<FileStream> AcquireLaunchLockAsync(string data, CancellationToken token)
    {
        var path = Path.Combine(data, "local-pipe-control.lock");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                LocalWorkerDiscovery.RejectLink(path);
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
                catch (IOException) when (!timeout.IsCancellationRequested)
                { await Task.Delay(100, timeout.Token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Another local Worker start/stop operation still owns this data directory. Wait for that operation or reconnect; no second launch was attempted.", error); }
    }
}
