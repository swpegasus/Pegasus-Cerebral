using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Worker;

internal sealed record OfflineRecoveryFilePin(string RelativePath, long Bytes, string Sha256);
internal sealed record OfflineRecoveryHoldPin(string HoldId, string RequestId, long Generation,
    long Revision, long RequestedEpoch);
internal sealed record OfflineRecoveryStoppedWorker(int ProcessId, long StartUtcTicks,
    string Executable, string OwnerSid, int SessionId, string PipeName, string PipeInstanceId);
internal sealed record OfflineRecoveryMaintenanceCode(string ExecutablePath, string ExecutableSha256,
    string WorkerDllSha256, string OwnerSid, int SessionId);
internal sealed record ServicePackageOfflineRecoveryRequest(string Schema, string RequestId,
    string DataRoot, string RepositoryRoot, string InstallationId, string PackageId,
    long ExpectedCurrentRevision, string ExpectedCurrentLifecycleSha256,
    string ExpectedCurrentVersion, string ExpectedCurrentGeneration, string RestoreVersion,
    string PreservedPackageRoot, string PreservedPackageFingerprint,
    OfflineRecoveryFilePin[] PreservedFiles, OfflineRecoveryHoldPin Hold,
    OfflineRecoveryStoppedWorker StoppedWorker, OfflineRecoveryMaintenanceCode MaintenanceCode);
internal sealed record ServicePackageOfflineRecoveryResult(string Schema, string RequestId,
    string PackageId, long Revision, string Version, string Generation, string PackageFingerprint,
    string IntentPath, string PreparedResultPath, string State);

