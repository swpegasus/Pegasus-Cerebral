using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private static readonly IReadOnlyList<WorkerWorkflows.OperationDescriptor> RuntimeSupportOperations =
    [
        new(ModelRuntimeSupportQueryProtocol.ReadOperation, ModelRuntimeSupportQueryProtocol.Version,
            "Read passive support only for an existing loaded native registration. No load, registration, fit or lease; unavailable does not mean unloaded.",
            RuntimeSupportRequestSchema(), OwnerOnly: true),
        new(ModelRuntimeSupportQueryProtocol.RemoteReadOperation, ModelRuntimeSupportQueryProtocol.Version,
            "Read passive loaded-native support through this coordinator's verified assigned Worker owner route, with independent target version discovery.",
            CerebralJson.Element(new { type = "object", additionalProperties = false,
                required = new[] { "workerId", "request" }, properties = new {
                    workerId = new { type = "string", minLength = 1, maxLength = 128 },
                    request = RuntimeSupportRequestSchema() } }), OwnerOnly: true)
    ];

    private static JsonElement RuntimeSupportRequestSchema() => CerebralJson.Element(new
    {
        type = "object", additionalProperties = false,
        required = new[] { "version", "modelId", "expectedWorkerInstallationId" },
        properties = new
        {
            version = new { type = "integer", @const = ModelRuntimeSupportQueryProtocol.Version },
            modelId = new { type = "string", minLength = 1, maxLength = 128 },
            expectedWorkerInstallationId = new { type = "string", minLength = 1, maxLength = 128 },
            // The full optional binding carries scope and digest together. The closed typed
            // codec validates each field; neither caller coordinates nor discovery grant authority.
            expectedBinding = new { type = new[] { "object", "null" }, additionalProperties = false,
                required = new[] { "workerInstallationId", "backendId", "adapterId", "adapterVersion", "modelId",
                    "configurationScopeId", "configurationSha256", "instanceId" },
                properties = new Dictionary<string, object>
                {
                    ["workerInstallationId"] = IdentifierSchema(), ["backendId"] = IdentifierSchema(),
                    ["adapterId"] = IdentifierSchema(), ["adapterVersion"] = IdentifierSchema(),
                    ["modelId"] = IdentifierSchema(), ["instanceId"] = IdentifierSchema(),
                    ["configurationScopeId"] = new { type = "string", minLength = 3, maxLength = 128 },
                    ["configurationSha256"] = new { type = "string", pattern = "^[0-9a-f]{64}$" }
                } }
        }
    });

    private static object IdentifierSchema() => new { type = "string", minLength = 1, maxLength = 128,
        pattern = "^[A-Za-z0-9._:-]+$" };

    private async Task<JsonElement> ReadNativeRuntimeSupportAsync(JsonElement args, CancellationToken token)
    {
        var request = ModelRuntimeSupportQueryProtocol.ReadRequest(args);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ModelRuntimeSupportQueryProtocol.Deadline);
        var ct = deadline.Token;
        ct.ThrowIfCancellationRequested();
        if (configuration.Get().InstallationId != request.ExpectedWorkerInstallationId)
        {
            var unavailable = ModelRuntimeSupportQueryProtocol.WriteResponse(
                ModelRuntimeSupportQueryProtocol.Unavailable(request, "identity_changed"), request);
            ct.ThrowIfCancellationRequested();
            return unavailable;
        }

        // Deliberately reuse only the passive reader. It neither creates a registration nor
        // acquires inference ownership. Null deliberately reveals no cause or model-state claim.
        var snapshot = await runtime.ReadLoadedNativeRuntimeSupportAsync(request.ModelId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ModelRuntimeSupportQueryResponse result;
        if (configuration.Get().InstallationId != request.ExpectedWorkerInstallationId)
            result = ModelRuntimeSupportQueryProtocol.Unavailable(request, "identity_changed");
        else if (snapshot is null)
            result = ModelRuntimeSupportQueryProtocol.Unavailable(request, "not_reported",
                request.ExpectedWorkerInstallationId, DateTimeOffset.UtcNow);
        else if (request.ExpectedBinding is { } expected && snapshot.Descriptor.Binding != expected)
            result = ModelRuntimeSupportQueryProtocol.Unavailable(request, "binding_changed",
                request.ExpectedWorkerInstallationId, DateTimeOffset.UtcNow);
        else
            result = new(ModelRuntimeSupportQueryProtocol.Version, request.ModelId,
                request.ExpectedWorkerInstallationId, DateTimeOffset.UtcNow, "available", null,
                snapshot.Descriptor, snapshot.ExpandedPathSupported);
        ModelRuntimeSupportQueryProtocol.RequirePassiveQuerySemantics(result);
        ct.ThrowIfCancellationRequested();
        var encoded = ModelRuntimeSupportQueryProtocol.WriteResponse(result, request);
        ct.ThrowIfCancellationRequested();
        return encoded;
    }
}
