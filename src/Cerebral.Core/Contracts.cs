using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Core;

public static class CerebralDefaults
{
    public const int WorkerPort = 2667;
    public const string WorkerUrl = "http://127.0.0.1:2667";
}

public static class CerebralJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Options);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Options) ?? throw new ArgumentException("Missing request body.");
}

public sealed record CommandRequest(string Operation, JsonElement Arguments, string? ProfileId = null);
public sealed record CommandResult(bool Success, JsonElement Data, string? Error = null)
{
    public static CommandResult Ok(object? value = null) => new(true, CerebralJson.Element(value));
    public static CommandResult Fail(string error) => new(false, CerebralJson.Element(null), error);
}
public sealed record ServiceEvent(long Sequence, string Type, string? JobId, JsonElement Data, DateTimeOffset At);
[method: JsonConstructor]
public sealed record HostInfo(string Product, string Version, int Protocol, string Name, string Architecture, string OperatingSystem, bool Coordinator, string DataDirectory, bool LocalRun = false, string? InstallationId = null)
{
    // Optional parameters do not preserve CLR signatures used by precompiled 0.2.22 consumers.
    public HostInfo(string Product, string Version, int Protocol, string Name, string Architecture,
        string OperatingSystem, bool Coordinator, string DataDirectory, bool LocalRun)
        : this(Product, Version, Protocol, Name, Architecture, OperatingSystem, Coordinator,
            DataDirectory, LocalRun, null) { }

    public void Deconstruct(out string Product, out string Version, out int Protocol, out string Name,
        out string Architecture, out string OperatingSystem, out bool Coordinator,
        out string DataDirectory, out bool LocalRun)
    {
        Product = this.Product; Version = this.Version; Protocol = this.Protocol; Name = this.Name;
        Architecture = this.Architecture; OperatingSystem = this.OperatingSystem;
        Coordinator = this.Coordinator; DataDirectory = this.DataDirectory; LocalRun = this.LocalRun;
    }
}
public sealed record GpuInfo(string Name, long? TotalMb, long? FreeMb, bool Unified, string? Error = null, int? UtilizationPercent = null)
{
    /// <summary>Device memory currently used according to the GPU runtime. Older Workers may omit this value.</summary>
    public long? UsedMb { get; init; }
}
public sealed record ResourceInfo(long ProcessMemoryMb, long? SystemAvailableMb, IReadOnlyList<GpuInfo> Gpus)
{
    public long? SystemTotalMb { get; init; }
    public double? CpuUtilizationPercent { get; init; }
}
public sealed record ModelUsageTotals(long ConfirmedInputTokens, long ConfirmedOutputTokens)
{
    /// <summary>True when every generation observed by this Worker reported evaluated-input usage; false marks a lower bound.</summary>
    public bool? ConfirmedInputComplete { get; init; }
    /// <summary>True when every generation observed by this Worker reported generated-output usage; false marks a lower bound.</summary>
    public bool? ConfirmedOutputComplete { get; init; }
}
/// <summary>Resources owned by the local Cerebral desktop, Worker, and model processes.</summary>
public sealed record CerebralProcessMetrics(long MemoryMb, double? CpuUtilizationPercent, long? GpuMemoryMb,
    int ProcessCount, long? StorageAvailableBytes, long? StorageTotalBytes, string StoragePath)
{
    /// <summary>Why process-owned VRAM could not be measured. Device-wide GPU metrics can still be available.</summary>
    public string? GpuMemoryUnavailableReason { get; init; }
}
public sealed record DesktopStatusMetrics(ResourceInfo Resources, int ActiveAssignments, int QueuedAssignments,
    int ActiveWorkflows, ModelUsageTotals Tokens, DateTimeOffset SampledAt)
{
    public CerebralProcessMetrics? Cerebral { get; init; }
}
public sealed record PeerHostInfo(string Product, string Version, int Protocol, string Name, string Architecture, string OperatingSystem, bool Coordinator);
public sealed record PeerQueueMetrics(int MaximumSlots, int ReservedOwnerSlots, int OwnerRunning, int OwnerQueued, int PeerRunning, int PeerQueued);
public sealed record PeerWorkerMetrics(PeerHostInfo Host, ResourceInfo Resources, PeerQueueMetrics Queue, DateTimeOffset SampledAt);
public sealed record PeerModelCatalogEntry(string Id, string Name, string State, string Architecture, string Quantization,
    int ContextTokens, IReadOnlyList<ModelCapabilityStatus> Capabilities);
