using System.Windows;

namespace Cerebral.Desktop.Plugins;

/// <summary>Versioned native desktop mount metadata. This is independent of the desktop package manifest API.</summary>
public static class DesktopMountContract
{
    public const string Id = "pegasus.cerebral.desktop.mounts";
    public const int Version = 1;
    public const int MaximumContributionsPerPlugin = 64;
}

/// <summary>Stable slot names. A known name is not a promise that a particular host surface mounts it.</summary>
public static class DesktopMountSlots
{
    public const string ComposerActions = "composer.actions";
    public const string ComposerAccessories = "composer.accessories";
    public const string AnswerActions = "answer.actions";
    public const string WorkspaceActions = "workspace.actions";
    public const string WorkspaceTree = "workspace.tree";
    /// <summary>Optional independently collapsible work panel in the Main window. Query host support first.</summary>
    public const string WorkspacePanels = "workspace.panels";
    public const string NewConversationStart = "conversation.start";
    public const string SidebarTabs = "sidebar.tabs";
    public const string StatusBar = "status.bar";
    public const string Menus = "menus";
    public const string MessageRenderers = "message.renderers";
    public const string MessageDecorations = "message.decorations";
    public const string ActivityRenderers = "activity.renderers";
    public const string FormsRenderers = "forms.renderers";
    public const string ConfigurationPages = "configuration.pages";

    public static IReadOnlyList<string> Known { get; } =
    [
        ComposerActions, ComposerAccessories, AnswerActions, WorkspaceActions, WorkspaceTree, WorkspacePanels, NewConversationStart, SidebarTabs, StatusBar,
        Menus, MessageRenderers, MessageDecorations, ActivityRenderers, FormsRenderers, ConfigurationPages
    ];
}

[Flags]
public enum DesktopMountSurfaces
{
    None = 0,
    Orchestrator = 1,
    Configuration = 2,
    All = Orchestrator | Configuration
}

public enum DesktopMountKind
{
    Action,
    TextEntryAction,
    View,
    MessageRenderer,
    MessageDecoration
}

/// <summary>
/// Optional bounded matching. Empty role and text-entry collections mean every target supported by the slot.
/// Matching never grants access and cannot move a contribution to another slot.
/// </summary>
public sealed record DesktopMountApplicability
{
    public DesktopMountSurfaces Surfaces { get; init; } = DesktopMountSurfaces.All;
    public IReadOnlyList<string> MessageRoles { get; init; } = [];
    public IReadOnlyList<string> TextEntryIds { get; init; } = [];
}

/// <summary>
/// Id is stable and local to the package and slot. Replaces, when present, is the exact
/// <c>package-id:local-id</c> in the same slot. Order is ascending, then qualified ID ordinal.
/// </summary>
public sealed record DesktopMountDescriptor(string Id, string Slot)
{
    public int ContractVersion { get; init; } = DesktopMountContract.Version;
    public int Order { get; init; }
    public DesktopMountApplicability Applicability { get; init; } = new();
    public string? Replaces { get; init; }
}

public interface IDesktopMountContribution
{
    DesktopMountDescriptor Descriptor { get; }
    DesktopMountKind Kind { get; }
}

public sealed record DesktopMountAction(DesktopMountDescriptor Descriptor, string Label,
    Func<CancellationToken, Task> ExecuteAsync, string? ToolTip = null) : IDesktopMountContribution
{
    public DesktopMountKind Kind => DesktopMountKind.Action;
}

public sealed record DesktopMountTextEntryAction(DesktopMountDescriptor Descriptor, string Label,
    Func<IDesktopTextEntry, CancellationToken, Task> ExecuteAsync, string? ToolTip = null) : IDesktopMountContribution
{
    public DesktopMountKind Kind => DesktopMountKind.TextEntryAction;
}

public sealed record DesktopMountView(DesktopMountDescriptor Descriptor, string Title,
    Func<FrameworkElement> CreateView) : IDesktopMountContribution
{
    public DesktopMountKind Kind => DesktopMountKind.View;
}

/// <summary>Display-only replacement. The original message remains authoritative for persistence, copy and model context.</summary>
public sealed record DesktopMountMessageRenderer(DesktopMountDescriptor Descriptor,
    Func<DesktopMessageSnapshot, FrameworkElement?> Render) : IDesktopMountContribution
{
    public DesktopMountKind Kind => DesktopMountKind.MessageRenderer;
}

/// <summary>Display-only content mounted after the ordinary or selected replacement message view.</summary>
public sealed record DesktopMountMessageDecoration(DesktopMountDescriptor Descriptor,
    Func<DesktopMessageSnapshot, FrameworkElement?> Render) : IDesktopMountContribution
{
    public DesktopMountKind Kind => DesktopMountKind.MessageDecoration;
}

/// <summary>Optional descriptor-first API. IDesktopPlugin remains the package entry contract for API v1.</summary>
public interface IDesktopMountPlugin
{
    IReadOnlyList<IDesktopMountContribution> MountContributions { get; }
}

public sealed record DesktopMountCapability(string Slot, int ContractVersion, bool Supported,
    IReadOnlyList<DesktopMountKind> Kinds, string? Reason = null);

/// <summary>Optional host discovery. Unsupported known slots are returned explicitly rather than silently ignored.</summary>
public interface IDesktopMountHost
{
    IReadOnlyList<DesktopMountCapability> GetMountCapabilities();
}
