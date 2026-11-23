using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cerebral.MetricsPlugin;

public static class MetricsContract
{
    public const string PluginId = "pegasus.metrics";
    public const string Version = "0.2.0";
    public const int MaximumRecordBytes = 8192;
    public const int MaximumRecords = 100_000;
    public const int MaximumQueryRows = 200;
    // All package DTO metadata stays in its collectible context. Reflection member
    // emit caches in the shared runtime otherwise keep plugin Types alive on unload.
    public static JsonSerializerOptions Json { get; } = new MetricsJsonContext(new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 }).Options;
    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T));
}

public enum MetricKind { Generation, Workflow, Tool, Capacity }
public enum MetricState { Active, Completed, Failed, Cancelled, Unknown, Available, Unavailable, Partial }
public enum MetricOrigin { Local, Remote, Unknown }
public enum MetricBasis { ProviderTelemetry, HostPreparedBudget, HostReceipt, HostSnapshot }
public enum MetricTokenScope { Unknown, ProviderUnspecified, VisibleOnly, ReasoningAndVisible }

/// <summary>Plugin-private scalar schema. No raw prompt, text, arguments, results, paths or errors.</summary>
public sealed record MetricObservation
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    public required string InstallationId { get; init; }
    public required string SourceGeneration { get; init; }
    // Stable across receipt rereads and Worker restarts for durable workflows/effects.
    public required string EntityId { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    [JsonRequired] public MetricKind Kind { get; init; }
    [JsonRequired] public MetricState State { get; init; }
    [JsonRequired] public MetricOrigin Origin { get; init; }
    [JsonRequired] public MetricBasis Basis { get; init; }
    public string? WorkerId { get; init; }
    public string? ModelId { get; init; }
    public string? AgentId { get; init; }
    public string? ConversationId { get; init; }
    public string? ProjectId { get; init; }
    public string? RunId { get; init; }
    public string? EffectId { get; init; }
    public string? ToolName { get; init; }
    public bool Partial { get; init; }
    public bool Truncated { get; init; }
    public long? ConfirmedInputTokens { get; init; }
    public long? ConfirmedOutputTokens { get; init; }
    // Schema2 progress samples are lower bounds, never confirmed consumption. Omit
    // these fields on legacy rows so a schema1 writer keeps its original shape.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? ProviderObservedInputTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? ProviderObservedOutputTokens { get; init; }
    public MetricTokenScope OutputTokenScope { get; init; }
    public int? PreparedInputTokens { get; init; }
    public int? EffectiveContextTokens { get; init; }
    public int? ResponseBudgetTokens { get; init; }
    public int? ReasoningReserveTokens { get; init; }
    public int? CachedTokens { get; init; }
    public int? ProcessedTokens { get; init; }
    public double? ProviderPromptMilliseconds { get; init; }
    public double? ProviderOutputMilliseconds { get; init; }
    public double? InputTokensPerSecond { get; init; }
    public double? OutputTokensPerSecond { get; init; }
    public long? GenerationHostMilliseconds { get; init; }
    public long? WorkflowActiveMilliseconds { get; init; }
    public long? WorkflowWallMilliseconds { get; init; }
    public int? AllocatedSlots { get; init; }
    public int? AvailableSlots { get; init; }
    public int? QuarantinedSlots { get; init; }
    public int? ObservedExecutions { get; init; }
    public int? ObservedInferenceActivity { get; init; }
    public double? ConnectionLatencyMilliseconds { get; init; }
    // Length-prefixed components cannot alias when identifiers themselves contain colons.
    // Durable runs/effects retain their identity across Worker restart. Lanes and capacity
    // samples are scoped to their actual source generation; capacity EntityId is a sample ID.
    [JsonIgnore] public string Key => Part(InstallationId) + Part(Kind.ToString()) +
        (Kind is MetricKind.Generation or MetricKind.Capacity ? Part(SourceGeneration) : "0:") + Part(EntityId);
    private static string Part(string value) => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
}

public sealed record MetricsPreferences(bool CaptureEnabled = false, int RetentionDays = 30,
    long MaximumBytes = 64L * 1024 * 1024);
public sealed record MetricsSettings(long Revision, MetricsPreferences Values);
public sealed record MetricsSettingsChange(long ExpectedRevision, MetricsPreferences Values);
public sealed record MetricsQuery(DateTimeOffset From, DateTimeOffset To, string? ModelId = null,
    string? AgentId = null, string? ConversationId = null, string? ProjectId = null,
    MetricOrigin? Origin = null, int Limit = 100, string? WorkerId = null);
public sealed record MetricsCoverage(DateTimeOffset? Earliest, DateTimeOffset? Latest, int RetainedEntities,
    long StoredBytes, long DuplicateReads, long RejectedRecords, long SourceDropped,
    bool Partial, string State, string? Reason, DateTimeOffset? CaptureStartedAt, DateTimeOffset? LastSourceAt);
public sealed record MetricTotal(long? Value, int Known, int Missing);
public sealed record MetricsTrend(DateOnly Day, MetricTotal InputTokens, MetricTotal OutputTokens,
    int CompletedWorkflows, int FailedWorkflows, int ToolReceipts)
{
    public MetricTotal ProviderObservedInputTokens { get; init; } = new(null, 0, 0);
    public MetricTotal ProviderObservedOutputTokens { get; init; } = new(null, 0, 0);
}
public sealed record MetricsActivity(string AgentId, int Generations, int CompletedWorkflows, int FailedWorkflows, int ToolReceipts);
public sealed record MetricsSummary(MetricTotal InputTokens, MetricTotal OutputTokens, int Generations, int ObservedWorkflows,
    int CompletedWorkflows, int FailedWorkflows, int CancelledWorkflows, int ToolReceipts, int FailedTools,
    double? LatestOutputTokensPerSecond, int? ObservedExecutionPeak, int? ObservedInferencePeak,
    MetricTokenScope[] OutputTokenScopes, MetricBasis[] TokenBases)
{
    public MetricTotal ProviderObservedInputTokens { get; init; } = new(null, 0, 0);
    public MetricTotal ProviderObservedOutputTokens { get; init; } = new(null, 0, 0);
}
public sealed record MetricsQueryResult(string Schema, DateTimeOffset GeneratedAt, MetricsQuery Query,
    MetricsCoverage Coverage, MetricsSummary Summary, MetricsTrend[] Trends, MetricsActivity[] Activity,
    MetricObservation[] Capacity, int OmittedCapacity, MetricObservation[] Rows, int OmittedRows, bool Truncated);
public sealed record MetricsStatus(string Schema, MetricsSettings Settings, MetricsCoverage Coverage,
    string SourceState, string SourceReason, bool CaptureActive, long SourceCleanupFaults = 0);

/// <summary>Plugin-private journal metadata; its four-field wire shape also serves legacy stores.</summary>
public sealed record MetricsCoverageCheckpoint(bool HasGap, long SourceDropped, DateTimeOffset? Started, DateTimeOffset? LastSource);
