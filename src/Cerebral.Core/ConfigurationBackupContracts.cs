using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Core;

/// <summary>Declared nonsecret configuration. No runtime state, credentials, plugin payloads or domain data.</summary>
public static class ConfigurationBackupContract
{
    public const string Format = "pegasus.cerebral.configuration";
    public const int SchemaVersion = 14;
    public const int MaximumUtf8Bytes = 1024 * 1024;
    public const int MaximumEntries = 128;
}

public sealed record ConfigurationBackupDocument(string Format, int SchemaVersion, string ApplicationVersion,
    DateTimeOffset CreatedAt, ConfigurationBackupHost Host, ConfigurationBackupPaths Paths,
    IReadOnlyList<ConfigurationBackupAgent> Agents, IReadOnlyList<ConfigurationBackupModel> Models,
    IReadOnlyList<ConfigurationBackupPrompt>? Prompts = null, ConfigurationBackupConnections? Connections = null,
    ConfigurationBackupInterfacePreferences? InterfacePreferences = null,
    IReadOnlyList<ConfigurationBackupSkill>? Skills = null)
{
    /// <summary>Schema14 and later. Keeps the existing compiled constructor signature intact.</summary>
    public ConfigurationBackupSettings? Settings { get; init; }

    /// <summary>Required only for schema15. Earlier portable documents never serialize this member.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SettingsTransferMode { get; init; }
}

/// <summary>Instruction-only skill state. Built-ins omit content so restore preserves the destination's shipped version.</summary>
public sealed record ConfigurationBackupSkill(SkillManifest Manifest, string? Content, bool Enabled,
    IReadOnlyList<SkillBinding> Bindings, bool BuiltIn);

/// <summary>Portable composer/layout choices only; window geometry and conversation choices stay local.</summary>
[method: JsonConstructor]
public sealed record ConfigurationBackupInterfacePreferences(bool? EnterSends, bool? NavigationVisible,
    bool? PanelsVisible, string? SelectedSurface, string? AppearanceTheme = null, string? MessageTimeFormat = null,
    int? RecentChatLimit = null)
{
    // Retain the installed constructor and Deconstruct ABI while JSON uses the complete schema.
    public ConfigurationBackupInterfacePreferences(bool? EnterSends, bool? NavigationVisible,
        bool? PanelsVisible, string? SelectedSurface, string? AppearanceTheme, string? MessageTimeFormat)
        : this(EnterSends, NavigationVisible, PanelsVisible, SelectedSurface, AppearanceTheme,
            MessageTimeFormat, null) { }

    public void Deconstruct(out bool? EnterSends, out bool? NavigationVisible, out bool? PanelsVisible,
        out string? SelectedSurface, out string? AppearanceTheme, out string? MessageTimeFormat)
    {
        EnterSends = this.EnterSends; NavigationVisible = this.NavigationVisible;
        PanelsVisible = this.PanelsVisible; SelectedSurface = this.SelectedSurface;
        AppearanceTheme = this.AppearanceTheme; MessageTimeFormat = this.MessageTimeFormat;
    }
}

/// <summary>Portable declarations. Secret values and opaque launch arguments remain locally bound.</summary>
public sealed record ConfigurationBackupConnections(IReadOnlyList<ConfigurationBackupWorker> Workers,
    IReadOnlyList<ConfigurationBackupExecutable> Executables, IReadOnlyList<ConfigurationBackupMcp> McpServers,
    string SharedFilesWorkerId, IReadOnlyList<string> CortexNamespaces, IReadOnlyList<string> CortexWriteNamespaces);

/// <summary>A shared or assignment-role behavioral layer; never changes the host action protocol or grants.</summary>
public sealed record ConfigurationBackupPrompt(string Role, string Content);

