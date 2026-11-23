using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    private static readonly DependencyProperty ActivityStepMarkerProperty = DependencyProperty.RegisterAttached(
        "ActivityStepMarker", typeof(string), typeof(WorkflowWorkspace), new PropertyMetadata("○"));
    private static readonly DependencyProperty ActivityAnimationBindingProperty = DependencyProperty.RegisterAttached(
        "ActivityAnimationBinding", typeof(ActivityAnimationElementBinding), typeof(WorkflowWorkspace));
    private readonly Dictionary<Expander, ActivityCaptionAnimation> activityCaptionAnimations = new();
    private DispatcherTimer? activityAnimationTimer;
    private bool activityAnimationLifecycleAttached;
    private bool activityAnimationSystemSubscribed;
    // Isolated native fixtures may exercise clocks without changing the user's Windows preferences.
    private Func<bool>? activityAnimationMotionPolicyForChecks = null;

    private sealed class ActivityCaptionAnimation(ActivityRowView row, long selection, long connection, long feed)
    {
        internal ActivityRowView Row = row;
        internal long Selection = selection, Connection = connection, Feed = feed;
        internal TextBlock? Caption, Marker;
        internal SolidColorBrush? AnimatedBrush;
    }

    private sealed record ActivityAnimationElementBinding(WeakReference<WorkflowWorkspace> Workspace, Expander Entry);

    internal static bool ActivityAnimationPermitted(bool systemMotionEnabled, bool highContrast, bool reducedMotion) =>
        systemMotionEnabled && !highContrast && !reducedMotion;

    internal static bool IsCurrentAnimatedActivityStep(ActivityRow row, WorkflowConversationRun run) =>
        run.Status == WorkflowStatus.Running && !run.CancelRequested && run.EffectState == WorkflowEffectState.Dispatched &&
        row.Call is null && row.Event.Type == CerebralEventTypes.ToolCall && row.Event.Actor.Kind == EventActorKind.Agent &&
        row.Event.Actor.Id == run.ProfileId && TryActivityOperationIdentity(row.Event, out var operation) &&
        operation.ConversationId == run.ConversationId && operation.TurnId == run.Id && operation.CallId == run.EffectId;

    private void UpdateActivityAnimation(ActivityRowView row, WorkflowConversationRun run)
    {
        AttachActivityAnimationLifecycle();
        if (!activityCaptionAnimations.TryGetValue(row.Expander, out var state))
        {
            activityCaptionAnimations[row.Expander] = state = new(row, generation, connectionEpoch, generationPreviewFeedGeneration);
            // Feed reset stops and discards the old binding, but retained row controls can
            // remain Loaded. A fresh journal reconciliation must rebind those same visuals.
            state.Caption = FindActivityAnimationText(row.Expander, "WorkflowActivityStepText");
            state.Marker = FindActivityAnimationText(row.Expander, "WorkflowActivityStepMarker");
        }
        else
        {
            state.Row = row; state.Selection = generation; state.Connection = connectionEpoch; state.Feed = generationPreviewFeedGeneration;
        }
        row.Expander.SetValue(ActivityStepMarkerProperty, ActivityStepMarker(row.Model, run));
        RefreshActivityCaptionAnimation(state);
    }

    private static TextBlock? FindActivityAnimationText(DependencyObject root, string automationId)
    {
        if (root is TextBlock text && AutomationProperties.GetAutomationId(text) == automationId) return text;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindActivityAnimationText(VisualTreeHelper.GetChild(root, index), automationId) is { } found) return found;
        return null;
    }

    private static string ActivityStepMarker(ActivityRow row, WorkflowConversationRun run)
    {
        if (IsCurrentAnimatedActivityStep(row, run)) return "●";
        var outcome = RecordedActivityOutcomes([row.Event]);
        return outcome.Completed > 0 ? "✓" : outcome.Failed > 0 ? "!" : outcome.Cancelled > 0 ? "■" :
            outcome.Unconfirmed > 0 ? "?" : "○";
    }

    private void AttachActivityAnimationLifecycle()
    {
        if (!activityAnimationLifecycleAttached)
        {
            activityAnimationLifecycleAttached = true;
            Loaded += (_, _) => { SubscribeActivityAnimationSystem(); RefreshActivityAnimations(); };
            Unloaded += (_, _) => StopActivityAnimations();
            _ = lifetime.Token.Register(() =>
            {
                if (Dispatcher.CheckAccess()) StopActivityAnimations();
                else PassiveDesktopInteraction.Defer(Dispatcher, "activity animation cleanup", StopActivityAnimations, message => report(message, true));
            });
            activityAnimationTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(200) };
            activityAnimationTimer.Tick += (_, _) => RefreshActivityAnimations();
        }
        if (IsLoaded) SubscribeActivityAnimationSystem();
    }

    private void SubscribeActivityAnimationSystem()
    {
        if (activityAnimationSystemSubscribed || disposed) return;
        SystemParameters.StaticPropertyChanged += ActivityAnimationSystemChanged;
        activityAnimationSystemSubscribed = true;
    }

    private void ActivityAnimationSystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Dispatcher.CheckAccess()) RefreshActivityAnimations();
        else PassiveDesktopInteraction.Defer(Dispatcher, "activity animation preference", RefreshActivityAnimations, message => report(message, true));
    }

    private void RefreshActivityAnimations()
    {
        if (disposed || !IsLoaded) { StopActivityAnimations(); return; }
        foreach (var state in activityCaptionAnimations.Values.ToArray()) RefreshActivityCaptionAnimation(state);
        if (activityCaptionAnimations.Values.Any(state => state.AnimatedBrush is not null)) activityAnimationTimer?.Start();
        else activityAnimationTimer?.Stop();
    }

    private void RefreshActivityCaptionAnimation(ActivityCaptionAnimation state)
    {
        var snapshot = conversation?.Snapshot;
        var selected = !disposed && IsLoaded && state.Selection == generation && state.Connection == connectionEpoch &&
            state.Feed == generationPreviewFeedGeneration && snapshot is { MutationUncertain: false, Run: { } run } &&
            IsCurrentAnimatedActivityStep(state.Row.Model, run);
        var permitted = activityAnimationMotionPolicyForChecks?.Invoke() ??
            ActivityAnimationPermitted(SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast, reducedMotion: false);
        if (!selected || !permitted || state.Caption is not { IsVisible: true } caption)
        { StopActivityCaptionAnimation(state); return; }
        if (state.AnimatedBrush is { } activeBrush)
        {
            if (!ReferenceEquals(caption.Foreground, activeBrush)) caption.Foreground = activeBrush;
            if (state.Marker is { } activeMarker && !ReferenceEquals(activeMarker.Foreground, activeBrush)) activeMarker.Foreground = activeBrush;
            return;
        }
        if (state.Row.Expander.Foreground is not SolidColorBrush original) return;
        // Animate only opacity on a private brush. Public caption, layout, metrics and event
        // identity stay unchanged; a static caption/marker remains when motion is disabled.
        var brush = new SolidColorBrush(original.Color);
        brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(.70, 1, TimeSpan.FromSeconds(1.5))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, FillBehavior = FillBehavior.Stop });
        state.AnimatedBrush = brush; caption.Foreground = brush;
        if (state.Marker is { } marker) marker.Foreground = brush;
        activityAnimationTimer?.Start();
    }

    private static void StopActivityCaptionAnimation(ActivityCaptionAnimation state)
    {
        if (state.AnimatedBrush is null) return;
        state.AnimatedBrush.BeginAnimation(Brush.OpacityProperty, null);
        state.Caption?.ClearValue(TextBlock.ForegroundProperty);
        state.Marker?.ClearValue(TextBlock.ForegroundProperty);
        state.AnimatedBrush = null;
    }

    private void RemoveActivityAnimation(Expander entry)
    {
        if (activityCaptionAnimations.Remove(entry, out var state)) StopActivityCaptionAnimation(state);
    }

    private void StopActivityAnimations()
    {
        activityAnimationTimer?.Stop();
        foreach (var state in activityCaptionAnimations.Values) StopActivityCaptionAnimation(state);
        activityCaptionAnimations.Clear();
        if (!activityAnimationSystemSubscribed) return;
        SystemParameters.StaticPropertyChanged -= ActivityAnimationSystemChanged;
        activityAnimationSystemSubscribed = false;
    }

    private static void ConfigureActivityCaptionAnimation(FrameworkElementFactory caption)
    {
        caption.SetValue(AutomationProperties.AutomationIdProperty, "WorkflowActivityStepText");
        caption.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(ActivityAnimationElementLoaded));
        caption.AddHandler(FrameworkElement.UnloadedEvent, new RoutedEventHandler(ActivityAnimationElementUnloaded));
    }

    private static FrameworkElementFactory ActivityStepMarkerFactory()
    {
        var marker = new FrameworkElementFactory(typeof(TextBlock));
        marker.SetValue(AutomationProperties.AutomationIdProperty, "WorkflowActivityStepMarker");
        marker.SetValue(TextBlock.FontSizeProperty, 10d); marker.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 7, 0));
        marker.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(ActivityStepMarkerProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Expander), 1) });
        marker.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(ActivityAnimationElementLoaded));
        marker.AddHandler(FrameworkElement.UnloadedEvent, new RoutedEventHandler(ActivityAnimationElementUnloaded));
        marker.SetValue(DockPanel.DockProperty, Dock.Left);
        return marker;
    }

    private static void ActivityAnimationElementLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not TextBlock text || ActivityAnimationOwner(text, out var entry) is not { } workspace) return;
        var row = workspace.activityViews.SelectMany(view => view.Rows.Values).FirstOrDefault(row => ReferenceEquals(row.Expander, entry));
        if (row is null || workspace.Snapshot?.Run is not { } run) return;
        workspace.UpdateActivityAnimation(row, run);
        var state = workspace.activityCaptionAnimations[entry!];
        text.SetValue(ActivityAnimationBindingProperty, new ActivityAnimationElementBinding(new(workspace), entry!));
        if (AutomationProperties.GetAutomationId(text) == "WorkflowActivityStepMarker") state.Marker = text; else state.Caption = text;
        text.IsVisibleChanged -= ActivityAnimationElementVisibilityChanged;
        text.IsVisibleChanged += ActivityAnimationElementVisibilityChanged;
        workspace.RefreshActivityCaptionAnimation(state);
    }

    private static void ActivityAnimationElementUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not TextBlock text) return;
        text.IsVisibleChanged -= ActivityAnimationElementVisibilityChanged;
        var workspace = ActivityAnimationOwner(text, out var entry);
        text.ClearValue(ActivityAnimationBindingProperty);
        if (workspace is null || entry is null || !workspace.activityCaptionAnimations.TryGetValue(entry, out var state)) return;
        var ownsCaption = ReferenceEquals(state.Caption, text); var ownsMarker = ReferenceEquals(state.Marker, text);
        if (ownsCaption || ownsMarker) StopActivityCaptionAnimation(state);
        if (ownsCaption) state.Caption = null;
        if (ownsMarker) state.Marker = null;
        if (state.Caption is null && state.Marker is null) workspace.activityCaptionAnimations.Remove(entry);
    }

    private static void ActivityAnimationElementVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TextBlock text && ActivityAnimationOwner(text, out var entry) is { } workspace && entry is not null &&
            workspace.activityCaptionAnimations.TryGetValue(entry, out var state)) workspace.RefreshActivityCaptionAnimation(state);
    }

    private static WorkflowWorkspace? ActivityAnimationOwner(DependencyObject child, out Expander? entry)
    {
        entry = null;
        if (child.GetValue(ActivityAnimationBindingProperty) is ActivityAnimationElementBinding binding && binding.Workspace.TryGetTarget(out var bound))
        { entry = binding.Entry; return bound; }
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (entry is null && current is Expander expander) entry = expander;
            if (current is WorkflowWorkspace workspace) return workspace;
        }
        return null;
    }
}
