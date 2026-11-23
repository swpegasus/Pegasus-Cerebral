using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cerebral.AiDictationPlugin;

/// <summary>One Start/Stop action. Preferences and exceptional pending-text recovery stay collapsed.</summary>
internal sealed class DictationReviewView : Border
{
    internal const double WindowWidth = 460, WindowHeight = 280, RecoveryWindowHeight = 280, ErrorWindowHeight = 336;
    private readonly TextBlock destination, state, errorDetail;
    private readonly Button toggle, grab, accept, play;
    private readonly TextBox review;
    private readonly StackPanel recovery;
    private readonly MenuItem automatic, external, grammar, dock, profiles, recover;
    private bool updating, recordingRequested, hasPending;
    private readonly bool playbackSupported;
    private readonly Cerebral.Orchestrator.ComposerDraftPresentation previewPresentation;
    private string profileId = "";
    internal FrameworkElement DragHandle { get; }
    internal TextBox ReviewEditor => review;
    internal double RequiredHeight => errorDetail.Visibility == Visibility.Visible ? ErrorWindowHeight : WindowHeight;
    internal event EventHandler? CloseRequested;
    internal event EventHandler? EditRequested;
    internal event EventHandler? LayoutModeChanged;

    internal DictationReviewView(Func<Task> startRecording, Action stopRecording, Action cancel,
        Func<Task> acceptReview, Action<string> edit, Func<Point, Task> pick, Action chooseCerebral,
        Func<Task> toggleAutomatic, Func<Task> toggleExternal, Func<Task> toggleGrammar, Func<Task> toggleDock,
        Func<Task<IReadOnlyList<DictationGrammarProfile>>> loadProfiles, Func<DictationGrammarProfile, Task> selectProfile,
        Func<Task> openAdvanced, Action? useRemembered = null, Func<Task>? playRecording = null)
    {
        playbackSupported = playRecording is not null;
        Background = Brush(43, 46, 49); BorderBrush = Brush(72, 78, 84); BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10); Padding = new Thickness(10);
        var body = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var close = Button("×", "Close dictation controls", 28); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        close.Click += (_, _) => { stopRecording(); CloseRequested?.Invoke(this, EventArgs.Empty); };
        var dockButton = Button("Dock", "Dock dictation controls", 44); DockPanel.SetDock(dockButton, Dock.Right); header.Children.Add(dockButton);
        dockButton.Click += async (_, _) => await toggleDock();
        var gear = Button("⚙", "Dictation settings", 28); DockPanel.SetDock(gear, Dock.Right); header.Children.Add(gear);
        var title = Label(12); title.Text = "⠿ One-off recording"; title.VerticalAlignment = VerticalAlignment.Center;
        title.Cursor = Cursors.SizeAll; title.Margin = new Thickness(2, 0, 8, 0); DockPanel.SetDock(title, Dock.Left); header.Children.Add(title);
        DragHandle = title;
        state = Label(11); state.VerticalAlignment = VerticalAlignment.Center; state.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(state); body.Children.Add(header);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        toggle = Button("Record", "Record one clip", 74); toggle.Height = 34;
        toggle.Click += async (_, _) => { if (recordingRequested) stopRecording(); else await startRecording(); };
        DockPanel.SetDock(toggle, Dock.Left); row.Children.Add(toggle);
        destination = Label(12); destination.Margin = new Thickness(9, 0, 0, 0); destination.VerticalAlignment = VerticalAlignment.Center;
        destination.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(destination); body.Children.Add(row);
        var foot = Label(11); foot.Text = "Record → Stop → Play / edit → Insert · never sends a message"; body.Children.Add(foot);
        errorDetail = Label(11); errorDetail.Foreground = Brushes.Salmon; errorDetail.TextWrapping = TextWrapping.Wrap;
        errorDetail.MaxHeight = 46; errorDetail.Margin = new Thickness(0, 5, 0, 0); errorDetail.Visibility = Visibility.Collapsed;
        body.Children.Add(errorDetail);

