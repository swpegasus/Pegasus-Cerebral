using System.Text.Json;
using System.Diagnostics;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private IReadOnlyList<RemoteSlotProjection.Registration>? remoteSlotRegistrations;
    private Dictionary<string, RemoteSlotProjection.Observation> remoteSlotObservations = new(StringComparer.Ordinal);
    private int remoteSlotRegistrationVersion;
    private int remoteSlotProbeCursor;
    private DateTimeOffset nextRemoteSlotRead;

    private void ResetRemoteSlotMetrics()
    {
        remoteSlotRegistrations = null;
        remoteSlotObservations.Clear();
        remoteSlotRegistrationVersion++;
        remoteSlotProbeCursor = 0;
        nextRemoteSlotRead = default;
    }

    private void CaptureRemoteSlotRegistrations(HostSettings settings)
    {
        // Retain only bounded display metadata. Owner keys and peer tokens remain in Worker settings.
        remoteSlotRegistrations = RemoteSlotProjection.Capture(settings);
        remoteSlotObservations.Clear();
        remoteSlotRegistrationVersion++;
        remoteSlotProbeCursor = 0;
        nextRemoteSlotRead = default;
    }

    private async Task RefreshRemoteSlotRegistrationsAsync()
    {
        if (!IsConnected || systemWorkMetricsInstallationId is not { } installationId) return;
        var client = Client;
        if (!SystemWorkMetricsBindingCurrent(client, installationId)) return;
        ResetRemoteSlotMetrics();
        var refreshVersion = remoteSlotRegistrationVersion;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var settings = await client.GetAsync<HostSettings>("settings.get", cancellationToken: deadline.Token);
            if (SystemWorkMetricsBindingCurrent(client, installationId) &&
                refreshVersion == remoteSlotRegistrationVersion)
            {
                if (settings.InstallationId == installationId) CaptureRemoteSlotRegistrations(settings);
                else ResetRemoteSlotMetrics();
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (SystemWorkMetricsBindingCurrent(client, installationId) &&
                refreshVersion == remoteSlotRegistrationVersion) ResetRemoteSlotMetrics();
        }
    }

    private async Task<RemoteSlotProjection.Presentation> ReadRemoteSlotMetricsAsync(CerebralClient client,
        string installationId)
    {
        var now = DateTimeOffset.UtcNow;
        if (!SystemWorkMetricsBindingCurrent(client, installationId))
            return RemoteSlotProjection.Show(remoteSlotRegistrations, remoteSlotObservations, now, false);
        var registrations = remoteSlotRegistrations;
        if (registrations is null || now < nextRemoteSlotRead)
            return RemoteSlotProjection.Show(registrations, remoteSlotObservations, now, true);

        var version = remoteSlotRegistrationVersion;
        nextRemoteSlotRead = now + RemoteSlotProjection.PollInterval;
        var (targets, nextCursor) = RemoteSlotProjection.SelectProbePage(registrations, remoteSlotProbeCursor);
        remoteSlotProbeCursor = nextCursor;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        async Task<(string Id, RemoteSlotProjection.Observation? Sample)> ProbeAsync(
            RemoteSlotProjection.Registration registration)
        {
            try
            {
                var started = Stopwatch.GetTimestamp();
                var wire = await client.GetAsync<JsonElement>("workers.models.diagnostics",
                    new { workerId = registration.Id }, cancellationToken: deadline.Token);
                var receivedAt = DateTimeOffset.UtcNow;
                var duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return (registration.Id, RemoteSlotProjection.ReadDiagnostics(wire, receivedAt, duration));
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
            catch (Exception) { return (registration.Id, null); }
        }
        var reads = await Task.WhenAll(targets.Select(ProbeAsync));
        if (!SystemWorkMetricsBindingCurrent(client, installationId) || version != remoteSlotRegistrationVersion)
            return RemoteSlotProjection.Show(remoteSlotRegistrations, remoteSlotObservations, DateTimeOffset.UtcNow, false);
        var updated = new Dictionary<string, RemoteSlotProjection.Observation>(remoteSlotObservations,
            StringComparer.Ordinal);
        foreach (var read in reads)
        {
            if (read.Sample is null) updated.Remove(read.Id);
            else updated[read.Id] = read.Sample;
        }
        remoteSlotObservations = updated;
        return RemoteSlotProjection.Show(registrations, remoteSlotObservations, DateTimeOffset.UtcNow, true);
    }
}
