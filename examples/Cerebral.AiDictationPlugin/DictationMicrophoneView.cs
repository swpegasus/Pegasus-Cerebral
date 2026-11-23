using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

internal enum DictationMicrophoneMode { NoTarget, Off, Starting, Listening, Processing }
internal sealed record DictationMicrophoneSnapshot(DictationMicrophoneMode Mode, bool Automatic,
    bool CanToggleMicrophone, bool CanToggleAutomatic, string? Target, string Detail, double? InputLevel, bool Error = false)
{
    internal bool Docked { get; init; }
    internal bool CanAccept { get; init; }
    internal bool ReviewBusy { get; init; }
    internal bool RecordingRequested { get; init; }
    internal bool HasRecording { get; init; }
    internal bool PlayingRecording { get; init; }
}

/// <summary>Native dictation controls. Capture controls preserve editor focus; settings supports keyboard navigation.</summary>
internal sealed class DictationMicrophoneView : Border, IDisposable
{
    private readonly Button microphone;
    private readonly CheckBox automatic;
    private readonly Button? settings;
    private Func<Task<DesktopOperationResult>>? openSettings;
    private Func<DesktopSettingsNavigationAvailability>? querySettingsAvailability;
    private bool settingsPending;
    private readonly string? settingsUnavailableReason;
    private string? settingsNavigationHelp;
    private const string SettingsUncertainHelp = "Configuration may have opened, but AI Dictation settings could not be confirmed. Check Configuration before trying again.";
    private const string SettingsHelp = "Open AI Dictation settings in Configuration to change the speech model, microphone input, audio boost and Auto timing.";
    internal const double FloatingWidth = 208, FloatingHeight = 176;
    private const double CompactStateWidth = 64;
    private readonly TextBlock state, target;
    private readonly MicrophoneGlyph glyph;
    private readonly TextBlock? captureLabel;
    private readonly DispatcherTimer animation;
    private readonly bool compact, animate;
    private DictationMicrophoneSnapshot snapshot = new(DictationMicrophoneMode.NoTarget, false, false, false, null, "", null);
    private bool disposed;
    private Button? cancelReview, stopSegment, acceptReview;
    internal FrameworkElement DragHandle { get; }
    internal event EventHandler? CloseRequested;
    internal bool AnimationRunning => animation.IsEnabled;
    internal DictationMicrophoneSnapshot Snapshot => snapshot;

