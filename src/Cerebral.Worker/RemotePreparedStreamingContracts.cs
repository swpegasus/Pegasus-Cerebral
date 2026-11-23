using System.Text.Json.Serialization;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

// Worker-private HTTP extension. Shared prepared SDK interfaces and DTOs remain unchanged.
internal interface IRemotePreparedStreamingOwnerClient
{
    Task<RemotePreparedStreamCapabilities?> GetPreparedStreamCapabilitiesAsync(
        RemotePreparedStreamOwnerIdentity owner, CancellationToken cancellationToken);
    Task<RemoteGeneratedResponse> GenerateStreamingAsync(RemotePreparedStreamRequest request,
        Action<string>? onText, CancellationToken transportCancellationToken);
}

internal sealed record RemotePreparedStreamOwnerIdentity(
    string ExpectedWorkerInstallationId, string ExpectedControllerInstallationId)
{
    public int SchemaVersion { get; init; } = 1;
}

internal sealed record RemotePreparedStreamRequest(
    int SchemaVersion, string StreamId, string ExpectedWorkerInstallationId,
    string ExpectedControllerInstallationId, int ProtocolVersion, string LeaseId,
    string ExpectedMetadataSha256);

internal sealed record RemotePreparedStreamCapabilities(
    int SchemaVersion, string WorkerInstallationId, string ControllerInstallationId,
    string GeneratePath, string ContentType, IReadOnlyList<int> PreparedProtocolVersions,
    int MaximumRequestBytes, int MaximumStartFrameBytes, int MaximumTextFrameBytes,
    int MaximumTerminalFrameBytes, int MaximumStreamBytes, int MaximumFrames,
    int MaximumTextFragmentCharacters);

internal sealed record RemotePreparedStreamFrame(
    string Schema, string Kind, int Sequence, string StreamId,
    string WorkerInstallationId, string ControllerInstallationId,
    int ProtocolVersion, string LeaseId, string MetadataSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreparedModelMetadata? Metadata { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteGeneratedResponse? Response { get; init; }
}

internal static class RemotePreparedStreamingLimits
{
    internal const string Schema = "cerebral.prepared-stream/1";
    internal const int RequestBytes = 4096, StartBytes = 65536, TextBytes = 131072;
    internal const int TerminalBytes = RemotePreparedProtocol.MaximumResponseBytes + 4096;
    internal const int StreamBytes = 32 * 1024 * 1024, Frames = 4096, FragmentCharacters = 16384;
}
