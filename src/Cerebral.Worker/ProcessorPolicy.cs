using System.Diagnostics;

namespace Cerebral.Worker;

using Cerebral.Core;

public sealed record ProcessorPolicyDiagnostics(
    int AvailableLogicalProcessorCount,
    int MaxLogicalProcessorCount,
    IReadOnlyList<int> AllowedLogicalProcessors,
    IReadOnlyList<int> RequestedLogicalProcessors,
    IReadOnlyList<int> EffectiveLogicalProcessors,
    bool Supported,
    bool Applied,
    bool RestartRequired,
    bool DirectChildrenInheritPolicy,
    string? DegradedReason);

public sealed class ProcessorPolicyState
{
    private readonly ProcessorAffinitySnapshot baseline;
    private readonly int startupMaximum;
    private readonly int[] startupAllowed;
    private readonly int[] effective;
    private readonly bool supported;
    private readonly bool applied;
    private readonly bool directChildrenInherit;
    private readonly string? reason;

    internal ProcessorPolicyState(ProcessorAffinitySnapshot baseline, HostSettings settings,
        IReadOnlyList<int> requested, IReadOnlyList<int> effective, bool supported, bool applied,
        bool directChildrenInherit, string? reason)
    {
        this.baseline = baseline;
        startupMaximum = settings.MaxLogicalProcessorCount;
        startupAllowed = CanonicalAllowed(settings);
        this.effective = effective.ToArray();
        this.supported = supported;
        this.applied = applied;
        this.directChildrenInherit = directChildrenInherit;
        this.reason = reason;
    }

    public ProcessorPolicyDiagnostics Diagnostics(HostSettings current)
    {
        var allowed = CanonicalAllowed(current);
        var requested = ProcessorPolicy.ResolveRequested(current, baseline.Processors);
        var restart = current.MaxLogicalProcessorCount != startupMaximum || !allowed.SequenceEqual(startupAllowed);
        var detail = reason;
        if (restart)
            detail = ProcessorPolicy.JoinReason(detail,
                "Saved processor settings differ from the policy applied at Worker startup. Restart the Worker to apply them to the service and future direct child processes.");
        return new(baseline.Processors.Count, current.MaxLogicalProcessorCount, allowed, requested, effective,
            supported, applied && !restart, restart, directChildrenInherit, detail);
    }

    internal static ProcessorPolicyState NotInitialized(HostSettings settings)
    {
        var processors = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount)).ToArray();
        var baseline = new ProcessorAffinitySnapshot(false, processors,
            "Processor policy was not initialized by this host; restart through the normal Worker entry point.", false);
        return new(baseline, settings, ProcessorPolicy.ResolveRequested(settings, processors), processors,
            false, false, false, baseline.Reason);
    }

    private static int[] CanonicalAllowed(HostSettings settings) =>
        settings.AllowedLogicalProcessors.Distinct().Order().ToArray();
}

public static class ProcessorPolicy
{
    public static ProcessorPolicyState Apply(HostSettings settings) => Apply(settings, new ProcessAffinityAdapter());

