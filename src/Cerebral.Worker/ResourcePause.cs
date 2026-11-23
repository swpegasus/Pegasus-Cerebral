using System.Diagnostics;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Model-runtime boundary used by the scheduler's owner-controlled resource policy.</summary>
public interface IResourcePauseModelControl
{
    Task<IReadOnlyList<string>> BeginPauseAsync(CancellationToken cancellationToken);
    Task UnloadPausedAsync(CancellationToken cancellationToken);
    Task ResumePausedAsync(IReadOnlyList<string> modelIds, CancellationToken cancellationToken);
    Task ReopenPausedAdmissionAsync(Action markDurablyActive, CancellationToken cancellationToken);
}

/// <summary>Small service-owned record that keeps a requested local GPU pause across process restarts.</summary>
public sealed record LocalResourcePauseRecord
{
    public int Version { get; init; } = ResourcePausePersistence.CurrentVersion;
    public bool PauseRequested { get; init; }
    public LocalResourceState State { get; init; } = LocalResourceState.Active;
    public bool RerouteEligibleWork { get; init; }
    public List<string> PrePauseResidentModelIds { get; init; } = [];
    public string Detail { get; init; } = "Local GPU admission is active.";
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Fixed typed persistence boundary; callers cannot select arbitrary record coordinates.</summary>
public static class ResourcePausePersistence
{
    public const int CurrentVersion = 1;
    public const string RecordKind = "resource-pause";
    public const string RecordId = "local";
    private const int MaximumResidentModels = 64;
    private const int MaximumDetailCharacters = 2048;

    public static LocalResourcePauseRecord Read(StateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            var saved = store.Get<LocalResourcePauseRecord>(RecordKind, RecordId);
            if (saved is null) return Active();
            if (saved.Version != CurrentVersion || !saved.PauseRequested)
                throw new InvalidDataException("The saved local GPU pause has an unsupported version or intent marker.");
            var ids = NormalizeIds(saved.PrePauseResidentModelIds);
            if (saved.Detail is null || saved.Detail.Length > MaximumDetailCharacters || saved.Detail.Contains('\0'))
                throw new InvalidDataException("The saved local GPU pause detail is invalid.");
            if (!Enum.IsDefined(saved.State)) throw new InvalidDataException("The saved local GPU pause state is invalid.");
            var state = saved.State switch
            {
                LocalResourceState.Paused or LocalResourceState.Faulted => saved.State,
                LocalResourceState.Draining or LocalResourceState.Resuming => LocalResourceState.Paused,
                _ => throw new InvalidDataException("A saved pause cannot restore active local GPU admission.")
            };
            var detail = saved.State == state
                ? saved.Detail
                : "Local GPU admission remains paused after the service restarted during a resource transition.";
            return saved with
            {
                State = state,
                PrePauseResidentModelIds = ids,
                Detail = detail,
                UpdatedAt = saved.UpdatedAt == default ? DateTimeOffset.UtcNow : saved.UpdatedAt
            };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            return new()
            {
                PauseRequested = true,
                State = LocalResourceState.Faulted,
                Detail = BoundDetail("Local GPU admission remains closed because its saved pause record is invalid: " + error.Message),
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>Stages an already-authorized pause while the Worker is stopped, before a replacement runtime starts.</summary>
    public static LocalResourcePauseRecord SaveOfflinePause(StateStore store,
        IReadOnlyList<string> prePauseResidentModelIds, bool rerouteEligibleWork = false, string? detail = null) =>
        SaveRequested(store, LocalResourceState.Paused, rerouteEligibleWork, prePauseResidentModelIds,
            detail ?? "Local GPU admission was paused before the Worker started.", DateTimeOffset.UtcNow);

    internal static LocalResourcePauseRecord SaveRequested(StateStore store, LocalResourceState state,
        bool rerouteEligibleWork, IReadOnlyList<string> prePauseResidentModelIds, string detail,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (state == LocalResourceState.Active || !Enum.IsDefined(state))
            throw new ArgumentException("A durable pause record cannot advertise active local GPU admission.", nameof(state));
        var saved = new LocalResourcePauseRecord
        {
            PauseRequested = true,
            State = state,
            RerouteEligibleWork = rerouteEligibleWork,
            PrePauseResidentModelIds = NormalizeIds(prePauseResidentModelIds),
            Detail = BoundDetail(detail),
            UpdatedAt = updatedAt == default ? DateTimeOffset.UtcNow : updatedAt
        };
        store.Put(RecordKind, RecordId, saved);
        return saved;
    }

    internal static void Clear(StateStore store) => store.Delete(RecordKind, RecordId);

    private static LocalResourcePauseRecord Active() => new();

    private static List<string> NormalizeIds(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count > MaximumResidentModels)
            throw new ArgumentException($"A local GPU pause can retain at most {MaximumResidentModels} resident model identifiers.");
        var result = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var id in result) Configuration.ValidateId(id);
        return result;
    }

