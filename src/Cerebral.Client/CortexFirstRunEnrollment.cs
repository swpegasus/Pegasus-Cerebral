using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

[assembly: InternalsVisibleTo("Cerebral.Worker.Tests")]

namespace Cerebral.Client;

public sealed record CortexFirstRunEnrollmentPlan(string CortexExecutablePath, string CliExecutablePath,
    string HostExecutablePath, string WorkerExecutablePath, string ClientId, string CredentialFile, string OwnerSid, int SessionId,
    string ReleaseVersion, string McpComponentVersion, string WorkerSha256,
    string CodeManifest, string CodeManifestSha256);

public sealed record CortexFirstRunEnrollmentResult(string ClientId, string KeyId, string CredentialFile,
    DateTimeOffset ExpiresUtc);

public sealed record CortexEnrollmentAttemptStatus(string AttemptId, string State, int? ProcessId,
    long? ProcessStartUtcTicks, int? ExitCode, string? IssueCode);

/// <summary>A register request may have committed even when its reply is lost. Never retry it automatically.</summary>
public sealed class CortexEnrollmentUncertainException : InvalidOperationException
{
    public string? AttemptId { get; }
    public string State { get; }
    public CortexEnrollmentUncertainException() : this(null, "unknown") { }
    internal CortexEnrollmentUncertainException(string? attemptId, string state) :
        base("Cortex enrollment may have changed. Inspect its owner-local attempt and connection status; no automatic retry or rotation was attempted.")
    { AttemptId = attemptId; State = state; }
}

/// <summary>The public client list was not confirmed; no register request was submitted.</summary>
public sealed class CortexConnectionStatusUnconfirmedException : InvalidOperationException
{
    public int? ProcessId { get; }
    public long? ProcessStartUtcTicks { get; }
    public string? CliExecutablePath { get; }
    internal CortexConnectionStatusUnconfirmedException(int? pid, long? startTicks, string? image) :
        base("Cortex connection status did not finish within the UI wait. Enrollment was not started; inspect the owner-local status before trying again.")
    { ProcessId = pid; ProcessStartUtcTicks = startTicks; CliExecutablePath = image; }
}

/// <summary>Consent-gated, owner-local first registration. No Cortex semantic or database API is used.</summary>
public static class CortexFirstRunEnrollment
{
    private const string MinimumMcpComponent = "2.2026.10.07.15.19.A";
    private const int MaximumCatalogBytes = 65536;
    private const int MaximumCliOutputChars = 16384;
    private static readonly TimeSpan CliDeadline = TimeSpan.FromSeconds(15);
    private static readonly byte[] EmptyDefaults = "{\"readNamespaces\":[],\"writeNamespaces\":[],\"pao\":false}"u8.ToArray();
    private static readonly ConcurrentBag<Process> UnobservedRegistrations = new();
    private static readonly ConcurrentBag<IDisposable> UnobservedCodeLeases = new();

