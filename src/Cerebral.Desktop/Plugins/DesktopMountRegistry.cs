using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cerebral.Desktop.Plugins;

internal sealed record DesktopMountedContribution(
    string PluginId,
    DesktopMountDescriptor Descriptor,
    DesktopMountKind Kind,
    object Value,
    bool CompatibilityAdapter = false)
{
    public string QualifiedId => PluginId + ":" + Descriptor.Id;
}

internal static class DesktopMountRegistry
{
    private static readonly IReadOnlyDictionary<DesktopPluginSurface, IReadOnlyDictionary<string, DesktopMountKind[]>> Supported =
        new Dictionary<DesktopPluginSurface, IReadOnlyDictionary<string, DesktopMountKind[]>>
        {
            [DesktopPluginSurface.Orchestrator] = new Dictionary<string, DesktopMountKind[]>(StringComparer.Ordinal)
            {
                [DesktopMountSlots.ComposerActions] = [DesktopMountKind.Action, DesktopMountKind.TextEntryAction],
                [DesktopMountSlots.ComposerAccessories] = [DesktopMountKind.View],
                [DesktopMountSlots.AnswerActions] = [DesktopMountKind.TextEntryAction],
                [DesktopMountSlots.WorkspaceActions] = [DesktopMountKind.Action],
                [DesktopMountSlots.WorkspaceTree] = [DesktopMountKind.View],
                [DesktopMountSlots.WorkspacePanels] = [DesktopMountKind.View],
                [DesktopMountSlots.NewConversationStart] = [DesktopMountKind.View],
                [DesktopMountSlots.SidebarTabs] = [DesktopMountKind.View],
                [DesktopMountSlots.StatusBar] = [DesktopMountKind.View],
                [DesktopMountSlots.Menus] = [DesktopMountKind.Action],
                [DesktopMountSlots.MessageRenderers] = [DesktopMountKind.MessageRenderer],
                [DesktopMountSlots.MessageDecorations] = [DesktopMountKind.MessageDecoration]
            },
            [DesktopPluginSurface.Configuration] = new Dictionary<string, DesktopMountKind[]>(StringComparer.Ordinal)
            {
                [DesktopMountSlots.ConfigurationPages] = [DesktopMountKind.View]
            }
        };

    public static IReadOnlyList<DesktopMountCapability> Capabilities(DesktopPluginSurface surface)
    {
        var supported = Supported[surface];
        return DesktopMountSlots.Known.Select(slot => supported.TryGetValue(slot, out var kinds)
            ? new DesktopMountCapability(slot, DesktopMountContract.Version, true, Array.AsReadOnly(kinds.ToArray()))
            : new DesktopMountCapability(slot, DesktopMountContract.Version, false, Array.Empty<DesktopMountKind>(),
                "This host surface does not currently mount this slot.")).ToArray();
    }

    public static IReadOnlyList<DesktopMountedContribution> Resolve(
        IEnumerable<DesktopMountedContribution> source, DesktopPluginSurface surface)
    {
        ArgumentNullException.ThrowIfNull(source);
        var all = source.ToArray();
        foreach (var item in all) ValidateDescriptor(item);

        var applicable = all.Where(item => AppliesToSurface(item.Descriptor, surface)).ToArray();
        var supported = Supported[surface];
        foreach (var item in applicable)
        {
            if (!supported.TryGetValue(item.Descriptor.Slot, out var kinds))
                throw new InvalidDataException($"Mount '{item.QualifiedId}' requests unsupported slot '{item.Descriptor.Slot}' on {surface}.");
            if (!kinds.Contains(item.Kind))
                throw new InvalidDataException($"Mount '{item.QualifiedId}' kind {item.Kind} is invalid for slot '{item.Descriptor.Slot}'.");
        }

        var duplicate = applicable.GroupBy(item => (item.Descriptor.Slot, item.QualifiedId))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Duplicate mount ID '{duplicate.Key.QualifiedId}' in slot '{duplicate.Key.Slot}'.");

        var targeted = new HashSet<(string Slot, string QualifiedId)>();
        var byKey = applicable.ToDictionary(item => (item.Descriptor.Slot, item.QualifiedId));
        foreach (var item in applicable.Where(item => item.Descriptor.Replaces is not null))
        {
            var target = item.Descriptor.Replaces!;
            var targetKey = (item.Descriptor.Slot, target);
            if (target.Equals(item.QualifiedId, StringComparison.Ordinal))
                throw new InvalidDataException($"Mount '{item.QualifiedId}' cannot replace itself.");
            if (!byKey.ContainsKey(targetKey))
                throw new InvalidDataException($"Mount '{item.QualifiedId}' replacement target '{target}' is unavailable in slot '{item.Descriptor.Slot}'. Declare the provider as a package dependency.");
            if (!targeted.Add(targetKey))
                throw new InvalidDataException($"More than one mount replaces '{target}' in slot '{item.Descriptor.Slot}'. Choose one provider explicitly.");
        }

        foreach (var start in applicable)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = start;
            while (current.Descriptor.Replaces is { } target)
            {
                if (!seen.Add(current.QualifiedId))
                    throw new InvalidDataException($"Replacement cycle detected in slot '{start.Descriptor.Slot}' at '{current.QualifiedId}'.");
                current = byKey[(current.Descriptor.Slot, target)];
            }
        }

