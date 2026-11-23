using System.Net;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic fake-clock/fake-transport checks. No DNS, sockets, browser, model or installed state.</summary>
public static class ManagedInternetBrokerChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        static ManagedInternetAuthority Owner(string main, string worker) => new(1, "fixture-install", main,
            worker, worker, "run-" + worker, "effect-" + worker, "http.fetch", new string('a', 64));
        var uri = new Uri("https://example.com/");
        var limits = new InternetLimits(RequestsPerSecond: 2, Burst: 2, MaximumConcurrentRequests: 4);
        Check(InternetPolicyRules.Allows(limits, uri), "null domain allowset permits public candidates");
        Check(!InternetPolicyRules.Allows(limits with { AllowedDomains = [] }, uri), "empty domain allowset denies all");
        Check(!InternetPolicyRules.Allows(limits with { BlockedDomains = ["example.com"] }, uri), "domain deny wins");
        var intersection = InternetPolicyRules.Intersect(limits with { AllowedDomains = ["a.example"] },
            limits with { AllowedDomains = ["b.example"] });
        Check(intersection.AllowedDomains is { Count: 0 } && !InternetPolicyRules.Allows(intersection, uri),
            "disjoint allowsets stay deny-all");
        Check(Throws(() => InternetPolicyRules.Narrow(limits, limits with { RequestsPerSecond = 3 })),
            "Main cannot raise global rate");
        Check(Throws(() => InternetPolicyRules.Validate(limits with { RequestsPerSecond = double.NaN })),
            "nonfinite rate rejected");
        Check(!ManagedInternetBroker.PublicAddress(IPAddress.Parse("127.0.0.1")) &&
            !ManagedInternetBroker.PublicAddress(IPAddress.Parse("::ffff:127.0.0.1")) &&
            !ManagedInternetBroker.PublicAddress(IPAddress.Parse("10.0.0.1")) &&
            ManagedInternetBroker.PublicAddress(IPAddress.Parse("8.8.8.8")),
            "arbitrary loopback/private URL is not trusted IPC");

        var clock = new ManualClock(); var transport = new FakeTransport(_ => Reply(new byte[3]));
        using (var broker = new ManagedInternetBroker(_ => new(limits, limits, 1, 1), null, clock, transport))
        {
            await broker.Open(Owner("a", "one")).SendAsync(new("GET", uri), default);
            await broker.Open(Owner("b", "two")).SendAsync(new("GET", uri), default);
            var queued = broker.Open(Owner("c", "three")).SendAsync(new("GET", uri), default);
            await SpinAsync(); Check(transport.Calls == 2 && !queued.IsCompleted, "global burst shared across Main chats");
            clock.Advance(TimeSpan.FromMilliseconds(500)); await PumpAsync(queued, clock);
            Check(transport.Calls == 3, "real send admitted after global token refill");
        }
        clock = new(); transport = new(_ => Reply(new byte[1]));
        var broad = limits with { RequestsPerSecond = 100, Burst = 100 };
        var narrow = limits with { RequestsPerSecond = 1, Burst = 1 };
        using (var broker = new ManagedInternetBroker(_ => new(broad, narrow, 1, 1), null, clock, transport))
        {
            await broker.Open(Owner("same-main", "worker-one")).SendAsync(new("GET", uri), default);
            var second = broker.Open(Owner("same-main", "worker-two")).SendAsync(new("GET", uri), default);
            await SpinAsync(); Check(transport.Calls == 1, "two owned workers do not each receive a new Main burst");
            clock.Advance(TimeSpan.FromSeconds(1)); await PumpAsync(second, clock);
            Check(transport.Calls == 2, "Main rate refill admits the waiting owned worker");
        }
        clock = new(); var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport = new(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new HeldStream(released.Task)) });
        var denied = false;
        using (var broker = new ManagedInternetBroker(_ => denied
            ? throw new InternetAccessException("internet-global-off", "fixture revoked")
            : new(broad with { MaximumConcurrentRequests = 1 }, broad with { MaximumConcurrentRequests = 1 }, 1, 1),
            null, clock, transport))
        {
            var first = broker.Open(Owner("same", "first")).SendAsync(new("GET", uri), default);
            await SpinAsync();
            var second = broker.Open(Owner("same", "second")).SendAsync(new("GET", uri), default);
            await SpinAsync(); Check(transport.Calls == 1 && !second.IsCompleted, "slot held through body consumption");
            denied = true; broker.Invalidate("same");
            var firstError = await Failure(first); var secondError = await Failure(second);
            Check(firstError.Dispatched && !secondError.Dispatched && transport.Calls == 1,
                "revocation cancels active body and queued request without another send");
            released.TrySetResult();
        }
        clock = new(); transport = new(_ => Reply(new byte[1025]));
        using (var broker = new ManagedInternetBroker(_ => new(broad, broad with { MaximumResponseBytes = 1024 }, 1, 1), null, clock, transport))
        {
            var error = await Failure(broker.Open(Owner("cap", "strict")).SendAsync(new("GET", uri), default));
            Check(error.Code == "internet-response-byte-limit" && error.Dispatched, "strict response byte limit is honest after dispatch");
            var clipped = await broker.Open(Owner("cap", "partial")).SendAsync(new("GET", uri, ReturnPartialBody: true), default);
            Check(clipped.Truncated && clipped.Body.Length == 1024, "fetch may retain a bounded partial body with explicit truncation");
        }
        clock = new(); transport = new(request => request.RequestUri!.AbsolutePath == "/"
            ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/next", UriKind.Relative) }, Content = new ByteArrayContent([]) }
            : Reply([1]));
        var revision = 1L;
        using (var broker = new ManagedInternetBroker(_ => new(broad, broad, revision, 1), value =>
        {
            if (value.State == "dispatched" && transport.Calls == 0) revision = 2;
        }, clock, transport))
        {
            var failure = await Failure(broker.Open(Owner("redirect", "changed")).SendAsync(new("GET", uri, FollowRedirects: true), default));
            Check(failure.Dispatched && transport.Calls == 1, "changed policy rechecked before redirected send");
        }
        clock = new(); transport = new(_ => new(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://blocked.example/") }, Content = new ByteArrayContent([]) });
        using (var broker = new ManagedInternetBroker(_ => new(broad, broad with { AllowedDomains = ["example.com"] }, 1, 1), null, clock, transport))
        {
            var failure = await Failure(broker.Open(Owner("redirect", "domain")).SendAsync(new("GET", uri, FollowRedirects: true), default));
            Check(failure.Code == "internet-domain-denied" && transport.Calls == 1, "redirect target denied before another request");
        }
        return checks;
    }
    private static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    private static HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static async Task<InternetAccessException> Failure(Task task)
    { try { await task; } catch (InternetAccessException error) { return error; } throw new InvalidOperationException("Expected rejection."); }
    private static async Task SpinAsync() { for (var i = 0; i < 16; i++) await Task.Yield(); }
    private static async Task PumpAsync(Task task, ManualClock clock)
    {
        for (var i = 0; i < 2000 && !task.IsCompleted; i++) { clock.Advance(TimeSpan.FromMilliseconds(10)); await Task.Yield(); }
        if (!task.IsCompleted) throw new InvalidOperationException("Fake-clock check did not settle."); await task;
    }
    private sealed class FakeTransport(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls); return Task.FromResult(send(request)); }
    }
    private sealed class HeldStream(Task released) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await released.WaitAsync(token); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    internal sealed class ManualClock : TimeProvider
    {
        private long ticks; private readonly List<ManualTimer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (timers) timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        public void Advance(TimeSpan amount)
        {
            ticks += amount.Ticks; ManualTimer[] copy; lock (timers) copy = timers.ToArray();
            foreach (var timer in copy) timer.Fire(ticks);
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue, period; private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + Math.Max(0, dueTime.Ticks);
                this.period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks; return true;
            }
            public void Fire(long now)
            { if (disposed || due > now) return; due = period > 0 ? now + period : long.MaxValue; callback(state); }
            public void Dispose() { disposed = true; lock (clock.timers) clock.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
