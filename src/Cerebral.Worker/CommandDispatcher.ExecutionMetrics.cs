using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private ExecutionMetricsSnapshot ReadExecutionMetrics(JsonElement arguments, CancellationToken token)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            throw new ArgumentException("Execution metrics require an empty argument object.");
        // Select only the durable issuing identity from a bounded read snapshot. A local
        // owner does not need an HTTP access key for this authenticated pipe diagnostic,
        // and metrics must never enter Configuration.Get normalization or route admission.
        var installation = ReadMetricInstallationId(token);
        token.ThrowIfCancellationRequested();
        return scheduler.ObserveExecutionMetrics(installation);
    }

    private string ReadMetricInstallationId(CancellationToken token) => store.WithOwnerSnapshotState(false, state =>
        {
            if (state.ConfigurationBody is null) throw new InvalidDataException("Execution metrics owner identity is unavailable.");
            using var document = OwnerSnapshotJson.Parse(state.ConfigurationBody, StateStore.MaximumSnapshotConfigurationBytes);
            var fields = OwnerSnapshotJson.Fields(document.RootElement);
            var id = OwnerSnapshotJson.Text(fields, "installationId", 128, false, out _);
            OwnerSnapshotConfiguration.RequireInstallationId(id);
            return id;
        }, token);

    private object ReadManagedSlotMetrics(JsonElement arguments, CancellationToken token)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            throw new ArgumentException("Slot metrics require an empty argument object.");
        var installation = ReadMetricInstallationId(token);
        var slots = runtime.ObserveManagedSlots();
        // Separate admission policy from individually sampled allocator counters. Neither
        // this read nor an available counter reserves a native slot or proves hardware fit.
        var executionAdmission = !scheduler.HasIntentionalWorkHold() && workflows?.Engine is { IsPaused: false };
        return new { schema = "cerebral.slot-metrics/1", issuingInstallationId = installation,
            executionAdmissionOpen = executionAdmission, runtime = slots,
            remote = (object?)null, scope = "local-managed-slot-observation; not hardware maximum or atomic admission" };
    }

    private static readonly WorkerWorkflows.OperationDescriptor ExecutionMetricsOperation = new(
        "metrics.execution.status", 1,
        "Bounded current scheduler-owned local agent identities and independent accepted execution routes. Remote working agents and inference capacity remain unobserved; no provider or configuration mutation.",
        CerebralJson.Element(new { type = "object", additionalProperties = false }), true);
    private static readonly WorkerWorkflows.OperationDescriptor ManagedSlotMetricsOperation = new(
        "metrics.slots.status", 1,
        "Bounded sampled managed allocator capacity, available/quarantined counts and separate local admission policy. Remote capacity and hardware maxima remain unobserved; no provider I/O or configuration mutation.",
        CerebralJson.Element(new { type = "object", additionalProperties = false }), true);
}
