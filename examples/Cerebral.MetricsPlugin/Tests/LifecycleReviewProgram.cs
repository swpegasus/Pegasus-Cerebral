using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cerebral.Core;
using Cerebral.MetricsPlugin;
using Cerebral.MetricsPlugin.Service;

namespace Cerebral.MetricsPlugin.Tests;

/// <summary>Focused successor selector. All sources are deterministic fixtures. No WPF,
/// renderer, production host, provider or model is exercised by this executable.</summary>
internal static class LifecycleReviewProgram
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<object> results = [];
    private static string root = "";
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("baseline-red" or "successor-green"))
            throw new ArgumentException("Choose baseline-red or successor-green.");
        root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "evidence", args[0]));
        if (Directory.Exists(root)) throw new InvalidOperationException("Preserve this executed run; choose a new run directory before rerunning.");
        Directory.CreateDirectory(root);
        await Case("dispose-throwing-source", DisposeThrowingSource);
        await Case("pump-finally-dispose-fault", PumpFinallyFault);
        await Case("read-and-dispose-failure-precedence", ReadAndDisposeFault);
        await Case("natural-source-end-does-not-retry", NaturalEnd);
        await Case("opt-out-dispose-fault", OptOutFault);
        await Case("requested-enable-eventual-progress", EventualEnable);
        await Case("opt-out-suppresses-deferred-enable", OptOutRace);
        await Case("failed-opt-out-save-suppresses-deferred-enable", FailedOptOutRace);
        await Case("dispose-suppresses-deferred-enable", DisposeRace);
        await Case("capacity-replay-time-conflict", CapacityReplay);
        var failures = results.Count(item => JsonSerializer.SerializeToElement(item).GetProperty("state").GetString() == "failed");
        var report = new
        {
            schema = "cerebral.metrics-plugin.lifecycle-review/1", selector = args[0],
            recordedUtc = DateTimeOffset.UtcNow, taskId = "10000000-0000-4000-8000-000000000004",
            success = failures == 0, passed = results.Count - failures, failed = failures, checks = results,
            evidenceTier = "deterministic source/lifecycle fixtures plus actual private local-file lock/reopen checks",
            modelCalls = 0, nativeConfiguration = "not exercised", renders = "not rerun",
            executedAssemblies = new[] { typeof(MetricsServicePlugin).Assembly.Location, typeof(MetricObservation).Assembly.Location }
                .Select(path => new { path, bytes = new FileInfo(path).Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) })
        };
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{args[0]}: {results.Count - failures} passed, {failures} failed; no old selector or render replay.");
        return failures == 0 ? 0 : 1;
    }
    private static async Task Case(string name, Func<Task> check)
    {
        try { await check().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception error) { results.Add(new { name, state = "failed", detail = error.GetType().Name + ": " + error.Message }); }
    }
    private static void Check(bool value, string name)
    { results.Add(new { name, state = value ? "passed" : "failed", detail = value ? "verified" : "condition not met" }); }
    private static string Data(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static MetricsServicePlugin Plugin(string path, Func<IMetricsSource> factory)
    {
        var plugin = new MetricsServicePlugin(factory, data => new(data, () => Now));
        plugin.Initialize(new Host(path)); return plugin;
    }
    private static async Task Set(MetricsServicePlugin plugin, long revision, bool enabled)
    { _ = await plugin.ExecuteAsync("metrics-history.settings.set", JsonSerializer.SerializeToElement(new MetricsSettingsChange(revision, new(enabled)), MetricsContract.Json), default); }
    private static async Task<MetricsStatus> Status(MetricsServicePlugin plugin) =>
        ((JsonElement)(await plugin.ExecuteAsync("metrics-history.status", JsonSerializer.SerializeToElement(new { }), default))!).Deserialize<MetricsStatus>(MetricsContract.Json)!;
    private static async Task<bool> Eventually(Func<bool> read, int milliseconds = 2000)
    {
        var end = Environment.TickCount64 + milliseconds;
        do { if (read()) return true; await Task.Delay(10); } while (Environment.TickCount64 < end);
        return read();
    }
    private static bool WriterAvailable(string path)
    { using var probe = new MetricsHistory(path, () => Now); return probe.Coverage().State == "available"; }
    private static Exception? DisposeObserved(MetricsServicePlugin plugin)
    { try { plugin.Dispose(); return null; } catch (Exception error) { return error; } }
    private static async Task DisposeThrowingSource()
    {
        var path = Data("dispose-fault"); var source = new ControlledSource(ignoreCancellation: true, throwDispose: true);
        var plugin = Plugin(path, () => source); await Set(plugin, 0, true); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var error = DisposeObserved(plugin);
        Check(error is null, "Dispose contains ordinary source-detach exceptions");
        Check(!WriterAvailable(path), "history writer remains owned while actual pump has not exited");
        source.Release.TrySetResult(); await source.Exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(await Eventually(() => WriterAvailable(path)), "history writer releases after actual pump exit despite source-dispose fault");
        Check(source.DisposeCalls >= 2, "independent terminal cleanup is attempted after initial detach failure");
    }
    private static async Task PumpFinallyFault()
    {
        var path = Data("pump-fault"); var source = new ControlledSource(ignoreCancellation: true, throwDispose: true);
        var plugin = Plugin(path, () => source); await Set(plugin, 0, true); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Release.TrySetResult(); await source.Exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(80);
        var status = await Status(plugin);
        Check(!status.CaptureActive && status.SourceState != "available", "throwing pump-finally dispose cannot leave capture marked active");
        var error = DisposeObserved(plugin);
        Check(error is null && await Eventually(() => WriterAvailable(path)), "faulted terminal source cannot strand writer during plugin disposal");
    }
    private static async Task OptOutFault()
    {
        var path = Data("opt-out-fault"); var source = new ControlledSource(throwDispose: true);
        var plugin = Plugin(path, () => source); await Set(plugin, 0, true); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Exception? failure = null;
        try { await Set(plugin, 1, false); } catch (Exception error) { failure = error; }
        var status = await Status(plugin);
        Check(failure is null && !status.Settings.Values.CaptureEnabled, "opt-out save completes despite independent source-dispose fault");
        Check(!status.CaptureActive, "opt-out terminal state always clears capture activity");
        DisposeObserved(plugin);
        Check(await Eventually(() => WriterAvailable(path)), "opt-out failure cleanup leaves no permanent history-lock leak");
    }
    private static async Task NaturalEnd()
    {
        var source = new ControlledSource(ignoreCancellation: true); int created = 0;
        var plugin = Plugin(Data("natural-end"), () => { Interlocked.Increment(ref created); return source; });
        await Set(plugin, 0, true); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Release.TrySetResult(); await source.Exited.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(120);
        var status = await Status(plugin);
        Check(created == 1 && !status.CaptureActive && status.SourceReason == "metrics-source-ended", "ordinary source EOF stays unavailable without an automatic restart loop");
        DisposeObserved(plugin);
    }
    private static async Task ReadAndDisposeFault()
    {
        var path = Data("read-and-dispose-fault"); var source = new ControlledSource(ignoreCancellation: true, throwDispose: true, throwRead: true);
        var plugin = Plugin(path, () => source); await Set(plugin, 0, true); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Release.TrySetResult(); await source.Exited.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(120);
        var json = (JsonElement)(await plugin.ExecuteAsync("metrics-history.status", JsonSerializer.SerializeToElement(new { }), default))!;
        Check(json.GetProperty("sourceReason").GetString() == "metrics-source-read-failed" &&
            json.TryGetProperty("sourceCleanupFaults", out var faults) && faults.GetInt64() > 0,
            "read failure remains primary while independent dispose failure is reported separately");
        DisposeObserved(plugin);
        Check(await Eventually(() => WriterAvailable(path)), "combined read and dispose faults still release the actual finished writer");
    }
    private static async Task<(MetricsServicePlugin Plugin, ControlledSource First, ControlledSource Second, Func<int> Count)> Retiring(string name)
    {
        var first = new ControlledSource(ignoreCancellation: true); var second = new ControlledSource(); int created = 0;
        var plugin = Plugin(Data(name), () => Interlocked.Increment(ref created) == 1 ? first : second);
        await Set(plugin, 0, true); await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Set(plugin, 1, false); await Set(plugin, 2, true);
        Check(created == 1 && !(await Status(plugin)).CaptureActive, name + ": no replacement overlaps an unretired pump");
        return (plugin, first, second, () => Volatile.Read(ref created));
    }
    private static async Task EventualEnable()
    {
        var state = await Retiring("eventual-enable");
        state.First.Release.TrySetResult(); await state.First.Exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(await Eventually(() => state.Count() == 2), "still-requested enable starts one replacement after actual retirement");
        Check(await Eventually(() => state.Second.Entered.Task.IsCompleted), "replacement makes actual source-reader progress");
        Check((await Status(state.Plugin)).CaptureActive, "eventual replacement is reflected in capture state");
        await Set(state.Plugin, 3, false); DisposeObserved(state.Plugin);
    }
    private static async Task OptOutRace()
    {
        var state = await Retiring("opt-out-race");
        await Set(state.Plugin, 3, false);
        state.First.Release.TrySetResult(); await state.First.Exited.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(120);
        var status = await Status(state.Plugin);
        Check(state.Count() == 1 && !status.Settings.Values.CaptureEnabled && !status.CaptureActive, "later opt-out suppresses deferred replacement");
        DisposeObserved(state.Plugin);
    }
    private static async Task DisposeRace()
    {
        var path = Path.Combine(root, "dispose-race"); var state = await Retiring("dispose-race");
        var error = DisposeObserved(state.Plugin);
        Check(error is null && !WriterAvailable(path), "plugin disposal retains writer until the old pump exits");
        state.First.Release.TrySetResult(); await state.First.Exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(await Eventually(() => WriterAvailable(path)), "deferred disposal releases history after actual retirement");
        await Task.Delay(120);
        Check(state.Count() == 1 && !state.Second.Entered.Task.IsCompleted, "disposal permanently suppresses deferred source replacement");
    }
    private static async Task FailedOptOutRace()
    {
        var state = await Retiring("failed-opt-out-save");
        Exception? failure = null;
        try { await Set(state.Plugin, 2, false); } catch (InvalidOperationException error) { failure = error; }
        Check(failure is not null && (await Status(state.Plugin)).Settings.Values.CaptureEnabled, "failed stale opt-out save preserves the earlier enabled preference");
        state.First.Release.TrySetResult(); await state.First.Exited.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(120);
        Check(state.Count() == 1 && !(await Status(state.Plugin)).CaptureActive, "live opt-out intent suppresses replacement even when persisted preference is still enabled");
        DisposeObserved(state.Plugin);
    }
    private static Task CapacityReplay()
    {
        var path = Data("capacity-time-conflict"); var original = Capacity();
        using (var history = new MetricsHistory(path, () => Now))
        {
            Check(history.Append(original), "capacity baseline sample appended");
            Check(!history.Append(original with { Revision = 2, ObservedAt = Now }), "append refuses time change for immutable capacity sample");
        }
        var file = Path.Combine(path, "history.jsonl");
        File.AppendAllText(file, JsonSerializer.Serialize(original with { Revision = 2, ObservedAt = Now }, MetricsContract.Json) + "\n", new UTF8Encoding(false));
        var before = SHA256.HashData(File.ReadAllBytes(file));
        using (var history = new MetricsHistory(path, () => Now))
        {
            var snapshot = history.Query(new(Now.AddDays(-1), Now.AddSeconds(1)), default);
            Check(history.Coverage().State == "read-only" && history.Coverage().Reason == "history-conflict", "replay applies immutable capacity sample-time guard");
            Check(snapshot.Capacity.Single().ObservedAt == original.ObservedAt, "replay preserves the earlier accepted capacity sample");
            Check(!history.Append(original with { EntityId = "new-sample" }), "conflicting replay becomes read-only without appending over evidence");
        }
        Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "conflicting capacity history bytes preserved exactly");
        return Task.CompletedTask;
    }
    private static MetricObservation Capacity() => new()
    {
        InstallationId = "fixture-owner", SourceGeneration = "fixture-source1", EntityId = "fixture-sample", Revision = 1,
        ObservedAt = Now.AddSeconds(-1), Kind = MetricKind.Capacity, State = MetricState.Available,
        Origin = MetricOrigin.Local, Basis = MetricBasis.HostSnapshot, WorkerId = "local", ModelId = "fixture-model", AllocatedSlots = 5
    };
    private sealed class Host(string path) : IServicePluginHost
    {
        public string PluginId => MetricsContract.PluginId;
        public string PackageDirectory => path;
        public string DataDirectory => path;
        public void Publish(string type, string? jobId, object? data) { }
    }
    private sealed class ControlledSource(bool ignoreCancellation = false, bool throwDispose = false, bool throwRead = false) : IMetricsSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposeCalls;
        public int DisposeCalls => Volatile.Read(ref disposeCalls);
        public string State => "available";
        public string Reason => "fixture-only";
        public long DroppedRecords => 0;
        public async IAsyncEnumerable<MetricObservation> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            Entered.TrySetResult();
            try
            {
                if (ignoreCancellation) await Release.Task; else await Release.Task.WaitAsync(token);
                if (throwRead) throw new IOException("fixture-source-read-fault");
            }
            finally { Exited.TrySetResult(); }
            yield break;
        }
        public void Dispose()
        {
            Interlocked.Increment(ref disposeCalls);
            if (throwDispose) throw new IOException("fixture-source-dispose-fault");
        }
    }
}
