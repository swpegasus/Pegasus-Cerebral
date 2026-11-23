using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

internal sealed record ExecutionMetricsAcceptedRoute(string Purpose, string AgentId, int AgentRevision,
    string? ModelId, string ModelWorkerId, string? ModelWorkerInstallationId, string Provenance);

internal sealed record ExecutionMetricsObservation(string ExecutionId, string Kind,
    string SchedulerOwnerInstallationId, string? ExecutionOwnerInstallationId, string? ExecutionWorkerId,
    string? AgentId, int? AgentRevision, bool CancellationRequested, string ProjectionState,
    string? UnavailableReason, IReadOnlyList<ExecutionMetricsAcceptedRoute> AcceptedModelRoutes);

internal sealed record ExecutionMetricsSnapshot(string Schema, string IssuingInstallationId,
    string ProcessGenerationId, string ProcessGenerationScope, DateTimeOffset SampledAt,
    string State, bool Partial, bool Truncated, int MaximumObservations, int CapturedOwnershipCount,
    int UnprojectableCount, int OwnershipChangedCount, int ObservedLocalExecutionCount,
    int ObservedLocalWorkingAgentCount, int? LocalWorkingAgentCount, int? ConfirmedRemoteWorkingAgentCount,
    int ObservedRemoteDispatchOrReconciliationCount, int ObservedMaintenanceCount,
    IReadOnlyList<ExecutionMetricsObservation> Observations, string Semantics)
{
    public bool Unprojectable => UnprojectableCount > 0;
}

// Private fixture input carries accepted records only. It is never returned by the owner operation.
internal sealed record ExecutionMetricsInput(string OwnershipKey, bool CancellationRequested,
    WorkflowSubmission? Submission = null, AgentJob? Job = null, string? UnavailableReason = null);

public sealed partial class JobScheduler
{
    internal const int MaximumExecutionMetricObservations = 256;
    private readonly string executionMetricsGeneration = Guid.NewGuid().ToString("N");

