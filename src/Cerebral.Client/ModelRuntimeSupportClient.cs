using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Client;

/// <summary>Optional ordinary owner-management query. The supplied transport retains its
/// existing authority; this wrapper grants none and never selects another transport.</summary>
public sealed class ModelRuntimeSupportClient
{
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonElement>> read;
    private readonly string? workerId;
    private static readonly JsonElement Empty = EmptyArguments();

    private static JsonElement EmptyArguments()
    { using var document = JsonDocument.Parse("{}"); return document.RootElement.Clone(); }

    public ModelRuntimeSupportClient(CerebralClient client, string? workerId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        read = (operation, arguments, token) => client.GetAsync<JsonElement>(operation, arguments, cancellationToken: token);
        this.workerId = workerId;
    }

    // Also supports the existing verified remote owner-client seam and deterministic fixtures.
    public ModelRuntimeSupportClient(Func<string, JsonElement, CancellationToken, Task<JsonElement>> read,
        string? workerId = null)
    { this.read = read ?? throw new ArgumentNullException(nameof(read)); this.workerId = workerId; }

    public async Task<ModelRuntimeSupportQueryResponse> ReadAsync(ModelRuntimeSupportQueryRequest request,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var arguments = ModelRuntimeSupportQueryProtocol.WriteRequest(request, workerId);
        var operation = workerId is null ? ModelRuntimeSupportQueryProtocol.ReadOperation :
            ModelRuntimeSupportQueryProtocol.RemoteReadOperation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ModelRuntimeSupportQueryProtocol.Deadline);
        var discovery = await read("sdk.discover", Empty, deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        var supported = ModelRuntimeSupportQueryProtocol.SupportsOperation(discovery, operation);
        deadline.Token.ThrowIfCancellationRequested();
        if (!supported)
            return ModelRuntimeSupportQueryProtocol.Unavailable(request, "operation_unavailable");
        var encoded = await read(operation, arguments, deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        var response = ModelRuntimeSupportQueryProtocol.ReadResponse(encoded, request);
        ModelRuntimeSupportQueryProtocol.RequirePassiveQuerySemantics(response);
        deadline.Token.ThrowIfCancellationRequested();
        return response;
    }
}
