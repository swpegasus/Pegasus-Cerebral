using System.Globalization;
using System.IO;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    internal sealed record SystemWorkPresentation(string Agents, string OpenSlots, string Details)
    {
        internal int? LoadedModels { get; init; }
        internal string RemoteSlots { get; init; } = "Remote: — free · workers unknown";
        internal string RemoteDetails { get; init; } = "Remote worker observations are unavailable.";
    }
    private CerebralClient? systemWorkMetricsClient;
    private string? systemWorkMetricsInstallationId;
    private bool systemWorkMetricsUnsupported;
    private bool systemSlotMetricsUnsupported;

    private void ResetSystemWorkMetrics(HostInfo info)
    {
        systemWorkMetricsClient = Client;
        systemWorkMetricsInstallationId = info.InstallationId;
        systemWorkMetricsUnsupported = false;
        systemSlotMetricsUnsupported = false;
        ResetRemoteSlotMetrics();
        ResetSystemInferenceOwnership(Client, info.InstallationId);
        ResetAgentSidebarSnapshot();
        ShowSystemStatusMessage("Cerebral metrics are updating…",
            "Resource and work observations are not yet available for this Worker connection.");
    }

    private async Task<SystemWorkPresentation> ReadSystemWorkMetricsAsync(CerebralClient client, string installationId,
        Task<SystemWorkPresentation> executionTask, Task<RemoteSlotProjection.Presentation> remoteTask)
    {
        if (!SystemWorkMetricsBindingCurrent(client, installationId))
            return UnavailableSystemWork();
        // Separate bounded observations share no admission guarantee or inferred remote totals.
        var slotTask = ReadSystemSlotMetricsAsync(client, installationId);
        await Task.WhenAll(executionTask, slotTask, remoteTask);
        if (!SystemWorkMetricsBindingCurrent(client, installationId)) return UnavailableSystemWork();
        var execution = await executionTask;
        var slots = await slotTask;
        var remote = await remoteTask;
        return execution with { OpenSlots = slots.OpenSlots, LoadedModels = slots.LoadedModels, RemoteSlots = remote.Caption,
            RemoteDetails = remote.Details,
            Details = execution.Details + "\n\n" + slots.Details + "\n\n" + remote.Details };
    }

    private bool SystemWorkMetricsBindingCurrent(CerebralClient client, string installationId) =>
        IsConnected && ReferenceEquals(client, Client) && ReferenceEquals(client, systemWorkMetricsClient) &&
        installationId == systemWorkMetricsInstallationId;

    private async Task<SystemWorkPresentation> ReadSystemExecutionMetricsAsync(CerebralClient client, string installationId)
    {
        if (!SystemWorkMetricsBindingCurrent(client, installationId)) return UnavailableSystemWork();
        if (systemWorkMetricsUnsupported)
        {
            ApplyAgentSidebarSnapshot(client, installationId, AgentSidebarProjection.Unavailable(installationId));
            return UnavailableSystemWork();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var value = await client.GetAsync<JsonElement>("metrics.execution.status", cancellationToken: deadline.Token);
            if (!SystemWorkMetricsBindingCurrent(client, installationId)) return UnavailableSystemWork();
            ApplyAgentSidebarSnapshot(client, installationId, AgentSidebarProjection.ReadSample(value, installationId));
            return ProjectExecutionStatus(value, installationId);
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
        catch (InvalidOperationException error) when (error.Message == "Unknown command: metrics.execution.status")
        {
            if (SystemWorkMetricsBindingCurrent(client, installationId))
            {
                systemWorkMetricsUnsupported = true;
                ApplyAgentSidebarSnapshot(client, installationId, AgentSidebarProjection.Unavailable(installationId));
            }
            return UnavailableSystemWork();
        }
        catch (Exception)
        {
            ApplyAgentSidebarSnapshot(client, installationId, AgentSidebarProjection.Unavailable(installationId));
            return UnavailableSystemWork();
        }
    }

    private async Task<ManagedSlotPresentation> ReadSystemSlotMetricsAsync(CerebralClient client, string installationId)
    {
        if (systemSlotMetricsUnsupported) return UnavailableManagedSlots();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var value = await client.GetAsync<JsonElement>("metrics.slots.status", cancellationToken: deadline.Token);
            return SystemWorkMetricsBindingCurrent(client, installationId)
                ? ProjectManagedSlotStatus(value, installationId) : UnavailableManagedSlots();
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
        catch (InvalidOperationException error) when (error.Message == "Unknown command: metrics.slots.status")
        {
            if (SystemWorkMetricsBindingCurrent(client, installationId)) systemSlotMetricsUnsupported = true;
            return UnavailableManagedSlots();
        }
        catch (Exception) { return UnavailableManagedSlots(); }
    }

    private static SystemWorkPresentation UnavailableSystemWork() => new("— local / — remote", "— managed local / — remote",
        "Current scheduler-owned unique agent counts are unavailable. Remote execution and managed inference slots remain unobserved.");

    internal static SystemWorkPresentation ProjectExecutionStatus(JsonElement value, string expectedInstallationId)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 1024 * 1024 ||
                value.EnumerateObject().GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
                value.GetProperty("schema").GetString() != "cerebral.execution-metrics/1" ||
                value.GetProperty("issuingInstallationId").GetString() != expectedInstallationId ||
                !Guid.TryParseExact(expectedInstallationId, "N", out var owner) || owner == Guid.Empty ||
                !Guid.TryParseExact(value.GetProperty("processGenerationId").GetString(), "N", out _) ||
                !value.GetProperty("processGenerationScope").GetString()!.StartsWith("issuing-worker-scheduler-instance;", StringComparison.Ordinal) ||
                !value.GetProperty("sampledAt").TryGetDateTimeOffset(out var sampled) || sampled == default)
                return UnavailableSystemWork();
            int Count(string key)
            {
                var count = value.GetProperty(key).GetInt32();
                if (count is < 0 or > 256) throw new InvalidDataException();
                return count;
            }
            var partial = value.GetProperty("partial").GetBoolean();
            var truncated = value.GetProperty("truncated").GetBoolean();
            var unprojectable = Count("unprojectableCount");
            var changed = Count("ownershipChangedCount");
            var observed = Count("observedLocalWorkingAgentCount");
            var executions = Count("observedLocalExecutionCount");
            var ownership = Count("capturedOwnershipCount");
            if (observed > executions || executions > ownership || partial != (truncated || unprojectable > 0 || changed > 0) ||
                value.GetProperty("state").GetString() != (partial ? "partial" : "observed") ||
                value.GetProperty("confirmedRemoteWorkingAgentCount").ValueKind != JsonValueKind.Null)
                return UnavailableSystemWork();
            var complete = value.GetProperty("localWorkingAgentCount");
            if (partial && complete.ValueKind != JsonValueKind.Null || !partial && complete.GetInt32() != observed)
                return UnavailableSystemWork();
            var local = partial ? observed > 0 ? "≥" + observed.ToString(CultureInfo.InvariantCulture) : "—" :
                observed.ToString(CultureInfo.InvariantCulture);
            return new(local + " local / — remote", "—",
                $"Sampled local scheduler-owned unique agents: {local}; independent local execution contexts: {executions}.\n" +
                "Unique agents use accepted agent ID plus this execution-owner installation. Ownership includes slot wait, cancellation and cleanup until release; it does not prove provider generation.\n" +
                (partial ? $"Partial sample: unprojectable {unprojectable}, changed ownership {changed}, truncated {truncated}.\n" : "") +
                "Remote dispatch/polling is separate and does not confirm current remote execution. Current provider route and measured all-machine inference slots are unobserved.\n" +
                $"Sampled: {sampled:O}; generation scope: local Worker scheduler instance.");
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or
            InvalidDataException or OverflowException or ArgumentException or NullReferenceException)
        { return UnavailableSystemWork(); }
    }
}
