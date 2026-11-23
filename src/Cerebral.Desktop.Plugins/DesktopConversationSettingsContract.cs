using System.Windows;

namespace Cerebral.Desktop.Plugins;

/// <summary>
/// Optional, versioned editors for plugin-owned settings of one saved native conversation.
/// This contract is independent of the desktop package manifest and core response-balance settings.
/// </summary>
public static class DesktopConversationSettingsContract
{
    public const string Id = "pegasus.cerebral.desktop.conversation-settings";
    public const int Version = 1;
    public const int MaximumContributionsPerPlugin = 8;
    public const int MaximumContributionIdChars = 64;
    public const int MaximumTitleChars = 100;
    public const int MaximumStatusChars = 2_000;
}

/// <summary>
/// Exact metadata captured by the host for the Properties target. A sidebar target can differ from
/// IDesktopPluginHost.GetConversation(); do not substitute the current selection for ConversationId.
/// WorkingDirectory is the saved working directory, not a path grant or a confinement boundary.
/// AvailabilityToken is cancelled when the editor's host binding or contributing plugin instance ends.
/// Metadata does not grant conversation mutation, service-command access, or cross-owner storage scope.
/// </summary>
public sealed record DesktopConversationSettingsContext(
    string ConversationId,
    string Title,
    string ProfileId,
    string? ProjectId,
    string? WorkingDirectory,
    CancellationToken AvailabilityToken);

/// <summary>
/// A lazy per-chat settings contribution. Id is local to the plugin; the runtime supplies package ownership.
/// Each opening must return a fresh editor and an unparented WPF View. Opening reads settings into a local
/// draft and does not save them. The host invokes the factory and editor methods on the WPF dispatcher.
/// </summary>
public sealed record DesktopConversationSettingsContribution(
    string Id,
    string Title,
    Func<DesktopConversationSettingsContext, CancellationToken, Task<IDesktopConversationSettingsEditor>> OpenAsync)
{
    public int ContractVersion { get; init; } = DesktopConversationSettingsContract.Version;
}

/// <summary>
/// Additive opt-in for native Orchestrator Properties. Existing desktop plugins need not implement it.
/// ConfigurationPanels remain global Configuration contributions and are not adapted to per-chat editors.
/// Hosts implementing this feature validate the contract version, bounded descriptors, exact loaded instance, target and lifetime.
/// New plugins referencing these types require a host SDK that contains this additive contract.
/// </summary>
public interface IDesktopConversationSettingsPlugin
{
    IReadOnlyList<DesktopConversationSettingsContribution> ConversationSettings { get; }
}

/// <summary>
/// One plugin-owned editor draft. Settings storage and its revision/receipt rules remain owned by the plugin,
/// independently of Cerebral's core preferences and other plugin editors. The host never implies an atomic
/// transaction across these stores. Native plugins retain the signed-in user's in-process trust.
/// </summary>
public interface IDesktopConversationSettingsEditor : IDisposable
{
    /// <summary>A fresh unparented view, retained for this editor session only.</summary>
    FrameworkElement View { get; }

    /// <summary>
    /// Save only this editor's exact captured target. Link cancellation with the context AvailabilityToken
    /// and check it before mutation. Completed means the settings were confirmed saved; Accepted represents
    /// accepted asynchronous work, not proof that persistence finished. Rejected/Failed expose their actual
    /// outcomes. A lost or malformed reply after dispatch is Uncertain and must not be automatically retried.
    /// Cancellation does not prove that a dispatched save was rolled back. Return bounded status text.
    /// </summary>
    Task<DesktopOperationResult> SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discard unsaved local edits. This must be idempotent and does not undo a completed save or resolve an
    /// uncertain dispatched mutation. The host cancels and awaits any in-flight save before final cleanup.
    /// Dispose must release subscriptions, timers and editor resources even when opening or saving failed.
    /// </summary>
    void Cancel();
}
