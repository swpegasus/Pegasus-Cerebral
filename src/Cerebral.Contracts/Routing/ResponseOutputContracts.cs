using System.Collections.Immutable;

namespace Cerebral.Contracts.Routing;

/// <summary>Version and public bounds for response and committed-output hooks.</summary>
public static class ResponseOutputHookContract
{
    public const string Id = "pegasus.cerebral.response-output-hooks";
    public const int Version = 1;
    public const int MaximumContentParts = CerebralEvent.MaximumContentParts;
    public const int MaximumContentUtf8Bytes = 128 * 1024;
}

public enum OutputHookBoundary
{
    ResponseBeforeCommit = 0,
    OutputAfterCommit = 100
}

public sealed record OutputHookIdentity(string Id, string Version);

/// <summary>
/// One versioned hook in one fixed output boundary. Ordering dependencies may name only hooks in
/// the same boundary; the host snapshots descriptors before a turn runs.
/// </summary>
public sealed class OutputHookDescriptor
{
    public OutputHookDescriptor(string id, string version, OutputHookBoundary boundary,
        IEnumerable<string>? before = null, IEnumerable<string>? after = null, TimeSpan? timeout = null)
    {
        Id = id;
        Version = version;
        Boundary = boundary;
        Before = before is null ? [] : [.. before];
        After = after is null ? [] : [.. after];
        Timeout = timeout;
    }

    public string Id { get; }
    public string Version { get; }
    public OutputHookBoundary Boundary { get; }
    public ImmutableArray<string> Before { get; }
    public ImmutableArray<string> After { get; }
    public TimeSpan? Timeout { get; }
}

/// <summary>
/// Host-owned response input. A hook receives typed content and current presentation visibility,
/// without a model, tool dispatcher, service provider, caller identity override, or grant handle.
/// </summary>
public sealed record ResponseOutputRequest(string RequestId, string ConversationId, string TurnId,
    IReadOnlyList<ContentPart> Content, EventProjectionPolicy Projection);

/// <summary>
/// OriginalContent and OriginalProjection never change. Content and HumanVisibility are the
/// currently adopted presentation. Model inclusion and retention remain host-owned.
/// </summary>
public sealed record ResponseOutputSnapshot(
    string RequestId,
    string ConversationId,
    string TurnId,
    ImmutableArray<ContentPart> OriginalContent,
    ImmutableArray<ContentPart> Content,
    EventProjectionPolicy OriginalProjection,
    HumanVisibility HumanVisibility,
    ImmutableArray<OutputHookIdentity> InvokedHooks,
    bool Derived);

public sealed record ResponseOutputOutcome(RouteDisposition Disposition, ResponseOutputSnapshot Snapshot,
    OutputHookIdentity? StoppedByHook = null, string? Reason = null);

public sealed record ResponseOutputHookContext(ResponseOutputSnapshot Input);

public sealed class ResponseOutputHookResult
{
    private ResponseOutputHookResult(RouteDisposition disposition, IReadOnlyList<ContentPart>? replacementContent,
        HumanVisibility? humanVisibility, string? reason)
    {
        Disposition = disposition;
        ReplacementContent = replacementContent;
        HumanVisibility = humanVisibility;
        Reason = reason;
    }

    public RouteDisposition Disposition { get; }
    public IReadOnlyList<ContentPart>? ReplacementContent { get; }
    public HumanVisibility? HumanVisibility { get; }
    public string? Reason { get; }

    public static ResponseOutputHookResult Continue(IReadOnlyList<ContentPart>? replacementContent = null,
        HumanVisibility? humanVisibility = null) =>
        new(RouteDisposition.Continue, replacementContent, humanVisibility, null);

    public static ResponseOutputHookResult Hold(string reason) =>
        new(RouteDisposition.Hold, null, null, reason);

    public static ResponseOutputHookResult Reject(string reason) =>
        new(RouteDisposition.Reject, null, null, reason);
}

public interface IResponseOutputHook
{
    OutputHookDescriptor Descriptor { get; }
    ValueTask<ResponseOutputHookResult> InvokeAsync(ResponseOutputHookContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Exact committed events supplied after the transaction. SourceEvent retains the authoritative
/// agent result. PresentationEvent is either that event or its derived human projection.
/// </summary>
public sealed record CommittedOutputContext(string RequestId, CerebralEvent SourceEvent,
    CerebralEvent PresentationEvent, ImmutableArray<OutputHookIdentity> ResponseHooks);

public sealed record OutputObserverDiagnostic(string Code, string Message);

public sealed record OutputObserverResult(IReadOnlyList<OutputObserverDiagnostic> Diagnostics)
{
    public static OutputObserverResult Empty { get; } = new([]);
}

/// <summary>
/// Observes already committed output. It receives no execution handle and cannot replace either
/// committed event. Host-mediated later work must use an ordinary authorized, deduplicated command.
/// </summary>
public interface ICommittedOutputObserver
{
    OutputHookDescriptor Descriptor { get; }
    ValueTask<OutputObserverResult> ObserveAsync(CommittedOutputContext context, CancellationToken cancellationToken);
}

/// <summary>Optional service-package contribution collected atomically at Worker startup.</summary>
public interface IOutputHookModule
{
    IReadOnlyList<IResponseOutputHook> ResponseHooks { get; }
    IReadOnlyList<ICommittedOutputObserver> OutputObservers { get; }
}

public sealed record AttributedOutputDiagnostic(string RequestId, OutputHookIdentity Hook, string Code, string Message, bool Failure);

public sealed record OutputObservationReport(ImmutableArray<OutputHookIdentity> InvokedObservers,
    ImmutableArray<AttributedOutputDiagnostic> Diagnostics);

public sealed class ResponseOutputPipelineOptions
{
    public int MaximumHooksPerBoundary { get; init; } = 128;
    public int MaximumContentParts { get; init; } = ResponseOutputHookContract.MaximumContentParts;
    public int MaximumContentUtf8Bytes { get; init; } = ResponseOutputHookContract.MaximumContentUtf8Bytes;
    public int MaximumDiagnostics { get; init; } = 128;
    public int MaximumDiagnosticCharacters { get; init; } = 4_096;
    public int MaximumReasonCharacters { get; init; } = 4_096;
    public TimeSpan DefaultResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DefaultObserverTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumHookTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
