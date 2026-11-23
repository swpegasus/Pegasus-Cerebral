using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Isolated record, process-identity and lock checks. Never starts or stops a Worker.</summary>
internal static class LocalWorkerDiscoveryChecks
{
    internal static int Verify()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cerebral-discovery-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Isolated local discovery verification failed."); checks++; }
        void Rejected(Action action)
        {
            var rejected = false;
            try { action(); } catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or IOException) { rejected = true; }
            Check(rejected);
        }
        try
        {
            Check(LocalWorkerDiscovery.Read(directory) is null);
            using var current = Process.GetCurrentProcess();
            var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.DiscoveryCheck." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var record = new LocalWorkerDiscoveryRecord(1, LocalWorkerProtocol.Product, endpoint, directory,
                Path.GetFullPath(current.MainModule!.FileName), current.Id, current.StartTime.ToUniversalTime().Ticks);
            LocalWorkerDiscovery.Write(record);
            Check(LocalWorkerDiscovery.Read(directory) == record);
            using (var process = LocalWorkerDiscovery.FindProcess(record)) Check(process?.Id == current.Id);
            using (var stale = LocalWorkerDiscovery.FindProcess(record with { ProcessStartUtcTicks = record.ProcessStartUtcTicks - 1 })) Check(stale is null);
            Rejected(() => LocalWorkerDiscovery.FindProcess(record with { Executable = Path.Combine(directory, "different.exe") }));
            Rejected(() => LocalWorkerDiscovery.FindProcess(record with { ProcessId = 0, ProcessStartUtcTicks = 0 }));
            Rejected(() => LocalWorkerDiscovery.Validate(record, Path.Combine(directory, "other")));
            Rejected(() => LocalWorkerDiscovery.Validate(record with { Endpoint = endpoint with { InstanceId = "wrong-generation" } }, directory));
            Rejected(() => LocalWorkerDiscovery.Validate(record with { ProcessId = 0 }, directory));
            Rejected(() => LocalWorkerDiscovery.NormalizeDataDirectory(Path.GetPathRoot(directory)!));
            Rejected(() => LocalWorkerDiscovery.NormalizeDataDirectory("relative-directory"));
            Rejected(() => LocalWorkerDiscovery.NormalizeDataDirectory("\\\\server\\share\\data"));
            LocalWorkerDiscovery.AssertDataAvailable(directory); Check(true);
            using (var owned = new FileStream(Path.Combine(directory, "service.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Rejected(() => LocalWorkerDiscovery.AssertDataAvailable(directory));
            var replacement = record with { Endpoint = endpoint with { InstanceId = Guid.NewGuid().ToString("N") } };
            LocalWorkerDiscovery.Write(replacement);
            Rejected(() => LocalWorkerDiscovery.RemoveIfCurrent(record));
            Check(LocalWorkerDiscovery.Read(directory) == replacement);
            LocalWorkerDiscovery.RemoveIfCurrent(replacement);
            Check(LocalWorkerDiscovery.Read(directory) is null);
            var path = Path.Combine(directory, LocalWorkerDiscovery.FileName);
            var json = JsonSerializer.Serialize(record, CerebralJson.Options);
            File.WriteAllText(path, json.TrimEnd()[..^1] + ",\"unknown\":true}");
            Rejected(() => LocalWorkerDiscovery.Read(directory));
            File.WriteAllText(path, json.TrimEnd()[..^1] + ",\"version\":1}");
            Rejected(() => LocalWorkerDiscovery.Read(directory));
            File.WriteAllText(path, new string(' ', 16 * 1024 + 1));
            Rejected(() => LocalWorkerDiscovery.Read(directory));
            File.WriteAllText(path, "{}");
            Rejected(() => LocalWorkerDiscovery.Read(directory));
            return checks;
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("cerebral-discovery-check-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact discovery-check directory.");
            Directory.Delete(full, true);
        }
    }
}
