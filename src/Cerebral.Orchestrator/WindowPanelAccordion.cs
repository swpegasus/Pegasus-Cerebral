using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cerebral.Orchestrator;

// A window presentation rule. It does not move focus, submit work, or own a panel's content.
internal sealed class WindowPanelAccordion : IDisposable
{
    private static readonly DependencyPropertyDescriptor VisibilityDescriptor =
        DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Expander));
    private readonly HashSet<Expander> panels = [];
    private bool changing;
    private Expander? selected;
    private Expander? fallback;
    private bool disposed;

    internal void Register(Expander panel)
    {
        if (!panels.Add(panel)) return;
        fallback ??= panel;
        panel.Expanded += Expanded;
        panel.Collapsed += Collapsed;
        panel.GotKeyboardFocus += Focused;
        panel.PreviewMouseDown += Pointed;
        VisibilityDescriptor.AddValueChanged(panel, VisibilityChanged);
        if (panel.IsExpanded) Open(panel);
    }

    internal void Unregister(Expander panel)
    {
        if (!panels.Remove(panel)) return;
        panel.Expanded -= Expanded;
        panel.Collapsed -= Collapsed;
        panel.GotKeyboardFocus -= Focused;
        panel.PreviewMouseDown -= Pointed;
        VisibilityDescriptor.RemoveValueChanged(panel, VisibilityChanged);
        panel.IsExpanded = false;
        if (ReferenceEquals(fallback, panel) || fallback?.Visibility != Visibility.Visible)
            fallback = panels.FirstOrDefault(other => other.Visibility == Visibility.Visible);
        if (ReferenceEquals(selected, panel))
        { selected = null; if (!disposed && fallback is not null) Open(fallback); }
    }

    internal void Open(Expander panel)
    {
        if (changing || !panels.Contains(panel) || panel.Visibility != Visibility.Visible) return;
        if (ReferenceEquals(selected, panel) && panel.IsExpanded && panels.All(other => ReferenceEquals(other, panel) || !other.IsExpanded)) return;
        selected = panel;
        changing = true;
        try
        {
            // Collapse first: an Expanded callback cannot observe two open folders.
            foreach (var other in panels)
                if (!ReferenceEquals(other, panel)) other.IsExpanded = false;
            panel.IsExpanded = true;
        }
        finally { changing = false; }
    }

    private void Expanded(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, args.OriginalSource)) Open((Expander)sender);
    }

    private void Focused(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (Owner(args.NewFocus as DependencyObject) is not { } owner || !ReferenceEquals(owner, sender)) return;
        // Collapsing a folder may move retained focus from its editor to its own
        // header. That internal move is not a new choice to reopen the folder.
        if (!owner.IsExpanded && ReferenceEquals(Owner(args.OldFocus as DependencyObject), owner)) return;
        Open(owner);
    }

    private void Collapsed(object sender, RoutedEventArgs args)
    {
        if (changing || !ReferenceEquals(sender, args.OriginalSource)) return;
        var panel = (Expander)sender;
        // The selected docking bar stays open even when its native toggle requests
        // collapse. Only choosing another registered bar changes selection.
        if (ReferenceEquals(selected, panel)) Open(panel);
        else if (!panels.Any(other => other.IsExpanded) && fallback is not null) Open(fallback);
    }

    private void VisibilityChanged(object? sender, EventArgs args)
    {
        var panel = (Expander)sender!;
        // Observe the bar's own property. IsVisibleChanged can remain silent while
        // an ancestor is unshown, but that must not strand a hidden selection.
        if (panel.Visibility != Visibility.Visible)
        {
            if (ReferenceEquals(selected, panel) &&
                panels.FirstOrDefault(other => other.Visibility == Visibility.Visible) is { } replacement)
                Open(replacement);
            return;
        }
        if (selected is null || selected.Visibility != Visibility.Visible) Open(panel);
        else if (!ReferenceEquals(selected, panel) && panel.IsExpanded)
        {
            // Restoring a previously hidden expanded bar preserves the live choice.
            changing = true;
            try { panel.IsExpanded = false; }
            finally { changing = false; }
        }
    }

    private void Pointed(object sender, MouseButtonEventArgs args)
    {
        if (Owner(args.OriginalSource as DependencyObject) is { } owner && ReferenceEquals(owner, sender)) Open(owner);
    }

    private Expander? Owner(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is Expander panel && panels.Contains(panel)) return panel;
        return null;
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var panel in panels.ToArray()) Unregister(panel);
    }
}
