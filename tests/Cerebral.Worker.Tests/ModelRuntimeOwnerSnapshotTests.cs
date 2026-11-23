using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class ModelRuntimeOwnerSnapshotTests
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var passed = new List<string>();
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            passed.Add(label);
        }
        async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
        {
            try { await action(); }
            catch (T) { Check(true, label); return; }
            throw new Exception("FAIL: " + label);
        }
        void Throws<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T) { Check(true, label); return; }
            throw new Exception("FAIL: " + label);
        }

        var model = new ModelRecord
        {
            Id = "snapshot-fixture", Path = "/synthetic/model.gguf", ChatTemplate = "{{ public fixture }}",
            StopSequences = ["stop", "", "stop"], MaximumContextTokens = 8192, ParallelRequests = 2,
            GpuLayers = 7, VisionProjectorPath = "/synthetic/projector.gguf",
            ContextTokens = 2048, RecommendedContextTokens = 4096, AutomaticContextSizing = true,
            NativeContextTokens = 8192, State = "loaded"
        };
        var capture = OwnerRuntimeLaunchCapture.Capture(model, "/synthetic/llama-server");
        var inputs = capture.CreateLaunchInputs();
        model.Path = "/changed/weights"; model.ChatTemplate = "changed";
        model.StopSequences[0] = "changed"; model.StopSequences.Add("extra");
        model.MaximumContextTokens = 16384; model.ParallelRequests = 8; model.GpuLayers = 0;
        model.VisionProjectorPath = null; model.ContextTokens = 512;
        var successful = capture.SuccessfulStartup(6144);
        var successfulBinding = successful.Binding ?? throw new Exception("Expected represented launch binding.");
        Check(successfulBinding.Id == "snapshot-fixture" && successfulBinding.Path == "/synthetic/model.gguf" &&
            successfulBinding.ChatTemplate == "{{ public fixture }}" &&
            successfulBinding.StopSequences.SequenceEqual(new[] { "stop", "", "stop" }) &&
            successfulBinding.MaximumContextTokens == 8192 && successfulBinding.ParallelRequests == 2 &&
            successfulBinding.GpuLayers == 7 && successfulBinding.VisionProjectorPath == "/synthetic/projector.gguf",
            "all eight launch binding fields survive source mutation");
        Check(successful.Context.ConfiguredContextTokens == 4096 && successful.Context.MeasuredContextTokens == 6144 &&
            successful.Context.State == "measured" && successful.Context.Source == "startup_log",
            "requested argument floor differs from persisted and measured context");
        Check(successful.Provider.Kind == "llama.cpp" && successful.Provider.Executable == "/synthetic/llama-server" &&
            successful.Provider.IdentityState == "captured_launch_configuration" &&
            successful.Provider.ImplementationIdentity is null, "provider configuration is not binary attestation");
        inputs.StopSequences.Clear(); inputs.Path = "/changed/local-copy";
        Check(capture.CreateLaunchInputs().Path == "/synthetic/model.gguf" &&
            capture.CreateLaunchInputs().StopSequences.Count == 3, "launch-input copies cannot mutate capture");
        var arguments = ModelRuntime.ServerArguments(capture.CreateLaunchInputs(), 12345, 256);
        Check(arguments[Array.IndexOf(arguments.ToArray(), "--ctx-size") + 1] == "8192" &&
            arguments[Array.IndexOf(arguments.ToArray(), "--parallel") + 1] == "2" &&
            arguments[Array.IndexOf(arguments.ToArray(), "--model") + 1] == successfulBinding.Path,
            "actual launch argument inputs match frozen provenance");
        Throws<NotSupportedException>(() => ((IList<string>)successfulBinding.StopSequences)[0] = "change",
            "returned stops are immutable");
        var second = capture.SuccessfulStartup(6144);
        Check(!ReferenceEquals(successfulBinding.StopSequences, second.Binding!.StopSequences),
            "successful observations own independent stop copies");
        var unmeasured = capture.SuccessfulStartup(null);
        Check(unmeasured.Context.MeasuredContextTokens is null && unmeasured.Context.State == "unobserved" &&
            unmeasured.Context.ConfiguredContextTokens == 4096, "configured context never becomes measured fallback");
        Throws<InvalidDataException>(() => capture.SuccessfulStartup(512), "below-floor startup cannot publish success");
        var legacy = capture.CreateLaunchInputs(); legacy.StopSequences = null!;
        var legacyCapture = OwnerRuntimeLaunchCapture.Capture(legacy, "/synthetic/server");
        Check(legacyCapture.SuccessfulStartup(null).Binding is null && legacyCapture.CreateLaunchInputs().StopSequences is null,
            "legacy null stops preserve launch input but make optional binding unavailable");
        legacy = capture.CreateLaunchInputs(); legacy.ChatTemplate = new string('x', 65537);
        var oversizedCapture = OwnerRuntimeLaunchCapture.Capture(legacy, "/synthetic/server");
        var oversizedLaunch = oversizedCapture.SuccessfulStartup(null);
        Check(oversizedLaunch.Binding is null && oversizedCapture.CreateLaunchInputs().ChatTemplate == legacy.ChatTemplate &&
            ModelRuntime.ServerArguments(oversizedCapture.CreateLaunchInputs(), 12345, 256).Contains(legacy.ChatTemplate),
            "legacy oversized template is not truncated or rejected by optional evidence capture");
        legacy = capture.CreateLaunchInputs(); legacy.StopSequences = Enumerable.Repeat("stop", 129).ToList();
        var manyStops = OwnerRuntimeLaunchCapture.Capture(legacy, "/synthetic/server");
        Check(manyStops.SuccessfulStartup(null).Binding is null && manyStops.CreateLaunchInputs().StopSequences.Count == 129,
            "remote stop-count bound affects evidence only");
        Check(oversizedLaunch.ModelId == "snapshot-fixture" && oversizedLaunch.Context.ConfiguredContextTokens == 4096 &&
            oversizedLaunch.Context.MeasuredContextTokens is null && oversizedLaunch.ParallelRequests == 2,
            "unavailable binding preserves independent requested context and instance coordinate");

        var store = new StateStore(Path.Combine(root, "state"));
        store.Put("model", "snapshot-fixture", model);
        var eventHub = new EventHub();
        var subscription = eventHub.Subscribe();
        var settingsCalls = 0;
        var runtime = new ModelRuntime(store, () => { settingsCalls++; throw new Exception("settings must stay unused"); }, eventHub);
        try
        {
            var baseline = FileHashes(root);
            var missing = await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", observation =>
            {
                // Normal isolated ModelRuntime composition plus one real retained SQLite read.
                Check(store.Get<ModelRecord>("model", "snapshot-fixture")!.State == "loaded",
                    "persisted loaded state remains separate from runtime absence");
                return observation;
            }, CancellationToken.None);
            Check(missing.InstanceState == "absent" && missing.InstanceId is null &&
                missing.LaunchBinding is null && missing.LaunchBindingState == "unavailable" &&
                missing.Provider is null && missing.EffectiveContext is null &&
                missing.ConfiguredParallelRequests is null, "missing instance has no invented loaded facts");
            Check(runtime.IsOwnerRuntimeSnapshotCurrent(missing), "missing observation has a valid pure fence");
            var inventory = await runtime.WithOwnerRuntimeSnapshotAsync(null, x => x, CancellationToken.None);
            Check(inventory.ModelId is null && inventory.InstanceState == "unobserved" && inventory.LaunchBinding is null,
                "inventory-only observation is explicitly unobserved");
            Check(!runtime.IsOwnerRuntimeSnapshotCurrent(missing with { ProcessGenerationId = "different" }) &&
                !runtime.IsOwnerRuntimeSnapshotCurrent(missing with { RuntimeEpoch = missing.RuntimeEpoch + 1 }) &&
                !runtime.IsOwnerRuntimeSnapshotCurrent(missing with { InstanceId = "invented" }),
                "generation epoch and instance identity each fence stale observations");
            Check(settingsCalls == 0 && !subscription.Reader.TryRead(out _), "snapshot invokes no settings or events");
            Check(SameHashes(baseline, FileHashes(root)), "isolated absent snapshot preserves every existing state file");

            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var callbackCalled = false;
            await ThrowsAsync<OperationCanceledException>(() => runtime.WithOwnerRuntimeSnapshotAsync("x", x =>
                { callbackCalled = true; return x; }, cancelled.Token), "pre-cancellation prevents callback");
            Check(!callbackCalled, "cancelled acquisition performs no retained read");
            using var midCancel = new CancellationTokenSource();
            await ThrowsAsync<OperationCanceledException>(() => runtime.WithOwnerRuntimeSnapshotAsync("x", x =>
                { midCancel.Cancel(); return x; }, midCancel.Token), "cancellation after callback rejects result");
            await ThrowsAsync<InvalidDataException>(() => runtime.WithOwnerRuntimeSnapshotAsync<int>("x",
                _ => throw new InvalidDataException("fixture callback failure"), CancellationToken.None),
                "callback failure propagates");
            Check((await runtime.WithOwnerRuntimeSnapshotAsync("x", x => x, CancellationToken.None)).InstanceState == "absent",
                "callback failure releases lifecycle");

            var lifecycle = Field<SemaphoreSlim>(runtime, "lifecycle");
            await lifecycle.WaitAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                await ThrowsAsync<OperationCanceledException>(() => runtime.WithOwnerRuntimeSnapshotAsync("x",
                    x => x, deadline.Token), "blocked lifecycle acquisition honors deadline");
            }
            finally { lifecycle.Release(); }

            // Synthetic entries use an already-running test process only for a read of HasExited.
            // They never launch a provider; remove entries before DisposeAsync, and NEVER call
            // ManagedProcess.Stop/Dispose on this wrapper (that would terminate this test process).
            using var currentProcess = Process.GetCurrentProcess();
            var fixture = CreateInstance(currentProcess);
            try
            {
                Invoke(runtime, "InstallOwnerObservedInstance", "snapshot-fixture", fixture);
                var pending = await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x => x, CancellationToken.None);
                Check(pending.InstanceState == "present" && pending.LaunchBinding is null &&
                    pending.EffectiveContext is null, "pre-success instance cannot expose launch observation");
                Invoke(runtime, "PublishOwnerSuccessfulLaunch", fixture, successful);
                var ready = await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x => x, CancellationToken.None);
                Check(ready.LaunchBindingState == "captured" && ready.LaunchBinding!.Path == "/synthetic/model.gguf" &&
                    ready.EffectiveContext!.MeasuredContextTokens == 6144, "successful publication returns retained launch facts");
                Check(!runtime.IsOwnerRuntimeSnapshotCurrent(pending), "successful publication advances observation epoch");
                Invoke(runtime, "PublishOwnerSuccessfulLaunch", fixture, oversizedLaunch);
                var optionalUnavailable = await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x => x, CancellationToken.None);
                Check(optionalUnavailable.InstanceState == "present" && optionalUnavailable.LaunchBinding is null &&
                    optionalUnavailable.LaunchBindingState == "unavailable" && optionalUnavailable.Provider is not null &&
                    optionalUnavailable.EffectiveContext!.ConfiguredContextTokens == 4096,
                    "present legacy runtime exposes unavailable binding without fabricating full evidence");
                Invoke(runtime, "PublishOwnerSuccessfulLaunch", fixture, successful);
                var again = await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x => x, CancellationToken.None);
                Check(!ReferenceEquals(ready.LaunchBinding!.StopSequences, again.LaunchBinding!.StopSequences),
                    "each runtime snapshot owns its binding stop copy");
                var persisted = store.Get<ModelRecord>("model", "snapshot-fixture")!;
                persisted.Path = "/new/persisted/path"; store.Put("model", persisted.Id, persisted);
                var afterMutation = await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x =>
                    (Runtime: x, Persisted: store.Get<ModelRecord>("model", "snapshot-fixture")!), CancellationToken.None);
                Check(afterMutation.Runtime.LaunchBinding!.Path == "/synthetic/model.gguf" &&
                    afterMutation.Persisted.Path == "/new/persisted/path",
                    "persisted update cannot retrospectively change successful launch");
                await ThrowsAsync<OwnerRuntimeSnapshotUnavailableException>(() =>
                    runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x =>
                    {
                        Task.Run(() => Invoke(runtime, "RemoveOwnerObservedInstance", "snapshot-fixture", fixture)).GetAwaiter().GetResult();
                        return x;
                    }, CancellationToken.None), "concurrent removal rejects copied result");
                Check(!runtime.IsOwnerRuntimeSnapshotCurrent(ready), "final pure fence detects removal");
                Invoke(runtime, "InstallOwnerObservedInstance", "snapshot-fixture", fixture);
                var replacement = CreateInstance(currentProcess);
                try
                {
                    Invoke(runtime, "InstallOwnerObservedInstance", "snapshot-fixture", replacement);
                    Invoke(runtime, "RemoveOwnerObservedInstance", "snapshot-fixture", fixture);
                    Check((await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x => x, CancellationToken.None)).InstanceId ==
                        Field<string>(replacement, "Id"), "stale cleanup cannot remove newer exact instance");
                }
                finally
                {
                    Invoke(runtime, "RemoveOwnerObservedInstance", "snapshot-fixture", replacement);
                    CleanupSyntheticInstance(replacement);
                }
            }
            finally
            {
                Invoke(runtime, "RemoveOwnerObservedInstance", "snapshot-fixture", fixture);
                CleanupSyntheticInstance(fixture);
            }

            // Unstarted process fixture exercises StopAsync's finally cleanup after WaitForExit
            // fails. It is unit cleanup evidence, not successful native startup/child-kill evidence.
            using var unstarted = new Process();
            var failedStart = CreateInstance(unstarted);
            var uncertain = runtime.Accounting.Begin(ModelOwnershipKind.LocalContext, "snapshot-fixture", requiresAdmission: false);
            uncertain.Bind(instanceId: Field<string>(failedStart, "Id"));
            uncertain.Phase(ModelOwnershipPhase.Loading, effectStarted: true);
            failedStart.GetType().GetField("Ownership")!.SetValue(failedStart, uncertain);
            Invoke(runtime, "InstallOwnerObservedInstance", "snapshot-fixture", failedStart);
            Invoke(runtime, "PublishOwnerSuccessfulLaunch", failedStart, successful);
            var unreadable = await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x => x, CancellationToken.None);
            Check(unreadable.InstanceState == "unobserved" && unreadable.LaunchBinding is null &&
                unreadable.Provider is null && unreadable.EffectiveContext is null &&
                unreadable.ConfiguredParallelRequests is null,
                "unreadable process cannot expose retained launch facts as current");
            await ThrowsAsync<IOException>(() =>
                (Task)Invoke(runtime, "StopAsync", "snapshot-fixture", failedStart)!,
                "post-start failure fixture executes stop finally cleanup");
            Check(Field<bool>(failedStart, "Stopped") &&
                (await runtime.WithOwnerRuntimeSnapshotAsync("snapshot-fixture", x => x, CancellationToken.None)).InstanceState == "absent",
                "failed startup cleanup removes instance and publishes no launch facts");
            Check(runtime.Accounting.Snapshot().LocalContexts == 1 && runtime.Accounting.Snapshot().UnknownOwnership == 1,
                "missing owned-job proof keeps native ownership uncertain after the visible instance disappears");

            Task? disposing = null;
            await ThrowsAsync<OwnerRuntimeSnapshotUnavailableException>(() => runtime.WithOwnerRuntimeSnapshotAsync("x", x =>
            {
                disposing = Task.Run(async () => await runtime.DisposeAsync());
                Check(SpinWait.SpinUntil(() => Field<bool>(runtime, "ownerRuntimeDisposed"), TimeSpan.FromSeconds(2)),
                    "real concurrent Dispose marks shutdown before waiting lifecycle");
                return x;
            }, CancellationToken.None), "concurrent shutdown rejects copied result");
            await disposing!;
            Check(!runtime.IsOwnerRuntimeSnapshotCurrent(missing), "shutdown invalidates earlier final fence");
            await runtime.DisposeAsync();
            await ThrowsAsync<OwnerRuntimeSnapshotUnavailableException>(() =>
                runtime.WithOwnerRuntimeSnapshotAsync("x", x => x, CancellationToken.None), "disposed runtime fails closed");
            try { Invoke(runtime, "ThrowIfOwnerRuntimeDisposed"); throw new Exception("Guard did not reject shutdown."); }
            catch (TargetInvocationException ex) when (ex.InnerException is ObjectDisposedException)
            { Check(true, "disposed lifecycle admission guard rejects before model or store effects"); }
            var mutationCalled = false;
            await ThrowsAsync<ObjectDisposedException>(() => runtime.WithNoLoadedModelsAsync(() =>
                { mutationCalled = true; return 1; }, CancellationToken.None), "disposed restore callback cannot run");
            Check(!mutationCalled, "shutdown gate prevents callback mutations");
            await ThrowsAsync<ObjectDisposedException>(() => runtime.SetIdleMinutesAsync("snapshot-fixture", 17, CancellationToken.None),
                "disposed idle setter refuses before persistence");
            await ThrowsAsync<ObjectDisposedException>(() => runtime.BeginPauseAsync(CancellationToken.None),
                "disposed pause entry refuses before state mutation");
            await ThrowsAsync<ObjectDisposedException>(() => runtime.ReopenPausedAdmissionAsync(() =>
                { mutationCalled = true; }, CancellationToken.None), "disposed resume callback cannot run");
            Check(!mutationCalled, "shutdown preserves pause commit callback");
            var postShutdownCallback = false;
            await ThrowsAsync<OwnerRuntimeSnapshotUnavailableException>(() =>
                runtime.WithOwnerRuntimeSnapshotAsync("x", x => { postShutdownCallback = true; return x; }, CancellationToken.None),
                "post-shutdown snapshot rejects before retained read");
            Check(!postShutdownCallback, "shutdown snapshot invokes no callback");
            Check(settingsCalls == 0 && !subscription.Reader.TryRead(out _),
                "all pure runtime accessors leave settings and event channels untouched");
        }
        finally { eventHub.Unsubscribe(subscription.Id); await runtime.DisposeAsync(); }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = "cerebral.engine.owner-runtime-snapshot-tests/1", passed = passed.Count, labels = passed,
            evidence = new[] { "unit: launch provenance and synthetic instance transitions",
                "isolated runtime plus real SQLite retained read: missing-instance accessor" },
            modelLoadPrepareGenerateCalls = 0, providerLaunches = 0
        }));
    }

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object?[] arguments) =>
        value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, arguments);
    private static object CreateInstance(Process process)
    {
        var managed = (ManagedProcess)typeof(ManagedProcess).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single().Invoke([process, null, false]);
        var instanceType = typeof(ModelRuntime).GetNestedType("Instance", BindingFlags.NonPublic)!;
        return instanceType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single().Invoke([managed, new HttpClient(), 2, null, null, TimeProvider.System]);
    }
    private static void CleanupSyntheticInstance(object value)
    {
        Field<LlamaSlotPool>(value, "Slots").Complete();
        Field<HttpClient>(value, "Http").Dispose();
    }
    private static SortedDictionary<string, string> FileHashes(string root) => new(
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            x => Path.GetRelativePath(root, x), x => HashFile(x)),
        StringComparer.Ordinal);
    private static string HashFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    private static bool SameHashes(SortedDictionary<string, string> before, SortedDictionary<string, string> after) =>
        before.Count == after.Count && before.All(x => after.TryGetValue(x.Key, out var hash) && hash == x.Value);
}
