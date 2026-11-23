using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Cerebral.Core;

namespace Cerebral.Worker;

public static partial class HardwareProbe
{
    private static readonly object CpuGate = new();
    private static ulong previousIdle;
    private static ulong previousKernel;
    private static ulong previousUser;

    public static async Task<ResourceInfo> ReadAsync(CancellationToken token = default)
    {
        var gpus = new List<GpuInfo>();
        long? available = null; long? physicalTotal = null;
        if (OperatingSystem.IsWindows())
        {
            var info = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref info)) { available = (long)(info.AvailablePhysical / 1024 / 1024); physicalTotal = (long)(info.TotalPhysical / 1024 / 1024); }
        }
        else if (File.Exists("/proc/meminfo"))
        {
            var lines = await File.ReadAllLinesAsync("/proc/meminfo", token);
            var line = lines.FirstOrDefault(x => x.StartsWith("MemAvailable:"));
            if (long.TryParse(line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var kb)) available = kb / 1024;
            var totalLine = lines.FirstOrDefault(x => x.StartsWith("MemTotal:"));
            if (long.TryParse(totalLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var totalKb)) physicalTotal = totalKb / 1024;
        }
        try
        {
            var start = new ProcessStartInfo("nvidia-smi") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--query-gpu=name,memory.total,memory.used,memory.free,utilization.gpu"); start.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(start) ?? throw new IOException("Cannot start GPU probe.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token); var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token); var output = await outputTask; var error = await errorTask;
                if (process.ExitCode != 0) throw new IOException(error[..Math.Min(error.Length, 400)]);
                gpus.AddRange(ParseGpuInventory(output, physicalTotal, available));
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { gpus.Add(new("GPU probe unavailable", null, null, false, ex.Message)); }
        return new(Environment.WorkingSet / 1024 / 1024, available, gpus)
        {
            SystemTotalMb = physicalTotal,
            CpuUtilizationPercent = ReadCpuUtilization()
        };
    }

    public static async Task<long?> ReadGpuProcessMemoryMbAsync(IReadOnlySet<int> processIds,
        CancellationToken token = default) => (await ReadGpuProcessMemoryAsync(processIds, token)).UsedMb;

    internal static async Task<GpuProcessMemoryProbe> ReadGpuProcessMemoryAsync(IReadOnlySet<int> processIds,
        CancellationToken token = default)
    {
        if (processIds.Count == 0) return new(0, null);
        try
        {
            var start = new ProcessStartInfo("nvidia-smi")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--query-compute-apps=pid,used_gpu_memory");
            start.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(start) ?? throw new IOException("Cannot start GPU process probe.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask; var error = await errorTask;
            if (process.ExitCode != 0)
                return new(null, BoundProbeError(error, "The NVIDIA process-memory probe failed."));
            return ParseGpuProcessMemory(output, processIds);
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            return new(null, BoundProbeError(error.Message, "Per-process GPU memory is unavailable."));
        }
    }

    internal static IReadOnlyList<GpuInfo> ParseGpuInventory(string output, long? physicalTotalMb = null,
        long? physicalAvailableMb = null)
    {
        var result = new List<GpuInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',').Select(value => value.Trim()).ToArray();
            if (fields.Length < 5 || string.IsNullOrWhiteSpace(fields[0])) continue;
            var unified = fields[0].Contains("GB10", StringComparison.OrdinalIgnoreCase);
            var reportedTotal = ParseMetric(fields[1]);
            var reportedUsed = ParseMetric(fields[2]);
            var reportedFree = ParseMetric(fields[3]);
            var total = unified ? physicalTotalMb ?? reportedTotal : reportedTotal;
            var free = unified ? physicalAvailableMb ?? reportedFree : reportedFree;
            var used = unified && total is { } unifiedTotal && free is { } unifiedFree
                ? Math.Max(0, unifiedTotal - unifiedFree)
                : reportedUsed;
            var utilization = ParseMetric(fields[4]) is { } percent ? (int?)Math.Clamp(percent, 0, 100) : null;
            result.Add(new(fields[0], total, free, unified, UtilizationPercent: utilization) { UsedMb = used });
        }
        return result;
    }

    internal static GpuProcessMemoryProbe ParseGpuProcessMemory(string output, IReadOnlySet<int> processIds)
    {
        long total = 0;
        var matched = false;
        var unavailable = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',').Select(value => value.Trim()).ToArray();
            if (fields.Length < 2 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ||
                !processIds.Contains(pid)) continue;
            matched = true;
            if (ParseMetric(fields[1]) is { } memory) total += Math.Max(0, memory);
            else unavailable = true;
        }
        if (unavailable)
            return new(null, "The GPU driver listed a Cerebral process but did not expose its VRAM usage. This is common with NVIDIA WDDM drivers; device-wide VRAM remains available.");
        return new(matched ? total : 0, null);
    }

    private static long? ParseMetric(string value)
    {
        var token = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : null;
    }

    private static string BoundProbeError(string value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 320 ? text : text[..320] + "…";
    }

    internal sealed record GpuProcessMemoryProbe(long? UsedMb, string? Error);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended; }
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);

    private static double? ReadCpuUtilization()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return null;
        lock (CpuGate)
        {
            var idle = idleTime.Value; var kernel = kernelTime.Value; var user = userTime.Value;
            var oldIdle = previousIdle; var oldKernel = previousKernel; var oldUser = previousUser;
            previousIdle = idle; previousKernel = kernel; previousUser = user;
            if (oldKernel == 0 && oldUser == 0 || kernel < oldKernel || user < oldUser || idle < oldIdle) return null;
            var idleDelta = idle - oldIdle;
            var total = (kernel - oldKernel) + (user - oldUser);
            if (total == 0) return null;
            var busy = total > idleDelta ? total - idleDelta : 0;
            return Math.Round(Math.Clamp(busy * 100d / total, 0d, 100d), 1);
        }
    }
}
