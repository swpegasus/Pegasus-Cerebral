using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class ProcessorPolicyTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            checks++;
        }

        var defaultAdapter = new FakeAdapter([0, 1, 2, 3]);
        var defaults = new HostSettings();
        var defaultState = ProcessorPolicy.Apply(defaults, defaultAdapter);
        var defaultDiagnostics = defaultState.Diagnostics(defaults);
        Check(defaultAdapter.Applied.SequenceEqual([1, 2, 3]) &&
              defaultDiagnostics.RequestedLogicalProcessors.SequenceEqual([1, 2, 3]) &&
              defaultDiagnostics.EffectiveLogicalProcessors.SequenceEqual([1, 2, 3]) &&
              defaultDiagnostics is { Supported: true, Applied: true, RestartRequired: false, DirectChildrenInheritPolicy: true },
            "default policy reserves logical processor 0 and is inherited by direct children when supported");

        var cappedAdapter = new FakeAdapter([0, 1, 2, 3, 4]);
        var capped = new HostSettings { MaxLogicalProcessorCount = 2 };
        var cappedDiagnostics = ProcessorPolicy.Apply(capped, cappedAdapter).Diagnostics(capped);
        Check(cappedAdapter.Applied.SequenceEqual([1, 2]) && cappedDiagnostics.EffectiveLogicalProcessors.SequenceEqual([1, 2]),
            "maximum processor count is applied after the default processor-zero reservation");

        var explicitAdapter = new FakeAdapter([0, 1, 2, 3, 4, 5]);
        var explicitSettings = new HostSettings { MaxLogicalProcessorCount = 2, AllowedLogicalProcessors = [5, 0, 3] };
        var explicitDiagnostics = ProcessorPolicy.Apply(explicitSettings, explicitAdapter).Diagnostics(explicitSettings);
        Check(explicitDiagnostics.AllowedLogicalProcessors.SequenceEqual([0, 3, 5]) &&
              explicitDiagnostics.RequestedLogicalProcessors.SequenceEqual([0, 3]) && explicitAdapter.Applied.SequenceEqual([0, 3]),
            "an explicit processor list can deliberately include processor 0 and combines deterministically with the maximum");

        var partialAdapter = new FakeAdapter([0, 1, 2, 3]);
        var partialSettings = new HostSettings { AllowedLogicalProcessors = [2, 9] };
        var partialDiagnostics = ProcessorPolicy.Apply(partialSettings, partialAdapter).Diagnostics(partialSettings);
        Check(partialDiagnostics.RequestedLogicalProcessors.SequenceEqual([2, 9]) &&
              partialDiagnostics.EffectiveLogicalProcessors.SequenceEqual([2]) && partialDiagnostics.Applied &&
              partialDiagnostics.DegradedReason?.Contains("9", StringComparison.Ordinal) == true,
            "unavailable explicit processors are omitted with actionable degraded diagnostics");

        var unsupportedAdapter = new FakeAdapter([0, 1, 2, 3], supported: false,
            reason: "Fixture operating system has no affinity API.");
        var unsupportedDiagnostics = ProcessorPolicy.Apply(defaults, unsupportedAdapter).Diagnostics(defaults);
        Check(unsupportedAdapter.ApplyCalls == 0 && !unsupportedDiagnostics.Supported && !unsupportedDiagnostics.Applied &&
              unsupportedDiagnostics.RequestedLogicalProcessors.SequenceEqual([1, 2, 3]) &&
              unsupportedDiagnostics.EffectiveLogicalProcessors.SequenceEqual([0, 1, 2, 3]) &&
              unsupportedDiagnostics.DegradedReason?.Contains("continues", StringComparison.OrdinalIgnoreCase) == true,
            "unsupported affinity preserves startup and reports requested and effective sets without failing");

        var failedAdapter = new FakeAdapter([0, 1, 2, 3]) { ApplyError = new IOException("fixture affinity rejection") };
        var failedDiagnostics = ProcessorPolicy.Apply(defaults, failedAdapter).Diagnostics(defaults);
        Check(!failedDiagnostics.Applied && failedDiagnostics.EffectiveLogicalProcessors.SequenceEqual([0, 1, 2, 3]) &&
              failedDiagnostics.DegradedReason?.Contains("fixture affinity rejection", StringComparison.Ordinal) == true,
            "an operating-system apply failure retains the prior set and does not abort Worker policy initialization");

        var singleAdapter = new FakeAdapter([0]);
        var singleDiagnostics = ProcessorPolicy.Apply(defaults, singleAdapter).Diagnostics(defaults);
        Check(singleAdapter.ApplyCalls == 0 && !singleDiagnostics.Applied && singleDiagnostics.RequestedLogicalProcessors.Count == 0 &&
              singleDiagnostics.EffectiveLogicalProcessors.SequenceEqual([0]) &&
              singleDiagnostics.DegradedReason?.Contains("leave the Worker with no processor", StringComparison.Ordinal) == true,
            "a single-processor host degrades safely instead of selecting an empty processor set");

        var restartAdapter = new FakeAdapter([0, 1, 2, 3]);
        var startup = new HostSettings { MaxLogicalProcessorCount = 2 };
        var restartState = ProcessorPolicy.Apply(startup, restartAdapter);
        var changed = new HostSettings { MaxLogicalProcessorCount = 1, AllowedLogicalProcessors = [3] };
        var restartDiagnostics = restartState.Diagnostics(changed);
        Check(restartDiagnostics.RestartRequired && !restartDiagnostics.Applied && restartDiagnostics.DirectChildrenInheritPolicy &&
              restartDiagnostics.RequestedLogicalProcessors.SequenceEqual([3]) &&
              restartDiagnostics.EffectiveLogicalProcessors.SequenceEqual([1, 2]) &&
              restartDiagnostics.DegradedReason?.Contains("Restart the Worker", StringComparison.Ordinal) == true,
            "diagnostics distinguish newly requested settings from the inherited effective startup policy until restart");

        Console.WriteLine($"Processor policy: {checks} deterministic checks passed (fake affinity adapter; no process affinity changed).");
    }

    private sealed class FakeAdapter : IProcessorAffinityAdapter
    {
        private readonly ProcessorAffinitySnapshot baseline;
        public IReadOnlyList<int> Applied { get; private set; } = [];
        public int ApplyCalls { get; private set; }
        public Exception? ApplyError { get; init; }

        public FakeAdapter(IReadOnlyList<int> processors, bool supported = true, string? reason = null) =>
            baseline = new(supported, processors.ToArray(), reason, supported);

        public ProcessorAffinitySnapshot Read() => baseline;

        public ProcessorAffinitySnapshot Apply(IReadOnlyList<int> processors)
        {
            ApplyCalls++;
            if (ApplyError is not null) throw ApplyError;
            Applied = processors.ToArray();
            return new(true, Applied, null, true);
        }
    }
}
