using System.Collections.Concurrent;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.EngineDiagnosticsPlugin;
using Cerebral.Worker;

if (args.Length == 2 && args[0] == "--lifetime-only")
{
    await Cerebral.EngineDiagnosticsPlugin.Tests.LifetimeProbe.RunAsync(args[1]); return;
}
if (args.Length != 1) throw new ArgumentException("Pass the task-owned private fixture directory.");
var allowed = Path.GetFullPath(args[0]);
if (!allowed.StartsWith(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp")) + Path.DirectorySeparatorChar,
    StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Fixtures must stay under this workspace's .tmp.");
var root = Path.Combine(allowed, "fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
    checks++; Console.WriteLine("PASS: " + label);
}
EngineDiagnosticRecord Record(string chat, DateTimeOffset at, string? text = "visible answer") =>
    new(1, at, chat, "request-fixture", "run-fixture", "effect-fixture", "agent-fixture", "worker-fixture",
        "model-fixture", "model.visible-fragment", text, InputTokens: 42, ResponseTokens: 256,
        ReasoningTokens: 0, ContextTokens: 4096);
JsonElement Status(object? value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while (true)
    {
        try { if (condition()) return; } catch (Exception error) when (error is IOException or JsonException) { }
        await Task.Delay(10, timeout.Token);
    }
}
JsonElement[] Lines(string path) => File.Exists(path) ? File.ReadAllLines(path).Where(x => x.Length != 0)
    .Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToArray() : [];
try
{
    var now = new DateTimeOffset(2026, 10, 11, 3, 0, 0, TimeSpan.Zero);
    var clock = now;
    var log = new RollingEngineLog(Path.Combine(root, "rolling"), 8192, 8, () => clock);
    log.Initialize();
    Check(log.Append(Record("chat-a", now, "café\nsecond line"), 3) &&
          log.Append(Record("chat-b", now, "Bearer fixture-token sk-fixture-not-a-real-credential"), 4), "two chat identities use one valid Unicode JSONL log");
    var lines = Lines(log.PathName);
    Check(lines.Length == 2 && lines.Select(x => x.GetProperty("chatId").GetString()).SequenceEqual(["chat-a", "chat-b"]) &&
          lines[0].GetProperty("text").GetString() == "café\nsecond line" &&
          lines[0].GetProperty("queueDroppedRecords").GetInt64() == 3, "chat IDs, multiline text and queue-loss accounting survive round trip");
    Check(lines[1].GetProperty("text").GetString() == "<redacted> <redacted>", "known credential-shaped output is redacted locally");
    Check(!log.Append(Record("", now), 0) && !log.Append(Record("chat-a", now.AddHours(-24)), 0) &&
          !log.Append(Record("chat-a", now.AddMinutes(6)), 0), "missing identity, expired and future records are rejected");
    Check(log.Append(Record("chat-a", now, new string('x', 9000)), 0) &&
          Lines(log.PathName).Last().GetProperty("textTruncated").GetBoolean() &&
          Lines(log.PathName).Last().GetProperty("text").GetString()!.Length == 2048, "large visible fragments are bounded and explicitly marked");
    var appended = true;
    for (var i = 0; i < 50; i++) appended &= log.Append(Record(i % 2 == 0 ? "chat-a" : "chat-b", now, "item-" + i + new string('x', 160)), 0);
    Check(appended, "sustained appends succeed while quotas rotate older records");
    lines = Lines(log.PathName);
    Check(new FileInfo(log.PathName).Length <= 8192 && lines.Length <= 8 && lines.Last().GetProperty("text").GetString()!.StartsWith("item-49"),
        "byte and entry quotas retain recent records without unbounded rotation files");
    var ordered = new RollingEngineLog(Path.Combine(root, "out-of-order"), clock: () => clock);
    ordered.Initialize(); ordered.Append(Record("new", now), 0); ordered.Append(Record("old", now.AddHours(-23)), 0);
    clock = now.AddHours(2); ordered.Maintain();
    Check(Lines(ordered.PathName).Select(x => x.GetProperty("chatId").GetString()).SequenceEqual(["new"]),
        "maintenance expires older out-of-order records while preserving newer output");
    clock = now.AddHours(26); log.Maintain();
    Check(Lines(log.PathName).Length == 0, "idle retention prunes expired output without requiring another model request");
    var damagedRoot = Path.Combine(root, "damaged"); Directory.CreateDirectory(damagedRoot);
    var damagedPath = Path.Combine(damagedRoot, "engine-debug.jsonl");
    File.WriteAllText(damagedPath, new string('x', 5000));
    var damaged = new RollingEngineLog(damagedRoot, 4096);
    var rejected = false; try { damaged.Initialize(); } catch (IOException) { rejected = true; }
    Check(rejected && new FileInfo(damagedPath).Length == 5000, "external oversized files are reported without silent deletion");
    File.Delete(damagedPath); damaged.Initialize(); Directory.CreateDirectory(damagedPath);
    Check(!damaged.Append(Record("chat-a", DateTimeOffset.UtcNow), 0) && damaged.Fault == "log-storage-failed",
        "storage failure is isolated into diagnostic status");
    Directory.Delete(damagedPath); damaged.Initialize();
    Check(damaged.Append(Record("chat-a", DateTimeOffset.UtcNow), 0), "explicit reinitialization recovers after storage repair");

    var events = new EventHub();
    using (var lease = events.SubscribeDiagnostics())
    {
        var normal = events.Subscribe();
        for (var i = 0; i < 500; i++) events.PublishDiagnostic(Record("chat-" + i % 2, DateTimeOffset.UtcNow));
        events.Publish("fixture.normal", null, new { okay = true });
        Check(lease.DroppedRecords == 372 && normal.Reader.TryRead(out var ordinary) && ordinary.Type == "fixture.normal",
            "a slow observer drops records within its 128-record queue and cannot block normal events");
        lease.Dispose();
        var seen = new List<EngineDiagnosticRecord>();
        await foreach (var item in lease.ReadAsync()) seen.Add(item);
        Check(seen.Count == 128 && seen.All(x => x.Sequence > 0 && x.ChatId.StartsWith("chat-")), "bounded queue retains original chat identities");
        var secondReaderRejected = false;
        try { await foreach (var item in lease.ReadAsync()) { } } catch (InvalidOperationException) { secondReaderRejected = true; }
        Check(secondReaderRejected && !events.HasDiagnosticSubscribers, "a subscription has one reader and detaches idempotently");
        events.Unsubscribe(normal.Id);
    }
    var subscriptions = new ConcurrentBag<IEngineDiagnosticSubscription>();
    await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
    {
        try { subscriptions.Add(events.SubscribeDiagnostics()); } catch (InvalidOperationException) { }
    })));
    Check(subscriptions.Count == 16, "concurrent subscription admission enforces the global 16-observer limit");
    foreach (var lease in subscriptions) lease.Dispose();
    Check(!events.HasDiagnosticSubscribers, "all admitted subscriptions detach");

    clock = DateTimeOffset.UtcNow;
    var captureTime = clock;
    var pluginRoot = Path.Combine(root, "plugin");
    var host = new FixtureHost(pluginRoot, events);
    using (var plugin = new EngineDiagnosticsPlugin
    {
        MaintenanceInterval = TimeSpan.FromMilliseconds(25),
        LogFactory = data => new RollingEngineLog(data, clock: () => clock)
    })
    {
        plugin.Initialize(host);
        var initial = Status(await plugin.ExecuteAsync("engine-debug.status", CerebralJson.Element(new { }), default));
        Check(initial.GetProperty("state").GetString() == "inactive" && !events.HasDiagnosticSubscribers && plugin.ToolExtensions.Count == 0,
            "capture defaults off and contributes no model tools or execution handles");
        var active = Status(await plugin.ExecuteAsync("engine-debug.set", CerebralJson.Element(new { enabled = true }), default));
        Check(active.GetProperty("state").GetString() == "active" && events.HasDiagnosticSubscribers, "owner command explicitly enables capture");
        events.PublishDiagnostic(Record("chat-a", now, "one")); events.PublishDiagnostic(Record("chat-b", now, "two"));
        var pluginPath = Path.Combine(pluginRoot, "engine-debug.jsonl");
        await Until(() => Lines(pluginPath).Length == 2);
        Check(Lines(pluginPath).Select(x => x.GetProperty("chatId").GetString()).SequenceEqual(["chat-a", "chat-b"]),
            "real EventHub-to-plugin capture merges both chats into one log");
        await plugin.ExecuteAsync("engine-debug.set", CerebralJson.Element(new { enabled = false }), default);
        Check(!events.HasDiagnosticSubscribers, "disabling detaches immediately");
        clock = captureTime.AddHours(25);
        await Until(() => Lines(pluginPath).Length == 0);
        Check(Lines(pluginPath).Length == 0, "loaded but disabled plugin still maintains the rolling 24-hour retention");
        clock = DateTimeOffset.UtcNow;
        await plugin.ExecuteAsync("engine-debug.set", CerebralJson.Element(new { enabled = true }), default);
        var settings = Path.Combine(pluginRoot, "engine-debug-settings.json");
        File.Delete(settings); Directory.CreateDirectory(settings);
        var failure = Status(await plugin.ExecuteAsync("engine-debug.set", CerebralJson.Element(new { enabled = false }), default));
        Check(failure.GetProperty("state").GetString() == "failed" && !events.HasDiagnosticSubscribers,
            "capture still stops when persisting the disabled preference fails");
        Directory.Delete(settings);
        await plugin.ExecuteAsync("engine-debug.set", CerebralJson.Element(new { enabled = true }), default);
    }
    Check(!events.HasDiagnosticSubscribers, "plugin disposal detaches capture");
    using (var restarted = new EngineDiagnosticsPlugin())
    {
        restarted.Initialize(host);
        Check(events.HasDiagnosticSubscribers, "explicit enabled preference survives plugin reload");
        await restarted.ExecuteAsync("engine-debug.set", CerebralJson.Element(new { enabled = false }), default);
    }

    var packages = Path.Combine(root, "packages");
    var package = Path.Combine(packages, EngineDiagnosticsPlugin.Id); Directory.CreateDirectory(package);
    File.Copy(typeof(EngineDiagnosticsPlugin).Assembly.Location, Path.Combine(package, "Cerebral.EngineDiagnosticsPlugin.dll"));
    File.WriteAllText(Path.Combine(package, "service-plugin.json"), JsonSerializer.Serialize(new ServicePluginManifest
    {
        Id = EngineDiagnosticsPlugin.Id, Name = "Engine debug monitor", Version = "1.0.0",
        Assembly = "Cerebral.EngineDiagnosticsPlugin.dll", EntryType = typeof(EngineDiagnosticsPlugin).FullName!
    }, CerebralJson.Options));
    var runtimeData = Path.Combine(root, "runtime-data");
    using (var runtime = new ServicePluginRuntime(packages, runtimeData, events))
    {
        Check(runtime.Status.Single().Loaded, "real service package loader admits the removable monitor");
        var command = runtime.CommandFor("engine-debug.set") ?? throw new Exception("Diagnostic owner command was not admitted.");
        Check(Status(await command.ExecuteAsync("engine-debug.set", CerebralJson.Element(new { enabled = true }), default))
            .GetProperty("state").GetString() == "active", "actual admitted package is enabled through its owner command");
        events.PublishDiagnostic(Record("runtime-chat-a", DateTimeOffset.UtcNow));
        events.PublishDiagnostic(Record("runtime-chat-b", DateTimeOffset.UtcNow));
        var logPath = Path.Combine(runtimeData, EngineDiagnosticsPlugin.Id, "engine-debug.jsonl");
        await Until(() => Lines(logPath).Length == 2);
        Check(Lines(logPath).Select(x => x.GetProperty("chatId").GetString()).SequenceEqual(["runtime-chat-a", "runtime-chat-b"]),
            "actual loaded assembly writes one shared chat-identified stream");
    }
    Check(!events.HasDiagnosticSubscribers, "service runtime detaches the plugin stream before unloading");
    var leakyPackage = Path.Combine(packages, "fixture.leaky"); Directory.CreateDirectory(leakyPackage);
    var fixtureDll = Path.GetFileName(typeof(LeakyDiagnosticFixture).Assembly.Location);
    File.Copy(typeof(LeakyDiagnosticFixture).Assembly.Location, Path.Combine(leakyPackage, fixtureDll));
    File.WriteAllText(Path.Combine(leakyPackage, "service-plugin.json"), JsonSerializer.Serialize(new ServicePluginManifest
    {
        Id = "fixture.leaky", Name = "Leaky subscription fixture", Version = "1.0.0",
        Assembly = fixtureDll, EntryType = typeof(LeakyDiagnosticFixture).FullName!
    }, CerebralJson.Options));
    using (var runtime = new ServicePluginRuntime(packages, runtimeData, events))
        Check(runtime.Status.All(x => x.Loaded) && events.HasDiagnosticSubscribers, "native fixture deliberately forgets its diagnostic lease");
    Check(!events.HasDiagnosticSubscribers, "host independently closes a lease even when a package neglects cleanup");
    Console.WriteLine($"Engine diagnostics: {checks} checks passed; real local file I/O/EventHub/native package loading; no inference, owner chat or installed deployment.");
    Console.WriteLine("Private fixture log: " + Path.Combine(runtimeData, EngineDiagnosticsPlugin.Id, "engine-debug.jsonl"));
}
catch { Console.WriteLine("Retained private failure fixture: " + root); throw; }

sealed class FixtureHost(string data, EventHub events) : IServicePluginHost, IServiceEngineDiagnosticsHost
{
    public string PluginId => EngineDiagnosticsPlugin.Id;
    public string PackageDirectory => data;
    public string DataDirectory => data;
    public void Publish(string eventType, string? jobId, object? body) => events.Publish(eventType, jobId, body);
    public IEngineDiagnosticSubscription OpenEngineDiagnostics() => events.SubscribeDiagnostics();
}
public sealed class LeakyDiagnosticFixture : IServicePlugin
{
    private IEngineDiagnosticSubscription? lease;
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public void Initialize(IServicePluginHost host) => lease = ((IServiceEngineDiagnosticsHost)host).OpenEngineDiagnostics();
    public void Dispose() { } // Deliberate fixture defect: the host must detach independently.
}
