using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;
using Cerebral.Speech;
using Cerebral.PackageSettings;
using Microsoft.Win32;

namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin : IDesktopPlugin, IDesktopConfigurationPlugin,
    IDesktopTextEntrySubmissionLifecyclePlugin, IDesktopMountPlugin, IDesktopPluginStartupReadiness
{
    private IDesktopPluginHost? host;
    private IDesktopTextEntryFocusHost? focusHost;
    private AiDictationSettingsStore? store;
    private PackagePreferenceSession<AiDictationSettings>? preferences;
    private Task? startupPreferencesTask;
    private AiDictationSession? session;
    private readonly IAiDictationCapture capture;
    private readonly Func<AiDictationSettings, ISpeechProvider> providerFactory;
    private readonly Action<Window> showControlsWindow, hideControlsWindow;
    private readonly Func<(CancellationTokenSource Finish, CancellationTokenSource Cancel)> createMicrophoneTestCancellation;
    private readonly Func<Action<IChosenWindowsTextTarget>, IDisposable> observeWindowsFocus;
    private readonly Func<IDictationPlayback> playbackFactory;
    private string? lastControlError;
    private Dispatcher? dispatcher;
    private TextBlock? status;
    private ProgressBar? meter;
    private TextBlock? meterReading;
    private Button? microphoneTestButton;
    private CancellationTokenSource? microphoneTestFinish;
    private CancellationTokenSource? microphoneTestCancel;
    private Task? microphoneTestWork;
    private CancellationTokenSource? automaticCancellation;
    private Task? automaticWork;
    private IDesktopTextEntry? automaticTarget;
    private IDesktopTextEntry? submissionTarget;
    private bool autoTranscribeOnSilence;
    private bool disposed;
    public AiDictationPlugin() : this(new WinMmWaveCapture(), CreateSpeechProvider) { }
    private static ISpeechProvider CreateSpeechProvider(AiDictationSettings value)
    {
        if (string.IsNullOrEmpty(value.RuntimePath)) throw new SpeechProviderException("unavailable",
            "The AI dictation runtime is not configured and no matching installed runtime was found. Open Dictation Settings to select it.");
        if (string.IsNullOrEmpty(value.ModelPath) && !value.AutoTranscribeOnSilence) throw new SpeechProviderException("unavailable",
            "The AI dictation model is not configured and no matching installed model was found. Open Dictation Settings to select it.");
        return new NativeSpeechProvider(value);
    }
    internal AiDictationPlugin(IAiDictationCapture capture, Func<AiDictationSettings, ISpeechProvider> providerFactory,
        Action<Window>? showControlsWindow = null, Action<Window>? hideControlsWindow = null,
        Func<(CancellationTokenSource Finish, CancellationTokenSource Cancel)>? createMicrophoneTestCancellation = null,
        Func<Action<IChosenWindowsTextTarget>, IDisposable>? observeWindowsFocus = null,
        Func<IDictationPlayback>? playbackFactory = null)
    {
        this.capture = capture; this.providerFactory = providerFactory;
        this.playbackFactory = playbackFactory ?? (() => new DictationPlayback());
        this.observeWindowsFocus = observeWindowsFocus ?? (remember => new WindowsDictationFocusObserver(remember, ReportWindowsObservationError, ObserveForeignFocus));
        this.showControlsWindow = showControlsWindow ?? (window => window.Show());
        this.hideControlsWindow = hideControlsWindow ?? (window => window.Hide());
        this.createMicrophoneTestCancellation = createMicrophoneTestCancellation ?? (() => (new(), new()));
    }
    public IReadOnlyList<DesktopPanel> Panels => [];
    // Main already has the status microphone, Auto and floating control. Only the
    // native answer slot needs per-field actions; legacy actions are copied into Main too.
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new(SettingsPanelId, "AI Dictation", CreateSettings)];
    internal IReadOnlyList<DesktopTextEntryAction> TextEntryActions =>
    [
        new(AiDictationSession.ActionId, "🎙✦ AI Dictate", ToggleAsync,
            "Start or Stop dictation for this field. Completed phrases appear there for editing before Send; dictation never submits a message.")
    ];
    private const string AutoActionId = "ai-dictate-auto";

    public void Initialize(IDesktopPluginHost host)
    {
        if (this.host is not null) throw new InvalidOperationException("AI dictation is already initialized.");
        if (host is not IDesktopPluginEnvironment environment) throw new InvalidOperationException("Plugin-owned settings storage is required.");
        this.host = host;
        supportsWindowsMemory = environment.Surface == DesktopPluginSurface.Orchestrator;
        focusHost = host as IDesktopTextEntryFocusHost;
        dispatcher = Dispatcher.CurrentDispatcher;
        store = new(environment.DataDirectory, (host as IDesktopProductEnvironment)?.ProductPaths);
        preferences = new(host, AiDictationSettingsPolicy.PluginId, AiDictationSettingsPolicy.PackageVersion,
            new AiDictationSettingsPolicy(), AiDictationSettingsPolicy.Decode, AiDictationSettingsPolicy.Encode, () => new());
        // Startup only hydrates central preferences. It never reads or saves a legacy file.
        startupPreferencesTask = ReadStartupPreferencesAsync();
        session = new(host, store.CaptureDirectory, ReadRuntimePreferences, providerFactory,
            capture, action => dispatcher.InvokeAsync(action).Task.Unwrap(), Present, IsCurrentTarget, CorrectSegmentAsync);
        host.ConversationChanged += ConversationChanged;
        if (focusHost is not null)
        {
            observedFocusedTarget = focusHost.FocusedTextEntry;
            lastCerebralTarget = observedFocusedTarget;
            focusHost.FocusedTextEntryChanged += FocusedTextEntryChanged;
            // Saved Auto is a preference, never permission to start microphone capture on launch.
        }
    }

    internal AiDictationSettings ReadRuntimePreferences() => store!.ResolveRuntimePaths(preferences!.Current);

    public async Task WaitForStartupAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var startup = startupPreferencesTask ?? throw new InvalidOperationException("Initialize AI Dictation before awaiting startup.");
        // Await the one passive read begun by Initialize. Waiting never retries it,
        // starts recording or loads a speech model. The host owns a fresh-instance Retry.
        await startup.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed || hostUnavailable, this);
    }

    private async Task ReadStartupPreferencesAsync()
    {
        var value = await preferences!.ReadAsync();
        ObjectDisposedException.ThrowIf(disposed || hostUnavailable, this);
        // A normal unmount/remount can retire controls while this read awaits. Retain the
        // acknowledged preference even then; only presentation depends on mounted controls.
        // The tracked task faults on failure so the host cannot publish false readiness.
        autoTranscribeOnSilence = value.AutoTranscribeOnSilence;
        UpdateWindowsFocusObservation();
        if (CallbacksAvailable) RefreshTargetViews();
    }

    private Task ToggleAsync(IDesktopTextEntry target, CancellationToken ct)
    { explicitDestination = true; pinnedCerebralTarget = target; pinnedWindowsTarget = null;
        if (preferences?.State is not null && !preferences.Current.DockControls) ShowDictationControls(); return ChangeRecordingAsync(target, ct, false); }
    private Task ResumeAsync(IDesktopTextEntry target, CancellationToken ct) => ChangeRecordingAsync(target, ct, true);

    private async Task ChangeRecordingAsync(IDesktopTextEntry target, CancellationToken ct, bool freshSegment)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (submissionTarget is not null) throw new InvalidOperationException("Wait for dictation to finish preparing the message.");
        LockTarget(target);
        if (!freshSegment && session?.IsActive != true) session!.BeginDestinationRun(target);
        var lockVersion = targetLockVersion;
        try
        {
            if (microphoneTestWork is not null) await StopMicrophoneTestAsync(cancel: false);
            var latestPreferences = await preferences!.ReadAsync(ct);
            UpdateWindowsFocusObservation();
            autoTranscribeOnSilence = latestPreferences.AutoTranscribeOnSilence;
            CheckTargetLock(target, lockVersion); ct.ThrowIfCancellationRequested();
            if (freshSegment) await session!.ResumeAsync(target, ct);
            else await session!.ToggleAsync(target, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var activity = new DesktopTextEntryActivity(AiDictationSession.PluginId, AiDictationSession.ActionId,
                DesktopTextEntryActivityState.Error, "Configure AI Dictation first: " + ex.Message, false, "🎙✦ AI Dictate");
            if (CallbacksAvailable && target is IDesktopTextEntryActivitySink sink) sink.ReportActivity(activity);
            Present(activity, null);
        }
    }

    private Task ToggleAutomaticAsync(IDesktopTextEntry target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return SaveWindowOptionAsync(value => value with { AutoTranscribeOnSilence = !value.AutoTranscribeOnSilence });
    }
    private void FocusedTextEntryChanged(object? sender, DesktopTextEntryFocusChangedEventArgs e)
    {
        if (disposed || hostUnavailable) return;
        observedFocusedTarget = e.Entry;
        if (e.Entry is not null && !ReferenceEquals(e.Entry, lockedTarget))
            PauseForDestinationFocusChange("Keyboard focus moved to another Cerebral text box.");
        if (e.Entry is not null)
        { lastCerebralTarget = e.Entry; rememberedWindowsTarget = null; lastDestinationFocusTime = Stopwatch.GetTimestamp(); }
        if (controlsRetired) return;
        // Focus is an observation for the explicit "use Cerebral editor" action. It never
        // selects another destination, arms recording, or discards the existing review.
        RefreshTargetViews();
    }

    private void StartAutomatic(IDesktopTextEntry target)
    {
        if (!CallbacksAvailable || submissionTarget is not null || !automaticArmed || !autoTranscribeOnSilence || automaticPaused || !ReferenceEquals(target, lockedTarget) ||
            ReferenceEquals(automaticTarget, target) && automaticWork is { IsCompleted: false } && automaticCancellation is { IsCancellationRequested: false }) return;
        StopAutomatic(cancelSession: true);
        automaticTarget = target;
        var cancellation = new CancellationTokenSource();
        automaticCancellation = cancellation;
        automaticWork = RunAutomaticAsync(target, cancellation);
    }

    private async Task RunAutomaticAsync(IDesktopTextEntry target, CancellationTokenSource cancellation)
    {
        var destination = session?.DestinationIdentity;
        try
        {
            if (microphoneTestWork is not null) await StopMicrophoneTestAsync(cancel: false);
            while (!cancellation.IsCancellationRequested && autoTranscribeOnSilence && IsFocused(target))
            {
                if (session!.IsActive)
                    await session.Completion.WaitAsync(cancellation.Token);
                else
                {
                    var latestPreferences = await preferences!.ReadAsync(cancellation.Token);
                    autoTranscribeOnSilence = latestPreferences.AutoTranscribeOnSilence;
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!autoTranscribeOnSilence || !IsFocused(target) || !ReferenceEquals(automaticTarget, target)) return;
                    await session.ToggleAsync(target, cancellation.Token);
                    await session.Completion.WaitAsync(cancellation.Token);
                }
                if (session.LastState == DesktopTextEntryActivityState.Error || !IsFocused(target)) return;
                if (session.ContinuousAutomatic) return; // Stop or the total audio bound requires an explicit new Start.
                if (ReferenceEquals(submissionTarget, target)) return;
                await Task.Delay(125, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var activity = new DesktopTextEntryActivity(AiDictationSession.PluginId, AutoActionId,
                DesktopTextEntryActivityState.Error, "Automatic AI Dictation paused: " + ex.Message, false, "☑ Auto");
            if (CallbacksAvailable && target is IDesktopTextEntryActivitySink sink) sink.ReportActivity(activity);
            Present(activity, null);
        }
        finally
        {
            try { if (session is not null) await session.EndRunAsync(destination); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (CallbacksAvailable) ReportControlError(new IOException("The local speech helper could not confirm shutdown. Restart Cerebral before trying again.", error)); }
            if (ReferenceEquals(automaticCancellation, cancellation))
            {
                automaticCancellation = null; automaticWork = null; automaticTarget = null;
                automaticArmed = false; automaticPaused = true;
            }
            cancellation.Dispose(); RefreshTargetViews();
        }
    }

    public async Task<DesktopOperationResult> PrepareSubmissionAsync(IDesktopTextEntry target,
        CancellationToken cancellationToken = default)
        => await PrepareSubmissionCoreAsync(null, target, cancellationToken);

    private async Task<DesktopOperationResult> PrepareSubmissionCoreAsync(string? requestId, IDesktopTextEntry target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (submissionTarget is not null)
            return new(DesktopOperationState.Rejected, "Wait for the current dictation submission preparation to finish.");
        preparedRearm = null;
        using var available = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            target is IDesktopTextEntryLifetime lifetime ? lifetime.AvailabilityToken : CancellationToken.None);
        cancellationToken = available.Token;
        var destination = lockedTarget ?? session!.Review.Target;
        var main = ReadMainPromptTarget();
        var binding = host!.GetConversation().Binding;
        if (destination is null) return new(DesktopOperationState.Completed);
        if (ReferenceEquals(destination, main) &&
            (lockedBinding != binding || !SubmissionViewMatches(target, main!, binding)))
            return new(DesktopOperationState.Rejected, "The dictation composer or owner changed. Inspect the pending phrase before sending.");
        if (!ReferenceEquals(destination, target) && !ReferenceEquals(destination, main))
            return new(DesktopOperationState.Completed);
        var rearm = requestId is not null && ReferenceEquals(destination, main) && automaticArmed &&
            autoTranscribeOnSilence && !automaticPaused && !session!.IsOneOff;
        submissionTarget = target;
        var priorPending = controlActionPending;
        controlActionPending = true;
        try
        {
            // Main may supply a short-lived semantic view of its composer. Finish
            // our already owned destination; never redirect it to that view or infer
            // identity/authority from an editor ID or equal text.
            var automatic = automaticWork;
            FinishControlMicrophone();
            var preparationVersion = targetLockVersion;
            var result = await session!.PrepareSubmissionAsync(destination, cancellationToken);
            if (automatic is not null) await automatic.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.State == DesktopOperationState.Completed && rearm && targetLockVersion == preparationVersion && IsCurrentTarget(destination) &&
                host.GetDraft() is { } draft && draft.Binding == binding && draft.Text == destination.Text)
                preparedRearm = new(requestId!, binding, draft.Revision, destination, targetLockVersion);
            return result;
        }
        finally
        {
            if (ReferenceEquals(submissionTarget, target)) submissionTarget = null;
            controlActionPending = priorPending;
            RefreshTargetViews();
        }
    }

    private bool IsFocused(IDesktopTextEntry target) => CallbacksAvailable && automaticArmed && autoTranscribeOnSilence &&
        !automaticPaused && ReferenceEquals(target, lockedTarget) && IsCurrentTarget(target);

    private void StopAutomatic(bool cancelSession, bool reportPaused = false)
    {
        targetLockVersion++;
        var prior = automaticTarget;
        // Each cancellation must run even if another token's callback fails.
        var failures = new List<Exception>();
        Cancel(() => resumeFocusCancellation?.Cancel());
        Cancel(() => automaticCancellation?.Cancel());
        if (cancelSession) Cancel(() => session?.Cancel());
        if (failures.Count != 0) throw new AggregateException("Dictation cancellation failed.", failures);
        if (reportPaused && CallbacksAvailable && prior is IDesktopTextEntryActivitySink sink)
            sink.ReportActivity(new(AiDictationSession.PluginId, AutoActionId, DesktopTextEntryActivityState.Result,
                "Automatic AI Dictation is paused until an editable field receives keyboard focus.", false, "☑ Auto"));

        void Cancel(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is not OutOfMemoryException) { failures.Add(error); }
        }
    }

    private FrameworkElement CreateSettings()
    {
        var panel = new StackPanel { Margin = new Thickness(18), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(Text("Local AI dictation", 22));
        panel.Children.Add(Text("Automatic dictation keeps listening while completed phrases are processed in order by the selected warm CPU model, including Parakeet. Optional live words are a separate mode requiring the Nemotron English streaming model. One-off Record / Stop / Play / Insert remains available. Speech recognition uses no Chat agent or network service."));
        var settings = new AiDictationSettings();
        status = Text("Choose an input and test its signal. Save preferences to apply changes.");
        AutomationProperties.SetAutomationId(status, "AiDictationStatus"); panel.Children.Add(status);
        var cpu = new CheckBox { Content = "CPU execution (required by the bundled NeMo-Speech.cpp runtime)",
            IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 2, 0, 8) };
        AutomationProperties.SetAutomationId(cpu, "AiDictationCpuRequired"); panel.Children.Add(cpu);

        panel.Children.Add(Text("Microphone input", 18));
        panel.Children.Add(Text("Choose the recording source used by AI Dictation. Windows default follows the input selected by the operating system."));
        var device = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = nameof(InputChoice.Label),
            IsTextSearchEnabled = true, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(device, "AiDictationInputDevice");
        void RefreshDevices(string? chosen = null)
        {
            var selectedName = chosen ?? (device.SelectedItem as InputChoice)?.DeviceName ?? settings.InputDeviceName;
            var choices = new List<InputChoice> { new("", "Windows default input", true) };
            try
            {
                choices.AddRange(WinMmWaveCapture.InputDevices.GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).Select(value => new InputChoice(value.Name, value.Label, true)));
            }
            catch (Exception ex)
            {
                status.Text = "Could not enumerate microphone inputs: " + ex.Message; status.Foreground = ThemeBrush(DesktopThemeResourceKeys.ErrorTextBrush, Brushes.Firebrick);
            }
            if (!string.IsNullOrWhiteSpace(selectedName) &&
                !choices.Any(value => string.Equals(value.DeviceName, selectedName, StringComparison.OrdinalIgnoreCase)))
                choices.Insert(1, new(selectedName, "Unavailable saved input · " + selectedName, false));
            device.ItemsSource = choices;
            device.SelectedItem = choices.FirstOrDefault(value => string.Equals(value.DeviceName, selectedName, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        }
        RefreshDevices();
        var refreshDevices = new Button { Content = "Refresh inputs", Padding = new Thickness(10, 5, 10, 5) };
        AutomationProperties.SetAutomationId(refreshDevices, "AiDictationRefreshInputs");
        refreshDevices.Click += (_, _) => RefreshDevices();
        var deviceRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        deviceRow.ColumnDefinitions.Add(new ColumnDefinition()); deviceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        deviceRow.Children.Add(device); Grid.SetColumn(refreshDevices, 1); deviceRow.Children.Add(refreshDevices); panel.Children.Add(deviceRow);

        var boostLabel = Text("", 13);
        var boost = new Slider { Minimum = 0, Maximum = 24, TickFrequency = 3, IsSnapToTickEnabled = true,
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight, Value = settings.InputBoostDb,
            AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft, AutoToolTipPrecision = 0,
            Margin = new Thickness(0, 0, 0, 5) };
        AutomationProperties.SetAutomationId(boost, "AiDictationSoftwareBoost");
        void ShowBoost() => boostLabel.Text = $"Software boost: +{boost.Value:0} dB · {Math.Pow(10, boost.Value / 20d):0.0}× amplitude";
        ShowBoost(); panel.Children.Add(boostLabel); panel.Children.Add(boost);
        panel.Children.Add(Text("Boost is applied to the recording before local transcription. It can help a quiet microphone, but it cannot restore a missing signal and excessive boost can clip loud speech."));
        var normalize = new CheckBox { Content = "Automatically normalize quiet recordings before transcription", IsChecked = settings.NormalizeQuietAudio,
            Margin = new Thickness(0, 2, 0, 8), ToolTip = "Raises usable quiet speech toward a safe peak, with an 18 dB cap. Near-silence is not amplified." };
        AutomationProperties.SetAutomationId(normalize, "AiDictationNormalizeInput"); panel.Children.Add(normalize);
        var calibrationLabel = Text("", 13);
        var calibration = new Slider { Minimum = 5, Maximum = 10, TickFrequency = 1, IsSnapToTickEnabled = true,
            Value = settings.NormalizationCalibrationSeconds, AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 0, Margin = new Thickness(0, 0, 0, 5), IsEnabled = normalize.IsChecked == true };
        AutomationProperties.SetAutomationId(calibration, "AiDictationNormalizationCalibration");
        void ShowCalibration() => calibrationLabel.Text = $"Normalization sample: first {calibration.Value:0} seconds";
        ShowCalibration(); panel.Children.Add(calibrationLabel); panel.Children.Add(calibration);
        panel.Children.Add(Text("The short sample chooses one bounded gain level. Longer recordings then write the remaining audio with that fixed level instead of rescanning the whole recording."));

        var automatic = new CheckBox { Content = "Automatically listen while an editable Cerebral field has focus", IsChecked = settings.AutoTranscribeOnSilence,
            Margin = new Thickness(0, 5, 0, 5), ToolTip = "Use Auto in the status bar for the message composer, or beside a supported question answer. Leaving editable fields pauses listening." };
        AutomationProperties.SetAutomationId(automatic, "AiDictationAutoTranscribe"); panel.Children.Add(automatic);
        var liveWords = new CheckBox { Content = "Show live words while speaking (requires the optional streaming model)",
            IsChecked = settings.LiveWordsWhileSpeaking, Margin = new Thickness(0, 2, 0, 5),
            ToolTip = "Off: recognize completed phrases after pauses with the selected offline model. On: genuine partial recognition using the separately installed streaming model; never simulated typing." };
        AutomationProperties.SetAutomationId(liveWords, "AiDictationLiveWords"); panel.Children.Add(liveWords);
        var silenceLabel = Text("", 13);
        var silence = new Slider { Minimum = .5, Maximum = 10, TickFrequency = .5, IsSnapToTickEnabled = true,
            Value = settings.SilenceWaitSeconds, AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 1, Margin = new Thickness(0, 0, 0, 5), IsEnabled = automatic.IsChecked == true };
        AutomationProperties.SetAutomationId(silence, "AiDictationSilenceWait");
        void ShowSilence() => silenceLabel.Text = $"Quiet time before transcription: {silence.Value:0.0} seconds";
        ShowSilence(); panel.Children.Add(silenceLabel); panel.Children.Add(silence);
        panel.Children.Add(Text("Start pins the destination and cursor. Confirmed inserted phrases advance that cursor; user edits, focus or cursor changes stop further writes. Phrase mode keeps 200 ms of pre-roll, at most two waiting phrases / 30 seconds of waiting audio, and stops at 30 seconds without a complete pause or two minutes total. A limit or interruption retains run audio for Play; inspect earlier inserted text before recovery."));

        meter = new ProgressBar { Minimum = 0, Maximum = 100, Height = 20, Margin = new Thickness(0, 6, 0, 2),
            ToolTip = "Processed peak level sent to the speech model" };
        AutomationProperties.SetAutomationId(meter, "AiDictationInputLevel"); panel.Children.Add(meter);
        var scale = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var _ in Enumerable.Range(0, 5)) scale.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var value in new[] { 0, 25, 50, 75, 100 })
        {
            var label = Text(value.ToString(), 11); label.HorizontalAlignment = value == 0 ? HorizontalAlignment.Left : value == 100 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            Grid.SetColumn(label, value / 25); scale.Children.Add(label);
        }
        panel.Children.Add(scale);
        meterReading = Text("Processed signal: 0%", 12); AutomationProperties.SetAutomationId(meterReading, "AiDictationInputReading"); panel.Children.Add(meterReading);
        microphoneTestButton = new Button { Content = "Test microphone", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(microphoneTestButton, "AiDictationMicrophoneTest");
        microphoneTestButton.Click += async (_, _) => await HandleMicrophoneTestAsync();
        var windowsInput = new Button { Content = "Windows microphone properties", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(windowsInput, "AiDictationWindowsInputSettings");
        windowsInput.Click += (_, _) =>
        {
            try { OpenWindowsInputSettings(); }
            catch (Exception ex) { status.Text = "Could not open Windows microphone properties: " + ex.Message; status.Foreground = ThemeBrush(DesktopThemeResourceKeys.ErrorTextBrush, Brushes.Firebrick); }
        };
        var microphoneActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
        microphoneTestButton.Margin = new Thickness(0, 0, 8, 0); microphoneActions.Children.Add(microphoneTestButton); microphoneActions.Children.Add(windowsInput); panel.Children.Add(microphoneActions);
        panel.Children.Add(Text("The meter shows the processed signal after software boost. Hardware gain remains driver-controlled; Windows microphone properties exposes it when the selected device supports it."));

        panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 10) });
        panel.Children.Add(Text("Local transcription runtime", 18));
        var runtime = Field("NeMo-Speech.cpp 0.1.0 CPU executable", settings.RuntimePath, "AiDictationRuntimePath", "Applications (*.exe)|*.exe");
        var model = Field("Parakeet GGUF model", settings.ModelPath, "AiDictationModelPath", "GGUF models (*.gguf)|*.gguf");
        var id = Field("Model ID", settings.ModelId, "AiDictationModelId");
        var revision = Field("Model revision", settings.ModelRevision, "AiDictationModelRevision");
        var source = Field("Original model link", settings.ModelSource, "AiDictationModelSource");
        var license = Field("Model license", settings.ModelLicense, "AiDictationModelLicense");
        AiDictationSettings Draft() => settings with { RuntimePath = runtime.Text, ModelPath = model.Text, ModelId = id.Text,
            ModelRevision = revision.Text, ModelSource = source.Text, ModelLicense = license.Text,
            InputDeviceName = (device.SelectedItem as InputChoice)?.DeviceName ?? "", InputBoostDb = boost.Value,
            NormalizeQuietAudio = normalize.IsChecked == true, NormalizationCalibrationSeconds = calibration.Value,
            AutoTranscribeOnSilence = automatic.IsChecked == true, LiveWordsWhileSpeaking = liveWords.IsChecked == true, SilenceWaitSeconds = silence.Value };
        void Hydrate(AiDictationSettings value)
        {
            settings = value;
            runtime.Text = value.RuntimePath; model.Text = value.ModelPath; id.Text = value.ModelId;
            revision.Text = value.ModelRevision; source.Text = value.ModelSource; license.Text = value.ModelLicense;
            RefreshDevices(value.InputDeviceName); boost.Value = value.InputBoostDb; normalize.IsChecked = value.NormalizeQuietAudio;
            calibration.Value = value.NormalizationCalibrationSeconds; automatic.IsChecked = value.AutoTranscribeOnSilence;
            liveWords.IsChecked = value.LiveWordsWhileSpeaking;
            silence.Value = value.SilenceWaitSeconds;
            ShowBoost(); ShowCalibration(); ShowSilence();
        }
        boost.ValueChanged += (_, _) => ShowBoost();
        normalize.Checked += (_, _) => calibration.IsEnabled = true;
        normalize.Unchecked += (_, _) => calibration.IsEnabled = false;
        calibration.ValueChanged += (_, _) => ShowCalibration();
        automatic.Checked += (_, _) => silence.IsEnabled = true;
        automatic.Unchecked += (_, _) => silence.IsEnabled = false;
        silence.ValueChanged += (_, _) => ShowSilence();
        panel.Children.Add(Text("Runtime: NVIDIA NeMo-Speech.cpp 0.1.0 · Apache-2.0. Preserve the package's licenses and third-party notices. Model: NVIDIA Parakeet TDT 0.6b v3 · CC BY 4.0; preserve its model card and attribution."));
        panel.Children.Add(Text("Recordings are limited to two minutes. Successfully applied automatic recordings and temporary phrase files are removed. Interrupted phrase-mode run audio remains available for Play until explicit Discard or plugin shutdown; it is never retried automatically. One-off recordings remain for Play / Insert. Cleanup failures are reported."));
        var output = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 6) };
        AutomationProperties.SetAutomationId(output, "AiDictationTestOutput");
        var entry = new TestEntry(output);
        var test = new Button { Content = "🎙✦ Test AI dictation", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(test, "AiDictationTest");
        test.Click += async (_, _) => await ToggleAsync(entry, CancellationToken.None);
        entry.Activity = activity => test.Content = activity.Active ? activity.ButtonLabel : "🎙✦ Test AI dictation";
        panel.Children.Add(test); panel.Children.Add(output);
        panel.Unloaded += (_, _) => { entry.Available.Cancel(); session?.Cancel(); CancelMicrophoneTest(); };
        panel.Loaded += (_, _) => entry.RenewAvailability();
        var editor = new PackagePreferenceEditor<AiDictationSettings>(preferences!, Draft, Hydrate, () => store!.Load(),
            "AiDictation", value => autoTranscribeOnSilence = value.AutoTranscribeOnSilence);
        return new ScrollViewer { Content = editor.Create(panel, value => store!.ResolveInstalledPaths(value)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        TextBox Field(string label, string value, string automationId, string? filter = null)
        {
            panel.Children.Add(Text(label));
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Text = value, MaxLength = 2048, MinWidth = 180, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(box, automationId); row.Children.Add(box);
            if (filter is not null)
            {
                var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
                Grid.SetColumn(browse, 1); row.Children.Add(browse);
                browse.Click += (_, _) =>
                {
                    var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false };
                    if (dialog.ShowDialog(Window.GetWindow(panel)) == true && !disposed) box.Text = dialog.FileName;
                };
            }
            panel.Children.Add(row);
            return box;
        }
    }

    private void Present(DesktopTextEntryActivity activity, double? level)
    {
        if (!CallbacksAvailable) return;
        if (activity.State == DesktopTextEntryActivityState.Error)
        {
            if (lastControlError != activity.Message) ReportHostStatus(activity.Message);
            lastControlError = activity.Message;
        }
        else if (activity.State == DesktopTextEntryActivityState.Listening && activity.Active) lastControlError = null;
        if (!activity.Active && (!autoTranscribeOnSilence || activity.State == DesktopTextEntryActivityState.Error))
        { automaticArmed = false; automaticPaused = true; }
        if (!activity.Active && activity.State == DesktopTextEntryActivityState.Result && grammarNotice is { } notice)
            activity = activity with { Message = activity.Message + " " + notice };
        controlActivity = activity; RefreshTargetViews();
        if (status is not null) { status.Text = activity.Message; status.Foreground = activity.State == DesktopTextEntryActivityState.Error
            ? ThemeBrush(DesktopThemeResourceKeys.ErrorTextBrush, Brushes.Firebrick)
            : ThemeBrush(DesktopThemeResourceKeys.MutedTextBrush, Brushes.DimGray); }
        SetMeter(activity.Active ? level ?? 0 : 0);
    }

    private void SetMeter(double level)
    {
        var normalized = Math.Clamp(level, 0, 1); var percent = normalized * 100;
        if (meter is not null) { meter.Value = percent; meter.Foreground = normalized >= .99 ? Brushes.OrangeRed : normalized >= .12 ? Brushes.MediumSeaGreen : Brushes.SlateGray; }
        if (meterReading is not null)
        {
            meterReading.Text = $"Processed signal: {percent:0}%" + (normalized >= .99 ? " · clipping — lower boost or hardware gain" : normalized < .02 ? " · very quiet" : "");
            meterReading.Foreground = normalized >= .99
                ? ThemeBrush(DesktopThemeResourceKeys.ErrorTextBrush, Brushes.Firebrick)
                : ThemeBrush(DesktopThemeResourceKeys.MutedTextBrush, Brushes.DimGray);
        }
    }

    // The Configure button and its returned task share this error boundary.
    private async Task HandleMicrophoneTestAsync()
    {
        try { await ToggleMicrophoneTestAsync(); }
        catch (Exception error)
        {
            if (!CallbacksAvailable) return;
            status!.Text = "Microphone test failed: " + error.Message;
            status.Foreground = ThemeBrush(DesktopThemeResourceKeys.ErrorTextBrush, Brushes.Firebrick);
        }
    }

    private async Task ToggleMicrophoneTestAsync()
    {
        if (!CallbacksAvailable) return;
        if (microphoneTestWork is not null) { await StopMicrophoneTestAsync(cancel: false); return; }
        if (session?.IsActive == true) throw new InvalidOperationException("Stop the active AI Dictation recording before testing the microphone.");
        var selected = await preferences!.ReadAsync();
        if (!CallbacksAvailable) return;
        Directory.CreateDirectory(store!.CaptureDirectory);
        var path = Path.Combine(store.CaptureDirectory, "input-test-" + Guid.NewGuid().ToString("N") + ".wav");
        (microphoneTestFinish, microphoneTestCancel) = createMicrophoneTestCancellation();
        var work = RunMicrophoneTestAsync(path, selected, microphoneTestFinish, microphoneTestCancel);
        microphoneTestWork = work;
        // Capture providers may complete or fail synchronously. Clear after assignment and
        // only for this operation, so a settled task cannot leave the test stuck active.
        try { await work; }
        finally { if (ReferenceEquals(microphoneTestWork, work)) microphoneTestWork = null; }
    }

    private async Task RunMicrophoneTestAsync(string path, AiDictationSettings selected, CancellationTokenSource finish, CancellationTokenSource cancel)
    {
        Exception? primary = null;
        var cleanupFailures = new List<Exception>();
        var recordingMayRemain = false;
        try
        {
            microphoneTestButton!.Content = "Stop microphone test";
            status!.Text = "Testing the selected microphone. Speak normally and watch the processed level."; status.Foreground = ThemeBrush(DesktopThemeResourceKeys.MutedTextBrush, Brushes.DimGray);
            await capture.CaptureAsync(path, selected.Capture, finish.Token, cancel.Token, value =>
            {
                if (CallbacksAvailable && ReferenceEquals(microphoneTestCancel, cancel))
                    _ = dispatcher!.InvokeAsync(() =>
                    {
                        if (CallbacksAvailable && ReferenceEquals(microphoneTestCancel, cancel)) SetMeter(value);
                    });
            });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { primary = error; }
        finally
        {
            // Retire owned bookkeeping before fallible presentation/disposal. Every cleanup
            // is independent; a failed meter reset cannot strand either cancellation source.
            if (ReferenceEquals(microphoneTestFinish, finish)) microphoneTestFinish = null;
            if (ReferenceEquals(microphoneTestCancel, cancel)) microphoneTestCancel = null;
            // Delete directly: an inaccessible recording must not look like an absent file.
            Cleanup(() => File.Delete(path), "The microphone test recording could not be removed.", recording: true);
            if (CallbacksAvailable)
            {
                Cleanup(() => microphoneTestButton!.Content = "Test microphone", "The microphone test button could not be reset.");
                Cleanup(() => SetMeter(0), "The microphone test meter could not be reset.");
            }
            Cleanup(finish.Dispose, "The microphone test finish token could not be disposed.");
            Cleanup(cancel.Dispose, "The microphone test cancellation token could not be disposed.");
        }
        if (primary is not null && cleanupFailures.Count == 0)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanupFailures.Count != 0)
        {
            if (primary is not null) cleanupFailures.Insert(0, primary);
            var message = recordingMayRemain
                ? "Microphone testing failed; a recording may remain in the plugin's temporary capture folder."
                : "Microphone testing stopped, but owned cleanup reported failures.";
            throw new AggregateException(message, cleanupFailures);
        }
        if (CallbacksAvailable)
        {
            status!.Text = "Microphone test stopped. Adjust the input or boost and test again as needed.";
            status.Foreground = ThemeBrush(DesktopThemeResourceKeys.MutedTextBrush, Brushes.DimGray);
        }

        void Cleanup(Action cleanup, string message, bool recording = false)
        {
            try { cleanup(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                recordingMayRemain |= recording;
                cleanupFailures.Add(new InvalidOperationException(message, error));
            }
        }
    }

    private async Task StopMicrophoneTestAsync(bool cancel)
    {
        var work = microphoneTestWork;
        if (work is null) return;
        if (cancel) microphoneTestCancel?.Cancel(); else microphoneTestFinish?.Cancel();
        await work;
    }

    private void CancelMicrophoneTest() => microphoneTestCancel?.Cancel();

    private static void OpenWindowsInputSettings()
    {
        var start = new ProcessStartInfo("control.exe", "mmsys.cpl,,1") { UseShellExecute = true };
        if (Process.Start(start) is null) throw new IOException("Windows microphone properties could not be opened.");
    }
    private void ConversationChanged(object? sender, DesktopConversationChangedEventArgs e)
    {
        if (!CallbacksAvailable) return;
        if (lockedBinding == e.Conversation.Binding) { RefreshTargetViews(); return; }
        if (lockedTarget is WindowsDictationTarget) { RefreshTargetViews(); return; }
        automaticArmed = false;
        StopAutomatic(cancelSession: true);
        lockedTarget = null; lockedBinding = null; explicitDestination = false;
        observedFocusedTarget = lastCerebralTarget = null;
        frontMicrophoneStarting = false; RefreshTargetViews();
    }
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Detach callbacks before closing WPF controls; closing can synchronously raise events.
        if (host is not null) AttemptTeardown(() => host.ConversationChanged -= ConversationChanged);
        if (focusHost is not null) AttemptTeardown(() => focusHost.FocusedTextEntryChanged -= FocusedTextEntryChanged);
        RetireControls();
        AttemptTeardown(() => preferences?.Dispose());
        AttemptTeardown(() => session?.Dispose());
        if (teardownFailures.Count != 0)
            throw new AggregateException("AI Dictation retired, but owned cleanup reported failures.", teardownFailures);
    }
    private static TextBlock Text(string value, double size = 13) => new()
    {
        Text = value, FontSize = size, Foreground = ThemeBrush(DesktopThemeResourceKeys.TextBrush, SystemColors.WindowTextBrush),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
    };
    private static Brush ThemeBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
    private sealed record InputChoice(string DeviceName, string Label, bool Available);

    private sealed class TestEntry(TextBox box) : IDesktopTextEntry, IDesktopTextEntryLifetime, IDesktopTextEntryActivitySink
    {
        public CancellationTokenSource Available { get; private set; } = new();
        public Action<DesktopTextEntryActivity>? Activity { get; set; }
        public string Id => "ai-dictation-test";
        public string Purpose => "AI dictation microphone test";
        public string Text => box.Text;
        public int SelectionStart => box.SelectionStart;
        public int SelectionLength => box.SelectionLength;
        public CancellationToken AvailabilityToken => Available.Token;
        public void RenewAvailability()
        {
            if (!Available.IsCancellationRequested) return;
            Available.Dispose(); Available = new();
        }
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Available.Token.ThrowIfCancellationRequested();
            box.SelectedText = text; box.CaretIndex = box.SelectionStart + text.Length;
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public void ReportActivity(DesktopTextEntryActivity activity) => Activity?.Invoke(activity);
    }
}
