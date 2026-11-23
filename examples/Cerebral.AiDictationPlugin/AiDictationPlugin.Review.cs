using System.Windows;
using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

public sealed partial class AiDictationPlugin
{
    private readonly SemaphoreSlim windowPreferenceGate = new(1, 1);
    private string? grammarNotice;
    private CancellationTokenSource? acceptCancellation;

    private void CancelCurrentDictation()
    {
        if (!CallbacksAvailable) return;
        var priorInsertionMayHaveOccurred = session?.Review.InsertionUnconfirmed == true || acceptCancellation is not null;
        acceptCancellation?.Cancel();
        playbackCancellation?.Cancel();
        automaticArmed = false; automaticPaused = true;
        StopAutomatic(cancelSession: true);
        session!.Cancel(discardReview: true);
        if (!session.IsActive) session.ClearRetainedAudio();
        controlActivity = new(AiDictationSession.PluginId, AiDictationSession.ActionId,
            DesktopTextEntryActivityState.Result, priorInsertionMayHaveOccurred
                ? "Current dictation canceled. A prior insertion may already have occurred; inspect the chosen app. Cancel cannot undo a dispatched insertion."
                : "Pending dictation discarded. Earlier dictated text remains in the destination.", false, "Dictate");
        RefreshTargetViews();
    }

    private async Task AcceptCurrentReviewAsync()
    {
        if (!CallbacksAvailable || controlActionPending) return;
        FinishControlMicrophone();
        if (session!.IsActive)
        {
            // A recording raced the click. Stop/transcribe for review, never silently accept
            // words the user has not had the opportunity to inspect.
            await session.Completion;
            RefreshTargetViews(); return;
        }
        controlActionPending = true; RefreshTargetViews();
        using var acceptance = new CancellationTokenSource(); acceptCancellation = acceptance;
        try
        {
            var result = await session.AcceptReviewAsync(acceptance.Token);
            controlActivity = new(AiDictationSession.PluginId, AiDictationSession.ActionId,
                result.State == DesktopOperationState.Completed ? DesktopTextEntryActivityState.Result : DesktopTextEntryActivityState.Error,
                result.Message ?? "Review was not inserted. Destination text was preserved.", false, "Dictate");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ReportControlError(error); }
        finally
        {
            if (ReferenceEquals(acceptCancellation, acceptance)) acceptCancellation = null;
            controlActionPending = false; RefreshTargetViews();
        }
    }

    private void ChooseCerebralTarget()
    {
        if (!CallbacksAvailable || session?.IsActive == true || lastCerebralTarget is not { } selected)
        { ReportControlError(new InvalidOperationException("Choose the intended Cerebral editor first, then select it here.")); return; }
        try
        {
            if (selected is IDesktopTextEntryLifetime { AvailabilityToken.IsCancellationRequested: true })
                throw new InvalidOperationException("Choose an available Cerebral editor first, then select it here.");
            explicitDestination = true;
            pinnedCerebralTarget = selected; pinnedWindowsTarget = null;
            LockTarget(selected); session!.Review.ChooseTarget(selected); RefreshTargetViews();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ReportControlError(error); }
    }

    private async Task GrabWindowsTargetAsync(Point point)
    {
        if (!CallbacksAvailable || preferences!.Current.AllowExternalTarget != true || session?.IsActive == true || controlActionPending) return;
        if (session!.Review.InsertionUnconfirmed)
        { ReportControlError(new InvalidOperationException("Inspect the unconfirmed insertion and clear its review before choosing another destination.")); return; }
        var version = targetLockVersion;
        controlActionPending = true; RefreshTargetViews();
        try
        {
            var selected = await WindowsDictationTarget.GrabAsync(point, CancellationToken.None);
            if (!CallbacksAvailable || targetLockVersion != version || !preferences.Current.AllowExternalTarget)
            { selected.Dispose(); return; }
            explicitDestination = true;
            pinnedWindowsTarget = selected.Native; pinnedCerebralTarget = null;
            LockTarget(selected); session!.Review.ChooseTarget(selected);
            controlActivity = new(AiDictationSession.PluginId, AiDictationSession.ActionId,
                DesktopTextEntryActivityState.Idle, "Destination pinned. Press Start when ready; focus changes never choose another destination.", false, "Dictate");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ReportControlError(error); }
        finally { controlActionPending = false; RefreshTargetViews(); }
    }

    private async Task SaveWindowOptionAsync(Func<AiDictationSettings, AiDictationSettings> update)
    {
        if (!CallbacksAvailable) return;
        await windowPreferenceGate.WaitAsync();
        try
        {
            var saved = await preferences!.ReadAsync();
            saved = await preferences.SaveAsync(update(saved), null, CancellationToken.None);
            if (!CallbacksAvailable) return;
            autoTranscribeOnSilence = saved.AutoTranscribeOnSilence;
            UpdateWindowsFocusObservation();
            if (!saved.AutoTranscribeOnSilence && automaticArmed) FinishControlMicrophone();
            if (!saved.AllowExternalTarget && lockedTarget is WindowsDictationTarget external)
            {
                StopAutomatic(cancelSession: true); external.Dispose(); lockedTarget = null; lockedBinding = null;
            }
            if (saved.DockControls) HideDictationControls();
            // Preferences never arm a microphone. Only an explicit Start action does that.
            RefreshTargetViews();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ReportControlError(error); }
        finally { windowPreferenceGate.Release(); }
    }

    private async Task<IReadOnlyList<DictationGrammarProfile>> LoadGrammarProfilesAsync()
    {
        if (!CallbacksAvailable) return [];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await DictationGrammar.ProfilesAsync(host!, deadline.Token);
    }

    private Task<string> CorrectSegmentAsync(AiDictationSettings selected, string recognized, CancellationToken token) =>
        dispatcher!.InvokeAsync(async () =>
        {
            if (!CallbacksAvailable) throw new OperationCanceledException(token);
            var corrected = await DictationGrammar.CorrectAsync(host!, selected, recognized, token);
            grammarNotice = corrected.Notice;
            if (corrected.Notice is { } notice && CallbacksAvailable) ReportHostStatus(notice);
            return corrected.Text;
        }).Task.Unwrap();
}
