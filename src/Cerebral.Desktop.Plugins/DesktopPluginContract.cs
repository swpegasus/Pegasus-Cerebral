using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Cerebral.Desktop.Plugins;

/// <summary>Public native WPF contract. Implementations execute with the signed-in user's full process trust.</summary>
public static class DesktopPluginContract
{
    public const string Id = "pegasus.cerebral.desktop";
    public const int Version = 1;
    public const int MaxTextChars = 256_000;
}

/// <summary>
/// Application resource keys for native plugin views. Values are live WPF brushes and change in place when
/// the user changes appearance, so plugins should resolve the resource instead of copying a color.
/// </summary>
public static class DesktopThemeResourceKeys
{
    public const string TextBrush = "Cerebral.Theme.TextBrush";
    public const string MutedTextBrush = "Cerebral.Theme.MutedTextBrush";
    public const string ErrorTextBrush = "Cerebral.Theme.ErrorTextBrush";
    public const string ErrorBackgroundBrush = "Cerebral.Theme.ErrorBackgroundBrush";
    public const string ErrorBorderBrush = "Cerebral.Theme.ErrorBorderBrush";
}

public sealed class DesktopPluginManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Contract { get; set; } = DesktopPluginContract.Id;
    public int ApiVersion { get; set; } = DesktopPluginContract.Version;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DesktopPluginDependency>? Dependencies { get; set; }
    /// <summary>Schema 3: whether a bundled package is enabled the first time it is installed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DefaultEnabled { get; set; }
    /// <summary>Schema 3: plugins in the same nonempty group are alternative providers; at most one may be enabled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExclusiveGroup { get; set; }
    /// <summary>Schema 3: startup reconciliation retains the highest-priority enabled provider in a group.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExclusivePriority { get; set; }
}

