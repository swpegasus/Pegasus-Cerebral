using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cerebral.BrowserPlugin;
using Cerebral.Core;

// Keep this local fake-transport lane ahead of scratch setup and all browser/network paths.
if (args.Length == 1 && args[0] == "--managed-internet-only")
{
    var managedChecks = await Cerebral.BrowserPlugin.Tests.BrowserManagedInternetChecks.RunAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        suite = "browser-managed-internet",
        evidence = "deterministic fake transport/interface; no network, browser renderer or installed acceptance",
        checks = managedChecks
    }));
    return 0;
}

var checks = 0;
void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; }
void Reject(Action action, string name)
{
    try { action(); throw new InvalidOperationException("Expected rejection: " + name); }
    catch (Exception ex) when (ex is ArgumentException or BrowserRejectedException or JsonException) { checks++; }
}
JsonElement E(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
JsonElement Data(ToolResult result) => JsonDocument.Parse(result.Content).RootElement.GetProperty("data").Clone();
if (args.Length == 1 && args[0] == "--workflow-portable-settings-only")
{
    BrowserWorkflowPortableSettingsChecks.Run();
    return 0;
}
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".scratch", "browser-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    if (args.Length == 1 && args[0] is "--public-page-only" or "--public-rendered-only" or "--public-dispose-only")
    {
        await BrowserPublicPageChecks.RunAsync(root, includeSource: args[0] == "--public-page-only", disposeOpenTab: args[0] == "--public-dispose-only");
        return 0;
    }
    var policy = new BrowserPolicy { AllowedDomains = ["localhost", "127.0.0.1"] };
    policy.Validate(); Check(policy.Allows("http://localhost/example"), "explicit domain admitted");
    Check(!policy.Allows("http://localhost.evil.test/") && !policy.Allows("file:///C:/secret") && !policy.Allows("http://user:pass@localhost/"), "domain boundary and schemes");
    Reject(() => (policy with { MaximumJobs = 5 }).Validate(), "hard job bound");
    Reject(() => (policy with { MinimumActionDelayMilliseconds = 0 }).Validate(), "nonzero pacing");
    Reject(() => (policy with { AllowedDomains = ["*.example.com"] }).Validate(), "wildcard policy rejected");
    Reject(() => (policy with { Downloads = "allow" }).Validate(), "no silent gate bypass");
    Reject(() => (policy with { QueueDepth = 17 }).Validate(), "hard queue bound");
    Reject(() => (policy with { MaximumActionsPerSession = 2 }).Validate(), "serial provider does not pretend action parallelism");
    var profile = new AgentProfile { Id = "browser-test", Capabilities = ["browser.read", "browser.navigate", "browser.interact"] };
    profile.PluginSettings[BrowserServicePlugin.Id] = E(policy);
    Check(BrowserPolicy.For(profile).MaximumTabs == 6, "profile settings round trip");
    BrowserWorkflowPortableSettingsChecks.Run();
    profile.PluginSettings[BrowserServicePlugin.Id] = JsonDocument.Parse("{\"maximumJobs\":1,\"maximumJobs\":2}").RootElement.Clone();
    Reject(() => BrowserPolicy.For(profile), "duplicate settings fail closed");
    profile.PluginSettings[BrowserServicePlugin.Id] = E(policy);
    var resource = new BrowserResources(root);
    Check(resource.Read().Revision == 0 && resource.Read().Active is null, "empty resource default");
    Reject(() => BrowserResources.ConfinedFile(root, "../elsewhere.exe"), "resource confinement");
    var generation = Path.Combine(resource.Root, "fixture"); Directory.CreateDirectory(generation);
    File.WriteAllText(Path.Combine(generation, "browser.exe"), "fixture-not-executable");
    File.WriteAllText(Path.Combine(generation, "LICENSE"), "fixture"); File.WriteAllText(Path.Combine(generation, "NOTICES"), "fixture");
    var binding = new BrowserResource("fixture", "chromium", "fixture-only", "win-x64", "1.63.0", "https://example.org/fixture", "fixture", "browser.exe", "LICENSE", "NOTICES");
    Check(resource.Activate(0, binding).Revision == 1, "local resource selection atomic revision");
    Check(new BrowserResources(root).Read().Active == binding, "resource restart persistence");
    Reject(() => resource.Activate(0, binding), "stale resource revision");
    Reject(() => resource.Validate(binding with { Distribution = "edge-test-only" }), "cannot mispackage proprietary browser");
    using (var missing = new BrowserServicePlugin(null, () => null))
    {
        missing.Initialize(new Host(Path.Combine(root, "empty")));
        var result = await missing.ExecuteAsync(new ToolCall("browser.open", E(new { url = "http://localhost/" })), new(profile, "chat", "job"), CancellationToken.None);
        Check(!result.Success && result.Content.Contains("runtime-missing"), "no automatic browser download");
        profile.Capabilities = [];
        result = await missing.ExecuteAsync(new ToolCall("browser.open", E(new { url = "http://localhost/" })), new(profile, "chat", "job", FullAccess: true), CancellationToken.None);
        Check(!result.Success && result.Content.Contains("capability-denied"), "full access does not bypass browser profile grant");
        profile.Capabilities = ["browser.read", "browser.navigate", "browser.interact"];
    }
    Console.WriteLine(JsonSerializer.Serialize(new { suite = "browser-policy", evidence = "deterministic fixtures; fake resource is never executed", checks }));
    await BrowserPageRequestChecks.RunAsync(root);
    if (args.Length == 2 && args[0] == "--existing-browser")
    {
        if (!File.Exists(args[1])) throw new FileNotFoundException("Explicit existing browser unavailable.");
        Check(string.Equals(BrowserSystemRuntime.FindEdgeExecutable(), Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase),
            "production system Edge discovery resolves the accepted browser");
        await using var site = new LocalSite();
        using var plugin = new BrowserServicePlugin(); plugin.Initialize(new Host(Path.Combine(root, "live")));
        policy = policy with { MaximumTabsPerJob = 1, MaximumTabs = 2, NavigationTimeoutSeconds = 5, AllowedDomains = ["127.0.0.1"] };
        profile.PluginSettings[BrowserServicePlugin.Id] = E(policy);
        var context = new ToolExecutionContext(profile, "chat", "job");
        Task<ToolResult> Call(string name, object value, ToolExecutionContext? selected = null) => plugin.ExecuteAsync(new(name, E(value)), selected ?? context, CancellationToken.None);
        var opened = await Call("browser.open", new { url = site.Url });
        Check(opened.Success && opened.Content.Contains("system-edge"), "production system Edge path opens isolated localhost: " + opened.Content);
        var tabId = Data(opened).GetProperty("tabId").GetString()!;
        var read = await Call("browser.inspect", new { tabId });
        Check(read.Success && read.Content.Contains("Browser fixture"), "real DOM projection");
        Check(!read.Content.Contains("never-return-password") && !read.Content.Contains("private-field-value"), "password and input values not projected");
        var projection = Data(read); var snapshotId = projection.GetProperty("snapshotId").GetString();
        var controls = projection.GetProperty("controls").EnumerateArray().ToArray();
        var field = controls.Single(row => row.GetProperty("description").GetProperty("label").GetString() == "Name").GetProperty("id").GetInt32();
        Check((await Call("browser.fill", new { tabId, snapshotId, elementId = field, text = "Cerebral" })).Success, "real fill");
        var stale = await Call("browser.fill", new { tabId, snapshotId, elementId = field, text = "stale" });
        Check(!stale.Success && stale.Content.Contains("stale-snapshot"), "stale snapshot rejected");
        read = await Call("browser.inspect", new { tabId }); projection = Data(read); snapshotId = projection.GetProperty("snapshotId").GetString();
        var button = projection.GetProperty("controls").EnumerateArray().Single(row => row.GetProperty("description").GetProperty("label").GetString() == "Greet").GetProperty("id").GetInt32();
        Check((await Call("browser.click", new { tabId, snapshotId, elementId = button })).Success, "real click");
        read = await Call("browser.inspect", new { tabId }); Check(read.Content.Contains("Hello Cerebral"), "real click changed DOM");
        var denied = await Call("browser.inspect", new { tabId }, context with { JobId = "other-job" });
        Check(!denied.Success && denied.Content.Contains("tab-not-owned"), "cross-job tab rejected");
        denied = await Call("browser.open", new { url = site.Url }); Check(!denied.Success && denied.Content.Contains("tab-limit"), "real active tab cap");
        denied = await Call("browser.navigate", new { tabId, url = "https://example.org/" }); Check(!denied.Success && denied.Content.Contains("outside-domain"), "outside domain rejected before navigation");
        var capture = await Call("browser.screenshot", new { tabId }); Check(capture.Success, "real bounded viewport capture");
        var captureId = Data(capture).GetProperty("captureId").GetString();
        Check(new FileInfo(Path.Combine(root, "live/captures", captureId + ".png")).Length > 100, "capture persisted in exact fixture storage");
        var replayContext = context with { EffectId = "exact-effect" };
        var first = await Call("browser.inspect", new { tabId }, replayContext);
        var replay = await Call("browser.inspect", new { tabId }, replayContext);
        Check(first.Content == replay.Content, "same effect returns existing receipt without new inspection");
        denied = await Call("browser.close", new { tabId }, replayContext); Check(!denied.Success && denied.Content.Contains("receipt-conflict"), "effect substitution rejected");
        Check((await Call("browser.close", new { tabId })).Success, "owned browser cleanup");
        var redirect = await Call("browser.open", new { url = site.Url + "redirect" });
        Check(!redirect.Success && site.ForbiddenRequests == 0, "cross-domain redirect blocked before its target receives a request");
        var download = await Call("browser.open", new { url = site.Url + "download" });
        Check(!download.Success && download.Content.Contains("download-confirmation-required"), "attachment download blocked before body delivery");
        var auth = await Call("browser.open", new { url = site.Url + "auth" });
        Check(!auth.Success && auth.Content.Contains("authentication-confirmation-required"), "HTTP authentication denied without credentials");
        profile.PluginSettings[BrowserServicePlugin.Id] = E(policy with { QueueDepth = 0 });
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var running = plugin.ExecuteAsync(new ToolCall("browser.open", E(new { url = site.Url + "slow" })), context, cancel.Token);
            await site.SlowEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var full = await Call("browser.open", new { url = site.Url });
            Check(!full.Success && full.Content.Contains("action-queue-full"), "busy execution does not admit an unbounded queue");
            cancel.Cancel();
            var cancelled = await running.WaitAsync(TimeSpan.FromSeconds(8));
            Check(!cancelled.Success && cancelled.EffectiveOutcome == ToolExecutionOutcome.Unknown, "cancelled browser action closes owned session without claiming site rollback");
        }
        var metrics = E(await ((IServiceCommandExtension)plugin).ExecuteAsync("browser.metrics", E(new { }), CancellationToken.None) ?? new { });
        Check(metrics.GetProperty("current").GetProperty("jobs").GetInt32() == 0 && metrics.GetProperty("current").GetProperty("tabs").GetInt32() == 0, "no active browser jobs after close");
        Console.WriteLine(JsonSerializer.Serialize(new { suite = "browser-runtime", evidence = "production system Edge discovery against owned localhost fixture; isolated temporary profile and no bundled browser claim", checks, browser = Path.GetFileName(args[1]) }));
    }
    return 0;
}
finally
{
    var scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".scratch"));
    if (root.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("browser-", StringComparison.Ordinal))
        Directory.Delete(root, true);
}