    internal DictationMicrophoneView(bool compact, Func<Task> toggleMicrophone, Func<Task> toggleAutomatic,
        Action? open = null, bool animate = true, Func<Task<DesktopOperationResult>>? openSettings = null, string? settingsUnavailableReason = null,
        Func<DesktopSettingsNavigationAvailability>? querySettingsAvailability = null)
    {
        this.compact = compact; this.animate = animate; this.openSettings = openSettings; this.querySettingsAvailability = querySettingsAvailability;
        this.settingsUnavailableReason = settingsUnavailableReason;
        Focusable = false; KeyboardNavigation.SetTabNavigation(this, compact ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
        CornerRadius = new(compact ? 7 : 14); BorderThickness = new(1);
        Background = new SolidColorBrush(Color.FromRgb(249, 251, 255));
        BorderBrush = new SolidColorBrush(Color.FromRgb(218, 226, 239));
        // Status contributions live inside the host's finite 30-DIP clipping boundary.
        Padding = compact ? new Thickness(3, 1, 3, 1) : new Thickness(10);
        glyph = new MicrophoneGlyph { Width = compact ? 18 : 72, Height = compact ? 18 : 72 };
        microphone = Button(glyph, "AiDictation.Microphone", compact ? 24 : 82);
        if (compact)
        {
            microphone.Content = null;
            var primary = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            glyph.Width = glyph.Height = 14;
            captureLabel = Label(11, true); captureLabel.Text = "Start"; captureLabel.Margin = new Thickness(3, 0, 0, 0);
            primary.Children.Add(glyph); primary.Children.Add(captureLabel);
            microphone.Content = primary; microphone.Width = 62;
        }
        microphone.Background = Brushes.Transparent;
        microphone.BorderBrush = Brushes.Transparent;
        microphone.Click += async (_, _) => await toggleMicrophone();
        automatic = new CheckBox
        {
            Content = "Auto", Focusable = false, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center,
            Style = AutomaticStyle(), MinHeight = 0, MinWidth = 0, Height = 20,
            Padding = new Thickness(3, 1, 3, 1), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent, Background = Brushes.Transparent,
            FontSize = compact ? 12 : 11, Margin = new Thickness(compact ? 6 : 0, 0, compact ? 2 : 0, 0),
            ToolTip = "Auto repeats after quiet in the chosen editor. Turning Auto off finishes the captured segment and prevents another automatic segment. Turning the microphone off finishes it and pauses Auto. Advanced options are in Configure."
        };
        AutomationProperties.SetAutomationId(automatic, "AiDictation.Auto");
        ToolTipService.SetShowOnDisabled(automatic, true); ToolTipService.SetShowOnDisabled(microphone, true);
        automatic.Click += async (_, _) => { automatic.IsChecked = snapshot.Automatic; await toggleAutomatic(); };
        state = Label(12, true); target = Label(compact ? 11 : 10.5);
        target.Foreground = Muted; target.TextTrimming = TextTrimming.CharacterEllipsis;
        if (compact)
        {
            var row = new CompactMicrophoneRow(state);
            row.Children.Add(microphone); state.Margin = new(4, 0, 0, 0); state.VerticalAlignment = VerticalAlignment.Center;
            // Reserve the label independently of its text; action widths win in narrow slots.
            state.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(state, "AiDictation.State");
            row.Children.Add(state); // Auto belongs to the plugin window's Settings menu.
            settings = Button(new TextBlock { Text = "\uE713", FontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13, Foreground = Ink }, "AiDictation.Settings", 22);
            settings.Margin = new(2, 0, 0, 0); settings.Background = Brushes.Transparent; settings.BorderBrush = Brushes.Transparent;
            settings.Focusable = true; settings.IsTabStop = true; settings.IsEnabled = openSettings is not null;
            AutomationProperties.SetName(settings, "Open AI Dictation settings");
            SetSettingsHelp(openSettings is not null ? SettingsHelp : settingsUnavailableReason ??
                "Opening plugin settings from this host is work in progress. Open Configuration > Desktop plugins > AI Dictation.");
            ToolTipService.SetShowOnDisabled(settings, true);
            settings.Click += OpenSettings;
            if (open is not null)
            {
                var pop = Button("↗", "AiDictation.OpenControls", 22);
                pop.Margin = new Thickness(3, 0, 0, 0); pop.ToolTip = "Open one-off recording, playback, review and settings";
                AutomationProperties.SetName(pop, "Open dictation controls");
                pop.Click += (_, _) => open(); row.Children.Add(pop);
            }
            Child = row; DragHandle = row;
        }
        else
        {
            var grid = new Grid();
            foreach (var height in new[] { GridLength.Auto, new GridLength(86), GridLength.Auto, GridLength.Auto })
                grid.RowDefinitions.Add(new() { Height = height });
            var chrome = new Grid { Background = Brushes.Transparent, Height = 22, Cursor = Cursors.SizeAll };
            chrome.ColumnDefinitions.Add(new()); chrome.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var title = Label(10, true); title.Text = "DICTATION"; title.Foreground = Muted; title.VerticalAlignment = VerticalAlignment.Center;
            title.ToolTip = "Drag to move · Ctrl+Alt+D opens the microphone"; chrome.Children.Add(title);
            var close = Button("×", "AiDictation.CloseControls", 22); close.FontSize = 16;
            close.Padding = new(0); close.Background = Brushes.Transparent; close.BorderBrush = Brushes.Transparent; close.ToolTip = "Close and cancel current segment";
            AutomationProperties.SetName(close, "Close and cancel current segment");
            Grid.SetColumn(close, 1); chrome.Children.Add(close); close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
            grid.Children.Add(chrome); DragHandle = title;
            microphone.HorizontalAlignment = HorizontalAlignment.Center; microphone.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(microphone, 1); grid.Children.Add(microphone);
            state.FontSize = 14; state.TextAlignment = TextAlignment.Center; state.Margin = new(0, 0, 0, 1); Add(state, 2);
            var footer = new Grid { Margin = new(0, 3, 0, 0), Height = 20 };
            footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            target.MaxWidth = 126; target.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(target);
            Grid.SetColumn(automatic, 1); footer.Children.Add(automatic); Add(footer, 3);
            Child = grid;
            void Add(UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
        }
        animation = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        animation.Tick += Tick;
        Loaded += LoadedView; Unloaded += UnloadedView;
        Update(snapshot);
    }

    internal void Update(DictationMicrophoneSnapshot value)
    {
        if (disposed) return;
        snapshot = value;
        // Session messages also serve composer actions. Front controls use their own labels
        // while keeping actionable errors exact and showing only a reported finite input level.
        var measured = value.InputLevel is { } input && double.IsFinite(input)
            ? $"Processed input {Math.Clamp(input, 0, 1) * 100:0}%{(input >= .99 ? " · clipping" : "")}. " : "";
        var guidance = value.Error ? value.Detail : value.Mode switch
        {
            DictationMicrophoneMode.NoTarget => "Choose the intended text box in Dictation with the grabber or Settings.",
            DictationMicrophoneMode.Starting => "Starting dictation. Stop finishes and adds the final phrase.",
            DictationMicrophoneMode.Listening => measured + (value.Automatic
                ? "Completed phrases are added after pauses. Stop adds the final phrase and ends listening."
                : "Stop transcribes and adds the final phrase directly to the destination."),
            DictationMicrophoneMode.Processing => "Adding the completed phrase. Stop ends automatic listening after this phrase.",
            _ => value.Detail
        };
        var label = value.Mode switch
        {
            DictationMicrophoneMode.NoTarget => "No target", DictationMicrophoneMode.Off => "Microphone off",
            DictationMicrophoneMode.Starting => "Starting…", DictationMicrophoneMode.Listening => "Listening", _ => "Processing…"
        };
        state.Text = value.Error ? "Error" : compact && value.Mode == DictationMicrophoneMode.Off ? "Off" : label;
        AutomationProperties.SetName(state, value.Error ? value.Detail : label);
        state.Foreground = value.Error ? Brushes.Firebrick : Ink;
        state.ToolTip = label + "\n" + guidance;
        target.Text = value.Error && !string.IsNullOrWhiteSpace(value.Detail) ? value.Detail : value.Target ?? "Choose an editor";
        target.Foreground = value.Error ? Brushes.Firebrick : Muted;
        target.ToolTip = (value.Target ?? "Supports Cerebral editors") + "\n" + guidance;
        automatic.IsChecked = value.Automatic; automatic.IsEnabled = value.CanToggleAutomatic;
        microphone.IsEnabled = value.CanToggleMicrophone;
        if (cancelReview is not null)
        {
            foreach (var action in new[] { cancelReview, stopSegment!, acceptReview! })
                action.Visibility = value.Docked ? Visibility.Visible : Visibility.Collapsed;
            stopSegment!.IsEnabled = value.Mode is DictationMicrophoneMode.Starting or DictationMicrophoneMode.Listening;
            acceptReview!.IsEnabled = value.CanAccept;
            acceptReview.Visibility = value.Docked && value.Mode is not (DictationMicrophoneMode.Starting or DictationMicrophoneMode.Listening)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        var on = value.RecordingRequested;
        if (captureLabel is not null) captureLabel.Text = on ? "Stop" : "Start";
        microphone.ToolTip = on ? "Stop dictation and add the final phrase" :
            value.Mode == DictationMicrophoneMode.Processing ? "Finishing the captured segment…" :
            value.Mode == DictationMicrophoneMode.NoTarget ? "Choose a destination in Dictation first" : "Start recording";
        AutomationProperties.SetName(microphone, on ? "Stop recording" : "Start recording");
        AutomationProperties.SetHelpText(microphone, guidance);
        ToolTip = compact ? label + " · " + (value.Target ?? "choose a Cerebral editor") + "\n" + guidance : null;
        glyph.SetState(value.Mode, value.InputLevel);
        UpdateAnimation();
        RefreshSettingsAvailability();
    }

    internal void AttachReviewActions(Action cancel, Func<Task> accept)
    {
        if (!compact || Child is not Panel row || cancelReview is not null) return;
        cancelReview = Button("×", "AiDictation.Cancel", 22); cancelReview.Click += (_, _) => cancel();
        stopSegment = Button("■", "AiDictation.Stop", 22);
        stopSegment.Click += (_, _) => microphone.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        acceptReview = Button("Insert", "AiDictation.Accept", 42); acceptReview.Click += async (_, _) => await accept();
        AutomationProperties.SetName(cancelReview, "Cancel current dictation");
        AutomationProperties.SetName(stopSegment, "Stop microphone and transcribe");
        AutomationProperties.SetName(acceptReview, "Insert review into chosen text box");
        foreach (var action in new[] { cancelReview, stopSegment, acceptReview }) { action.Visibility = Visibility.Collapsed; row.Children.Add(action); }
    }

    private void RefreshSettingsAvailability()
    {
        if (disposed || settings is null || settingsPending) return;
        var availability = openSettings is null
            ? new DesktopSettingsNavigationAvailability(false, settingsUnavailableReason)
            : ReadSettingsAvailability();
        // A scoped host may revoke and dispose this view during the query.
        if (disposed) return;
        settings.IsEnabled = openSettings is not null && availability.Available;
        SetSettingsHelp(availability.Available
            ? settingsNavigationHelp ?? SettingsHelp
            : availability.Message ?? settingsUnavailableReason ??
                "AI Dictation settings are unavailable. Open Configuration > Desktop plugins > AI Dictation.");
    }

    // The host can narrow its status slot without resizing the whole window to its
    // minimum. Measure actions first, then reserve a text-independent label budget.
    private sealed class CompactMicrophoneRow(TextBlock status) : Panel
    {
        private double LabelWidth(double available, double actions)
        {
            var maximum = CompactStateWidth + status.Margin.Left + status.Margin.Right;
            return double.IsPositiveInfinity(available) ? maximum : Math.Clamp(available - actions, 0, maximum);
        }
        protected override Size MeasureOverride(Size available)
        {
            double actions = 0, height = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (ReferenceEquals(child, status)) continue;
                child.Measure(new Size(double.PositiveInfinity, available.Height));
                actions += child.DesiredSize.Width; height = Math.Max(height, child.DesiredSize.Height);
            }
            var label = LabelWidth(available.Width, actions);
            status.Measure(new Size(label, available.Height));
            return new Size(actions + label, Math.Max(height, status.DesiredSize.Height));
        }
        protected override Size ArrangeOverride(Size final)
        {
            double actions = 0;
            foreach (UIElement child in InternalChildren)
                if (!ReferenceEquals(child, status)) actions += child.DesiredSize.Width;
            var label = LabelWidth(final.Width, actions);
            double x = 0;
            foreach (UIElement child in InternalChildren)
            {
                var width = ReferenceEquals(child, status) ? label : child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, final.Height)); x += width;
            }
            return final;
        }
    }

