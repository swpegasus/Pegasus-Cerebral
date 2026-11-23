namespace Cerebral.Core;

/// <summary>Application-managed Internet authority. This does not sandbox arbitrary plugin or child-process sockets.</summary>
public static class InternetAuthorityContract
{
    public const string Id = "pegasus.cerebral.managed-internet";
    public const int Version = 1;
    public const string Scope = "managed-web-tools";
    public static IReadOnlyList<string> ManagedToolIds { get; } = Array.AsReadOnly<string>(
        ["http.fetch", "browser.request", "browser.open", "browser.navigate", "browser.click", "browser.fill"]);
}
public sealed record InternetLimits(
    double RequestsPerSecond = 2, int Burst = 4, int MaximumConcurrentRequests = 4,
    int RequestTimeoutSeconds = 30, int MaximumResponseBytes = 8388608, int MaximumRedirects = 5,
    IReadOnlyList<string>? AllowedDomains = null, IReadOnlyList<string>? BlockedDomains = null);
public sealed record InternetToolRule(string ToolId, string DescriptorFingerprint);
public sealed record InternetToolCatalogItem(string ToolId, string Capability, string DescriptorFingerprint, string Source);
public sealed record InternetGlobalPolicy(int SchemaVersion, string WorkerInstallationId, long Revision,
    string Mode, InternetLimits Limits, IReadOnlyList<InternetToolRule> AllowedTools,
    DateTimeOffset? UpdatedAt, string Provenance);
public sealed record InternetMainPolicy(int SchemaVersion, string WorkerInstallationId, string MainConversationId,
    long Revision, string Choice, InternetLimits? Limits, IReadOnlyList<string>? AllowedTools,
    long? ApprovedGlobalRevision, string? ApprovedGlobalFingerprint, DateTimeOffset? UpdatedAt, string Provenance);
public sealed record InternetGlobalSnapshot(string ContractId, int SchemaVersion, InternetGlobalPolicy Policy,
    string PolicyFingerprint, string CatalogGeneration, IReadOnlyList<InternetToolCatalogItem> ManagedTools);
public sealed record InternetMainSnapshot(string ContractId, int SchemaVersion, string WorkerInstallationId,
    string MainConversationId, long RootRevision, InternetGlobalPolicy GlobalPolicy,
    string GlobalPolicyFingerprint, InternetMainPolicy Policy, string EffectiveState,
    string? BlockedReason, InternetLimits EffectiveLimits, IReadOnlyList<InternetToolRule> EffectiveTools,
    string CatalogGeneration, IReadOnlyList<InternetToolCatalogItem> ManagedTools);
public sealed record InternetGlobalSaveRequest(long ExpectedRevision, string ExpectedCatalogGeneration,
    string Mode, InternetLimits Limits, IReadOnlyList<InternetToolRule> AllowedTools);
public sealed record InternetMainSaveRequest(string MainConversationId, long ExpectedRevision,
    long ExpectedGlobalRevision, string ExpectedGlobalFingerprint, long ExpectedRootRevision,
    string ExpectedCatalogGeneration, string Choice, InternetLimits? Limits, IReadOnlyList<string>? AllowedTools);
public sealed record InternetApprovalRequest(int SchemaVersion, string RequestId, long Revision,
    string WorkerInstallationId, string MainConversationId, string? ContextId, string ConversationId,
    string RunId, string EffectId, string ToolName, string ToolFingerprint, string ArgumentsSha256,
    long RootRevision, long GlobalRevision, string GlobalPolicyFingerprint, long MainRevision,
    string RequestDigest, string Scope, InternetLimits RequestedLimits, IReadOnlyList<InternetToolRule> RequestedTools,
    string? DestinationOrigin, string Status, DateTimeOffset CreatedAt, string? DecisionId = null,
    bool? Allowed = null, string? QuestionSetId = null, long? QuestionRevision = null,
    string ResumeState = "not-ready", string? QuestionRunId = null);
public sealed record InternetRequestPage(string ContractId, int SchemaVersion, string MainConversationId,
    IReadOnlyList<InternetApprovalRequest> Requests, string? NextAfterRequestId);
public sealed record InternetDecisionRequest(string RequestId, long ExpectedRequestRevision, string RequestDigest,
    long ExpectedGlobalRevision, string ExpectedGlobalFingerprint, long ExpectedMainRevision, long ExpectedRootRevision,
    string QuestionSetId, long ExpectedQuestionRevision, bool Allowed, string DecisionId);
public sealed record InternetOwnerAction(int SchemaVersion, string Kind, string ApprovalRequestId,
    string RequestDigest, string SourceRunId, string EffectId, string ToolName, string ArgumentsSha256, bool NoEffect);
public sealed record ManagedInternetAuthority(int SchemaVersion, string WorkerInstallationId,
    string MainConversationId, string? ContextId, string ConversationId, string RunId,
    string EffectId, string ToolName, string ToolFingerprint);
/// <summary>One actual forwarded HTTP attempt; redirects in an intercepted browser request carry their native chain count.
/// Headers and bodies remain subject to the provider's stricter checks. Broker never supplies credentials/cookies.</summary>
public sealed record ManagedInternetRequest(string Method, Uri Url, IReadOnlyDictionary<string, string[]>? Headers = null,
    byte[]? Body = null, int? MaximumResponseBytes = null, bool FollowRedirects = false, int RedirectCount = 0, bool ReturnPartialBody = false);
public sealed record ManagedInternetResponse(int StatusCode, Uri FinalUrl, IReadOnlyDictionary<string, string[]> Headers,
    byte[] Body, int Redirects, long GlobalRevision, long MainRevision, long ThrottledMilliseconds, bool Truncated = false);
/// <summary>Host-created capability, never accepted from model arguments. Implementations recheck live authority on every send.
/// No instance means managed networking is unavailable. Local identity-validated IPC uses separate transports.</summary>
public interface IManagedInternetSession
{
    int Version { get; }
    ManagedInternetAuthority Authority { get; }
    Task<ManagedInternetResponse> SendAsync(ManagedInternetRequest request, CancellationToken cancellationToken);
}
public sealed class InternetAccessException(string code, string message, bool dispatched = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Dispatched { get; } = dispatched;
}

/// <summary>Opt-in marker for the host-gated Internet tool provider; absence is unavailable, never a raw-network fallback.</summary>
public interface IManagedInternetToolProvider
{
    int ManagedInternetVersion { get; }
}

public sealed record InternetCapabilities(string ContractId, int SchemaVersion, string Scope,
    IReadOnlyList<string> SupportedManagedToolIds, IReadOnlyList<string> Operations,
    IReadOnlyList<string> UncoveredTransports, string DomainSemantics, string ResponseByteSemantics);

public sealed record InternetRequestRetireRequest(string RequestId, long ExpectedRevision, string RequestDigest);