sealed class Host(string data) : IServicePluginHost
{
    public string PluginId => BrowserServicePlugin.Id;
    public string PackageDirectory => AppContext.BaseDirectory;
    public string DataDirectory => data;
    public void Publish(string eventType, string? jobId, object? value) { }
}

sealed class LocalSite : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly List<Task> connections = [];
    public string Url { get; }
    public int ForbiddenRequests { get; private set; }
    public TaskCompletionSource SlowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public LocalSite()
    {
        listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"; loop = ServeAsync();
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                connections.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        await Task.WhenAll(connections);
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
                try
                {
                await using var stream = client.GetStream();
                var buffer = new byte[8192]; var length = await stream.ReadAsync(buffer, stop.Token);
                var request = Encoding.ASCII.GetString(buffer, 0, length);
                if (request.StartsWith("GET /forbidden ", StringComparison.Ordinal)) ForbiddenRequests++;
                if (request.StartsWith("GET /slow ", StringComparison.Ordinal))
                { SlowEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, stop.Token); }
                if (request.StartsWith("GET /redirect ", StringComparison.Ordinal))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                    return;
                }
                if (request.StartsWith("GET /download ", StringComparison.Ordinal) || request.StartsWith("GET /auth ", StringComparison.Ordinal))
                {
                    var status = request.StartsWith("GET /auth ", StringComparison.Ordinal)
                        ? "401 Unauthorized\r\nWWW-Authenticate: Basic realm=fixture" : "200 OK\r\nContent-Disposition: attachment; filename=test.txt";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                    return;
                }
                var body = Encoding.UTF8.GetBytes("""
                    <!doctype html><html><head><title>Browser fixture</title></head><body>
                    <h1>Browser fixture</h1><label for="name">Name</label><input id="name" value="private-field-value">
                    <input type="password" value="never-return-password"><button onclick="document.getElementById('result').textContent='Hello '+document.getElementById('name').value">Greet</button>
                    <p id="result">Ready</p></body></html>
                    """);
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(body, stop.Token);
                }
                // Browser navigation/cancellation can abandon a favicon or response. It is an expected peer close.
                catch (IOException) { }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; stop.Dispose(); }
}