    /// <summary>Samples current scheduler ownership, not durable active rows or provider admission.
    /// The caller must obtain and authorize the issuing installation identity independently.</summary>
    internal ExecutionMetricsSnapshot ObserveExecutionMetrics(string issuingInstallationId, int maximumObservations = MaximumExecutionMetricObservations)
    {
        ValidateExecutionMetricRequest(issuingInstallationId, maximumObservations);
        // Admission/finally use resource admission -> StateStore. This read takes only StateStore,
        // then exact journal reads, and never acquires a runtime/resource lock in reverse order.
        return store.Locked(() =>
        {
            var sampledAt = DateTimeOffset.UtcNow;
            var captured = running.Take(maximumObservations + 1).ToArray();
            var truncated = captured.Length > maximumObservations;
            var inputs = new List<ExecutionMetricsInput>(maximumObservations);
            var changed = 0;
            foreach (var pair in captured.Take(maximumObservations))
            {
                ExecutionMetricsInput input;
                try
                {
                    if (pair.Key == WorkerWorkflows.IdleCompactionExecutionKey)
                        input = new(pair.Key, pair.Value.IsCancellationRequested);
                    else if (pair.Key.StartsWith("workflow:", StringComparison.Ordinal))
                    {
                        var run = workflows?.Store.TryGet(pair.Key["workflow:".Length..]);
                        input = new(pair.Key, pair.Value.IsCancellationRequested, run?.Submission,
                            UnavailableReason: run is null ? "accepted-workflow-unavailable" : null);
                    }
                    else
                    {
                        var job = store.Get<AgentJob>("job", pair.Key);
                        input = new(pair.Key, pair.Value.IsCancellationRequested, Job: job,
                            UnavailableReason: job is null ? "accepted-job-unavailable" : null);
                    }
                }
                catch (Exception error) when (IsWorkflowRunnerFailure(error))
                {
                    // Storage diagnostics may contain private bodies/paths. Publish only a fixed code.
                    input = new(pair.Key, false, UnavailableReason: "accepted-record-unreadable");
                }
                // Idle-compaction removal is concurrent with StateStore. A replaced/removed token
                // cannot establish current ownership of the row read for its predecessor.
                if (!running.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value))
                {
                    changed++;
                    input = new(pair.Key, input.CancellationRequested, UnavailableReason: "ownership-changed-during-sample");
                }
                inputs.Add(input);
            }
            // Plugin scan owns a task rather than a cancellation entry; account for it separately.
            if (executions.TryGetValue(PluginTestScanKey, out var scan) && !scan.IsCompleted)
            {
                if (inputs.Count < maximumObservations) inputs.Add(new(PluginTestScanKey, false));
                else truncated = true;
            }
            return ProjectExecutionMetrics(issuingInstallationId, executionMetricsGeneration, sampledAt,
                inputs, maximumObservations, truncated, changed);
        });
    }

    internal static ExecutionMetricsSnapshot ProjectExecutionMetrics(string issuingInstallationId,
        string processGenerationId, DateTimeOffset sampledAt, IEnumerable<ExecutionMetricsInput> inputs,
        int maximumObservations = MaximumExecutionMetricObservations, bool truncated = false, int ownershipChangedCount = 0)
    {
        ValidateExecutionMetricRequest(issuingInstallationId, maximumObservations);
        if (!MetricIdentifier(processGenerationId) || sampledAt == default || ownershipChangedCount < 0)
            throw new ArgumentException("Execution metrics require a bounded generation identity, sample time and nonnegative drift count.");
        ArgumentNullException.ThrowIfNull(inputs);
        var bounded = inputs.Take(maximumObservations + 1).ToArray();
        truncated |= bounded.Length > maximumObservations;
        var observations = bounded.Take(maximumObservations).Select(input => ProjectExecutionObservation(issuingInstallationId, input)).ToArray();
        var unprojectable = observations.Count(item => item.ProjectionState != "observed");
        var local = observations.Where(item => item.ProjectionState == "observed" &&
            item.Kind is "workflow-execution" or "legacy-local-execution").ToArray();
        var identities = local.Select(item => (item.ExecutionOwnerInstallationId, item.AgentId)).Distinct().Count();
        var partial = truncated || unprojectable > 0 || ownershipChangedCount > 0;
        return new("cerebral.execution-metrics/1", issuingInstallationId, processGenerationId,
            "issuing-worker-scheduler-instance; not ModelRuntime ownerProcessGenerationId or runtimeEpoch", sampledAt,
            partial ? "partial" : "observed", partial, truncated, maximumObservations, observations.Length,
            unprojectable, ownershipChangedCount, local.Length, identities, partial ? null : identities, null,
            observations.Count(item => item.ProjectionState == "observed" && item.Kind == "remote-dispatch-or-reconciliation"),
            observations.Count(item => item.ProjectionState == "observed" && item.Kind == "maintenance"), observations,
            "Current sampled scheduler ownership only, including cancellation/cleanup and slot waiters until their ownership exits. " +
            "Local unique identities use issuing installation plus accepted agent ID; execution rows remain independent across revisions/routes. " +
            "Remote dispatch/polling does not confirm remote execution. Maintenance is separate. " +
            "Accepted primary/compactor routes are configuration provenance, not current provider activity, available slots, hardware capacity or readiness. " +
            "This sample is not an atomic admission fence and does not count durable ActiveWorkflows again.");
    }

    private static ExecutionMetricsObservation ProjectExecutionObservation(string installationId, ExecutionMetricsInput input)
    {
        if (input is null) return Unprojectable("", "invalid-observation");
        if (!MetricIdentifier(input.OwnershipKey)) return Unprojectable("", "invalid-ownership-identity");
        if (input.UnavailableReason is not null)
            return Unprojectable(input.OwnershipKey, input.UnavailableReason == "ownership-changed-during-sample"
                ? input.UnavailableReason : "accepted-record-unavailable");
        if (input.OwnershipKey is WorkerWorkflows.IdleCompactionExecutionKey or PluginTestScanKey)
            return new(input.OwnershipKey, "maintenance", installationId, installationId, "local", null, null,
                input.CancellationRequested, "observed", null, []);
        try
        {
            if (input.OwnershipKey.StartsWith("workflow:", StringComparison.Ordinal))
            {
                var submission = input.Submission ?? throw new InvalidOperationException();
                if (input.Job is not null || input.OwnershipKey != "workflow:" + submission.RequestId)
                    throw new InvalidOperationException();
                var accepted = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
                var profile = accepted.Profile;
                if (profile.Id != submission.ProfileId || !MetricProfile(profile) || profile.WorkerId != "local")
                    throw new InvalidOperationException();
                var routes = new List<ExecutionMetricsAcceptedRoute> { AcceptedRoute("primary", profile, accepted.Model, "accepted-workflow-settings") };
                if (accepted.Compactor is { } compactor)
                {
                    if (!MetricProfile(compactor.Profile) || compactor.Profile.WorkerId != "local") throw new InvalidOperationException();
                    routes.Add(AcceptedRoute("compactor", compactor.Profile, compactor.Model, "accepted-workflow-settings"));
                }
                return new(input.OwnershipKey, "workflow-execution", installationId, installationId, "local",
                    profile.Id, profile.Revision, input.CancellationRequested, "observed", null, routes.ToArray());
            }
            var job = input.Job ?? throw new InvalidOperationException();
            if (input.Submission is not null || job.Id != input.OwnershipKey || !MetricProfile(job.Profile) ||
                job.ProfileId != job.Profile.Id || !MetricIdentifier(job.WorkerId)) throw new InvalidOperationException();
            var remote = job.WorkerId != "local";
            return new(job.Id, remote ? "remote-dispatch-or-reconciliation" : job.Kind == "compact" ? "maintenance" : "legacy-local-execution",
                installationId, remote ? null : installationId, job.WorkerId, job.Profile.Id, job.Profile.Revision,
                input.CancellationRequested, "observed", null,
                [AcceptedRoute("primary", job.Profile, null, "accepted-legacy-profile; model-worker-installation-unobserved")]);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or InvalidDataException or NullReferenceException)
        { return Unprojectable(input.OwnershipKey, "accepted-settings-unprojectable"); }

        ExecutionMetricsObservation Unprojectable(string id, string reason) => new(id, "unprojectable", installationId,
            null, null, null, null, input?.CancellationRequested ?? false, "unavailable", reason, []);
    }

    private static ExecutionMetricsAcceptedRoute AcceptedRoute(string purpose, AgentProfile profile,
        WorkerExecutionSnapshot.ModelBinding? model, string provenance)
    {
        var modelId = model?.Id ?? profile.ModelId;
        var worker = model?.ModelWorkerId ?? profile.ModelWorkerId;
        var origin = model?.ModelWorkerInstallationId;
        if ((!string.IsNullOrEmpty(modelId) && !MetricIdentifier(modelId)) || !MetricIdentifier(worker) ||
            origin is not null && !MetricIdentifier(origin)) throw new InvalidOperationException();
        return new(purpose, profile.Id, profile.Revision, string.IsNullOrEmpty(modelId) ? null : modelId, worker,
            origin, provenance);
    }

    private static bool MetricProfile(AgentProfile? profile) => profile is { Revision: >= 0 } &&
        MetricIdentifier(profile.Id) && MetricIdentifier(profile.WorkerId) && MetricIdentifier(profile.ModelWorkerId);

    private static bool MetricIdentifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    private static void ValidateExecutionMetricRequest(string installationId, int maximumObservations)
    {
        if (!MetricIdentifier(installationId)) throw new ArgumentException("A validated issuing installation identity is required.", nameof(installationId));
        if (maximumObservations is < 1 or > MaximumExecutionMetricObservations)
            throw new ArgumentOutOfRangeException(nameof(maximumObservations), "Execution observations must be bounded to 1..256.");
    }
}
