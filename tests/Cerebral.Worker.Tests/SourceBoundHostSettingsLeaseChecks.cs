using System.Collections;
using System.Reflection;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Actual Worker host and package-lifetime checks with temporary StateStore
/// and a collectible service package. No Worker process, model, SDK or network.
/// </summary>
internal static class SourceBoundHostSettingsLeaseChecks
{
    internal static async Task<int> RunAsync(string exactOwnedFixtureParent)
    {
        if (!Path.IsPathFullyQualified(exactOwnedFixtureParent) ||
            !Directory.Exists(exactOwnedFixtureParent))
            throw new ArgumentException("Provide an existing absolute private fixture parent.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exactOwnedFixtureParent));
        if (parent == Path.GetPathRoot(parent))
            throw new ArgumentException("The fixture parent must be below a volume root.");
        var root = Path.Combine(parent, "source-bound-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Source-bound host: " + description);
            checks++;
        }
        try
        {
            var store = new StateStore(Path.Combine(root, "state"));
            var access = new ScopedPluginSettingsAccess(store, "fixture.source-bound", "1.0.0",
                new PassSettingsPolicy());
            var hostType = typeof(ServicePluginRuntime).GetNestedType("Host", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Actual service host type is unavailable.");
            var constructor = hostType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();
            var host = constructor.Invoke([
                "fixture.source-bound", root, root, Array.Empty<ServicePluginDependencyBinding>(),
                new EventHub(), access, null
            ]);
            try
            {
                var source = (IServiceSourceBoundBackgroundHost)host;
                var settings = ((IServicePluginSettingsHost)host).Settings;
                source.SetSourceBoundSubscription(false, [], 0);
                var saved = settings.Save(1, CerebralJson.Element(new { enabled = true }), 0);
                Check(saved.Changed && saved.State.Revision == 1,
                    "central settings save commits a first exact revision");
                source.SetSourceBoundSubscription(true, ["source-profile"], saved.State.Revision);
                var changed = settings.Save(1, CerebralJson.Element(new { enabled = false }),
                    saved.State.Revision);
                Check(changed.Changed && changed.State.Revision == 2,
                    "later settings change advances the persisted revision");
                var staleRefused = false;
                try { source.SetSourceBoundSubscription(true, ["source-profile"], 1); }
                catch (WorkflowConflictException) { staleRefused = true; }
                Check(staleRefused, "stale subscription cannot be republished after settings change");
                source.SetSourceBoundSubscription(false, [], changed.State.Revision);
                Check(access.Get().Revision == changed.State.Revision,
                    "host metadata decision leaves the actual settings record intact");

                // Hold the real StateStore transaction while another thread
                // reaches subscription admission. It must wait before taking
                // the Host input gate, preserving StateStore -> Host order.
                var inputGate = hostType.GetField("inputObservationGate",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                Thread? contender = null;
                Exception? contenderFailure = null;
                var hostGateFree = store.Locked(() =>
                {
                    contender = new Thread(() =>
                    {
                        try { source.SetSourceBoundSubscription(false, [],
                            changed.State.Revision); }
                        catch (Exception error) { contenderFailure = error; }
                    }) { IsBackground = true };
                    contender.Start();
                    if (!SpinWait.SpinUntil(() =>
                            (contender.ThreadState & ThreadState.WaitSleepJoin) != 0,
                            TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Subscription did not reach its settings gate.");
                    if (!Monitor.TryEnter(inputGate, TimeSpan.FromMilliseconds(250)))
                        return false;
                    Monitor.Exit(inputGate);
                    return true;
                });
                if (!contender!.Join(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Subscription did not settle after StateStore release.");
                if (contenderFailure is not null)
                    throw new InvalidOperationException("Subscription contender failed.", contenderFailure);
                Check(hostGateFree, "subscription waits on StateStore before taking Host input gate");
            }
            finally { ((IDisposable)host).Dispose(); }

            // A non-product accessor must not be called from the host's
            // revision/lease gates. Only the sealed StateStore-backed accessor
            // can establish the ordering and current revision.
            var untrusted = new CountingSettings();
            var other = constructor.Invoke([
                "fixture.source-bound", root, root, Array.Empty<ServicePluginDependencyBinding>(),
                new EventHub(), untrusted, null
            ]);
            try
            {
                var refused = false;
                try { ((IServiceSourceBoundBackgroundHost)other).SetSourceBoundSubscription(false, [], 0); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused && untrusted.GetCalls == 0,
                    "arbitrary scoped-settings getter is not used by host metadata admission");
            }
            finally { ((IDisposable)other).Dispose(); }

            await VerifyRuntimeGateAsync(root, Check);

            var packageId = "fixture.refresh-only";
            var package = Path.Combine(root, "packages", packageId);
            Directory.CreateDirectory(package);
            var assembly = typeof(SourceBoundRefreshOnlyFixturePlugin).Assembly.Location;
            File.Copy(assembly, Path.Combine(package, Path.GetFileName(assembly)));
            File.WriteAllText(Path.Combine(package, ServicePluginRuntime.ManifestFileName),
                System.Text.Json.JsonSerializer.Serialize(new ServicePluginManifest
                {
                    Id = packageId, Name = "Refresh-only lifetime fixture", Version = "1.0.0",
                    Assembly = Path.GetFileName(assembly),
                    EntryType = typeof(SourceBoundRefreshOnlyFixturePlugin).FullName!
                }, CerebralJson.Options));
            var runtime = new ServicePluginRuntime(Path.Combine(root, "packages"),
                Path.Combine(root, "package-data"), new EventHub());
            var loaded = ((IEnumerable)(typeof(ServicePluginRuntime)
                .GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(runtime)!)).Cast<object>().Single();
            var plugin = loaded.GetType().GetProperty("Plugin")!.GetValue(loaded)!;
            var pluginType = plugin.GetType();
            var entered = (TaskCompletionSource)pluginType.GetProperty("Entered")!.GetValue(plugin)!;
            var release = (TaskCompletionSource)pluginType.GetProperty("Release")!.GetValue(plugin)!;
            var disposed = (TaskCompletionSource)pluginType.GetProperty("Disposed")!.GetValue(plugin)!;
            try
            {
                Check(runtime.Status.Single().Loaded, "refresh-only fixture package is actually loaded");
                runtime.QueueOwnSettingsRefresh(packageId);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                runtime.Dispose();
                Check(!runtime.CommittedRootUserInputSettlement.IsCompleted && !disposed.Task.IsCompleted,
                    "refresh-only package teardown waits for its real callback lease");
                release.TrySetResult();
                await runtime.CommittedRootUserInputSettlement.WaitAsync(TimeSpan.FromSeconds(5));
                Check(disposed.Task.IsCompleted,
                    "refresh-only package unloads after the callback lease finishes");
            }
            finally
            {
                release.TrySetResult();
                runtime.Dispose();
                await runtime.CommittedRootUserInputSettlement.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // The collectible package DLL is still referenced by this test method.
            // Callback settlement and Unload do not prove that its ALC was collected.
            // The owner removes this exact root after the child process exits.
            Console.WriteLine("Retained collectible fixture root for post-exit cleanup: " + root);
        }
    }

    private static async Task VerifyRuntimeGateAsync(string root, Action<bool, string> check)
    {
        var store = new StateStore(Path.Combine(root, "gate-state"));
        var packageId = SourceBoundRuntimeGateFixturePlugin.PackageId;
        var package = Path.Combine(root, "gate-packages", packageId);
        Directory.CreateDirectory(package);
        var assembly = typeof(SourceBoundRuntimeGateFixturePlugin).Assembly.Location;
        File.Copy(assembly, Path.Combine(package, Path.GetFileName(assembly)));
        File.WriteAllText(Path.Combine(package, ServicePluginRuntime.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(new ServicePluginManifest
            {
                Id = packageId, Name = "Actual input gate fixture", Version = "1.0.0",
                Assembly = Path.GetFileName(assembly),
                EntryType = typeof(SourceBoundRuntimeGateFixturePlugin).FullName!
            }, CerebralJson.Options));
        var runtime = new ServicePluginRuntime(Path.Combine(root, "gate-packages"),
            Path.Combine(root, "gate-package-data"), new EventHub(),
            (id, version, policy) => new ScopedPluginSettingsAccess(store, id, version, policy));
        Thread? contender = null;
        try
        {
            check(runtime.Status.Single().Loaded, "actual observer package loads for runtime gate checks");
            var loaded = ((IEnumerable)(typeof(ServicePluginRuntime)
                .GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(runtime)!)).Cast<object>().Single();
            var host = loaded.GetType().GetProperty("Host")!.GetValue(loaded)!;
            var generic = (IServiceCommittedRootUserInputHost)host;
            var sourceBound = (IServiceSourceBoundBackgroundHost)host;
            var source = new CommittedRootUserInputSource("source-request", "source-conversation",
                "source-profile", "fixture.source:flow", "1.0.0", null, "source-event", 1);
            var gate = runtime.CommittedRootUserInputGate;
            var target = gate.EligibleObservers(source).Single();

            // No central settings record exists. Hold its actual transaction on
            // this thread while another thread acquires the generic observer.
            // Taking the store lock in generic mode would block this acquisition.
            var started = new ManualResetEventSlim();
            Exception? contenderFailure = null;
            var genericAcquired = false;
            var genericSettledUnderStoreLock = store.Locked(() =>
            {
                contender = new Thread(() =>
                {
                    started.Set();
                    try
                    {
                        using var lease = gate.TryAcquire(target, source);
                        genericAcquired = lease is not null;
                    }
                    catch (Exception error) { contenderFailure = error; }
                }) { IsBackground = true };
                contender.Start();
                if (!started.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Generic runtime gate contender did not start.");
                return contender.Join(TimeSpan.FromSeconds(2));
            });
            if (!contender!.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Generic runtime gate contender did not settle.");
            if (contenderFailure is not null)
                throw new InvalidOperationException("Generic runtime gate contender failed.", contenderFailure);
            check(genericSettledUnderStoreLock && genericAcquired,
                "generic runtime acquisition does not depend on central settings");

            var preferences = new SettingsPreferencesStore(store, () => runtime.SettingsPolicies);
            var saved = preferences.SavePlugin(new(new(packageId,
                SettingsPreferenceContract.GlobalScope, null, "1.0.0", 1,
                CerebralJson.Element(new { enabled = true })), 0));
            check(saved.Changed && saved.State.Revision == 1,
                "actual observer central settings save establishes a revision");
            sourceBound.SetSourceBoundSubscription(true, [source.ProfileId], saved.State.Revision);
            target = gate.EligibleObservers(source).Single();

            // The contender must wait at StateStore before taking the Host gate.
            // Commit the next revision while that barrier is held; its eventual
            // acquisition must reject the stale subscription.
            started = new ManualResetEventSlim();
            contender = null;
            contenderFailure = null;
            ICommittedRootUserInputCallbackLease? staleLease = null;
            var inputGate = host.GetType().GetField("inputObservationGate",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var changedSettings = false;
            var hostGateFree = store.Locked(() =>
            {
                contender = new Thread(() =>
                {
                    started.Set();
                    try { staleLease = gate.TryAcquire(target, source); }
                    catch (Exception error) { contenderFailure = error; }
                }) { IsBackground = true };
                contender.Start();
                if (!started.Wait(TimeSpan.FromSeconds(5)) ||
                    !SpinWait.SpinUntil(() =>
                        (contender.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Source-bound runtime contender did not reach its barrier.");
                var free = Monitor.TryEnter(inputGate, TimeSpan.FromMilliseconds(250));
                if (free) Monitor.Exit(inputGate);
                var changed = preferences.SavePlugin(new(new(packageId,
                    SettingsPreferenceContract.GlobalScope, null, "1.0.0", 1,
                    CerebralJson.Element(new { enabled = false })), saved.State.Revision));
                changedSettings = changed.Changed && changed.State.Revision == 2;
                return free;
            });
            if (!contender!.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Source-bound runtime contender did not settle.");
            if (contenderFailure is not null)
                throw new InvalidOperationException("Source-bound runtime contender failed.", contenderFailure);
            staleLease?.Dispose();
            check(changedSettings,
                "central settings save commits behind the actual acquisition barrier");
            check(hostGateFree && staleLease is null,
                "runtime gate refuses stale source-bound acquisition after settings save");
            generic.SetCommittedRootUserInputSubscription(true, [source.ProfileId]);
            using var restored = gate.TryAcquire(target, source);
            check(restored is not null,
                "generic mode remains available after source-bound revision changes");
        }
        finally
        {
            var contenderSettled = contender is not { IsAlive: true } ||
                contender.Join(TimeSpan.FromSeconds(5));
            runtime.Dispose();
            await runtime.CommittedRootUserInputSettlement.WaitAsync(TimeSpan.FromSeconds(5));
            if (!contenderSettled)
                throw new TimeoutException("Runtime gate contender remained active during cleanup.");
        }
    }

    private sealed class PassSettingsPolicy : IPluginSettingsPolicy
    {
        public PluginSettingsAdmission Admit(PluginSettingsDocument incoming,
            System.Text.Json.JsonElement? currentValues, bool portable) =>
            new(true, 1, incoming.Values.Clone());
        public PluginSettingsProjection ExportPortable(PluginSettingsDocument current) =>
            new(1, current.Values.Clone());
    }

    private sealed class CountingSettings : IScopedPluginSettings
    {
        public int GetCalls { get; private set; }
        public PluginSettingsState Get(string scope = SettingsPreferenceContract.GlobalScope,
            string? scopeId = null)
        {
            GetCalls++;
            throw new InvalidOperationException("Fixture getter must remain unused.");
        }
        public PluginSettingsSaveResult Save(int settingsVersion,
            System.Text.Json.JsonElement values, long expectedRevision,
            string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null) =>
            throw new NotSupportedException();
    }
}

public sealed class SourceBoundRefreshOnlyFixturePlugin : IServicePlugin, IServiceOwnSettingsRefresh
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Initialize(IServicePluginHost host) { }
    public async ValueTask RefreshOwnSettingsAsync(CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        await Release.Task.ConfigureAwait(false);
    }
    public void Dispose() => Disposed.TrySetResult();
}

public sealed class SourceBoundRuntimeGateFixturePlugin : IServicePlugin,
    IServiceCommittedRootUserInputObserver, IPluginSettingsPolicy
{
    public const string PackageId = "fixture.runtime-gate";
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Initialize(IServicePluginHost host) =>
        ((IServiceCommittedRootUserInputHost)host)
            .SetCommittedRootUserInputSubscription(true, ["source-profile"]);
    public ValueTask ObserveCommittedRootUserInputAsync(
        CommittedRootUserInputObservation observation, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming,
        System.Text.Json.JsonElement? currentValues, bool portable) =>
        new(true, 1, incoming.Values.Clone());
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current) =>
        new(1, current.Values.Clone());
    public void Dispose() { }
}