    /// <summary>Read-only preparation. The caller must show a separate, ordinary consent UI before RegisterAsync.</summary>
    public static CortexFirstRunEnrollmentPlan Prepare(string workerExecutable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cortex native enrollment requires Windows.");
        var prerequisite = CortexPrerequisite.InspectRegistered();
        var worker = RequireWorker(workerExecutable);
        using var workerInput = new FileStream(worker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var workerSha = Convert.ToHexStringLower(SHA256.HashData(workerInput));
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("A signed-in Cortex owner is required.");
        var session = process.SessionId;
        if (session <= 0 || sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
            throw new UnauthorizedAccessException("Cortex enrollment requires the signed-in non-service owner session.");

        var active = Path.Combine(prerequisite.DataDirectory, "install", "active.v1.json");
        InstalledCortexDiscovery.RejectLinks(active);
        using var input = new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is < 1 or > MaximumCatalogBytes) throw new InvalidDataException("The Cortex active catalog is unavailable or oversized.");
        var catalogBytes = new byte[checked((int)input.Length)];
        input.ReadExactly(catalogBytes);
        using var catalog = JsonDocument.Parse(catalogBytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = catalog.RootElement;
        RequireUnambiguousObject(root);
        if (RequiredText(root, "releaseVersion") != prerequisite.ReleaseVersion ||
            !root.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Cortex installed catalog changed during enrollment preparation.");
        var mcp = roles.EnumerateArray().Where(role =>
        {
            RequireUnambiguousObject(role);
            return RequiredText(role, "role") == "mcp";
        }).ToArray();
        if (mcp.Length != 1) throw new InvalidDataException("The installed Cortex semantic role is ambiguous.");
        var version = RequiredText(mcp[0], "componentVersion");
        if (RequiredText(mcp[0], "component") != "semantics" ||
            RequiredText(mcp[0], "relativePath") != "bin/semantics/PegasusCortex.exe" ||
            !SupportedComponent(version))
            throw new InvalidDataException("The installed Cortex semantic component does not advertise owner-local enrollment. Update Cortex first.");

        var selected = Path.Combine(prerequisite.InstallDirectory, "bin", "semantics", "PegasusCortex.exe");
        var app = Path.Combine(prerequisite.InstallDirectory, "bin", "semantics", "PegasusCortex.dll");
        var local = Path.Combine(prerequisite.InstallDirectory, "bin", "semantics", "PegasusCortex.Mcp.Local.dll");
        RequireInstalledFile(selected);
        RequireInstalledFile(app);
        RequireInstalledFile(local);
        // This is an additional static check on the selected installed binaries, not a source-version claim.
        if (!ContainsBinaryMarker(app, "--defaults-file") ||
            !ContainsBinaryMarker(local, "CortexConnectionCommands") ||
            !ContainsBinaryMarker(local, "connections_arguments_invalid") ||
            !ContainsBinaryMarker(local, "windows-current-user-dpapi"))
            throw new InvalidDataException("The selected installed Cortex CLI does not advertise the required connection contract.");

        var host = Path.Combine(prerequisite.InstallDirectory, "bin", "host", "PegasusCortex.Host.exe");
        RequireInstalledFile(host);
        using var code = OpenCodeClosure(prerequisite.InstallDirectory);
        var clientId = ClientIdForWorker(worker, sid);
        var credential = Path.Combine(prerequisite.DataDirectory, "users", sid, "config", "connections", clientId + ".key");
        InstalledCortexDiscovery.RejectLinks(credential);
        return new(prerequisite.ExecutablePath, selected, host, worker, clientId, credential, sid, session,
            prerequisite.ReleaseVersion, version, workerSha, code.Manifest, code.ManifestSha256);
    }

    /// <summary>Call only after the user accepts the enrollment and connection summary.</summary>
    public static Task<CortexFirstRunEnrollmentResult> RegisterAsync(CortexFirstRunEnrollmentPlan plan,
        Func<Action, CancellationToken, Task> admitAndLaunch, CancellationToken token = default) =>
        RegisterCoreAsync(plan, Prepare, RunListAsync, StartObservedCli,
            ProductionFiles.Instance, admitAndLaunch, CliDeadline, token);

    /// <summary>Read only. An unknown or partial attempt must be reviewed, never replayed.</summary>
    public static CortexEnrollmentAttemptStatus? InspectAttempt(string attemptId) =>
        ProductionFiles.Instance.Inspect(attemptId);

    /// <summary>Read only. A pending first-run attempt suppresses repeated enrollment offers.</summary>
    public static CortexEnrollmentAttemptStatus? FindUnresolvedAttempt() =>
        EnrollmentAttempts.FindUnresolved(null);

    /// <summary>Read only. Another Worker installation's attempt does not block this exact client.</summary>
    public static CortexEnrollmentAttemptStatus? FindUnresolvedAttempt(CortexFirstRunEnrollmentPlan plan) =>
        EnrollmentAttempts.FindUnresolved(plan.ClientId);

    // Test-only orchestration seam. The public entry point always re-runs the real machine gate.
    internal static async Task<CortexFirstRunEnrollmentResult> RegisterCoreAsync(CortexFirstRunEnrollmentPlan plan,
        Func<string, CortexFirstRunEnrollmentPlan> prepare,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<CortexCliReply>> list,
        Func<string, IReadOnlyList<string>, CortexRegistrationProcess> start,
        ICortexEnrollmentFiles files,
        Func<Action, CancellationToken, Task> admitAndLaunch,
        TimeSpan wait,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(admitAndLaunch);
        if (prepare(plan.WorkerExecutablePath) != plan)
            throw new InvalidOperationException("The Cortex installation, Worker, or signed-in owner changed. Open setup again.");
        token.ThrowIfCancellationRequested();
        files.RequireNoPending(plan.ClientId);
        var clients = await list(plan.CliExecutablePath, ["connections", "list"], token).ConfigureAwait(false);
        RequireNoExistingClient(clients, plan.ClientId, plan.WorkerExecutablePath);
        token.ThrowIfCancellationRequested();
        if (files.CredentialExists(plan.CredentialFile))
            throw new InvalidOperationException("A Cortex credential already exists without a usable registration. Repair it explicitly; enrollment was not retried.");
        var attempt = files.CreateAttempt(plan, Convert.ToHexStringLower(SHA256.HashData(EmptyDefaults)));
        var targetCurrent = false;
        try { targetCurrent = prepare(plan.WorkerExecutablePath) == plan && !files.CredentialExists(plan.CredentialFile); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
        if (!targetCurrent)
        {
            files.MarkTerminal(attempt.AttemptId, "not_started", null, "target_changed");
            files.DeleteDefaults(attempt.DefaultsPath);
            throw new InvalidOperationException("The Cortex enrollment target changed. Open setup again before registering.");
        }
        if (token.IsCancellationRequested)
        {
            files.MarkTerminal(attempt.AttemptId, "not_started", null, "canceled_before_launch");
            files.DeleteDefaults(attempt.DefaultsPath);
            token.ThrowIfCancellationRequested();
        }
        IDisposable leases;
        try { leases = files.AcquireCodeLeases(plan); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            files.MarkTerminal(attempt.AttemptId, "not_started", null, "code_changed");
            files.DeleteDefaults(attempt.DefaultsPath);
            throw new InvalidOperationException("The selected Cortex or Worker code changed before enrollment. Open setup again.", error);
        }
        // The caller's live connection/Worker gate invokes this one-use action in the same UI turn.
        // There is no await between that gate and Process.Start.
        var launchState = 0; // 0 = available, 1 = used, 2 = closed without launch
        var startAttempted = false;
        Task<CortexFirstRunEnrollmentResult>? observer = null;
        void Launch()
        {
            if (Interlocked.CompareExchange(ref launchState, 1, 0) != 0)
                throw new InvalidOperationException("The Cortex enrollment launch action is no longer available.");
            token.ThrowIfCancellationRequested();
            startAttempted = true;
            CortexRegistrationProcess process;
            try
            {
                process = start(plan.CliExecutablePath,
                    ["connections", "register", "--client", plan.ClientId, "--application", plan.WorkerExecutablePath,
                        "--transport", "native", "--defaults-file", attempt.DefaultsPath]);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                MarkUnknownBestEffort(files, attempt.AttemptId, "launch_unknown");
                UnobservedCodeLeases.Add(leases); // OS launch may have happened despite a lost handle.
                throw new CortexEnrollmentUncertainException(attempt.AttemptId, "unknown");
            }
            try { files.MarkLaunched(attempt.AttemptId, process.ProcessId, process.ProcessStartUtcTicks); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                observer = ObserveWithLeaseAsync(process, attempt, plan, files, leases);
                throw new CortexEnrollmentUncertainException(attempt.AttemptId, "unknown");
            }
            observer = ObserveWithLeaseAsync(process, attempt, plan, files, leases);
        }
        Task admission;
        try { admission = admitAndLaunch(Launch, token); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { admission = Task.FromException(error); }
        var elapsed = Task.Delay(wait, token);
        if (await Task.WhenAny(admission, elapsed).ConfigureAwait(false) != admission && !admission.IsCompleted)
        {
            var closed = Interlocked.CompareExchange(ref launchState, 2, 0) == 0;
            _ = ObserveLateAdmissionAsync(admission);
            if (closed)
            {
                leases.Dispose();
                files.MarkTerminal(attempt.AttemptId, "not_started", null, "admission_unconfirmed");
                files.DeleteDefaults(attempt.DefaultsPath);
                throw new InvalidOperationException("The live Cerebral owner was not confirmed before enrollment.");
            }
            // Launch may be running on the dispatcher while this wait completes.
            MarkUnknownBestEffort(files, attempt.AttemptId, "admission_ended");
            throw new CortexEnrollmentUncertainException(attempt.AttemptId, "unknown");
        }
        Interlocked.CompareExchange(ref launchState, 2, 0);
        try { await admission.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (!startAttempted)
            {
                leases.Dispose();
                files.MarkTerminal(attempt.AttemptId, "not_started", null, "owner_rejected");
                files.DeleteDefaults(attempt.DefaultsPath);
                throw;
            }
            if (error is not CortexEnrollmentUncertainException)
                MarkUnknownBestEffort(files, attempt.AttemptId, "admission_failed_after_launch");
            throw new CortexEnrollmentUncertainException(attempt.AttemptId, "unknown");
        }
        if (observer is null)
        {
            leases.Dispose();
            files.MarkTerminal(attempt.AttemptId, "not_started", null, "owner_did_not_launch");
            files.DeleteDefaults(attempt.DefaultsPath);
            throw new InvalidOperationException("The live Cerebral owner did not admit enrollment.");
        }
        if (await Task.WhenAny(observer, elapsed).ConfigureAwait(false) == observer || observer.IsCompleted)
            return await observer.ConfigureAwait(false);
        try { files.MarkUnknown(attempt.AttemptId, "await_ended"); }
        catch (Exception journalError) when (journalError is not OutOfMemoryException and not StackOverflowException) { }
        throw new CortexEnrollmentUncertainException(attempt.AttemptId, "unknown");
    }

    private static async Task ObserveLateAdmissionAsync(Task admission)
    {
        try { await admission.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }

    private static void MarkUnknownBestEffort(ICortexEnrollmentFiles files, string attemptId, string issueCode)
    {
        try { files.MarkUnknown(attemptId, issueCode); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }

    // A late Process.Start result may enrich an unknown attempt, but it must not
    // replace a previously observed child identity or make the attempt retryable.
    internal static CortexEnrollmentAttemptStatus MergeLateChildIdentity(
        CortexEnrollmentAttemptStatus prior, int? pid, long? startUtcTicks)
    {
        if (prior.State != "unknown" || pid is <= 0 || startUtcTicks is <= 0 ||
            (prior.ProcessId.HasValue && pid.HasValue && prior.ProcessId != pid) ||
            (prior.ProcessStartUtcTicks.HasValue && startUtcTicks.HasValue &&
             prior.ProcessStartUtcTicks != startUtcTicks))
            throw new InvalidDataException("A late Cortex enrollment child identity conflicts with the durable attempt.");
        return prior with
        {
            ProcessId = pid ?? prior.ProcessId,
            ProcessStartUtcTicks = startUtcTicks ?? prior.ProcessStartUtcTicks
        };
    }

    internal sealed record CortexCliReply(int ExitCode, string Output);
    internal sealed record CortexRegistrationProcess(int? ProcessId, long? ProcessStartUtcTicks,
        Task<CortexCliReply> Completion);
    internal sealed record CortexEnrollmentAttempt(string AttemptId, string DefaultsPath);
    internal interface ICortexEnrollmentFiles
    {
        bool CredentialExists(string path);
        void RequireNoPending(string clientId);
        CortexEnrollmentAttempt CreateAttempt(CortexFirstRunEnrollmentPlan plan, string defaultsSha256);
        IDisposable AcquireCodeLeases(CortexFirstRunEnrollmentPlan plan);
        void MarkLaunched(string attemptId, int? pid, long? startUtcTicks);
        void MarkUnknown(string attemptId, string issueCode);
        void MarkTerminal(string attemptId, string state, int? exitCode, string? issueCode);
        void DeleteDefaults(string path);
        bool CredentialValid(string path);
        CortexEnrollmentAttemptStatus? Inspect(string attemptId);
    }

    private sealed class ProductionFiles : ICortexEnrollmentFiles
    {
        internal static readonly ProductionFiles Instance = new();
        public bool CredentialExists(string path)
        {
            InstalledCortexDiscovery.RejectLinks(path);
            return File.Exists(path) || Directory.Exists(path);
        }
        public void RequireNoPending(string clientId) => EnrollmentAttempts.RequireNoPending(clientId);
        public CortexEnrollmentAttempt CreateAttempt(CortexFirstRunEnrollmentPlan plan, string defaultsSha256) =>
            EnrollmentAttempts.Create(plan, defaultsSha256);
        public IDisposable AcquireCodeLeases(CortexFirstRunEnrollmentPlan plan)
        {
            var installRoot = Path.GetDirectoryName(plan.CortexExecutablePath)
                ?? throw new InvalidDataException("The registered Cortex install root is unavailable.");
            var code = OpenCodeClosure(installRoot);
            FileStream? worker = null;
            try
            {
                if (code.Manifest != plan.CodeManifest || code.ManifestSha256 != plan.CodeManifestSha256)
                    throw new InvalidDataException("The selected Cortex code changed after review.");
                InstalledCortexDiscovery.RejectLinks(plan.WorkerExecutablePath);
                worker = new FileStream(plan.WorkerExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var sha = Convert.ToHexStringLower(SHA256.HashData(worker));
                InstalledCortexDiscovery.RejectLinks(plan.WorkerExecutablePath);
                if (sha != plan.WorkerSha256)
                    throw new InvalidDataException("The selected Cerebral Worker changed after review.");
                return new CodeAndWorkerLease(code, worker);
            }
            catch { worker?.Dispose(); code.Dispose(); throw; }
        }
        public void MarkLaunched(string attemptId, int? pid, long? startUtcTicks) =>
            EnrollmentAttempts.Update(attemptId, "running", pid, startUtcTicks, null, null);
        public void MarkUnknown(string attemptId, string issueCode) =>
            EnrollmentAttempts.Update(attemptId, "unknown", null, null, null, issueCode);
        public void MarkTerminal(string attemptId, string state, int? exitCode, string? issueCode) =>
            EnrollmentAttempts.Update(attemptId, state, null, null, exitCode, issueCode);
        public void DeleteDefaults(string path)
        {
            InstalledCortexDiscovery.RejectLinks(path);
            if (File.Exists(path)) File.Delete(path);
        }
        public bool CredentialValid(string path)
        {
            InstalledCortexDiscovery.RejectLinks(path);
            var info = new FileInfo(path);
            return info.Exists && info.Length is >= 1 and <= 4096;
        }
        public CortexEnrollmentAttemptStatus? Inspect(string attemptId) => EnrollmentAttempts.Inspect(attemptId);
    }

    private sealed class CodeAndWorkerLease(CodeClosure code, FileStream worker) : IDisposable
    {
        public void Dispose() { worker.Dispose(); code.Dispose(); }
    }

    private sealed record AttemptDocument(string AttemptId, string State, DateTimeOffset CreatedUtc,
        string ClientId,
        string CliExecutablePath, string WorkerExecutablePath, string WorkerSha256,
        string CodeManifest, string CodeManifestSha256,
        string OwnerSid, int SessionId, string DefaultsSha256,
        int? ProcessId, long? ProcessStartUtcTicks, int? ExitCode, string? IssueCode);

    private static class EnrollmentAttempts
    {
        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };
        private const int MaximumAttemptBytes = 32768;

        private static string Root(bool create)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Owner-local Cortex enrollment evidence requires Windows.");
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local) ||
                local.StartsWith(@"\\", StringComparison.Ordinal) || local.Any(char.IsControl) ||
                !string.Equals(local, Path.GetFullPath(local), StringComparison.OrdinalIgnoreCase) ||
                new DriveInfo(Path.GetPathRoot(local)!).DriveType != DriveType.Fixed)
                throw new InvalidDataException("The signed-in owner's local data directory is unavailable.");
            var parent = Path.Combine(local, "Pegasus", "Cerebral");
            var root = Path.Combine(parent, "CortexEnrollment");
            var attempts = Path.Combine(root, "attempts");
            InstalledCortexDiscovery.RejectLinks(attempts);
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("The current owner SID is unavailable.");
            RequireOwnerDirectory(local, sid);
            if (create)
            {
                Directory.CreateDirectory(parent);
                RequireOwnerDirectory(parent, sid);
                CreatePrivate(root);
                CreatePrivate(attempts);
            }
            if (Directory.Exists(attempts))
            {
                RequireOwnerPrivateDirectory(root, sid);
                RequireOwnerPrivateDirectory(attempts, sid);
            }
            return attempts;
        }

        private static void CreatePrivate(string path)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Owner-private Cortex enrollment evidence requires Windows.");
            InstalledCortexDiscovery.RejectLinks(path);
            if (Directory.Exists(path)) return;
            var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("The current owner SID is unavailable.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
            RequireOwnerPrivateDirectory(path, owner.Value);
        }

        internal static void RequireNoPending(string clientId)
        {
            if (FindUnresolved(clientId) is not null)
                throw new InvalidOperationException("A Cortex enrollment attempt remains unknown or partial. Inspect and repair it before another registration.");
        }

        internal static CortexEnrollmentAttemptStatus? FindUnresolved(string? clientId)
        {
            var root = Root(create: false);
            if (!Directory.Exists(root)) return null;
            var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).Take(514).ToArray();
            if (entries.Length > 513) throw new InvalidDataException("Too many Cortex enrollment artifacts require review.");
            var documents = new Dictionary<string, AttemptDocument>(StringComparer.Ordinal);
            var defaults = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in entries)
            {
                InstalledCortexDiscovery.RejectLinks(path);
                var name = Path.GetFileName(path);
                if (name == ".attempts.lock" && File.Exists(path)) continue;
                if (Regex.IsMatch(name, "^[0-9a-f]{32}\\.json$", RegexOptions.CultureInvariant))
                {
                    var document = Read(path);
                    if (!documents.TryAdd(document.AttemptId, document))
                        throw new InvalidDataException("Duplicate Cortex enrollment attempt identity.");
                }
                else if (Regex.IsMatch(name, "^[0-9a-f]{32}\\.defaults$", RegexOptions.CultureInvariant) && File.Exists(path))
                    defaults.Add(name[..32]);
                else throw new InvalidDataException("An unrecognized Cortex enrollment artifact requires review.");
            }
            if (documents.Count > 256) throw new InvalidDataException("Too many Cortex enrollment attempts require review.");
            foreach (var id in defaults)
                if (!documents.TryGetValue(id, out var document) || document.State is "confirmed" or "not_started")
                    throw new InvalidDataException("An orphaned Cortex enrollment defaults file requires review.");
            CortexEnrollmentAttemptStatus? pending = null;
            foreach (var document in documents.Values)
            {
                if (clientId is not null && document.ClientId != clientId) continue;
                if (document.State is "confirmed" or "not_started") continue;
                if (pending is not null) throw new InvalidDataException("Multiple Cortex enrollment attempts require review.");
                pending = new(document.AttemptId, document.State, document.ProcessId,
                    document.ProcessStartUtcTicks, document.ExitCode, document.IssueCode);
            }
            return pending;
        }

        internal static CortexEnrollmentAttempt Create(CortexFirstRunEnrollmentPlan plan, string defaultsSha256)
        {
            lock (Gate)
            {
                var root = Root(create: true);
                var lockPath = Path.Combine(root, ".attempts.lock");
                InstalledCortexDiscovery.RejectLinks(lockPath);
                using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                RequireNoPending(plan.ClientId);
                var id = Guid.NewGuid().ToString("N");
                var intent = new AttemptDocument(id, "prepared", DateTimeOffset.UtcNow, plan.ClientId,
                    plan.CliExecutablePath, plan.WorkerExecutablePath, plan.WorkerSha256,
                    plan.CodeManifest, plan.CodeManifestSha256,
                    plan.OwnerSid, plan.SessionId, defaultsSha256, null, null, null, null);
                var path = Path.Combine(root, id + ".json");
                Write(path, intent, replace: false); // Durable intent precedes any CLI launch.
                var defaults = Path.Combine(root, id + ".defaults");
                using (var file = new FileStream(defaults, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(EmptyDefaults); file.Flush(true); }
                return new(id, defaults);
            }
        }

        internal static void Update(string attemptId, string state, int? pid, long? startTicks, int? exitCode, string? issueCode)
        {
            lock (Gate)
            {
                var path = AttemptPath(attemptId, create: false);
                var old = Read(path);
                if (old.State is ("confirmed" or "partial" or "not_started") &&
                    state is ("running" or "unknown")) return;
                if (old.State == "unknown" && state == "running")
                {
                    // UI admission can expire while Process.Start is returning. Keep the
                    // no-replay state, but retain a subsequently observed exact child ID.
                    var enriched = MergeLateChildIdentity(new(old.AttemptId, old.State,
                        old.ProcessId, old.ProcessStartUtcTicks, old.ExitCode, old.IssueCode), pid, startTicks);
                    Write(path, old with
                    {
                        ProcessId = enriched.ProcessId,
                        ProcessStartUtcTicks = enriched.ProcessStartUtcTicks
                    }, replace: true);
                    return;
                }
                Write(path, old with
                {
                    State = state, ProcessId = pid ?? old.ProcessId,
                    ProcessStartUtcTicks = startTicks ?? old.ProcessStartUtcTicks,
                    ExitCode = exitCode ?? old.ExitCode, IssueCode = issueCode
                }, replace: true);
            }
        }

        internal static CortexEnrollmentAttemptStatus? Inspect(string attemptId)
        {
            var path = AttemptPath(attemptId, create: false);
            if (!File.Exists(path)) return null;
            var document = Read(path);
            return new(document.AttemptId, document.State, document.ProcessId,
                document.ProcessStartUtcTicks, document.ExitCode, document.IssueCode);
        }

        private static string AttemptPath(string attemptId, bool create)
        {
            if (!Regex.IsMatch(attemptId ?? "", "^[0-9a-f]{32}$", RegexOptions.CultureInvariant))
                throw new ArgumentException("The Cortex enrollment attempt ID is invalid.");
            return Path.Combine(Root(create), attemptId + ".json");
        }

        private static AttemptDocument Read(string path)
        {
            InstalledCortexDiscovery.RejectLinks(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is < 1 or > MaximumAttemptBytes) throw new InvalidDataException("The Cortex enrollment attempt is invalid or oversized.");
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            RejectDuplicateProperties(parsed.RootElement);
            var document = parsed.RootElement.Deserialize<AttemptDocument>(Json)
                ?? throw new InvalidDataException("The Cortex enrollment attempt is invalid.");
            if (Path.GetFileNameWithoutExtension(path) != document.AttemptId ||
                string.IsNullOrWhiteSpace(document.CliExecutablePath) ||
                string.IsNullOrWhiteSpace(document.WorkerExecutablePath) ||
                !Regex.IsMatch(document.ClientId ?? "", "^cerebral-[0-9a-f]{32}$", RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(document.OwnerSid) || document.SessionId < 1 ||
                !Regex.IsMatch(document.WorkerSha256 ?? "", "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(document.CodeManifest) ||
                !Regex.IsMatch(document.CodeManifestSha256 ?? "", "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
                document.CodeManifestSha256 != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document.CodeManifest))) ||
                !Regex.IsMatch(document.DefaultsSha256 ?? "", "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
                document.State is not ("prepared" or "running" or "unknown" or "confirmed" or "partial" or "not_started"))
                throw new InvalidDataException("The Cortex enrollment attempt identity or state is invalid.");
            return document;
        }

        private static void Write(string path, AttemptDocument document, bool replace)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
            if (bytes.Length > MaximumAttemptBytes) throw new InvalidDataException("The Cortex enrollment attempt exceeds its bound.");
            var target = replace ? path + "." + Guid.NewGuid().ToString("N") + ".tmp" : path;
            try
            {
                using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(bytes); file.Flush(true); }
                if (replace)
                {
                    InstalledCortexDiscovery.RejectLinks(path);
                    File.Move(target, path, overwrite: true);
                }
            }
            finally { if (replace && File.Exists(target)) File.Delete(target); }
        }
    }

    private static async Task<CortexFirstRunEnrollmentResult> ObserveWithLeaseAsync(CortexRegistrationProcess process,
        CortexEnrollmentAttempt attempt, CortexFirstRunEnrollmentPlan plan, ICortexEnrollmentFiles files, IDisposable leases)
    {
        try { return await ObserveAsync(process, attempt, plan, files).ConfigureAwait(false); }
        finally
        {
            // A successful capture proves the child exited. On capture failure its terminal
            // state may be unprovable, so preserve leases until this app exits.
            if (process.Completion.IsCompletedSuccessfully) leases.Dispose();
            else UnobservedCodeLeases.Add(leases);
        }
    }

    private static async Task<CortexFirstRunEnrollmentResult> ObserveAsync(CortexRegistrationProcess process,
        CortexEnrollmentAttempt attempt, CortexFirstRunEnrollmentPlan plan, ICortexEnrollmentFiles files)
    {
        CortexCliReply reply;
        try { reply = await process.Completion.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            try { files.MarkUnknown(attempt.AttemptId, "capture_failed"); }
            catch (Exception journalError) when (journalError is not OutOfMemoryException and not StackOverflowException) { }
            throw new CortexEnrollmentUncertainException(attempt.AttemptId, "unknown");
        }
        CortexFirstRunEnrollmentResult? result = null;
        try { result = ParseRegistration(reply, plan.ClientId, plan.CredentialFile, files.CredentialValid); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
        var state = result is null ? "partial" : "confirmed";
        try { files.MarkTerminal(attempt.AttemptId, state, reply.ExitCode, result is null ? "result_unconfirmed" : null); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { throw new CortexEnrollmentUncertainException(attempt.AttemptId, "unknown"); }
        try { files.DeleteDefaults(attempt.DefaultsPath); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            try { files.MarkTerminal(attempt.AttemptId, "partial", reply.ExitCode, "defaults_cleanup_failed"); }
            catch (Exception journalError) when (journalError is not OutOfMemoryException and not StackOverflowException) { }
            throw new CortexEnrollmentUncertainException(attempt.AttemptId, "partial");
        }
        if (result is null) throw new CortexEnrollmentUncertainException(attempt.AttemptId, "partial");
        return result;
    }

    internal static void RequireNoExistingClient(CortexCliReply reply, string expectedClientId, string workerExecutable)
    {
        if (reply.ExitCode != 0) throw new InvalidOperationException("Cortex connection status is unavailable; enrollment was not started.");
        using var doc = ParsePublicJson(reply.Output);
        var root = doc.RootElement;
        RequireObject(root, "ok", "clients");
        if (root.GetProperty("ok").ValueKind != JsonValueKind.True ||
            root.GetProperty("clients").ValueKind != JsonValueKind.Array ||
            root.GetProperty("clients").GetArrayLength() > 32)
            throw new InvalidDataException("Cortex returned an invalid public client list.");
        foreach (var client in root.GetProperty("clients").EnumerateArray())
        {
            if (client.ValueKind != JsonValueKind.Object || !client.TryGetProperty("clientId", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !client.TryGetProperty("application", out var application) || application.ValueKind != JsonValueKind.String ||
                !client.TryGetProperty("transport", out var transport) || transport.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Cortex returned an invalid public client entry.");
            if (id.GetString() == expectedClientId ||
                string.Equals(application.GetString(), workerExecutable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cortex already has a client for this Worker. Keep its binding or repair it explicitly; no key was issued.");
        }
    }

    internal static CortexFirstRunEnrollmentResult ParseRegistration(CortexCliReply reply, string expectedClientId,
        string expectedCredentialFile,
        Func<string, bool> credentialValid)
    {
        if (reply.ExitCode != 0) throw new InvalidOperationException("Cortex enrollment did not return a confirmed success.");
        using var doc = ParsePublicJson(reply.Output);
        var root = doc.RootElement;
        RequireObject(root, "ok", "clientId", "keyId", "transport", "expiresUtc", "credentialFile", "protection");
        var keyId = root.GetProperty("keyId").GetString();
        var file = root.GetProperty("credentialFile").GetString();
        if (root.GetProperty("ok").ValueKind != JsonValueKind.True ||
            root.GetProperty("clientId").GetString() != expectedClientId ||
            root.GetProperty("transport").GetString() != "native" ||
            root.GetProperty("protection").GetString() != "windows-current-user-dpapi" ||
            keyId is null || !Regex.IsMatch(keyId, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant) ||
            !string.Equals(file, expectedCredentialFile, StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("expiresUtc").ValueKind != JsonValueKind.String ||
            !root.GetProperty("expiresUtc").TryGetDateTimeOffset(out var expiry) ||
            expiry <= DateTimeOffset.UtcNow || expiry > DateTimeOffset.UtcNow.AddDays(91))
            throw new InvalidDataException("Cortex enrollment returned an invalid public result.");
        if (!credentialValid(expectedCredentialFile))
            throw new InvalidDataException("Cortex did not leave a bounded protected credential file.");
        return new(expectedClientId, keyId, expectedCredentialFile, expiry);
    }

    private static JsonDocument ParsePublicJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output) || output.Length > MaximumCliOutputChars)
            throw new InvalidDataException("Cortex returned no bounded public connection result.");
        var doc = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 12 });
        try { RejectDuplicateProperties(doc.RootElement); return doc; }
        catch { doc.Dispose(); throw; }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Cortex returned ambiguous public connection data.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void RequireObject(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal)) == false)
            throw new InvalidDataException("Cortex returned an unexpected public contract shape.");
    }

    private static void RequireUnambiguousObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
            value.EnumerateObject().Count())
            throw new InvalidDataException("The Cortex installed catalog is ambiguous.");
    }