        recovery = new StackPanel { Visibility = Visibility.Visible, Margin = new Thickness(0, 8, 0, 0) };
        review = new TextBox { Height = 92, MaxLength = DictationReview.MaximumCharacters, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush(34, 37, 40), Foreground = Brushes.WhiteSmoke, BorderBrush = Brush(81, 89, 97),
            CaretBrush = Brushes.WhiteSmoke, Padding = new Thickness(7) };
        previewPresentation = new(review);
        AutomationProperties.SetName(review, "Review one-off transcript before insertion"); AutomationProperties.SetAutomationId(review, "AiDictation.Review");
        review.TextChanged += (_, _) => { if (!updating) edit(review.Text); };
        review.PreviewMouseLeftButtonDown += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty); recovery.Children.Add(review);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        play = Button("Play", "Play recording", 68);
        play.IsEnabled = false; play.Click += async (_, _) => { if (playRecording is not null) await playRecording(); }; actions.Children.Add(play);
        accept = Button("Insert", "Insert retained phrase into chosen text box", 70);
        accept.Click += async (_, _) => await acceptReview(); actions.Children.Add(accept);
        var discard = Button("Discard", "Discard retained phrase", 80);
        discard.Click += (_, _) => cancel(); actions.Children.Add(discard); recovery.Children.Add(actions);
        body.Children.Add(recovery); Child = body;

        var menu = new ContextMenu(); gear.ContextMenu = menu; gear.Click += (_, _) => menu.IsOpen = true;
        automatic = Toggle("Add phrases after pauses", toggleAutomatic);
        automatic.ToolTip = "When off, record until Stop and add the final phrase. This preference never starts recording.";
        external = Toggle("Allow Windows destinations and remember eligible text boxes", toggleExternal);
        external.ToolTip = "Remember one eligible control from focus events; Start captures its text and cursor. No window scan.";
        grammar = Toggle("Correct new dictated phrases", toggleGrammar); dock = Toggle("Dock controls in status bar", toggleDock);
        profiles = new MenuItem { Header = "Grammar profile" };
        foreach (var item in new[] { automatic, external, grammar, profiles, dock }) menu.Items.Add(item);
        grab = Button("Drag + to choose a Windows text box", "Choose Windows text box", 280); grab.FontSize = 12; grab.Cursor = Cursors.Cross;
        var dragging = false;
        grab.PreviewMouseLeftButtonDown += (_, args) => { dragging = grab.CaptureMouse(); args.Handled = dragging; };
        grab.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            if (!dragging) return;
            var point = grab.PointToScreen(args.GetPosition(grab)); dragging = false; grab.ReleaseMouseCapture();
            args.Handled = true; menu.IsOpen = false; await pick(point);
        };
        grab.LostMouseCapture += (_, _) => dragging = false;
        menu.Items.Add(new MenuItem { Header = grab, StaysOpenOnClick = true });
        var cerebral = new MenuItem { Header = "Use the last Cerebral editor" }; cerebral.Click += (_, _) => chooseCerebral(); menu.Items.Add(cerebral);
        if (useRemembered is not null)
        { var remembered = new MenuItem { Header = "Use the last eligible text box" }; remembered.Click += (_, _) => useRemembered(); menu.Items.Add(remembered); }
        recover = new MenuItem { Header = "Recover pending transcript…", IsEnabled = false }; recover.Click += (_, _) => ShowRecovery(); menu.Items.Add(recover);
        menu.Items.Add(new MenuItem { Header = "CPU recognizer stays ready while automatic dictation is on", IsEnabled = false,
            ToolTip = "The private speech helper retains the model across automatic phrases, then closes on Stop, cancellation or error. Live words require the optional installed streaming model." });
        var advanced = new MenuItem { Header = "Microphone and model settings…" }; advanced.Click += async (_, _) => await openAdvanced(); menu.Items.Add(advanced);
        menu.Opened += async (_, _) =>
        {
            profiles.Items.Clear();
            try
            {
                var choices = await loadProfiles();
                foreach (var choice in choices)
                {
                    var option = new MenuItem { Header = choice.Name, IsCheckable = true, IsChecked = choice.Id == profileId };
                    option.Click += async (_, _) => await selectProfile(choice); profiles.Items.Add(option);
                }
                if (choices.Count == 0) profiles.Items.Add(new MenuItem { Header = "No configured dictation grammar profile", IsEnabled = false });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { profiles.Items.Add(new MenuItem { Header = "Grammar unavailable · original words are preserved", IsEnabled = false }); }
        };
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { stopRecording(); CloseRequested?.Invoke(this, EventArgs.Empty); args.Handled = true; } };
    }

    internal void ShowRecovery()
    { if (hasPending) { recovery.Visibility = Visibility.Visible; LayoutModeChanged?.Invoke(this, EventArgs.Empty); } }

    internal void Update(DictationMicrophoneSnapshot value, AiDictationSettings settings, string pendingText, bool canAccept)
    {
        updating = true;
        try
        {
            recordingRequested = value.RecordingRequested;
            if (review.Text != pendingText)
            {
                var start = review.SelectionStart; var length = review.SelectionLength;
                var wasReadOnly = review.IsReadOnly;
                review.IsReadOnly = false; // Private preview update; user editing remains disabled during capture.
                try
                {
                    var caret = Math.Min(start, pendingText.Length);
                    previewPresentation.Apply(pendingText, new(new(start, length),
                        new(caret, Math.Min(length, pendingText.Length - caret)), FadeChangedText: value.ReviewBusy));
                }
                finally { review.IsReadOnly = wasReadOnly; }
            }
            hasPending = !string.IsNullOrWhiteSpace(pendingText); recover.IsEnabled = hasPending;
            review.IsReadOnly = value.ReviewBusy; destination.Text = value.Target ?? "Choose a destination in Settings"; destination.ToolTip = destination.Text;
            state.Text = value.Error ? "Error" : value.Mode switch
            {
                DictationMicrophoneMode.Listening => "Listening", DictationMicrophoneMode.Starting => "Starting…",
                DictationMicrophoneMode.Processing => "Transcribing…", _ => hasPending ? "Transcript" : "Ready"
            };
            errorDetail.Text = value.Error ? value.Detail : "";
            var errorVisibility = value.Error ? Visibility.Visible : Visibility.Collapsed;
            if (errorDetail.Visibility != errorVisibility)
            { errorDetail.Visibility = errorVisibility; LayoutModeChanged?.Invoke(this, EventArgs.Empty); }
            errorDetail.ToolTip = value.Detail;
            state.ToolTip = value.Detail; toggle.Content = recordingRequested ? "Stop" : "Record";
            AutomationProperties.SetName(toggle, recordingRequested ? "Stop recording" : "Record one clip"); toggle.IsEnabled = value.CanToggleMicrophone;
            play.Content = value.PlayingRecording ? "Stop play" : "Play";
            AutomationProperties.SetName(play, value.PlayingRecording ? "Stop playback" : "Play recording");
            play.IsEnabled = playbackSupported && value.HasRecording && value.Mode is not (DictationMicrophoneMode.Starting or DictationMicrophoneMode.Listening);
            accept.IsEnabled = canAccept && !recordingRequested;
            grab.IsEnabled = settings.AllowExternalTarget && !recordingRequested && value.Mode != DictationMicrophoneMode.Processing;
            automatic.IsChecked = settings.AutoTranscribeOnSilence; external.IsChecked = settings.AllowExternalTarget;
            grammar.IsChecked = settings.GrammarCorrection; dock.IsChecked = settings.DockControls;
            grammar.IsEnabled = settings.GrammarProfileId.Length > 0; profileId = settings.GrammarProfileId;
        }
        finally { updating = false; }
    }

    private static MenuItem Toggle(string label, Func<Task> change)
    { var item = new MenuItem { Header = label, IsCheckable = true }; item.Click += async (_, _) => await change(); return item; }
    private static Brush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
    private static TextBlock Label(double size) => new() { FontSize = size, Foreground = Brushes.WhiteSmoke };
    private static Button Button(string content, string name, double width)
    {
        var button = new Button { Content = content, Width = width, Height = 28, Margin = new Thickness(2), Padding = new Thickness(0),
            FontSize = 13, Foreground = Brushes.WhiteSmoke, Background = Brush(58, 63, 68), BorderThickness = new Thickness(0), ToolTip = name };
        var frame = new FrameworkElementFactory(typeof(Border)); frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5)); var caption = new FrameworkElementFactory(typeof(ContentPresenter));
        caption.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); caption.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(caption); var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        template.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, .4) } });
        button.Template = template; AutomationProperties.SetName(button, name); return button;
    }
}
