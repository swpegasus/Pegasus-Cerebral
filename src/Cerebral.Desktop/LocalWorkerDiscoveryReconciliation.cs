using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>One reviewed replacement of a dead local discovery generation. The caller supplies
/// a fresh exact old-file identity and an independently qualified current Worker identity.</summary>
public sealed record LocalWorkerDiscoveryReconcileRequest(int Version, string DataDirectory,
    string ExpectedOldSha256, long ExpectedOldBytes, string ExpectedOldFileId,
    LocalWorkerGeneration OldGeneration, LocalWorkerGeneration CurrentGeneration,
    string Executable, string AccountSid, int SessionId, IReadOnlyList<string> NativeArgv,
    string SelectedEnginesDirectory, string InstallationId);

public sealed record LocalWorkerDiscoveryReconcilePreimage(int Version, string DataDirectory,
    string Sha256, long Bytes, string FileId, LocalWorkerGeneration Generation);

/// <summary>Unknown means publication may have started and must be inspected; it is never retry credit.</summary>
public sealed record LocalWorkerDiscoveryReconcileResult(int Version, string State, string Code,
    string? OldSha256, string? NewSha256, string? PreservedOldPath,
    string? FailureType = null, string? FailureMessage = null);

internal sealed record LocalWorkerReconcileObservation(int ProcessId, long StartUtcTicks,
    string Executable, string AccountSid, int SessionId, IReadOnlyList<string> NativeArgv,
    string SelectedEnginesDirectory, string InstallationId, LocalWorkerEndpoint Endpoint,
    string DataDirectory, string ServiceLockFileId);

internal interface ILocalWorkerReconcileObserver
{
    Task<LocalWorkerReconcileObservation> ObserveAsync(LocalWorkerDiscoveryReconcileRequest request,
        CancellationToken token);
}

public static partial class LocalDevelopment
{
    /// <summary>Read-only preimage capture for a later separately reviewed one-shot request.
    /// Capturing a preimage does not authorize publication or attest a live Worker.</summary>
    public static LocalWorkerDiscoveryReconcilePreimage InspectLocalWorkerDiscoveryForReconciliation(string dataDirectory)
    {
        var data = LocalWorkerDiscovery.NormalizeDataDirectory(dataDirectory);
        var snapshot = ReadReconcileSnapshot(data);
        return new(1, data, snapshot.Sha256, snapshot.Bytes.Length, snapshot.FileId,
            new(1, snapshot.Record.Endpoint, snapshot.Record.ProcessId, snapshot.Record.ProcessStartUtcTicks));
    }

    /// <summary>Reconciles only an exact stale discovery file to an independently observed live
    /// Worker. This never launches, stops, or selects an engine. Completed/held/unknown are distinct.</summary>
    public static Task<LocalWorkerDiscoveryReconcileResult> ReconcileLocalWorkerDiscoveryAsync(
        LocalWorkerDiscoveryReconcileRequest request, CancellationToken token) =>
        ReconcileLocalWorkerDiscoveryCoreAsync(request, new LocalWorkerReconcileWindows(), null, null, token);