public sealed class DesktopPluginDependency
{
    public string Id { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExactVersion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinimumVersion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaximumVersionExclusive { get; set; }
    public bool Optional { get; set; }
}

/// <summary>Generation changes whenever the host changes connection or conversation, including an unsaved conversation.</summary>
public sealed record DesktopConversationBinding(string Generation, string? SessionId);
public sealed record DesktopConversationSnapshot(DesktopConversationBinding Binding, string Title, string? ProjectId, string? ProjectPath, bool IsRunning);
public sealed record DesktopDraftSnapshot(DesktopConversationBinding Binding, long Revision, string Text);
public sealed record DesktopDraftUpdate(DesktopConversationBinding Binding, long ExpectedRevision, string Text)
{
    /// <summary>Optional caret-checked presentation. Use only when the default text-entry host advertises support.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DesktopDraftPresentation? Presentation { get; init; }
}
public sealed record DesktopTextSelection(int Start, int Length)
{
    public bool IsWithin(int textLength) => textLength >= 0 && Start >= 0 && Length >= 0 &&
        Start <= textLength && Length <= textLength - Start;
}
/// <summary>A generation/revision-checked draft edit also checks the expected selection; no focus or submit action is implied.</summary>
public sealed record DesktopDraftPresentation(DesktopTextSelection ExpectedSelection,
    DesktopTextSelection Selection, bool FadeChangedText = false);
/// <summary>Keep RequestId unchanged when reconciling or retrying an uncertain submission.</summary>
public sealed record DesktopSubmission(DesktopConversationBinding Binding, string RequestId, string Text);
/// <summary>
/// A trusted plugin workflow keeps the user's exact visible request separate from bounded plugin-owned execution
/// context. The host records VisibleText as the user message and sends Context only as backend assignment context.
/// </summary>
public sealed record DesktopWorkflowSubmission(DesktopConversationBinding Binding, string RequestId, string VisibleText, string Context)
{
    /// <summary>Null retains private Chat planning. Explicit targets require IDesktopNativeWorkflowHost.</summary>
    public DesktopWorkflowTarget? Target { get; init; }
}

/// <summary>
/// An exact installed service workflow in this desktop plugin's namespace. Input is copied at admission;
/// the Worker validates its schema, capabilities, dependencies and effective execution limits.
/// </summary>
public sealed record DesktopWorkflowTarget(string DefinitionId, string DefinitionVersion, JsonElement Input, int StepLimit = 64);
public sealed record DesktopInputRequest(DesktopConversationSnapshot Conversation, DesktopSubmission Submission, bool IsSteering);
public enum DesktopInputInterceptionState { Pass, Hold, Cancel }
/// <summary>Pass preserves the exact submission. Hold requires a fresh unparented guidance view and creates no Worker job.</summary>
public sealed record DesktopInputInterception(DesktopInputInterceptionState State, string? Message = null, FrameworkElement? Guidance = null);
/// <summary>
/// One always-available, plugin-owned composer command. A command may consume only a prefix of the exact draft;
/// the host owns the remaining submission, attachments, conversation binding and final dispatch.
/// </summary>
public sealed record DesktopComposerCommand(string Id,
    Func<DesktopInputRequest, CancellationToken, Task<DesktopComposerCommandResult>> ExecuteAsync);
public enum DesktopComposerCommandState { Pass, Continue, Handled }
/// <summary>
/// Continue removes ConsumedPrefixCharacters and submits the remaining draft. Handled consumes the command without
/// creating agent work. Plugins cannot replace arbitrary user text through this contract.
/// </summary>
public sealed record DesktopComposerCommandResult(DesktopComposerCommandState State,
    int ConsumedPrefixCharacters = 0, string? Message = null);
public sealed record DesktopMessageSnapshot(string Id, DesktopConversationBinding Binding, string Role, string Text, DateTimeOffset CreatedAt, bool Truncated = false);
public enum DesktopOperationState { Accepted, Completed, Rejected, Failed, Uncertain }
public sealed record DesktopOperationResult(DesktopOperationState State, string? Message = null, string? RequestId = null, string? JobId = null);

/// <summary>The signed-in desktop process currently hosting the plugin instance.</summary>
public enum DesktopPluginSurface { Orchestrator, Configuration }

/// <summary>Optional host details for plugins that keep removable, per-user settings outside their replaceable package.</summary>
public interface IDesktopPluginEnvironment
{
    DesktopPluginSurface Surface { get; }
    string DataDirectory { get; }
    string PackageDirectory { get; }
}

/// <summary>
/// Host-owned product locations. Plugins should compose their private files below DataDirectory and use the
/// shared Models/Tools roots instead of persisting guessed installation paths.
/// </summary>
public sealed record DesktopProductPaths(string InstallDirectory, string DataDirectory, string UserDataDirectory,
    string ProjectsDirectory, string ModelsDirectory, string ToolsDirectory, string SharedDirectory)
{
    /// <summary>The stable base from which standard installed or portable product roots are derived.</summary>
    public string BaseDirectory { get; init; } = Path.GetDirectoryName(InstallDirectory) ?? InstallDirectory;
    public bool Portable { get; init; }
}
public interface IDesktopProductEnvironment
{
    DesktopProductPaths ProductPaths { get; }
}

/// <summary>Result from an authenticated service command issued by a trusted in-process desktop plugin.</summary>
public sealed record DesktopServiceCommandResult(bool Success, JsonElement Data, string? Error = null);

/// <summary>
/// Optional access to the same structured Worker commands used by the hosting GUI. Native plugins already execute
/// with the signed-in user's trust; the service still validates the command and owns its persistent state.
/// </summary>
public interface IDesktopServiceHost
{
    Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>Host-owned conversation allocation exposed narrowly to trusted desktop plugins.</summary>
public sealed record DesktopResponseBalanceSnapshot(DesktopConversationBinding Binding, int? RequestedSharePercent,
    int ContextTokens, int EffectiveResponseTokens);
public interface IDesktopConversationSettingsHost
{
    DesktopResponseBalanceSnapshot GetResponseBalance();
    Task<DesktopOperationResult> SetResponseBalanceAsync(DesktopConversationBinding expectedBinding,
        int? responseSharePercent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional host-owned clipboard boundary. Plugins use this instead of writing the process clipboard directly so
/// deterministic hosts can capture the requested text without changing the signed-in user's clipboard.
/// </summary>
public interface IDesktopClipboardHost
{
    Task<DesktopOperationResult> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Optional trusted host path for a plugin-owned workflow that must not expose or misattribute its internal handoff.</summary>
public interface IDesktopWorkflowHost
{
    Task<DesktopOperationResult> SubmitWorkflowAsync(DesktopWorkflowSubmission submission,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability marker for explicit native Work targets. A plugin must check for this interface before
/// submitting Target; older hosts only support private Chat handoffs and must not receive an explicit target.
/// </summary>
public interface IDesktopNativeWorkflowHost : IDesktopWorkflowHost { }

public sealed class DesktopConversationChangedEventArgs(DesktopConversationSnapshot conversation) : EventArgs
{
    public DesktopConversationSnapshot Conversation { get; } = conversation;
}
public sealed class DesktopLeadMessageEventArgs(DesktopMessageSnapshot message) : EventArgs
{
    public DesktopMessageSnapshot Message { get; } = message;
}

/// <summary>Host calls and events run on the WPF dispatcher. The host enforces current binding and existing service permissions.</summary>
public interface IDesktopPluginHost
{
    DesktopConversationSnapshot GetConversation();
    DesktopDraftSnapshot GetDraft();
    event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged;
    event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted;
    Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default);
    Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default);
    Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default);
    Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken cancellationToken = default);
    /// <summary>Closes the current plugin-owned over-composer guidance without changing the draft or conversation.</summary>
    void DismissInputGuidance();
    void ReportStatus(string text);
    void RefreshPresentation();
}

/// <summary>
/// Whether the current scoped host can route toward a declared Configuration panel. Availability
/// does not verify that the receiver has selected or rendered that panel.
/// </summary>
public sealed record DesktopSettingsNavigationAvailability(bool Available, string? Message = null);

/// <summary>
/// Optional host-owned navigation to a plugin's declared Configuration panel. The scoped host supplies
/// its plugin identity and loaded package generation; callers supply only that plugin's panel ID.
/// Completed means the matching receiver reported the exact panel visible and ready. Rejected is a
/// confirmed refusal. Accepted or Uncertain does not confirm selection; inspect the Configuration
/// window before another action and do not automatically replay a possible navigation effect.
/// </summary>
public interface IDesktopPluginSettingsNavigationHost
{
    DesktopSettingsNavigationAvailability GetSettingsNavigationAvailability(string panelId);
    Task<DesktopOperationResult> OpenSettingsAsync(string panelId, CancellationToken cancellationToken = default);
}

/// <summary>An Orchestrator contribution mounted as its own tab in the collapsible right sidebar.</summary>
public sealed record DesktopPanel(string Id, string Title, Func<FrameworkElement> CreateView);
public sealed record DesktopAction(string Id, string Label, Func<CancellationToken, Task> ExecuteAsync, string? ToolTip = null);
/// <summary>A host-owned editable text surface. InsertAsync replaces its current selection and keeps the host's normal text limits.</summary>
public interface IDesktopTextEntry
{
    string Id { get; }
    string Purpose { get; }
    string Text { get; }
    int SelectionStart { get; }
    int SelectionLength { get; }
    Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default);
}
/// <summary>Optional status channel for an action operating on one host-owned text entry.</summary>
public enum DesktopTextEntryActivityState { Idle, Starting, Listening, Stopping, Result, Error }
public sealed record DesktopTextEntryActivity(string PluginId, string ActionId, DesktopTextEntryActivityState State,
    string Message, bool Active, string? ButtonLabel = null)
{
    /// <summary>Optional processed microphone peak from 0 through 1 for an in-context live input meter.</summary>
    public double? InputLevel { get; init; }
}
public interface IDesktopTextEntryActivitySink
{
    void ReportActivity(DesktopTextEntryActivity activity);
}
/// <summary>Optional lifetime exposed by transient text entries such as an open question answer.</summary>
public interface IDesktopTextEntryLifetime
{
    CancellationToken AvailabilityToken { get; }
}
/// <summary>Optional focus capability for a host-owned editable text surface.</summary>
public interface IDesktopTextEntryFocus
{
    bool IsKeyboardFocusWithin { get; }
    Task<DesktopOperationResult> FocusAsync(CancellationToken cancellationToken = default);
}
/// <summary>Raised when the host's active plugin-accessible editable text surface changes.</summary>
public sealed record DesktopTextEntryFocusChangedEventArgs(IDesktopTextEntry? Entry);
/// <summary>
/// Optional Orchestrator capability for plugins whose behavior follows the user's active editable field.
/// A null entry means that focus is outside every plugin-accessible text surface.
/// </summary>
public interface IDesktopTextEntryFocusHost
{
    IDesktopTextEntry? FocusedTextEntry { get; }
    event EventHandler<DesktopTextEntryFocusChangedEventArgs>? FocusedTextEntryChanged;
}
/// <summary>
/// Optional Main-window capability for the current host-owned composer. It may be null while the
/// composer or its owner is unavailable. The returned entry keeps its own availability and edit checks.
/// </summary>
public interface IDesktopDefaultTextEntryHost
{
    IDesktopTextEntry? DefaultTextEntry { get; }
    /// <summary>Older hosts default to false. Support applies only to their current owned Main composer.</summary>
    bool SupportsDraftPresentation => false;
}
public sealed record DesktopTextEntryAction(string Id, string Label,
    Func<IDesktopTextEntry, CancellationToken, Task> ExecuteAsync, string? ToolTip = null);
/// <summary>An explicitly selected per-conversation input mode. It cannot rewrite the user's submission.</summary>
public sealed record DesktopInputMode(string Id, string Label,
    Func<DesktopInputRequest, CancellationToken, Task<DesktopInputInterception>> InterceptAsync, string? ToolTip = null)
{
    /// <summary>Expose this interceptor as an independent checkbox that applies across every selected input mode.</summary>
    public bool QuickToggle { get; init; }
    /// <summary>Optional preparation performed whenever the checkbox is enabled, such as loading its configured model.</summary>
    public Func<CancellationToken, Task<string?>>? PrepareAsync { get; init; }
}
/// <summary>Presentation only: the application retains the original message for copy, context and persistence.</summary>
public sealed record DesktopMessagePresentation(FrameworkElement? Replacement = null, FrameworkElement? Decoration = null);
/// <summary>Behavioral defaults only. The host supplies model binding, paths, capabilities, and tools.</summary>
public sealed record DesktopAgentTemplate(string Id, string Name, string ExecutionRole, string Purpose, string Instructions, bool AutomaticBudgets = true)
{
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    public IReadOnlyList<string> RequiredTools { get; init; } = [];
    /// <summary>Execution defaults for specializations that normally require more than the core helper's short tool loop.</summary>
    public int MaxSteps { get; init; } = 12;
    public int MaxSeconds { get; init; } = 600;
    /// <summary>Opaque settings persisted under the contributing plugin ID. The desktop host validates bounds but not plugin schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> PluginSettings { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>Optional Configuration-only panels. Ordinary Panels remain Orchestrator contributions.</summary>
public interface IDesktopConfigurationPlugin
{
    IReadOnlyList<DesktopPanel> ConfigurationPanels { get; }
}

/// <summary>Optional actions mounted beside supported Orchestrator text-entry surfaces.</summary>
public interface IDesktopTextEntryPlugin
{
    IReadOnlyList<DesktopTextEntryAction> TextEntryActions { get; }
}

/// <summary>
/// Optional preparation performed immediately before the host freezes one text entry for submission.
/// A participant may finish an in-flight edit such as speech transcription, but it cannot submit the
/// message itself. Completed or Accepted lets the host continue; every other state keeps the draft local.
/// </summary>
public interface IDesktopTextEntrySubmissionPlugin
{
    Task<DesktopOperationResult> PrepareSubmissionAsync(IDesktopTextEntry target,
        CancellationToken cancellationToken = default);
}

/// <summary>Host proof of one accepted user Send and its exact empty-composer transition. Contains no message text.</summary>
public sealed record DesktopTextEntrySubmissionAcknowledgment(string RequestId, DesktopConversationBinding Binding,
    long SubmittedDraftRevision, long EmptyDraftRevision);

/// <summary>
/// Optional request-correlated preparation and one-use acknowledgment. The host notifies only the successfully
/// prepared, still-loaded plugin generation after an accepted user submission clears that exact draft.
/// Failure, cancellation, manual clearing and assistant output never acknowledge submission. A callback may
/// rearm an existing user-requested feature, but must not submit, redirect focus or replay uncertain edits.
/// Hosts and plugins implementing only the original preparation interface retain its existing behavior.
/// </summary>
public interface IDesktopTextEntrySubmissionLifecyclePlugin : IDesktopTextEntrySubmissionPlugin
{
    Task<DesktopOperationResult> PrepareSubmissionAsync(string requestId, IDesktopTextEntry target,
        CancellationToken cancellationToken = default);
    void SubmissionAcknowledged(DesktopTextEntrySubmissionAcknowledgment acknowledgment);
}

/// <summary>Optional guided input modes. The Orchestrator activates at most one mode for a conversation.</summary>
public interface IDesktopInputModePlugin
{
    IReadOnlyList<DesktopInputMode> InputModes { get; }
}

/// <summary>
/// Optional explicit composer commands. They run before selectable input guidance and remain available whenever the
/// package is enabled. The host validates every returned prefix length and owns the resulting submission.
/// </summary>
public interface IDesktopComposerCommandPlugin
{
    IReadOnlyList<DesktopComposerCommand> ComposerCommands { get; }
}

/// <summary>Optional Configuration-only agent specializations. ExecutionRole is lead, helper, or compactor.</summary>
public interface IDesktopAgentTemplatePlugin
{
    IReadOnlyList<DesktopAgentTemplate> AgentTemplates { get; }
}

/// <summary>Optional passive startup completion for work begun by Initialize, such as
/// adopting acknowledged preferences. Success confirms only the participant's declared
/// work. Propagate the original failure/cancellation; never arm capture, start inference,
/// submit work or change settings here. A retry creates a new plugin instance.</summary>
public interface IDesktopPluginStartupReadiness
{
    Task WaitForStartupAsync(CancellationToken cancellationToken);
}

public interface IDesktopPlugin : IDisposable
{
    void Initialize(IDesktopPluginHost host);
    IReadOnlyList<DesktopPanel> Panels { get; }
    IReadOnlyList<DesktopAction> ComposerActions { get; }
    DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message);
}
