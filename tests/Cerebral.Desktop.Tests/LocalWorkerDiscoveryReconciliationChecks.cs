using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Desktop.Tests;

internal static class LocalWorkerDiscoveryReconciliationChecks
{
    private sealed class FixtureObserver(Func<LocalWorkerReconcileObservation> read) : ILocalWorkerReconcileObserver
    {
        internal int Calls { get; private set; }
        public Task<LocalWorkerReconcileObservation> ObserveAsync(
            LocalWorkerDiscoveryReconcileRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(read());
        }
    }

    internal static async Task<int> Run()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory, "ReconcileFixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Discovery reconciliation check failed: " + label);
            checks++;
        }
        try
        {
            var data = Path.Combine(scratch, "data");
            var image = Path.Combine(scratch, "Cerebral.Worker.exe");
            var engines = Path.Combine(scratch, "engines");
            Directory.CreateDirectory(data); Directory.CreateDirectory(engines);
            File.WriteAllBytes(image, [1]);
            var lockPath = Path.Combine(data, "service.lock");
            using (var lockOwner = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var id = LocalWorkerReconcileWindows.ObserveServiceLock(lockPath, Environment.ProcessId);
                Check(id.Length == 48, "actual native service lock holder and file ID are observed");
            }
            var unheldLockRejected = false;
            try { _ = LocalWorkerReconcileWindows.ObserveServiceLock(lockPath, Environment.ProcessId); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidDataException)
            { unheldLockRejected = true; }
            Check(unheldLockRejected, "an unheld service lock is not counted as exclusive ownership");
            Check(LocalWorkerReconcileWindows.IsExactSharingViolation(
                    new IOException("sharing", unchecked((int)0x80070020))) &&
                !LocalWorkerReconcileWindows.IsExactSharingViolation(
                    new IOException("access", unchecked((int)0x80070005))) &&
                !LocalWorkerReconcileWindows.IsExactSharingViolation(
                    new IOException("other facility", unchecked((int)0x80100020))),
                "only the exact Win32 sharing-violation HRESULT proves the probe was denied");
            var currentArgv = LocalWorkerReconcileWindows.ReadNativeArgv(Environment.ProcessId);
            Check(currentArgv.Length > 0 && Path.GetFullPath(currentArgv[0]).Equals(
                Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase),
                "actual native command line is read from current process");
            var oversizedArgvRejected = false;
            try { _ = LocalWorkerReconcileWindows.ParseNativeCommandLine(
                string.Join(' ', Enumerable.Repeat("argument", 33))); }
            catch (InvalidDataException) { oversizedArgvRejected = true; }
            Check(oversizedArgvRejected, "oversized native argv is refused through the pointer cleanup path");
            var oldEndpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.Local." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var currentEndpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.Local." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var old = new LocalWorkerGeneration(1, oldEndpoint, int.MaxValue - 1, 638900000000000000);
            var current = new LocalWorkerGeneration(1, currentEndpoint, int.MaxValue - 2, 638900000000000100);
            var original = new LocalWorkerDiscoveryRecord(1, LocalWorkerProtocol.Product,
                oldEndpoint, data, image, old.ProcessId, old.ProcessStartUtcTicks);
            var path = Path.Combine(data, LocalWorkerDiscovery.FileName);
            var args = new[] { image, "--local-run", "--data", data, "--pipe-name", currentEndpoint.PipeName,
                "--pipe-instance", currentEndpoint.InstanceId, "--service-plugins", engines, "--engines", engines };
            LocalWorkerReconcileObservation observed() => new(current.ProcessId, current.ProcessStartUtcTicks,
                image, "S-1-5-21-1-2-3-1001", 1, args, engines, "fixture-installation",
                currentEndpoint, data, "fixture-service-lock-id");
            LocalWorkerDiscoveryReconcileRequest request()
            {
                var pre = LocalDevelopment.InspectLocalWorkerDiscoveryForReconciliation(data);
                return new(1, data, pre.Sha256, pre.Bytes, pre.FileId, old, current, image,
                    observed().AccountSid, 1, args, engines, "fixture-installation");
            }
            async Task<LocalWorkerDiscoveryReconcileResult> run(LocalWorkerDiscoveryReconcileRequest item,
                Func<LocalWorkerReconcileObservation>? read = null, Action? before = null, Action? after = null)
            {
                var observer = new FixtureObserver(read ?? observed);
                return await LocalDevelopment.ReconcileLocalWorkerDiscoveryCoreAsync(item, observer,
                    before, after, CancellationToken.None).ConfigureAwait(false);
            }
            void reset() => LocalWorkerDiscovery.Write(original);
            reset();
            var oldBytes = File.ReadAllBytes(path);
            var wrongHash = await run(request() with { ExpectedOldSha256 = new string('a', 64) }).ConfigureAwait(false);
            Check(wrongHash.State == "held" && File.ReadAllBytes(path).AsSpan().SequenceEqual(oldBytes),
                "changed hash holds before mutation");

            var wrongPeer = await run(request(), () => observed() with { ProcessId = current.ProcessId - 1 }).ConfigureAwait(false);
            Check(wrongPeer.State == "held" && File.ReadAllBytes(path).AsSpan().SequenceEqual(oldBytes),
                "different OS peer holds before mutation");
            var wrongStart = await run(request(), () => observed() with { StartUtcTicks = current.ProcessStartUtcTicks - 1 }).ConfigureAwait(false);
            Check(wrongStart.State == "held" && File.ReadAllBytes(path).AsSpan().SequenceEqual(oldBytes),
                "different process start holds before mutation");
            var wrongLock = await run(request(), () => observed() with { ServiceLockFileId = "" }).ConfigureAwait(false);
            Check(wrongLock.State == "held" && File.ReadAllBytes(path).AsSpan().SequenceEqual(oldBytes),
                "missing service lock identity holds before mutation");
            var observedCalls = 0;
            var changedPeer = await run(request(), () => ++observedCalls == 1
                ? observed() : observed() with { SessionId = 2 }).ConfigureAwait(false);
            Check(changedPeer.State == "held" && File.ReadAllBytes(path).AsSpan().SequenceEqual(oldBytes),
                "changed peer between observations holds before mutation");
            using (var self = Process.GetCurrentProcess())
            {
                var liveOld = old with { ProcessId = self.Id,
                    ProcessStartUtcTicks = self.StartTime.ToUniversalTime().Ticks };
                LocalWorkerDiscovery.Write(original with { ProcessId = liveOld.ProcessId,
                    ProcessStartUtcTicks = liveOld.ProcessStartUtcTicks });
                var liveOldResult = await run(request() with { OldGeneration = liveOld }).ConfigureAwait(false);
                Check(liveOldResult.State == "held" && LocalWorkerDiscovery.Read(data)?.ProcessId == self.Id,
                    "live old generation holds before mutation");
            }
            reset();
            var changed = await run(request(), before: () =>
                LocalWorkerDiscovery.Write(original with { ProcessStartUtcTicks = old.ProcessStartUtcTicks + 1 })).ConfigureAwait(false);
            Check(changed.State == "held" && LocalWorkerDiscovery.Read(data)?.ProcessStartUtcTicks == old.ProcessStartUtcTicks + 1,
                "late old-generation change holds at final CAS");

            reset();
            var replacedIdentityRequest = request();
            // The content is identical but the file object changes, so content hash alone cannot admit it.
            File.Delete(path); reset();
            var differentFile = await run(replacedIdentityRequest).ConfigureAwait(false);
            Check(differentFile.State == "held" && LocalWorkerDiscovery.Read(data) == original,
                "changed file identity holds before mutation");

            reset();
            var successRequest = request();
            var successOldBytes = File.ReadAllBytes(path);
            var success = await run(successRequest).ConfigureAwait(false);
            Check(success.State == "completed" && success.Code == "exact_replacement_verified" &&
                LocalWorkerDiscovery.Read(data)?.ProcessId == current.ProcessId,
                "exact replacement completes with current generation");
            Check(success.PreservedOldPath is not null && File.ReadAllBytes(success.PreservedOldPath).AsSpan().SequenceEqual(successOldBytes) &&
                Convert.ToHexString(SHA256.HashData(successOldBytes)).Equals(success.OldSha256, StringComparison.OrdinalIgnoreCase),
                "original record is preserved byte-for-byte");

            var existingArchive = File.ReadAllBytes(success.PreservedOldPath!);
            reset();
            var collision = await run(request()).ConfigureAwait(false);
            Check(collision.State == "held" && collision.PreservedOldPath is null &&
                File.ReadAllBytes(success.PreservedOldPath!).AsSpan().SequenceEqual(existingArchive) &&
                LocalWorkerDiscovery.Read(data) == original,
                "existing archive is not reused or deleted and never receives a preserved-path claim");
            File.Delete(success.PreservedOldPath!);
            reset();
            var unknownOldBytes = File.ReadAllBytes(path);
            var unknown = await run(request(), after: () => throw new IOException("fixture interruption after write")).ConfigureAwait(false);
            Check(unknown.State == "unknown" && unknown.Code == "publication_unconfirmed" &&
                unknown.PreservedOldPath is not null &&
                File.ReadAllBytes(unknown.PreservedOldPath).AsSpan().SequenceEqual(unknownOldBytes) &&
                unknown.FailureMessage?.Contains("fixture interruption after write", StringComparison.Ordinal) == true,
                "post-write failure is unknown, preserves old bytes and reports original failure");
            return checks;
        }
        finally
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar;
            if (!scratch.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(scratch).StartsWith("ReconcileFixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside its exact test directory.");
            Directory.Delete(scratch, recursive: true);
        }
    }
}
