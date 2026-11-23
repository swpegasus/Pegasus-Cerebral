using System.Text.Json;

namespace Cerebral.Engine.Contracts;

/// <summary>Data-only strategy boundary. The host owns admission, effects, resources and durable state.</summary>
public static class EngineProtocol
{
    public const string Id = "pegasus.cerebral.engine";
    public const int Version = 1;
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public const string StatelessFormat = "stateless/1";
}

public sealed record EngineWorkSupport(string InputContract, int InputVersion, string OutputContract, int OutputVersion);
public sealed record EngineDescriptor(string Id, string Version, int ProtocolVersion, string StateFormat,
    IReadOnlyList<EngineWorkSupport> Work);
public sealed record EngineConfiguration(string Fingerprint, IReadOnlyDictionary<string, string> Values);

/// <summary>Issued by the host. OperationId binds the exact immutable input to scoped feature evaluation.</summary>
public sealed record EngineRequest(string OperationId, string WorkId, long ExpectedRevision,
    long ActivationEpoch, string ConfigurationFingerprint, string Contract, int Version, JsonElement Payload);
public sealed record EngineDecision(string OperationId, string WorkId, long ExpectedRevision,
    long ActivationEpoch, string ConfigurationFingerprint, string Contract, int Version, JsonElement Payload);

/// <summary>Only evaluates a pure feature for the exact request currently issued by this host.
/// This is neither a tool executor nor a capability grant.</summary>
public interface IEngineFeatureHost
{
    Task<JsonElement> EvaluateFeatureAsync(EngineRequest request, CancellationToken cancellationToken);
}

/// <summary>Trusted, cooperative managed strategy. Mutable plugin/project data never belongs in its code slot.</summary>
public interface IEnginePlugin : IAsyncDisposable
{
    EngineDescriptor Descriptor { get; }
    Task InitializeAsync(IEngineFeatureHost host, EngineConfiguration configuration, CancellationToken cancellationToken);
    Task CheckHealthAsync(CancellationToken cancellationToken);
    Task<EngineDecision> EvaluateAsync(EngineRequest request, CancellationToken cancellationToken);
}

/// <summary>A failure of the host feature, distinct from a malfunction of the selected strategy.</summary>
public sealed class EngineFeatureFailureException(string message) : Exception(message);
