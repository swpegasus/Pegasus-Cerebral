using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cerebral.BrowserPlugin;
using Cerebral.Core;

internal static class BrowserPageRequestChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        JsonElement E(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        JsonElement Data(ToolResult result) => JsonDocument.Parse(result.Content).RootElement.GetProperty("data").Clone();
        await using var site = new PageSite();
        var policy = new BrowserPolicy { AllowedDomains = ["127.0.0.1"], NavigationTimeoutSeconds = 2,
            ActionTimeoutSeconds = 1, MinimumActionDelayMilliseconds = 100 };
        var profile = new AgentProfile { Id = "page-fixture", Capabilities = ["browser.read"] };
        profile.PluginSettings[BrowserServicePlugin.Id] = E(policy);
        var context = new ToolExecutionContext(profile, "owned-chat", "owned-job");
        var effectContext = context with { EffectId = "host-effect" };
        using var plugin = new BrowserServicePlugin(null, () => null);
        plugin.Initialize(new PageHost(Path.Combine(root, "page-requests")));
        object Args(string id, string path, int maximumBytes = 65536, int maximumTextBytes = 12000) =>
            new { requestId = id, url = site.Url + path, maximumBytes, maximumTextBytes };
        Task<ToolResult> Call(object input, ToolExecutionContext? selected = null, CancellationToken token = default) =>
            plugin.ExecuteAsync(new("browser.request", E(input)), selected ?? context, token);

        var input = Args("read-once", "ok?token=fixture-marker#fragment-marker");
        var read = await Call(input, effectContext);
        Check(read.Success && read.EffectiveOutcome == ToolExecutionOutcome.Succeeded, "HTTP source succeeds without any browser runtime");
        var data = Data(read);
        Check(data.GetProperty("requestId").GetString() == "read-once" && data.GetProperty("requestSessionId").GetString()?.Length == 32,
            "request and provider session correlation returned");
        Check(data.GetProperty("tabId").ValueKind == JsonValueKind.Null && !data.GetProperty("javascriptExecuted").GetBoolean() &&
            data.GetProperty("representation").GetString() == "http-source", "HTTP source never claims a rendered tab or JavaScript execution");
        Check(data.GetProperty("text").GetString() == "Browser HTTP source", "bounded source text returned");
        Check(!read.Content.Contains("fixture-marker") && !read.Content.Contains("fragment-marker"), "query and fragment omitted from receipt output");
        var count = site.Count("/ok");
        var replay = await Call(input, effectContext);
        Check(replay.Content == read.Content && site.Count("/ok") == count, "same request ID returns exact receipt without another GET");
        var conflict = await Call(Args("read-once", "cookie"), effectContext);
        var effectConflict = await Call(Args("different-request", "cookie"), effectContext);
        Check(!effectConflict.Success && site.Count("/cookie") == 0, "host effect identity cannot be reassigned through another request ID");
        Check(!conflict.Success && conflict.Content.Contains("receipt-conflict") && site.Count("/cookie") == 0, "request substitution rejected");
        var isolated = await Call(input, context with { SessionId = "other-chat", EffectId = "other-host-effect" });
        Check(isolated.Success && Data(isolated).GetProperty("requestSessionId").GetString() != data.GetProperty("requestSessionId").GetString() &&
            site.Count("/ok") == count + 1, "same request ID does not cross conversation ownership");
        profile.Capabilities = [];
        var denied = await Call(input);
        Check(!denied.Success && denied.Content.Contains("capability-denied"), "cached source cannot bypass a revoked capability");
        profile.Capabilities = ["browser.read"];
        denied = await Call(new { requestId = "wrong-tab", url = site.Url, tabId = Guid.NewGuid().ToString("N") });
        Check(!denied.Success && denied.Content.Contains("tab-not-owned"), "unowned tab correlation denied before GET");
        var total = site.Total;
        foreach (var invalid in new object[]
        {
            new { requestId = "header", url = site.Url, headers = new { Cookie = "fixture" } },
            new { requestId = "post", url = site.Url, method = "POST" },
            new { requestId = "body", url = site.Url, body = "fixture" },
            new { requestId = "too-small", url = site.Url, maximumBytes = 1 },
            new { requestId = "", url = site.Url },
            new { requestId = "user-info", url = site.Url.Replace("http://", "http://user:fixture@", StringComparison.Ordinal) }
        })
            Check(!(await Call(invalid)).Success, "closed read-only request boundary rejects unsupported input");
        Check(site.Total == total, "invalid inputs dispatch no network traffic");

        read = await Call(Args("redirect-ok", "redirect"));
        Check(read.Success && Data(read).GetProperty("redirects").GetInt32() == 1, "allowed relative redirect follows GET");
        denied = await Call(Args("redirect-denied", "outside"));
        Check(!denied.Success && denied.Content.Contains("outside-domain") && site.Count("/forbidden") == 0,
            "redirect domain revalidated before target connection");
        denied = await Call(Args("redirect-loop", "loop"));
        Check(!denied.Success && denied.Content.Contains("redirect-limit") && site.Count("/loop") == 6, "redirect chain is fixed and bounded");
        denied = await Call(Args("known-large", "large", 1024));
        Check(!denied.Success && denied.Content.Contains("response-byte-limit") && denied.EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "declared response byte quota is enforced without claiming an unsent request");
        denied = await Call(Args("chunked-large", "chunked", 1024));
        Check(!denied.Success && denied.Content.Contains("response-byte-limit"), "streaming byte quota does not trust absent content length");
        read = await Call(Args("utf8-clip", "unicode", 65536, 256));
        var clipped = Data(read);
        Check(read.Success && clipped.GetProperty("truncated").GetBoolean() &&
            Encoding.UTF8.GetByteCount(clipped.GetProperty("text").GetString()!) <= 256 &&
            !clipped.GetProperty("text").GetString()!.Contains('\uFFFD'), "UTF-8 projection clipping preserves scalar boundaries");
        foreach (var path in new[] { "binary", "compressed", "invalid-utf8", "auth", "download" })
        {
            denied = await Call(Args("unsupported-" + path, path));
            Check(!denied.Success && denied.EffectiveOutcome == ToolExecutionOutcome.Unknown, "post-dispatch media/auth/download rejection remains uncertain");
        }
        await Call(Args("cookie-source", "cookie"));
        await Call(Args("after-cookie", "ok"));
        Check(site.SensitiveHeaders == 0, "no cookies, authorization or caller headers are forwarded");

        using (var cancel = new CancellationTokenSource())
        {
            var pending = Call(Args("cancelled-once", "slow"), token: cancel.Token);
            await site.SlowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            denied = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!denied.Success && denied.EffectiveOutcome == ToolExecutionOutcome.Unknown, "cancelled dispatched request records Unknown");
        }
        var slow = site.Count("/slow");
        replay = await Call(Args("cancelled-once", "slow"));
        Check(replay.Content == denied.Content && site.Count("/slow") == slow, "unknown delivery never repeats GET");
        var timedOut = await Call(Args("timeout-once", "slow"));
        Check(!timedOut.Success && timedOut.EffectiveOutcome == ToolExecutionOutcome.Unknown, "HTTP timeout produces retained Unknown");
        Check(site.Count("/slow") == slow + 1, "distinct explicit request gets one attempt");

        profile.PluginSettings[BrowserServicePlugin.Id] = E(policy with { AllowedDomains = [] });
        var policyChanged = await Call(input, effectContext);
        Check(!policyChanged.Success && policyChanged.Content.Contains("receipt-conflict"), "changed policy cannot retrieve cached prior source");
        profile.PluginSettings[BrowserServicePlugin.Id] = E(policy);
        Check(!BrowserNetworkAddressPolicy.Allows(IPAddress.Loopback, "allowed.example", new BrowserPolicy { AllowedDomains = ["allowed.example"] }),
            "DNS name grant does not implicitly grant loopback");
        Check(BrowserNetworkAddressPolicy.Allows(IPAddress.Loopback, "127.0.0.1", policy), "explicit numeric loopback grant supports owned fixtures");
        Check(!BrowserNetworkAddressPolicy.Allows(IPAddress.Parse("169.254.169.254"), "169.254.169.254", new BrowserPolicy { AllowedDomains = ["169.254.169.254"] }),
            "link-local metadata addresses fail closed");
        Check(!BrowserNetworkAddressPolicy.Allows(IPAddress.Any, "0.0.0.0", policy) &&
            !BrowserNetworkAddressPolicy.Allows(IPAddress.Parse("ff02::1"), "ff02::1", policy), "unspecified and multicast addresses fail closed");

        foreach (var special in new[] { "100.64.0.1", "198.18.0.1", "64:ff9b::a00:1" })
            Check(!BrowserNetworkAddressPolicy.Allows(IPAddress.Parse(special), "allowed.example", new BrowserPolicy { AllowedDomains = ["allowed.example"] }),
                "special-purpose DNS addresses require an explicit numeric grant");
        ToolResult? unretained = null; object? unretainedArguments = null;
        for (var index = 0; index < 40 && unretained is null; index++)
        {
            var bodyArguments = Args("body-budget-" + index, "escaped", 65536, 16384);
            var bodyResult = await Call(bodyArguments);
            if (!Data(bodyResult).GetProperty("receiptBodyRetained").GetBoolean()) { unretained = bodyResult; unretainedArguments = bodyArguments; }
        }
        Check(unretained is { Success: true }, "aggregate body budget limits retention while the first response remains available");
        var beforeReadback = site.Count("/escaped");
        var withoutBody = await Call(unretainedArguments!);
        Check(withoutBody.Success && withoutBody.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
            !Data(withoutBody).GetProperty("bodyAvailable").GetBoolean() && site.Count("/escaped") == beforeReadback,
            "body-free completion marker retains no-replay proof without keeping the source");
        var retainedMetrics = E(await ((IServiceCommandExtension)plugin).ExecuteAsync("browser.metrics", E(new { }), CancellationToken.None) ?? new { });
        Check(retainedMetrics.GetProperty("current").GetProperty("pageRequestBodyUtf8Bytes").GetInt64() <= 2 * 1024 * 1024,
            "reported aggregate body retention stays inside its fixed ceiling");
        var full = false;
        for (var index = 0; index < 300 && !full; index++)
        {
            var filled = await Call(new { requestId = "capacity-" + index, url = "https://outside.invalid/" });
            full = filled.Content.Contains("request-receipt-limit");
        }
        Check(full, "receipt capacity refuses new work rather than evicting replay protection");
        slow = site.Count("/slow");
        replay = await Call(Args("cancelled-once", "slow"));
        Check(replay.Content == denied.Content && site.Count("/slow") == slow, "unknown receipt remains readable after capacity exhaustion");
        await ((IServiceCommandExtension)plugin).ExecuteAsync("browser.close-all", E(new { }), CancellationToken.None);
        var metrics = E(await ((IServiceCommandExtension)plugin).ExecuteAsync("browser.metrics", E(new { }), CancellationToken.None) ?? new { });
        Check(metrics.GetProperty("current").GetProperty("jobs").GetInt32() == 0 &&
            metrics.GetProperty("current").GetProperty("tabs").GetInt32() == 0, "owned HTTP sessions close without a browser process");
        Console.WriteLine(JsonSerializer.Serialize(new { suite = "browser-page-request", checks,
            evidence = "real isolated loopback HTTP plus deterministic address/correlation boundaries; no external page, browser launch, model or Source" }));
        return checks;
    }

    private sealed class PageHost(string directory) : IServicePluginHost
    {
        public string PluginId => BrowserServicePlugin.Id;
        public string PackageDirectory => AppContext.BaseDirectory;
        public string DataDirectory => directory;
        public void Publish(string eventType, string? jobId, object? data) { }
    }

    private sealed class PageSite : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> connections = [];
        private readonly ConcurrentDictionary<string, int> counts = new();
        private readonly Task loop;
        public string Url { get; }
        public int SensitiveHeaders;
        public int Total => counts.Values.Sum();
        public int Count(string path) => counts.GetValueOrDefault(path);
        public TaskCompletionSource SlowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PageSite() { listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"; loop = ServeAsync(); }
        private async Task ServeAsync()
        {
            try { while (!stop.IsCancellationRequested) connections.Add(HandleAsync(await listener.AcceptTcpClientAsync(stop.Token))); }
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
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var first = await reader.ReadLineAsync(stop.Token) ?? "";
                    var path = first.Split(' ').ElementAtOrDefault(1)?.Split('?')[0] ?? "/";
                    counts.AddOrUpdate(path, 1, (_, current) => current + 1);
                    var headerBytes = first.Length;
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } header)
                    {
                        headerBytes += header.Length; if (headerBytes > 65536) return;
                        if (header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) || header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) ||
                            header.StartsWith("X-", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref SensitiveHeaders);
                    }
                    if (path == "/slow") { SlowEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, stop.Token); }
                    var status = "200 OK"; var extra = ""; var media = "text/plain; charset=utf-8";
                    var body = Encoding.UTF8.GetBytes("Browser HTTP source");
                    if (path is "/redirect" or "/outside" or "/loop")
                    {
                        status = "302 Found"; body = [];
                        extra = "Location: " + (path == "/outside" ? Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "forbidden" :
                            path == "/loop" ? "/loop" : "/ok") + "\r\n";
                    }
                    else if (path == "/large" || path == "/chunked") body = new byte[4096];
                    else if (path == "/escaped") body = new byte[16384];
                    else if (path == "/unicode") body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("😀", 200)));
                    else if (path == "/binary") media = "application/octet-stream";
                    else if (path == "/compressed") extra = "Content-Encoding: gzip\r\n";
                    else if (path == "/invalid-utf8") body = [255];
                    else if (path == "/auth") { status = "401 Unauthorized"; extra = "WWW-Authenticate: Basic realm=fixture\r\n"; body = []; }
                    else if (path == "/download") extra = "Content-Disposition: attachment; filename=fixture.txt\r\n";
                    else if (path == "/cookie") extra = "Set-Cookie: fixture-cookie=fixture; Path=/\r\n";
                    var framing = path == "/chunked" ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + body.Length + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + media + "\r\n" + extra + framing + "Connection: close\r\n\r\n"), stop.Token);
                    if (path == "/chunked") await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("X") + "\r\n"), stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                    if (path == "/chunked") await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"), stop.Token);
                }
                catch (IOException) { }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; stop.Dispose(); }
    }
}