    internal static async Task<LocalWorkerDiscoveryReconcileResult> ReconcileLocalWorkerDiscoveryCoreAsync(
        LocalWorkerDiscoveryReconcileRequest request, ILocalWorkerReconcileObserver observer,
        Action? beforePublication, Action? afterWriteForChecks, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);
        var data = ValidateReconcileRequest(request);
        string? oldSha = null, preserved = null;
        var publicationStarted = false;
        try
        {
            await using var launchLock = await AcquireLaunchLockAsync(data, token).ConfigureAwait(false);
            var first = ReadReconcileSnapshot(data);
            oldSha = first.Sha256;
            RequireOldPreimage(request, first);
            RequireOldGenerationAbsent(request.OldGeneration);
            var observed = await observer.ObserveAsync(request, token).ConfigureAwait(false);
            RequireCurrentObservation(request, observed);
            // Observation is bounded and read-only. Recheck it just before the exact-file CAS.
            var confirmed = await observer.ObserveAsync(request, token).ConfigureAwait(false);
            RequireCurrentObservation(request, confirmed);
            if (!SameObservation(confirmed, observed))
                throw new InvalidOperationException("The current Worker observation changed before publication.");
            RequireOldGenerationAbsent(request.OldGeneration);
            beforePublication?.Invoke();
            var replacement = new LocalWorkerDiscoveryRecord(1, LocalWorkerProtocol.Product,
                request.CurrentGeneration.Endpoint, data, request.Executable,
                request.CurrentGeneration.ProcessId, request.CurrentGeneration.ProcessStartUtcTicks);
            LocalWorkerDiscovery.Validate(replacement, data);
            var newBytes = JsonSerializer.SerializeToUtf8Bytes(replacement, CerebralJson.Options);
            if (newBytes.Length is < 2 or > 16 * 1024)
                throw new InvalidDataException("Replacement discovery exceeds its bound.");
            var newSha = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant();
            var path = Path.Combine(data, LocalWorkerDiscovery.FileName);
            // Exclusive open fences other file writers/deleters from the final comparison through
            // the durable write. Any failure after Write begins is unknown, never safe to replay.
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                LocalWorkerDiscovery.RejectLink(path);
                var final = ReadReconcileSnapshot(file, data);
                RequireOldPreimage(request, final);
                if (!final.Bytes.AsSpan().SequenceEqual(first.Bytes))
                    throw new InvalidOperationException("The discovery bytes changed before publication.");
                var archivePath = Path.Combine(data, "local-pipe.previous." + first.Sha256 + ".json");
                LocalWorkerDiscovery.RejectLink(archivePath);
                using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    archive.Write(first.Bytes);
                    archive.Flush(true);
                }
                // Only a fully flushed and closed CreateNew archive earns a preserved-path claim.
                preserved = archivePath;
                try
                {
                    token.ThrowIfCancellationRequested();
                    publicationStarted = true;
                    file.Position = 0;
                    file.Write(newBytes);
                    afterWriteForChecks?.Invoke();
                    file.SetLength(newBytes.Length);
                    file.Flush(true);
                }
                catch (Exception error) when (publicationStarted && error is not OutOfMemoryException)
                {
                    return Failure("unknown", "publication_unconfirmed", oldSha, preserved, error);
                }
            }
            try
            {
                var after = ReadReconcileSnapshot(data);
                if (after.Sha256 != newSha || after.Record != replacement)
                    return new(1, "unknown", "postimage_mismatch", oldSha, null, preserved);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return Failure("unknown", "postimage_unavailable", oldSha, preserved, error); }
            return new(1, "completed", "exact_replacement_verified", oldSha, newSha, preserved);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        { return Failure(publicationStarted ? "unknown" : "held",
            publicationStarted ? "publication_unconfirmed" : "cancelled_before_publication", oldSha, preserved, error); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Failure(publicationStarted ? "unknown" : "held",
            publicationStarted ? "publication_unconfirmed" : ReconcileErrorCode(error), oldSha, preserved, error); }
    }

    private sealed record ReconcileSnapshot(byte[] Bytes, string Sha256, string FileId,
        LocalWorkerDiscoveryRecord Record);

    private static ReconcileSnapshot ReadReconcileSnapshot(string data)
    {
        var path = Path.Combine(data, LocalWorkerDiscovery.FileName);
        LocalWorkerDiscovery.RejectLink(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return ReadReconcileSnapshot(file, data);
    }

    private static ReconcileSnapshot ReadReconcileSnapshot(FileStream file, string data)
    {
        if (file.Length is < 2 or > 16 * 1024) throw new InvalidDataException("Discovery preimage has an invalid length.");
        var bytes = new byte[checked((int)file.Length)];
        file.Position = 0; file.ReadExactly(bytes);
        var id = LocalWorkerReconcileWindows.FileId(file.SafeFileHandle);
        var record = LocalWorkerDiscovery.ParseExact(bytes, data);
        return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), id, record);
    }

    private static string ValidateReconcileRequest(LocalWorkerDiscoveryReconcileRequest request)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local discovery reconciliation requires Windows.");
        if (request.Version != 1 || request.OldGeneration is null || request.CurrentGeneration is null)
            throw new ArgumentException("A version-one old and current generation is required.");
        ValidateManagedGeneration(request.OldGeneration);
        ValidateManagedGeneration(request.CurrentGeneration);
        if (request.OldGeneration == request.CurrentGeneration)
            throw new ArgumentException("A distinct current Worker process generation is required.");
        var data = LocalWorkerDiscovery.NormalizeDataDirectory(request.DataDirectory);
        if (!Directory.Exists(data) || !data.Equals(request.DataDirectory, StringComparison.Ordinal))
            throw new ArgumentException("The exact existing local data directory is required.");
        if (!IsSha(request.ExpectedOldSha256) || request.ExpectedOldBytes is < 2 or > 16 * 1024 ||
            string.IsNullOrWhiteSpace(request.ExpectedOldFileId) || request.ExpectedOldFileId.Length != 48)
            throw new ArgumentException("An exact bounded old discovery preimage is required.");
        if (request.Executable != NormalizeManagedPath(request.Executable, false, "Cerebral.Worker.exe") ||
            request.SelectedEnginesDirectory != NormalizeManagedPath(request.SelectedEnginesDirectory, true, null))
            throw new ArgumentException("Exact canonical Worker and selected-engine paths are required.");
        if (string.IsNullOrWhiteSpace(request.AccountSid) || request.AccountSid.Length > 128 ||
            request.SessionId <= 0 || string.IsNullOrWhiteSpace(request.InstallationId) ||
            request.InstallationId.Length > 128 || request.NativeArgv is null ||
            request.NativeArgv.Count is < 4 or > 32 || request.NativeArgv.Any(x => x is null || x.Length > 2048))
            throw new ArgumentException("A bounded exact native Worker identity is required.");
        return data;
    }

    private static void RequireOldPreimage(LocalWorkerDiscoveryReconcileRequest request, ReconcileSnapshot snapshot)
    {
        if (snapshot.Bytes.Length != request.ExpectedOldBytes || snapshot.Sha256 != request.ExpectedOldSha256 ||
            snapshot.FileId != request.ExpectedOldFileId ||
            snapshot.Record.Endpoint != request.OldGeneration.Endpoint ||
            snapshot.Record.ProcessId != request.OldGeneration.ProcessId ||
            snapshot.Record.ProcessStartUtcTicks != request.OldGeneration.ProcessStartUtcTicks)
            throw new InvalidOperationException("The old discovery preimage or file identity changed.");
    }

    private static void RequireOldGenerationAbsent(LocalWorkerGeneration old)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(old.ProcessId);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == old.ProcessStartUtcTicks)
                throw new InvalidOperationException("The old Worker generation is still running.");
        }
        catch (ArgumentException) { /* The exact old PID is absent now. */ }
    }

    private static void RequireCurrentObservation(LocalWorkerDiscoveryReconcileRequest request, LocalWorkerReconcileObservation observed)
    {
        if (observed.ProcessId != request.CurrentGeneration.ProcessId ||
            observed.StartUtcTicks != request.CurrentGeneration.ProcessStartUtcTicks ||
            observed.Endpoint != request.CurrentGeneration.Endpoint ||
            !observed.Executable.Equals(request.Executable, StringComparison.OrdinalIgnoreCase) ||
            observed.AccountSid != request.AccountSid || observed.SessionId != request.SessionId ||
            observed.InstallationId != request.InstallationId ||
            observed.DataDirectory != request.DataDirectory ||
            observed.SelectedEnginesDirectory != request.SelectedEnginesDirectory ||
            string.IsNullOrEmpty(observed.ServiceLockFileId) ||
            !observed.NativeArgv.SequenceEqual(request.NativeArgv, StringComparer.Ordinal))
            throw new InvalidOperationException("The live Worker, pipe, service lock or native arguments differ from the reviewed binding.");
    }

    private static bool SameObservation(LocalWorkerReconcileObservation first, LocalWorkerReconcileObservation second) =>
        first with { NativeArgv = Array.Empty<string>() } == second with { NativeArgv = Array.Empty<string>() } &&
        first.NativeArgv.SequenceEqual(second.NativeArgv, StringComparer.Ordinal);

    private static bool IsSha(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ReconcileErrorCode(Exception error) => error switch
    {
        FileNotFoundException => "discovery_missing",
        UnauthorizedAccessException => "identity_or_file_access_refused",
        IOException => "observation_or_file_io_failed",
        OperationCanceledException => "observation_cancelled",
        InvalidDataException => "invalid_discovery",
        _ => "admission_refused"
    };

    private static LocalWorkerDiscoveryReconcileResult Failure(string state, string code,
        string? oldSha, string? preserved, Exception error) =>
        new(1, state, code, oldSha, null, preserved, error.GetType().FullName,
            error.Message.Length <= 512 ? error.Message : error.Message[..512]);
}
