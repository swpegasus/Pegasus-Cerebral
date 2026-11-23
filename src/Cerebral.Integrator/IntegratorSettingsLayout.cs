using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly Dictionary<string, string> settingsLayoutSelections = new(StringComparer.Ordinal);
    private SettingsLayoutSession? settingsLayoutSession;
    private DispatcherTimer? settingsLayoutTimer;
    private bool persistCentralSettingsLayout;
    private bool centralSettingsLayoutRead;
    private bool settingsLayoutCloseAllowed;
    private bool settingsLayoutClosing;
    private RowDefinition? toolsTopRow;
    private RowDefinition? toolsBottomRow;
    private double toolsPreferredTopShare = IntegratorSettingsLayoutCodec.DefaultToolsTopShare;

    private void SettingsPaneChanged()
    {
        if (!IsLoaded || settingsLayoutSession is null || settingsLayoutSession.Hydrating) return;
        // Input before the first read is a real local draft, but cannot save without a validated baseline.
        settingsLayoutSession.Changed();
        if (centralSettingsLayoutRead) QueueSettingsLayoutSave();
    }

    private static GridSplitter CreateSettingsRowDivider(string id, string name, RowDefinition top,
        RowDefinition bottom, Action<double> remember)
    {
        var divider = new GridSplitter
        {
            Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Bottom,
            ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = true, Focusable = true, Background = Ui.Line, Cursor = Cursors.SizeNS,
            ToolTip = name + "; drag or use arrow keys when focused."
        };
        AutomationProperties.SetAutomationId(divider, id);
        AutomationProperties.SetName(divider, name);
        (GridLength Top, GridLength Bottom, double ActualTop)? input = null;
        void Begin() => input = (top.Height, bottom.Height, top.ActualHeight);
        void End(bool canceled)
        {
            if (input is not { } before) return;
            input = null;
            if (!canceled && divider.Parent is Grid panel) panel.UpdateLayout();
            // Native no-op input can rewrite star weights to clamped ActualHeight. Retain the
            // original weights when the divider did not move, so expansion restores the preference.
            if (canceled || Math.Abs(top.ActualHeight - before.ActualTop) < .01)
            {
                top.Height = before.Top; bottom.Height = before.Bottom; return;
            }
            var sum = top.Height.Value + bottom.Height.Value;
            if (!top.Height.IsStar || !bottom.Height.IsStar || !double.IsFinite(sum) || sum <= 0)
            {
                top.Height = before.Top; bottom.Height = before.Bottom; return;
            }
            var share = top.Height.Value / sum;
            if (double.IsFinite(share) && share is > 0 and < 1) remember(share);
            else { top.Height = before.Top; bottom.Height = before.Bottom; }
        }
        divider.DragStarted += (_, _) => Begin();
        divider.DragCompleted += (_, args) => End(args.Canceled);
        divider.PreviewKeyDown += (_, args) =>
        {
            if (!divider.IsDragging && (args.Key is Key.Up or Key.Down)) Begin();
        };
        // GridSplitter handles these keys; observe after its native resize, even when marked handled.
        divider.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (!divider.IsDragging && (args.Key is Key.Up or Key.Down)) End(canceled: false);
        }), handledEventsToo: true);
        return divider;
    }

    private void HydrateSettingsPaneSizes(IntegratorSettingsPaneSizes sizes)
    {
        settingsNavigation.HydratePreferredRailWidth(sizes.NavigationPreferredWidth);
        computePreferredTopShare = sizes.MachinesTopShare;
        toolsPreferredTopShare = sizes.ToolsTopShare;
        if (computeListRow is not null && computeDetailRow is not null)
        {
            computeListRow.Height = new(computePreferredTopShare, GridUnitType.Star);
            computeDetailRow.Height = new(1 - computePreferredTopShare, GridUnitType.Star);
        }
        if (toolsTopRow is not null && toolsBottomRow is not null)
        {
            toolsTopRow.Height = new(toolsPreferredTopShare, GridUnitType.Star);
            toolsBottomRow.Height = new(1 - toolsPreferredTopShare, GridUnitType.Star);
        }
    }

    private void InitializeSettingsLayout(bool persist)
    {
        persistCentralSettingsLayout = persist;
        if (!persist) return;
        // Legacy preferences remain a read-only fallback. The base file auto-writer is deliberately disabled.
        WindowPreferenceStore.RestoreLegacyGeometry(this, "Integrator");
        settingsLayoutSession = new(CaptureSettingsLayout, ApplySettingsLayoutAsync);
        settingsLayoutTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(450) };
        settingsLayoutTimer.Tick += async (_, _) =>
        {
            settingsLayoutTimer.Stop();
            if (settingsPresetClient is not { IsCurrent: true } owner || settingsLayoutSession is not { Supported: true, Blocked: false }) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await settingsLayoutSession.FlushAsync(owner, captureCurrent: false, timeout.Token);
                settingsLayoutStatus.Text = settingsLayoutSession.Status;
            }
            catch (Exception) { settingsLayoutStatus.Text = settingsLayoutSession.Status; SetStatus(settingsLayoutSession.Status, true); }
        };
        void GeometryChanged(object? _, EventArgs __)
        {
            if (!IsLoaded || !centralSettingsLayoutRead || settingsLayoutSession.Hydrating) return;
            settingsLayoutSession.Changed(); QueueSettingsLayoutSave();
        }
        LocationChanged += GeometryChanged; SizeChanged += GeometryChanged; StateChanged += GeometryChanged;
        Closing += CloseWithSettingsLayout;
        Closed += (_, _) => settingsLayoutTimer.Stop();
    }

    private string? ReadSettingsLayoutSelection(string key)
    {
        const string prefix = "Integrator.NestedTab.";
        var mount = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
        return settingsLayoutSelections.GetValueOrDefault(mount) ??
            (!centralSettingsLayoutRead ? UiPreferenceStore.ReadString(key) : null);
    }

    private void RecordSettingsLayoutSelection(string key, string value)
    {
        const string prefix = "Integrator.NestedTab.";
        var mount = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
        settingsLayoutSelections[mount] = value;
        if (settingsLayoutSession is null || settingsLayoutSession.Hydrating || !IsLoaded) return;
        settingsLayoutSession.Changed(); QueueSettingsLayoutSave();
    }

    private void QueueSettingsLayoutSave()
    {
        if (settingsLayoutTimer is null || settingsLayoutSession is not { Supported: true, Blocked: false } || settingsLayoutClosing) return;
        settingsLayoutTimer.Stop(); settingsLayoutTimer.Start();
    }

    private async Task HydrateSettingsLayoutAsync(SettingsPresetClient owner, bool force = false)
    {
        if (!persistCentralSettingsLayout || settingsLayoutSession is null) return;
        settingsLayoutTimer?.Stop();
        await settingsLayoutSession.LoadAsync(owner, force, Lifetime.Token);
        centralSettingsLayoutRead = true;
        if (settingsLayoutSession.Dirty) QueueSettingsLayoutSave();
        settingsLayoutStatus.Text = settingsLayoutSession.Status;
    }

    private async Task FlushSettingsLayoutAsync(SettingsPresetClient owner)
    {
        if (!persistCentralSettingsLayout || settingsLayoutSession is null) return;
        foreach (var state in nestedTabPreferences) state.Flush();
        settingsLayoutTimer?.Stop();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await settingsLayoutSession.FlushAsync(owner, captureCurrent: true, timeout.Token);
        settingsLayoutStatus.Text = settingsLayoutSession.Status;
    }

    private JsonElement CaptureSettingsLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var selections = new SortedDictionary<string, string>(settingsLayoutSelections, StringComparer.Ordinal);
        foreach (var state in nestedTabPreferences)
            if (state.DesiredSelection is { Length: > 0 } selected) selections[state.MountId] = selected;
        var root = nestedTabPreferences.FirstOrDefault(x => x.MountId == AutomationProperties.GetAutomationId(Tabs));
        var rootId = root?.DesiredSelection ?? (Tabs.SelectedItem is TabItem active ? AutomationProperties.GetAutomationId(active) : null);
        var top = Tabs.Items.OfType<TabItem>().FirstOrDefault(tab => AutomationProperties.GetAutomationId(tab) == rootId);
        var page = rootId;
        if (top?.Content is TabControl nested)
        {
            var state = nestedTabPreferences.FirstOrDefault(x => x.MountId == AutomationProperties.GetAutomationId(nested));
            page = state?.DesiredSelection ?? (nested.SelectedItem is TabItem selected ? AutomationProperties.GetAutomationId(selected) : null);
        }
        return IntegratorSettingsLayoutCodec.Write(new(new(new(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            WindowState == WindowState.Maximized), page, selections,
            new(settingsNavigation.PreferredRailWidth, computePreferredTopShare, toolsPreferredTopShare)));
    }

    private async Task ApplySettingsLayoutAsync(JsonElement? values)
    {
        if (values is null) return;
        var layout = IntegratorSettingsLayoutCodec.Read(values.Value);
        settingsLayoutSelections.Clear();
        if (layout is null)
        {
            HydrateSettingsPaneSizes(IntegratorSettingsLayoutCodec.DefaultPaneSizes());
            WindowState = WindowState.Normal; Width = 1360; Height = 880;
            var work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
            foreach (var state in nestedTabPreferences) state.HydrateSelection(null);
        }
        else
        {
            HydrateSettingsPaneSizes(layout.PaneSizes!);
            foreach (var pair in layout.MountSelections) settingsLayoutSelections.Add(pair.Key, pair.Value);
            WindowState = WindowState.Normal;
            var bounds = layout.Window.NormalBounds;
            Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
            WindowStartupLocation = WindowStartupLocation.Manual;
            foreach (var state in nestedTabPreferences) state.HydrateSelection(settingsLayoutSelections.GetValueOrDefault(state.MountId));
            if (layout.SelectedPageId is { } page)
            {
                var target = settingsNavigation.VisiblePages.FirstOrDefault(x => AutomationProperties.GetAutomationId(x.Tab) == page);
                if (target is not null) settingsNavigation.SelectPage(target.Tab);
            }
            if (layout.Window.Maximized) WindowState = WindowState.Maximized;
        }
        WindowSizing.FitCurrentMonitor(this);
        // Keep resize/selection consequences inside the hydration window; none may enqueue a default save.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private async void CloseWithSettingsLayout(object? sender, CancelEventArgs e)
    {
        if (settingsLayoutCloseAllowed || settingsLayoutSession is null || !centralSettingsLayoutRead ||
            settingsPresetClient is not { IsCurrent: true } owner || !settingsLayoutSession.Supported || settingsLayoutSession.Blocked || !settingsLayoutSession.Dirty) return;
        e.Cancel = true;
        if (settingsLayoutClosing) return;
        settingsLayoutClosing = true; settingsLayoutTimer?.Stop();
        try { await FlushSettingsLayoutAsync(owner); }
        catch (Exception) { SetStatus(settingsLayoutSession.Status, true); }
        finally
        {
            settingsLayoutCloseAllowed = true;
            _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
        }
    }
}