[method: JsonConstructor]
public sealed record ConfigurationBackupHost(int MaxConcurrentJobs, int QueueCapacity, int JobHistoryLimit,
    int JobHistoryDays, int ActivityHistoryLimit, int ActivityHistoryDays, int ReservedMemoryMb,
    int DefaultModelIdleMinutes, int MaxExecutableAttemptsPerAssignment, string DefaultModelId,
    int QuestionTimeoutSeconds, string QuestionTimeoutAction,
    int? EngineEventHistoryLimit = null, int? EngineEventHistoryMegabytes = null,
    int? EngineEventHistoryDays = null, int? MaxLogicalProcessorCount = null,
    IReadOnlyList<int>? AllowedLogicalProcessors = null, string DefaultModelWorkerId = "local",
    bool? PromptCacheDiagnosticsEnabled = null, int? PromptCacheDiagnosticsChangePercentagePoints = null)
{
    // Retain exact precompiled 0.2.22 signatures; appended schema fields keep their current defaults.
    public ConfigurationBackupHost(int MaxConcurrentJobs, int QueueCapacity, int JobHistoryLimit,
        int JobHistoryDays, int ActivityHistoryLimit, int ActivityHistoryDays, int ReservedMemoryMb,
        int DefaultModelIdleMinutes, int MaxExecutableAttemptsPerAssignment, string DefaultModelId,
        int QuestionTimeoutSeconds, string QuestionTimeoutAction, int? EngineEventHistoryLimit,
        int? EngineEventHistoryMegabytes, int? EngineEventHistoryDays, int? MaxLogicalProcessorCount,
        IReadOnlyList<int>? AllowedLogicalProcessors)
        : this(MaxConcurrentJobs, QueueCapacity, JobHistoryLimit, JobHistoryDays, ActivityHistoryLimit,
            ActivityHistoryDays, ReservedMemoryMb, DefaultModelIdleMinutes,
            MaxExecutableAttemptsPerAssignment, DefaultModelId, QuestionTimeoutSeconds,
            QuestionTimeoutAction, EngineEventHistoryLimit, EngineEventHistoryMegabytes,
            EngineEventHistoryDays, MaxLogicalProcessorCount, AllowedLogicalProcessors,
            "local", null, null) { }

    public void Deconstruct(out int MaxConcurrentJobs, out int QueueCapacity, out int JobHistoryLimit,
        out int JobHistoryDays, out int ActivityHistoryLimit, out int ActivityHistoryDays,
        out int ReservedMemoryMb, out int DefaultModelIdleMinutes, out int MaxExecutableAttemptsPerAssignment,
        out string DefaultModelId, out int QuestionTimeoutSeconds, out string QuestionTimeoutAction,
        out int? EngineEventHistoryLimit, out int? EngineEventHistoryMegabytes,
        out int? EngineEventHistoryDays, out int? MaxLogicalProcessorCount,
        out IReadOnlyList<int>? AllowedLogicalProcessors)
    {
        MaxConcurrentJobs = this.MaxConcurrentJobs; QueueCapacity = this.QueueCapacity;
        JobHistoryLimit = this.JobHistoryLimit; JobHistoryDays = this.JobHistoryDays;
        ActivityHistoryLimit = this.ActivityHistoryLimit; ActivityHistoryDays = this.ActivityHistoryDays;
        ReservedMemoryMb = this.ReservedMemoryMb; DefaultModelIdleMinutes = this.DefaultModelIdleMinutes;
        MaxExecutableAttemptsPerAssignment = this.MaxExecutableAttemptsPerAssignment;
        DefaultModelId = this.DefaultModelId; QuestionTimeoutSeconds = this.QuestionTimeoutSeconds;
        QuestionTimeoutAction = this.QuestionTimeoutAction;
        EngineEventHistoryLimit = this.EngineEventHistoryLimit;
        EngineEventHistoryMegabytes = this.EngineEventHistoryMegabytes;
        EngineEventHistoryDays = this.EngineEventHistoryDays; MaxLogicalProcessorCount = this.MaxLogicalProcessorCount;
        AllowedLogicalProcessors = this.AllowedLogicalProcessors;
    }

    // Omission in an older backup preserves the destination setting.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WorkflowIdleExpiryDays { get; init; }
}

public sealed record ConfigurationBackupPaths(string ModelDirectory, string ProjectsDirectory, string SharedDirectory,
    string? ToolsDirectory = null);

public sealed record ConfigurationBackupAgent
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Purpose { get; init; } = "";
    public string Instructions { get; init; } = "";
    public string Role { get; init; } = "helper";
    public required string ModelId { get; init; }
    public bool FollowsDefaultModel { get; init; }
    public string WorkerId { get; init; } = "local";
    public string ModelWorkerId { get; init; } = "local";
    public int ContextTokens { get; init; } = 4096;
    public int ResponseTokens { get; init; } = 1024;
    public bool FollowsModelBudgets { get; init; } = true;
    public string ReasoningMode { get; init; } = "off";
    public int ReasoningTokens { get; init; } = 1024;
    public bool AllowReasoningAdjustment { get; init; }
    public int ReasoningTokenCeiling { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentTaskBudgetPolicy? TaskBudgetPolicy { get; init; }
    public double Temperature { get; init; } = .2;
    public int MaxSteps { get; init; } = 12;
    public int MaxSeconds { get; init; } = 600;
    public int MemoryBudgetMb { get; init; }
    public int IdleMinutes { get; init; } = -1;
    public bool AutomaticCompaction { get; init; } = true;
    public string? CompactionProfileId { get; init; }
    public int CompactionReductionPercent { get; init; } = 50;
    /// <summary>Compact older context at this percentage of measured input capacity after output, reasoning and safety reservations.</summary>
    public int CompactionTriggerPercent { get; init; } = AgentTokenLimits.DefaultCompactionTriggerPercent;
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> RequiredModelCapabilities { get; init; } = [];
    /// <summary>Portable compatibility lane for schemas6–14. Schema15 uses only the centralized complete settings facet.</summary>
    public IReadOnlyList<ConfigurationBackupPluginSettings>? PluginSettings { get; init; }
}

