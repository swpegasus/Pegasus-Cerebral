using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

/// <summary>Optional owner HTTP transport. Discovery precedes lease consumption; a dispatched stream is never retried.</summary>
internal sealed class RemotePreparedStreamingOwnerClient : IRemotePreparedStreamingOwnerClient, IDisposable
{
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };

    internal RemotePreparedStreamingOwnerClient(WorkerRegistration worker, HttpMessageHandler? fixtureHandler = null)
    {
        PortableRegistrationPolicy.RequireReady(worker);
        if (worker.Url.Length > 4096 || !Uri.TryCreate(worker.Url, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Prepared streaming requires the approved bounded owner endpoint.");
        if (string.IsNullOrWhiteSpace(worker.AccessKey) || worker.AccessKey.Length is < 24 or > 512 ||
            worker.AccessKey.Any(char.IsWhiteSpace) || worker.AccessKey.Any(char.IsControl))
            throw new UnauthorizedAccessException("Prepared streaming requires the installation owner key.");
        client = new(fixtureHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri(worker.Url.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("X-Cerebral-Key", worker.AccessKey);
    }

    public async Task<RemotePreparedStreamCapabilities?> GetPreparedStreamCapabilitiesAsync(
        RemotePreparedStreamOwnerIdentity owner, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = Request(OwnerPreparedStreamingHttp.CapabilitiesPath, owner);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        RequireSuccess(response);
        RequireContentType(response, "application/json");
        var body = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false),
            OwnerPreparedStreamingHttp.MaximumCapabilitiesBytes, deadline.Token).ConfigureAwait(false);
        return RemotePreparedStreamingCodec.ParseCapabilities(body, owner);
    }

    public async Task<RemoteGeneratedResponse> GenerateStreamingAsync(RemotePreparedStreamRequest input,
        Action<string>? onText, CancellationToken transportCancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(transportCancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(11));
        using var request = Request(OwnerPreparedStreamingHttp.GeneratePath, input);
        // The adapter supplies an independent transport lifetime. Its caller-cancel/release
        // lane continues reading terminal evidence; a lost response leaves route uncertainty.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        RequireSuccess(response);
        RequireContentType(response, "application/x-ndjson");
        if (response.Content.Headers.ContentLength is > OwnerPreparedStreamingHttp.MaximumStreamBytes)
            throw new RemoteModelProtocolException("Prepared stream exceeded its transport bound; terminal evidence is unconfirmed.");
        await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        return await RemotePreparedStreamingCodec.ReadAsync(body, input, onText, deadline.Token).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(string path, object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (body.Length > OwnerPreparedStreamingHttp.MaximumRequestBytes)
            throw new RemoteModelProtocolException("Prepared stream request exceeded its bound; no request was sent.");
        var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/')) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        return request;
    }

    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Prepared stream owner authentication or assignment was refused.");
        if (!response.IsSuccessStatusCode)
            throw new RemoteModelProtocolException("Prepared stream transport was refused; it was not replayed.");
    }

    private static void RequireContentType(HttpResponseMessage response, string expected)
    {
        var content = response.Content.Headers;
        var type = content.ContentType;
        if (type is null || !string.Equals(type.MediaType, expected, StringComparison.OrdinalIgnoreCase) ||
            type.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase) ||
            content.ContentEncoding.Count != 0)
            throw new RemoteModelProtocolException("Prepared stream returned an unsupported content encoding.");
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream input, int maximum, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[Math.Min(maximum + 1, 8192)];
        while (true)
        {
            var remaining = maximum + 1 - (int)result.Length;
            if (remaining == 0) throw new RemoteModelProtocolException("Prepared stream envelope exceeded its byte bound.");
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) return result.ToArray();
            result.Write(buffer, 0, read);
            if (result.Length > maximum) throw new RemoteModelProtocolException("Prepared stream envelope exceeded its byte bound.");
        }
    }

    public void Dispose() => client.Dispose();
}
