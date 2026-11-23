using System.IO.Pipes;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

// Preserve Main's source/attach API while sharing native identity admission with Configuration.
internal static class DesktopControlIdentity
{
    internal static SemanticControlRequest Shared(DesktopControlRequest request) => new(request.InstanceId, request.RequestId, request.Operation, request.Arguments);
    internal static DesktopControlRequest ToMain(SemanticControlRequest request) => new(request.InstanceId, request.RequestId, request.Operation, request.Arguments);
    internal static SemanticControlResponse? Shared(DesktopControlResponse? response) => response is null ? null :
        new(response.RequestId, response.Operation, response.State, response.Data, response.Error);
    internal static DesktopControlResponse ToMain(SemanticControlResponse response) => new(response.RequestId, response.Operation, response.State, response.Data, response.Error);
    internal static SemanticControlEndpoint Shared(DesktopControlEndpoint endpoint) => new(
        endpoint.Product, endpoint.Protocol, endpoint.PipeName, endpoint.InstanceId, endpoint.ProcessId,
        endpoint.StartedAt, endpoint.ProcessStartUtcTicks, endpoint.SessionId);

    internal static DesktopControlEndpoint ToMain(SemanticControlEndpoint endpoint) => new(
        endpoint.Product, endpoint.Protocol, endpoint.PipeName, endpoint.InstanceId, endpoint.ProcessId,
        endpoint.StartedAt, endpoint.ProcessStartUtcTicks, endpoint.SessionId);

    internal static async Task<DesktopControlEndpoint> ReadEndpointAsync(string path, CancellationToken token) =>
        ToMain(await SemanticControlIdentity.ReadEndpointAsync(path, DesktopControlProtocol.Product,
            DesktopControlProtocol.PipePrefix, token));

    internal static DesktopControlEndpoint ParseEndpoint(byte[] bytes) =>
        ToMain(SemanticControlIdentity.ParseEndpoint(bytes, DesktopControlProtocol.Product, DesktopControlProtocol.PipePrefix));

    internal static void RequireServer(NamedPipeClientStream pipe, DesktopControlEndpoint endpoint) =>
        SemanticControlIdentity.RequireServer(pipe, Shared(endpoint), typeof(Program).Assembly.Location);

    internal static bool PeerMatches(DesktopControlEndpoint endpoint, InstalledWorkerProcessObservation peer,
        string[] argv, string? currentSid, int currentSession, string appHost, string? managedHost, string entry) =>
        SemanticControlIdentity.PeerMatches(Shared(endpoint), peer, argv, currentSid, currentSession, appHost, managedHost, entry);
}
