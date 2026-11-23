using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>A same-user launch record, not authority to control a process. A live process and pipe
/// handshake must both match before the desktop may use it. A zero PID is an uncertain launch intent.</summary>
internal sealed record LocalWorkerDiscoveryRecord(int Version, string Product, LocalWorkerEndpoint Endpoint,
    string DataDirectory, string Executable, int ProcessId, long ProcessStartUtcTicks);

internal static class LocalWorkerDiscovery
{
    internal const string FileName = "local-pipe.json";
    private const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 8
    };

    internal static string NormalizeDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Local Worker discovery requires an absolute local data directory.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new ArgumentException("Local Worker discovery cannot use a network drive.");
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The local Worker data directory cannot be a volume root.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Local Worker discovery does not follow linked data directories. Select the actual local directory.");
        return full;
    }

    internal static LocalWorkerDiscoveryRecord? Read(string dataDirectory)
    {
        var data = NormalizeDataDirectory(dataDirectory);
        var path = Path.Combine(data, FileName);
        try
        {
            RejectLink(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is < 2 or > MaximumBytes) throw new InvalidDataException("Local Worker discovery must be a bounded JSON record.");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            return ParseExact(bytes, data);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw new InvalidDataException("Local Worker discovery is invalid. Inspect the local Worker before repairing its discovery record; no replacement was started.", error); }
    }

    internal static LocalWorkerDiscoveryRecord ParseExact(byte[] bytes, string dataDirectory)
    {
        if (bytes.Length is < 2 or > MaximumBytes) throw new InvalidDataException("Local Worker discovery must be a bounded JSON record.");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
        RejectDuplicateProperties(document.RootElement);
        var record = JsonSerializer.Deserialize<LocalWorkerDiscoveryRecord>(bytes, Json)
            ?? throw new InvalidDataException("Local Worker discovery is empty.");
        Validate(record, dataDirectory);
        return record;
    }

    internal static void Write(LocalWorkerDiscoveryRecord record)
    {
        Validate(record, record.DataDirectory);
        var data = NormalizeDataDirectory(record.DataDirectory);
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, FileName);
        RejectLink(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Local Worker discovery exceeds its size bound.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void RemoveIfCurrent(LocalWorkerDiscoveryRecord record)
    {
        var current = Read(record.DataDirectory);
        if (current != record) throw new InvalidOperationException("Local Worker discovery changed. Its current generation was retained.");
        File.Delete(Path.Combine(record.DataDirectory, FileName));
    }

    internal static Process? FindProcess(LocalWorkerDiscoveryRecord record)
    {
        Validate(record, record.DataDirectory);
        if (record.ProcessId == 0)
            throw new InvalidOperationException("A previous local Worker launch has an uncertain process identity. Inspect the Worker and its local log before repairing discovery; no second process was started.");
        Process process;
        try { process = Process.GetProcessById(record.ProcessId); }
        catch (ArgumentException) { return null; }
        try
        {
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != record.ProcessStartUtcTicks)
            { process.Dispose(); return null; }
            var executable = process.MainModule?.FileName;
            if (executable is null || !Path.GetFullPath(executable).Equals(record.Executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The discovered process does not match the recorded Worker executable. No process was started or stopped.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    internal static void AssertDataAvailable(string dataDirectory)
    {
        var data = NormalizeDataDirectory(dataDirectory);
        var path = Path.Combine(data, "service.lock");
        RejectLink(path);
        try { using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error)
        {
            throw new IOException("The local data directory is already owned or cannot be checked. Connect to its existing Worker; no replacement was started.", error);
        }
    }

    internal static void Validate(LocalWorkerDiscoveryRecord record, string expectedDataDirectory)
    {
        if (record.Version != 1 || record.Product != LocalWorkerProtocol.Product || record.Endpoint is null)
            throw new InvalidDataException("The local Worker discovery product or version is unsupported.");
        record.Endpoint.Validate();
        var data = NormalizeDataDirectory(record.DataDirectory);
        if (!data.Equals(NormalizeDataDirectory(expectedDataDirectory), StringComparison.OrdinalIgnoreCase) ||
            !data.Equals(record.DataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The discovery record belongs to a different data directory.");
        if (string.IsNullOrWhiteSpace(record.Executable) || !Path.IsPathFullyQualified(record.Executable) ||
            record.Executable.StartsWith("\\\\", StringComparison.Ordinal) || record.Executable.Length > 2048 ||
            !Path.GetFullPath(record.Executable).Equals(record.Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The discovery record does not identify an exact local executable.");
        if (record.ProcessId < 0 || record.ProcessStartUtcTicks < 0 ||
            (record.ProcessId == 0) != (record.ProcessStartUtcTicks == 0) || record.ProcessStartUtcTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("The discovery process identity is invalid.");
    }

    internal static void RejectLink(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Local Worker discovery does not follow linked control files.");
    }

    internal static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON fields are not valid connection or discovery declarations.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