    private void SetSettingsHelp(string text)
    {
        if (settings is null) return;
        settings.ToolTip = text; AutomationProperties.SetHelpText(settings, text);
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (disposed || settingsPending || settings?.IsEnabled != true || openSettings is not { } navigate) return;
        settingsPending = true; settings.IsEnabled = false;
        settingsNavigationHelp = null;
        SetSettingsHelp("Opening AI Dictation settings in Configuration…");
        try
        {
            var result = await navigate();
            DesktopSettingsNavigationAvailability? retry = null;
            if (!disposed && result.State == DesktopOperationState.Rejected)
            {
                retry = ReadSettingsAvailability();
            }
            if (!disposed) SetSettingsHelp(settingsNavigationHelp = result.State switch
            {
                DesktopOperationState.Completed => SettingsHelp,
                DesktopOperationState.Accepted => "Configuration is opening, but the AI Dictation panel is not confirmed yet. Check Configuration before trying again.",
                DesktopOperationState.Uncertain => SettingsUncertainHelp,
                DesktopOperationState.Rejected => retry?.Available == true
                    ? "Configuration temporarily refused this request. Its settings route is available; check the application status and try again."
                    : retry?.Message ?? "AI Dictation settings are unavailable. Open Configuration > Desktop plugins > AI Dictation.",
                _ => "AI Dictation settings could not open. Open Configuration > Desktop plugins > AI Dictation and try again."
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!disposed) SetSettingsHelp(settingsNavigationHelp = SettingsUncertainHelp);
        }
        finally { settingsPending = false; RefreshSettingsAvailability(); }
    }

    private DesktopSettingsNavigationAvailability ReadSettingsAvailability()
    {
        const string unavailable = "AI Dictation settings availability could not be confirmed. Open Configuration > Desktop plugins > AI Dictation.";
        try { return querySettingsAvailability?.Invoke() ?? new(openSettings is not null, openSettings is null ? unavailable : null); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(false, unavailable); }
    }

    private void LoadedView(object sender, RoutedEventArgs e)
    {
        UpdateAnimation();
        // Factories run before registry publication. Requery for this same mounted view.
        RefreshSettingsAvailability();
    }
    private void UnloadedView(object sender, RoutedEventArgs e) => animation.Stop();
    private void Tick(object? sender, EventArgs e) => glyph.Advance();
    private void UpdateAnimation()
    {
        if (!disposed && animate && IsLoaded && snapshot.Mode is DictationMicrophoneMode.Starting or DictationMicrophoneMode.Listening or DictationMicrophoneMode.Processing)
            animation.Start();
        else animation.Stop();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (settings is not null) { settings.Click -= OpenSettings; settings.IsEnabled = false; }
        openSettings = null; querySettingsAvailability = null; animation.Stop(); animation.Tick -= Tick; Loaded -= LoadedView; Unloaded -= UnloadedView;
    }
    private static Brush Ink => new SolidColorBrush(Color.FromRgb(30, 47, 75));
    private static Brush Muted => new SolidColorBrush(Color.FromRgb(89, 105, 129));
    private static TextBlock Label(double size, bool bold = false) => new()
    { FontFamily = new("Segoe UI"), FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Ink };
    private static Style AutomaticStyle()
    {
        // This light widget owns paired colors and geometry, including disabled state.
        // An application CheckBox style must not add a dark plate or a 32-DIP minimum.
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        var chrome = new FrameworkElementFactory(typeof(Border), "AutoChrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var indicator = new FrameworkElementFactory(typeof(Border), "AutoIndicator");
        indicator.SetValue(DockPanel.DockProperty, Dock.Left);
        indicator.SetValue(FrameworkElement.WidthProperty, 12d);
        indicator.SetValue(FrameworkElement.HeightProperty, 12d);
        indicator.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        indicator.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        indicator.SetValue(Border.BackgroundProperty, Brushes.White);
        indicator.SetValue(Border.BorderBrushProperty, Muted);
        indicator.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var mark = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path), "AutoCheck");
        mark.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 1,5 L 4,8 L 9,1"));
        mark.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.White);
        mark.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5d);
        mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        indicator.AppendChild(mark); row.AppendChild(indicator);
        var content = new FrameworkElementFactory(typeof(ContentPresenter), "AutoContent");
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        row.AppendChild(content); chrome.AppendChild(row);
        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = chrome };
        var selected = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
        var accent = new SolidColorBrush(Color.FromRgb(47, 102, 208));
        selected.Setters.Add(new Setter(Border.BackgroundProperty, accent, "AutoIndicator"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, accent, "AutoIndicator"));
        selected.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "AutoCheck"));
        template.Triggers.Add(selected);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(225, 235, 253)), "AutoChrome"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(240, 243, 248)), "AutoChrome"));
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(230, 235, 242)), "AutoIndicator"));
        disabled.Setters.Add(new Setter(Border.BorderBrushProperty, Muted, "AutoIndicator"));
        disabled.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Muted, "AutoCheck"));
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Triggers.Add(new Trigger { Property = IsEnabledProperty, Value = false,
            Setters = { new Setter(Control.ForegroundProperty, Muted) } });
        return style;
    }

    private static Button Button(object content, string id, double size)
    {
        var button = new Button { Content = content, Width = size, Height = size,
            // An explicit local style prevents ambient app Button minimums/margins from changing geometry.
            Style = new Style(typeof(Button)), MinHeight = 0, MinWidth = 0, Margin = new(0), Padding = new(0),
            BorderThickness = new(0),
            Focusable = false, IsTabStop = false, Cursor = Cursors.Hand, Foreground = Ink };
        // A dedicated template keeps disabled/hover states circular instead of letting a host
        // button style introduce a rectangular plate around the graphical microphone.
        var shell = new FrameworkElementFactory(typeof(Border));
        shell.Name = "Shell"; shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(999));
        shell.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Button.Background)) { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content"); shell.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = shell };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(225, 235, 253)), "Shell"));
        template.Triggers.Add(hover);
        var focused = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Ink, "Shell"));
        focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Shell"));
        template.Triggers.Add(focused);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .6, "Shell")); template.Triggers.Add(disabled);
        button.Template = template;
        AutomationProperties.SetAutomationId(button, id); return button;
    }
}

