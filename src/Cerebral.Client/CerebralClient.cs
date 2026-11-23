using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Client;

public sealed class CerebralClient : IDisposable
{
    // Prepared native results are capped at 4 MiB. Leave equal headroom for the command envelope
    // while preventing an explicit owner connection from buffering an unbounded malformed response.
    private const long OwnerCommandResponseBufferBytes = 8L * 1024 * 1024;
    private readonly HttpClient? http;
    private readonly LocalWorkerClient? local;
    private readonly string commandPath = "api/command";
    public bool IsLocalPipe => local is not null;
    public CerebralClient(LocalWorkerEndpoint endpoint) => local = new(endpoint);
    public CerebralClient(LocalWorkerExpectedOwner expectedOwner) => local = new(expectedOwner);

    internal bool HasExpectedLocalOwner => local?.HasExpectedOwner == true;
    private CerebralClient(LocalWorkerClient local) => this.local = local;
    public static CerebralClient ForInstalledWorker() => new(LocalWorkerClient.ForInstalledWorker());
    public static CerebralClient ForInstalledUserWorker(LocalWorkerExpectedOwner expectedOwner) =>
        new(LocalWorkerClient.ForInstalledUserWorker(expectedOwner));
    public static CerebralClient ForOwner(WorkerRegistration worker)
    {
        var url = ReadyWorkerUrl(worker);
        if (string.IsNullOrWhiteSpace(worker.AccessKey))
            throw new InvalidOperationException("Worker registration does not contain an owner access key. No request was sent.");
        return new(url, worker.AccessKey, peer: false, responseBufferBytes: OwnerCommandResponseBufferBytes);
    }
    public static CerebralClient ForPeer(WorkerRegistration worker)
    {
        var url = ReadyWorkerUrl(worker);
        if (string.IsNullOrWhiteSpace(worker.PeerToken))
            throw new InvalidOperationException("Worker registration does not contain a peer token. No request was sent.");
        return new(url, worker.PeerToken, peer: true);
    }
    public CerebralClient(WorkerRegistration worker) : this(ReadyWorkerUrl(worker),
        string.IsNullOrWhiteSpace(worker.PeerToken) ? worker.AccessKey : worker.PeerToken,
        !string.IsNullOrWhiteSpace(worker.PeerToken)) { }
    private static string ReadyWorkerUrl(WorkerRegistration worker)
    {
        PortableRegistrationPolicy.RequireReady(worker);
        return worker.Url;
    }
    public CerebralClient(string url = CerebralDefaults.WorkerUrl, string? accessKey = null, bool peer = false) :
        this(url, accessKey, peer, responseBufferBytes: null) { }
    private CerebralClient(string url, string? accessKey, bool peer, long? responseBufferBytes)
    {
        // Operations have different lifetimes (a model transfer may take hours). Callers and the
        // service bound each operation; a blanket client timeout would abort valid slow transfers.
        // A worker redirect must not forward the installation key or an uploaded model to another host.
        http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        if (responseBufferBytes is { } bufferBytes) http.MaxResponseContentBufferSize = bufferBytes;
        if (!string.IsNullOrWhiteSpace(accessKey)) http.DefaultRequestHeaders.Add(peer ? "X-Cerebral-Peer" : "X-Cerebral-Key", accessKey);
        commandPath = peer ? "api/peer/command" : "api/command";
    }
    public async Task<CommandResult> CommandAsync(string operation, object? arguments = null, string? profileId = null, CancellationToken cancellationToken = default)
    {
        var request = new CommandRequest(operation, CerebralJson.Element(arguments ?? new { }), profileId);
        if (local is not null) return await local.CommandAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await http!.PostAsJsonAsync(commandPath, request, CerebralJson.Options, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CommandResult>(CerebralJson.Options, cancellationToken) ?? throw new IOException("Service returned an empty result.");
    }
    public async Task<T> GetAsync<T>(string operation, object? arguments = null, string? profileId = null, CancellationToken cancellationToken = default)
    {
        var result = await CommandAsync(operation, arguments, profileId, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        return CerebralJson.Read<T>(result.Data);
    }
    /// <summary>Reads the source as the caller's user and sends its bytes; the service need not access that user's private folders.</summary>
    public async Task<ModelRecord> UploadModelAsync(string path, string name, string? license = null, CancellationToken cancellationToken = default)
    {
        if (local is not null)
            throw new NotSupportedException("Model file upload is not implemented for the local pipe transport. Use a supported local model registration/import command, or explicitly configure an HTTP connection for upload. No file was opened and no transport fallback was attempted.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(8));
        var id = Guid.NewGuid().ToString("N");
        var metadata = JsonSerializer.Serialize(new { id, name, license }, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false });
        await using var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/models/upload?metadata=" + Uri.EscapeDataString(metadata));
        request.Content = new StreamContent(input); request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        try
        {
            using var response = await http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CommandResult>(CerebralJson.Options, timeout.Token).ConfigureAwait(false) ?? throw new IOException("The service returned no upload result.");
            if (!result.Success) throw new IOException(result.Error ?? "Model upload failed.");
            return CerebralJson.Read<ModelRecord>(result.Data);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new IOException($"Model upload could not be confirmed ({id}). Refresh the model list before retrying; the service may already have accepted the file. {ex.Message}", ex);
        }
    }
    public async IAsyncEnumerable<ServiceEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (local is not null)
        {
            await foreach (var item in local.EventsAsync(cancellationToken).ConfigureAwait(false)) yield return item;
            yield break;
        }
        using var response = await http!.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) yield break;
            if (line?.StartsWith("data: ", StringComparison.Ordinal) == true)
            {
                var item = JsonSerializer.Deserialize<ServiceEvent>(line[6..], CerebralJson.Options);
                if (item is not null) yield return item;
            }
        }
    }
    public void Dispose() { local?.Dispose(); http?.Dispose(); }
}