public sealed record PeerAgentCatalogEntry(string Id, int Revision, string Name, string Purpose, string Role,
    string ModelId, IReadOnlyList<string> Capabilities, IReadOnlyList<string> RequiredModelCapabilities);
public sealed record PeerCatalog(IReadOnlyList<PeerAgentCatalogEntry> Agents, IReadOnlyList<PeerModelCatalogEntry> Models, int Revision);
public sealed record PeerGrantToken(string GrantId, string Token);
public sealed record PeerGrantSummary(string Id, string Name, int Revision, IReadOnlyList<string> SharedProfileIds,
    int MaxConcurrentJobs, bool Draining, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt, DateTimeOffset? RotatedAt);
public sealed record WorkerProbeResult(PeerHostInfo Host, PeerWorkerMetrics Metrics, long RoundTripLatencyMilliseconds,
    PeerCatalog? Catalog = null);
public sealed record WorkerManagementStatus(string WorkerInstallationId, string State,
    string? ControllerInstallationId, string? ControllerName, DateTimeOffset? ProvisioningExpiresAt);

/// <summary>The owner-controlled admission state for GPU-backed work on this installation.</summary>
public enum LocalResourceState { Active, Draining, Paused, Resuming, Faulted }

/// <summary>Bounded, credential-free peer capacity sampled while applying a local resource policy.</summary>
public sealed record ResourcePausePeerStatus(string WorkerId, string Name, bool Reachable,
    long? RoundTripLatencyMilliseconds, int? AvailableSlots, int? QueuedAssignments,
    IReadOnlyList<string> SharedProfileIds, string? Error = null);

/// <summary>
/// Owner-visible state for pausing local GPU admission. Native workflow runs remain in the local durable queue;
/// only legacy root assignments already supported by the paired-Worker protocol may be rerouted.
/// </summary>
public sealed record ResourcePauseStatus(LocalResourceState State, bool RerouteEligibleWork,
    int ActiveLocalAssignments, int QueuedLocalAssignments, int QueuedLocalWorkflows,
    int RoutedAssignments, IReadOnlyList<string> ModelsToReload,
    IReadOnlyList<ResourcePausePeerStatus> Peers, string Detail, DateTimeOffset UpdatedAt);

