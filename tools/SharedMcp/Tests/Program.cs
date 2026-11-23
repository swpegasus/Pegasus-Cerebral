using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.SharedMcp;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;

if (args.FirstOrDefault() == "--tls") return await TlsChecks.RunAsync();
if (args.FirstOrDefault() == "--identity") return await IdentityChecks.RunAsync();
if (args.FirstOrDefault() == "--configured-tls")
{
    if (args.Length == 2) return await TlsChecks.RunConfiguredAsync(args[1]);
    Console.WriteLine("{\"success\":false,\"code\":\"usage_configured_tls_explicit_config_path\"}");
    return 2;
}
if (args.FirstOrDefault() == "--fixture") { await Fixture(args[1]); return 0; }
if (args.FirstOrDefault() == "--operator-fixture")
{
    using var registry = new OperatorMcpRegistry((operation, arguments, token) =>
        Task.FromResult(CommandResult.Ok(new { fixture = true, operation })));
    return await OperatorMcpHost.RunAsync(registry, Console.In, Console.Out);
}
var live = args.FirstOrDefault() == "--live";
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var testToken = deadline.Token;
var trace = new ConcurrentQueue<McpTrace>();
var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks.Add(label); Console.WriteLine("PASS: " + label); }
var defaultPolicy = new PrivateNetworkPolicy(new McpServerOptions().AllowedPeerCidrs);
Check(defaultPolicy.Allows(IPAddress.Loopback) && defaultPolicy.Allows(IPAddress.IPv6Loopback) &&
    !defaultPolicy.Allows(IPAddress.Parse("192.168.1.2")) && !defaultPolicy.Allows(IPAddress.Parse("100.64.1.2")), "library defaults allow loopback, deny LAN and VPN");
var explicitPolicy = new PrivateNetworkPolicy(["192.168.50.0/24", "100.100.2.3/32", "fd00:1234::/48"]);
Check(explicitPolicy.Allows(IPAddress.Parse("192.168.50.9")) && !explicitPolicy.Allows(IPAddress.Parse("192.168.51.9")) &&
    explicitPolicy.Allows(IPAddress.Parse("100.100.2.3")) && !explicitPolicy.Allows(IPAddress.Parse("100.100.2.4")) &&
    explicitPolicy.Allows(IPAddress.Parse("fd00:1234::5")), "explicit subnet and VPN address policy admits only configured peers (synthetic)");
foreach (var range in new[] { "0.0.0.0/0", "::/0", "8.8.8.8/32", "192.168.50.9/24", "10.0.0.0/7" })
{
    var rejected = false; try { _ = new PrivateNetworkPolicy([range]); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "reject unsafe or noncanonical policy " + range);
}

var process = Environment.ProcessPath!;
var selfArgs = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
    ? new[] { Assembly.GetExecutingAssembly().Location } : Array.Empty<string>();
StdioMcpDispatcher FixtureBackend(string product) => new(new(process, selfArgs.Concat(["--fixture", product]).ToArray(), Environment.CurrentDirectory)
    { CortexMultiplex = product == "cortex", RequestTimeout = TimeSpan.FromSeconds(10) });
await using var cortex = live ? new StdioMcpDispatcher(new(@"C:\Program Files\Pegasus\Cortex\PegasusCortex.exe", ["stdio"], @"C:\Development\Pegasus-Cortex")
    { CortexMultiplex = true }) : FixtureBackend("cortex");
await using var cerebral = FixtureBackend("cerebral");
await using var cerebralOperator = live
    ? new StdioMcpDispatcher(new(Path.GetFullPath("tools/SharedMcp/CerebralBackend/bin/Debug/net10.0/Pegasus.Cerebral.McpBackend.exe"), [], Environment.CurrentDirectory))
    : new StdioMcpDispatcher(new(process, selfArgs.Concat(["--operator-fixture"]).ToArray(), Environment.CurrentDirectory));
