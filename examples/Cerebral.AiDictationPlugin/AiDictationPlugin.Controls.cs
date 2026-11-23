using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin
{
    private IDesktopTextEntry? lockedTarget;
    private DesktopConversationBinding? lockedBinding;
    private bool automaticPaused;
    private long targetLockVersion;
    private CancellationTokenSource? resumeFocusCancellation;
    private Window? controlsWindow, controlsOwner;
    private NonActivatingDictationWindow? floatingBehavior;
    private readonly List<DictationMicrophoneView> microphoneViews = [];
    private readonly List<DictationReviewView> reviewViews = [];
    private bool automaticArmed;
    private DesktopTextEntryActivity? controlActivity;
    private bool controlActionPending, frontMicrophoneStarting;
    private bool explicitDestination;

    public IReadOnlyList<IDesktopMountContribution> MountContributions =>
    [
        new DesktopMountView(new("dictation-control", DesktopMountSlots.StatusBar)
            { Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator } }, "Dictation", CreateDictationStatus),
        new DesktopMountAction(new("dictation-controls-open", DesktopMountSlots.Menus)
            { Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator } }, "Dictation controls",
            ct => ExecuteControlsActionAsync(show: true, ct), "Open the floating microphone without starting recording or enabling Auto."),
        new DesktopMountAction(new("dictation-controls-close", DesktopMountSlots.Menus)
            { Order = 1, Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator } }, "Hide dictation controls",
            ct => ExecuteControlsActionAsync(show: false, ct), "Hide the floating microphone while preserving the current recording and Auto state."),
        // Status controls own composer dictation; explicit field actions belong to question answers.
        .. TextEntryActions.Select(action => new DesktopMountTextEntryAction(new(action.Id, DesktopMountSlots.AnswerActions)
            { Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator } },
            action.Label, action.ExecuteAsync, action.ToolTip))
    ];

    private void LockTarget(IDesktopTextEntry target)
    {
        if (!ReferenceEquals(lockedTarget, target))
        {
            if (session?.Review.InsertionUnconfirmed == true)
                throw new InvalidOperationException("Inspect the unconfirmed insertion and clear its review before choosing another destination.");
            StopAutomatic(cancelSession: true);
            if (lockedTarget is WindowsDictationTarget old) old.Dispose();
            session?.Review.ChooseTarget(target);
        }
        if (!TryReadHost(() => host!.GetConversation().Binding, out var binding))
            throw new OperationCanceledException("The dictation host is no longer available.");
        controlsRetired = false;
        lockedTarget = target; lockedBinding = binding;
        RefreshTargetViews();
    }

    private IDesktopTextEntry CurrentLockedTarget()
    {
        if (lockedTarget is null || !IsCurrentTarget(lockedTarget))
            throw new InvalidOperationException("Choose the intended text box in Dictation before recording.");
        return lockedTarget;
    }

    private void CheckTargetLock(IDesktopTextEntry target, long version)
    {
        if (version != targetLockVersion || !ReferenceEquals(CurrentLockedTarget(), target) || !IsCurrentTarget(target))
            throw new OperationCanceledException("The dictation target changed before recording could start.");
    }

    // The floating window never becomes the editor. A missing focus report can occur while
    // the user clicks a composer action; another editor or deactivated owner retires the work.
    private bool IsCurrentTarget(IDesktopTextEntry target)
    {
        if (!CallbacksAvailable || !ReferenceEquals(lockedTarget, target)) return false;
        try
        {
            if (target is IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true }) return false;
        }
        catch (ObjectDisposedException) { return false; }
        return TryReadHost(() => (target is WindowsDictationTarget ? preferences?.State is not null && preferences.Current.AllowExternalTarget : host?.GetConversation().Binding == lockedBinding) &&
            (target is not WindowsDictationTarget external || external.Available), out var current) && current;
    }

    private IDesktopTextEntry? FocusedControlTarget()
    {
        if (!CallbacksAvailable) return null;
        if (explicitDestination || session?.IsActive == true || session?.Review.HasText == true || session?.Review.InsertionUnconfirmed == true)
            return lockedTarget is not null && IsCurrentTarget(lockedTarget) ? lockedTarget : null;
        if (rememberedWindowsTarget is not null) return null;
        // Reuse real host-reported history; only a missing history uses the optional
        // genuine composer route. TryReadHost handles revoked host capabilities.
        var target = lastCerebralTarget ?? observedFocusedTarget;
        if (target is null && !TryReadHost(() => (host as IDesktopDefaultTextEntryHost)?.DefaultTextEntry, out target)) return null;
        try
        {
            return target is not null && target is not IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true } ? target : null;
        }
        catch (ObjectDisposedException) { observedFocusedTarget = null; return null; }
    }

    private Task ResumeLockedTargetAsync() => StartLockedTargetAsync(oneOff: false);
    private async Task StartLockedTargetAsync(bool oneOff)
    {
        var selected = CurrentLockedTarget(); var version = targetLockVersion;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
            selected is IDesktopTextEntryLifetime available ? available.AvailabilityToken : CancellationToken.None);
        resumeFocusCancellation?.Cancel(); resumeFocusCancellation = attempt;
        try
        {
            attempt.Token.ThrowIfCancellationRequested(); CheckTargetLock(selected, version);
            // Nonactivating microphone clicks preserve the caret and need no focus request.
            // Main may be collapsed after Send. Its exact owned draft/caret CAS
            // remains available; recording must not depend on acquiring keyboard focus.
            if (!ReferenceEquals(selected, ReadMainPromptTarget()) &&
                selected is IDesktopTextEntryFocus { IsKeyboardFocusWithin: false } focus)
            {
                var focused = await focus.FocusAsync(attempt.Token);
                if (focused.State is not DesktopOperationState.Completed and not DesktopOperationState.Accepted)
                    throw new InvalidOperationException(focused.Message ?? "The chosen editor could not receive focus.");
            }
            attempt.Token.ThrowIfCancellationRequested(); CheckTargetLock(selected, version);
            if (oneOff) session!.BeginOneOff(selected); else session!.BeginDestinationRun(selected);
            automaticArmed = !oneOff; automaticPaused = oneOff;
            if (!oneOff && autoTranscribeOnSilence) StartAutomatic(selected); else await ResumeAsync(selected, CancellationToken.None);
        }
        finally { if (ReferenceEquals(resumeFocusCancellation, attempt)) resumeFocusCancellation = null; }
    }

    private Task ToggleControlMicrophoneAsync() => ToggleMicrophoneAsync(mainPrompt: false);
    private Task ToggleStatusMicrophoneAsync() => ToggleMicrophoneAsync(mainPrompt: true);
    private Task ToggleOneOffMicrophoneAsync() => ToggleMicrophoneAsync(mainPrompt: false, oneOff: true);
    private async Task ToggleMicrophoneAsync(bool mainPrompt, bool oneOff = false)
    {
        if (!CallbacksAvailable) return;
        if (!mainPrompt && (preferences?.State is null || !preferences.Current.DockControls)) ShowDictationControls();
        if (frontMicrophoneStarting || automaticArmed || session?.IsActive == true)
        {
            FinishControlMicrophone();
            return;
        }
        if (controlActionPending || session?.IsActive == true) return;
        var version = targetLockVersion;
        controlActionPending = frontMicrophoneStarting = true; RefreshTargetViews();
        try
        {
            await StopPlaybackAsync();
            if (oneOff)
            {
                var latest = await preferences!.ReadAsync();
                autoTranscribeOnSilence = latest.AutoTranscribeOnSilence;
            }
            var target = mainPrompt ? ReadMainPromptTarget() : await ResolveStartTargetAsync();
            if (!CallbacksAvailable || !frontMicrophoneStarting || targetLockVersion != version)
            { if (target is WindowsDictationTarget abandoned && !ReferenceEquals(target, lockedTarget)) abandoned.Dispose(); return; }
            if (target is null) throw new InvalidOperationException("Focus the Cerebral prompt or choose an eligible text box before pressing Start.");
            LockTarget(target); await StartLockedTargetAsync(oneOff);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportControlError(error); }
        finally { controlActionPending = frontMicrophoneStarting = false; RefreshTargetViews(); }
    }

    private void FinishControlMicrophone()
    {
        automaticArmed = false; automaticPaused = true; frontMicrophoneStarting = false;
        targetLockVersion++; resumeFocusCancellation?.Cancel();
        // Do not cancel automaticCancellation: the owned segment is linked to that token.
        // Finish it, then the automatic loop observes the pause and exits without a new capture.
        session?.FinishCurrent();
        RefreshTargetViews();
    }

    private async Task ToggleControlAutomaticAsync()
    {
        if (!CallbacksAvailable || controlActionPending || session?.IsProcessing == true && !autoTranscribeOnSilence) return;
        var target = FocusedControlTarget();
        if (target is null) { RefreshTargetViews(); return; }
        controlActionPending = true; RefreshTargetViews();
        try { await ToggleAutomaticAsync(target, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportControlError(error); }
        finally { controlActionPending = false; RefreshTargetViews(); }
    }

    private void ReportControlError(Exception error)
    {
        Present(new(AiDictationSession.PluginId, AiDictationSession.ActionId,
            DesktopTextEntryActivityState.Error, error.Message, false, "AI Dictate"), null);
    }

    private FrameworkElement CreateDictationStatus()
    {
        var view = CreateMicrophoneView(compact: true);
        view.Loaded += (_, _) =>
        {
            if (disposed || hostUnavailable) { view.Dispose(); return; }
            controlsRetired = false;
            if (!TryReadHost(() => focusHost?.FocusedTextEntry, out observedFocusedTarget)) return;
            if (observedFocusedTarget is not null) lastCerebralTarget = observedFocusedTarget;
            if (!microphoneViews.Contains(view)) microphoneViews.Add(view);
            AttachControlOwner(Window.GetWindow(view));
            AttachControlsShortcut(controlsOwner);
            UpdateWindowsFocusObservation();
            RefreshTargetViews();
        };
        view.Unloaded += (_, _) =>
        {
            microphoneViews.Remove(view);
            if (!microphoneViews.Any(item => item.IsLoaded)) RetireControls();
        };
        return view;
    }

    private DictationMicrophoneView CreateMicrophoneView(bool compact)
    {
        var navigation = compact ? ReadSettingsNavigationAvailability() : new DesktopSettingsNavigationAvailability(false);
        var view = new DictationMicrophoneView(compact, compact ? ToggleStatusMicrophoneAsync : ToggleControlMicrophoneAsync,
            ToggleControlAutomaticAsync, compact ? ShowDictationControls : null,
            // Availability can be false until the runtime publishes this constructed instance.
            // Retain the guarded route; the view refreshes current availability after mounting.
            openSettings: compact && host is IDesktopPluginSettingsNavigationHost ? OpenSettingsFromControlsAsync : null,
            settingsUnavailableReason: navigation.Message,
            querySettingsAvailability: compact ? ReadSettingsNavigationAvailability : null);
        microphoneViews.Add(view); RefreshTargetViews(); return view;
    }

    private void RefreshTargetViews()
    {
        if (!CallbacksAvailable) return;
        var target = FocusedControlTarget();
        var processing = session?.IsProcessing == true;
        var on = session?.IsCapturing == true || frontMicrophoneStarting ||
            !automaticPaused && automaticWork is { IsCompleted: false } && !processing;
        var mode = processing ? DictationMicrophoneMode.Processing :
            on ? (controlActivity?.State == DesktopTextEntryActivityState.Listening ? DictationMicrophoneMode.Listening : DictationMicrophoneMode.Starting) :
            target is null && !HasRememberedWindowsTarget ? DictationMicrophoneMode.NoTarget : DictationMicrophoneMode.Off;
        var pendingAudio = session?.HasPendingPhraseAudio == true;
        var detail = pendingAudio ? session!.LastMessage : lastControlError ?? controlActivity?.Message;
        if (mode == DictationMicrophoneMode.NoTarget && lastControlError is null && !pendingAudio) detail = "Choose the intended text box in Dictation with the grabber or Settings.";
        else if (mode == DictationMicrophoneMode.Off && lastControlError is null && !pendingAudio && controlActivity?.State != DesktopTextEntryActivityState.Error)
            detail = automaticPaused && autoTranscribeOnSilence ? "Auto paused · turn the microphone on to resume." : "Turn the microphone on when you are ready.";
        var snapshot = new DictationMicrophoneSnapshot(mode, session?.CaptureAutomatic ?? autoTranscribeOnSilence,
            !disposed && preferences?.State is not null && (automaticArmed || frontMicrophoneStarting || session?.IsCapturing == true ||
                !processing && !controlActionPending && session?.Review.HasText != true && session?.Review.InsertionUnconfirmed != true && session?.HasPendingPhraseAudio != true && (target is not null || HasRememberedWindowsTarget)),
            !disposed && !controlActionPending && (!processing || autoTranscribeOnSilence) && target is not null,
            processing || on ? lockedTarget?.Purpose : target?.Purpose ?? (HasRememberedWindowsTarget ? "Last eligible Windows text box · captured at Start" : null), detail ?? "",
            mode == DictationMicrophoneMode.Listening ? controlActivity?.InputLevel : null,
            pendingAudio || lastControlError is not null || controlActivity?.State == DesktopTextEntryActivityState.Error);
        // Factories can mount while the first acknowledged preference read is still pending.
        // Render a non-armed state; never turn an unavailable read into recording permission.
        var preferencesSnapshot = preferences?.State is not null ? preferences.Current : new AiDictationSettings { AutoTranscribeOnSilence = autoTranscribeOnSilence };
        var canAccept = !controlActionPending && session is { IsActive: false } ready && ready.Review.HasText && !ready.Review.InsertionUnconfirmed &&
            ready.Review.Target is { } destination && IsCurrentTarget(destination);
        snapshot = snapshot with { Docked = preferencesSnapshot.DockControls, CanAccept = canAccept, ReviewBusy = controlActionPending || session?.IsActive == true,
            RecordingRequested = automaticArmed || frontMicrophoneStarting || session?.IsCapturing == true,
            HasRecording = session?.RetainedAudioPath is not null, PlayingRecording = playbackWork is { IsCompleted: false } };
        // Availability reads can revoke the host and retire all views during this refresh.
        var mainTarget = ReadMainPromptTarget();
        var statusSnapshot = snapshot with
        {
            CanToggleMicrophone = preferences?.State is not null && (on || automaticArmed ||
                !processing && !controlActionPending && session?.Review.HasText != true && session?.Review.InsertionUnconfirmed != true && session?.HasPendingPhraseAudio != true && mainTarget is not null),
            Target = on || processing ? lockedTarget?.Purpose : mainTarget?.Purpose
        };
        if (!on && !processing && mainTarget is not null) statusSnapshot = statusSnapshot with { Mode = DictationMicrophoneMode.Off };
        foreach (var view in microphoneViews.ToArray()) view.Update(statusSnapshot);
        var preview = session?.Review.HasText == true ? session.Review.Text : session?.PreviewText ?? "";
        foreach (var view in reviewViews.ToArray()) view.Update(snapshot, preferencesSnapshot, preview, canAccept);
    }

    private void AttachControlOwner(Window? owner)
    {
        if (!CallbacksAvailable || owner is null || ReferenceEquals(owner, controlsOwner)) return;
        DetachControlOwner(); controlsOwner = owner;
        owner.Activated += ControlOwnerActivated;
        owner.Deactivated += ControlOwnerDeactivated;
        owner.Closed += ControlOwnerClosed;
    }
    private void ControlOwnerActivated(object? sender, EventArgs e) => RefreshTargetViews();
    private void ControlOwnerDeactivated(object? sender, EventArgs e)
    {
        if (lockedTarget is not WindowsDictationTarget)
            PauseForDestinationFocusChange("The Main window lost activation.");
        // External destinations retain their own focus-event admission; no rerouting.
        if (CallbacksAvailable) RefreshTargetViews();
    }
    private void ControlOwnerClosed(object? sender, EventArgs e)
    {
        RetireControls();
    }
    private void DetachControlOwner()
    {
        if (controlsOwner is not { } owner) return;
        owner.Activated -= ControlOwnerActivated; owner.Deactivated -= ControlOwnerDeactivated; owner.Closed -= ControlOwnerClosed;
        controlsOwner = null;
    }

    // Both owner IPC and human contributions use the same owned window. Recheck the
    // scoped host on the UI thread: a queued action must not reopen controls after teardown.
    private Task ExecuteControlsActionAsync(bool show, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CallbacksAvailable || dispatcher is not { } owner || owner.HasShutdownStarted || owner.HasShutdownFinished)
            return Task.CompletedTask;
        return owner.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CallbacksAvailable || !TryReadHost(() => host!.GetConversation().Binding, out _)) return;
            if (show) ShowDictationControls(); else HideDictationControls();
        }, DispatcherPriority.Normal, cancellationToken).Task;
    }

    private void HideDictationControls()
    {
        // Visibility commands retain this window and its callbacks. Closing it would also
        // retire a user-chosen segment through Closed; only explicit human close/teardown does that.
        if (controlsWindow is { } window) hideControlsWindow(window);
    }

    private void ShowDictationControls()
    {
        if (!CallbacksAvailable) return;
        if (controlsWindow is { } existing) { showControlsWindow(existing); return; }
        var view = new DictationReviewView(ToggleOneOffMicrophoneAsync, FinishControlMicrophone, CancelCurrentDictation,
            AcceptCurrentReviewAsync, text => { session!.Review.Edit(text); RefreshTargetViews(); }, GrabWindowsTargetAsync,
            ChooseCerebralTarget, () => SaveWindowOptionAsync(value => value with { AutoTranscribeOnSilence = !value.AutoTranscribeOnSilence }),
            () => SaveWindowOptionAsync(value => value with { AllowExternalTarget = !value.AllowExternalTarget }),
            () => SaveWindowOptionAsync(value => value with { GrammarCorrection = !value.GrammarCorrection }),
            () => SaveWindowOptionAsync(value => value with { DockControls = !value.DockControls }),
            LoadGrammarProfilesAsync, choice => SaveWindowOptionAsync(value => value with { GrammarProfileId = choice.Id, GrammarProfileRevision = choice.Revision }),
            async () => { await OpenSettingsFromControlsAsync(); }, UseRememberedDestination, PlayRecordingAsync);
        reviewViews.Add(view);
        var window = new Window
        {
            Title = "Cerebral · Dictation", Width = DictationReviewView.WindowWidth, Height = DictationReviewView.WindowHeight, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Content = view,
            WindowStartupLocation = controlsOwner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        controlsWindow = window;
        view.EditRequested += (_, _) => { window.Focusable = true; window.Activate(); view.ReviewEditor.Focus(); };
        var behavior = new NonActivatingDictationWindow(window, view.DragHandle, reportFailure: error =>
            ReportControlError(new InvalidOperationException("The microphone could not move: " + error.Message, error))); floatingBehavior = behavior;
        view.LayoutModeChanged += (_, _) => window.Height = view.RequiredHeight;
        view.CloseRequested += (_, _) => { playbackCancellation?.Cancel(); HideDictationControls(); };
        window.Closing += (_, args) =>
        {
            if (!CallbacksAvailable) return;
            args.Cancel = true; FinishControlMicrophone(); playbackCancellation?.Cancel(); HideDictationControls();
        };
        var retireCaptureOnClose = true;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(floatingBehavior, behavior)) floatingBehavior = null;
            if (ReferenceEquals(controlsWindow, window)) controlsWindow = null;
            reviewViews.Remove(view);
            AttemptTeardown(behavior.Dispose);
            if (retireCaptureOnClose)
            {
                automaticPaused = true; frontMicrophoneStarting = false;
                AttemptTeardown(() => acceptCancellation?.Cancel());
                AttemptTeardown(() => StopAutomatic(cancelSession: true));
            }
            if (CallbacksAvailable) RefreshTargetViews();
        };
        try
        {
            RefreshTargetViews();
            // Prepare creates only this widget's handle, applies and verifies the style before Show.
            behavior.Prepare();
            showControlsWindow(window);
        }
        catch (Exception error)
        {
            // A failed presentation must release partial UI without stopping existing capture.
            retireCaptureOnClose = false;
            AttemptTeardown(window.Close); ReportControlError(new InvalidOperationException("The floating microphone could not open safely. Use the status-bar microphone. " + error.Message, error));
        }
    }

    private void DisposeControls()
    {
        var window = controlsWindow; controlsWindow = null;
        var behavior = floatingBehavior; floatingBehavior = null;
        var views = microphoneViews.ToArray(); microphoneViews.Clear();
        AttemptTeardown(DetachControlOwner);
        if (window is not null) AttemptTeardown(window.Close);
        if (behavior is not null) AttemptTeardown(behavior.Dispose);
        foreach (var view in views) AttemptTeardown(view.Dispose);
        reviewViews.Clear();
        if (lockedTarget is WindowsDictationTarget external) AttemptTeardown(external.Dispose);
        lockedTarget = null; lockedBinding = null;
    }
}