public sealed class HostSettings
{
    public int Revision { get; set; }
    /// <summary>Stable service identity. Configuration backups never replace it.</summary>
    public string InstallationId { get; set; } = "";
    public string Name { get; set; } = Environment.MachineName;
    public bool Coordinator { get; set; } = true;
    public string ListenUrl { get; set; } = CerebralDefaults.WorkerUrl;
    /// <summary>Bootstrap-only port scan. A completed provisioning flow pins the selected endpoint.</summary>
    public bool AutoSelectAvailablePort { get; set; }
    public bool AllowLan { get; set; }
    public string AccessKey { get; set; } = "";
    /// <summary>True only for a Worker-only installation provisioned for another Cerebral installation.</summary>
    public bool StandaloneWorker { get; set; }
    /// <summary>Short-lived invitation identity. Cleared when Cerebral Configuration claims the Worker.</summary>
    public string? ProvisioningId { get; set; }
    public DateTimeOffset? ProvisioningExpiresAt { get; set; }
    /// <summary>Stable identity of the Cerebral installation that claimed this standalone Worker.</summary>
    public string? ManagementControllerId { get; set; }
    public string? ManagementControllerName { get; set; }
    public bool ExternalCommandsEnabled { get; set; }
    public int MaxConcurrentJobs { get; set; } = 2;
    public int QueueCapacity { get; set; } = 32;
    /// <summary>Maximum queued or active assignments admitted from all paired installations.</summary>
    public int PeerQueueCapacity { get; set; } = 32;
    /// <summary>Execution slots held for owner work while owner work is waiting. Peers may borrow them while the owner is idle.</summary>
    public int ReservedOwnerSlots { get; set; } = 1;
    /// <summary>Maximum logical processors available to this Worker. Zero leaves the selected set uncapped.</summary>
    public int MaxLogicalProcessorCount { get; set; }
    /// <summary>
    /// Explicit logical processor identifiers available to this Worker. An empty list uses every processor available
    /// to the process except logical processor 0, reserving processor 0 for the operating system by default.
    /// </summary>
    public List<int> AllowedLogicalProcessors { get; set; } = [];
    // These bound the separate history view, not durable execution evidence or active queue admission.
    public int JobHistoryLimit { get; set; } = 500;
    public int JobHistoryDays { get; set; } = 30;
    public const int DefaultWorkflowIdleExpiryDays = 7;
    public const int MaximumWorkflowIdleExpiryDays = 3650;
    /// <summary>Idle days before a safely interrupted workflow expires. Zero disables expiry.</summary>
    public int WorkflowIdleExpiryDays { get; set; } = DefaultWorkflowIdleExpiryDays;
    /// <summary>Maximum completed tool-call journal rows kept for one conversation.</summary>
    public int ActivityHistoryLimit { get; set; } = 2000;
    /// <summary>Maximum age of completed tool-call journal rows.</summary>
    public int ActivityHistoryDays { get; set; } = 30;
    /// <summary>Maximum rolling Engine diagnostic events retained for one conversation.</summary>
    public int EngineEventHistoryLimit { get; set; } = 2000;
    /// <summary>Maximum rolling Engine diagnostic payload retained for one conversation, in MiB.</summary>
    public int EngineEventHistoryMegabytes { get; set; } = 64;
    /// <summary>Maximum age of rolling Engine diagnostic events.</summary>
    public int EngineEventHistoryDays { get; set; } = 30;
    /// <summary>Collect optional numeric prompt-cache diagnostics in a separate rolling 24-hour history.</summary>
    public bool PromptCacheDiagnosticsEnabled { get; set; } = true;
    /// <summary>Meaningful change threshold. Reuse uses percentage points; tokens/prefill use relative percent with absolute floors.</summary>
    public int PromptCacheDiagnosticsChangePercentagePoints { get; set; } = 5;
    public int ReservedMemoryMb { get; set; } = 1024;
    /// <summary>Idle model unload delay used by model registrations that inherit the service default. Zero keeps inherited models loaded.</summary>
    public int DefaultModelIdleMinutes { get; set; } = 10;
    /// <summary>Maximum launches of the same executable target during one accepted assignment.</summary>
    public int MaxExecutableAttemptsPerAssignment { get; set; } = 2;
    /// <summary>The model followed by the Orchestrator and any profiles that opt into the shared default.</summary>
    public string DefaultModelId { get; set; } = "";
    /// <summary>Location of the default model; independent of where each agent executes its tools.</summary>
    public string DefaultModelWorkerId { get; set; } = "local";
    public string LlamaServerPath { get; set; } = "";
    public string ModelDirectory { get; set; } = "";
    public string ProjectsDirectory { get; set; } = "";
    public string SharedDirectory { get; set; } = "";
    /// <summary>Shared portable executables and runtimes available to agents and plugins on this host.</summary>
    public string ToolsDirectory { get; set; } = "";
    public string SharedFilesWorkerId { get; set; } = "local";
    public List<ExecutableRegistration> Executables { get; set; } = [];
    public List<McpRegistration> McpServers { get; set; } = [];
    /// <summary>Initial local installation observation only; never selects a provider or grants transport access.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InitialCortexDiscoveryState? InitialCortexDiscovery { get; set; }
    public List<WorkerRegistration> Workers { get; set; } = [];
    public int QuestionTimeoutSeconds { get; set; }
    public string QuestionTimeoutAction { get; set; } = "wait";
    public bool BackgroundTasksEnabled { get; set; }
    public List<BackgroundTaskDefinition> BackgroundTasks { get; set; } = [];
    public List<string> CortexNamespaces { get; set; } = ["project.pegasus-cerebral", "knowledge.shared"];
    public List<string> CortexWriteNamespaces { get; set; } = ["project.pegasus-cerebral"];
}
public sealed record InitialCortexDiscoveryState(string State, string? ExecutablePath);
public sealed class BackgroundTaskDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Background check";
    public string ProfileId { get; set; } = "";
    public string Prompt { get; set; } = "";
    /// <summary>User-authored text retained in the conversation when Prompt is private trusted-plugin workflow context.</summary>
    public string? VisiblePrompt { get; set; }
    /// <summary>Trusted desktop plugin that supplied private workflow context.</summary>
    public string? WorkflowSource { get; set; }
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public int MaxRunMinutes { get; set; } = 10;
    public string? LastJobId { get; set; }
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}
public sealed class ExecutableRegistration
{
    public bool RequiresConfiguration { get; set; }
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> PrefixArguments { get; set; } = [];
}
public sealed class McpRegistration
{
    public bool RequiresConfiguration { get; set; }
    public string Id { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string? Url { get; set; }
    public bool ViaUserConnector { get; set; } = true;
    public bool Enabled { get; set; }
    /// <summary>Explicit native Cortex binding. Tool aliases and grants remain compatible; this route never starts MCP.</summary>
    public CortexNativeConnection? CortexNative { get; set; }
    public bool CortexNativeRequired { get; set; }
}
public sealed class WorkerRegistration
{
    public bool RequiresConfiguration { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Legacy owner-level HTTP key. Paired capacity should use PeerToken instead.</summary>
    public string AccessKey { get; set; } = "";
    /// <summary>Show-once token issued by the receiving Worker's scoped peer grant.</summary>
    public string PeerToken { get; set; } = "";
    public string? SshHost { get; set; }
    public string? SshUser { get; set; }
    public int SshPort { get; set; } = 22;
    public string? SshIdentityFile { get; set; }
    /// <summary>Local OpenSSH host-key database used to pin the remote worker identity. This path is never exported.</summary>
    public string? SshKnownHostsFile { get; set; }
    public string? InstallDirectory { get; set; }
}
public sealed class PeerAccessGrant
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Base64url SHA-256 digest of the random secret. The show-once token is never persisted by the receiver.</summary>
    public string SecretDigest { get; set; } = "";
    public int Revision { get; set; }
    public List<string> SharedProfileIds { get; set; } = [];
    public int MaxConcurrentJobs { get; set; } = 1;
    public bool Draining { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
}
/// <summary>Explicit owner grant for task-local context and visible-output choices.
/// Null on a profile preserves all legacy fixed/default budget behavior.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentTaskBudgetPolicy(int SchemaVersion, int? MaxContextTokens,
    int? MaxResponseTokens, bool AllowAutonomousSelection);

public sealed class AgentProfile
{
    public string Id { get; set; } = "";
    public int Revision { get; set; }
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string Role { get; set; } = "helper";
    public string ModelId { get; set; } = "";
    /// <summary>When true, the profile moves with the service-owned default model.</summary>
    public bool FollowsDefaultModel { get; set; }
    public string WorkerId { get; set; } = "local";
    /// <summary>Receiver-owned profile contribution selected on a remote Worker. Null preserves the legacy matching-ID behavior.</summary>
    public string? RemoteProfileId { get; set; }
    public string ModelWorkerId { get; set; } = "local";
    public int ContextTokens { get; set; } = 4096;
    public int ResponseTokens { get; set; } = 1024;
    /// <summary>When true, Configuration derives the token budgets from the selected model's recommendation.</summary>
    public bool FollowsModelBudgets { get; set; } = true;
    public string ReasoningMode { get; set; } = "off";
    public int ReasoningTokens { get; set; } = 1024;
    /// <summary>Owner opt-in for one-generation reasoning choices. Off remains the default; on requires a positive reserve.</summary>
    public bool AllowReasoningAdjustment { get; set; }
    /// <summary>Zero uses ReasoningTokens as the owner ceiling, preserving existing profiles.</summary>
    public int ReasoningTokenCeiling { get; set; }
    /// <summary>Owner-authored opt-in ceilings, distinct from configured or model-derived defaults.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentTaskBudgetPolicy? TaskBudgetPolicy { get; set; }
    public double Temperature { get; set; } = 0.2;
    public int MaxSteps { get; set; } = 12;
    public int MaxSeconds { get; set; } = 600;
    public int MemoryBudgetMb { get; set; }
    /// <summary>-1 inherits the service default, zero disables idle unloading, and a positive value is a model-specific override.</summary>
    public int IdleMinutes { get; set; } = -1;
    public bool AutomaticCompaction { get; set; } = true;
    public string? CompactionProfileId { get; set; }
    /// <summary>Minimum percentage of older active context that compaction should remove. The remainder is also bounded by the selected compactor's model budgets.</summary>
    public int CompactionReductionPercent { get; set; } = 50;
    /// <summary>Compact older context at this percentage of measured input capacity after output, reasoning and safety reservations.</summary>
    public int CompactionTriggerPercent { get; set; } = AgentTokenLimits.DefaultCompactionTriggerPercent;
    public List<string> AllowedRoots { get; set; } = [];
    public List<string> AllowedTools { get; set; } = [];
    public List<string> Capabilities { get; set; } = ["diagnostics"];
    /// <summary>Additional model abilities required by this profile. Text generation remains the legacy baseline.</summary>
    public List<string> RequiredModelCapabilities { get; set; } = [];
    /// <summary>Opaque, bounded settings owned by installed modules. Core persists these values but never interprets a module's schema.</summary>
    public Dictionary<string, JsonElement> PluginSettings { get; set; } = new(StringComparer.Ordinal);
}
public sealed class ModelRecord
{
    public string? ChatTemplate { get; set; }
    public List<string> StopSequences { get; set; } = [];
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Explicit matching image projector component. Null keeps the model text-only.</summary>
    public string? VisionProjectorPath { get; set; }
    public string? SourceUrl { get; set; }
    public string? License { get; set; }
    /// <summary>Human-readable publisher or organization responsible for the upstream model.</summary>
    public string? Publisher { get; set; }
    /// <summary>Stable upstream repository identifier, such as Qwen/Qwen3.5-9B.</summary>
    public string? UpstreamModelId { get; set; }
    /// <summary>Official upstream model or model-card page. This is distinct from the exact GGUF artifact URL.</summary>
    public string? UpstreamModelUrl { get; set; }
    /// <summary>Official license text or license record for the upstream model.</summary>
    public string? LicenseUrl { get; set; }
    /// <summary>Optional immutable revision or release label supplied by the package author.</summary>
    public string? UpstreamRevision { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>Package identity is retained after import so Configuration can explain how the model was installed.</summary>
    public string? PackageId { get; set; }
    public string? PackageVersion { get; set; }
    public string? PackagePurpose { get; set; }
    public bool StarterPackage { get; set; }
    public long SizeBytes { get; set; }
    public string State { get; set; } = "unloaded";
    public string? Error { get; set; }
    public string Architecture { get; set; } = "";
    public string? DeclaredType { get; set; }
    public string Quantization { get; set; } = "";
    public long? ParameterCount { get; set; }
    public int? NativeContextTokens { get; set; }
    public int RecommendedContextTokens { get; set; } = 4096;
    public int? BlockCount { get; set; }
    public int? EmbeddingLength { get; set; }
    public int? AttentionHeadCount { get; set; }
    public int? AttentionHeadCountKv { get; set; }
    public bool HasEmbeddedChatTemplate { get; set; }
    public string Purpose { get; set; } = "General";
    /// <summary>Either gguf for detected metadata or filename/fallback for a clearly labelled estimate.</summary>
    public string MetadataSource { get; set; } = "fallback";
    public string? MetadataWarning { get; set; }
    /// <summary>When enabled, the worker chooses the largest context that fits current memory while preserving the configured service reserve.</summary>
    public bool AutomaticContextSizing { get; set; } = true;
    /// <summary>Optional ceiling for automatic context sizing. Zero uses the native GGUF limit when available.</summary>
    public int MaximumContextTokens { get; set; }
    public int ContextTokens { get; set; } = 4096;
    public int ParallelRequests { get; set; } = 1;
    public int GpuLayers { get; set; } = -1;
    public int IdleMinutes { get; set; } = 5;
    /// <summary>Layered capability evidence recalculated by the service from model metadata and implemented runtime paths.</summary>
    public List<ModelCapabilityStatus> CapabilityStatus { get; set; } = [];
}
public sealed class SessionMessage
{
    public bool ExcludedFromContext { get; set; }
    public string Origin { get; set; } = "user";
    /// <summary>Stable source identity for an idempotent service-owned question/answer projection.</summary>
    public string? SourceQuestionId { get; set; }
    public int Sequence { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? ToolName { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class TaskSession
{
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? ProjectId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New task";
    public string ProfileId { get; set; } = "";
    public int Revision { get; set; }
    public string? Checkpoint { get; set; }
    public int CheckpointThroughSequence { get; set; }
    /// <summary>Optional conversation preference. Null inherits the accepted agent profile's response budget.</summary>
    public int? ResponseSharePercent { get; set; }
    /// <summary>Total retained messages. A client page can contain fewer entries in Messages.</summary>
    public int MessageCount { get; set; }
    /// <summary>True when the current client page starts after an earlier retained message.</summary>
    public bool HasEarlierMessages { get; set; }
    public int? OldestLoadedSequence { get; set; }
    public List<SessionMessage> Messages { get; set; } = [];
    public Dictionary<string, string> RemoteSessionIds { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Durable presentation binding for one conversation. The owning plugin keeps domain content and detailed view state
/// in its own service-owned store; Cerebral retains only enough identity to restore the correct workspace surface.
/// </summary>
public sealed record ConversationWorkspaceBinding(
    string ConversationId,
    string PluginId,
    string SurfaceId,
    string ResourceId,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed class AgentJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Prompt { get; set; } = "";
    /// <summary>Optional exact user text displayed and persisted while Prompt remains plugin-owned backend context.</summary>
    public string? VisiblePrompt { get; set; }
    public string? WorkflowSource { get; set; }
    public string Kind { get; set; } = "agent";
    public string Status { get; set; } = "queued";
    public string? WaitingReason { get; set; }
    public string? Result { get; set; }
    public string? Outcome { get; set; }
    public int StartSequence { get; set; }
    public List<ExecutionEvidence> Evidence { get; set; } = [];
    public string? Error { get; set; }
    public string? ParentJobId { get; set; }
    // A running helper stays attached to the input that delegated it when the lead is steered.
    public int? RootTurnSequence { get; set; }
    public string WorkerId { get; set; } = "local";
    /// <summary>Inbound paired-capacity owner. Null identifies installation-owner work.</summary>
    public string? PeerGrantId { get; set; }
    public string? RemoteJobId { get; set; }
    public string? RemoteSessionId { get; set; }
    public SubmitJobRequest? RemoteSubmission { get; set; }
    public bool RemoteConfirmed { get; set; }
    public int RemoteFailures { get; set; }
    public DateTimeOffset? NextReconcileAt { get; set; }
    public bool CancelRequested { get; set; }
    public bool CancellationTombstone { get; set; }
    public int StepCount { get; set; }
    /// <summary>The conversation or submission balance snapshotted when this assignment was accepted.</summary>
    public int? ResponseSharePercent { get; set; }
    public int? LastPromptTokens { get; set; }
    public int? LastResponseTokenLimit { get; set; }
    public double ActiveSeconds { get; set; }
    public DateTimeOffset? ExecutionStartedAt { get; set; }
    public List<string> ConsumedHelperJobIds { get; set; } = [];
    public AgentProfile Profile { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
public sealed class LongFormState
{
    public string SessionId { get; set; } = "";
    public string SourceJobId { get; set; } = "";
    /// <summary>The user-message sequence whose target and fresh-artifact intent produced this plan.</summary>
    public int SourceRequestSequence { get; set; }
    public int Revision { get; set; }
    public string Path { get; set; } = "";
    /// <summary>True until the first receipt for a plan that selected a new destination rather than continuing a supplied artifact.</summary>
    public bool StartsNewArtifact { get; set; }
    public string TargetLabel { get; set; } = "";
    public int TargetWords { get; set; }
    public int WordsPerPage { get; set; } = 250;
    public int VerifiedWords { get; set; }
    public int VerifiedLines { get; set; }
    public int VerifiedBytes { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>A bounded ending of the latest successfully dispatched write or append, retained only for prose continuity.</summary>
    public string? RecentTail { get; set; }
    /// <summary>An append saved before dispatch so an interrupted exact replay cannot duplicate content.</summary>
    public LongFormPendingAppend? PendingAppend { get; set; }
    public int CurrentSectionIndex { get; set; }
    public List<LongFormSection> Sections { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class LongFormPendingAppend
{
    public string JobId { get; set; } = "";
    public int Step { get; set; }
    public string Path { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
    public int ExpectedBytes { get; set; }
    public string Content { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class LongFormSection
{
    public string Title { get; set; } = "";
    public string Purpose { get; set; } = "";
    public int TargetWords { get; set; }
    public int? CompletedAtWords { get; set; }
}
public sealed class SubmitJobRequest
{
    public string? ProjectId { get; set; }
    public string? RequestId { get; set; }
    public string? SessionId { get; set; }
    public string ProfileId { get; set; } = "";
    /// <summary>Private module-prepared context executed by Cerebral. The user-authored text remains in VisiblePrompt.</summary>
    public string Prompt { get; set; } = "";
    /// <summary>Exact user-visible request retained in conversation history for a private plugin workflow.</summary>
    public string? VisiblePrompt { get; set; }
    /// <summary>Validated desktop plugin identity that prepared the private workflow context.</summary>
    public string? WorkflowSource { get; set; }
    public string? Title { get; set; }
    public string Kind { get; set; } = "agent";
    public string? ParentJobId { get; set; }
    /// <summary>Optional percentage of the fixed context to reserve for visible output for this assignment.</summary>
    public int? ResponseSharePercent { get; set; }
    public AgentProfile? DelegatedProfile { get; set; }
    public List<SessionMessage>? InitialMessages { get; set; }
    public string? InitialCheckpoint { get; set; }
    /// <summary>Set only by the receiving peer endpoint after token authentication. Network request bodies cannot choose this value.</summary>
    public string? PeerGrantId { get; set; }
}
public sealed record ToolDescriptor(string Name, string Description, JsonElement InputSchema, string Capability)
{
    /// <summary>
    /// Whether this tool may be projected into a model action schema. Service-owned sinks remain callable by
    /// validated workflows and structured clients while staying unavailable for direct model invocation.
    /// </summary>
    public bool ModelVisible { get; init; } = true;

    /// <summary>
    /// In an exact multi-tool assignment, remove this operation from the model catalog after its first successful
    /// receipt. Use for read-only workflow gates whose identical repetition cannot add evidence; repeatable paged
    /// reads and mutation tools keep the default false value.
    /// </summary>
    public bool SingleUsePerAssignment { get; init; }

    /// <summary>
    /// Related workflow tools whose schemas should remain available when this tool is routed to a model. These
    /// bounded preparation hints neither execute tools nor impose activation gates. Saved profile capabilities
    /// and the dispatcher remain the authorization boundary.
    /// </summary>
    public IReadOnlyList<string> PrerequisiteTools { get; init; } = [];

    /// <summary>
    /// Alternative earlier operations that activate this tool for the current assignment. When nonempty, at least one
    /// named tool must have a successful service receipt before this descriptor enters the model action schema.
    /// </summary>
    public IReadOnlyList<string> ActivationTools { get; init; } = [];
}
public sealed class UserQuestion
{
    public string? KnowledgeProposalId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? Reason { get; set; }
    public List<string> Choices { get; set; } = [];
    public bool Required { get; set; }
    public bool CanAssume { get; set; }
    public string? Assumption { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public bool Assumed { get; set; }
    public string? Answer { get; set; }
    public string? RemoteQuestionId { get; set; }
    public bool RemoteAnswerDelivered { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
[JsonConverter(typeof(JsonStringEnumConverter<InterfaceFailureStage>))]
public enum InterfaceFailureStage
{
    PackageInspection,
    PackageInstall,
    NativeLoad,
    ContributionValidation,
    ViewBinding,
    ViewCapture,
    AttachmentImport,
    InstalledGenerationChanged
}

public sealed class InterfaceRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    /// <summary>legacy for the previous scheduler, workflow for the durable conversation engine.</summary>
    public string Source { get; set; } = "legacy";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Exact source package bytes admitted for this plugin review; absent in older requests.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageFingerprint { get; set; }
    /// <summary>Admission-verified SDK source root for this exact request/path/fingerprint. Reference only;
    /// current authoring/build coordinates still require exact plugin.projects inspection. Imported and historical requests omit it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedSourceRoot { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ClaimId { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedAt { get; set; }
    public string? ProcessStatus { get; set; }
    public int? ExitCode { get; set; }
    public string? Output { get; set; }
    public bool StopRequested { get; set; }
    public bool RepairScheduled { get; set; }
    public string? Error { get; set; }
    /// <summary>Closed host-reported phase for a failed native plugin review. Historical failures may omit it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InterfaceFailureStage? FailureStage { get; set; }
    public string? Result { get; set; }
    /// <summary>Optional immutable attachment coordinate produced by the signed-in desktop while satisfying the request.</summary>
    public InterfaceResourceReceipt? Resource { get; set; }
    /// <summary>Owner-produced installed package identity. Absence in older receipts grants no plugin control access.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InterfacePluginReceipt? Plugin { get; set; }
    /// <summary>Host-owned follow-up intent persisted with explicit owner consent. It is not an installation or test-pass receipt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InterfacePluginTestContinuation? PluginTests { get; set; }
}
/// <summary>
/// A bounded, idempotent service continuation for one owner-reviewed generation. Mode is absent on
/// historical installed-test records; repair-before-install records retain failed review consent
/// without representing an installation, mounted plug-in, or successful native control test.
/// </summary>
public sealed record InterfacePluginTestContinuation(string RequestId, string OriginRunId, int Round,
    string Status = "pending", string? RunId = null, string? Message = null, string? Mode = null);
public static class InterfacePluginTestModes
{
    public const string RepairBeforeInstall = "repair-before-install";
}
public sealed record InterfaceClaimResult(bool Claimed, InterfaceRequest Request);
public sealed record InterfaceResourceReceipt(string Id, long Revision, string MediaType, string OriginalName, long Length);
/// <summary>
/// Binds an owner-approved presentation to one immutable installed native package generation and its actual mounts.
/// ControlTestsAllowed is an explicit owner choice; neither a source manifest nor a model-authored argument grants it.
/// </summary>
public sealed record InterfacePluginReceipt(string Id, string Version, string Generation, string ManifestPath,
    IReadOnlyList<InterfacePluginMountReceipt> Mounts, bool ControlTestsAllowed = false, int SchemaVersion = 1);
/// <summary>Id is the exact host-qualified mount identifier. The supported sidebar control slot is named sidebar.</summary>
public sealed record InterfacePluginMountReceipt(string Slot, string Id);
public sealed record SystemInstructionDocument(string Path, string Content, string Sha256);
public sealed record ToolCall(string Name, JsonElement Arguments);
/// <summary>
/// Optional service-owned raw generation followed by one validated tool dispatch. A plugin uses this when a large
/// payload is more reliable as ordinary model text than as a string embedded inside structured action JSON.
/// </summary>
public sealed record ToolGenerationRequest(string Prompt, string SinkToolName, JsonElement SinkArguments,
    string ContentProperty = "content", int MaxTokens = 4096, bool CompleteAssignment = true,
    int MaxAttempts = 1);

public sealed record ToolResult(bool Success, string Content, bool Truncated = false,
    ToolGenerationRequest? Generation = null, bool CompleteAssignment = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolExecutionEvidence? Evidence { get; init; }
    /// <summary>Provider-owned completion milestones introduced by successful work, not new tool grants.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredSuccessfulTools { get; init; }
    [JsonIgnore]
    public ToolExecutionOutcome EffectiveOutcome => Evidence?.Outcome ?? (Success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed);
}
public enum ToolExecutionOutcome { Succeeded, Failed, Rejected, Cancelled, Unknown }
/// <summary>Host-owned outcome metadata retained separately from bounded human/model result content.</summary>
public sealed record ToolExecutionEvidence(ToolExecutionOutcome Outcome, string ProviderId, string? RequestId = null,
    string? JobId = null, string? State = null, bool? Partial = null, int? ExitCode = null);
[method: JsonConstructor]
public sealed record ToolExecutionContext(AgentProfile Profile, string SessionId, string JobId, string? WorkingDirectory = null,
    CortexAccessScope? Cortex = null, bool FullAccess = false, string? EffectId = null)
{
    /// <summary>Do not implicitly widen a workflow's accepted access scope when application Full Access is enabled later.</summary>
    public bool EnforceProfileScope { get; init; }
    /// <summary>Optional host-owned operation-aware path ceiling; Full Access cannot bypass it.</summary>
    public ScopedPathAccessScope? PathAccess { get; init; }
    /// <summary>Host-owned live Internet scope. Full Access cannot bypass it; model arguments cannot select its owning Main.</summary>
    [JsonIgnore] public IManagedInternetSession? Internet { get; init; }
    // Keep the original compiled constructor available to existing extensions. EffectId belongs to
    // the host's durable workflow, never to a model-authored tool argument or an authorization grant.
    public ToolExecutionContext(AgentProfile Profile, string SessionId, string JobId, string? WorkingDirectory,
        CortexAccessScope? Cortex, bool FullAccess) : this(Profile, SessionId, JobId, WorkingDirectory, Cortex, FullAccess, null) { }
    public void Deconstruct(out AgentProfile profile, out string sessionId, out string jobId, out string? workingDirectory,
        out CortexAccessScope? cortex, out bool fullAccess)
    { profile = Profile; sessionId = SessionId; jobId = JobId; workingDirectory = WorkingDirectory; cortex = Cortex; fullAccess = FullAccess; }
}
public sealed record FullAccessStatus(bool Active, bool Remembered = false, string? ProviderId = null, DateTimeOffset? EnabledAt = null, string? Message = null);
public interface IToolDispatcher
{
    bool FullAccessActive => false;
    Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken);
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken);
    /// <summary>Inspect an existing execution without dispatching the original tool again. Unsupported recovery stays unknown.</summary>
    Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context, ToolExecutionEvidence? previousEvidence,
        CancellationToken cancellationToken) => Task.FromResult(new ToolResult(false, "This tool provider cannot inspect the existing execution.")
        { Evidence = previousEvidence is null ? new(ToolExecutionOutcome.Unknown, "cerebral") : previousEvidence with { Outcome = ToolExecutionOutcome.Unknown } });
}
/// <summary>A bounded typed tool contribution composed into the Worker's existing dispatcher.</summary>
public interface IToolExtension
{
    IReadOnlyList<ToolDescriptor> Tools { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken);
}
public interface ICommandDispatcher
{
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default);
}