/// <summary>Drawn at native scale. The ring animates listening state; the bars use only reported input level.</summary>
internal sealed class MicrophoneGlyph : FrameworkElement
{
    private DictationMicrophoneMode mode;
    private double? level;
    private double phase;
    internal double? InputLevel => level;
    internal void SetState(DictationMicrophoneMode value, double? input)
    {
        if (mode != value) phase = 0;
        mode = value; level = input is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : null;
        InvalidateVisual();
    }
    internal void Advance() { phase = (phase + .09) % 1; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var scale = Math.Min(ActualWidth, ActualHeight) / 86;
        if (scale <= 0) return;
        dc.PushTransform(new ScaleTransform(scale, scale));
        var active = mode is DictationMicrophoneMode.Listening or DictationMicrophoneMode.Starting;
        var blue = new SolidColorBrush(Color.FromRgb(38, 104, 218));
        var ink = active || mode == DictationMicrophoneMode.Processing ? blue : new SolidColorBrush(Color.FromRgb(92, 111, 141));
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(234, 241, 253)), null, new(43, 43), 37, 37);
        if (active)
        {
            var pulse = 34 + 5 * Math.Sin(phase * Math.PI);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(80, 38, 104, 218)), 2), new(43, 43), pulse, pulse);
        }
        dc.DrawRoundedRectangle(ink, null, new(35, 20, 16, 29), 8, 8);
        var pen = new Pen(ink, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(new(29, 39), false, false);
            context.ArcTo(new(57, 39), new(14, 18), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        dc.DrawGeometry(null, pen, arc); dc.DrawLine(pen, new(43, 57), new(43, 64)); dc.DrawLine(pen, new(36, 64), new(50, 64));
        if (mode == DictationMicrophoneMode.Off || mode == DictationMicrophoneMode.NoTarget)
            dc.DrawLine(new Pen(Brushes.White, 5), new(25, 23), new(60, 61));
        if (mode == DictationMicrophoneMode.Off || mode == DictationMicrophoneMode.NoTarget)
            dc.DrawLine(new Pen(ink, 2.5), new(25, 23), new(60, 61));
        if (mode == DictationMicrophoneMode.Listening && level is { } amount)
        {
            for (var i = 0; i < 5; i++)
            {
                var bar = 2 + amount * (i == 2 ? 15 : i % 2 == 0 ? 7 : 11);
                dc.DrawRoundedRectangle(blue, null, new(28 + i * 7, 76 - bar, 3, bar), 1.5, 1.5);
            }
        }
        if (mode == DictationMicrophoneMode.Processing)
        {
            var a = phase * Math.PI * 2;
            dc.DrawEllipse(blue, null, new(43 + 34 * Math.Cos(a), 43 + 34 * Math.Sin(a)), 4, 4);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 38, 104, 218)), 2), new(43, 43), 34, 34);
        }
        dc.Pop();
    }
}
