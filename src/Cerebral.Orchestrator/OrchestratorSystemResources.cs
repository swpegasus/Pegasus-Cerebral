using System.Globalization;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    internal sealed record SystemResourcePresentation(IReadOnlyList<string> Values, string Details);

    internal static SystemResourcePresentation ProjectSystemResources(DesktopStatusMetrics metrics, SystemWorkPresentation? work = null)
    {
        static string Percent(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value)
            ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
        static string Memory(long? value) => value is >= 0
            ? Gib(value.Value).ToString("0.0", CultureInfo.InvariantCulture) : "—";
        var local = metrics.Cerebral;
        var resources = metrics.Resources;
        var total = resources.SystemTotalMb is > 0 ? resources.SystemTotalMb : null;
        var used = total is { } capacity && resources.SystemAvailableMb is >= 0 && resources.SystemAvailableMb <= capacity
            ? capacity - resources.SystemAvailableMb.Value : (long?)null;
        var gpu = ProjectGpuStatus(metrics);
        var cpu = $"{Percent(local?.CpuUtilizationPercent)} Cerebral / {Percent(resources.CpuUtilizationPercent)} total";
        var ram = $"{Memory(local?.MemoryMb)} Cerebral / {Memory(used)}/{Memory(total)} GiB total";
        // This contract observes device utilization, but has no process-owned GPU utilization.
        // Never attribute device-wide GPU activity to Cerebral or infer it from VRAM ownership.
        var gpuValue = "— Cerebral / " + gpu.Utilization + " total";
        var vram = $"{Memory(local?.GpuMemoryMb)} GiB Cerebral / {gpu.Memory} total";
        var queue = metrics.QueuedAssignments >= 0 ? metrics.QueuedAssignments.ToString(CultureInfo.InvariantCulture) : "—";
        var details = "Readouts show Cerebral-owned usage followed by machine usage. RAM shows machine used/capacity; VRAM shows observed device used/capacity.\n" +
            "Owned usage includes only the exact local Cerebral desktop, Worker and model process set. Codex and Cortex are excluded.\n" +
            "Process-owned GPU utilization is unavailable from this telemetry contract.\n" +
            "Managed slot availability, allocation and quarantine are separate allocator observations. Accepted agents, scheduler assignments, queued work and configured maxima are not slot capacity.\n" +
            $"Current scheduler assignments: {(metrics.ActiveAssignments >= 0 ? metrics.ActiveAssignments.ToString(CultureInfo.InvariantCulture) : "unavailable")}\n" +
            $"Durable active workflow rows: {(metrics.ActiveWorkflows >= 0 ? metrics.ActiveWorkflows.ToString(CultureInfo.InvariantCulture) : "unavailable")} (may overlap scheduler assignments)\n" +
            $"Sampled: {metrics.SampledAt:O}\n" +
            (local is null ? "Cerebral-owned process metrics are unavailable; the Worker process memory alone is not a combined owned total.\n" :
                $"Local Cerebral process count: {local.ProcessCount}\n" +
                (local.GpuMemoryMb is null ? "Cerebral process VRAM unavailable" +
                    (string.IsNullOrWhiteSpace(local.GpuMemoryUnavailableReason) ? ".\n" : ": " + local.GpuMemoryUnavailableReason + "\n") : "") +
                (local.StorageAvailableBytes is { } free ? "Model storage: " + FormatBytes(free) + " free.\n" : "")) +
            gpu.Details;
        return new([cpu, ram, gpuValue, vram, work?.OpenSlots ?? SlotStatusPresentation.UnknownAllocatorCaption, queue],
            details + (work is null ? "" : "\n\nAccepted agents (scheduler observation): " + work.Agents +
                ". This is not slot capacity.\n\n" + work.Details));
    }
}
