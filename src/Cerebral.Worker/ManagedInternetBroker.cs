using System.Net;
using System.Net.Sockets;
using Cerebral.Core;

namespace Cerebral.Worker;

internal sealed record ManagedInternetPolicyState(InternetLimits GlobalLimits, InternetLimits EffectiveLimits,
    long GlobalRevision, long MainRevision);
internal sealed record InternetDispatchObservation(ManagedInternetAuthority Authority, string State,
    long ThrottledMilliseconds, string? Reason = null);

/// <summary>One dispatcher for application-managed web requests. All Main descendants share its buckets.
/// No listener, proxy, credential store, automatic retry, or Full Access override is introduced.</summary>
internal sealed class ManagedInternetBroker : IDisposable
{
    private readonly Func<ManagedInternetAuthority, ManagedInternetPolicyState> resolve;
    private readonly Action<InternetDispatchObservation>? observe;
    private readonly TimeProvider clock;
    private readonly HttpClient client;
    private readonly object sync = new();
    private sealed class Bucket { public double Tokens; public long Stamp; public int Active; }
    private readonly Bucket global;
    private readonly Dictionary<string, Bucket> mains = new(StringComparer.Ordinal);
    private readonly Dictionary<CancellationTokenSource, string> pending = [];
    private bool disposed;
    private bool globalInitialized;
    private sealed class ConnectionAttempt { public int Count; }
    private static readonly HttpRequestOptionsKey<ConnectionAttempt> AttemptKey = new("cerebral.internet.connection.v1");
    private static readonly HttpRequestOptionsKey<Func<ManagedInternetPolicyState>> ScopeKey = new("cerebral.internet.scope.v1");
    private static readonly HttpRequestOptionsKey<ManagedInternetAuthority> AuthorityKey = new("cerebral.internet.authority.v1");

