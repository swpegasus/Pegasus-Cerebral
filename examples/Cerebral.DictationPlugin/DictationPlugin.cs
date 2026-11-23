using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;
using Cerebral.PackageSettings;

namespace Cerebral.DictationPlugin;

public sealed class DictationPlugin : IDesktopPlugin, IDesktopConfigurationPlugin, IDesktopTextEntryPlugin
{
    private IDesktopPluginHost? host;
    private Dispatcher? dispatcher;
    private PackagePreferenceSession<DictationPreferences>? preferences;
    private WindowsDictationSession? session;
    private SpeechEnvironment environment = new([], "Windows speech has not been checked.");
    private TextBlock? status;
    private ComboBox? recognizer;
    private ComboBox? confidence;
    private CheckBox? voiceControls;
    private TextBox? vocabulary;
    private TextBox? corrections;
    private Button? testButton;
    private TextBox? testOutput;
    private TextBlock? trainingStatus;
    private ProgressBar? microphoneLevel;
    private TextBlock? microphoneLevelText;
    private TextBlock? microphoneSignalText;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("dictation-settings", "Dictation", CreateConfigurationPanel)];
    public IReadOnlyList<DesktopTextEntryAction> TextEntryActions =>
    [new("dictate", "🎙 Dictate", DictateAsync, "Start offline Windows dictation for this text box. Select it again to stop.")];

    public void Initialize(IDesktopPluginHost host)
    {
        this.host = host;
        dispatcher = Dispatcher.CurrentDispatcher;
        if (host is not IDesktopPluginEnvironment environmentHost)
            throw new InvalidOperationException("This Cerebral build does not expose plugin-owned data storage.");
        preferences = new(host, DictationSettingsPolicy.PluginId, DictationSettingsPolicy.PackageVersion,
            new DictationSettingsPolicy(), DictationSettingsPolicy.Decode, DictationSettingsPolicy.Encode, () => new());
        environment = WindowsSpeechSupport.Inspect();
        session = new(ReadRuntimePreferences, VoiceControlBinding, ExecuteVoiceControlAsync, Report, UpdateAudioLevel, UpdateAudioSignal,
            action => dispatcher!.InvokeAsync(action).Task.Unwrap());
        host.ConversationChanged += OnConversationChanged;
    }

    internal DictationPreferences ReadRuntimePreferences() => preferences!.Current;

    private async Task DictateAsync(IDesktopTextEntry target, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await preferences!.ReadAsync(cancellationToken);
        await (session ?? throw new InvalidOperationException("Dictation is not initialized.")).ToggleAsync(target, cancellationToken);
    }

    private FrameworkElement CreateConfigurationPanel()
    {
        var initial = new DictationPreferences();
        var content = new StackPanel { Margin = new Thickness(18), MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(Heading("Offline Windows dictation", 23));
        content.Children.Add(Text("Adds a Dictate button beside the Cerebral message box and open question answers. Recognition stays on this computer and uses Windows' installed desktop speech engine; no model, cloud account, or Worker tool is involved.", 13, Brushes.DimGray));
        status = Text(environment.Message, 13, environment.Available ? Brushes.Teal : Brushes.Firebrick); status.Margin = new Thickness(0, 4, 0, 14); content.Children.Add(status);

        content.Children.Add(Label("Speech language"));
        recognizer = new ComboBox { ItemsSource = environment.Recognizers, DisplayMemberPath = nameof(SpeechRecognizerChoice.Label), SelectedValuePath = nameof(SpeechRecognizerChoice.Id),
            MinWidth = 340, MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12), IsEnabled = environment.Available };
        recognizer.SelectedValue = environment.Recognizers.Any(x => x.Id == initial.RecognizerId) ? initial.RecognizerId : environment.Recognizers.FirstOrDefault()?.Id;
        AutomationProperties.SetAutomationId(recognizer, "DictationRecognizer"); content.Children.Add(recognizer);

        content.Children.Add(Label("Recognition filtering"));
        var modes = new[] { new ConfidenceChoice("Lenient · accepts more uncertain phrases", .30), new ConfidenceChoice("Balanced · recommended", .45), new ConfidenceChoice("Strict · rejects more uncertain phrases", .60) };
        confidence = new ComboBox { ItemsSource = modes, DisplayMemberPath = nameof(ConfidenceChoice.Label), SelectedValuePath = nameof(ConfidenceChoice.Value),
            MinWidth = 340, MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
        confidence.SelectedValue = modes.MinBy(x => Math.Abs(x.Value - initial.MinimumConfidence))!.Value;
        AutomationProperties.SetAutomationId(confidence, "DictationConfidence"); content.Children.Add(confidence);

        content.Children.Add(Label("Voice controls"));
        voiceControls = new CheckBox
        {
            Content = "Enable explicit voice commands in the main message box",
            IsChecked = initial.VoiceControlsEnabled,
            Margin = new Thickness(0, 0, 0, 6)
        };
        AutomationProperties.SetAutomationId(voiceControls, "EnableDictationVoiceControls");
        content.Children.Add(voiceControls);
        var voiceGuidance = Text(VoiceControls.ConfigurationGuidance, 12, Brushes.DimGray);
        AutomationProperties.SetAutomationId(voiceGuidance, "DictationVoiceControlGuidance");
        content.Children.Add(voiceGuidance);

        content.Children.Add(Label("Words and phrases to recognize"));
        content.Children.Add(Text("One per line. Add names, product terms, commands, and technical phrases that Windows often misses.", 12, Brushes.DimGray));
        vocabulary = Editor(string.Join(Environment.NewLine, initial.Vocabulary), 105, "DictationVocabulary"); content.Children.Add(vocabulary);

        content.Children.Add(Label("Corrections"));
        content.Children.Add(Text("One per line in heard phrase => replacement form. Example: see sharp => C#", 12, Brushes.DimGray));
        corrections = Editor(string.Join(Environment.NewLine, initial.Corrections.Select(x => x.Heard + " => " + x.Replacement)), 105, "DictationCorrections"); content.Children.Add(corrections);

        testButton = Button(WindowsSpeechTraining.TestActionLabel, TestMicrophone); testButton.IsEnabled = environment.Available; AutomationProperties.SetAutomationId(testButton, "TestDictationMicrophone");
        var training = Button(WindowsSpeechTraining.TrainActionLabel, OpenTraining); AutomationProperties.SetAutomationId(training, "OpenWindowsSpeechTraining");
        var speechCenter = Button(WindowsSpeechTraining.CenterActionLabel, OpenSpeechCenter); AutomationProperties.SetAutomationId(speechCenter, "OpenWindowsSpeechRecognitionCenter");
        var refresh = Button("Check speech support", RefreshSupport); AutomationProperties.SetAutomationId(refresh, "RefreshDictationSupport");
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        buttons.Children.Add(speechCenter); buttons.Children.Add(training); buttons.Children.Add(testButton); buttons.Children.Add(refresh); content.Children.Add(buttons);
        trainingStatus = Text(WindowsSpeechTraining.InitialGuidance, 12, Brushes.DimGray);
        trainingStatus.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetAutomationId(trainingStatus, "WindowsSpeechTrainingStatus");
        AutomationProperties.SetName(trainingStatus, "Windows speech training status");
        content.Children.Add(trainingStatus);

        content.Children.Add(Label("Live microphone input level"));
        var meter = new Grid { MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 4) };
        meter.ColumnDefinitions.Add(new ColumnDefinition());
        meter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        microphoneLevel = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(microphoneLevel, "DictationMicrophoneLevel");
        AutomationProperties.SetName(microphoneLevel, "Live microphone input level");
        microphoneLevelText = Text("Idle", 12, Brushes.DimGray);
        microphoneLevelText.Margin = new Thickness(10, 0, 0, 0);
        microphoneLevelText.MinWidth = 48;
        AutomationProperties.SetAutomationId(microphoneLevelText, "DictationMicrophoneLevelText");
        Grid.SetColumn(microphoneLevelText, 1);
        meter.Children.Add(microphoneLevel);
        meter.Children.Add(microphoneLevelText);
        content.Children.Add(meter);
        microphoneSignalText = Text("Signal: idle", 12, Brushes.DimGray);
        microphoneSignalText.Margin = new Thickness(0, 0, 0, 4);
        AutomationProperties.SetAutomationId(microphoneSignalText, "DictationMicrophoneSignal");
        content.Children.Add(microphoneSignalText);
        content.Children.Add(Text("The meter shows the live input level reported by Windows while Cerebral is listening. Microphone gain remains a Windows device setting.", 12, Brushes.DimGray));

        content.Children.Add(Label("Microphone test"));
        testOutput = Editor("", 90, "DictationTestOutput"); testOutput.ToolTip = "Recognized test phrases appear here. Test text is not saved."; content.Children.Add(testOutput);
        var trainingGuidance = Text(WindowsSpeechTraining.OwnershipGuidance, 12, Brushes.DimGray);
        AutomationProperties.SetAutomationId(trainingGuidance, "WindowsSpeechTrainingGuidance");
        content.Children.Add(trainingGuidance);

        var language = recognizer; var filtering = confidence; var controls = voiceControls; var words = vocabulary; var pairs = corrections;
        var hydrated = new DictationPreferences();
        DictationPreferences Draft() => new()
        {
            RecognizerId = language.SelectedValue as string,
            MinimumConfidence = filtering.SelectedValue is double value ? value : .45,
            VoiceControlsEnabled = controls.IsChecked == true,
            Vocabulary = words.Text == string.Join(Environment.NewLine, hydrated.Vocabulary) ? [.. hydrated.Vocabulary]
                : words.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
            Corrections = pairs.Text == string.Join(Environment.NewLine, hydrated.Corrections.Select(x => x.Heard + " => " + x.Replacement))
                ? DictationSettingsPolicy.Copy(hydrated).Corrections : DictationText.ParseCorrections(pairs.Text).ToList()
        };
        void Hydrate(DictationPreferences value)
        {
            hydrated = DictationSettingsPolicy.Copy(value);
            // Preserve an unavailable saved recognizer and an exact non-preset confidence value.
            var choices = environment.Recognizers.ToList();
            if (value.RecognizerId is { } saved && !choices.Any(x => x.Id == saved))
                choices.Add(new(saved, "Unavailable saved speech language", System.Globalization.CultureInfo.InvariantCulture));
            language.ItemsSource = choices; language.SelectedValue = value.RecognizerId;
            var filters = modes.ToList();
            if (!filters.Any(x => x.Value == value.MinimumConfidence)) filters.Add(new("Saved confidence", value.MinimumConfidence));
            filtering.ItemsSource = filters; filtering.SelectedValue = value.MinimumConfidence;
            controls.IsChecked = value.VoiceControlsEnabled;
            words.Text = string.Join(Environment.NewLine, value.Vocabulary);
            pairs.Text = string.Join(Environment.NewLine, value.Corrections.Select(x => x.Heard + " => " + x.Replacement));
        }
        var editor = new PackagePreferenceEditor<DictationPreferences>(preferences!, Draft, Hydrate,
            () => DictationSettingsPolicy.PrepareLegacy(PackagePreferenceJson.ReadLegacy(
                Path.Combine(((IDesktopPluginEnvironment)host!).DataDirectory, "settings.json"), DictationSettingsPolicy.MaximumBytes)), "Dictation");
        return new ScrollViewer { Content = editor.Create(content), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, Padding = new Thickness(0, 0, 8, 0) };
    }

    private async void TestMicrophone()
    {
        if (testOutput is null || session is null) return;
        try { await preferences!.ReadAsync(lifetime.Token); await session.ToggleAsync(new PanelTextEntry(testOutput), lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { Report("Load saved dictation preferences before testing the microphone.", false, true); }
    }

    private DesktopConversationBinding VoiceControlBinding() =>
        (host ?? throw new InvalidOperationException("The Cerebral plugin host is unavailable.")).GetConversation().Binding;

    private Task<DesktopOperationResult> ExecuteVoiceControlAsync(VoiceControlCommand command, IDesktopTextEntry source,
        DesktopConversationBinding expectedBinding, CancellationToken cancellationToken) =>
        VoiceControlExecutor.ExecuteAsync(host ?? throw new InvalidOperationException("The Cerebral plugin host is unavailable."),
            command, source, expectedBinding, cancellationToken);

    private void RefreshSupport()
    {
        environment = WindowsSpeechSupport.Inspect();
        if (recognizer is not null)
        {
            var old = recognizer.SelectedValue as string; recognizer.ItemsSource = environment.Recognizers;
            recognizer.SelectedValue = environment.Recognizers.Any(x => x.Id == old) ? old : environment.Recognizers.FirstOrDefault()?.Id;
            recognizer.IsEnabled = environment.Available;
        }
        if (testButton is not null) testButton.IsEnabled = environment.Available;
        Report(environment.Message, false, !environment.Available);
    }

    private void OpenTraining()
    {
        try
        {
            var launch = WindowsSpeechTraining.Resolve();
            var process = Process.Start(WindowsSpeechTraining.StartInfo(launch)) ?? throw new IOException($"Windows returned no process for {launch.FileName}.");
            if (trainingStatus is not null) trainingStatus.ToolTip = launch.FileName + " " + launch.Argument;
            ReportTraining("Opening Windows Speech Recognition voice training…", false);
            _ = ConfirmTrainingStartedAsync(process, launch);
        }
        catch (Exception ex)
        {
            ReportTraining("Windows speech training could not be opened: " + ex.Message, true);
        }
    }

    private void OpenSpeechCenter()
    {
        try
        {
            session?.Stop("Stopping dictation before opening Windows Speech Properties…");
            var launch = WindowsSpeechTraining.ResolveCenter();
            Process Start(SpeechCenterLaunch candidate) =>
                Process.Start(WindowsSpeechTraining.StartInfo(candidate)) ?? throw new IOException($"Windows returned no process for {candidate.FileName}.");
            Process process;
            try { process = Start(launch); }
            catch when (!launch.UsesCanonicalName)
            {
                process = Start(new(launch.FileName, WindowsSpeechTraining.SpeechRecognitionCanonicalName, true));
            }
            process.Dispose();
            ReportTraining("Windows accepted the request to open Speech Properties. Use it to manage speech profiles, microphone setup, and additional training, then run Test microphone and profile again.", false);
        }
        catch (Exception ex)
        {
            ReportTraining("Windows Speech Recognition Center could not be opened: " + ex.Message, true);
        }
    }

    private async Task ConfirmTrainingStartedAsync(Process process, SpeechTrainingLaunch launch)
    {
        try
        {
            await Task.Delay(600);
            process.Refresh();
            if (process.HasExited)
            {
                ReportTraining($"Windows speech training could not be opened: {Path.GetFileName(launch.FileName)} exited before its training window opened (exit code {process.ExitCode}).", true);
                return;
            }
            ReportTraining(WindowsSpeechTraining.OpenedGuidance, false);
            await process.WaitForExitAsync(lifetime.Token);
            ReportTraining(WindowsSpeechTraining.ClosedGuidance, false);
            testButton?.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ReportTraining("Windows speech training launch could not be confirmed: " + ex.Message, true); }
        finally { process.Dispose(); }
    }

    private void ReportTraining(string message, bool error)
    {
        if (disposed) return;
        if (trainingStatus is not null) { trainingStatus.Text = message; trainingStatus.Foreground = error ? Brushes.Firebrick : Brushes.Teal; }
        Report(message, false, error);
    }

    private void UpdateAudioLevel(int level, bool listening)
    {
        level = Math.Clamp(level, 0, 100);
        void Update()
        {
            if (disposed) return;
            if (microphoneLevel is not null) microphoneLevel.Value = level;
            if (microphoneLevelText is not null)
            {
                microphoneLevelText.Text = listening ? $"{level}%" : "Idle";
                microphoneLevelText.Foreground = listening ? Brushes.Teal : Brushes.DimGray;
            }
        }
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Update); else Update();
    }

    private void UpdateAudioSignal(string? problem, bool listening)
    {
        void Update()
        {
            if (disposed || microphoneSignalText is null) return;
            microphoneSignalText.Text = !listening ? "Signal: idle" : problem is null ? "Signal: listening" : "Signal: " + problem;
            microphoneSignalText.Foreground = problem is null ? Brushes.DimGray : Brushes.Firebrick;
        }
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Update); else Update();
    }

    private void OnConversationChanged(object? sender, DesktopConversationChangedEventArgs args) => session?.Stop();

    private void Report(string message, bool listening) => Report(message, listening, false);
    private void Report(string message, bool listening, bool error)
    {
        void Update()
        {
            if (disposed) return;
            if (status is not null) { status.Text = message; status.Foreground = error ? Brushes.Firebrick : listening ? Brushes.Teal : Brushes.DimGray; }
            if (testButton is not null) testButton.Content = listening ? "Stop microphone test" : WindowsSpeechTraining.TestActionLabel;
        }
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Update); else Update();
        try { host?.ReportStatus(message); } catch (ObjectDisposedException) { }
    }

    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        lifetime.Cancel();
        if (host is not null) host.ConversationChanged -= OnConversationChanged;
        preferences?.Dispose();
        session?.Dispose(); session = null; host = null;
        lifetime.Dispose();
    }

    private static TextBlock Heading(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private static TextBlock Text(string text, double size, Brush color) => new() { Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 5, 0, 5) };
    private static TextBox Editor(string text, double minimumHeight, string automationId)
    {
        var value = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = minimumHeight, MaxHeight = 210, Margin = new Thickness(0, 0, 0, 13), Padding = new Thickness(7) };
        AutomationProperties.SetAutomationId(value, automationId); return value;
    }
    private static Button Button(string text, Action action, bool primary = false)
    {
        var value = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 6, 10, 6), MinHeight = 31 };
        if (primary) { value.Background = Brushes.Teal; value.Foreground = Brushes.White; value.BorderBrush = Brushes.Teal; }
        value.Click += (_, _) => action(); return value;
    }

    private sealed record ConfidenceChoice(string Label, double Value);

    private sealed class PanelTextEntry(TextBox input) : IDesktopTextEntry
    {
        public string Id => "dictation-test";
        public string Purpose => "Dictation microphone test";
        public string Text => input.Dispatcher.CheckAccess() ? input.Text : input.Dispatcher.Invoke(() => input.Text);
        public int SelectionStart => input.Dispatcher.CheckAccess() ? input.SelectionStart : input.Dispatcher.Invoke(() => input.SelectionStart);
        public int SelectionLength => input.Dispatcher.CheckAccess() ? input.SelectionLength : input.Dispatcher.Invoke(() => input.SelectionLength);
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DesktopOperationResult Insert()
            {
                if (!input.IsLoaded && PresentationSource.FromVisual(input) is null) return new(DesktopOperationState.Rejected, "The microphone test is no longer open.");
                if (input.Text.Length - input.SelectionLength + text.Length > DesktopPluginContract.MaxTextChars) return new(DesktopOperationState.Rejected, "The microphone test text is full.");
                var start = input.SelectionStart; input.SelectedText = text; input.CaretIndex = start + text.Length; input.SelectionLength = 0;
                return new(DesktopOperationState.Completed);
            }
            return input.Dispatcher.CheckAccess() ? Task.FromResult(Insert()) : input.Dispatcher.InvokeAsync(Insert).Task;
        }
    }
}
