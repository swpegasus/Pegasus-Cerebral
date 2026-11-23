using System.Diagnostics;
using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin
{
    private IDisposable? windowsFocusObservation;
    private IChosenWindowsTextTarget? rememberedWindowsTarget;
    private long windowsObservationGeneration;
    private long lastDestinationFocusTime;
    private bool supportsWindowsMemory;
    private IChosenWindowsTextTarget? pinnedWindowsTarget;
    private IDesktopTextEntry? pinnedCerebralTarget;
    private IDesktopTextEntry? ReadMainPromptTarget()
    {
        if (!CallbacksAvailable || !TryReadHost(() => (host as IDesktopDefaultTextEntryHost)?.DefaultTextEntry, out var target)) return null;
        try { return target is IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true } ? null : target; }
        catch (ObjectDisposedException) { return null; }
    }

    private void PauseForDestinationFocusChange(string reason)
    {
        if (!CallbacksAvailable || !automaticArmed && session?.IsCapturing != true && submissionTarget is null && preparedRearm is null) return;
        automaticArmed = false; automaticPaused = true; targetLockVersion++; resumeFocusCancellation?.Cancel();
        session?.InterruptDestination(reason);
        controlActivity = new(AiDictationSession.PluginId, AiDictationSession.ActionId, DesktopTextEntryActivityState.Error,
            reason + " Dictation stopped; the current phrase will be retained for recovery.", false, "Start");
        RefreshTargetViews();
    }

    private void ObserveForeignFocus(long observedAt)
    {
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.InvokeAsync(() =>
        {
            if (observedAt > lastDestinationFocusTime)
                PauseForDestinationFocusChange("Keyboard focus left the captured destination.");
        });
    }
    private bool HasRememberedWindowsTarget => rememberedWindowsTarget is not null && !explicitDestination &&
        session?.IsActive != true && session?.Review.HasText != true && session?.Review.InsertionUnconfirmed != true;

    private void UpdateWindowsFocusObservation()
    {
        var allowed = supportsWindowsMemory && CallbacksAvailable && preferences?.State is not null && preferences.Current.AllowExternalTarget;
        if (!allowed) { StopWindowsFocusObservation(); return; }
        if (windowsFocusObservation is not null) return;
        var generation = ++windowsObservationGeneration;
        try
        {
            windowsFocusObservation = observeWindowsFocus(target =>
            {
                var observedAt = target is WindowsUiaTextTarget native ? native.ObservedAt : Stopwatch.GetTimestamp();
                // Native events never touch WPF/session state, choose an active capture target,
                // read destination contents or arm the microphone.
                _ = dispatcher!.InvokeAsync(() =>
                {
                    if (generation != windowsObservationGeneration || !CallbacksAvailable ||
                        preferences?.State is null || !preferences.Current.AllowExternalTarget || observedAt <= lastDestinationFocusTime) return;
                    lastDestinationFocusTime = observedAt;
                    if (lockedTarget is not null && target.Identity != lockedTarget.Id)
                        PauseForDestinationFocusChange("Keyboard focus moved to another text box.");
                    rememberedWindowsTarget = target;
                    RefreshTargetViews();
                });
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ReportHostStatus("Windows destination memory is unavailable. Use the grabber or a Cerebral editor. " + error.Message); }
    }

    private void UseRememberedDestination()
    {
        if (!CallbacksAvailable || session?.IsActive == true || controlActionPending) return;
        if (session?.Review.HasText == true || session?.Review.InsertionUnconfirmed == true)
        { ReportControlError(new InvalidOperationException("Insert or cancel the retained review before returning to destination memory.")); return; }
        if (lockedTarget is WindowsDictationTarget external) external.Dispose();
        pinnedWindowsTarget = null; pinnedCerebralTarget = null;
        lockedTarget = null; lockedBinding = null; explicitDestination = false;
        RefreshTargetViews();
    }

    private void ReportWindowsObservationError(Exception error)
    {
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.InvokeAsync(() =>
        {
            if (CallbacksAvailable) ReportHostStatus("Windows destination memory is unavailable; use the grabber or a Cerebral editor.");
        });
    }

    private void StopWindowsFocusObservation()
    {
        windowsObservationGeneration++;
        rememberedWindowsTarget = null;
        pinnedWindowsTarget = null;
        var old = windowsFocusObservation; windowsFocusObservation = null;
        old?.Dispose();
    }

    private async Task<IDesktopTextEntry?> ResolveStartTargetAsync()
    {
        if (session?.IsActive == true || session?.Review.HasText == true || session?.Review.InsertionUnconfirmed == true)
            return FocusedControlTarget();
        if (pinnedCerebralTarget is not null) return pinnedCerebralTarget;
        var remembered = pinnedWindowsTarget ?? (HasRememberedWindowsTarget ? rememberedWindowsTarget : null);
        if (remembered is null) return FocusedControlTarget();
        var generation = windowsObservationGeneration;
        // Snapshot this exact remembered control at Start. Later focus events can update
        // history but cannot redirect the recording, review, or subsequent Insert.
        var selected = await WindowsDictationTarget.CaptureAsync(() => remembered, CancellationToken.None);
        if (!CallbacksAvailable || generation != windowsObservationGeneration ||
            preferences?.State is null || !preferences.Current.AllowExternalTarget)
        { selected.Dispose(); throw new OperationCanceledException("Windows destination access was withdrawn."); }
        return selected;
    }
}