    public ManagedInternetBroker(Func<ManagedInternetAuthority, ManagedInternetPolicyState> resolve,
        Action<InternetDispatchObservation>? observe = null) : this(resolve, observe, TimeProvider.System, null) { }
    internal ManagedInternetBroker(Func<ManagedInternetAuthority, ManagedInternetPolicyState> resolve,
        Action<InternetDispatchObservation>? observe, TimeProvider clock, HttpMessageHandler? transport)
    {
        this.resolve = resolve; this.observe = observe; this.clock = clock;
        global = new() { Tokens = 0, Stamp = clock.GetTimestamp() };
        client = new(transport ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.Zero, MaxResponseDrainSize = 0, ConnectCallback = ConnectAsync
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public IManagedInternetSession Open(ManagedInternetAuthority authority) => new Session(this, authority);
    public IManagedInternetSession Open(ManagedInternetAuthority authority, Func<ManagedInternetPolicyState> liveScope) => new Session(this, authority, liveScope);
    private sealed class Session(ManagedInternetBroker owner, ManagedInternetAuthority authority, Func<ManagedInternetPolicyState>? scope = null) : IManagedInternetSession
    {
        public int Version => InternetAuthorityContract.Version;
        public ManagedInternetAuthority Authority => authority;
        public Task<ManagedInternetResponse> SendAsync(ManagedInternetRequest request, CancellationToken token) =>
            owner.SendAsync(authority, request, token, scope);
    }
    private ManagedInternetPolicyState Current(ManagedInternetAuthority authority, Uri? url = null, Func<ManagedInternetPolicyState>? scope = null)
    {
        var value = scope?.Invoke() ?? resolve(authority); // Must check live Main ownership, accepted tool and policy; absence throws.
        InternetPolicyRules.Validate(value.GlobalLimits); InternetPolicyRules.Validate(value.EffectiveLimits);
        if (url is not null && !InternetPolicyRules.Allows(value.EffectiveLimits, url))
            throw new InternetAccessException("internet-domain-denied", "The destination is outside the current Internet policy.");
        return value;
    }
    public void Invalidate(string? mainId = null)
    {
        CancellationTokenSource[] stop;
        lock (sync) stop = pending.Where(x => mainId is null || x.Value == mainId).Select(x => x.Key).ToArray();
        foreach (var source in stop) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
    }
    private async Task<ManagedInternetResponse> SendAsync(ManagedInternetAuthority authority,
        ManagedInternetRequest input, CancellationToken token, Func<ManagedInternetPolicyState>? scope)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Url is null || input.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS") ||
            input.Body is { Length: > 1048576 } || input.RedirectCount is < 0 or > 10 ||
            input.MaximumResponseBytes is <= 0 or > 8388608)
            throw new InternetAccessException("internet-request-invalid", "The managed HTTP request exceeds its fixed bounds.");
        var initial = Current(authority, input.Url, scope);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending.Count >= 256) throw new InternetAccessException("internet-queue-full", "The bounded managed Internet queue is full.");
            pending.Add(stop, authority.MainConversationId);
        }
        // Deadline includes queueing, redirects and full bounded body consumption.
        using var timer = clock.CreateTimer(_ => { try { stop.Cancel(); } catch (ObjectDisposedException) { } }, null,
            TimeSpan.FromSeconds(initial.EffectiveLimits.RequestTimeoutSeconds), Timeout.InfiniteTimeSpan);
        var dispatched = false; long throttled = 0;
        try
        {
            var uri = input.Url; var method = input.Method; var body = input.Body;
            for (var redirects = input.RedirectCount; ; redirects++)
            {
                stop.Token.ThrowIfCancellationRequested();
                var policy = Current(authority, uri, scope);
                if (redirects > policy.EffectiveLimits.MaximumRedirects)
                    throw new InternetAccessException("internet-redirect-limit", "The current redirect ceiling was reached.", dispatched);
                var maximum = Math.Min(input.MaximumResponseBytes ?? policy.EffectiveLimits.MaximumResponseBytes,
                    policy.EffectiveLimits.MaximumResponseBytes);
                using var slot = await AcquireAsync(authority, uri, stop.Token, scope).ConfigureAwait(false);
                throttled += slot.WaitedMilliseconds;
                policy = Current(authority, uri, scope);
                if (policy.GlobalRevision != initial.GlobalRevision || policy.MainRevision != initial.MainRevision)
                    throw new InternetAccessException("internet-policy-changed", "The Internet policy changed before dispatch.", dispatched);
                using var request = new HttpRequestMessage(new HttpMethod(method), uri)
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.ConnectionClose = true;
                request.Options.Set(AttemptKey, new ConnectionAttempt());
                request.Options.Set(AuthorityKey, authority);
                request.Options.Set(ScopeKey, scope ?? (() => resolve(authority)));
                if (body is not null) request.Content = new ByteArrayContent(body);
                AddHeaders(request, input.Headers);
                stop.Token.ThrowIfCancellationRequested();
                slot.MarkDispatched(); dispatched = true;
                observe?.Invoke(new(authority, "dispatched", throttled));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                var headers = response.Headers.Concat(response.Content.Headers)
                    .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.SelectMany(v => v.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
                if (headers.Sum(x => x.Key.Length + x.Value.Sum(y => y.Length)) > 65536)
                    throw new InternetAccessException("internet-response-headers-limit", "Response headers exceed the fixed ceiling.", true);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = new Uri(uri, location);
                    Current(authority, next, scope); // Also check returned redirects even when the caller does not follow.
                    if (redirects >= policy.EffectiveLimits.MaximumRedirects)
                        throw new InternetAccessException("internet-redirect-limit", "The current redirect ceiling was reached.", true);
                    if (input.FollowRedirects && response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                        HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                    {
                        if (method is not ("GET" or "HEAD"))
                            throw new InternetAccessException("internet-redirect-method-denied", "Automatic redirects are limited to GET and HEAD.", true);
                        uri = next; continue; // The next loop re-acquires rate/slots and rechecks current authority.
                    }
                }
                if (!input.ReturnPartialBody && response.Content.Headers.ContentLength > maximum)
                    throw new InternetAccessException("internet-response-byte-limit", "The response exceeds the current body-byte ceiling.", true);
                await using var stream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[Math.Min(16384, maximum + 1)];
                int read; var truncated = false;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum - output.Length + 1)),
                    stop.Token).ConfigureAwait(false)) != 0)
                {
                    var current = Current(authority, uri, scope);
                    if (current.GlobalRevision != policy.GlobalRevision || current.MainRevision != policy.MainRevision)
                        throw new InternetAccessException("internet-policy-changed", "The Internet policy changed during response consumption.", true);
                    if (output.Length + read > maximum)
                    {
                        if (!input.ReturnPartialBody) throw new InternetAccessException("internet-response-byte-limit", "The response reached its current body-byte ceiling.", true);
                        output.Write(buffer, 0, maximum - (int)output.Length); truncated = true; break;
                    }
                    output.Write(buffer, 0, read);
                }
                observe?.Invoke(new(authority, "completed", throttled));
                return new((int)response.StatusCode, uri, headers, output.ToArray(),
                    redirects - input.RedirectCount, policy.GlobalRevision, policy.MainRevision, throttled, truncated);
            }
        }
        catch (InternetAccessException error)
        {
            throw new InternetAccessException(error.Code, error.Message, error.Dispatched || dispatched);
        }
        catch (OperationCanceledException)
        {
            observe?.Invoke(new(authority, "blocked", throttled, "internet-timeout-or-policy-changed"));
            throw new InternetAccessException(token.IsCancellationRequested ? "internet-cancelled" : "internet-timeout-or-policy-changed",
                dispatched ? "The managed request stopped after dispatch; its delivery may be uncertain. It was not replayed."
                    : "The managed request stopped before dispatch; no request was sent.", dispatched);
        }
        catch (HttpRequestException error)
        {
            for (Exception? cause = error; cause is not null; cause = cause.InnerException)
                if (cause is InternetAccessException rejected) throw new InternetAccessException(rejected.Code, rejected.Message, rejected.Dispatched);
            throw new InternetAccessException("internet-delivery-unknown",
                "The request did not return a complete response. Do not replay an uncertain send.", dispatched);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or IOException)
        {
            throw new InternetAccessException("internet-authority-unavailable", "The managed request stopped because current authority or response validation became unavailable.", dispatched);
        }
        finally { lock (sync) pending.Remove(stop); }
    }
    private static void AddHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string[]>? headers)
    {
        if (headers is null) return;
        if (headers.Count > 128 || headers.Sum(x => x.Key.Length + (x.Value?.Sum(y => y?.Length ?? 0) ?? 0)) > 65536)
            throw new InternetAccessException("internet-request-headers-limit", "Request headers exceed the fixed ceiling.");
        foreach (var (key, values) in headers)
        {
            if (values is null || key.Any(char.IsControl) || values.Any(x => x is null || x.Any(char.IsControl)) ||
                key.Equals("authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase))
                throw new InternetAccessException("internet-authentication-denied", "Managed Internet authentication credentials are unavailable.");
            if (key.Equals("host", StringComparison.OrdinalIgnoreCase) || key.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("connection", StringComparison.OrdinalIgnoreCase) || key.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(key, values))
            {
                if (request.Content is null || !request.Content.Headers.TryAddWithoutValidation(key, values))
                    throw new InternetAccessException("internet-header-invalid", "A forwarded request header is invalid.");
            }
        }
    }
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(AuthorityKey, out var authority))
            throw new InternetAccessException("internet-authority-missing", "The managed connection has no host authority.");
        if (!context.InitialRequestMessage.Options.TryGetValue(ScopeKey, out var scope))
            throw new InternetAccessException("internet-authority-missing", "The connection has no live policy scope.");
        Current(authority, context.InitialRequestMessage.RequestUri!, scope);
        // HTTP/1.1 fresh connections prevent pooled/HTTP2 stream retries from hiding sends.
        // A handler retry that asks for another connection fails before DNS/connect; explicit
        // provider retries must obtain a new broker admission and retain no-replay evidence.
        if (!context.InitialRequestMessage.Options.TryGetValue(AttemptKey, out var attempt) ||
            Interlocked.Increment(ref attempt.Count) != 1)
            throw new InternetAccessException("internet-transport-retry-denied", "An implicit transport retry was refused. Reconcile the existing request before retrying.");
        IPAddress[] addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal] : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Length is < 1 or > 16 || addresses.Any(x => !PublicAddress(x)))
            throw new InternetAccessException("internet-address-denied", "The destination resolves to a non-public or unsupported address.");
        foreach (var address in addresses)
        {
            Current(authority, context.InitialRequestMessage.RequestUri!, scope); token.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No permitted address accepted the connection.");
    }
    internal static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99) ||
                b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) ||
                b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Conservative global-unicast only; exclude translation, documentation and special-purpose prefixes.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8) ||
              b[0] == 0x3f && (b[1] & 0xf0) == 0xf0 || b[0] == 0x20 && b[1] == 0x02);
    }
    private async Task<Lease> AcquireAsync(ManagedInternetAuthority authority, Uri url, CancellationToken token, Func<ManagedInternetPolicyState>? scope)
    {
        var began = clock.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested(); var policy = Current(authority, url, scope);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!mains.TryGetValue(authority.MainConversationId, out var main))
                {
                    if (mains.Count >= 1024)
                    {
                        var expired = mains.FirstOrDefault(x => x.Value.Active == 0 &&
                            clock.GetElapsedTime(x.Value.Stamp).TotalMinutes >= 30);
                        if (expired.Key is null) throw new InternetAccessException("internet-main-capacity", "The bounded Main Internet scheduler is full.");
                        mains.Remove(expired.Key);
                    }
                    main = new() { Tokens = policy.EffectiveLimits.Burst, Stamp = clock.GetTimestamp() };
                    mains.Add(authority.MainConversationId, main);
                }
                if (!globalInitialized) { global.Tokens = policy.GlobalLimits.Burst; global.Stamp = clock.GetTimestamp(); globalInitialized = true; }
                Refill(global, policy.GlobalLimits); Refill(main, policy.EffectiveLimits);
                if (global.Active < policy.GlobalLimits.MaximumConcurrentRequests &&
                    main.Active < policy.EffectiveLimits.MaximumConcurrentRequests && global.Tokens >= 1 && main.Tokens >= 1)
                {
                    global.Tokens--; main.Tokens--; global.Active++; main.Active++;
                    return new(this, main, policy, (long)clock.GetElapsedTime(began).TotalMilliseconds);
                }
            }
            observe?.Invoke(new(authority, "throttled", (long)clock.GetElapsedTime(began).TotalMilliseconds));
            await Task.Delay(TimeSpan.FromMilliseconds(25), clock, token).ConfigureAwait(false);
        }
    }
    private void Refill(Bucket bucket, InternetLimits limits)
    {
        var now = clock.GetTimestamp();
        bucket.Tokens = Math.Min(limits.Burst, bucket.Tokens + clock.GetElapsedTime(bucket.Stamp, now).TotalSeconds * limits.RequestsPerSecond);
        bucket.Stamp = now;
    }
    private sealed class Lease(ManagedInternetBroker owner, Bucket main, ManagedInternetPolicyState policy,
        long waited) : IDisposable
    {
        private bool dispatched; private int ended;
        public long WaitedMilliseconds => waited;
        public void MarkDispatched() => dispatched = true;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            lock (owner.sync)
            {
                owner.global.Active--; main.Active--;
                if (!dispatched) { owner.global.Tokens = Math.Min(policy.GlobalLimits.Burst, owner.global.Tokens + 1);
                    main.Tokens = Math.Min(policy.EffectiveLimits.Burst, main.Tokens + 1); }
            }
        }
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        Invalidate(); client.Dispose();
    }
}