        var active = applicable.Where(item => !targeted.Contains((item.Descriptor.Slot, item.QualifiedId)))
            .OrderBy(item => item.Descriptor.Order)
            .ThenBy(item => item.QualifiedId, StringComparer.Ordinal)
            .ToArray();

        var renderers = active.Where(item => item.Descriptor.Slot == DesktopMountSlots.MessageRenderers).ToArray();
        for (var left = 0; left < renderers.Length; left++)
        for (var right = left + 1; right < renderers.Length; right++)
            if (RoleSetsOverlap(renderers[left].Descriptor.Applicability.MessageRoles,
                    renderers[right].Descriptor.Applicability.MessageRoles))
                throw new InvalidDataException($"Message renderers '{renderers[left].QualifiedId}' and '{renderers[right].QualifiedId}' have overlapping applicability. Select one with Replaces or use disjoint message roles.");

        return active;
    }

    public static bool AppliesToMessage(DesktopMountDescriptor descriptor, DesktopMessageSnapshot message)
    {
        var roles = descriptor.Applicability.MessageRoles;
        return roles.Count == 0 || roles.Contains(message.Role, StringComparer.OrdinalIgnoreCase);
    }

    public static bool AppliesToTextEntry(DesktopMountDescriptor descriptor, string id)
    {
        var targets = descriptor.Applicability.TextEntryIds;
        return targets.Count == 0 || targets.Contains(id, StringComparer.Ordinal);
    }

    public static DesktopMountDescriptor Freeze(DesktopMountDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var applicability = value.Applicability ?? throw new InvalidDataException("A mount descriptor needs applicability metadata.");
        return value with
        {
            Applicability = applicability with
            {
                MessageRoles = applicability.MessageRoles?.ToArray() ?? throw new InvalidDataException("MessageRoles cannot be null."),
                TextEntryIds = applicability.TextEntryIds?.ToArray() ?? throw new InvalidDataException("TextEntryIds cannot be null.")
            }
        };
    }

    private static void ValidateDescriptor(DesktopMountedContribution item)
    {
        if (string.IsNullOrWhiteSpace(item.PluginId)) throw new InvalidDataException("A mount contribution needs a package ID.");
        var descriptor = item.Descriptor ?? throw new InvalidDataException("A mount contribution returned no descriptor.");
        if (descriptor.ContractVersion != DesktopMountContract.Version)
            throw new InvalidDataException($"Mount '{item.PluginId}:{descriptor.Id}' requires unsupported mount contract version {descriptor.ContractVersion}.");
        if (!ValidLocalId(descriptor.Id))
            throw new InvalidDataException("Mount IDs must be 1-64 lowercase letters, digits, dots, dashes, or underscores and start with a letter or digit.");
        if (!DesktopMountSlots.Known.Contains(descriptor.Slot, StringComparer.Ordinal))
            throw new InvalidDataException($"Mount '{item.QualifiedId}' uses unknown slot '{descriptor.Slot}'.");
        if (descriptor.Order is < -100_000 or > 100_000)
            throw new InvalidDataException($"Mount '{item.QualifiedId}' order is outside the supported range.");
        var applicability = descriptor.Applicability ?? throw new InvalidDataException($"Mount '{item.QualifiedId}' needs applicability metadata.");
        if (applicability.Surfaces == DesktopMountSurfaces.None ||
            (applicability.Surfaces & ~DesktopMountSurfaces.All) != 0)
            throw new InvalidDataException($"Mount '{item.QualifiedId}' has invalid surface applicability.");
        ValidateSelectors(applicability.MessageRoles, "message role", item.QualifiedId);
        ValidateSelectors(applicability.TextEntryIds, "text-entry ID", item.QualifiedId);
        if (descriptor.Replaces is { } replacement && !ValidQualifiedId(replacement))
            throw new InvalidDataException($"Mount '{item.QualifiedId}' has invalid replacement target '{replacement}'.");
    }

    public static bool AppliesToSurface(DesktopMountDescriptor descriptor, DesktopPluginSurface surface) =>
        surface switch
        {
            DesktopPluginSurface.Orchestrator => descriptor.Applicability.Surfaces.HasFlag(DesktopMountSurfaces.Orchestrator),
            DesktopPluginSurface.Configuration => descriptor.Applicability.Surfaces.HasFlag(DesktopMountSurfaces.Configuration),
            _ => false
        };

    private static void ValidateSelectors(IReadOnlyList<string>? values, string name, string id)
    {
        if (values is null || values.Count > 32 || values.Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Contains('\0')))
            throw new InvalidDataException($"Mount '{id}' has invalid or excessive {name} applicability.");
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
            throw new InvalidDataException($"Mount '{id}' repeats a {name} applicability value.");
    }

    private static bool RoleSetsOverlap(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == 0 || right.Count == 0 || left.Intersect(right, StringComparer.OrdinalIgnoreCase).Any();

    private static bool ValidLocalId(string value) => ValidIdentifier(value, 64);

    private static bool ValidIdentifier(string value, int maximum) => value.Length >= 1 && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool ValidQualifiedId(string value)
    {
        if (value.Length is < 3 or > 160 || value.Count(character => character == ':') != 1) return false;
        var separator = value.IndexOf(':');
        return separator is > 0 and < 159 && ValidIdentifier(value[..separator], 128) && ValidLocalId(value[(separator + 1)..]);
    }
}

