using System.Collections.Immutable;

namespace Cerebral.Contracts.Routing;

/// <summary>
/// Fixed request-routing stages. A hook may order itself only against another hook in the
/// same stage; stage order is owned by the engine.
/// </summary>
public enum RequestRouteStage
{
    Ingress = 0,
    Transform = 100,
    Context = 200,
    PreDispatch = 300
}

public enum RouteDisposition
{
    Continue,
    Hold,
    Reject
}

/// <summary>An immutable text request entering the routing pipeline.</summary>
public sealed record TextRouteRequest(string RequestId, string Text);

/// <summary>
/// A typed contribution made by a hook. Contributions are data only; they do not expose a
/// tool dispatcher, service provider, or another execution capability.
/// </summary>
public abstract record RouteContribution;

public sealed record ContextContribution(string Name, string Text) : RouteContribution;
public sealed record InstructionContribution(string Name, string Text) : RouteContribution;
public sealed record LabelContribution(string Name, string Value) : RouteContribution;
public sealed record DiagnosticContribution(string Code, string Message) : RouteContribution;

/// <summary>The engine supplies source attribution; hooks cannot impersonate another hook.</summary>
public sealed record AttributedRouteContribution(string HookId, RouteContribution Value);

/// <summary>
/// The immutable snapshot supplied to each hook. OriginalText is never replaced. Text is the
/// currently adopted text after earlier hooks.
/// </summary>
public sealed record TextRouteSnapshot(
    string RequestId,
    string OriginalText,
    string Text,
    ImmutableArray<AttributedRouteContribution> Contributions,
    ImmutableArray<string> InvokedHookIds);

public sealed record TextRouteOutcome(
    RouteDisposition Disposition,
    TextRouteSnapshot Snapshot,
    string? StoppedByHookId = null,
    string? Reason = null);

public sealed class RouteHookDescriptor
{
    public RouteHookDescriptor(
        string id,
        RequestRouteStage stage,
        IEnumerable<string>? before = null,
        IEnumerable<string>? after = null,
        TimeSpan? timeout = null)
    {
        Id = id;
        Stage = stage;
        Before = before is null ? [] : [.. before];
        After = after is null ? [] : [.. after];
        Timeout = timeout;
    }

    public string Id { get; }
    public RequestRouteStage Stage { get; }
    public ImmutableArray<string> Before { get; }
    public ImmutableArray<string> After { get; }
    public TimeSpan? Timeout { get; }
}

/// <summary>
/// The only context visible to a route hook. It intentionally contains no command, tool,
/// filesystem, model, or service execution handle.
/// </summary>
public sealed record RouteHookContext(RequestRouteStage Stage, TextRouteSnapshot Input);

public sealed class RouteHookResult
{
    private RouteHookResult(
        RouteDisposition disposition,
        string? replacementText,
        string? reason,
        IEnumerable<RouteContribution>? contributions)
    {
        Disposition = disposition;
        ReplacementText = replacementText;
        Reason = reason;
        Contributions = contributions is null ? [] : [.. contributions];
    }

    public RouteDisposition Disposition { get; }
    public string? ReplacementText { get; }
    public string? Reason { get; }
    public ImmutableArray<RouteContribution> Contributions { get; }

    public static RouteHookResult Continue(
        string? replacementText = null,
        IEnumerable<RouteContribution>? contributions = null) =>
        new(RouteDisposition.Continue, replacementText, null, contributions);

    public static RouteHookResult Hold(
        string reason,
        IEnumerable<RouteContribution>? contributions = null) =>
        new(RouteDisposition.Hold, null, reason, contributions);

    public static RouteHookResult Reject(
        string reason,
        IEnumerable<RouteContribution>? contributions = null) =>
        new(RouteDisposition.Reject, null, reason, contributions);
}

public interface IRequestRouteHook
{
    RouteHookDescriptor Descriptor { get; }
    ValueTask<RouteHookResult> InvokeAsync(RouteHookContext context, CancellationToken cancellationToken);
}

public sealed class RequestRoutePipelineOptions
{
    public int MaximumHooks { get; init; } = 128;
    public int MaximumTextCharacters { get; init; } = 1_000_000;
    public int MaximumContributions { get; init; } = 256;
    public int MaximumContributionCharacters { get; init; } = 32_768;
    public int MaximumReasonCharacters { get; init; } = 4_096;
    public TimeSpan DefaultHookTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumHookTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class RouteConfigurationException(string message) : InvalidOperationException(message);
