using System.Net;
using System.Net.Http.Headers;
using Cerebral.Core;
using Microsoft.Playwright;

namespace Cerebral.BrowserPlugin;

// Browser forwarding has no raw client or listening proxy. The host broker owns all actual sockets,
// live global/Main permission, revision checks, shared rate/concurrency and bounded response collection.
internal sealed class BrowserNetworkGate(BrowserSession job) : IDisposable
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int MaximumSessionBytes = 64 * 1024 * 1024;
    private readonly (CancellationTokenSource Source, CancellationToken Token) stopping = CreateStop();
    private static (CancellationTokenSource, CancellationToken) CreateStop()
    { var source = new CancellationTokenSource(); return (source, source.Token); }
    private int disposed;
    private readonly SemaphoreSlim slots = new(8, 8);
    private readonly object byteGate = new();
    private long reservedBytes;
    private readonly Dictionary<string, SemaphoreSlim> originSlots = new(StringComparer.OrdinalIgnoreCase);
    private int pending;
    private int requests;
    private long bytes;
    public long ResponseBytes => Interlocked.Read(ref bytes);

    public async Task RouteAsync(IRoute route)
    {
        var acquired = false;
        if (Interlocked.Increment(ref pending) > 32)
        { Interlocked.Decrement(ref pending); await DenyAsync(route, "network-queue-limit"); return; }
        try
        {
            // Capture before queuing: a delayed callback must never acquire a newer action's authority.
            var scope = job.RequireInternet();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, scope.Token);
            var token = linked.Token;
            await slots.WaitAsync(token); acquired = true;
            if (Interlocked.Increment(ref requests) > 512 || !job.Policy.Allows(route.Request.Url))
            { await DenyAsync(route, "outside-domain-or-request-limit"); return; }
            var page = route.Request.Frame.Page;
            if (!job.Tabs.Values.Any(tab => ReferenceEquals(tab.Page, page)))
            { await DenyAsync(route, "popup-denied"); return; }
            var body = route.Request.PostDataBuffer;
            if (body?.Length > 1024 * 1024)
            { await DenyAsync(route, "request-body-limit"); return; }
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in await route.Request.AllHeadersAsync())
            {
                if (header.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase))
                { await DenyAsync(route, "authentication-confirmation-required"); return; }
                if (header.Key.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)) continue;
                headers.Add(header.Key, [header.Value]);
            }
            var uri = new Uri(route.Request.Url, UriKind.Absolute);
            var response = await SendAsync(scope, new(route.Request.Method, uri, headers, body,
                MaximumResponseBytes, FollowRedirects: false, RedirectCount: RedirectCount(route.Request)), token);
            RequireResponse(response, uri);
            var delivered = ResponseHeaders(response);
            delivered.Remove("transfer-encoding"); delivered.Remove("connection"); delivered.Remove("content-length");
            if (route.Request.ResourceType == "document")
            {
                const string sandbox = "sandbox allow-scripts allow-forms allow-same-origin";
                delivered["Content-Security-Policy"] = delivered.TryGetValue("Content-Security-Policy", out var existing)
                    ? existing + ", " + sandbox : sandbox;
                delivered["Permissions-Policy"] = "clipboard-read=(), clipboard-write=(), camera=(), microphone=(), geolocation=()";
            }
            await route.FulfillAsync(new() { Status = response.StatusCode, Headers = delivered, BodyBytes = response.Body });
        }
        catch (InternetAccessException error) { try { await DenyAsync(route, error.Code); } catch (PlaywrightException) { } }
        catch (BrowserRejectedException error) { try { await DenyAsync(route, error.Code); } catch (PlaywrightException) { } }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or PlaywrightException or ArgumentException or FormatException)
        { try { await DenyAsync(route, "network-request-failed"); } catch (PlaywrightException) { } }
        finally { if (acquired) slots.Release(); Interlocked.Decrement(ref pending); }
    }

    internal async Task<object> RequestPageAsync(BrowserPageRequestArguments input, CancellationToken cancellationToken)
    {
        var scope = job.RequireInternet();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken, scope.Token);
        var token = linked.Token; var dispatched = false; var sendAttempted = false; var acquired = false;
        if (Interlocked.Increment(ref pending) > 32)
        { Interlocked.Decrement(ref pending); throw new BrowserPageRequestException("network-queue-limit", "The bounded network queue is full.", false); }
        try
        {
            await slots.WaitAsync(token); acquired = true;
            var uri = new Uri(input.Url, UriKind.Absolute);
            for (var redirects = 0; ; redirects++)
            {
                token.ThrowIfCancellationRequested();
                job.Policy.RequireUrl(uri.AbsoluteUri);
                if (Interlocked.Increment(ref requests) > 512)
                    throw new BrowserRejectedException("request-limit", "This session reached its bounded request limit.");
                sendAttempted = true;
                var response = await SendAsync(scope, new("GET", uri,
                    new Dictionary<string, string[]> { ["Accept-Encoding"] = ["identity"] },
                    MaximumResponseBytes: input.MaximumBytes, FollowRedirects: false, RedirectCount: redirects), token);
                dispatched = true; sendAttempted = false;
                RequireResponse(response, uri);
                if (response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects >= 5 || Header(response, "Location") is not { } location)
                        throw new BrowserRejectedException("redirect-limit", "The page redirect chain is unavailable or exceeds five redirects.");
                    uri = new Uri(uri, location);
                    job.Policy.RequireUrl(uri.AbsoluteUri);
                    continue;
                }
                if (Header(response, "Content-Encoding") is { } encodingHeader &&
                    !encodingHeader.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    throw new BrowserRejectedException("unsupported-content-encoding", "Only uncompressed page responses are supported.");
                var type = Header(response, "Content-Type");
                MediaTypeHeaderValue? parsed = null;
                if (type is not null && !MediaTypeHeaderValue.TryParse(type, out parsed))
                    throw new BrowserRejectedException("unsupported-media-type", "The response media type is invalid.");
                var media = parsed?.MediaType;
                if (response.StatusCode is not (204 or 304) && !BrowserPageText.AllowsMedia(media))
                    throw new BrowserRejectedException("unsupported-media-type", "Only bounded text, HTML, JSON and XML response sources are supported.");
                var encoding = BrowserPageText.EncodingFor(parsed?.CharSet);
                var source = encoding.GetString(response.Body);
                if (source.Length > 0 && source[0] == '\uFEFF') source = source[1..];
                var text = BrowserPageText.Clip(source, input.MaximumTextBytes, out var truncated);
                return new { requestId = input.RequestId, requestSessionId = job.RequestSessionId, tabId = input.TabId,
                    url = BrowserProjection.PublicUrl(uri.AbsoluteUri), statusCode = response.StatusCode,
                    mediaType = media, representation = "http-source", encoding = encoding.WebName,
                    receivedBytes = response.Body.Length, returnedUtf8Bytes = System.Text.Encoding.UTF8.GetByteCount(text),
                    text, truncated, redirects, javascriptExecuted = false, contentTrust = "untrusted-page-data",
                    contentType = media, bodyAvailable = true, receiptBodyRetained = true };
            }
        }
        catch (InternetAccessException error)
        { throw new BrowserPageRequestException(error.Code, error.Message, dispatched || error.Dispatched); }
        catch (BrowserRejectedException error)
        { throw new BrowserPageRequestException(error.Code, error.Message, dispatched); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ArgumentException or FormatException)
        { throw new BrowserPageRequestException("request-outcome-unknown", "The page request did not return a complete response. Use its same request ID to read the retained outcome.", dispatched || sendAttempted); }
        finally { if (acquired) slots.Release(); Interlocked.Decrement(ref pending); }
    }

    private async Task<ManagedInternetResponse> SendAsync(BrowserInternetScope scope, ManagedInternetRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(job.Policy.NavigationTimeoutSeconds));
        SemaphoreSlim origin;
        lock (byteGate)
        {
            var key = request.Url.GetLeftPart(UriPartial.Authority);
            if (!originSlots.TryGetValue(key, out origin!))
            {
                if (originSlots.Count >= 512)
                    throw new BrowserRejectedException("request-limit", "The bounded origin connection capacity is full.");
                origin = new(4, 4); originSlots.Add(key, origin);
            }
        }
        // Preserve the former four connections per origin and provider navigation timeout even
        // when the user's central policy permits a larger shared ceiling.
        await origin.WaitAsync(deadline.Token);
        try
        {
            var maximum = Math.Min(request.MaximumResponseBytes ?? MaximumResponseBytes, MaximumResponseBytes);
            lock (byteGate)
            {
                maximum = (int)Math.Min(maximum, MaximumSessionBytes - bytes - reservedBytes);
                if (maximum <= 0) throw new BrowserRejectedException("response-byte-limit", "This session reached its bounded byte ceiling.");
                reservedBytes += maximum;
            }
            var charged = 0;
            try
            {
                var response = await scope.SendAsync(request with { MaximumResponseBytes = maximum }, deadline.Token);
                charged = response.Truncated ? maximum : Math.Min(response.Body.Length, maximum);
                if (response.Truncated || response.Body.Length > maximum)
                    throw new BrowserRejectedException("response-byte-limit", "The response exceeds the admitted byte ceiling.");
                return response;
            }
            catch (InternetAccessException error) { if (error.Dispatched) charged = maximum; throw; }
            catch (BrowserRejectedException) { throw; }
            catch { charged = maximum; throw; }
            finally
            {
                // Reserve before dispatch because buffered results cannot charge a streaming counter.
                // An unknown dispatched outcome consumes its whole reservation.
                lock (byteGate) { reservedBytes -= maximum; Interlocked.Add(ref bytes, charged); }
            }
        }
        finally { origin.Release(); }
    }

    private void RequireResponse(ManagedInternetResponse response, Uri requested)
    {
        if (!requested.Equals(response.FinalUrl))
            throw new BrowserRejectedException("managed-internet-response-mismatch", "The broker followed an unexpected redirect.");
        if (response.StatusCode is 401 or 407)
            throw new BrowserRejectedException("authentication-confirmation-required", "Browser authentication is unavailable.");
        if (Header(response, "Content-Disposition") is { } disposition)
        {
            if (!ContentDispositionHeaderValue.TryParse(disposition, out var parsed) ||
                parsed.DispositionType.Equals("attachment", StringComparison.OrdinalIgnoreCase))
                throw new BrowserRejectedException("download-confirmation-required", "Browser attachments are unavailable.");
        }
        if (response.StatusCode is >= 300 and < 400 && Header(response, "Location") is { } location)
            job.Policy.RequireUrl(new Uri(requested, location).AbsoluteUri);
    }

    private static string? Header(ManagedInternetResponse response, string name)
    {
        var values = response.Headers.Where(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value).ToArray();
        if (values.Length == 0) return null;
        if (values.Length != 1)
            throw new BrowserRejectedException("invalid-response-headers", "The response contains an ambiguous security header.");
        return values[0];
    }

    private static Dictionary<string, string> ResponseHeaders(ManagedInternetResponse response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            headers.Add(header.Key, string.Join(header.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", header.Value));
        return headers;
    }

    private static int RedirectCount(IRequest request)
    {
        var seen = new HashSet<IRequest>(ReferenceEqualityComparer.Instance); var count = 0;
        for (var prior = request.RedirectedFrom; prior is not null; prior = prior.RedirectedFrom)
        {
            if (!seen.Add(prior) || ++count > 10)
                throw new BrowserRejectedException("redirect-limit", "The native redirect chain is invalid or exceeds the managed redirect ceiling.");
        }
        return count;
    }

    private async Task DenyAsync(IRoute route, string code)
    { Interlocked.Increment(ref job.BlockedRequests); job.LastGate = code; await route.AbortAsync("blockedbyclient"); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { stopping.Source.Cancel(); } finally { stopping.Source.Dispose(); }
    }
}
