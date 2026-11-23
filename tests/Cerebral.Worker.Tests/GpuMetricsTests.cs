using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class GpuMetricsTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var devices = HardwareProbe.ParseGpuInventory(
            "NVIDIA GeForce RTX 5080, 16303, 1954, 14024, 37\n");
        Check(devices.Count == 1 && devices[0].Name == "NVIDIA GeForce RTX 5080" &&
              devices[0].TotalMb == 16_303 && devices[0].UsedMb == 1_954 && devices[0].FreeMb == 14_024 &&
              devices[0].UtilizationPercent == 37,
            "GPU inventory parsing must retain driver-reported utilization and used/total/free VRAM.");

        var unified = HardwareProbe.ParseGpuInventory("NVIDIA GB10, 128000, 42000, 86000, 12\n", 128_000, 90_000);
        Check(unified.Single() is { Unified: true, TotalMb: 128_000, FreeMb: 90_000, UsedMb: 38_000, UtilizationPercent: 12 },
            "Unified-memory GPU parsing must report the shared physical pool once.");

        var numeric = HardwareProbe.ParseGpuProcessMemory("101, 2048\n202, 512\n303, 4096\n", new HashSet<int> { 101, 202 });
        Check(numeric.UsedMb == 2_560 && numeric.Error is null,
            "Numeric process VRAM rows must sum only the Worker/model process identities.");

        var noMatch = HardwareProbe.ParseGpuProcessMemory("303, 4096\n", new HashSet<int> { 101, 202 });
        Check(noMatch.UsedMb == 0 && noMatch.Error is null,
            "A successful process probe with no matching GPU processes must report zero rather than unavailable.");

        var wddm = HardwareProbe.ParseGpuProcessMemory("101, [N/A]\n", new HashSet<int> { 101 });
        Check(wddm.UsedMb is null && wddm.Error?.Contains("WDDM", StringComparison.Ordinal) == true,
            "A WDDM N/A row must degrade to unavailable with an actionable reason instead of false zero VRAM.");

        var partial = HardwareProbe.ParseGpuProcessMemory("101, 2048\n202, [N/A]\n", new HashSet<int> { 101, 202 });
        Check(partial.UsedMb is null && partial.Error is not null,
            "A partially measurable process set must stay unavailable instead of under-reporting Cerebral VRAM.");

        Console.WriteLine($"GPU metrics: {checks} deterministic NVIDIA output parsing checks passed; no GPU process or driver was invoked.");
    }
}