    internal static ProcessorPolicyState Apply(HostSettings settings, IProcessorAffinityAdapter adapter)
    {
        ProcessorAffinitySnapshot baseline;
        try { baseline = adapter.Read(); }
        catch (Exception error)
        {
            var processors = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount)).ToArray();
            baseline = new(false, processors, "The operating system affinity state could not be read: " + SafeMessage(error), false);
        }

        var requested = ResolveRequested(settings, baseline.Processors);
        if (!baseline.Supported)
            return new(baseline, settings, requested, baseline.Processors, false, false, false,
                JoinReason(baseline.Reason, "Processor affinity is unsupported on this host. The Worker continues without changing its processor set."));

        var availableSet = baseline.Processors.ToHashSet();
        var unavailable = requested.Where(processor => !availableSet.Contains(processor)).ToArray();
        var target = requested.Where(availableSet.Contains).ToArray();
        var reason = baseline.Reason;
        if (unavailable.Length > 0)
            reason = JoinReason(reason, "Requested logical processors are unavailable to this Worker: " + string.Join(", ", unavailable) + ".");
        if (target.Length == 0)
        {
            var explanation = settings.AllowedLogicalProcessors.Count == 0
                ? "Reserving logical processor 0 would leave the Worker with no processor. The existing processor set remains active."
                : "None of the explicitly allowed logical processors is available to this Worker. The existing processor set remains active.";
            return new(baseline, settings, requested, baseline.Processors, true, false, false, JoinReason(reason, explanation));
        }

        if (target.SequenceEqual(baseline.Processors))
            return new(baseline, settings, requested, baseline.Processors, true, true,
                baseline.DirectChildrenInheritPolicy, reason);

        try
        {
            var observed = adapter.Apply(target);
            if (!observed.Supported)
                return new(baseline, settings, requested, observed.Processors, false, false, false,
                    JoinReason(reason, observed.Reason ?? "The operating system did not retain the requested processor affinity."));
            var exact = observed.Processors.SequenceEqual(target);
            if (!exact)
                reason = JoinReason(reason, "The operating system retained a different processor set than requested.");
            reason = JoinReason(reason, observed.Reason);
            return new(baseline, settings, requested, observed.Processors, true, exact,
                exact && observed.DirectChildrenInheritPolicy, reason);
        }
        catch (Exception error)
        {
            return new(baseline, settings, requested, baseline.Processors, true, false, false,
                JoinReason(reason, "The processor policy could not be applied: " + SafeMessage(error) + " The Worker continues with its prior processor set."));
        }
    }

    internal static int[] ResolveRequested(HostSettings settings, IReadOnlyList<int> available)
    {
        IEnumerable<int> selected = settings.AllowedLogicalProcessors.Count > 0
            ? settings.AllowedLogicalProcessors.Distinct().Order()
            : available.Where(processor => processor != 0).Distinct().Order();
        if (settings.MaxLogicalProcessorCount > 0) selected = selected.Take(settings.MaxLogicalProcessorCount);
        return selected.ToArray();
    }

    internal static string? JoinReason(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second;
        return string.IsNullOrWhiteSpace(second) ? first : first.TrimEnd() + " " + second.TrimStart();
    }

    private static string SafeMessage(Exception error) => string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
}

internal sealed record ProcessorAffinitySnapshot(bool Supported, IReadOnlyList<int> Processors, string? Reason,
    bool DirectChildrenInheritPolicy);

internal interface IProcessorAffinityAdapter
{
    ProcessorAffinitySnapshot Read();
    ProcessorAffinitySnapshot Apply(IReadOnlyList<int> processors);
}

internal sealed class ProcessAffinityAdapter : IProcessorAffinityAdapter
{
    public ProcessorAffinitySnapshot Read()
    {
        var fallback = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount)).ToArray();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return new(false, fallback, "This operating system does not expose process affinity through the supported Worker runtime.", false);
        try
        {
            using var process = Process.GetCurrentProcess();
            var mask = unchecked((ulong)process.ProcessorAffinity.ToInt64());
            var processors = Processors(mask);
            if (processors.Length == 0)
                return new(false, fallback, "The operating system returned an empty process-affinity mask.", false);
            return new(true, processors, null, true);
        }
        catch (Exception error) when (error is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            return new(false, fallback, "Process affinity is unavailable: " + error.Message, false);
        }
    }

    public ProcessorAffinitySnapshot Apply(IReadOnlyList<int> processors)
    {
        var width = IntPtr.Size * 8;
        if (processors.Count == 0 || processors.Any(processor => processor < 0 || processor >= width))
            throw new PlatformNotSupportedException($"This runtime can represent logical processor identifiers 0-{width - 1} in one affinity mask.");
        ulong mask = 0;
        foreach (var processor in processors) mask |= 1UL << processor;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            using (var process = Process.GetCurrentProcess()) process.ProcessorAffinity = unchecked((nint)(long)mask);
            return Read();
        }
        throw new PlatformNotSupportedException("Process affinity is supported only on Windows and Linux Worker hosts.");
    }

    private static int[] Processors(ulong mask)
    {
        var result = new List<int>();
        for (var processor = 0; processor < IntPtr.Size * 8; processor++)
            if ((mask & (1UL << processor)) != 0) result.Add(processor);
        return result.ToArray();
    }
}