await using var injected = new NoncooperativeDispatcher();
// Inject only a disposable loopback endpoint; the library must ignore ambient Kestrel configuration.
var environmentKey = "Kestrel__Endpoints__Unrequested__Url";
var previousEnvironment = Environment.GetEnvironmentVariable(environmentKey);
SharedMcpServer server;
try
{
    Environment.SetEnvironmentVariable(environmentKey, "http://127.0.0.1:0");
    server = await SharedMcpServer.StartAsync(new() { Port = 0, MaximumPendingPerSession = 2, MaximumPendingTotal = 8, Trace = trace.Enqueue },
        new Dictionary<string, IMcpDispatcher> { ["cortex"] = cortex, ["cerebral"] = cerebral, ["cerebral-operator"] = cerebralOperator, ["inprocess"] = injected }, testToken);
}
finally { Environment.SetEnvironmentVariable(environmentKey, previousEnvironment); }
await using var serverLifetime = server;
Check(IPAddress.IsLoopback(IPAddress.Parse(server.Address.Host)), "ambient Kestrel configuration cannot add an unrequested listener");
using var http = new HttpClient { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(20) };

async Task<(HttpStatusCode Status, JsonObject? Body, string? Session)> Send(string product, string? session, JsonObject? message, string method = "POST", CancellationToken token = default)
{
    using var request = new HttpRequestMessage(new(method), "mcp/" + product);
    request.Headers.Accept.ParseAdd("application/json"); request.Headers.Accept.ParseAdd("text/event-stream");
    if (session is not null) { request.Headers.Add("Mcp-Session-Id", session); request.Headers.Add("MCP-Protocol-Version", "2025-06-18"); }
    if (message is not null) request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
    using var response = await http.SendAsync(request, token);
    var text = await response.Content.ReadAsStringAsync(token);
    return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject,
        response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.Single() : null);
}
JsonObject Rpc(JsonNode? id, string method, JsonObject? parameters = null)
{
    var obj = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters ?? new() };
    if (id is not null) obj["id"] = id.DeepClone(); return obj;
}
async Task<string> Connect(string product)
{
    var response = await Send(product, null, Rpc(1, "initialize", new() { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "shared-mcp-focused-test", ["version"] = "0" } }), token: testToken);
    Check(response.Status == HttpStatusCode.OK && response.Session is not null && response.Body?["result"]?["protocolVersion"]?.GetValue<string>() == "2025-06-18",
        "initialize " + product);
    await Send(product, response.Session, Rpc(null, "notifications/initialized"), token: testToken);
    return response.Session!;
}
JsonObject Call(int id, string label, int delay = 0, string name = "echo") => Rpc(id, "tools/call", new()
{
    ["name"] = name, ["arguments"] = new JsonObject { ["label"] = label, ["delay"] = delay },
    ["_meta"] = new JsonObject { ["threadId"] = label, ["sessionId"] = "codex-session-fixture" }
});
async Task WaitPending(int count)
{
    var until = Stopwatch.StartNew();
    while (server.PendingCount != count && until.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10, testToken);
    Check(server.PendingCount == count, "bounded pending count " + count);
}
var a = await Connect("cortex");
var b = await Connect("cortex");
var c = await Connect("cerebral");
Check(a != b && b != c, "independent product sessions");
Check((await Send("cerebral", a, Rpc(2, "tools/list"))).Status == HttpStatusCode.NotFound, "session cannot route across products");
Check((await Send("cortex", a, null, "GET")).Status == HttpStatusCode.MethodNotAllowed, "JSON-response profile returns GET 405");

