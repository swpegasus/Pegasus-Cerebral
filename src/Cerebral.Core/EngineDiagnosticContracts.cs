namespace Cerebral.Core;

/// <summary>Read-only diagnostic copy. Output text is a model proposal, never proof of a tool effect.
/// The host never supplies prepared prompts, private reasoning or credential sources here.
/// Visible output can contain proposed actions; consumers must bound and redact their local logs.</summary>
public sealed record EngineDiagnosticRecord(long Sequence, DateTimeOffset At, string ChatId, string RequestId,
    string? RunId, string EffectId, string AgentId, string WorkerId, string ModelId, string Phase,
    string? Text = null, bool TextTruncated = false, int? InputTokens = null, int? ResponseTokens = null,
    int? ReasoningTokens = null, int? ContextTokens = null, int? GeneratedTokens = null,
    long? ElapsedMilliseconds = null, string? Outcome = null);

/// <summary>Optional service-host seam. Reading grants no execution or context mutation capability.
/// The host owns bounded buffering and detaches subscriptions when the package is removed.</summary>
public interface IServiceEngineDiagnosticsHost
{
    IEngineDiagnosticSubscription OpenEngineDiagnostics();
}
public interface IEngineDiagnosticSubscription : IDisposable
{
    long DroppedRecords { get; }
    IAsyncEnumerable<EngineDiagnosticRecord> ReadAsync(CancellationToken cancellationToken = default);
}