    private static string RequiredText(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: > 0 } text)
            throw new InvalidDataException("The Cortex installed catalog has an invalid " + name + ".");
        return text;
    }

    private static string RequireWorker(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).Equals("Cerebral.Worker.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the exact canonical Cerebral Worker executable.");
        InstalledCortexDiscovery.RejectLinks(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The selected Cerebral Worker executable is missing.");
        return path;
    }

    internal static string ClientIdForWorker(string canonicalWorkerExecutable, string ownerSid)
    {
        var binding = canonicalWorkerExecutable.ToUpperInvariant() + "\n" + ownerSid;
        return "cerebral-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding)))[..32];
    }

    private static void RequireInstalledFile(string path)
    {
        InstalledCortexDiscovery.RejectLinks(path);
        if (!File.Exists(path)) throw new InvalidDataException("The selected installed Cortex component is missing.");
    }

    private static bool ContainsBinaryMarker(string path, string marker)
    {
        var info = new FileInfo(path);
        if (info.Length is < 1 or > 4_194_304) return false;
        var bytes = File.ReadAllBytes(path);
        var utf8 = Encoding.UTF8.GetString(bytes);
        if (utf8.Contains(marker, StringComparison.Ordinal)) return true;
        return Encoding.Unicode.GetString(bytes).Contains(marker, StringComparison.Ordinal) ||
            Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1).Contains(marker, StringComparison.Ordinal);
    }

    // The selected role's deps file names the product-managed managed runtime assets.
    // Pin the two role directories separately; equal DLL names are distinct physical files.
    private sealed class CodeClosure(string manifest, string manifestSha256, List<FileStream> streams) : IDisposable
    {
        internal string Manifest { get; } = manifest;
        internal string ManifestSha256 { get; } = manifestSha256;
        public void Dispose() { foreach (var stream in streams) stream.Dispose(); }
    }

    private static CodeClosure OpenCodeClosure(string installRoot)
    {
        var opened = new SortedDictionary<string, FileStream>(StringComparer.Ordinal);
        var projections = new SortedSet<string>(StringComparer.Ordinal);
        void Add(string relative)
        {
            if (opened.ContainsKey(relative)) return;
            if (opened.Count >= 64 || relative.Length > 240 || relative.Any(char.IsControl) ||
                relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') ||
                relative.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("The Cortex selected code closure is invalid or too large.");
            var path = Path.Combine(installRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            RequireInstalledFile(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            try
            {
                InstalledCortexDiscovery.RejectLinks(path);
                if (stream.Length is < 1 or > 16_777_216)
                    throw new InvalidDataException("A selected Cortex code file is missing or oversized.");
                opened.Add(relative, stream);
            }
            catch { stream.Dispose(); throw; }
        }
        try
        {
            foreach (var role in new[] { "semantics", "host" })
            {
                var entry = role == "semantics" ? "PegasusCortex" : "PegasusCortex.Host";
                Add($"bin/{role}/{entry}.exe");
                Add($"bin/{role}/{entry}.dll"); // The managed entry must be pinned even if deps omits it.
                Add($"bin/{role}/{entry}.deps.json");
                Add($"bin/{role}/{entry}.runtimeconfig.json");
            }
            foreach (var role in new[] { "semantics", "host" })
            {
                var entry = role == "semantics" ? "PegasusCortex" : "PegasusCortex.Host";
                var deps = opened[$"bin/{role}/{entry}.deps.json"];
                deps.Position = 0;
                using var document = JsonDocument.Parse(deps, new JsonDocumentOptions { MaxDepth = 32 });
                RejectDuplicateProperties(document.RootElement);
                var root = document.RootElement;
                if (!root.TryGetProperty("runtimeTarget", out var runtimeTarget) || runtimeTarget.ValueKind != JsonValueKind.Object ||
                    !runtimeTarget.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object ||
                    !targets.TryGetProperty(name.GetString()!, out var target) || target.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The selected Cortex deps contract is incomplete.");
                var selectedAssets = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var library in target.EnumerateObject())
                {
                    if (library.Value.ValueKind != JsonValueKind.Object ||
                        !library.Value.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var asset in runtime.EnumerateObject())
                    {
                        var path = asset.Name;
                        if (path.Length > 240 || path.Contains('\\') || path.Contains(':') ||
                            path.StartsWith('/') || path.Split('/').Any(part => part is "" or "." or ".."))
                            throw new InvalidDataException("The selected Cortex runtime asset path is invalid.");
                        var filename = path.Split('/')[^1];
                        if (!filename.StartsWith("Pegasus", StringComparison.Ordinal) ||
                            !Regex.IsMatch(filename, @"^[A-Za-z0-9._-]+\.(dll|exe)$", RegexOptions.CultureInvariant))
                            continue;
                        if (!RecordProductAsset(selectedAssets, filename, path)) continue;
                        var physical = $"bin/{role}/{filename}";
                        projections.Add($"asset\tbin/{role}/{path}\t{physical}\n");
                        Add(physical);
                    }
                }
                RequireRoleClosure(role, selectedAssets, relative => opened.ContainsKey(relative));
            }
            var manifest = new StringBuilder();
            foreach (var projection in projections) manifest.Append(projection);
            foreach (var (relative, stream) in opened)
            {
                stream.Position = 0;
                var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
                manifest.Append(relative).Append('\t').Append(stream.Length.ToString(CultureInfo.InvariantCulture))
                    .Append('\t').Append(sha).Append('\n');
            }
            var content = manifest.ToString();
            return new(content, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                opened.Values.ToList());
        }
        catch
        {
            foreach (var stream in opened.Values) stream.Dispose();
            throw;
        }
    }

    // Pure closure checks also make malformed future deps layouts testable without launching Cortex.
    internal static bool RecordProductAsset(Dictionary<string, string> selectedAssets, string filename, string declaredPath)
    {
        // The installed kit projects this one NuGet asset into each role root.
        if (declaredPath != filename && declaredPath != "lib/net8.0/Pegasus.Shared.dll")
            throw new InvalidDataException("The selected Cortex product runtime asset layout is unsupported.");
        if (selectedAssets.TryGetValue(filename, out var prior))
        {
            if (prior != declaredPath)
                throw new InvalidDataException("The selected Cortex product runtime asset layout is ambiguous.");
            return false;
        }
        selectedAssets.Add(filename, declaredPath);
        return true;
    }

    internal static void RequireRoleClosure(string role, IReadOnlyDictionary<string, string> selectedAssets,
        Func<string, bool> physicalPresent)
    {
        if (role is not ("semantics" or "host") || selectedAssets.Count is < 8 or > 32)
            throw new InvalidDataException("The selected Cortex product-managed runtime closure is incomplete or oversized.");
        var entry = role == "semantics" ? "PegasusCortex" : "PegasusCortex.Host";
        if (!physicalPresent($"bin/{role}/{entry}.exe") ||
            !physicalPresent($"bin/{role}/{entry}.dll") ||
            !physicalPresent($"bin/{role}/{entry}.deps.json") ||
            !physicalPresent($"bin/{role}/{entry}.runtimeconfig.json") ||
            (role == "semantics" &&
                (!selectedAssets.ContainsKey("PegasusCortex.Mcp.Local.dll") ||
                 !physicalPresent("bin/semantics/PegasusCortex.Mcp.Local.dll"))))
            throw new InvalidDataException("The selected Cortex role entry or semantic MCP payload is missing.");
    }

    private static bool SupportedComponent(string actual)
    {
        static int[]? Parts(string value)
        {
            var match = Regex.Match(value, @"^([0-9]+)\.([0-9]{4})\.([0-9]{2})\.([0-9]{2})\.([0-9]{2})\.([0-9]{2})\.([A-D])$", RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            var parts = new int[7];
            for (var i = 1; i <= 6; i++)
                if (!int.TryParse(match.Groups[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i - 1])) return null;
            parts[6] = match.Groups[7].Value[0];
            return parts;
        }
        var left = Parts(actual); var right = Parts(MinimumMcpComponent);
        if (left is null || right is null) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] > right[i]) return true;
            if (left[i] < right[i]) return false;
        }
        return true;
    }

    private static void RequireOwnerPrivateDirectory(string path, string sid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Owner-private Cortex enrollment evidence requires Windows.");
        InstalledCortexDiscovery.RejectLinks(path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) throw new InvalidDataException("The enrollment evidence directory is unavailable.");
        var owner = new SecurityIdentifier(sid);
        var security = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The enrollment evidence directory is not owner-private.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType != AccessControlType.Allow || !owner.Equals(rule.IdentityReference))
                throw new UnauthorizedAccessException("The enrollment evidence directory has an unexpected access rule.");
    }

    private static void RequireOwnerDirectory(string path, string sid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Owner-local Cortex enrollment evidence requires Windows.");
        InstalledCortexDiscovery.RejectLinks(path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists ||
            !new SecurityIdentifier(sid).Equals(directory.GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("The local enrollment parent is not owned by the signed-in user.");
    }

    private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    // A timed-out status process keeps draining to exit. No register request follows an unconfirmed list.
    private static Task<CortexCliReply> RunListAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
    {
        var process = StartObservedCli(executable, args);
        return AwaitPublicListAsync(process.Completion, CliDeadline, token,
            process.ProcessId, process.ProcessStartUtcTicks, executable);
    }

    // Pure wait seam: timeout/cancellation releases the UI, never the launched collector.
    internal static async Task<CortexCliReply> AwaitPublicListAsync(Task<CortexCliReply> completion,
        TimeSpan wait, CancellationToken token, int? pid = null, long? startTicks = null, string? image = null)
    {
        var elapsed = Task.Delay(wait, token);
        if (await Task.WhenAny(completion, elapsed).ConfigureAwait(false) == completion || completion.IsCompleted)
            return await completion.ConfigureAwait(false);
        _ = ObserveLateListAsync(completion);
        throw new CortexConnectionStatusUnconfirmedException(pid, startTicks, image);
    }

    private static async Task ObserveLateListAsync(Task<CortexCliReply> completion)
    {
        try { _ = await completion.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }

    // The effectful CLI is never tied to the UI wait token or a kill-on-close job.
    private static CortexRegistrationProcess StartObservedCli(string executable, IReadOnlyList<string> args)
    {
        var process = new Process { StartInfo = StartInfo(executable, args) };
        try
        {
            if (!process.Start()) throw new IOException("The selected Cortex CLI did not start.");
        }
        catch
        {
            // A rare launch exception after OS creation is still an effectful, unknown attempt.
            try
            {
                if (process.Id > 0)
                    return new(process.Id, null, CaptureCliAsync(process));
            }
            catch (InvalidOperationException)
            {
                // Process.Start never associated an OS process with this object.
                process.Dispose();
                throw;
            }
            catch (Exception identityError) when (identityError is not OutOfMemoryException and not StackOverflowException)
            {
                _ = CaptureCliAsync(process);
                throw;
            }
            _ = CaptureCliAsync(process);
            throw;
        }
        int? pid = null; long? startTicks = null;
        try { pid = process.Id; startTicks = process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
        return new(pid, startTicks, CaptureCliAsync(process));
    }

    private static async Task<CortexCliReply> CaptureCliAsync(Process process)
    {
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, CancellationToken.None);
            var stderr = ReadBoundedAsync(process.StandardError, CancellationToken.None, capture: false);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
            return new(process.ExitCode, stdout.Result);
        }
        finally
        {
            // A failed capture leaves a durable unknown attempt. Never kill or dispose a running CLI.
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                process.Dispose();
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                var exited = false;
                try { exited = process.HasExited; }
                catch (Exception statusError) when (statusError is not OutOfMemoryException and not StackOverflowException) { }
                if (exited) process.Dispose();
                else UnobservedRegistrations.Add(process); // Retain the handle until the app exits; intent stays unknown.
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token, bool capture = true)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        var overflow = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (!capture) continue;
            if (result.Length + count > MaximumCliOutputChars) overflow = true;
            if (!overflow) result.Append(buffer, 0, count);
        }
        if (overflow) throw new InvalidDataException("Cortex CLI output exceeded its public response bound.");
        return result.ToString();
    }
}