if (live)
{
    var listed = await Send("cortex", a, Rpc(10, "tools/list"), token: testToken);
    Check(listed.Body?["result"]?["tools"] is JsonArray tools && tools.Any(t => t?["name"]?.GetValue<string>() == "cortex_status"), "LIVE Cortex tools from existing adapter");
    var status = await Send("cortex", b, Rpc(11, "tools/call", new() { ["name"] = "cortex_status", ["arguments"] = new JsonObject(),
        ["_meta"] = new JsonObject { ["threadId"] = "10000000-0000-4000-8000-000000000005" } }), token: testToken);
    Check(status.Body?["result"] is JsonObject result && result["isError"]?.GetValue<bool>() != true, "LIVE Cortex status over shared HTTP to installed resident host");
    Console.WriteLine("Live status completed; response bodies intentionally omitted.");
    var operatorStart = await Send("cerebral-operator", null, Rpc(1, "initialize", new()
    { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "bounded-live-inspection", ["version"] = "0" } }));
    if (operatorStart.Status == HttpStatusCode.OK && operatorStart.Session is { } operatorSession)
    {
        await Send("cerebral-operator", operatorSession, Rpc(null, "notifications/initialized"));
        var discovery = await Send("cerebral-operator", operatorSession, Rpc(12, "tools/call", new()
        { ["name"] = "cerebral_discover", ["arguments"] = new JsonObject(), ["_meta"] = new JsonObject { ["threadId"] = "10000000-0000-4000-8000-000000000005" } }));
        Check(discovery.Body?["result"]?["structuredContent"] is JsonObject && discovery.Body?["result"]?["isError"]?.GetValue<bool>() != true,
            "LIVE Cerebral compiled catalog through captured installed service route");
        if (args.Contains("--cerebral-health"))
        {
            try
            {
                var health = await Send("cerebral-operator", operatorSession, Rpc(13, "tools/call", new()
                { ["name"] = "cerebral_call", ["arguments"] = new JsonObject { ["operation"] = "health", ["arguments"] = new JsonObject() } }));
                Console.WriteLine("LIVE Cerebral service health outcome=" + health.Body?["result"]?["structuredContent"]?["state"]?.GetValue<string>() +
                    "; successful typed service health=" + (health.Body?["result"]?["structuredContent"]?["state"]?.GetValue<string>() == "completed"));
            }
            catch (OperationCanceledException)
            {
                await Send("cerebral-operator", operatorSession, Rpc(null, "notifications/cancelled", new() { ["requestId"] = 13 }));
                Console.WriteLine("UNAVAILABLE: Cerebral service health exceeded the client deadline; original wait cancellation requested, no replay or product startup.");
            }
        }
        else Console.WriteLine("NOT EXECUTED: live Cerebral service health; --cerebral-health explicitly enables that separate read.");
    }
    else Console.WriteLine("UNAVAILABLE: Cerebral captured installed-service backend could not initialize; no application or alternate owner was started.");
}
else
{
    var calls = new[] { Send("cortex", a, Call(7, "chat-a", 120)), Send("cortex", b, Call(7, "chat-b", 10)), Send("cerebral", c, Call(7, "chat-c", 30)) };
    var replies = await Task.WhenAll(calls);
    Check(replies.All(r => r.Body?["id"]?.GetValue<int>() == 7) && replies.Select(r => r.Body?["result"]?["label"]?.GetValue<string>()).SequenceEqual(["chat-a", "chat-b", "chat-c"]),
        "concurrent identical frontend IDs map to unique backend requests and original sessions");
    Check(replies[0].Body?["result"]?["product"]?.GetValue<string>() == "cortex" && replies[2].Body?["result"]?["product"]?.GetValue<string>() == "cerebral", "correct product dispatcher/catalog routing");
    Check(replies[0].Body?["result"]?["backendId"]?.ToJsonString() != replies[1].Body?["result"]?["backendId"]?.ToJsonString(), "shared child IDs globally unique");
    Check(replies[0].Body?["result"]?["pid"]?.ToJsonString() == replies[1].Body?["result"]?["pid"]?.ToJsonString(), "two client sessions share one backend process");
    Check(replies[0].Body?["result"]?["stream"]?.ToJsonString() != replies[1].Body?["result"]?["stream"]?.ToJsonString(), "Cortex multiplex streams isolated");
    Check(trace.Any(t => t.Context.TaskId == "chat-a" && t.Context.Product == "cortex" && t.Context.SessionId == a), "trace correlates task, HTTP session, product and typed request ID");

    var slow = Send("cortex", a, Call(20, "cancel-me", 5000));
    await WaitPending(1);
    await Send("cortex", a, Rpc(null, "notifications/cancelled", new() { ["requestId"] = 20 }));
    var cancelled = await slow;
    Check(cancelled.Body?["error"] is JsonObject, "explicit cancellation reaches mapped pending request");
    Check((await Send("cortex", b, Call(20, "survives-cancel"))).Body?["result"]?["label"]?.GetValue<string>() == "survives-cancel", "cancellation leaves other session usable");
    await WaitPending(0);
    var pending1 = Send("cortex", a, Call(31, "hold-1", 5000));
    var pending2 = Send("cortex", a, Call(32, "hold-2", 5000));
    await WaitPending(2);
    Check((await Send("cortex", a, Call(33, "over-cap"))).Status == HttpStatusCode.TooManyRequests, "per-session pending bound rejects overload");
    await Send("cortex", a, null, "DELETE");
    Check((await pending1).Body?["error"] is JsonObject && (await pending2).Body?["error"] is JsonObject, "DELETE cancels only its session pending requests");
    Check((await Send("cortex", a, Call(34, "stale"))).Status == HttpStatusCode.NotFound, "terminated session requires reinitialization");
    var reconnect = await Connect("cortex");
    Check((await Send("cortex", reconnect, Call(34, "reconnected"))).Body?["result"]?["label"]?.GetValue<string>() == "reconnected", "fresh client reconnect succeeds");
    var lost = await Send("cortex", b, Call(40, "die", name: "die"));
    Check(lost.Body?["error"] is JsonObject, "backend EOF settles dispatched request as unknown without replay");
    Check((await Send("cortex", b, Call(41, "new-backend"))).Body?["result"]?["label"]?.GetValue<string>() == "new-backend", "new request reconnects after backend exit");
    Check((await Send("cerebral", c, Call(42, "unaffected-product"))).Body?["result"]?["label"]?.GetValue<string>() == "unaffected-product", "backend failure leaves other product usable");
    // Drop a single HTTP connection. MCP requires explicit cancellation, so work must continue.
    using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
    try { await Send("cortex", b, Call(50, "http-dropped", 250), token: abort.Token); } catch (OperationCanceledException) { }
    await Task.Delay(350, testToken);
    Check((await Send("cortex", b, Call(51, "after-http-drop"))).Body?["result"]?["label"]?.GetValue<string>() == "after-http-drop", "HTTP disconnect does not tear down shared session");
    using var badOrigin = new HttpRequestMessage(HttpMethod.Post, "mcp/cortex");
    badOrigin.Headers.Add("Origin", "https://untrusted.example");
    Check((await http.SendAsync(badOrigin)).StatusCode == HttpStatusCode.Forbidden, "cross-origin request rejected before body or dispatch");

    var operatorSession = await Connect("cerebral-operator");
    var catalog = await Send("cerebral-operator", operatorSession, Rpc(60, "tools/call", new() { ["name"] = "cerebral_discover", ["arguments"] = new JsonObject(),
        ["_meta"] = new JsonObject { ["threadId"] = "operator-chat" } }));
    Check(catalog.Body?["result"]?["isError"]?.GetValue<bool>() != true && catalog.Body?["result"]?["structuredContent"] is JsonObject,
        "real Cerebral operator catalog accepts Codex metadata (fixture owner)");
    var typedHealth = await Send("cerebral-operator", operatorSession, Rpc(61, "tools/call", new() { ["name"] = "cerebral_call", ["arguments"] = new JsonObject
        { ["operation"] = "health", ["arguments"] = new JsonObject() }, ["_meta"] = new JsonObject { ["threadId"] = "operator-chat" } }));
    Check(typedHealth.Body?["result"]?["isError"]?.GetValue<bool>() != true && typedHealth.Body?["result"]?["structuredContent"]?["state"]?.GetValue<string>() == "completed",
        "Cerebral typed registry routes health through injected deterministic owner fixture");
    var direct = await Connect("inprocess");
    var ignored1 = Send("inprocess", direct, Call(70, "direct-a"));
    var ignored2 = Send("inprocess", direct, Call(71, "direct-b"));
    await WaitPending(2);
    await Send("inprocess", direct, Rpc(null, "notifications/cancelled", new() { ["requestId"] = 70 }));
    await Send("inprocess", direct, Rpc(null, "notifications/cancelled", new() { ["requestId"] = 71 }));
    Check((await ignored1).Body?["error"] is JsonObject && (await ignored2).Body?["error"] is JsonObject,
        "in-process dispatcher cancellation callbacks cannot break transport");
    Check(server.PendingCount == 2 && (await Send("inprocess", direct, Call(72, "blocked"))).Status == HttpStatusCode.TooManyRequests,
        "noncooperative dispatcher retains pending reservations after caller cancellation");
    Check((await Send("inprocess", direct, null, "DELETE")).Status == HttpStatusCode.NoContent,
        "throwing product cancellation callbacks do not break session teardown");
    injected.Release.TrySetResult();
    await WaitPending(0);
    Check(injected.Contexts.All(x => x.Product == "inprocess" && x.SessionId == direct && x.TaskId is "direct-a" or "direct-b"),
        "direct embedding receives independent explicit product/session/task/request context");
}
Console.WriteLine($"{checks.Count} focused checks passed; tier={(live ? "live Cortex pipe + HTTP and synthetic network policy" : "isolated real HTTP/child processes with deterministic fixture backends")}; listener={server.Address}");
return 0;