    private static string BoundDetail(string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Local GPU admission remains paused." : value.Replace("\0", "", StringComparison.Ordinal);
        return value.Length <= MaximumDetailCharacters ? value : value[..MaximumDetailCharacters];
    }
}

/// <summary>Bounded peer inspection used only to select an already paired, explicitly shared receiver profile.</summary>
public interface IResourcePausePeerProbe
{
    Task<IReadOnlyList<ResourcePausePeerStatus>> ProbeAsync(IReadOnlyList<WorkerRegistration> workers,
        CancellationToken cancellationToken);
}

public sealed class NetworkResourcePausePeerProbe : IResourcePausePeerProbe
{
    public async Task<IReadOnlyList<ResourcePausePeerStatus>> ProbeAsync(IReadOnlyList<WorkerRegistration> workers,
        CancellationToken cancellationToken)
    {
        var paired = workers.Where(worker => !string.IsNullOrWhiteSpace(worker.PeerToken))
            .OrderBy(worker => worker.Id, StringComparer.Ordinal).ToArray();
        var probes = paired.Select(worker => ProbeAsync(worker, cancellationToken)).ToArray();
        return probes.Length == 0 ? [] : await Task.WhenAll(probes).ConfigureAwait(false);
    }

    private static async Task<ResourcePausePeerStatus> ProbeAsync(WorkerRegistration worker,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var clock = Stopwatch.StartNew();
        try
        {
            using var client = new CerebralClient(worker);
            var host = await client.GetAsync<PeerHostInfo>("health", cancellationToken: timeout.Token).ConfigureAwait(false);
            if (host.Product != "Pegasus Cerebral" || host.Protocol != 1)
                throw new InvalidOperationException("Worker identity or protocol mismatch.");
            var metrics = await client.GetAsync<PeerWorkerMetrics>("peer.metrics", cancellationToken: timeout.Token).ConfigureAwait(false);
            var catalog = await client.GetAsync<PeerCatalog>("peer.catalog", cancellationToken: timeout.Token).ConfigureAwait(false);
            if (metrics.Host.Product != host.Product || metrics.Host.Protocol != host.Protocol || metrics.Host.Name != host.Name)
                throw new InvalidOperationException("Worker metrics identity does not match the paired endpoint.");
            clock.Stop();
            var available = Math.Max(0, metrics.Queue.MaximumSlots - metrics.Queue.OwnerRunning - metrics.Queue.PeerRunning);
            var queued = Math.Max(0, metrics.Queue.OwnerQueued + metrics.Queue.PeerQueued);
            return new(worker.Id, string.IsNullOrWhiteSpace(worker.Name) ? host.Name : worker.Name, true,
                Math.Max(0, clock.ElapsedMilliseconds), available, queued,
                catalog.Agents.Select(agent => agent.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(worker.Id, worker.Name, false, null, null, null, [], "Timed out while checking paired capacity.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or ArgumentException)
        {
            return new(worker.Id, worker.Name, false, null, null, null, [], error.Message);
        }
    }
}

public sealed partial class JobScheduler
{
    private readonly object resourcePauseGate = new();
    private readonly IResourcePauseModelControl resourcePauseModels = resourceModels ?? runtime;
    private readonly IResourcePausePeerProbe resourcePausePeers = resourcePeers ?? new NetworkResourcePausePeerProbe();
    private LocalResourcePauseRecord resourcePause = ResourcePausePersistence.Read(store);
    private LocalResourceState resourcePauseState
    {
        get => resourcePause.State;
        set => resourcePause = resourcePause with { State = value };
    }
    private bool resourcePauseReroute
    {
        get => resourcePause.RerouteEligibleWork;
        set => resourcePause = resourcePause with { RerouteEligibleWork = value };
    }
    private int resourcePauseRouted;
    private string resourcePauseDetail
    {
        get => resourcePause.Detail;
        set => resourcePause = resourcePause with { Detail = value };
    }
    private DateTimeOffset resourcePauseUpdatedAt
    {
        get => resourcePause.UpdatedAt;
        set => resourcePause = resourcePause with { UpdatedAt = value };
    }
    private IReadOnlyList<string> resourcePauseModelsToReload
    {
        get => resourcePause.PrePauseResidentModelIds;
        set => resourcePause = resourcePause with { PrePauseResidentModelIds = value.ToList() };
    }
    private IReadOnlyList<ResourcePausePeerStatus> resourcePausePeerStatus = [];
    private Task? resourcePauseTransition;

    private T WithResourceAdmission<T>(Func<bool, T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (resourcePauseGate) return callback(resourcePauseState != LocalResourceState.Active);
    }

    public ResourcePauseStatus GetResourcePauseStatus()
    {
        LocalResourceState state;
        bool reroute;
        int routed;
        string detail;
        DateTimeOffset updated;
        IReadOnlyList<string> models;
        IReadOnlyList<ResourcePausePeerStatus> peers;
        lock (resourcePauseGate)
        {
            state = resourcePauseState;
            reroute = resourcePauseReroute;
            routed = resourcePauseRouted;
            detail = resourcePauseDetail;
            updated = resourcePauseUpdatedAt;
            models = resourcePauseModelsToReload.ToArray();
            peers = resourcePausePeerStatus.ToArray();
        }
        var activeLocal = running.Keys.Count(IsLocalExecutionKey);
        var queuedLocal = store.ActiveJobs().Count(job => IsRunnable(job) && UsesLocalGpu(job) && !running.ContainsKey(job.Id));
        var queuedWorkflows = workflows?.Store.ListReady().Count(WorkflowUsesLocalGpu) ?? 0;
        return new(state, reroute, activeLocal, queuedLocal, queuedWorkflows, routed, models, peers,
            detail, updated);
    }

    /// <summary>
    /// Serializes a short owner operation with local-resource resume. The callback may enter the
    /// state store, so this establishes the existing resource-pause-gate then state-store lock order.
    /// It must not call another scheduler pause API.
    /// </summary>
    internal T WithQuiescentLocalPause<T>(DateTimeOffset? expectedUpdatedAt, Func<DateTimeOffset, T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (resourcePauseGate)
        {
            if (resourcePauseState != LocalResourceState.Paused)
                throw new InvalidOperationException("Pause and fully drain local GPU work before retaining a waiting local conversation during a remote default switch.");
            if (expectedUpdatedAt is { } expected && resourcePauseUpdatedAt != expected)
                throw new InvalidOperationException("The local resource pause changed while the remote model was loading. The switch was not applied.");
            if (running.Keys.Any(IsLocalExecutionKey))
                throw new InvalidOperationException("Pause and fully drain local GPU work before retaining a waiting local conversation during a remote default switch.");
            return callback(resourcePauseUpdatedAt);
        }
    }

    public async Task<ResourcePauseStatus> PauseLocalResourcesAsync(bool rerouteEligibleWork,
        CancellationToken cancellationToken = default)
    {
        Task? refreshOnly = null;
        var existingPause = false;
        lock (resourcePauseGate)
        {
            if (resourcePauseState == LocalResourceState.Resuming)
                throw new InvalidOperationException("Finish resuming local resources before pausing them again.");
            resourcePauseReroute = rerouteEligibleWork;
            resourcePauseUpdatedAt = DateTimeOffset.UtcNow;
            if (resourcePauseState == LocalResourceState.Paused)
            {
                existingPause = true;
                resourcePauseDetail = rerouteEligibleWork
                    ? "Local models remain unloaded while paired capacity is refreshed."
                    : "Local models are unloaded and queued local work remains on this installation.";
                if (rerouteEligibleWork)
                    refreshOnly = resourcePauseTransition = RefreshPeersAndRouteAsync(CancellationToken.None);
            }
            else if (resourcePauseState == LocalResourceState.Draining)
                return GetResourcePauseStatus();
            else
            {
                resourcePauseState = LocalResourceState.Draining;
                resourcePauseDetail = "Local GPU admission is closed. In-flight work is reaching a safe boundary.";
            }
        }
        // Recheck after closing scheduler admission so a racing local maintenance claim cannot escape cancellation.
        if (workflows?.IdleCompactionUsesLocalModel == true) workflows.InterruptIdleCompaction();
        try { PersistResourcePause(); }
        catch (Exception error)
        {
            SetResourcePauseFault("Local GPU admission was closed in memory, but its durable pause intent could not be saved: " + error.Message);
            if (existingPause) return GetResourcePauseStatus();
        }
        if (refreshOnly is not null) return GetResourcePauseStatus();

        IReadOnlyList<string> loaded;
        try
        {
            loaded = await resourcePauseModels.BeginPauseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            SetResourcePauseFault("Local model admission could not be closed safely: " + error.Message);
            return GetResourcePauseStatus();
        }
        lock (resourcePauseGate)
        {
            resourcePauseModelsToReload = resourcePauseModelsToReload.Concat(loaded)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        try { PersistResourcePause(); }
        catch (Exception error)
        {
            SetResourcePauseFault("Local model admission is closed, but the pre-pause resident model list could not be saved: " + error.Message);
            return GetResourcePauseStatus();
        }
        lock (resourcePauseGate) resourcePauseTransition = DrainAndUnloadAsync(CancellationToken.None);
        events.Publish("resources.pause.updated", null, GetResourcePauseStatus());
        return GetResourcePauseStatus();
    }

    public async Task<ResourcePauseStatus> ResumeLocalResourcesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> models;
        lock (resourcePauseGate)
        {
            if (resourcePauseState == LocalResourceState.Active) return GetResourcePauseStatus();
            if (resourcePauseState == LocalResourceState.Draining)
                throw new InvalidOperationException("Local resources are still draining. Wait for the paused state before resuming.");
            if (resourcePauseState == LocalResourceState.Resuming)
                return GetResourcePauseStatus();
            if (resourcePauseState == LocalResourceState.Faulted)
                throw new InvalidOperationException("The pause transition failed closed. Retry Pause after the reported condition is resolved before resuming.");
            resourcePauseState = LocalResourceState.Resuming;
            resourcePauseDetail = resourcePauseModelsToReload.Count == 0
                ? "Reopening local GPU admission."
                : "Reloading the models that were resident before the pause.";
            resourcePauseUpdatedAt = DateTimeOffset.UtcNow;
            models = ModelsStillAssignedLocally(resourcePauseModelsToReload);
            resourcePauseModelsToReload = models;
        }
        try { PersistResourcePause(); }
        catch (Exception error)
        {
            SetResourcePauseFault("Local resources remain paused because the resume transition could not be saved: " + error.Message);
            return GetResourcePauseStatus();
        }
        events.Publish("resources.pause.updated", null, GetResourcePauseStatus());
        try
        {
            await resourcePauseModels.ResumePausedAsync(models, cancellationToken).ConfigureAwait(false);
            await resourcePauseModels.ReopenPausedAdmissionAsync(
                () => ResourcePausePersistence.Clear(store), CancellationToken.None).ConfigureAwait(false);
            lock (resourcePauseGate)
            {
                resourcePause = new LocalResourcePauseRecord();
                resourcePauseState = LocalResourceState.Active;
                resourcePauseReroute = false;
                resourcePauseModelsToReload = [];
                resourcePausePeerStatus = [];
                resourcePauseDetail = "Local GPU admission is active.";
                resourcePauseUpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception error)
        {
            SetResourcePauseFault("Local model reload did not complete; GPU admission remains closed: " + error.Message);
        }
        events.Publish("resources.pause.updated", null, GetResourcePauseStatus());
        return GetResourcePauseStatus();
    }

    internal async Task WaitForResourcePauseTransitionAsync(TimeSpan timeout)
    {
        Task? transition;
        lock (resourcePauseGate) transition = resourcePauseTransition;
        if (transition is not null) await transition.WaitAsync(timeout).ConfigureAwait(false);
    }

    private async Task DrainAndUnloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            bool reroute;
            lock (resourcePauseGate) reroute = resourcePauseReroute;
            if (reroute) await RefreshPeersAndRouteAsync(cancellationToken).ConfigureAwait(false);
            while (running.Keys.Any(IsLocalExecutionKey))
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            await resourcePauseModels.UnloadPausedAsync(cancellationToken).ConfigureAwait(false);
            lock (resourcePauseGate)
            {
                resourcePauseState = LocalResourceState.Paused;
                var retained = store.ActiveJobs().Count(job => IsRunnable(job) && UsesLocalGpu(job)) +
                    (workflows?.Store.ListReady().Count(WorkflowUsesLocalGpu) ?? 0);
                resourcePauseDetail = retained == 0
                    ? "Local models are unloaded. No local work is waiting."
                    : $"Local models are unloaded. {retained} local item{(retained == 1 ? "" : "s")} remain durably queued.";
                resourcePauseUpdatedAt = DateTimeOffset.UtcNow;
            }
            PersistResourcePause();
        }
        catch (Exception error)
        {
            SetResourcePauseFault("Local resources could not be unloaded at a confirmed safe boundary: " + error.Message);
        }
        events.Publish("resources.pause.updated", null, GetResourcePauseStatus());
    }

    private async Task RefreshPeersAndRouteAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ResourcePausePeerStatus> statuses;
        try
        {
            statuses = await resourcePausePeers.ProbeAsync(configuration.Get().Workers, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            statuses = [new("unavailable", "Paired workers", false, null, null, null, [], error.Message)];
        }
        lock (resourcePauseGate)
        {
            resourcePausePeerStatus = statuses.ToArray();
            resourcePauseUpdatedAt = DateTimeOffset.UtcNow;
        }

        var capacity = statuses.Where(peer => peer.Reachable && peer.AvailableSlots > 0)
            .ToDictionary(peer => peer.WorkerId, peer => peer.AvailableSlots!.Value, StringComparer.Ordinal);
        foreach (var queued in store.ActiveJobs().Where(job => IsRunnable(job) && UsesLocalGpu(job) &&
                     job.PeerGrantId is null && job.ParentJobId is null && job.RemoteSubmission is null)
                 .OrderBy(Priority).ThenBy(job => job.CreatedAt).ToArray())
        {
            var receiverProfile = queued.Profile.RemoteProfileId ?? queued.ProfileId;
            var peer = statuses.FirstOrDefault(candidate => candidate.Reachable &&
                capacity.GetValueOrDefault(candidate.WorkerId) > 0 &&
                candidate.SharedProfileIds.Contains(receiverProfile, StringComparer.Ordinal));
            if (peer is null) continue;
            var routed = store.Locked(() =>
            {
                var current = store.Get<AgentJob>("job", queued.Id);
                if (current is null || !IsRunnable(current) || !UsesLocalGpu(current) || current.PeerGrantId is not null ||
                    current.ParentJobId is not null || current.RemoteSubmission is not null) return false;
                current.WorkerId = peer.WorkerId;
                current.WaitingReason = $"Routed to paired worker '{peer.Name}' while the local GPU is reserved.";
                Save(current);
                return true;
            });
            if (!routed) continue;
            capacity[peer.WorkerId]--;
            lock (resourcePauseGate)
            {
                resourcePauseRouted++;
                resourcePauseDetail = "Eligible queued assignments are using confirmed paired capacity; unsupported or unmatched work remains local.";
                resourcePauseUpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private bool IsLocalExecutionKey(string key)
    {
        if (key == WorkerWorkflows.IdleCompactionExecutionKey) return workflows?.IdleCompactionUsesLocalModel == true;
        if (key.StartsWith("workflow:", StringComparison.Ordinal))
            return workflows?.Store.TryGet(key["workflow:".Length..]) is { } run && WorkflowUsesLocalGpu(run);
        return store.Get<AgentJob>("job", key) is { } job && UsesLocalGpu(job);
    }

    private static bool UsesLocalGpu(AgentJob job) =>
        job.WorkerId == "local" && IsLocalModelWorker(job.Profile.ModelWorkerId);

    private static bool IsLocalModelWorker(string? workerId) =>
        string.IsNullOrWhiteSpace(workerId) || workerId == "local";

    private IReadOnlyList<string> ModelsStillAssignedLocally(IReadOnlyList<string> residentIds)
    {
        var profiles = store.List<AgentProfile>("agent");
        var local = profiles.Where(profile => IsLocalModelWorker(profile.ModelWorkerId))
            .Select(profile => profile.ModelId).ToHashSet(StringComparer.Ordinal);
        var remote = profiles.Where(profile => !IsLocalModelWorker(profile.ModelWorkerId))
            .Select(profile => profile.ModelId).ToHashSet(StringComparer.Ordinal);
        // Preserve a deliberately loaded, unassigned model. If current profiles moved a saved ID to
        // remote inference, reload it only when another current profile still has a local binding.
        return residentIds.Where(id => local.Contains(id) || !remote.Contains(id))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool WorkflowUsesLocalGpu(Cerebral.Contracts.Workflows.WorkflowRun run)
    {
        try
        {
            var snapshot = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot);
            if (IsLocalModelWorker(snapshot.Profile.ModelWorkerId)) return true;
            return run.Effect is { CommandId: WorkerWorkflowEffects.CompactCommand,
                       State: Cerebral.Contracts.Workflows.WorkflowEffectState.Prepared or
                              Cerebral.Contracts.Workflows.WorkflowEffectState.Dispatched or
                              Cerebral.Contracts.Workflows.WorkflowEffectState.Unknown } &&
                   IsLocalModelWorker(snapshot.Compactor?.Profile.ModelWorkerId);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            // Invalid frozen execution settings must never cause pause admission to open.
            return true;
        }
    }

    private void PersistResourcePause()
    {
        lock (resourcePauseGate)
        {
            resourcePause = ResourcePausePersistence.SaveRequested(store, resourcePauseState,
                resourcePauseReroute, resourcePauseModelsToReload, resourcePauseDetail, resourcePauseUpdatedAt);
        }
    }

    private void SetResourcePauseFault(string detail)
    {
        lock (resourcePauseGate)
        {
            resourcePauseState = LocalResourceState.Faulted;
            resourcePauseDetail = detail;
            resourcePauseUpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                resourcePause = ResourcePausePersistence.SaveRequested(store, resourcePauseState,
                    resourcePauseReroute, resourcePauseModelsToReload, resourcePauseDetail, resourcePauseUpdatedAt);
            }
            catch (Exception persistenceError)
            {
                resourcePauseDetail = $"{detail} Durable pause state also could not be updated: {persistenceError.Message}";
            }
        }
        events.Publish("resources.pause.updated", null, GetResourcePauseStatus());
    }
}