/// <summary>
/// Stopped-owner package repair. The owner lock and read-only persisted-work proof precede
/// the lifecycle write. An existing intent selects inspection only, never an automatic replay.
/// </summary>
internal static class ServicePackageOfflineRecovery
{
    private static readonly JsonSerializerOptions RequestJson = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Offline package recovery requires Windows.");
        if (args.Length != 7 || args[0] != "--local-run" || args[1] != "--data" ||
            args[3] != "--service-plugins" || args[5] != "--service-package-recover")
            throw new ArgumentException("Use exactly --local-run --data <existing-root> --service-plugins <existing-root> --service-package-recover <request.json>.");
        LocalWorkerPipeServer.ValidateHostingIdentity();
        var dataRoot = ExistingDirectory(args[2]);
        var repositoryRoot = ExistingDirectory(args[4]);
        var requestPath = ExistingFile(args[6]);
        var requestBytes = File.ReadAllBytes(requestPath);
        if (requestBytes.Length is < 2 or > 64 * 1024)
            throw new InvalidDataException("The offline recovery request exceeds its bound.");
        using (var document = JsonDocument.Parse(requestBytes, new JsonDocumentOptions { MaxDepth = 32 }))
            RejectDuplicateProperties(document.RootElement);
        var request = JsonSerializer.Deserialize<ServicePackageOfflineRecoveryRequest>(requestBytes, RequestJson)
            ?? throw new InvalidDataException("The offline recovery request is empty.");
        ValidateRequest(request, dataRoot, repositoryRoot);
        var requestDigest = Convert.ToHexStringLower(SHA256.HashData(requestBytes));
        // The normal Worker uses this exact exclusive lock for the same data root. Open only
        // an existing lock so an uninitialized or competing owner is never treated as stopped.
        var lockPath = ExistingFile(Path.Combine(dataRoot, "service.lock"));
        using var owner = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        RequireStoppedPeer(request.StoppedWorker);
        var result = ServicePackageLifecycle.RecoverOffline(request, requestDigest);
        Console.WriteLine(JsonSerializer.Serialize(result, CerebralJson.Options));
        return 0;
    }

    private static void ValidateRequest(ServicePackageOfflineRecoveryRequest request,
        string dataRoot, string repositoryRoot)
    {
        if (request.Schema != "cerebral.service-package-offline-recovery/1" ||
            !Guid.TryParseExact(request.RequestId, "N", out _) ||
            request.ExpectedCurrentRevision < 1 ||
            !PluginVersion.TryParse(request.ExpectedCurrentVersion, out _) ||
            !PluginVersion.TryParse(request.RestoreVersion, out _) ||
            request.ExpectedCurrentGeneration.Length is < 1 or > 160 ||
            request.InstallationId.Length is < 1 or > 128 || request.InstallationId.Any(char.IsControl))
            throw new InvalidDataException("The offline recovery request has invalid identity or version fields.");
        ServicePluginRuntime.ValidatePackageId(request.PackageId);
        RequireHash(request.ExpectedCurrentLifecycleSha256);
        RequireHash(request.PreservedPackageFingerprint);
        if (!SamePath(ExistingDirectory(request.DataRoot), dataRoot) ||
            !SamePath(ExistingDirectory(request.RepositoryRoot), repositoryRoot) ||
            SamePath(dataRoot, repositoryRoot) || Under(dataRoot, repositoryRoot) ||
            Under(repositoryRoot, dataRoot) ||
            SamePath(request.PreservedPackageRoot, repositoryRoot) ||
            SamePath(request.PreservedPackageRoot, dataRoot) ||
            Under(request.PreservedPackageRoot, repositoryRoot) ||
            Under(request.PreservedPackageRoot, dataRoot))
            throw new InvalidDataException("The offline recovery roots do not match the selected owner.");
        ExistingDirectory(request.PreservedPackageRoot);
        var pins = request.PreservedFiles;
        if (pins is not { Length: >= 1 and <= 512 } ||
            pins.Any(pin => pin is null || !ValidRelativeFilePath(pin.RelativePath) ||
                pin.Bytes is < 1 or > 268_435_456 || !IsHash(pin.Sha256)) ||
            pins.Select(pin => pin.RelativePath).Distinct(StringComparer.Ordinal).Count() != pins.Length ||
            pins.Sum(pin => pin.Bytes) > 268_435_456)
            throw new InvalidDataException("The preserved package file pins are invalid or exceed the package bound.");
        if (!pins.Any(pin => pin.RelativePath == ServicePluginRuntime.ManifestFileName))
            throw new InvalidDataException("The preserved package manifest pin is missing.");
        var hold = request.Hold;
        if (hold is null || !Guid.TryParseExact(hold.HoldId, "N", out _) ||
            !Guid.TryParseExact(hold.RequestId, "N", out _) ||
            hold.Generation < 1 || hold.Revision < 1 || hold.RequestedEpoch < 0)
            throw new InvalidDataException("The recovery hold coordinates are incomplete.");
        var stopped = request.StoppedWorker;
        if (stopped is null || stopped.ProcessId <= 0 || stopped.StartUtcTicks <= 0 ||
            stopped.SessionId < 0 || string.IsNullOrWhiteSpace(stopped.OwnerSid) ||
            stopped.OwnerSid.Length > 184 || !Guid.TryParseExact(stopped.PipeInstanceId, "N", out _))
            throw new InvalidDataException("The stopped Worker witness is incomplete.");
        _ = LocalPath(stopped.Executable); // Historical path need not still exist after a failed publication.
        new LocalWorkerEndpoint(stopped.PipeName, stopped.PipeInstanceId).Validate();
        var maintenance = request.MaintenanceCode;
        if (maintenance is null || maintenance.SessionId != stopped.SessionId ||
            maintenance.OwnerSid != stopped.OwnerSid ||
            !SamePath(ExistingFile(maintenance.ExecutablePath),
                Environment.ProcessPath ?? throw new InvalidDataException("The maintenance process path is unavailable.")))
            throw new UnauthorizedAccessException("The maintenance process differs from the reviewed signed-in owner.");
        RequireHash(maintenance.ExecutableSha256);
        RequireHash(maintenance.WorkerDllSha256);
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (identity.User?.Value != maintenance.OwnerSid || process.SessionId != maintenance.SessionId ||
            FileHash(maintenance.ExecutablePath) != maintenance.ExecutableSha256 ||
            FileHash(typeof(ServicePackageOfflineRecovery).Assembly.Location) != maintenance.WorkerDllSha256)
            throw new UnauthorizedAccessException("The current maintenance code or owner identity changed.");
    }

    private static void RequireStoppedPeer(OfflineRecoveryStoppedWorker stopped)
    {
        if (stopped.ProcessId == Environment.ProcessId)
            throw new UnauthorizedAccessException("Maintenance cannot identify itself as the stopped Worker.");
        try
        {
            using var process = Process.GetProcessById(stopped.ProcessId);
            process.Refresh();
            if (!process.HasExited)
                throw new UnauthorizedAccessException("The expected stopped Worker PID is still occupied.");
        }
        catch (ArgumentException) { /* The reviewed process no longer exists. */ }
        if (WaitNamedPipe(@"\\.\pipe\" + stopped.PipeName, 0) ||
            Marshal.GetLastWin32Error() != 2)
            throw new UnauthorizedAccessException("The reviewed Worker pipe name may still be owned.");
    }

    internal static string ExistingDirectory(string path)
    {
        var full = LocalPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("A required recovery directory is missing.");
        RejectLinks(full);
        return full;
    }

    internal static string ExistingFile(string path)
    {
        var full = LocalPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("A required recovery file is missing.");
        RejectLinks(full);
        return full;
    }

    internal static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Offline recovery requires an absolute local path.");
        return Path.GetFullPath(path);
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Offline recovery paths cannot traverse links or junctions.");
    }

    internal static bool SamePath(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool Under(string child, string parent)
    {
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidRelativeFilePath(string? path)
    {
        if (path is not { Length: >= 1 and <= 1024 } || path.StartsWith('/') ||
            path.Contains('\\') || path.Contains(':')) return false;
        var parts = path.Split('/');
        return parts.Length is >= 1 and <= 16 && parts.All(part =>
            part is { Length: >= 1 and <= 255 } and not ("." or "..") &&
            !part.EndsWith('.') && !part.EndsWith(' ') &&
            part.All(character => !char.IsControl(character) &&
                character is not ('<' or '>' or '"' or '|' or '?' or '*')));
    }

    internal static string FileHash(string path)
    {
        ExistingFile(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    internal static void RequireHash(string value)
    {
        if (!IsHash(value)) throw new InvalidDataException("A recovery SHA-256 pin is invalid.");
    }

    internal static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The recovery request contains duplicate fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "WaitNamedPipeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string pipeName, uint timeoutMilliseconds);
}