static async Task Fixture(string product)
{
    var calls = new ConcurrentDictionary<string, CancellationTokenSource>();
    var writes = new SemaphoreSlim(1);
    async Task Send(JsonObject obj) { await writes.WaitAsync(); try { await Console.Out.WriteLineAsync(obj.ToJsonString()); } finally { writes.Release(); } }
    async Task Reply(JsonObject request)
    {
        var id = request["id"]!.DeepClone(); var key = id.ToJsonString();
        var cancellation = new CancellationTokenSource(); calls[key] = cancellation;
        try
        {
            if (request["method"]?.GetValue<string>() == "initialize")
            { await Send(new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject(), ["experimental"] = new JsonObject { ["pegasus.cortex/multiplex"] = true } }, ["serverInfo"] = new JsonObject { ["name"] = product, ["version"] = "fixture" } } }); return; }
            if (request["params"]?["name"]?.GetValue<string>() == "die") Environment.Exit(17);
            var delay = request["params"]?["arguments"]?["delay"]?.GetValue<int>() ?? 0;
            await Task.Delay(delay, cancellation.Token);
            await Send(new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject
            { ["label"] = request["params"]?["arguments"]?["label"]?.DeepClone(), ["product"] = product,
              ["backendId"] = id.DeepClone(), ["stream"] = request["params"]?["_meta"]?["pegasus.cortex/streamId"]?.DeepClone(), ["pid"] = Environment.ProcessId } });
        }
        catch (OperationCanceledException) { await Send(new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32800, ["message"] = "fixture cancelled" } }); }
        finally { calls.TryRemove(key, out _); cancellation.Dispose(); }
    }
    string? line;
    while ((line = await Console.In.ReadLineAsync()) is not null)
    {
        var request = JsonNode.Parse(line)!.AsObject();
        if (request["method"]?.GetValue<string>() == "notifications/cancelled")
        { if (calls.TryGetValue(request["params"]!["requestId"]!.ToJsonString(), out var cancellation)) cancellation.Cancel(); }
        else if (request["id"] is not null) _ = Reply(request);
    }
}

sealed class NoncooperativeDispatcher : IMcpDispatcher
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<McpRequestContext> Contexts { get; } = new();
    public Task<JsonObject> InitializeAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject
    {
        ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "injected-test", ["version"] = "fixture" }
    });
    public async Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request, CancellationToken cancellationToken)
    {
        Contexts.Enqueue(context);
        using var registration = cancellationToken.Register(() => throw new InvalidOperationException("fixture callback failure"));
        await Release.Task;
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject() };
    }
    public ValueTask DisposeAsync() { Release.TrySetResult(); return ValueTask.CompletedTask; }
}