public static class DesktopMountLayout
{
    public const double AccessoryMaximumWidth = 280;
    public const double AccessoryMaximumHeight = 44;
    public const double WorkspaceTreeMaximumHeight = 280;
    public const double StatusItemMaximumWidth = 320;
    public const double StatusItemMaximumHeight = 30;

    /// <summary>Creates the host-owned clipping boundary used for composer accessories.</summary>
    public static Border AccessoryFrame(FrameworkElement view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Parent is not null || VisualTreeHelper.GetParent(view) is not null)
            throw new InvalidDataException("A composer accessory must be a fresh unparented WPF element.");
        return new Border
        {
            Child = view,
            MaxWidth = AccessoryMaximumWidth,
            MaxHeight = AccessoryMaximumHeight,
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 6, 4)
        };
    }

    /// <summary>Creates a bounded, collapsible branch beside the core project/chat tree.</summary>
    public static Expander WorkspaceTreeFrame(string title, FrameworkElement view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title); ArgumentNullException.ThrowIfNull(view);
        if (view.Parent is not null || VisualTreeHelper.GetParent(view) is not null)
            throw new InvalidDataException("A workspace-tree view must be a fresh unparented WPF element.");
        return new Expander
        {
            Header = title,
            IsExpanded = true,
            Margin = new Thickness(0, 0, 4, 6),
            Content = new ScrollViewer
            {
                Content = view,
                MaxHeight = WorkspaceTreeMaximumHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
    }

    /// <summary>Creates a compact boundary for a plugin-contributed status item.</summary>
    public static Border StatusItemFrame(FrameworkElement view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Parent is not null || VisualTreeHelper.GetParent(view) is not null)
            throw new InvalidDataException("A status-bar view must be a fresh unparented WPF element.");
        return new Border
        {
            Child = view,
            MaxWidth = StatusItemMaximumWidth,
            MaxHeight = StatusItemMaximumHeight,
            ClipToBounds = true,
            Margin = new Thickness(10, 0, 0, 0)
        };
    }
}
