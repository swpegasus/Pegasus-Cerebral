using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ServicePluginRuntime
{
    private WorkerWorkflows? sourceBoundWorkflows;

    internal void AttachSourceBoundWorkflows(WorkerWorkflows workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        lock (contextContributionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sourceBoundWorkflows is not null) throw new InvalidOperationException("Source-bound host was already attached.");
            sourceBoundWorkflows = workflows;
            foreach (var item in loaded) item.Host.AttachSourceBoundWorkflows(workflows);
        }
    }

    // The save has already committed when this is called. Never make its return
    // value depend on an optional package callback or a scheduling failure.
    internal void QueueOwnSettingsRefresh(string packageId)
    {
        try
        {
            LoadedPlugin? item;
            IDisposable? lease;
            var coalesced = false;
            lock (contextContributionGate)
            {
                if (disposed) return;
                item = loaded.SingleOrDefault(candidate => candidate.Host.PluginId == packageId &&
                    bindings.TryGetValue(packageId, out var binding) && IsCurrentLoadedPlugin(candidate, binding));
                lease = item?.Plugin is IServiceOwnSettingsRefresh
                    ? item.Host.TryAcquireOwnSettingsRefresh(out coalesced) : null;
            }
            if (item is null) return;
            if (coalesced) return;
            if (lease is null)
            {
                if (item.Host.HasSourceBoundSubscription)
                {
                    item.Host.DisableSourceBoundSubscription();
                    try { item.Host.ReportSourceBound("settings-refresh-unavailable"); } catch { }
                }
                return;
            }
            try { _ = Task.Run(async () =>
            {
                using (lease)
                {
                    do
                    {
                        try
                        {
                            await ((IServiceOwnSettingsRefresh)item.Plugin)
                                .RefreshOwnSettingsAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                        {
                            item.Host.DisableSourceBoundSubscription();
                            try { item.Host.ReportSourceBound("settings-refresh-unavailable"); } catch { }
                        }
                    }
                    while (item.Host.ContinueOwnSettingsRefresh());
                }
            }); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                item.Host.AbortOwnSettingsRefresh();
                lease.Dispose();
                item.Host.DisableSourceBoundSubscription();
                try { item.Host.ReportSourceBound("settings-refresh-unavailable"); } catch { }
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // A committed save remains committed. The stale revision gate refuses
            // new source-bound callbacks even when refresh dispatch is unavailable.
        }
    }

    internal void QueueAllOwnSettingsRefresh()
    {
        string[] ids;
        lock (contextContributionGate)
            ids = disposed ? [] : loaded
                .Select(item => item.Host.PluginId).ToArray();
        foreach (var id in ids) QueueOwnSettingsRefresh(id);
    }

    private sealed partial class Host
    {
        private ServicePluginPackageBinding? sourceBoundPackage;
        private WorkerWorkflows? sourceBoundWorkflowHost;
        private bool sourceBoundRefreshRunning;
        private bool sourceBoundRefreshDirty;

        internal void BindSourceBoundPackage(ServicePluginPackageBinding package) =>
            sourceBoundPackage = package ?? throw new ArgumentNullException(nameof(package));

        internal void AttachSourceBoundWorkflows(WorkerWorkflows workflows) =>
            sourceBoundWorkflowHost = workflows ?? throw new ArgumentNullException(nameof(workflows));

        internal bool HasRevisionBoundInputSubscription
        {
            get
            {
                lock (inputObservationGate)
                    return !inputObservationRetired && sourceBoundExpectedSettingsRevision is not null;
            }
        }

        internal T WithSourceBoundSettingsView<T>(
            Func<ScopedPluginSettingsAdmissionView, T> decide)
        {
            ArgumentNullException.ThrowIfNull(decide);
            var access = sourceBoundSettingsAccess ??
                throw new InvalidOperationException("Product-owned central package settings are unavailable.");
            return access.WithGlobalStateLocked(decide);
        }

        public void SetSourceBoundSubscription(bool enabled, IReadOnlyList<string> sourceProfileIds,
            long expectedSettingsRevision)
        {
            ArgumentNullException.ThrowIfNull(sourceProfileIds);
            if (expectedSettingsRevision < 0 || enabled && expectedSettingsRevision == 0)
                throw new ArgumentOutOfRangeException(nameof(expectedSettingsRevision));
            var profiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in sourceProfileIds)
            {
                if (profiles.Count == 1024 || string.IsNullOrEmpty(id) || id.Length > 128 || id == "*" ||
                    id.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
                    !profiles.Add(id))
                    throw new ArgumentException("Source-bound input requires at most 1024 unique exact profile IDs.", nameof(sourceProfileIds));
            }
            if (enabled && profiles.Count == 0)
                throw new ArgumentException("Enabled source-bound input requires a profile.", nameof(sourceProfileIds));
            if (JsonSerializer.SerializeToUtf8Bytes(new { enabled, sourceProfileIds = profiles.Order(StringComparer.Ordinal).ToArray(),
                    expectedSettingsRevision }, CerebralJson.Options).Length > SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes)
                throw new ArgumentException("Source-bound subscription metadata is too large.", nameof(sourceProfileIds));
            var access = sourceBoundSettingsAccess ??
                throw new InvalidOperationException("Product-owned central package settings are unavailable.");
            _ = access.WithGlobalStateLocked(current =>
            {
                lock (inputObservationGate)
                {
                    ObjectDisposedException.ThrowIf(inputObservationRetired, this);
                    if (current.Revision != expectedSettingsRevision || enabled && !current.HasActive)
                        throw new WorkflowConflictException("Package settings changed before source-bound subscription.");
                    inputObservationProfiles = profiles;
                    inputObservationEnabled = enabled;
                    sourceBoundExpectedSettingsRevision = expectedSettingsRevision;
                    return true;
                }
            });
        }

        public ValueTask<SourceBoundBackgroundAdmission> AdmitAsync(
            CommittedRootUserInputObservation source, SourceBoundBackgroundIntent intent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(intent);
            var access = sourceBoundSettingsAccess ??
                throw new InvalidOperationException("Product-owned central package settings are unavailable.");
            var selected = access.WithGlobalStateLocked(current =>
            {
                lock (inputObservationGate)
                {
                    if (inputObservationRetired || !inputObservationEnabled ||
                        !inputObservationProfiles.Contains(source.Source.ProfileId) ||
                        sourceBoundExpectedSettingsRevision is not { } revision ||
                        revision != intent.ExpectedSettingsRevision || current.Revision != revision ||
                        !current.HasActive)
                        throw new WorkflowConflictException("Source-bound subscription or settings changed.");
                    return (
                        Package: sourceBoundPackage ?? throw new InvalidOperationException("Package binding is unavailable."),
                        Workflows: sourceBoundWorkflowHost ?? throw new InvalidOperationException("Workflow host is unavailable."));
                }
            });
            return new(selected.Workflows.SubmitSourceBoundBackgroundAsync(
                source, intent, selected.Package, cancellationToken));
        }

        public ValueTask<SourceBoundBackgroundAdmission> InspectAsync(string workRequestId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServicePluginPackageBinding package;
            WorkerWorkflows workflows;
            lock (inputObservationGate)
            {
                ObjectDisposedException.ThrowIf(inputObservationRetired, this);
                package = sourceBoundPackage ?? throw new InvalidOperationException("Package binding is unavailable.");
                workflows = sourceBoundWorkflowHost ?? throw new InvalidOperationException("Workflow host is unavailable.");
            }
            return ValueTask.FromResult(workflows.InspectSourceBoundBackground(workRequestId, package));
        }

        internal IDisposable? TryAcquireOwnSettingsRefresh(out bool coalesced)
        {
            lock (inputObservationGate)
            {
                coalesced = false;
                if (inputObservationRetired) return null;
                if (sourceBoundRefreshRunning)
                {
                    sourceBoundRefreshDirty = true;
                    coalesced = true;
                    return null;
                }
                sourceBoundRefreshRunning = true;
                inputObservationLeases++;
                return new OwnSettingsRefreshLease(this);
            }
        }

        internal bool ContinueOwnSettingsRefresh()
        {
            lock (inputObservationGate)
            {
                if (sourceBoundRefreshDirty)
                {
                    sourceBoundRefreshDirty = false;
                    return true;
                }
                sourceBoundRefreshRunning = false;
                return false;
            }
        }

        internal void AbortOwnSettingsRefresh()
        {
            lock (inputObservationGate)
            {
                sourceBoundRefreshRunning = false;
                sourceBoundRefreshDirty = false;
            }
        }

        internal void DisableSourceBoundSubscription()
        {
            lock (inputObservationGate)
                if (!inputObservationRetired && sourceBoundExpectedSettingsRevision is not null)
                    inputObservationEnabled = false;
        }

        internal bool HasSourceBoundSubscription
        {
            get { lock (inputObservationGate) return !inputObservationRetired &&
                sourceBoundExpectedSettingsRevision is not null; }
        }

        internal void ReportSourceBound(string code) =>
            sourceBoundEvents.Publish("workflow.input.observer", PluginId, new { code, packageId = PluginId });

        private sealed class OwnSettingsRefreshLease(Host owner) : IDisposable
        {
            private Host? current = owner;
            public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseCommittedRootUserInputLease();
        }
    }
}
