using System.Reflection;
using System.Text;
using Cerebral.Core;
using Microsoft.Playwright;

namespace Cerebral.BrowserPlugin.Tests;

// Fake host capabilities and narrow Playwright interface stubs; no socket,
// browser process, user profile, policy store or shared scheduler implementation is created here.
public static class BrowserManagedInternetChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); checks++; }
        bool Rejects(Action action, string code)
        { try { action(); return false; } catch (BrowserRejectedException error) { return error.Code == code; } }
        async Task<BrowserPageRequestException?> RejectedPage(BrowserSession job, int maximum = 1024)
        {
            try { await job.Network.RequestPageAsync(new("fixture", "https://allowed.invalid/start", MaximumBytes: maximum), default); return null; }
            catch (BrowserPageRequestException error) { return error; }
        }

        Check(Rejects(() => BrowserInternetScope.Capture(Context(null), "browser.request", default),
            "managed-internet-unavailable"), "Missing host broker fails closed.");
        var badVersion = new FakeBroker { Version = 0 };
        Check(Rejects(() => BrowserInternetScope.Capture(Context(badVersion), "browser.request", default),
            "managed-internet-contract-mismatch") && badVersion.Calls.Count == 0, "An unsupported broker version never sends.");
        var badOwner = new FakeBroker(); badOwner.Authority = badOwner.Authority with { RunId = "another-run" };
        Check(Rejects(() => BrowserInternetScope.Capture(Context(badOwner), "browser.request", default),
            "managed-internet-owner-mismatch"), "Model/tool context cannot substitute another host run.");
        using var provider = new BrowserServicePlugin();
        Check(provider is IManagedInternetToolProvider { ManagedInternetVersion: 1 } &&
            BrowserInternetScope.Capture(Context(null), "browser.close", default) is null,
            "The provider opts into host-gated v1 admission while cleanup close remains available without Internet.");

        {
            var broker = new FakeBroker();
            broker.Handle = (request, _) => Task.FromResult(broker.Calls.Count == 1
                ? Response(request, 302, location: "https://assets.invalid/final") : Response(request));
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.request", default)!;
            job.BindInternet(scope);
            await job.Network.RequestPageAsync(new("redirect", "https://allowed.invalid/start", MaximumBytes: 1024), default);
            Check(broker.Calls.Count == 2 && broker.Calls[0].RedirectCount == 0 && broker.Calls[1].RedirectCount == 1 &&
                broker.Calls.All(x => !x.FollowRedirects && x.MaximumResponseBytes == 1024 && x.Body is null),
                "Every explicit page/redirect hop uses the broker with native counts and the lower provider byte limit.");
        }
        {
            var broker = new FakeBroker { Handle = (request, _) => Task.FromResult(Response(request, 302, location: "https://blocked.invalid/")) };
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.request", default)!;
            job.BindInternet(scope);
            var rejected = await RejectedPage(job);
            Check(rejected is not null && broker.Calls.Count == 1 && rejected.Dispatched,
                "A provider-domain redirect is rejected before the destination is sent, even if the host broker permits it.");
        }
        {
            var broker = new FakeBroker();
            broker.Handle = (request, _) => broker.Calls.Count == 1
                ? Task.FromResult(Response(request, 302, location: "/next"))
                : throw new InternetAccessException("internet-policy-changed", "Synthetic live revision change.");
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.request", default)!;
            job.BindInternet(scope);
            var rejected = await RejectedPage(job);
            Check(rejected is { Code: "internet-policy-changed", Dispatched: true } && broker.Calls.Count == 2,
                "A later hop re-enters live host admission; an earlier dispatched hop is not misreported as never sent.");
        }
        {
            var broker = new FakeBroker { Handle = (request, _) => Task.FromResult(Response(request, body: new byte[1025])) };
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.request", default)!;
            job.BindInternet(scope);
            Check((await RejectedPage(job)) is { Code: "response-byte-limit" } && broker.Calls.Count == 1,
                "A broker violating its requested byte bound cannot deliver a body to the caller.");
        }
        {
            var broker = new FakeBroker
            {
                Handle = (request, _) => Task.FromResult(Response(request) with { Truncated = true })
            };
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.request", default)!;
            job.BindInternet(scope);
            Check((await RejectedPage(job)) is { Code: "response-byte-limit" },
                "Browser never accepts a broker partial-prefix response when its complete-response contract was requested.");
        }
        {
            var old = new FakeBroker();
            await using var job = Job();
            using var expired = BrowserInternetScope.Capture(Context(old), "browser.request", default)!;
            job.BindInternet(expired); expired.Dispose();
            var rejected = await RejectedPage(job);
            var fresh = new FakeBroker(); fresh.Authority = fresh.Authority with { EffectId = "effect-two" };
            using var next = BrowserInternetScope.Capture(Context(fresh, "effect-two"), "browser.request", default)!;
            job.BindInternet(next);
            await job.Network.RequestPageAsync(new("next", "https://allowed.invalid/start", MaximumBytes: 1024), default);
            var otherMain = new FakeBroker(); otherMain.Authority = otherMain.Authority with { MainConversationId = "another-main" };
            using var wrong = BrowserInternetScope.Capture(Context(otherMain), "browser.request", default)!;
            var denied = Rejects(() => job.BindInternet(wrong), "managed-internet-owner-mismatch");
            Check(rejected is not null && old.Calls.Count == 0 && fresh.Calls.Count == 1 && denied,
                "Ended scopes cannot send; a fresh effect rebinds, and another owning Main cannot inherit the browser.");
        }
        {
            var old = new FakeBroker(); old.Authority = old.Authority with { ToolName = "browser.open" };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.Handle = async (request, token) =>
            {
                if (old.Calls.Count == 8) entered.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                catch (OperationCanceledException)
                { throw new InternetAccessException("managed-internet-scope-expired", "Synthetic queued attempt never dispatched.", false); }
                return Response(request);
            };
            await using var job = Job();
            using var prior = BrowserInternetScope.Capture(Context(old), "browser.open", default)!;
            job.BindInternet(prior);
            var page = Stub<IPage>(_ => null); job.Tabs.TryAdd("tab", new BrowserTab(page));
            var pending = Enumerable.Range(0, 9).Select(_ =>
                job.Network.RouteAsync(Route(page, Request(page, _ % 2 == 0 ? "https://allowed.invalid/queued" : "https://assets.invalid/queued")).Value)).ToArray();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            prior.Dispose();
            var fresh = new FakeBroker(); fresh.Authority = fresh.Authority with { ToolName = "browser.open", EffectId = "effect-two" };
            using var next = BrowserInternetScope.Capture(Context(fresh, "effect-two"), "browser.open", default)!;
            job.BindInternet(next);
            var current = Route(page, Request(page, "https://allowed.invalid/current"));
            await job.Network.RouteAsync(current.Value);
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            Check(old.Calls.Count == 8 && fresh.Calls.Count == 1 && current.Fulfilled,
                "A callback already queued under an old action cannot spend the replacement action's authority.");
        }
        {
            var broker = new FakeBroker(); broker.Authority = broker.Authority with { ToolName = "browser.open" };
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.open", default)!;
            job.BindInternet(scope);
            var page = Stub<IPage>(_ => null);
            job.Tabs.TryAdd("tab", new BrowserTab(page));
            var ancestor = Request(page, "https://allowed.invalid/a");
            var parent = Request(page, "https://allowed.invalid/b", ancestor);
            var asset = Route(page, Request(page, "https://assets.invalid/script.js", parent));
            await job.Network.RouteAsync(asset.Value);
            Check(asset.Fulfilled && !asset.Aborted && broker.Calls.Single().RedirectCount == 2 &&
                !broker.Calls.Single().FollowRedirects,
                "Rendered assets use the same host broker and carry the intercepted browser redirect-chain count.");
        }
        {
            var broker = new FakeBroker(); broker.Authority = broker.Authority with { ToolName = "browser.open" };
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.open", default)!;
            job.BindInternet(scope);
            var page = Stub<IPage>(_ => null); job.Tabs.TryAdd("tab", new BrowserTab(page));
            var auth = Route(page, Request(page, "https://allowed.invalid/auth", headers: new() { ["Authorization"] = "synthetic" }));
            await job.Network.RouteAsync(auth.Value);
            var cookie = Route(page, Request(page, "https://allowed.invalid/cookie", headers: new() { ["Cookie"] = "synthetic" }));
            await job.Network.RouteAsync(cookie.Value);
            var tooLarge = Route(page, Request(page, "https://allowed.invalid/post", body: new byte[1048577]));
            await job.Network.RouteAsync(tooLarge.Value);
            broker.Handle = (request, _) => Task.FromResult(Response(request, 401));
            var challenge = Route(page, Request(page, "https://allowed.invalid/challenge"));
            await job.Network.RouteAsync(challenge.Value);
            broker.Handle = (request, _) => Task.FromResult(Response(request, disposition: "attachment; filename=synthetic.txt"));
            var attachment = Route(page, Request(page, "https://allowed.invalid/file"));
            await job.Network.RouteAsync(attachment.Value);
            Check(auth.Aborted && cookie.Fulfilled && tooLarge.Aborted && challenge.Aborted && attachment.Aborted &&
                !challenge.Fulfilled && !attachment.Fulfilled && broker.Calls.Count == 3,
                "Authorization, oversized uploads, auth challenges and attachments stay denied; isolated explicit cookies pass only through the broker.");
        }
        {
            var broker = new FakeBroker(); broker.Authority = broker.Authority with { ToolName = "browser.open" };
            var four = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            broker.Handle = async (request, token) =>
            {
                if (broker.Calls.Count == 4) four.TrySetResult();
                await release.Task.WaitAsync(token);
                return Response(request);
            };
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.open", default)!;
            job.BindInternet(scope);
            var page = Stub<IPage>(_ => null); job.Tabs.TryAdd("tab", new BrowserTab(page));
            var first = Enumerable.Range(0, 4).Select(_ =>
                job.Network.RouteAsync(Route(page, Request(page, "https://allowed.invalid/parallel")).Value)).ToArray();
            await four.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var fifth = job.Network.RouteAsync(Route(page, Request(page, "https://allowed.invalid/fifth")).Value);
            var bounded = broker.Calls.Count == 4;
            release.TrySetResult();
            await Task.WhenAll(first.Append(fifth)).WaitAsync(TimeSpan.FromSeconds(5));
            Check(bounded && broker.Calls.Count == 5, "The provider retains its four-request per-origin connection bound.");
        }
        {
            var broker = new FakeBroker(); broker.Authority = broker.Authority with { ToolName = "browser.open" };
            var payload = new byte[8 * 1024 * 1024];
            broker.Handle = (request, _) => Task.FromResult(Response(request, body: payload));
            await using var job = Job();
            using var scope = BrowserInternetScope.Capture(Context(broker), "browser.open", default)!;
            job.BindInternet(scope);
            var page = Stub<IPage>(_ => null); job.Tabs.TryAdd("tab", new BrowserTab(page));
            for (var i = 0; i < 8; i++)
                await job.Network.RouteAsync(Route(page, Request(page, "https://allowed.invalid/asset")).Value);
            var excess = Route(page, Request(page, "https://allowed.invalid/excess"));
            await job.Network.RouteAsync(excess.Value);
            Check(broker.Calls.Count == 8 && job.Network.ResponseBytes == 64L * 1024 * 1024 && excess.Aborted,
                "Buffered broker results retain the aggregate session byte ceiling without an extra send.");
        }
        return checks;
    }

    private static ToolExecutionContext Context(IManagedInternetSession? broker, string effect = "effect-one") =>
        new(new AgentProfile { Id = "profile", Name = "Synthetic" }, "conversation", "run", EffectId: effect) { Internet = broker };
    private static BrowserSession Job() => new(new BrowserPolicy { AllowedDomains = ["allowed.invalid", "assets.invalid"] }, "owner", "profile");
    private static ManagedInternetResponse Response(ManagedInternetRequest request, int status = 200,
        string? location = null, byte[]? body = null, string? disposition = null)
    {
        var headers = new Dictionary<string, string[]> { ["Content-Type"] = ["text/plain; charset=utf-8"] };
        if (location is not null) headers["Location"] = [location];
        if (disposition is not null) headers["Content-Disposition"] = [disposition];
        return new(status, request.Url, headers, body ?? Encoding.UTF8.GetBytes("synthetic"), 0, 1, 1, 0);
    }

    private sealed class FakeBroker : IManagedInternetSession
    {
        public int Version { get; set; } = 1;
        public ManagedInternetAuthority Authority { get; set; } =
            new(1, "installation", "main", null, "conversation", "run", "effect-one", "browser.request", new string('a', 64));
        internal readonly List<ManagedInternetRequest> Calls = [];
        internal Func<ManagedInternetRequest, CancellationToken, Task<ManagedInternetResponse>>? Handle;
        public Task<ManagedInternetResponse> SendAsync(ManagedInternetRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add(request); return Handle?.Invoke(request, token) ?? Task.FromResult(Response(request)); }
    }

    private sealed class RouteState
    {
        internal required IRoute Value;
        internal bool Aborted, Fulfilled;
    }
    private static RouteState Route(IPage page, IRequest request)
    {
        var state = new RouteState { Value = null! };
        state.Value = Stub<IRoute>(call => call.Method.Name switch
        {
            "get_Request" => request,
            "AbortAsync" => Mark(() => state.Aborted = true),
            "FulfillAsync" => Mark(() => state.Fulfilled = true),
            _ => throw new NotSupportedException(call.Method.Name)
        });
        return state;
    }
    private static IRequest Request(IPage page, string url, IRequest? previous = null,
        Dictionary<string, string>? headers = null, byte[]? body = null)
    {
        var frame = Stub<IFrame>(call => call.Method.Name == "get_Page" ? page : throw new NotSupportedException(call.Method.Name));
        return Stub<IRequest>(call => call.Method.Name switch
        {
            "get_Url" => url, "get_Method" => body is null ? "GET" : "POST", "get_Frame" => frame,
            "get_PostDataBuffer" => body, "get_ResourceType" => "script", "get_RedirectedFrom" => previous,
            "AllHeadersAsync" => Completed(call.Method.ReturnType, headers ?? new Dictionary<string, string>()),
            _ => throw new NotSupportedException(call.Method.Name)
        });
    }
    private static Task Mark(Action action) { action(); return Task.CompletedTask; }
    private static object Completed(Type type, object value) => typeof(Task).GetMethod(nameof(Task.FromResult))!
        .MakeGenericMethod(type.GenericTypeArguments[0]).Invoke(null, [value])!;
    private static T Stub<T>(Func<(MethodInfo Method, object?[]? Arguments), object?> handler) where T : class
    {
        var value = DispatchProxy.Create<T, PlaywrightStub>();
        ((PlaywrightStub)(object)value).Handler = handler;
        return value;
    }
    public class PlaywrightStub : DispatchProxy
    {
        public Func<(MethodInfo Method, object?[]? Arguments), object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Handler((method ?? throw new InvalidOperationException("Missing stub method."), args));
    }
}