public sealed record ConfigurationBackupPluginSettings(string PluginId, string SourcePackageVersion,
    int SettingsVersion, JsonElement Values);

public sealed record ConfigurationBackupModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? VisionProjectorPath { get; init; }
    public string? SourceUrl { get; init; }
    public string? License { get; init; }
    public string? Publisher { get; init; }
    public string? UpstreamModelId { get; init; }
    public string? UpstreamModelUrl { get; init; }
    public string? LicenseUrl { get; init; }
    public string? UpstreamRevision { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? PackageId { get; init; }
    public string? PackageVersion { get; init; }
    public string? PackagePurpose { get; init; }
    public bool StarterPackage { get; init; }
    public string Purpose { get; init; } = "General";
    public bool AutomaticContextSizing { get; init; } = true;
    public int MaximumContextTokens { get; init; }
    public int ContextTokens { get; init; } = 4096;
    public int ParallelRequests { get; init; } = 1;
    public int GpuLayers { get; init; } = -1;
    public int IdleMinutes { get; init; } = -1;
    public string? ChatTemplate { get; init; }
    public IReadOnlyList<string> StopSequences { get; init; } = [];
}

/// <summary>Messages identify omitted sections or fields, never their values or free-text content.</summary>
public sealed record ConfigurationBackupAttention(string Code, string Section, string? Id, string Message);
public sealed record ConfigurationBackupExport(ConfigurationBackupDocument Document,
    IReadOnlyList<ConfigurationBackupAttention> Attention, bool RequiresTextReview = true)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompleteSettingsFileCoverage? CompleteSettingsCoverage { get; init; }
}

/// <summary>Changes references only: no directories are created and no data is moved.</summary>
public sealed record ConfigurationRestorePathOverrides(string? ModelDirectory = null,
    string? ProjectsDirectory = null, string? SharedDirectory = null, string? ToolsDirectory = null,
    IReadOnlyDictionary<string, string>? ModelFiles = null,
    IReadOnlyDictionary<string, string>? ModelProjectorFiles = null);
public sealed record ConfigurationRestorePreviewRequest(ConfigurationBackupDocument Document,
    ConfigurationRestorePathOverrides? PathOverrides = null);
public sealed record ConfigurationRestorePreview(string PreviewId, DateTimeOffset ExpiresAt,
    int ExpectedHostRevision, IReadOnlyList<string> AddedAgents, IReadOnlyList<string> UpdatedAgents,
    IReadOnlyList<string> AddedModels, IReadOnlyList<string> UpdatedModels,
    IReadOnlyList<ConfigurationBackupAttention> Attention, bool RequiresTextReview = true,
    string Mode = "upsert", IReadOnlyList<string>? UpdatedPromptRoles = null,
    IReadOnlyList<string>? UpdatedSkillIds = null)
{
    public SettingsRestoreSummary? Settings { get; init; }
}
public sealed record ConfigurationRestoreApplyRequest(string PreviewId, int ExpectedHostRevision,
    bool ReviewedFreeText = false);
public sealed record ConfigurationRestoreResult(int HostRevision, IReadOnlyList<string> AgentIds,
    IReadOnlyList<string> ModelIds, string Mode = "upsert", IReadOnlyList<string>? PromptRoles = null,
    bool InterfacePreferencesRestored = false, int PortablePluginSettingsRestored = 0,
    int SkillsRestored = 0)
{
    public SettingsRestoreSummary? Settings { get; init; }
}

public sealed record ConfigurationRestoreStatusRequest(string PreviewId);
/// <summary>Contains only accepted apply fields and its committed result. No backup, prompt or credential bodies.</summary>
public sealed record ConfigurationRestoreReceipt(string PreviewId, int ExpectedHostRevision, bool ReviewedFreeText,
    DateTimeOffset AppliedAt, ConfigurationRestoreResult Result, string NotificationOutcome = "pending");
/// <summary>Unknown means no retained committed receipt; it never proves that an apply did not happen.</summary>
public sealed record ConfigurationRestoreStatus(string PreviewId, string State, ConfigurationRestoreReceipt? Receipt,
    int MaximumRetainedReceipts = 128, int RetentionDays = 30);
